module BotSharp.Tests.Application.AutoCompactServiceTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.AutoCompactService

// ═══════════════════════════════════════════════════════════════════════════
// AutoCompactService unit tests
//
// Focuses on what the service actually does: skip active sessions, skip
// recently-modified files, and run compaction on idle sessions when they
// have enough unconsolidated messages.
// ═══════════════════════════════════════════════════════════════════════════

/// Build a minimal AgentDependencies with controlled session storage.
let private mkDeps
    (sessions   : System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>)
    : AgentDependencies =
    { Provider          = { Id = "stub"; DefaultModel = "m"; Capabilities = Set.empty
                            RetryPolicy = RetryPolicy.standard
                            Chat        = fun _ _ _ -> async { return Result.Ok { Body = TextOnly "ok"; ReasoningContent = None; ThinkingBlocks = []; Usage = { PromptTokens=1;CompletionTokens=1;CachedTokens=0 }; FinishReason = None } }
                            ChatStream  = fun _ _ _ _ -> async { return Result.Ok () } }
      Tools             = Map.empty
      LoadSession       = fun sid -> async {
          match sessions.TryGetValue(sid) with
          | true, snap -> return Result.Ok snap
          | _          -> return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow)
      }
      PersistSession    = fun snap -> async { sessions[SessionSnapshot.id snap] <- snap; return Result.Ok () }
      BuildSystemPrompt = fun _ _ -> async { return "sys" }
      Config            = BotSharpConfig.defaults
      StreamHook        = NoStreaming
      CronService       = None
      Hook              = AgentHook.none
      LastTokenUsage    = ref None
      CurrentIteration  = ref 0
      RuleEngine        = None
      FallbackProviders = [] }

[<Fact>]
let ``AutoCompactService Start/Stop with ttl=0 does not crash`` () =
    let deps = mkDeps (System.Collections.Generic.Dictionary())
    let svc  = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 0)
    svc.Start()
    svc.Stop()   // should not throw

[<Fact>]
let ``AutoCompactService with non-existent sessions directory does not crash`` () =
    // compactPass checks Directory.Exists and returns () immediately when absent.
    // The service runs compactPass once immediately on Start(), before the first sleep.
    let tmp  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    // Do NOT create the directory — sessions/ will not exist.
    let cfg  = { BotSharpConfig.defaults with WorkspacePath = tmp }
    let deps = { mkDeps (System.Collections.Generic.Dictionary()) with Config = cfg }
    // intervalMinutes=60 → compactPass runs once immediately, then sleeps 60 min.
    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 1)
    svc.Start()
    System.Threading.Thread.Sleep(100)   // give compactPass time to finish
    svc.Stop()  // must not throw

[<Fact>]
let ``AutoCompactService Start/Stop with positive ttl does not crash`` () =
    let deps = mkDeps (System.Collections.Generic.Dictionary())
    let svc  = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(50)
    svc.Stop()

[<Fact>]
let ``active session is skipped even if file is old`` () =
    // Arrange: a session in a temp directory with an old mtime
    let tmp  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid = SessionId "test-session"
    let file = Path.Combine(sessDir, "test-session.jsonl")
    File.WriteAllText(file, "")
    // Force mtime to 2 hours ago
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2.0))

    let persisted = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    let cfg = { BotSharpConfig.defaults with WorkspacePath = tmp }
    let deps = { mkDeps persisted with Config = cfg }

    // Act: active session IDs includes our test session — should be skipped
    let svc = AutoCompactService(deps, (fun () -> Set.singleton sid), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(100)
    svc.Stop()

    // Assert: PersistSession was NOT called (session was in active set)
    Assert.False(persisted.ContainsKey(sid), "Expected active session to be skipped")

    Directory.Delete(tmp, true)

[<Fact>]
let ``recent file is skipped regardless of message count`` () =
    // Arrange: a session file written 1 minute ago (well within 60-min TTL)
    let tmp  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid  = SessionId "recent-session"
    let file = Path.Combine(sessDir, "recent-session.jsonl")
    File.WriteAllText(file, "")
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1.0))

    let persisted = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    let cfg = { BotSharpConfig.defaults with WorkspacePath = tmp }
    let deps = { mkDeps persisted with Config = cfg }

    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(100)
    svc.Stop()

    Assert.False(persisted.ContainsKey(sid), "Expected recently-modified file to be skipped")

    Directory.Delete(tmp, true)

