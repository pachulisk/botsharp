module BotSharp.Tests.Application.Phase1ExtractorTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.Phase1Extractor
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// Phase1Extractor unit tests
//
// Tests extractSession with mocked AgentDependencies.
// Uses real file-based SQLite (StateDb.init) to exercise job queue
// and stage1_outputs persistence.
// ═══════════════════════════════════════════════════════════════════════════

/// Create a real file-based StateDb.
let private mkDb () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let factory = init tmp |> Async.RunSynchronously
    (factory, tmp)

/// Build minimal AgentDependencies with a given provider.
let private mkDeps (provider: LLMProvider) (workspacePath: string) : AgentDependencies =
    let sessions = System.Collections.Generic.Dictionary<SessionId, SessionSnapshot>()
    { Provider          = provider
      Tools             = Map.empty
      LoadSession       = fun sid -> async {
          match sessions.TryGetValue(sid) with
          | true, snap -> return Result.Ok snap
          | _ -> return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow)
      }
      PersistSession    = fun snap -> async { sessions[SessionSnapshot.id snap] <- snap; return Result.Ok () }
      BuildSystemPrompt = fun _ _ -> async { return "sys" }
      Config            = { BotSharpConfig.defaults with WorkspacePath = workspacePath }
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

/// Build a LLMProvider that returns a save_phase1 tool call.
let private mkSavePhase1Provider (rawMemory: string) (summary: string) (slug: string) : LLMProvider =
    let argsJson =
        sprintf "{\"raw_memory\":\"%s\",\"rollout_summary\":\"%s\",\"rollout_slug\":\"%s\"}"
            (rawMemory.Replace("\"", "\\\""))
            (summary.Replace("\"", "\\\""))
            (slug.Replace("\"", "\\\""))
    let args =
        use doc = JsonDocument.Parse(argsJson)
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = { Id = ToolCallId "p1-1"; Tool = ToolName "save_phase1"; Arguments = args; ProviderMeta = None }
    let resp = {
        Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [ call ])
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = None
    }
    { Id = "stub"; DefaultModel = "m"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
      Chat = fun _ _ _ -> async { return Result.Ok resp }
      ChatStream = fun _ _ _ _ -> async { return Result.Ok () } }

/// Build a LLMProvider that always returns an LLM error.
let private mkErrorProvider () : LLMProvider =
    { Id = "error"; DefaultModel = "m"; Capabilities = Set.empty
      RetryPolicy = { RetryPolicy.standard with Mode = FixedRetries (0, []) }
      Chat = fun _ _ _ -> async {
          return Result.Error { Kind = ServerError 500; RawMessage = "simulated error"; ProviderCode = None }
      }
      ChatStream = fun _ _ _ _ -> async { return Result.Ok () } }

/// Build a provider that returns a text-only response (no tool call).
let private mkTextOnlyProvider () : LLMProvider =
    let resp = {
        Body = TextOnly "I cannot extract memories."; ReasoningContent = None; ThinkingBlocks = []
        Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None
    }
    { Id = "textonly"; DefaultModel = "m"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
      Chat = fun _ _ _ -> async { return Result.Ok resp }
      ChatStream = fun _ _ _ _ -> async { return Result.Ok () } }

/// Build a SessionSnapshot with given messages.
let private mkSnap (sid: SessionId) (msgs: Message list) =
    let now = DateTimeOffset.UtcNow
    match SessionSnapshot.create sid msgs 0 now now with
    | Result.Ok s    -> s
    | Result.Error e -> failwith e

// ── extractSession tests ─────────────────────────────────────────────────

