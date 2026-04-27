module BotSharp.Tests.Domain.StateMachineTests

open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.StateMachine

// ═══════════════════════════════════════════════════════════════════════════
// Shared test fixtures
// ═══════════════════════════════════════════════════════════════════════════

let private emptyRequest : LLMRequest = {
    Messages = []
    Tools    = []
    Model    = "test"
    Settings = GenerationSettings.defaults
}

let private dummyCall : ToolCall = {
    Id           = ToolCallId "call1"
    Tool         = ToolName "test_tool"
    Arguments    = Map.empty
    ProviderMeta = None
}

let private dummyCalls = NonEmptyList.singleton dummyCall

let private dummyInbound : InboundMessage = {
    Channel            = ChannelId "cli"
    Sender             = UserId "user"
    Chat               = ChatId "test"
    Input              = ChatMessage ("hello", [])
    Metadata           = Map.empty
    SessionKeyOverride = None
}

// ═══════════════════════════════════════════════════════════════════════════
// Legal transitions
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Idle + MessageReceived -> BuildingPrompt []`` () =
    let result = transition Idle (MessageReceived dummyInbound)
    Assert.Equal(BuildingPrompt [], result)

[<Fact>]
let ``BuildingPrompt + PromptBuilt -> AwaitingLLM (request, 0)`` () =
    let result = transition (BuildingPrompt []) (PromptBuilt emptyRequest)
    Assert.Equal(AwaitingLLM (emptyRequest, 0), result)

[<Fact>]
let ``AwaitingLLM + LlmRespondedWithText -> Finalizing with that content`` () =
    let result = transition (AwaitingLLM (emptyRequest, 0)) (LlmRespondedWithText ("hi", None))
    Assert.Equal(Finalizing ("hi", None), result)

[<Fact>]
let ``AwaitingLLM + LlmRespondedWithTools -> ExecutingTools with iter preserved`` () =
    let result = transition (AwaitingLLM (emptyRequest, 0)) (LlmRespondedWithTools (dummyCalls, None))
    match result with
    | ExecutingTools (calls, _, iter) ->
        Assert.Equal(dummyCalls, calls)
        Assert.Equal(0, iter)
    | other -> Assert.Fail($"Expected ExecutingTools, got {other}")

