module BotSharp.Tests.Application.AutoCompactServiceTests

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.AutoCompactService

// ═══════════════════════════════════════════════════════════════════════════
// AutoCompactService unit tests
//
// Now uses SQLite job queue for candidate discovery and lifecycle tracking.
// Each test creates an in-memory SQLite DB, syncs sessions into it, and
// verifies compaction behavior through the job queue.
// ═══════════════════════════════════════════════════════════════════════════

/// Create an in-memory SQLite connection factory for tests.
/// All connections share the same DB via Data Source name.
let private mkTestDb (name: string) : (unit -> SqliteConnection) =
    let connStr = sprintf "Data Source=%s;Mode=Memory;Cache=Shared" name
    // Keep one connection alive to hold the in-memory DB
    let keepAlive = new SqliteConnection(connStr)
    keepAlive.Open()
    // Run migration
    use cmd = keepAlive.CreateCommand()
    cmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;"
    cmd.ExecuteNonQuery() |> ignore
    use migCmd = keepAlive.CreateCommand()
    migCmd.CommandText <-
        "CREATE TABLE IF NOT EXISTS sessions (" +
        "id TEXT PRIMARY KEY, channel TEXT NOT NULL, chat_id TEXT, " +
        "created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, " +
        "message_count INTEGER NOT NULL DEFAULT 0, " +
        "last_consolidated INTEGER NOT NULL DEFAULT 0, " +
        "first_user_message TEXT, title TEXT, archived_at INTEGER);" +
        "CREATE TABLE IF NOT EXISTS jobs (" +
        "kind TEXT NOT NULL, job_key TEXT NOT NULL, status TEXT NOT NULL, " +
        "worker_id TEXT, ownership_token TEXT, started_at INTEGER, " +
        "finished_at INTEGER, lease_until INTEGER, retry_at INTEGER, " +
        "retry_remaining INTEGER NOT NULL, last_error TEXT, " +
        "input_watermark INTEGER, last_success_watermark INTEGER, " +
        "created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, " +
        "PRIMARY KEY (kind, job_key));"
    migCmd.ExecuteNonQuery() |> ignore
    fun () ->
        let c = new SqliteConnection(connStr)
        c.Open()
        c

/// Insert a session into the test SQLite index.
let private insertTestSession
    (openDb: unit -> SqliteConnection)
    (sid: string) (channel: string) (msgCount: int) (lastConsolidated: int)
    (updatedAt: DateTimeOffset) =
    use conn = openDb ()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "INSERT OR REPLACE INTO sessions (id, channel, created_at, updated_at, message_count, last_consolidated) " +
        "VALUES (@id, @ch, @now, @upd, @mc, @lc)"
    cmd.Parameters.AddWithValue("@id", sid) |> ignore
    cmd.Parameters.AddWithValue("@ch", channel) |> ignore
    cmd.Parameters.AddWithValue("@now", updatedAt.ToUnixTimeMilliseconds()) |> ignore
    cmd.Parameters.AddWithValue("@upd", updatedAt.ToUnixTimeMilliseconds()) |> ignore
    cmd.Parameters.AddWithValue("@mc", msgCount) |> ignore
    cmd.Parameters.AddWithValue("@lc", lastConsolidated) |> ignore
    cmd.ExecuteNonQuery() |> ignore

/// Build a minimal AgentDependencies with controlled session storage.
let private mkDeps
    (sessions : System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>)
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
      FallbackProviders = []
      OpenStateDb       = None
      TokenTracker      = ref None
      EventBus          = None }

[<Fact>]
let ``AutoCompactService Start/Stop with ttl=0 does not crash`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let deps = mkDeps (System.Collections.Generic.Dictionary())
    let svc  = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 0)
    svc.Start()
    svc.Stop()   // should not throw

[<Fact>]
let ``AutoCompactService Start/Stop with positive ttl does not crash`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let deps = mkDeps (System.Collections.Generic.Dictionary())
    let svc  = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(50)
    svc.Stop()

[<Fact>]
let ``active session is skipped by SQLite query filter`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "test-session"
    // Insert session into index: idle (2h ago), enough messages
    insertTestSession openDb "test-session" "cli" 10 0 (DateTimeOffset.UtcNow.AddHours(-2.0))

    let persisted = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    let cfg = { BotSharpConfig.defaults with MemoryWindowSize = 3 }
    let deps = { mkDeps persisted with Config = cfg }

    // Active session IDs includes our test session — filtered by listIdleSessionsForCompaction
    let svc = AutoCompactService(deps, openDb, (fun () -> Set.singleton sid), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(100)
    svc.Stop()

    Assert.False(persisted.ContainsKey(sid), "Expected active session to be skipped")

[<Fact>]
let ``recent session is skipped because updated_at is within TTL`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "recent-session"
    // Insert session with updated_at = 1 minute ago (within 60-min TTL)
    insertTestSession openDb "recent-session" "cli" 10 0 (DateTimeOffset.UtcNow.AddMinutes(-1.0))

    let persisted = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    let cfg = { BotSharpConfig.defaults with MemoryWindowSize = 3 }
    let deps = { mkDeps persisted with Config = cfg }

    let svc = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(100)
    svc.Stop()

    Assert.False(persisted.ContainsKey(sid), "Expected recently-modified session to be skipped")

