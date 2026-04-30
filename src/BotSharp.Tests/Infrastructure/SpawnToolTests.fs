module BotSharp.Tests.Infrastructure.SpawnToolTests

open System
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.SubagentManager
open BotSharp.Infrastructure.Tools.SpawnTool

// ═══════════════════════════════════════════════════════════════════════════
// Stub helpers
// ═══════════════════════════════════════════════════════════════════════════

let private stubProvider : LLMProvider = {
    Id           = "stub"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async { return Result.Ok { Body = TextOnly "done"; ReasoningContent = None; ThinkingBlocks = []; Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None } }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

let private stubDeps : AgentDependencies = {
    Provider          = stubProvider
    Tools             = Map.empty
    LoadSession       = fun sid -> async { return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow) }
    PersistSession    = fun _ -> async { return Result.Ok () }
    BuildSystemPrompt = fun _ _ -> async { return "stub" }
    Config            = BotSharpConfig.defaults
    StreamHook        = NoStreaming
    Hook              = AgentHook.none
    CronService       = None
    LastTokenUsage    = ref None
    CurrentIteration  = ref 0
    RuleEngine        = None
    FallbackProviders = []
}

/// No-op completion callback for tests.
let private noopComplete : OnSubagentComplete = fun _ -> async { return () }

let private makeMgr () = SubagentManager(stubDeps, noopComplete)

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private makeArgs (pairs: (string * string) list) : Map<string, JsonElement> =
    pairs |> List.map (fun (k, v) -> k, jsonStr v) |> Map.ofList

// ═══════════════════════════════════════════════════════════════════════════
// spawnToolSpec — schema correctness
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``spawnToolSpec has correct tool name`` () =
    let (ToolName n) = spawnToolSpec.Name
    Assert.Equal("spawn", n)

[<Fact>]
let ``spawnToolSpec requires task parameter`` () =
    let param = spawnToolSpec.Parameters.["task"]
    Assert.True(param.Required)
    Assert.Equal(JsString, param.Type)

[<Fact>]
let ``spawnToolSpec requires channel parameter`` () =
    let param = spawnToolSpec.Parameters.["channel"]
    Assert.True(param.Required)
    Assert.Equal(JsString, param.Type)

[<Fact>]
let ``spawnToolSpec requires chat parameter`` () =
    let param = spawnToolSpec.Parameters.["chat"]
    Assert.True(param.Required)
    Assert.Equal(JsString, param.Type)

[<Fact>]
let ``spawnToolSpec has optional label parameter`` () =
    let param = spawnToolSpec.Parameters.["label"]
    Assert.False(param.Required)
    Assert.Equal(JsString, param.Type)

// ═══════════════════════════════════════════════════════════════════════════
// executeSpawn — argument validation failures (mgr.Spawn never called)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeSpawn returns ToolFailure when task is missing`` () =
    let mgr  = makeMgr ()
    let args = makeArgs [ "channel", "cli"; "chat", "c1" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for missing task, got {other}")

[<Fact>]
let ``executeSpawn returns ToolFailure when channel is missing`` () =
    let mgr  = makeMgr ()
    let args = makeArgs [ "task", "do stuff"; "chat", "c1" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for missing channel, got {other}")

[<Fact>]
let ``executeSpawn returns ToolFailure when chat is missing`` () =
    let mgr  = makeMgr ()
    let args = makeArgs [ "task", "do stuff"; "channel", "cli" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for missing chat, got {other}")

[<Fact>]
let ``executeSpawn returns ToolFailure when all required args are missing`` () =
    let mgr  = makeMgr ()
    let args = Map.empty
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for empty args, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// executeSpawn — success path
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeSpawn returns ToolSuccess with confirmation message when all args provided`` () =
    let mgr  = makeMgr ()
    let args = makeArgs [ "task", "analyze logs"; "channel", "cli"; "chat", "test-chat" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        // Confirmation should mention the task label or "started"
        Assert.True(
            msg.Contains("started") || msg.Contains("Subagent"),
            $"Expected confirmation message, got: {msg}")
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``executeSpawn confirmation includes subagent label in message`` () =
    let mgr  = makeMgr ()
    let args = makeArgs [
        "task",    "check the deployment"
        "label",   "deploy-check"
        "channel", "cli"
        "chat",    "test-chat" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("deploy-check", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with label, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// allTools registration
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allTools returns exactly 1 tool`` () =
    let mgr   = makeMgr ()
    let tools = allTools mgr
    Assert.Equal(1, tools.Length)

[<Fact>]
let ``allTools tool name is spawn`` () =
    let mgr   = makeMgr ()
    let tools = allTools mgr
    let (spec, _) = List.head tools
    let (ToolName n) = spec.Name
    Assert.Equal("spawn", n)

// ═══════════════════════════════════════════════════════════════════════════
// executeSpawn — specific error type for validation failures
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeSpawn ToolFailure for missing task is ParameterMissing`` () =
    // requireStringArg produces ParameterMissing when the key is absent
    let mgr  = makeMgr ()
    let args = makeArgs [ "channel", "cli"; "chat", "c1" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolFailure (ParameterMissing _) -> ()
    | other -> Assert.Fail($"Expected ToolFailure(ParameterMissing), got {other}")

[<Fact>]
let ``executeSpawn without label arg passes None to Spawn`` () =
    // No "label" key in args — tryStringArg returns None — label displays as task prefix
    let mgr  = makeMgr ()
    let args = makeArgs [ "task", "the actual task"; "channel", "cli"; "chat", "c1" ]
    let result = executeSpawn mgr args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        // Without an explicit label, the task text (or prefix) should appear in the message
        Assert.True(msg.Contains("the actual task") || msg.Contains("started"),
                    $"Unexpected confirmation message: {msg}")
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
