module BotSharp.Tests.Application.HeartbeatServiceTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.HeartbeatService

// ═══════════════════════════════════════════════════════════════════════════
// Stub helpers
// ═══════════════════════════════════════════════════════════════════════════

/// Build an LLMResponse whose body is a heartbeat tool call with the given action + tasks.
let private mkHeartbeatToolResponse (action: string) (tasks: string option) : LLMResponse =
    let argsMap =
        let base_ = Map.ofList [ "action", JsonSerializer.SerializeToElement(action) ]
        match tasks with
        | None   -> base_
        | Some t -> Map.add "tasks" (JsonSerializer.SerializeToElement(t)) base_
    let call : ToolCall = {
        Id           = ToolCallId "hb-test-1"
        Tool         = ToolName "heartbeat"
        Arguments    = argsMap
        ProviderMeta = None
    }
    { Body             = WithToolCalls (None, NonEmptyList.singleton call)
      ReasoningContent = None
      ThinkingBlocks   = []
      Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
      FinishReason     = None
    }

/// Stub that always returns a pre-built LLMResponse.
let private constProvider (response: LLMResponse) : LLMProvider = {
    Id           = "stub"
    DefaultModel = "test-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async { return Result.Ok response }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

/// Stub that always returns an LLM error.
let private errorProvider : LLMProvider = {
    Id           = "error"
    DefaultModel = "test-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async {
        return Result.Error { Kind = ServerError 500; RawMessage = "simulated error"; ProviderCode = None }
    }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

/// Stub that returns a plain text response (no tool call).
let private textProvider (text: string) : LLMProvider =
    let response = {
        Body             = TextOnly text
        ReasoningContent = None
        ThinkingBlocks   = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = None
    }
    constProvider response

let private runProvider  (tasks: string) = constProvider (mkHeartbeatToolResponse "run"  (Some tasks))
let private skipProvider ()              = constProvider (mkHeartbeatToolResponse "skip" None)

let private withTempDir (f: string -> Async<unit>) =
    async {
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        try
            do! f dir
        finally
            try Directory.Delete(dir, recursive = true) with _ -> ()
    }

let private writeHeartbeat (dir: string) (content: string) =
    File.WriteAllText(Path.Combine(dir, "HEARTBEAT.md"), content)

// ═══════════════════════════════════════════════════════════════════════════
// TriggerNow — file-presence gate
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``TriggerNow with no HEARTBEAT.md does not call onExecute`` () =
    withTempDir (fun dir -> async {
        let mutable executed = false
        let svc =
            HeartbeatService(
                dir, errorProvider, "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called when HEARTBEAT.md is absent")
    }) |> Async.RunSynchronously

[<Fact>]
let ``TriggerNow with empty HEARTBEAT.md does not call onExecute`` () =
    withTempDir (fun dir -> async {
        writeHeartbeat dir ""
        let mutable executed = false
        let svc =
            HeartbeatService(
                dir, errorProvider, "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called for an empty HEARTBEAT.md")
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// TriggerNow — LLM decision gate
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``TriggerNow with LLM returning skip does not call onExecute`` () =
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n<!-- none -->"
        let mutable executed = false
        let svc =
            HeartbeatService(
                dir, skipProvider (), "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called when LLM returns action=skip")
    }) |> Async.RunSynchronously

[<Fact>]
let ``TriggerNow with LLM error conservatively skips and does not call onExecute`` () =
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Do something"
        let mutable executed = false
        let svc =
            HeartbeatService(
                dir, errorProvider, "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called when the LLM returns an error")
    }) |> Async.RunSynchronously