[<Fact>]
let ``idle session with enough unconsolidated messages gets compacted and persisted`` () =
    let tmp     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid  = SessionId "idle-session"
    // Create a session file with mtime 2 hours ago (exceeds the 60-min TTL)
    let file = Path.Combine(sessDir, "idle-session.jsonl")
    File.WriteAllText(file, "")
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2.0))

    // Build a snapshot with 5 messages, lastConsolidated = 0;
    // MemoryWindowSize will be set to 3 → unconsolidated (5) ≥ window (3) → compaction eligible
    let now  = DateTimeOffset.UtcNow
    let msgs = [ 1..5 ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Ok s    -> s
        | Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    // Provider that returns a valid save_memory tool call
    let saveMemArgs =
        use doc = JsonDocument.Parse("""{"history_entry":"Test history.","memory_update":"Updated memory."}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let saveMemCall = {
        Id = ToolCallId "sm-1"; Tool = ToolName "save_memory"
        Arguments = saveMemArgs; ProviderMeta = None
    }
    let saveMemResp = {
        Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [ saveMemCall ])
        ReasoningContent = None
        ThinkingBlocks   = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = None
    }
    let saveMemProvider = {
        Id           = "stub"
        DefaultModel = "m"
        Capabilities = Set.empty
        RetryPolicy  = RetryPolicy.standard
        Chat         = fun _ _ _ -> async { return Result.Ok saveMemResp }
        ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
    }

    let cfg  = { BotSharpConfig.defaults with WorkspacePath = tmp; MemoryWindowSize = 3 }
    let deps = { mkDeps sessions with Config = cfg; Provider = saveMemProvider }

    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    // Poll up to 3 s for compactPass to finish; resolves quickly (~100ms) on a stub provider
    // but the deadline prevents flakiness under full-suite parallelism.
    let deadline = DateTime.UtcNow.AddSeconds(3.0)
    while (not (sessions.ContainsKey(sid) && SessionSnapshot.lastConsolidated sessions[sid] > 0))
          && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep(50)
    svc.Stop()

    // PersistSession should have been called with the compacted snapshot
    Assert.True(sessions.ContainsKey(sid), "Expected idle session to be compacted and persisted")
    let compacted = sessions[sid]
    // After compaction, lastConsolidated should equal message count (5)
    Assert.Equal(5, SessionSnapshot.lastConsolidated compacted)

    Directory.Delete(tmp, true)

[<Fact>]
let ``idle session with too few unconsolidated messages is not compacted`` () =
    // unconsolidated (2) < MemoryWindowSize (5) → compactPass skips without consolidating
    let tmp     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid  = SessionId "sparse-session"
    let file = Path.Combine(sessDir, "sparse-session.jsonl")
    File.WriteAllText(file, "")
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2.0))

    // Only 2 messages, window=5 → unconsolidated (2) < 5 → skip
    let now  = DateTimeOffset.UtcNow
    let msgs = [ UserMessage ("a", []); AssistantMessage ("b", None) ]
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Ok s    -> s
        | Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    let cfg  = { BotSharpConfig.defaults with WorkspacePath = tmp; MemoryWindowSize = 5 }
    let deps = { mkDeps sessions with Config = cfg }

    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(200)
    svc.Stop()

    // Snapshot should remain unchanged (lastConsolidated still 0)
    let snap' = sessions[sid]
    Assert.Equal(0, SessionSnapshot.lastConsolidated snap')

    Directory.Delete(tmp, true)

[<Fact>]
let ``LoadSession error for an idle file is silently ignored`` () =
    // When LoadSession returns Error, compactPass swallows it and continues
    let tmp     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid  = SessionId "error-session"
    let file = Path.Combine(sessDir, "error-session.jsonl")
    File.WriteAllText(file, "")
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2.0))

    let persisted = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    let cfg = { BotSharpConfig.defaults with WorkspacePath = tmp }
    let deps =
        { mkDeps persisted with
            Config      = cfg
            LoadSession = fun _ -> async { return Result.Error (WriteFailure "simulated load failure") } }

    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(200)
    svc.Stop()   // must not throw

    Assert.False(persisted.ContainsKey(sid), "Nothing should be persisted on load error")

    Directory.Delete(tmp, true)

[<Fact>]
let ``consolidation error for idle session is silently ignored and session is not persisted`` () =
    // Tests | Result.Error _ -> () branch after consolidate fails (line 76 in AutoCompactService)
    let tmp     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid  = SessionId "error-compact"
    let file = Path.Combine(sessDir, "error-compact.jsonl")
    File.WriteAllText(file, "")
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2.0))

    let now  = DateTimeOffset.UtcNow
    let msgs = [ 1..5 ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Ok s    -> s
        | Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    // Provider returns error → consolidate returns Error → autocompact silently swallows it
    let errorProvider = {
        Id           = "error"
        DefaultModel = "m"
        Capabilities = Set.empty
        // Zero retries so the test doesn't hang waiting for backoff delays.
        RetryPolicy  = { RetryPolicy.standard with Mode = FixedRetries (0, []) }
        Chat         = fun _ _ _ -> async {
            return Result.Error { Kind = ServerError 500; RawMessage = "fail"; ProviderCode = None }
        }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let cfg  = { BotSharpConfig.defaults with WorkspacePath = tmp; MemoryWindowSize = 3 }
    let deps = { mkDeps sessions with Config = cfg; Provider = errorProvider }

    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(300)
    svc.Stop()   // must not throw

    // Snapshot unchanged (consolidation failed and was silently ignored)
    Assert.Equal(0, SessionSnapshot.lastConsolidated sessions[sid])

    Directory.Delete(tmp, true)

[<Fact>]
let ``sessionTtlMinutes=0 disables the service so no session is compacted`` () =
    // When sessionTtlMinutes <= 0, Start() is a no-op (the background loop never starts).
    // Even an idle session with many unconsolidated messages must NOT be compacted.
    let tmp     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let sessDir = Path.Combine(tmp, "sessions")
    Directory.CreateDirectory(sessDir) |> ignore

    let sid  = SessionId "disabled-compact"
    let file = Path.Combine(sessDir, "disabled-compact.jsonl")
    File.WriteAllText(file, "")
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2.0))

    let now  = DateTimeOffset.UtcNow
    let msgs = [ 1..10 ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Ok s    -> s
        | Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    let cfg  = { BotSharpConfig.defaults with WorkspacePath = tmp; MemoryWindowSize = 3 }
    let deps = { mkDeps sessions with Config = cfg }

    // ttl = 0 → feature disabled
    let svc = AutoCompactService(deps, (fun () -> Set.empty), sessionTtlMinutes = 0)
    svc.Start()
    System.Threading.Thread.Sleep(100)   // give any (incorrectly-started) loop time to run
    svc.Stop()

    // Service was disabled — session must remain unchanged (lastConsolidated still 0)
    Assert.Equal(0, SessionSnapshot.lastConsolidated sessions[sid])

    Directory.Delete(tmp, true)