[<Fact>]
let ``AwaitingLLM + LlmRespondedWithTools appends ToolCallMessage to pending`` () =
    let messages = [ AssistantMessage ("context", None) ]
    let req = { emptyRequest with Messages = messages }
    let result = transition (AwaitingLLM (req, 0)) (LlmRespondedWithTools (dummyCalls, None))
    match result with
    | ExecutingTools (_, pending, _) ->
        // pending = req.Messages @ [ToolCallMessage dummyCalls]
        Assert.Equal(2, List.length pending)
        Assert.Equal(AssistantMessage ("context", None), List.item 0 pending)
        Assert.Equal(ToolCallMessage (dummyCalls, None), List.item 1 pending)
    | other -> Assert.Fail($"Expected ExecutingTools, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted [] (empty stop signal) -> Finalizing with iter count`` () =
    // ToolsExecuted [] = forced stop; the state machine finalizes immediately.
    // AgentLoop uses this signal to stop the loop (via Finalizing directly now,
    // but this case is preserved for tests that call the state machine directly.)
    let state = ExecutingTools (dummyCalls, [], 5)
    let result = transition state (ToolsExecuted [])
    match result with
    | Finalizing (msg, _) -> Assert.Contains("5", msg)
    | other -> Assert.Fail($"Expected Finalizing, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted (non-empty) -> AwaitingLLM with incremented iter`` () =
    let state = ExecutingTools (dummyCalls, [], 3)
    let result = transition state (ToolsExecuted [ (dummyCall, ToolSuccess "x") ])
    match result with
    | AwaitingLLM (_, iter) -> Assert.Equal(4, iter)
    | other -> Assert.Fail($"Expected AwaitingLLM with iter=4, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted appends ToolResultMessages to pending`` () =
    let pending = [ AssistantMessage ("prior", None) ]
    let state = ExecutingTools (dummyCalls, pending, 0)
    let toolResult = ToolSuccess "result content"
    let result = transition state (ToolsExecuted [ (dummyCall, toolResult) ])
    match result with
    | AwaitingLLM (req, _) ->
        // pending (1 msg) + 1 result message = 2
        Assert.Equal(2, List.length req.Messages)
        match List.item 1 req.Messages with
        | ToolResultMessage (id, name, content) ->
            Assert.Equal(ToolCallId "call1", id)
            Assert.Equal(ToolName "test_tool", name)
            Assert.Equal("result content", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted formats ToolFailure ExecutionFailed into message`` () =
    let state = ExecutingTools (dummyCalls, [], 0)
    let toolResult = ToolFailure (ExecutionFailed "boom")
    let result = transition state (ToolsExecuted [ (dummyCall, toolResult) ])
    match result with
    | AwaitingLLM (req, _) ->
        match List.item 0 req.Messages with
        | ToolResultMessage (_, _, content) ->
            Assert.Contains("boom", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

[<Fact>]
let ``Finalizing + ResponseSent -> Idle`` () =
    let result = transition (Finalizing ("done", None)) ResponseSent
    Assert.Equal(Idle, result)

// ═══════════════════════════════════════════════════════════════════════════
// Illegal transitions: state is returned unchanged
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Idle + ResponseSent -> Idle (unchanged)`` () =
    let state = Idle
    Assert.Equal(state, transition state ResponseSent)

[<Fact>]
let ``Idle + PromptBuilt -> Idle (unchanged)`` () =
    let state = Idle
    Assert.Equal(state, transition state (PromptBuilt emptyRequest))

[<Fact>]
let ``Idle + LlmRespondedWithText -> Idle (unchanged)`` () =
    let state = Idle
    Assert.Equal(state, transition state (LlmRespondedWithText ("oops", None)))

[<Fact>]
let ``Idle + ToolsExecuted -> Idle (unchanged)`` () =
    let state = Idle
    Assert.Equal(state, transition state (ToolsExecuted []))

[<Fact>]
let ``BuildingPrompt + MessageReceived -> BuildingPrompt (unchanged)`` () =
    let state = BuildingPrompt [ AssistantMessage ("x", None) ]
    Assert.Equal(state, transition state (MessageReceived dummyInbound))

[<Fact>]
let ``BuildingPrompt + ResponseSent -> BuildingPrompt (unchanged)`` () =
    let state = BuildingPrompt []
    Assert.Equal(state, transition state ResponseSent)

[<Fact>]
let ``AwaitingLLM + ResponseSent -> AwaitingLLM (unchanged)`` () =
    let state = AwaitingLLM (emptyRequest, 3)
    Assert.Equal(state, transition state ResponseSent)

[<Fact>]
let ``AwaitingLLM + ToolsExecuted -> AwaitingLLM (unchanged)`` () =
    let state = AwaitingLLM (emptyRequest, 3)
    Assert.Equal(state, transition state (ToolsExecuted []))

[<Fact>]
let ``ExecutingTools + ResponseSent -> ExecutingTools (unchanged)`` () =
    let state = ExecutingTools (dummyCalls, [], 0)
    Assert.Equal(state, transition state ResponseSent)

[<Fact>]
let ``ExecutingTools + LlmRespondedWithText -> ExecutingTools (unchanged)`` () =
    let state = ExecutingTools (dummyCalls, [], 0)
    Assert.Equal(state, transition state (LlmRespondedWithText ("stray", None)))

[<Fact>]
let ``Finalizing + MessageReceived -> Finalizing (unchanged)`` () =
    let state = Finalizing ("final answer", None)
    Assert.Equal(state, transition state (MessageReceived dummyInbound))

[<Fact>]
let ``Finalizing + ToolsExecuted -> Finalizing (unchanged)`` () =
    let state = Finalizing ("final answer", None)
    Assert.Equal(state, transition state (ToolsExecuted []))

// ═══════════════════════════════════════════════════════════════════════════
// Consolidating illegal transitions
// ═══════════════════════════════════════════════════════════════════════════

let private dummyConsolidating =
    Consolidating (SessionSnapshot.empty (SessionId "s") System.DateTimeOffset.UtcNow)

[<Fact>]
let ``Consolidating + MessageReceived -> Consolidating (unchanged)`` () =
    Assert.Equal(dummyConsolidating, transition dummyConsolidating (MessageReceived dummyInbound))

[<Fact>]
let ``Consolidating + PromptBuilt -> Consolidating (unchanged)`` () =
    Assert.Equal(dummyConsolidating, transition dummyConsolidating (PromptBuilt emptyRequest))

[<Fact>]
let ``Consolidating + LlmRespondedWithText -> Consolidating (unchanged)`` () =
    Assert.Equal(dummyConsolidating, transition dummyConsolidating (LlmRespondedWithText ("stray", None)))

[<Fact>]
let ``Consolidating + LlmRespondedWithTools -> Consolidating (unchanged)`` () =
    Assert.Equal(dummyConsolidating, transition dummyConsolidating (LlmRespondedWithTools (dummyCalls, None)))

[<Fact>]
let ``Consolidating + ToolsExecuted -> Consolidating (unchanged)`` () =
    Assert.Equal(dummyConsolidating, transition dummyConsolidating (ToolsExecuted []))

[<Fact>]
let ``Consolidating + ResponseSent -> Consolidating (unchanged)`` () =
    Assert.Equal(dummyConsolidating, transition dummyConsolidating ResponseSent)

// ═══════════════════════════════════════════════════════════════════════════
// ToolFailure formatting — remaining variants
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ExecutingTools + ToolsExecuted formats ToolFailure ToolNotFound into message`` () =
    let state  = ExecutingTools (dummyCalls, [], 0)
    let result = transition state (ToolsExecuted [ (dummyCall, ToolFailure (ToolNotFound (ToolName "missing_tool"))) ])
    match result with
    | AwaitingLLM (req, _) ->
        match List.item 0 req.Messages with
        | ToolResultMessage (_, _, content) -> Assert.Contains("missing_tool", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted formats ToolFailure ParameterMissing into message`` () =
    let state  = ExecutingTools (dummyCalls, [], 0)
    let result = transition state (ToolsExecuted [ (dummyCall, ToolFailure (ParameterMissing "path")) ])
    match result with
    | AwaitingLLM (req, _) ->
        match List.item 0 req.Messages with
        | ToolResultMessage (_, _, content) -> Assert.Contains("path", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted formats ToolFailure ParameterInvalid into message`` () =
    let state  = ExecutingTools (dummyCalls, [], 0)
    let result = transition state (ToolsExecuted [ (dummyCall, ToolFailure (ParameterInvalid ("limit", "must be positive"))) ])
    match result with
    | AwaitingLLM (req, _) ->
        match List.item 0 req.Messages with
        | ToolResultMessage (_, _, content) ->
            Assert.Contains("limit", content)
            Assert.Contains("must be positive", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted formats ToolFailure ExecutionTimeout into message`` () =
    let state  = ExecutingTools (dummyCalls, [], 0)
    let result = transition state (ToolsExecuted [ (dummyCall, ToolFailure (ExecutionTimeout (System.TimeSpan.FromSeconds 30.0))) ])
    match result with
    | AwaitingLLM (req, _) ->
        match List.item 0 req.Messages with
        | ToolResultMessage (_, _, content) -> Assert.Contains("30", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

[<Fact>]
let ``ExecutingTools + ToolsExecuted formats ToolFailure WorkspaceViolation into message`` () =
    let state  = ExecutingTools (dummyCalls, [], 0)
    let result = transition state (ToolsExecuted [ (dummyCall, ToolFailure (WorkspaceViolation "/etc/passwd")) ])
    match result with
    | AwaitingLLM (req, _) ->
        match List.item 0 req.Messages with
        | ToolResultMessage (_, _, content) -> Assert.Contains("/etc/passwd", content)
        | other -> Assert.Fail($"Expected ToolResultMessage, got {other}")
    | other -> Assert.Fail($"Expected AwaitingLLM, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Remaining illegal transitions (each line in StateMachine.fs covered once)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Idle + LlmRespondedWithTools -> Idle (unchanged)`` () =
    Assert.Equal(Idle, transition Idle (LlmRespondedWithTools (dummyCalls, None)))

[<Fact>]
let ``BuildingPrompt + LlmRespondedWithText -> BuildingPrompt (unchanged)`` () =
    let state = BuildingPrompt []
    Assert.Equal(state, transition state (LlmRespondedWithText ("stray", None)))

[<Fact>]
let ``BuildingPrompt + LlmRespondedWithTools -> BuildingPrompt (unchanged)`` () =
    let state = BuildingPrompt []
    Assert.Equal(state, transition state (LlmRespondedWithTools (dummyCalls, None)))

[<Fact>]
let ``BuildingPrompt + ToolsExecuted -> BuildingPrompt (unchanged)`` () =
    let state = BuildingPrompt []
    Assert.Equal(state, transition state (ToolsExecuted []))

[<Fact>]
let ``AwaitingLLM + MessageReceived -> AwaitingLLM (unchanged)`` () =
    let state = AwaitingLLM (emptyRequest, 1)
    Assert.Equal(state, transition state (MessageReceived dummyInbound))

[<Fact>]
let ``AwaitingLLM + PromptBuilt -> AwaitingLLM (unchanged)`` () =
    let state = AwaitingLLM (emptyRequest, 1)
    Assert.Equal(state, transition state (PromptBuilt emptyRequest))

[<Fact>]
let ``ExecutingTools + MessageReceived -> ExecutingTools (unchanged)`` () =
    let state = ExecutingTools (dummyCalls, [], 0)
    Assert.Equal(state, transition state (MessageReceived dummyInbound))

[<Fact>]
let ``ExecutingTools + PromptBuilt -> ExecutingTools (unchanged)`` () =
    let state = ExecutingTools (dummyCalls, [], 0)
    Assert.Equal(state, transition state (PromptBuilt emptyRequest))

[<Fact>]
let ``ExecutingTools + LlmRespondedWithTools -> ExecutingTools (unchanged)`` () =
    let state = ExecutingTools (dummyCalls, [], 0)
    Assert.Equal(state, transition state (LlmRespondedWithTools (dummyCalls, None)))

[<Fact>]
let ``Finalizing + PromptBuilt -> Finalizing (unchanged)`` () =
    let state = Finalizing ("done", None)
    Assert.Equal(state, transition state (PromptBuilt emptyRequest))

[<Fact>]
let ``Finalizing + LlmRespondedWithText -> Finalizing (unchanged)`` () =
    let state = Finalizing ("done", None)
    Assert.Equal(state, transition state (LlmRespondedWithText ("stray", None)))

[<Fact>]
let ``Finalizing + LlmRespondedWithTools -> Finalizing (unchanged)`` () =
    let state = Finalizing ("done", None)
    Assert.Equal(state, transition state (LlmRespondedWithTools (dummyCalls, None)))