[<Fact>]
let ``TriggerNow with LLM returning TextOnly does not call onExecute`` () =
    // LLM chose not to use the tool — conservative skip.
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Do something"
        let mutable executed = false
        let svc =
            HeartbeatService(
                dir, textProvider "I see no tasks.", "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called when LLM returns text instead of a tool call")
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// TriggerNow — execution + notify path
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``TriggerNow with run decision calls onExecute with the task text`` () =
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Send morning report"
        let mutable receivedTasks : string list = []
        let svc =
            HeartbeatService(
                dir, runProvider "Send morning report", "model",
                onExecute = (fun tasks -> async { receivedTasks <- tasks; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.Equal<string list>([ "Send morning report" ], receivedTasks)
    }) |> Async.RunSynchronously

[<Fact>]
let ``TriggerNow when onExecute returns Some text calls onNotify with that text`` () =
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Do something"
        let mutable notified : string option = None
        let svc =
            HeartbeatService(
                dir, runProvider "Do something", "model",
                onExecute = (fun _ -> async { return Some "task complete" }),
                onNotify  = (fun text -> async { notified <- Some text }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.Equal(Some "task complete", notified)
    }) |> Async.RunSynchronously

[<Fact>]
let ``TriggerNow when onExecute returns None does not call onNotify`` () =
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Do something"
        let mutable notified = false
        let svc =
            HeartbeatService(
                dir, runProvider "Do something", "model",
                onExecute = (fun _ -> async { return None }),
                onNotify  = (fun _ -> async { notified <- true }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(notified, "onNotify must not be called when onExecute returns None")
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// Start / Stop — lifecycle
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Start and Stop do not throw`` () =
    withTempDir (fun dir -> async {
        // Use a 1h interval so no tick fires during this test.
        let svc =
            HeartbeatService(
                dir, errorProvider, "model",
                onExecute = (fun _ -> async { return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        svc.Start()
        do! Async.Sleep 50
        svc.Stop()
        // Verify no exception was thrown — test passes by completing without error.
    }) |> Async.RunSynchronously

[<Fact>]
let ``Stop before Start does not throw`` () =
    withTempDir (fun dir -> async {
        let svc =
            HeartbeatService(
                dir, errorProvider, "model",
                onExecute = (fun _ -> async { return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        // Stop before Start — must not throw.
        svc.Stop()
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// decide — Empty body branch (distinct from TextOnly)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``TriggerNow with Empty LLM response body does not call onExecute`` () =
    // The Empty branch in decide (| TextOnly _ | Empty -> SkipHeartbeat)
    // is separate from the TextOnly case already covered above.
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Do something"
        let mutable executed = false
        let emptyResp : LLMResponse = {
            Body             = Empty
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 1; CompletionTokens = 0; CachedTokens = 0 }
            FinishReason     = None
        }
        let svc =
            HeartbeatService(
                dir, constProvider emptyResp, "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called when LLM returns Empty body")
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// decide — action=run with empty tasks string
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``TriggerNow with run action and empty tasks string calls onExecute with empty list`` () =
    // tasks="" → | null | "" -> [] → RunHeartbeat [] → onExecute []
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Something"
        let mutable receivedTasks : string list option = None
        let svc =
            HeartbeatService(
                dir, runProvider "", "model",
                onExecute = (fun tasks -> async { receivedTasks <- Some tasks; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        match receivedTasks with
        | None       -> Assert.Fail("onExecute was not called")
        | Some tasks -> Assert.Empty(tasks)
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// decide — missing action field defaults to skip
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``TriggerNow with heartbeat tool call but no action field defaults to skip`` () =
    // Tool call arguments = {} (no "action" key) → | _ -> "skip" fallback → SkipHeartbeat
    withTempDir (fun dir -> async {
        writeHeartbeat dir "# Tasks\n\n- Do something"
        let mutable executed = false
        // Provider returns a heartbeat tool call with NO action field in arguments
        let call : ToolCall = {
            Id           = ToolCallId "hb-no-action"
            Tool         = ToolName "heartbeat"
            Arguments    = Map.empty   // deliberately empty — no "action" key
            ProviderMeta = None
        }
        let response = {
            Body             = WithToolCalls (None, NonEmptyList.singleton call)
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
            FinishReason     = None
        }
        let svc =
            HeartbeatService(
                dir, constProvider response, "model",
                onExecute = (fun _ -> async { executed <- true; return None }),
                onNotify  = (fun _ -> async { return () }),
                intervalSeconds = 3600)
        do! svc.TriggerNow()
        Assert.False(executed, "onExecute must not be called when action field is absent (defaults to skip)")
    }) |> Async.RunSynchronously