[<Fact>]
let ``extractSession returns SucceededNoOutput when unconsolidated messages empty`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "cli:empty-session"
        let snap = mkSnap sid []
        let deps = mkDeps (mkSavePhase1Provider "mem" "summary" "slug") tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let result = extractSession openDb deps sid snap token |> Async.RunSynchronously
        Assert.Equal(SucceededNoOutput, result)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``extractSession returns SucceededWithOutput when provider returns valid save_phase1`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "cli:extract-session"
        let msgs = [ UserMessage ("What is 2+2?", []); AssistantMessage ("4", None) ]
        let snap = mkSnap sid msgs
        let deps = mkDeps (mkSavePhase1Provider "mem content" "Session summary" "test_task") tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let result = extractSession openDb deps sid snap token |> Async.RunSynchronously
        Assert.Equal(SucceededWithOutput, result)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``extractSession writes stage1_output to database on success`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "cli:persist-session"
        let msgs = [ UserMessage ("Deploy the service.", []); AssistantMessage ("Done.", None) ]
        let snap = mkSnap sid msgs
        let deps = mkDeps (mkSavePhase1Provider "mem content" "Deployed service." "deploy_service") tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let _ = extractSession openDb deps sid snap token |> Async.RunSynchronously
        // Verify stage1_output was written
        use conn2 = openDb ()
        use cmd = conn2.CreateCommand()
        cmd.CommandText <- "SELECT session_id, raw_memory, rollout_summary, rollout_slug FROM stage1_outputs WHERE session_id = 'cli:persist-session'"
        use reader = cmd.ExecuteReader()
        Assert.True(reader.Read(), "Expected stage1_output row")
        Assert.Equal("cli:persist-session", reader.GetString(0))
        Assert.Equal("mem content", reader.GetString(1))
        Assert.Equal("Deployed service.", reader.GetString(2))
        Assert.Equal("deploy_service", reader.GetString(3))
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``extractSession returns Phase1Failed when provider returns error`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "cli:error-session"
        let msgs = [ UserMessage ("Hello", []); AssistantMessage ("Hi", None) ]
        let snap = mkSnap sid msgs
        let deps = mkDeps (mkErrorProvider ()) tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let result = extractSession openDb deps sid snap token |> Async.RunSynchronously
        Assert.Equal(Phase1Failed, result)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``extractSession returns Phase1Failed when provider returns text-only response`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "cli:textonly-session"
        let msgs = [ UserMessage ("Do something.", []); AssistantMessage ("OK.", None) ]
        let snap = mkSnap sid msgs
        let deps = mkDeps (mkTextOnlyProvider ()) tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let result = extractSession openDb deps sid snap token |> Async.RunSynchronously
        Assert.Equal(Phase1Failed, result)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``extractSession minimum signal gate returns SucceededNoOutput when raw_memory and summary both empty`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "cli:empty-signal"
        let msgs = [ UserMessage ("What time is it?", []); AssistantMessage ("12:00", None) ]
        let snap = mkSnap sid msgs
        let deps = mkDeps (mkSavePhase1Provider "" "" "") tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let result = extractSession openDb deps sid snap token |> Async.RunSynchronously
        Assert.Equal(SucceededNoOutput, result)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``extractSession extracts channel from session id prefix`` () =
    let openDb, tmp = mkDb ()
    try
        Directory.CreateDirectory(Path.Combine(tmp, "memory")) |> ignore
        let sid  = SessionId "telegram:extract-chan"
        let msgs = [ UserMessage ("Hello from telegram.", []); AssistantMessage ("Hi", None) ]
        let snap = mkSnap sid msgs
        let deps = mkDeps (mkSavePhase1Provider "mem" "summary" "slug") tmp
        let token = Guid.NewGuid().ToString("N")
        use conn = openDb ()
        syncSession conn snap |> Async.RunSynchronously
        let result = extractSession openDb deps sid snap token |> Async.RunSynchronously
        Assert.Equal(SucceededWithOutput, result)
        // Verify channel was extracted correctly
        use conn2 = openDb ()
        use cmd = conn2.CreateCommand()
        cmd.CommandText <- "SELECT channel FROM stage1_outputs WHERE session_id = 'telegram:extract-chan'"
        let chObj = cmd.ExecuteScalar()
        let ch = if chObj = null then "" else string chObj
        Assert.Equal("telegram", ch)
    finally
        try Directory.Delete(tmp, true) with _ -> ()