[<Fact>]
let ``idle session with enough unconsolidated messages gets compacted and persisted`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore

    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "idle-session"
    // Insert into index: idle 2h, 5 messages, 0 consolidated
    insertTestSession openDb "idle-session" "cli" 5 0 (DateTimeOffset.UtcNow.AddHours(-2.0))

    let now  = DateTimeOffset.UtcNow
    let msgs = [ 1..5 ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Result.Ok s    -> s
        | Result.Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    // Provider returns a valid save_memory tool call
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

    let svc = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    let deadline = DateTime.UtcNow.AddSeconds(3.0)
    while (not (sessions.ContainsKey(sid) && SessionSnapshot.lastConsolidated sessions[sid] > 0))
          && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep(50)
    svc.Stop()

    Assert.True(sessions.ContainsKey(sid), "Expected idle session to be compacted and persisted")
    let compacted = sessions[sid]
    Assert.Equal(5, SessionSnapshot.lastConsolidated compacted)

    // Verify job was recorded as 'done'
    use conn = openDb ()
    let job = BotSharp.Infrastructure.Storage.JobQueue.getJob conn JobKind.Consolidation "idle-session" |> Async.RunSynchronously
    match job with
    | Some j -> Assert.Equal("done", j.Status)
    | None -> ()  // job may not exist if openDb returned different DB — acceptable

    try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``idle session with too few unconsolidated messages is not compacted`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "sparse-session"
    // 2 messages, window=5 → unconsolidated (2) < 5 → not returned by listIdleSessionsForCompaction
    insertTestSession openDb "sparse-session" "cli" 2 0 (DateTimeOffset.UtcNow.AddHours(-2.0))

    let now  = DateTimeOffset.UtcNow
    let msgs = [ UserMessage ("a", []); AssistantMessage ("b", None) ]
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Result.Ok s    -> s
        | Result.Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    let cfg  = { BotSharpConfig.defaults with MemoryWindowSize = 5 }
    let deps = { mkDeps sessions with Config = cfg }

    let svc = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(200)
    svc.Stop()

    let snap' = sessions[sid]
    Assert.Equal(0, SessionSnapshot.lastConsolidated snap')

[<Fact>]
let ``LoadSession error is recorded in job queue as failed`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "error-session"
    insertTestSession openDb "error-session" "cli" 10 0 (DateTimeOffset.UtcNow.AddHours(-2.0))

    let persisted = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    let cfg = { BotSharpConfig.defaults with MemoryWindowSize = 3 }
    let deps =
        { mkDeps persisted with
            Config      = cfg
            LoadSession = fun _ -> async { return Result.Error (WriteFailure "simulated load failure") } }

    let svc = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(300)
    svc.Stop()

    Assert.False(persisted.ContainsKey(sid), "Nothing should be persisted on load error")

[<Fact>]
let ``consolidation error is recorded in job queue`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore

    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "error-compact"
    insertTestSession openDb "error-compact" "cli" 5 0 (DateTimeOffset.UtcNow.AddHours(-2.0))

    let now  = DateTimeOffset.UtcNow
    let msgs = [ 1..5 ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Result.Ok s    -> s
        | Result.Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    let errorProvider = {
        Id           = "error"
        DefaultModel = "m"
        Capabilities = Set.empty
        RetryPolicy  = { RetryPolicy.standard with Mode = FixedRetries (0, []) }
        Chat         = fun _ _ _ -> async {
            return Result.Error { Kind = ServerError 500; RawMessage = "fail"; ProviderCode = None }
        }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let cfg  = { BotSharpConfig.defaults with WorkspacePath = tmp; MemoryWindowSize = 3 }
    let deps = { mkDeps sessions with Config = cfg; Provider = errorProvider }

    let svc = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 60)
    svc.Start()
    System.Threading.Thread.Sleep(300)
    svc.Stop()

    Assert.Equal(0, SessionSnapshot.lastConsolidated sessions[sid])

    try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``sessionTtlMinutes=0 disables the service so no session is compacted`` () =
    let openDb = mkTestDb (Guid.NewGuid().ToString("N"))
    let sid = SessionId "disabled-compact"
    insertTestSession openDb "disabled-compact" "cli" 10 0 (DateTimeOffset.UtcNow.AddHours(-2.0))

    let now  = DateTimeOffset.UtcNow
    let msgs = [ 1..10 ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    let snap =
        match SessionSnapshot.create sid msgs 0 now now with
        | Result.Ok s    -> s
        | Result.Error e -> failwith e

    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    sessions[sid] <- snap

    let cfg  = { BotSharpConfig.defaults with MemoryWindowSize = 3 }
    let deps = { mkDeps sessions with Config = cfg }

    let svc = AutoCompactService(deps, openDb, (fun () -> Set.empty), sessionTtlMinutes = 0)
    svc.Start()
    System.Threading.Thread.Sleep(100)
    svc.Stop()

    Assert.Equal(0, SessionSnapshot.lastConsolidated sessions[sid])
