module BotSharp.Tests.Application.SubagentManagerTests

open System
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.SubagentManager

// ═══════════════════════════════════════════════════════════════════════════
// Stub helpers (same pattern as SessionActorTests)
// ═══════════════════════════════════════════════════════════════════════════

let private stubProvider : LLMProvider = {
    Id           = "stub"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async {
        return Result.Ok {
            Body             = TextOnly "task done"
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
            FinishReason     = None
        }
    }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

let private stubDeps : AgentDependencies = {
    Provider          = stubProvider
    Tools             = Map.empty
    LoadSession       = fun sid -> async { return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow) }
    PersistSession    = fun _   -> async { return Result.Ok () }
    BuildSystemPrompt = fun _ _ -> async { return "stub system prompt" }
    Config            = BotSharpConfig.defaults
    StreamHook        = NoStreaming
    Hook              = AgentHook.none
    CronService       = None
    LastTokenUsage    = ref None
    CurrentIteration  = ref 0
}

let private noopComplete : OnSubagentComplete = fun _ -> async { return () }

let private errorProvider : LLMProvider = {
    Id           = "error"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    // Zero retries so the test doesn't hang waiting for backoff delays.
    RetryPolicy  = { RetryPolicy.standard with Mode = FixedRetries (0, []) }
    Chat         = fun _ _ _ -> async {
        return Result.Error { Kind = ServerError 503; RawMessage = "simulated error"; ProviderCode = None }
    }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

let private errorDeps : AgentDependencies = {
    stubDeps with Provider = errorProvider
}

// ═══════════════════════════════════════════════════════════════════════════
// Spawn — return value format
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Spawn returns a confirmation string containing 'Subagent'`` () =
    let mgr  = SubagentManager(stubDeps, noopComplete)
    let msg  = mgr.Spawn("do a task", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Assert.Contains("Subagent", msg)

[<Fact>]
let ``Spawn return string contains 'started'`` () =
    let mgr = SubagentManager(stubDeps, noopComplete)
    let msg = mgr.Spawn("do a task", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Assert.Contains("started", msg)

[<Fact>]
let ``Spawn includes the task text as label when no label provided and task is short`` () =
    let mgr  = SubagentManager(stubDeps, noopComplete)
    let task = "check logs"   // <= 30 chars — used verbatim as display label
    let msg  = mgr.Spawn(task, None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Assert.Contains("check logs", msg)

[<Fact>]
let ``Spawn truncates long task text to 30 chars for display label`` () =
    let mgr      = SubagentManager(stubDeps, noopComplete)
    let longTask = "This is a very long task description that exceeds thirty characters"
    let msg      = mgr.Spawn(longTask, None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    // Should contain a prefix of the task (30 chars, indices 0..29) followed by "..."
    Assert.Contains("This is a very long task descr...", msg)

[<Fact>]
let ``Spawn uses provided label instead of task text`` () =
    let mgr  = SubagentManager(stubDeps, noopComplete)
    let msg  = mgr.Spawn("do something complex", Some "my-label", ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Assert.Contains("my-label", msg)

[<Fact>]
let ``Spawn returns immediately without waiting for subagent to finish`` () =
    // The stub provider returns instantly, but even with a slow provider the
    // Spawn call should return before the background work completes.
    // We verify this by using noopComplete and checking the return is fast.
    let mgr   = SubagentManager(stubDeps, noopComplete)
    let start = DateTime.UtcNow
    let _     = mgr.Spawn("long task", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    let elapsed = (DateTime.UtcNow - start).TotalSeconds
    Assert.True(elapsed < 5.0, $"Spawn should return quickly, took {elapsed}s")

// ═══════════════════════════════════════════════════════════════════════════
// onComplete callback — eventually invoked after subagent finishes
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``onComplete callback is called after subagent finishes`` () =
    let mutable completedCount = 0
    let trackComplete : OnSubagentComplete = fun _ ->
        async { completedCount <- completedCount + 1 }
    let mgr = SubagentManager(stubDeps, trackComplete)
    let _   = mgr.Spawn("run task", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    // Give the background work time to finish (stub provider is instant)
    Async.Sleep 200 |> Async.RunSynchronously
    Assert.Equal(1, completedCount)

[<Fact>]
let ``onComplete message targets the originChannel and originChat`` () =
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg ->
        async { received <- Some msg }
    let mgr = SubagentManager(stubDeps, capture)
    let _   = mgr.Spawn("analyze", None, ChannelId "myChannel", ChatId "myChat") |> Async.RunSynchronously
    Async.Sleep 200 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg ->
        Assert.Equal(ChannelId "myChannel", msg.Channel)
        Assert.Equal(ChatId "myChat",       msg.Chat)

[<Fact>]
let ``onComplete message sender is 'subagent'`` () =
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg ->
        async { received <- Some msg }
    let mgr = SubagentManager(stubDeps, capture)
    let _   = mgr.Spawn("check", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Async.Sleep 200 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg -> Assert.Equal(UserId "subagent", msg.Sender)

[<Fact>]
let ``onComplete message contains the task text`` () =
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg ->
        async { received <- Some msg }
    let mgr  = SubagentManager(stubDeps, capture)
    let task = "inspect the deployment logs"
    let _    = mgr.Spawn(task, None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Async.Sleep 200 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg ->
        match msg.Input with
        | ChatMessage (text, _) ->
            Assert.Contains(task, text)
        | other -> Assert.Fail($"Expected ChatMessage, got {other}")

[<Fact>]
let ``two separate Spawns invoke onComplete twice`` () =
    let mutable count = 0
    let track : OnSubagentComplete = fun _ ->
        async { System.Threading.Interlocked.Increment(&count) |> ignore }
    let mgr = SubagentManager(stubDeps, track)
    let _   = mgr.Spawn("task-a", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    let _   = mgr.Spawn("task-b", None, ChannelId "cli", ChatId "c2") |> Async.RunSynchronously
    Async.Sleep 1000 |> Async.RunSynchronously
    Assert.Equal(2, count)

[<Fact>]
let ``onComplete message contains 'failed' when agent loop errors`` () =
    // When the provider returns an error, announce is called with Result.Error.
    // The content should say "failed" rather than "completed successfully".
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg -> async { received <- Some msg }
    let mgr = SubagentManager(errorDeps, capture)
    let _   = mgr.Spawn("run failing task", None, ChannelId "cli", ChatId "err-chat") |> Async.RunSynchronously
    Async.Sleep 500 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called after error")
    | Some msg ->
        match msg.Input with
        | ChatMessage (text, _) -> Assert.Contains("failed", text)
        | other -> Assert.Fail($"Expected ChatMessage, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Label boundary: exactly 30 chars is not truncated
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Spawn does not truncate task text at exactly 30 chars`` () =
    let mgr      = SubagentManager(stubDeps, noopComplete)
    let task30   = String.replicate 30 "x"   // exactly 30 chars
    let msg      = mgr.Spawn(task30, None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    // Should contain the full 30-char text, not task30.[..29] + "..."
    Assert.Contains(task30, msg)
    Assert.DoesNotContain("...", msg)

[<Fact>]
let ``Spawn truncates task text at 31 chars with ellipsis`` () =
    let mgr      = SubagentManager(stubDeps, noopComplete)
    let task31   = String.replicate 31 "y"   // 31 chars — one over the boundary
    let msg      = mgr.Spawn(task31, None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Assert.Contains("...", msg)

// ═══════════════════════════════════════════════════════════════════════════
// onComplete message metadata
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``onComplete message metadata has source = 'subagent'`` () =
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg -> async { received <- Some msg }
    let mgr = SubagentManager(stubDeps, capture)
    let _   = mgr.Spawn("check source meta", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Async.Sleep 300 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg ->
        match msg.Metadata.TryFind "source" with
        | Some v -> Assert.Equal("subagent", v)
        | None   -> Assert.Fail("Expected 'source' key in Metadata")

[<Fact>]
let ``onComplete message metadata contains task_id key`` () =
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg -> async { received <- Some msg }
    let mgr = SubagentManager(stubDeps, capture)
    let _   = mgr.Spawn("check task_id meta", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Async.Sleep 300 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg ->
        Assert.True(msg.Metadata.ContainsKey("task_id"), "Expected 'task_id' key in Metadata")

[<Fact>]
let ``onComplete message metadata task_id is 8 hex characters`` () =
    // taskId = Guid.NewGuid().ToString("N").[..7] → 8-char lowercase hex string
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg -> async { received <- Some msg }
    let mgr = SubagentManager(stubDeps, capture)
    let _   = mgr.Spawn("verify task_id format", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Async.Sleep 300 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg ->
        match msg.Metadata.TryFind "task_id" with
        | None     -> Assert.Fail("task_id key not found in metadata")
        | Some tid -> Assert.Equal(8, tid.Length)

[<Fact>]
let ``onComplete message on success path contains 'completed successfully'`` () =
    // Verifies the Result.Ok branch of announce produces the expected status text.
    let mutable received : InboundMessage option = None
    let capture : OnSubagentComplete = fun msg -> async { received <- Some msg }
    let mgr = SubagentManager(stubDeps, capture)
    let _   = mgr.Spawn("successful task", None, ChannelId "cli", ChatId "c1") |> Async.RunSynchronously
    Async.Sleep 300 |> Async.RunSynchronously
    match received with
    | None     -> Assert.Fail("onComplete was not called")
    | Some msg ->
        match msg.Input with
        | ChatMessage (text, _) -> Assert.Contains("completed successfully", text)
        | other -> Assert.Fail($"Expected ChatMessage, got {other}")
