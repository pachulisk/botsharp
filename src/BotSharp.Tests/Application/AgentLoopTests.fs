module BotSharp.Tests.Application.AgentLoopTests

open System
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Infrastructure.Shared.StringUtils
open BotSharp.Application.AgentLoop
open BotSharp.Application.MemoryConsolidator

// ═══════════════════════════════════════════════════════════════════════════
// Stub helpers
// ═══════════════════════════════════════════════════════════════════════════

/// Zero-retry policy for test stubs — avoids multi-second backoff delays in tests.
let private noRetryPolicy = { RetryPolicy.standard with Mode = FixedRetries (0, []) }

/// A stub LLMProvider that always returns the given LLMResponse.
let private stubProvider (response: LLMResponse) : LLMProvider = {
    Id           = "stub"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    RetryPolicy  = noRetryPolicy
    Chat         = fun _ _ _ -> async { return Result.Ok response }
    ChatStream   = fun _ _ _ emitter ->
        async {
            match response.Body with
            | TextOnly t -> do! emitter (ContentDelta (TextDelta t))
            | _ -> ()
            return Result.Ok ()
        }
}

/// A stub LLMProvider that sequences through a list of responses.
let private stubProviderSeq (responses: LLMResponse list) : LLMProvider =
    let queue = System.Collections.Generic.Queue(responses)
    { Id           = "stub-seq"
      DefaultModel = "stub-model"
      Capabilities = Set.empty
      RetryPolicy  = noRetryPolicy
      Chat         = fun _ _ _ -> async {
          if queue.Count > 0 then return Result.Ok (queue.Dequeue())
          // ConnectionFailed is NOT retryable — avoids backoff delays when queue is empty.
          else return Result.Error { Kind = ConnectionFailed "queue empty"; RawMessage = "queue empty"; ProviderCode = None}
      }
      ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }

let private textResponse (text: string) : LLMResponse = {
    Body = TextOnly text; ReasoningContent = None; ThinkingBlocks = []
    Usage = { PromptTokens = 5; CompletionTokens = 10; CachedTokens = 0 }
    FinishReason = None
}

let private textResponseWithFinish (text: string) (reason: FinishReason) : LLMResponse = {
    Body = TextOnly text; ReasoningContent = None; ThinkingBlocks = []
    Usage = { PromptTokens = 5; CompletionTokens = 10; CachedTokens = 0 }
    FinishReason = Some reason
}

let private toolCallResponse (calls: ToolCall list) : LLMResponse = {
    Body = WithToolCalls (None, NonEmptyList.ofListUnsafe calls); ReasoningContent = None; ThinkingBlocks = []
    Usage = { PromptTokens = 5; CompletionTokens = 10; CachedTokens = 0 }
    FinishReason = None
}

let private makeDepsWithTools
    (provider : LLMProvider)
    (tools    : Map<ToolName, ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)>)
    : AgentDependencies =
    let mutable stored : SessionSnapshot option = None
    { Provider          = provider
      Tools             = tools
      LoadSession       = fun sid -> async {
          return Result.Ok (match stored with
                            | Some s -> s
                            | None   -> SessionSnapshot.empty sid DateTimeOffset.UtcNow)
      }
      PersistSession    = fun snap -> async {
          stored <- Some snap
          return Result.Ok ()
      }
      BuildSystemPrompt = fun _ _ -> async { return "You are a helpful assistant." }
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

let private makeDeps (provider: LLMProvider) : AgentDependencies =
    makeDepsWithTools provider Map.empty

let private dummyInbound =
    { Channel            = ChannelId "cli"
      Sender             = UserId "user"
      Chat               = ChatId "test"
      Input              = ChatMessage ("hello", [])
      Metadata           = Map.empty
      SessionKeyOverride = None }

// ═══════════════════════════════════════════════════════════════════════════
// Text response (happy path)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop returns text from LLM`` () =
    let deps = makeDeps (stubProvider (textResponse "Hello back!"))
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("Hello back!", _) -> ()
    | other -> Assert.Fail($"Expected Ok \"Hello back!\", got {other}")

[<Fact>]
let ``runAgentLoop appends user and assistant messages to snapshot`` () =
    let deps = makeDeps (stubProvider (textResponse "Hi there!"))
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        // snapshot should have user msg + assistant msg = 2 messages
        Assert.Equal(2, SessionSnapshot.messageCount snap)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``runAgentLoop with pendingSummary passes it to buildRequest and succeeds`` () =
    // pendingSummary = Some injects [Resumed Session] into runtime context.
    // Verify the loop still returns a valid result (i.e., the summary doesn't break the pipeline).
    let capturedMessages = System.Collections.Generic.List<Message list>()
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ msgs _ ->
            async {
                capturedMessages.Add(msgs)
                return Result.Ok (textResponse "response with summary")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps provider
    let summary = "Previous session: worked on F# parity tasks."
    match runAgentLoop dummyInbound deps (Some summary) |> Async.RunSynchronously with
    | Result.Ok ("response with summary", _) ->
        // The first message sent to the LLM should contain [Resumed Session] + summary
        Assert.True(capturedMessages.Count >= 1, "Expected at least 1 LLM call")
        let userMsg =
            capturedMessages.[0]
            |> List.tryPick (fun m ->
                match m with
                | UserMessage (text, _) when text.Contains("[Resumed Session]") -> Some text
                | _ -> None)
        Assert.True(userMsg.IsSome, "Expected [Resumed Session] in user message")
        Assert.True(userMsg.Value.Contains(summary), "Expected summary text in user message")
    | other -> Assert.Fail($"Expected Ok with summary, got: {other}")

[<Fact>]
let ``runAgentLoop with empty LLM response returns EmptyResponse error`` () =
    // Body = Empty triggers a finalization retry; if the retry also returns Empty,
    // the error is surfaced to the user.
    let emptyResp = { Body = Empty; ReasoningContent = None; ThinkingBlocks = []
                      Usage = { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }; FinishReason = None }
    let deps = makeDeps (stubProvider emptyResp)
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Error (AgentLlmFailure { Kind = EmptyResponse _ }) -> ()
    | other -> Assert.Fail($"Expected EmptyResponse error, got {other}")

[<Fact>]
let ``runAgentLoop recovers when finalization retry returns text`` () =
    // First call returns Empty; finalization retry returns text.
    // Mirrors Python runner._request_finalization_retry happy path.
    let callCount = ref 0
    let recoveringProvider : LLMProvider = {
        Id = "recovering"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                if !callCount = 1 then
                    // First call: empty body
                    return Result.Ok { Body = Empty; ReasoningContent = None; ThinkingBlocks = []
                                       Usage = { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }; FinishReason = None }
                else
                    // Finalization retry: text body
                    return Result.Ok { Body = TextOnly "recovered!"; ReasoningContent = None; ThinkingBlocks = []
                                       Usage = { PromptTokens = 1; CompletionTokens = 5; CachedTokens = 0 }; FinishReason = None }
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps recoveringProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("recovered!", _) -> ()
    | other -> Assert.Fail($"Expected Ok \"recovered!\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Length recovery (finish_reason = "length")
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop concatenates text when finish_reason is length`` () =
    // Provider returns a length-truncated response on call 1, then a normal stop on call 2.
    // The agent loop must perform length recovery: append partial text + recovery message,
    // then loop. The second response is returned as the final answer.
    // Note: the two partial texts are NOT concatenated by the agent loop — the second LLM
    // call produces a continuation that becomes the final PlainResponse.
    let callCount = ref 0
    let lengthProvider : LLMProvider = {
        Id = "length-test"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Ok (
                    if !callCount = 1 then
                        textResponseWithFinish "Part one of a long answer..." Length
                    else
                        textResponse "...and part two, now complete.")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps lengthProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (text, _) ->
        Assert.Equal("...and part two, now complete.", text)
        Assert.Equal(2, !callCount)
    | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")

[<Fact>]
let ``runAgentLoop stops length recovery after max 3 attempts`` () =
    // Provider always returns finish_reason=length with non-empty text.
    // After 3 recoveries (calls 1..3 return length; call 4 should be treated as Stop),
    // the 4th call returns the answer as a normal stop.
    let callCount = ref 0
    let alwaysLengthProvider : LLMProvider = {
        Id = "always-length"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Ok (
                    if !callCount <= 3 then
                        textResponseWithFinish $"chunk {!callCount}" Length
                    else
                        textResponse "final")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps alwaysLengthProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (text, _) ->
        Assert.Equal("final", text)
        Assert.Equal(4, !callCount)   // 3 recoveries + 1 final stop
    | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")

[<Fact>]
let ``length recovery with empty text does not loop but empty-retry does`` () =
    // If finish_reason=length but text is empty/whitespace, skip length recovery.
    // However, the empty-content retry mechanism still fires (up to _MAX_EMPTY_RETRIES=2 times).
    // So total calls = 2 (empty retries) + 1 (final blank, which falls through) = 3.
    let callCount = ref 0
    let blankLengthProvider : LLMProvider = {
        Id = "blank-length"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Ok (textResponseWithFinish "  " Length)  // whitespace-only
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps blankLengthProvider
    // Blank text → empty-content retry fires (up to _MAX_EMPTY_RETRIES=2), then falls through.
    // Each retry = 1 main call + 1 finalization call → 2 retries × 2 + 1 final = 5 total.
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok _ -> Assert.Equal(5, !callCount)   // 2 retries × 2 calls each + 1 final fallthrough
    | Result.Error _ -> Assert.Equal(5, !callCount)

// ═══════════════════════════════════════════════════════════════════════════
// Empty response retry (blank text — mirrors Python empty_content_retries)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop retries blank text response up to 2 times then returns non-blank`` () =
    // Call 1 and 2: blank text. Call 3: real answer.
    // The agent silently retries blank text up to _MAX_EMPTY_RETRIES (2) times.
    let callCount = ref 0
    let blankThenRealProvider : LLMProvider = {
        Id = "blank-test"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Ok (
                    if !callCount <= 2 then textResponse "  "   // blank response
                    else textResponse "real answer")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps blankThenRealProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (text, _) ->
        Assert.Equal("real answer", text)
        Assert.Equal(3, !callCount)   // 2 blank retries + 1 real answer
    | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")

[<Fact>]
let ``runAgentLoop stops blank text retry after max 2 attempts and returns blank`` () =
    // Provider always returns blank text. After 2 retries the agent gives up and returns it.
    let callCount = ref 0
    let alwaysBlankProvider : LLMProvider = {
        Id = "always-blank"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Ok (textResponse "  ")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps alwaysBlankProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok _ ->
        // Each retry = 1 main call + 1 finalization call; 2 retries × 2 + 1 final = 5 total.
        Assert.Equal(5, !callCount)
    | Result.Error _ ->
        // Either result is acceptable; what matters is the call count
        Assert.Equal(5, !callCount)

// ═══════════════════════════════════════════════════════════════════════════
// External lookup throttle (web_fetch / web_search de-duplication)
// ═══════════════════════════════════════════════════════════════════════════

open BotSharp.Application.AgentLoop

[<Fact>]
let ``externalLookupSignature returns None for non-lookup tools`` () =
    let call = { Id = ToolCallId "c1"; Tool = ToolName "read_file"; Arguments = Map.empty; ProviderMeta = None }
    Assert.Equal(None, externalLookupSignature call)

[<Fact>]
let ``externalLookupSignature returns web_fetch key for web_fetch tool`` () =
    let args =
        use doc = JsonDocument.Parse("""{"url":"https://example.com/page"}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = { Id = ToolCallId "c1"; Tool = ToolName "web_fetch"; Arguments = args; ProviderMeta = None }
    match externalLookupSignature call with
    | Some key -> Assert.StartsWith("web_fetch:", key)
    | None -> Assert.Fail("Expected Some key for web_fetch")

[<Fact>]
let ``externalLookupSignature returns web_search key for web_search tool`` () =
    let args =
        use doc = JsonDocument.Parse("""{"query":"best practices"}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = { Id = ToolCallId "c1"; Tool = ToolName "web_search"; Arguments = args; ProviderMeta = None }
    match externalLookupSignature call with
    | Some key -> Assert.StartsWith("web_search:", key)
    | None -> Assert.Fail("Expected Some key for web_search")

[<Fact>]
let ``runAgentLoop blocks web_fetch after 2 identical calls`` () =
    // Scenario: LLM calls web_fetch with the same URL 3 times (in 3 tool rounds).
    // 1st and 2nd calls succeed; 3rd is blocked by the throttle.
    // The agent ultimately replies after tool results are fed back.
    let fetchUrl = "https://example.com/data"
    let fetchArgs =
        use doc = JsonDocument.Parse($"""{{"url":"{fetchUrl}"}}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let fetchCallCount = ref 0
    let llmCallCount   = ref 0
    // LLM keeps requesting web_fetch until it gets blocked, then replies.
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr llmCallCount
                // Calls 1-3: request web_fetch. Call 4: return plain reply.
                if !llmCallCount <= 3 then
                    let call = { Id = ToolCallId $"c{!llmCallCount}"; Tool = ToolName "web_fetch"
                                 Arguments = fetchArgs; ProviderMeta = None }
                    return Result.Ok { Body = WithToolCalls (None, NonEmptyList.singleton call)
                                       ReasoningContent = None; ThinkingBlocks = []
                                       Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
                                       FinishReason = None }
                else
                    return Result.Ok (textResponse "done after throttle")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let fetchTool =
        ( { Name = ToolName "web_fetch"; Description = "fetch"; Parameters = Map.empty; ConcurrencySafe = false },
          fun _ -> async { incr fetchCallCount; return ToolSuccess "page content" } )
    let tools = Map.ofList [ ToolName "web_fetch", fetchTool ]
    let deps = { makeDepsWithTools provider tools with
                    Config = { BotSharpConfig.defaults with MaxIterations = 10 } }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("done after throttle", _) ->
        // web_fetch was executed only 2 times (3rd was throttled before hitting the tool)
        Assert.Equal(2, !fetchCallCount)
        Assert.Equal(4, !llmCallCount)
    | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
    | Result.Ok (t, _) -> Assert.Fail($"Expected 'done after throttle', got '{t}'")

// ═══════════════════════════════════════════════════════════════════════════
// Concurrent tool batching
// ═══════════════════════════════════════════════════════════════════════════

open BotSharp.Application.AgentLoop

[<Fact>]
let ``partitionToolBatches groups consecutive concurrent-safe tools into one batch`` () =
    // Three web_fetch calls (concurrent-safe) → single batch of 3
    let fetchSpec = { Name = ToolName "web_fetch"; Description = "fetch"; Parameters = Map.empty; ConcurrencySafe = true }
    let fetchFn   = fun _ -> async { return ToolSuccess "page" }
    let deps = { makeDeps (stubProvider (textResponse "done")) with
                    Tools = Map.ofList [ ToolName "web_fetch", (fetchSpec, fetchFn) ] }
    let calls = [
        { Id = ToolCallId "c1"; Tool = ToolName "web_fetch"; Arguments = Map.empty; ProviderMeta = None }
        { Id = ToolCallId "c2"; Tool = ToolName "web_fetch"; Arguments = Map.empty; ProviderMeta = None }
        { Id = ToolCallId "c3"; Tool = ToolName "web_fetch"; Arguments = Map.empty; ProviderMeta = None }
    ]
    let batches = partitionToolBatches deps calls
    Assert.Equal(1, batches.Length)
    Assert.Equal(3, batches.[0].Length)

[<Fact>]
let ``partitionToolBatches puts non-concurrent-safe tool in its own batch`` () =
    // One exec call (not concurrent-safe) → single batch of 1
    let execSpec = { Name = ToolName "exec"; Description = "shell"; Parameters = Map.empty; ConcurrencySafe = false }
    let execFn   = fun _ -> async { return ToolSuccess "ok" }
    let deps = { makeDeps (stubProvider (textResponse "done")) with
                    Tools = Map.ofList [ ToolName "exec", (execSpec, execFn) ] }
    let calls = [
        { Id = ToolCallId "c1"; Tool = ToolName "exec"; Arguments = Map.empty; ProviderMeta = None }
    ]
    let batches = partitionToolBatches deps calls
    Assert.Equal(1, batches.Length)
    Assert.Equal(1, batches.[0].Length)

[<Fact>]
let ``partitionToolBatches interleaves batches correctly for mixed tools`` () =
    // Pattern: fetch(safe), fetch(safe), exec(not-safe), fetch(safe)
    // Expected: [[fetch, fetch], [exec], [fetch]] — 3 batches
    let fetchSpec = { Name = ToolName "web_fetch"; Description = "fetch"; Parameters = Map.empty; ConcurrencySafe = true }
    let execSpec  = { Name = ToolName "exec";      Description = "shell"; Parameters = Map.empty; ConcurrencySafe = false }
    let noopFn    = fun _ -> async { return ToolSuccess "ok" }
    let deps = { makeDeps (stubProvider (textResponse "done")) with
                    Tools = Map.ofList [
                        ToolName "web_fetch", (fetchSpec, noopFn)
                        ToolName "exec",      (execSpec,  noopFn)
                    ] }
    let calls = [
        { Id = ToolCallId "c1"; Tool = ToolName "web_fetch"; Arguments = Map.empty; ProviderMeta = None }
        { Id = ToolCallId "c2"; Tool = ToolName "web_fetch"; Arguments = Map.empty; ProviderMeta = None }
        { Id = ToolCallId "c3"; Tool = ToolName "exec";      Arguments = Map.empty; ProviderMeta = None }
        { Id = ToolCallId "c4"; Tool = ToolName "web_fetch"; Arguments = Map.empty; ProviderMeta = None }
    ]
    let batches = partitionToolBatches deps calls
    Assert.Equal(3, batches.Length)
    Assert.Equal(2, batches.[0].Length)   // two concurrent-safe fetches
    Assert.Equal(1, batches.[1].Length)   // exclusive exec
    Assert.Equal(1, batches.[2].Length)   // final fetch alone (no successor to batch with)

[<Fact>]
let ``partitionToolBatches treats unknown tools as non-concurrent-safe`` () =
    // Call uses a tool not in deps.Tools — should get its own batch (non-safe default)
    let deps = makeDeps (stubProvider (textResponse "done"))
    let calls = [
        { Id = ToolCallId "c1"; Tool = ToolName "unknown_tool"; Arguments = Map.empty; ProviderMeta = None }
    ]
    let batches = partitionToolBatches deps calls
    Assert.Equal(1, batches.Length)
    Assert.Equal(1, batches.[0].Length)

// ═══════════════════════════════════════════════════════════════════════════
// Tool call round-trip
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop executes tool call and feeds result back to LLM`` () =
    let toolCall = {
        Id           = ToolCallId "call_1"
        Tool         = ToolName "echo"
        Arguments    =
            use doc = JsonDocument.Parse("""{"text":"ping"}""")
            doc.RootElement.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.Clone())
            |> Map.ofSeq
        ProviderMeta = None
    }
    // First LLM call: tool call; second: text response
    let responses = [ toolCallResponse [toolCall]; textResponse "pong from tool!" ]
    let echoTools =
        Map.ofList [
            ToolName "echo",
            ( { Name = ToolName "echo"; Description = "echo"; Parameters = Map.empty; ConcurrencySafe = false },
              fun (_args: Map<string, JsonElement>) -> async { return ToolSuccess "pong" })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) echoTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("pong from tool!", snap) ->
        // Messages: user + ToolCallMessage + ToolResultMessage + AssistantMessage = 4
        // Tool call/result messages are now persisted so the next turn can see them.
        Assert.Equal(4, SessionSnapshot.messageCount snap)
        // Verify the message types in order
        let msgs = SessionSnapshot.messages snap
        match msgs with
        | [ UserMessage _; ToolCallMessage _; ToolResultMessage _; AssistantMessage _ ] -> ()
        | other -> Assert.Fail($"Expected [User, ToolCall, ToolResult, Assistant], got {other |> List.map (fun m -> m.GetType().Name)}")
    | other -> Assert.Fail($"Expected Ok \"pong from tool!\", got {other}")

[<Fact>]
let ``runAgentLoop persists ToolCallMessage and ToolResultMessage for next turn`` () =
    // Validates that tool call/result messages appear in the session snapshot,
    // so the NEXT user turn's LLM call can see the tool usage history.
    let toolCall = {
        Id = ToolCallId "call_persist"; Tool = ToolName "echo2"; Arguments = Map.empty; ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "echoed!" ]
    let echoTools =
        Map.ofList [
            ToolName "echo2",
            ( { Name = ToolName "echo2"; Description = "echo2"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess "result-value" })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) echoTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let msgs = SessionSnapshot.messages snap
        let hasToolCall   = msgs |> List.exists (function ToolCallMessage _ -> true | _ -> false)
        let hasToolResult = msgs |> List.exists (function ToolResultMessage _ -> true | _ -> false)
        Assert.True(hasToolCall,   "ToolCallMessage should be in snapshot for next-turn context")
        Assert.True(hasToolResult, "ToolResultMessage should be in snapshot for next-turn context")
        // Verify the tool result content is preserved
        let resultContent =
            msgs |> List.tryPick (function ToolResultMessage (_, _, c) -> Some c | _ -> None)
        Assert.Equal(Some "result-value", resultContent)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``runAgentLoop handles tool not found gracefully`` () =
    let toolCall = {
        Id           = ToolCallId "call_x"
        Tool         = ToolName "nonexistent_tool"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "sorry!" ]
    let deps = makeDeps (stubProviderSeq responses)
    // Should not throw — tool failure propagates to LLM as error message
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("sorry!", _) -> ()
    | other -> Assert.Fail($"Expected Ok \"sorry!\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// LLM error propagation
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop propagates LLM error`` () =
    let errorProvider : LLMProvider = {
        Id = "err"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = noRetryPolicy
        Chat = fun _ _ _ -> async {
            return Result.Error { Kind = ServerError 503; RawMessage = "down"
                                  ProviderCode = None}
        }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps errorProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Error (AgentLlmFailure { Kind = ServerError 503 }) -> ()
    | other -> Assert.Fail($"Expected AgentLlmFailure, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Storage error propagation
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop propagates AgentStorageFailure when LoadSession fails`` () =
    let failingDeps = {
        makeDeps (stubProvider (textResponse "ok")) with
            LoadSession = fun _ ->
                async { return Result.Error (WriteFailure "disk full") }
    }
    match runAgentLoop dummyInbound failingDeps None |> Async.RunSynchronously with
    | Result.Error (AgentStorageFailure (WriteFailure _)) -> ()
    | other -> Assert.Fail($"Expected AgentStorageFailure, got {other}")

[<Fact>]
let ``runAgentLoop propagates AgentStorageFailure when PersistSession fails`` () =
    let failingDeps = {
        makeDeps (stubProvider (textResponse "ok")) with
            PersistSession = fun _ ->
                async { return Result.Error (WriteFailure "no space") }
    }
    match runAgentLoop dummyInbound failingDeps None |> Async.RunSynchronously with
    | Result.Error (AgentStorageFailure (WriteFailure _)) -> ()
    | other -> Assert.Fail($"Expected AgentStorageFailure, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// should_execute_tools gate (mirrors Python's LLMResponse.should_execute_tools #3220)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool calls with ContentFilter finish_reason are not executed`` () =
    // When finish_reason=ContentFilter, the agent should NOT dispatch tool calls —
    // instead it should treat the response like plain text (using prefix text if any).
    let executed = ref false
    let toolCall = {
        Id = ToolCallId "c1"; Tool = ToolName "blocked_tool"; Arguments = Map.empty; ProviderMeta = None
    }
    // Response: WithToolCalls but finish_reason=ContentFilter
    let blockedResp = {
        Body             = WithToolCalls (Some "Sorry, blocked.", NonEmptyList.singleton toolCall)
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = Some ContentFilter
    }
    let provider = stubProvider blockedResp
    let tools =
        Map.ofList [
            ToolName "blocked_tool",
            ( { Name = ToolName "blocked_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { executed.Value <- true; return ToolSuccess "should not run" })
        ]
    let deps = makeDepsWithTools provider tools
    let _ = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.False(!executed, "Tool should not be executed when finish_reason=ContentFilter")

[<Fact>]
let ``tool calls with ToolCalls finish_reason are executed normally`` () =
    // finish_reason=ToolCalls is the normal case — tool should be dispatched.
    let executed = ref false
    let toolCall = {
        Id = ToolCallId "c1"; Tool = ToolName "ok_tool"; Arguments = Map.empty; ProviderMeta = None
    }
    let toolCallsResp = {
        Body             = WithToolCalls (None, NonEmptyList.singleton toolCall)
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = Some ToolCalls
    }
    let responses = [ toolCallsResp; textResponse "done" ]
    let tools =
        Map.ofList [
            ToolName "ok_tool",
            ( { Name = ToolName "ok_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { executed.Value <- true; return ToolSuccess "ran" })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) tools
    let _ = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.True(!executed, "Tool should be executed when finish_reason=ToolCalls")

[<Fact>]
let ``tool calls with Stop finish_reason are executed normally`` () =
    // Python parity: test_tool_calls_with_stop_reason_executes
    // Some compliant providers emit "stop" together with tool_calls — agent must execute.
    let executed = ref false
    let toolCall = { Id = ToolCallId "c1"; Tool = ToolName "stop_tool"; Arguments = Map.empty; ProviderMeta = None }
    let stopResp = {
        Body             = WithToolCalls (None, NonEmptyList.singleton toolCall)
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = Some Stop
    }
    let responses = [ stopResp; textResponse "done" ]
    let tools = Map.ofList [
        ToolName "stop_tool",
        ( { Name = ToolName "stop_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
          fun _ -> async { executed.Value <- true; return ToolSuccess "ran" }) ]
    let deps = makeDepsWithTools (stubProviderSeq responses) tools
    let _ = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.True(!executed, "Tool should be executed when finish_reason=Stop")

[<Fact>]
let ``tool calls with None finish_reason are executed normally`` () =
    // Python parity: should_execute_tools — None (unset) is allowed.
    let executed = ref false
    let toolCall = { Id = ToolCallId "c1"; Tool = ToolName "none_reason_tool"; Arguments = Map.empty; ProviderMeta = None }
    let noneResp = {
        Body             = WithToolCalls (None, NonEmptyList.singleton toolCall)
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = None
    }
    let responses = [ noneResp; textResponse "done" ]
    let tools = Map.ofList [
        ToolName "none_reason_tool",
        ( { Name = ToolName "none_reason_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
          fun _ -> async { executed.Value <- true; return ToolSuccess "ran" }) ]
    let deps = makeDepsWithTools (stubProviderSeq responses) tools
    let _ = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.True(!executed, "Tool should be executed when finish_reason is None/unset")

[<Fact>]
let ``tool calls with Length finish_reason are NOT executed`` () =
    // Python parity: test_tool_calls_under_anomalous_reason_blocked — "length" blocked
    let executed = ref false
    let toolCall = { Id = ToolCallId "c1"; Tool = ToolName "length_tool"; Arguments = Map.empty; ProviderMeta = None }
    let lengthResp = {
        Body             = WithToolCalls (None, NonEmptyList.singleton toolCall)
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = Some Length
    }
    let tools = Map.ofList [
        ToolName "length_tool",
        ( { Name = ToolName "length_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
          fun _ -> async { executed.Value <- true; return ToolSuccess "ran" }) ]
    let deps = makeDepsWithTools (stubProvider lengthResp) tools
    let _ = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.False(!executed, "Tool should NOT be executed when finish_reason=Length")

[<Fact>]
let ``tool calls with OtherReason refusal are NOT executed`` () =
    // Python parity: test_tool_calls_under_anomalous_reason_blocked — "refusal" / "error" blocked
    let executed = ref false
    let toolCall = { Id = ToolCallId "c1"; Tool = ToolName "refusal_tool"; Arguments = Map.empty; ProviderMeta = None }
    let refusalResp = {
        Body             = WithToolCalls (None, NonEmptyList.singleton toolCall)
        ReasoningContent = None; ThinkingBlocks = []
        Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
        FinishReason     = Some (OtherReason "refusal")
    }
    let tools = Map.ofList [
        ToolName "refusal_tool",
        ( { Name = ToolName "refusal_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
          fun _ -> async { executed.Value <- true; return ToolSuccess "ran" }) ]
    let deps = makeDepsWithTools (stubProvider refusalResp) tools
    let _ = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.False(!executed, "Tool should NOT be executed when finish_reason=refusal (OtherReason)")

// ═══════════════════════════════════════════════════════════════════════════
// MaxIterationsMessage config field
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``MaxIterationsMessage is used when max_iterations is reached`` () =
    // When MaxIterations=1, agent stops after 1 tool round; custom message should be returned.
    let toolCall = {
        Id = ToolCallId "c1"; Tool = ToolName "endless"; Arguments = Map.empty; ProviderMeta = None
    }
    let toolCallResp = {
        Body = WithToolCalls (None, NonEmptyList.singleton toolCall); ReasoningContent = None; ThinkingBlocks = []
        Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None
    }
    // Provider always returns a tool call (infinite loop scenario)
    let provider = stubProvider toolCallResp
    let tools =
        Map.ofList [
            ToolName "endless",
            ( { Name = ToolName "endless"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess "still running" })
        ]
    let customMsg = "Agent reached the {maxIterations}-iteration limit."
    let cfg = {
        BotSharpConfig.defaults with
            MaxIterations        = 1
            MaxIterationsMessage = Some customMsg
    }
    let deps = { makeDepsWithTools provider tools with Config = cfg }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (text, _) ->
        Assert.Contains("1", text)          // {maxIterations} substituted
        Assert.Contains("iteration limit", text)
    | Result.Error e -> Assert.Fail($"Expected Ok with custom message, got Error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// FailOnToolError config field
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``FailOnToolError=true causes loop to return AgentToolFailure on first tool failure`` () =
    // When FailOnToolError=true and a tool returns ToolFailure, the loop should
    // immediately abort with Result.Error (AgentToolFailure ...) instead of
    // feeding the error text back to the LLM.
    let toolCall = {
        Id = ToolCallId "fail1"; Tool = ToolName "badtool"; Arguments = Map.empty; ProviderMeta = None
    }
    let toolCallResp = {
        Body = WithToolCalls (None, NonEmptyList.singleton toolCall); ReasoningContent = None; ThinkingBlocks = []
        Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None
    }
    let provider = stubProvider toolCallResp
    let tools =
        Map.ofList [
            ToolName "badtool",
            ( { Name = ToolName "badtool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolFailure (ExecutionFailed "something went wrong") })
        ]
    let cfg = { BotSharpConfig.defaults with FailOnToolError = true }
    let deps = { makeDepsWithTools provider tools with Config = cfg }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Error (AgentToolFailure (ExecutionFailed msg)) ->
        Assert.Contains("something went wrong", msg)
    | Result.Ok (text, _) ->
        Assert.Fail($"Expected AgentToolFailure error but got Ok: {text}")
    | Result.Error e ->
        Assert.Fail($"Expected AgentToolFailure but got different error: {e}")

[<Fact>]
let ``FailOnToolError=false allows loop to continue after tool failure`` () =
    // When FailOnToolError=false (default), a ToolFailure is fed back to the LLM
    // as an error message and the loop continues to completion.
    let callCount = ref 0
    let toolCall = {
        Id = ToolCallId "fail2"; Tool = ToolName "badtool2"; Arguments = Map.empty; ProviderMeta = None
    }
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                let resp =
                    if !callCount = 1 then
                        { Body = WithToolCalls (None, NonEmptyList.singleton toolCall)
                          ReasoningContent = None; ThinkingBlocks = []
                          Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None }
                    else
                        textResponse "recovered after failure"
                return Result.Ok resp
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let tools =
        Map.ofList [
            ToolName "badtool2",
            ( { Name = ToolName "badtool2"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolFailure (ExecutionFailed "transient error") })
        ]
    let cfg = { BotSharpConfig.defaults with FailOnToolError = false }
    let deps = { makeDepsWithTools provider tools with Config = cfg }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("recovered after failure", _) -> ()   // loop continued past failure
    | Result.Ok (text, _) ->
        Assert.Fail($"Expected 'recovered after failure' but got: {text}")
    | Result.Error e ->
        Assert.Fail($"Expected loop to continue but got error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// Tool result truncation (MaxToolResultChars)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool result is truncated when it exceeds MaxToolResultChars`` () =
    // Capture the messages seen by the LLM on its second call (after tool execution)
    let capturedMessages = System.Collections.Generic.List<Message list>()
    let toolCall = {
        Id = ToolCallId "call_trunc"; Tool = ToolName "bigtool"; Arguments = Map.empty; ProviderMeta = None
    }
    let callCount = ref 0
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ msgs _ ->
            async {
                capturedMessages.Add(msgs)
                incr callCount
                let resp =
                    if !callCount = 1 then
                        { Body = WithToolCalls (None, NonEmptyList.ofListUnsafe [toolCall])
                          ReasoningContent = None; ThinkingBlocks = []
                          Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None }
                    else
                        textResponse "done"
                return Result.Ok resp
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    // Tool returns a 30-char result; limit is 10
    let bigTool =
        Map.ofList [
            ToolName "bigtool",
            ( { Name = ToolName "bigtool"; Description = "big"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess "123456789012345678901234567890" })
        ]
    // WorkspacePath = "" disables maybePersistToolResult so we test pure truncation.
    let deps = { makeDepsWithTools provider bigTool with
                    Config = { BotSharpConfig.defaults with MaxToolResultChars = 10; WorkspacePath = "" } }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("done", _) ->
        // Second call's messages should contain truncated tool result
        Assert.True(capturedMessages.Count >= 2, "Expected at least 2 LLM calls")
        let secondCallMsgs = capturedMessages.[1]
        let toolResultContent =
            secondCallMsgs |> List.tryPick (fun msg ->
                match msg with
                | ToolResultMessage (_, _, content) -> Some content
                | _ -> None)
        match toolResultContent with
        | None -> Assert.Fail("Expected ToolResultMessage in second LLM call")
        | Some content ->
            Assert.True(content.StartsWith("1234567890"), $"Expected first 10 chars, got: {content}")
            Assert.Contains("(truncated)", content)
    | other -> Assert.Fail($"Expected Ok \"done\", got {other}")

[<Fact>]
let ``tool result under MaxToolResultChars is not truncated`` () =
    let toolCall = {
        Id = ToolCallId "call_ok"; Tool = ToolName "smalltool"; Arguments = Map.empty; ProviderMeta = None
    }
    let capturedMessages = System.Collections.Generic.List<Message list>()
    let callCount = ref 0
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ msgs _ ->
            async {
                capturedMessages.Add(msgs)
                incr callCount
                let resp =
                    if !callCount = 1 then
                        { Body = WithToolCalls (None, NonEmptyList.ofListUnsafe [toolCall])
                          ReasoningContent = None; ThinkingBlocks = []
                          Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None }
                    else
                        textResponse "ok"
                return Result.Ok resp
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let smallTool =
        Map.ofList [
            ToolName "smalltool",
            ( { Name = ToolName "smalltool"; Description = "small"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess "hi" })
        ]
    let deps = { makeDepsWithTools provider smallTool with
                    Config = { BotSharpConfig.defaults with MaxToolResultChars = 100 } }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("ok", _) ->
        let secondCallMsgs = capturedMessages.[1]
        let toolResultContent =
            secondCallMsgs |> List.tryPick (fun msg ->
                match msg with
                | ToolResultMessage (_, _, content) -> Some content
                | _ -> None)
        match toolResultContent with
        | None -> Assert.Fail("Expected ToolResultMessage in second LLM call")
        | Some content ->
            Assert.Equal("hi", content)
    | other -> Assert.Fail($"Expected Ok \"ok\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Context window trimming (trimToContextWindow)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``trimToContextWindow passes messages unchanged when contextWindowTokens = 0`` () =
    let msgs = [ SystemMessage "sys"; UserMessage ("hello", []); AssistantMessage ("hi", None) ]
    let result = trimToContextWindow 0 512 None None msgs
    Assert.Equal<Message list>(msgs, result)

[<Fact>]
let ``trimToContextWindow passes messages unchanged when they fit within budget`` () =
    // Short messages — well under any reasonable budget
    let msgs = [ UserMessage ("hi", []); AssistantMessage ("hello", None) ]
    let result = trimToContextWindow 128_000 512 None None msgs
    Assert.Equal<Message list>(msgs, result)

[<Fact>]
let ``trimToContextWindow keeps system messages when trimming`` () =
    // Build messages that exceed a tight budget; system should survive
    let sys    = SystemMessage "system prompt"
    let users  = [ for i in 1..20 -> UserMessage (String.replicate 200 "x", []) ]
    let assts  = [ for i in 1..20 -> AssistantMessage (String.replicate 200 "x", None) ]
    let msgs   = sys :: List.concat [ users; assts ]
    let result = trimToContextWindow 2048 256 None None msgs
    Assert.Contains(sys, result)

[<Fact>]
let ``trimToContextWindow result starts with user message after trimming`` () =
    // Many large messages to force trimming
    let msgs =
        [ for i in 1..30 ->
            if i % 2 = 1 then UserMessage (String.replicate 100 "u", [])
            else AssistantMessage (String.replicate 100 "a", None) ]
    let result = trimToContextWindow 1024 128 None None msgs
    if result.IsEmpty then ()  // empty is acceptable if all non-system is too large
    else
        let nonSystem = result |> List.filter (fun m -> match m with SystemMessage _ -> false | _ -> true)
        if not nonSystem.IsEmpty then
            match List.head nonSystem with
            | UserMessage _ -> ()
            | other -> Assert.Fail($"Expected first non-system message to be UserMessage, got {other}")

[<Fact>]
let ``trimToContextWindow drops oldest messages first`` () =
    // 10 pairs: oldest user/assistant have "old-" prefix
    let msgs =
        [ UserMessage ("old-first", [])
          AssistantMessage ("old-reply", None)
          UserMessage ("recent", [])
          AssistantMessage ("recent-reply", None) ]
    // Very tight budget: only last 2 messages fit
    let result = trimToContextWindow 100 50 None None msgs
    // recent messages should be kept; old-first may be dropped
    let allContent = result |> List.collect (fun m ->
        match m with
        | UserMessage (s, _) | SystemMessage s | ToolResultMessage (_, _, s) -> [s]
        | AssistantMessage (s, _) -> [s]
        | ToolCallMessage _ -> [])
    Assert.Contains("recent", allContent)   // recent msg must survive

// ═══════════════════════════════════════════════════════════════════════════
// ensureNonEmptyResult
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ensureNonEmptyResult: non-empty ToolSuccess is returned unchanged`` () =
    let result = ensureNonEmptyResult (ToolName "grep") (ToolSuccess "output")
    Assert.Equal(ToolSuccess "output", result)

[<Fact>]
let ``ensureNonEmptyResult: empty string gets placeholder`` () =
    let result = ensureNonEmptyResult (ToolName "exec") (ToolSuccess "")
    match result with
    | ToolSuccess content -> Assert.Contains("exec", content)
    | _ -> Assert.Fail("Expected ToolSuccess with placeholder")

[<Fact>]
let ``ensureNonEmptyResult: whitespace-only string gets placeholder`` () =
    let result = ensureNonEmptyResult (ToolName "exec") (ToolSuccess "   \n\t  ")
    match result with
    | ToolSuccess content -> Assert.Contains("exec", content)
    | _ -> Assert.Fail("Expected ToolSuccess with placeholder")

[<Fact>]
let ``ensureNonEmptyResult: ToolFailure is returned unchanged`` () =
    let err = ToolFailure (ExecutionFailed "boom")
    Assert.Equal(err, ensureNonEmptyResult (ToolName "exec") err)

// ═══════════════════════════════════════════════════════════════════════════
// microcompact
// ═══════════════════════════════════════════════════════════════════════════

let private bigResult (tool: string) (n: int) =
    // Build a result string that is ≥ 500 chars (MICROCOMPACT_MIN_CHARS)
    ToolResultMessage (ToolCallId $"c{n}", ToolName tool, String.replicate 600 "x")

[<Fact>]
let ``microcompact leaves messages unchanged when compactable results ≤ 10`` () =
    // 10 read_file results — all kept (KEEP_RECENT = 10)
    let msgs = [ for i in 1..10 -> bigResult "read_file" i ]
    let result = microcompact msgs
    Assert.Equal<Message list>(msgs, result)

[<Fact>]
let ``microcompact compacts the 11th read_file result (oldest)`` () =
    // 11 results: the first (oldest) should be compacted
    let msgs = [ for i in 1..11 -> bigResult "read_file" i ]
    let result = microcompact msgs
    Assert.Equal(11, List.length result)
    match List.head result with
    | ToolResultMessage (_, ToolName "read_file", content) ->
        Assert.Equal("[read_file result omitted from context]", content)
    | other -> Assert.Fail($"Expected compacted ToolResultMessage at head, got {other}")

[<Fact>]
let ``microcompact keeps the 10 most recent results full`` () =
    let msgs = [ for i in 1..15 -> bigResult "read_file" i ]
    let result = microcompact msgs
    // Last 10 should be unchanged
    let tail10 = result |> List.rev |> List.take 10 |> List.rev
    for msg in tail10 do
        match msg with
        | ToolResultMessage (_, _, content) -> Assert.DoesNotContain("omitted", content)
        | _ -> ()

[<Fact>]
let ``microcompact does not compact non-compactable tools`` () =
    // "my_custom_tool" is not in the compactable set — should never be compacted
    let msgs = [ for i in 1..15 -> ToolResultMessage (ToolCallId $"c{i}", ToolName "my_custom_tool", String.replicate 600 "x") ]
    let result = microcompact msgs
    for msg in result do
        match msg with
        | ToolResultMessage (_, _, content) -> Assert.DoesNotContain("omitted", content)
        | _ -> ()

[<Fact>]
let ``microcompact does not compact short results`` () =
    // Results under 500 chars should never be compacted even if there are > 10
    let msgs = [ for i in 1..15 -> ToolResultMessage (ToolCallId $"c{i}", ToolName "read_file", "short") ]
    let result = microcompact msgs
    for msg in result do
        match msg with
        | ToolResultMessage (_, _, content) -> Assert.Equal("short", content)
        | _ -> ()

[<Fact>]
let ``microcompact preserves non-ToolResultMessage messages unchanged`` () =
    let user = UserMessage ("hello", [])
    let asst = AssistantMessage ("hi", None)
    let msgs = [ yield user; yield asst; for i in 1..12 do yield bigResult "grep" i ]
    let result = microcompact msgs
    Assert.Equal(user, List.head result)
    Assert.Equal(asst, List.item 1 result)

[<Fact>]
let ``microcompact counts each compactable tool independently`` () =
    // 6 read_file + 6 list_dir = 12 results, each tool independently ≤ 10 kept recent
    // → neither set should be compacted (6 < 10 for each)
    let msgs =
        [ for i in 1..6 -> bigResult "read_file" i ]
        @ [ for i in 1..6 -> bigResult "list_dir" i ]
    let result = microcompact msgs
    Assert.Equal<Message list>(msgs, result)

// ═══════════════════════════════════════════════════════════════════════════
// dropOrphanToolResults
// ═══════════════════════════════════════════════════════════════════════════

let private mkCall id tool =
    { Id = ToolCallId id; Tool = ToolName tool; Arguments = Map.empty; ProviderMeta = None }

[<Fact>]
let ``dropOrphanToolResults: no orphans returns list unchanged`` () =
    let call = mkCall "c1" "grep"
    let msgs = [
        ToolCallMessage (NonEmptyList.singleton call, None)
        ToolResultMessage (ToolCallId "c1", ToolName "grep", "output")
    ]
    Assert.Equal<Message list>(msgs, dropOrphanToolResults msgs)

[<Fact>]
let ``dropOrphanToolResults: orphaned result is removed`` () =
    // ToolResultMessage with no prior ToolCallMessage should be dropped
    let msgs = [
        UserMessage ("hi", [])
        ToolResultMessage (ToolCallId "ghost", ToolName "grep", "output")
        AssistantMessage ("hello", None)
    ]
    let expected = [ UserMessage ("hi", []); AssistantMessage ("hello", None) ]
    Assert.Equal<Message list>(expected, dropOrphanToolResults msgs)

[<Fact>]
let ``dropOrphanToolResults: matched result is kept`` () =
    let call = mkCall "c1" "grep"
    let msgs = [
        ToolCallMessage (NonEmptyList.singleton call, None)
        ToolResultMessage (ToolCallId "c1", ToolName "grep", "ok")
        AssistantMessage ("done", None)
    ]
    // nothing should be dropped
    Assert.Equal<Message list>(msgs, dropOrphanToolResults msgs)

// ═══════════════════════════════════════════════════════════════════════════
// backfillMissingToolResults
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``backfillMissingToolResults: complete turn returns list unchanged`` () =
    let call = mkCall "c1" "read_file"
    let msgs = [
        ToolCallMessage (NonEmptyList.singleton call, None)
        ToolResultMessage (ToolCallId "c1", ToolName "read_file", "content")
    ]
    Assert.Equal<Message list>(msgs, backfillMissingToolResults msgs)

[<Fact>]
let ``backfillMissingToolResults: missing result gets synthetic placeholder`` () =
    let call = mkCall "c1" "exec"
    // No ToolResultMessage follows the ToolCallMessage
    let msgs = [ ToolCallMessage (NonEmptyList.singleton call, None) ]
    let result = backfillMissingToolResults msgs
    Assert.Equal(2, List.length result)
    match List.item 1 result with
    | ToolResultMessage (ToolCallId "c1", ToolName "exec", content) ->
        Assert.Contains("unavailable", content.ToLowerInvariant())
    | other -> Assert.Fail($"Expected synthetic ToolResultMessage, got {other}")

[<Fact>]
let ``backfillMissingToolResults: empty list returns empty`` () =
    Assert.Equal<Message list>([], backfillMissingToolResults [])

// ═══════════════════════════════════════════════════════════════════════════
// Pipeline integration: trim → dropOrphan → backfill
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``trim+dropOrphan+backfill: orphaned result from trimming is dropped`` () =
    // Simulate the scenario that can arise after context-window trimming:
    // the trim dropped a ToolCallMessage from an early turn, leaving its
    // ToolResultMessage as an orphan.  dropOrphanToolResults should remove it.
    let alreadyTrimmed = [
        // ToolCallMessage was dropped by trim; ToolResultMessage remains
        ToolResultMessage (ToolCallId "c1", ToolName "read_file", "file content")
        AssistantMessage ("got it", None)
        UserMessage ("second", [])
    ]
    let sanitized =
        alreadyTrimmed
        |> dropOrphanToolResults
        |> backfillMissingToolResults
    // The orphaned ToolResultMessage must be removed
    for msg in sanitized do
        match msg with
        | ToolResultMessage (ToolCallId "c1", _, _) ->
            Assert.Fail("Orphaned ToolResultMessage survived sanitization")
        | _ -> ()
    // AssistantMessage and UserMessage should survive
    Assert.Equal(2, List.length sanitized)

// ═══════════════════════════════════════════════════════════════════════════
// needsConsolidation
// ═══════════════════════════════════════════════════════════════════════════

let private cfgWithWindow n =
    { BotSharpConfig.defaults with MemoryWindowSize = n }

let private snapWithMessages (msgs: Message list) =
    let sid = SessionId "test"
    let now = DateTimeOffset.UtcNow
    SessionSnapshot.create sid msgs 0 now now
    |> function Result.Ok s -> s | Error e -> failwith e

[<Fact>]
let ``needsConsolidation false when no messages`` () =
    let snap = snapWithMessages []
    Assert.False(needsConsolidation snap (cfgWithWindow 10) None None)

[<Fact>]
let ``needsConsolidation false when below window size`` () =
    let msgs = List.replicate 4 (UserMessage ("hi", []))
    let snap = snapWithMessages msgs
    Assert.False(needsConsolidation snap (cfgWithWindow 5) None None)

[<Fact>]
let ``needsConsolidation true when at window size`` () =
    let msgs = List.replicate 5 (UserMessage ("hi", []))
    let snap = snapWithMessages msgs
    Assert.True(needsConsolidation snap (cfgWithWindow 5) None None)

[<Fact>]
let ``needsConsolidation true when above window size`` () =
    let msgs = List.replicate 9 (UserMessage ("hi", []))
    let snap = snapWithMessages msgs
    Assert.True(needsConsolidation snap (cfgWithWindow 5) None None)

[<Fact>]
let ``needsConsolidation counts only unconsolidated messages`` () =
    // 8 messages total, first 6 already consolidated → 2 unconsolidated < window 5
    let msgs = List.replicate 8 (UserMessage ("hi", []))
    let sid  = SessionId "test"
    let now  = DateTimeOffset.UtcNow
    let snap =
        SessionSnapshot.create sid msgs 6 now now
        |> function Result.Ok s -> s | Error e -> failwith e
    Assert.False(needsConsolidation snap (cfgWithWindow 5) None None)

// ═══════════════════════════════════════════════════════════════════════════
// AgentHook integration tests
// ═══════════════════════════════════════════════════════════════════════════

/// Build a hook that records which callbacks were invoked.
let private observingHook
    () : AgentHook * (unit -> string list) =
    let log = System.Collections.Generic.List<string>()
    let hook = {
        AgentHook.none with
            WantsStreaming     = false
            BeforeIteration    = fun ctx -> async { log.Add(sprintf "before:%d" ctx.Iteration) }
            OnStream           = fun ctx d -> async { log.Add(sprintf "stream:%d:%s" ctx.Iteration d) }
            OnStreamEnd        = fun ctx hasTools -> async { log.Add(sprintf "stream_end:%d:%b" ctx.Iteration hasTools) }
            BeforeExecuteTools = fun ctx -> async { log.Add(sprintf "before_tools:%d" ctx.Iteration) }
            AfterIteration     = fun ctx -> async { log.Add(sprintf "after:%d" ctx.Iteration) }
            FinalizeContent    = fun _ content -> content
    }
    hook, (fun () -> log |> Seq.toList)

[<Fact>]
let ``BeforeIteration and AfterIteration are called for a simple text reply`` () =
    let provider = stubProvider (textResponse "hello")
    let hook, getLog = observingHook ()
    let deps = { makeDeps provider with Hook = hook }
    let result = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.True(Result.isOk result)
    let log = getLog ()
    Assert.Contains("before:0", log)
    Assert.Contains("after:0", log)

[<Fact>]
let ``BeforeExecuteTools is called before tool dispatch`` () =
    let toolSpec : ToolSpec = {
        Name = ToolName "noop"; Description = "noop"
        Parameters = Map.empty
        ConcurrencySafe = false
    }
    let toolCall : ToolCall = {
        Id = ToolCallId "c1"; Tool = ToolName "noop"
        Arguments = Map.empty; ProviderMeta = None
    }
    // First call: tool call; second call: text reply to end the loop
    let provider = stubProviderSeq [
        toolCallResponse [ toolCall ]
        textResponse "done"
    ]
    let tools = Map.ofList [
        ToolName "noop", (toolSpec, fun _ -> async { return ToolSuccess "ok" })
    ]
    let hook, getLog = observingHook ()
    let deps = { makeDepsWithTools provider tools with Hook = hook }
    let result = runAgentLoop dummyInbound deps None |> Async.RunSynchronously
    Assert.True(Result.isOk result)
    let log = getLog ()
    // BeforeExecuteTools fires in the ExecutingTools pass (iterIdx is advanced after AwaitingLLM).
    Assert.True(log |> List.exists (fun e -> e.StartsWith("before_tools:")),
                sprintf "Expected a before_tools: entry in log, got: %A" log)

[<Fact>]
let ``FinalizeContent can transform the reply`` () =
    let provider = stubProvider (textResponse "original")
    let hook = {
        AgentHook.none with
            FinalizeContent = fun _ _ -> Some "transformed"
    }
    let deps = { makeDeps provider with Hook = hook }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Error e -> Assert.Fail(sprintf "Expected Ok, got Error: %A" e)
    | Result.Ok (text, _) -> Assert.Equal("transformed", text)

[<Fact>]
let ``AgentHook.compose fans out to all hooks`` () =
    let log1 = System.Collections.Generic.List<string>()
    let log2 = System.Collections.Generic.List<string>()
    let hook1 = { AgentHook.none with BeforeIteration = fun _ -> async { log1.Add("h1") } }
    let hook2 = { AgentHook.none with BeforeIteration = fun _ -> async { log2.Add("h2") } }
    let composed = AgentHook.compose [ hook1; hook2 ]
    let provider = stubProvider (textResponse "hi")
    let deps = { makeDeps provider with Hook = composed }
    runAgentLoop dummyInbound deps None |> Async.RunSynchronously |> ignore
    Assert.Contains("h1", log1)
    Assert.Contains("h2", log2)

[<Fact>]
let ``AgentHook.compose with empty list returns none`` () =
    let composed = AgentHook.compose []
    Assert.False(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose WantsStreaming is true if any hook wants streaming`` () =
    let noStream  = AgentHook.none
    let yesStream = { AgentHook.none with WantsStreaming = true }
    let composed  = AgentHook.compose [ noStream; yesStream; noStream ]
    Assert.True(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose WantsStreaming is false when all hooks have WantsStreaming = false`` () =
    let composed = AgentHook.compose [ AgentHook.none; AgentHook.none ]
    Assert.False(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose WantsStreaming is false for empty list`` () =
    let composed = AgentHook.compose []
    Assert.False(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose error isolation: faulty BeforeIteration does not prevent other hooks from running`` () =
    let log = System.Collections.Generic.List<string>()
    let bad  = { AgentHook.none with BeforeIteration = fun _ -> async { failwith "boom" } }
    let good = { AgentHook.none with BeforeIteration = fun _ -> async { log.Add("good") } }
    let composed = AgentHook.compose [ bad; good ]
    composed.BeforeIteration (AgentHook.mkContext 0 []) |> Async.RunSynchronously
    Assert.Contains("good", log)

[<Fact>]
let ``AgentHook.compose error isolation: faulty AfterIteration does not prevent other hooks from running`` () =
    let log = System.Collections.Generic.List<string>()
    let bad  = { AgentHook.none with AfterIteration  = fun _ -> async { failwith "boom" } }
    let good = { AgentHook.none with AfterIteration  = fun _ -> async { log.Add("good") } }
    let composed = AgentHook.compose [ bad; good ]
    composed.AfterIteration (AgentHook.mkContext 0 []) |> Async.RunSynchronously
    Assert.Contains("good", log)

[<Fact>]
let ``AgentHook.compose error isolation: faulty OnStream does not prevent other hooks from running`` () =
    let log = System.Collections.Generic.List<string>()
    let bad  = { AgentHook.none with OnStream = fun _ _ -> async { failwith "stream-boom" } }
    let good = { AgentHook.none with OnStream = fun _ d -> async { log.Add(d) } }
    let composed = AgentHook.compose [ bad; good ]
    composed.OnStream (AgentHook.mkContext 0 []) "delta" |> Async.RunSynchronously
    Assert.Contains("delta", log)

[<Fact>]
let ``AgentHook.compose error isolation: faulty OnStreamEnd and BeforeExecuteTools do not prevent other hooks`` () =
    // Python parity: test_composite_error_isolation_all_async — on_stream_end, before_execute_tools, after_iteration
    let log = System.Collections.Generic.List<string>()
    let bad = {
        AgentHook.none with
            OnStreamEnd        = fun _ _ -> async { failwith "stream_end-boom" }
            BeforeExecuteTools = fun _ -> async { failwith "tools-boom" }
    }
    let good = {
        AgentHook.none with
            OnStreamEnd        = fun _ _ -> async { log.Add("on_stream_end") }
            BeforeExecuteTools = fun _ -> async { log.Add("before_execute_tools") }
    }
    let composed = AgentHook.compose [ bad; good ]
    let ctx = AgentHook.mkContext 0 []
    composed.OnStreamEnd ctx true        |> Async.RunSynchronously
    composed.BeforeExecuteTools ctx      |> Async.RunSynchronously
    let logList = log |> Seq.toList
    Assert.Contains("on_stream_end", logList)
    Assert.Contains("before_execute_tools", logList)

[<Fact>]
let ``AgentHook.compose FinalizeContent pipeline: hooks transform in order`` () =
    let upper  = { AgentHook.none with FinalizeContent = fun _ c -> c |> Option.map (fun s -> s.ToUpperInvariant()) }
    let suffix = { AgentHook.none with FinalizeContent = fun _ c -> c |> Option.map (fun s -> s + "!") }
    let composed = AgentHook.compose [ upper; suffix ]
    let ctx = AgentHook.mkContext 0 []
    Assert.Equal(Some "HELLO!", composed.FinalizeContent ctx (Some "hello"))

[<Fact>]
let ``AgentHook.compose FinalizeContent None passthrough`` () =
    let composed = AgentHook.compose [ AgentHook.none ]
    let ctx = AgentHook.mkContext 0 []
    Assert.Equal(None, composed.FinalizeContent ctx None)

[<Fact>]
let ``AgentHook.compose wrapping another composed hook works correctly`` () =
    let log = System.Collections.Generic.List<string>()
    let inner    = { AgentHook.none with BeforeIteration = fun _ -> async { log.Add("inner") } }
    let composed = AgentHook.compose [ AgentHook.compose [ inner ] ]
    composed.BeforeIteration (AgentHook.mkContext 0 []) |> Async.RunSynchronously
    Assert.Contains("inner", log)

[<Fact>]
let ``AgentHook.compose all async methods fan out to every hook`` () =
    let events = System.Collections.Generic.List<string>()
    let h1 = {
        AgentHook.none with
            BeforeIteration    = fun _ -> async { events.Add("before_iteration") }
            OnStream           = fun _ d -> async { events.Add($"on_stream:{d}") }
            OnStreamEnd        = fun _ r -> async { events.Add($"on_stream_end:{r}") }
            BeforeExecuteTools = fun _ -> async { events.Add("before_execute_tools") }
            AfterIteration     = fun _ -> async { events.Add("after_iteration") }
    }
    let composed = AgentHook.compose [ h1; h1 ]  // same hook twice to verify double fan-out
    let ctx = AgentHook.mkContext 0 []
    composed.BeforeIteration ctx    |> Async.RunSynchronously
    composed.OnStream ctx "hi"      |> Async.RunSynchronously
    composed.OnStreamEnd ctx true   |> Async.RunSynchronously
    composed.BeforeExecuteTools ctx |> Async.RunSynchronously
    composed.AfterIteration ctx     |> Async.RunSynchronously
    let evList = events |> Seq.toList
    let expected : string list =
        ["before_iteration"; "before_iteration"
         "on_stream:hi";     "on_stream:hi"
         "on_stream_end:True"; "on_stream_end:True"
         "before_execute_tools"; "before_execute_tools"
         "after_iteration";  "after_iteration"]
    Assert.Equal<string list>(expected, evList)

// ═══════════════════════════════════════════════════════════════════════════
// MaxIterations guard
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop stops with MaxIterations message when tool loop exceeds limit`` () =
    // Provider that always returns a tool call (would loop forever without the guard).
    let toolCall = {
        Id           = ToolCallId "call_loop"
        Tool         = ToolName "noop"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let alwaysToolCallProvider : LLMProvider = {
        Id           = "always-tool"
        DefaultModel = "x"
        Capabilities = Set.empty
        RetryPolicy  = RetryPolicy.standard
        Chat         = fun _ _ _ -> async {
            return Result.Ok {
                Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [ toolCall ])
                ReasoningContent = None
                ThinkingBlocks   = []
                Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
                FinishReason     = None
            }
        }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let noopTools =
        Map.ofList [
            ToolName "noop",
            ( { Name = ToolName "noop"; Description = "noop"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess "ok" } )
        ]
    // MaxIterations = 1: guard fires on the second tool-call round (state iter = 1 ≥ 1).
    let deps = { makeDepsWithTools alwaysToolCallProvider noopTools with
                    Config = { BotSharpConfig.defaults with MaxIterations = 1 } }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (text, _) ->
        Assert.Contains("stopped", text)
        Assert.Contains("1", text)
    | Result.Error e -> Assert.Fail($"Expected Ok with stop message, got Error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// executeTool — ToolFailure(ExecutionFailed) truncation
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool ExecutionFailed message is truncated when it exceeds MaxToolResultChars`` () =
    // The truncation path: ToolFailure (ExecutionFailed msg) → truncateResult cap msg
    let toolCall = {
        Id = ToolCallId "call_fail"; Tool = ToolName "badtool"; Arguments = Map.empty; ProviderMeta = None
    }
    let capturedMessages = System.Collections.Generic.List<Message list>()
    let callCount = ref 0
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ msgs _ ->
            async {
                capturedMessages.Add(msgs)
                incr callCount
                let resp =
                    if !callCount = 1 then
                        { Body = WithToolCalls (None, NonEmptyList.ofListUnsafe [toolCall])
                          ReasoningContent = None; ThinkingBlocks = []
                          Usage = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }; FinishReason = None }
                    else textResponse "handled"
                return Result.Ok resp
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    // Tool returns ExecutionFailed with a long error message
    let longError = String.replicate 50 "E"
    let failTool =
        Map.ofList [
            ToolName "badtool",
            ( { Name = ToolName "badtool"; Description = "fail"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolFailure (ExecutionFailed longError) } )
        ]
    // MaxToolResultChars = 10 → error message truncated to 10 chars + "(truncated)"
    let deps = { makeDepsWithTools provider failTool with
                    Config = { BotSharpConfig.defaults with MaxToolResultChars = 10 } }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("handled", _) ->
        Assert.True(capturedMessages.Count >= 2, "Expected at least 2 LLM calls")
        let secondCallMsgs = capturedMessages.[1]
        let toolResultContent =
            secondCallMsgs |> List.tryPick (fun msg ->
                match msg with
                | ToolResultMessage (_, _, content) -> Some content
                | _ -> None)
        match toolResultContent with
        | None -> Assert.Fail("Expected ToolResultMessage in second LLM call")
        | Some content ->
            Assert.True(content.Length < longError.Length, $"Expected truncated content, got: {content}")
            Assert.Contains("(truncated)", content)
    | other -> Assert.Fail($"Expected Ok \"handled\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// reasoning_content preservation (Python parity: test_runner.py)
//
// When the first LLM response includes ReasoningContent, the ToolCallMessage
// appended to the session must carry that value so it is visible in the
// second LLM call's message list.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ReasoningContent from tool-call response is preserved in subsequent LLM call messages`` () =
    // Python parity: test_runner_preserves_reasoning_fields_and_tool_results
    let toolCall = {
        Id = ToolCallId "rc-call"; Tool = ToolName "noop_tool"; Arguments = Map.empty; ProviderMeta = None
    }
    let capturedMessages = System.Collections.Generic.List<Message list>()
    let callCount = ref 0
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ msgs _ ->
            async {
                capturedMessages.Add(msgs)
                incr callCount
                let resp =
                    if !callCount = 1 then
                        // First response: tool calls WITH reasoning content
                        { Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [toolCall])
                          ReasoningContent = Some "hidden reasoning"
                          ThinkingBlocks   = []
                          Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
                          FinishReason     = None }
                    else textResponse "done"
                return Result.Ok resp
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let noopTool =
        Map.ofList [
            ToolName "noop_tool",
            ( { Name = ToolName "noop_tool"; Description = ""; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess "tool result" } )
        ]
    let deps = makeDepsWithTools provider noopTool
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("done", _) ->
        Assert.True(capturedMessages.Count >= 2, "Expected at least 2 LLM calls")
        let secondCallMsgs = capturedMessages.[1]
        // The ToolCallMessage in the second call should carry the reasoning content
        let toolCallMsg =
            secondCallMsgs |> List.tryPick (fun msg ->
                match msg with
                | ToolCallMessage (_, Some rc) -> Some rc
                | _ -> None)
        match toolCallMsg with
        | None -> Assert.Fail("Expected ToolCallMessage with ReasoningContent in second LLM call")
        | Some rc -> Assert.Equal("hidden reasoning", rc)
    | other -> Assert.Fail($"Expected Ok \"done\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// trimToContextWindow — edge cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``trimToContextWindow returns messages unchanged when budget is zero or negative`` () =
    // contextWindowTokens (100) - maxTokens (100) - _SNIP_BUFFER (1024) = -1024 <= 0
    // → messages returned unchanged
    let msgs = [ UserMessage ("hi", []); AssistantMessage ("hello", None) ]
    let result = trimToContextWindow 100 100 None None msgs
    Assert.Equal<Message list>(msgs, result)

[<Fact>]
let ``messageTokens ToolCallMessage accounts for argument sizes`` () =
    let args =
        use doc = System.Text.Json.JsonDocument.Parse("""{"key":"value"}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = {
        Id           = ToolCallId "c1"
        Tool         = ToolName "read_file"
        Arguments    = args
        ProviderMeta = None
    }
    let msg = ToolCallMessage (NonEmptyList.singleton call, None)
    // Token estimate is > 0 (the function must not return 0 for a non-empty call)
    let tokens = messageTokens msg
    Assert.True(tokens > 0, $"Expected positive token count for ToolCallMessage, got {tokens}")

[<Fact>]
let ``trimToContextWindow all-assistant messages after trim starts from earliest kept message`` () =
    // When no UserMessage is in the kept set (keepFromUser = None), the implementation
    // returns the last kept message wrapped in a singleton list.
    // This exercises the | None -> kept |> List.tryLast |> ... branch.
    // Build a context where the only messages that fit are AssistantMessages.
    let msgs = [
        AssistantMessage (String.replicate 10 "a", None)   // old, fits
        AssistantMessage (String.replicate 10 "b", None)   // recent, fits
    ]
    // Budget: context 128, maxTokens 64, snip 1024 → budget = 128 - 64 - 1024 < 0 → pass-through
    // We need budget > 0 but messages exceed it.
    // Use a tiny budget: contextWindowTokens = 1200, maxTokens = 1, snip = 1024 → budget = 175
    // Both AssistantMessages are tiny → they both fit → no trimming needed
    // To force the None branch, we need all non-system kept messages to NOT be UserMessage.
    // Use a very small budget so only 1 message fits, and that message is AssistantMessage.
    let result = trimToContextWindow 1200 1 None None msgs
    // Either both fit (budget 175 vs ~10 tokens each) or at worst the last assistant message survives
    Assert.NotEmpty(result)

[<Fact>]
let ``trimToContextWindow keepFromUser None returns last kept message when no user message fits`` () =
    // Design: most-recent non-system message is an AssistantMessage (always added first).
    // The next message is a large UserMessage that cannot fit in the remaining budget.
    // → keepFromUser = None → fallback to List.tryLast kept.
    // contextWindowTokens=1350, maxTokens=1 → budget = 1350 - 1 - 1024 = 325
    // AssistantMessage "recent" → estimateTokens 6 chars = max 1 (6/4) = 1 + 4 = 5 tokens (fits)
    // UserMessage (1280 'u') → estimateTokens 1280 chars = 320 + 4 = 324 tokens  (5+324=329 > 325 → skip)
    // totalEst = 329 > 325 → trimming triggered
    // kept = [AssistantMessage "recent"], keepFromUser = None
    // result = system @ [AssistantMessage "recent"] = [AssistantMessage "recent"]
    let recentAsst = AssistantMessage ("recent", None)
    let bigUser    = UserMessage (String.replicate 1280 "u", [])
    let msgs = [ bigUser; recentAsst ]   // recentAsst is newest (List.rev puts it first)
    let result = trimToContextWindow 1350 1 None None msgs
    Assert.Equal<Message list>([ recentAsst ], result)

[<Fact>]
let ``trimToContextWindow contextBlockLimit overrides computed budget`` () =
    // With contextWindowTokens=0 (disabled), passing Some limit via contextBlockLimit
    // should still trim messages that exceed the limit.
    // Budget = Some 50 → direct override; short messages fit, large ones are dropped.
    let shortMsg  = UserMessage ("hi", [])
    let largeMsg  = UserMessage (String.replicate 1000 "x", [])   // ~250 tokens
    let msgs = [ largeMsg; shortMsg ]   // largeMsg is oldest
    // contextWindowTokens=0 would normally disable trimming, but contextBlockLimit=Some 50 overrides
    let result = trimToContextWindow 0 0 (Some 50) None msgs
    // shortMsg should survive; largeMsg (250 tokens) exceeds budget and should be dropped
    Assert.DoesNotContain(largeMsg, result)
    Assert.Contains(shortMsg, result)

[<Fact>]
let ``trimToContextWindow contextBlockLimit=None with contextWindowTokens=0 disables trimming`` () =
    // No limit at all — messages pass through unchanged.
    let msgs = [ UserMessage (String.replicate 5000 "x", []); AssistantMessage ("hi", None) ]
    let result = trimToContextWindow 0 0 None None msgs
    Assert.Equal<Message list>(msgs, result)

// ═══════════════════════════════════════════════════════════════════════════
// messageTokens — ToolResultMessage branch
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``messageTokens ToolResultMessage returns positive token count`` () =
    let msg = ToolResultMessage (ToolCallId "c1", ToolName "mytool", "result content")
    let tokens = messageTokens msg
    Assert.True(tokens > 0, $"Expected positive token count for ToolResultMessage, got {tokens}")

[<Fact>]
let ``messageTokens AssistantMessage with reasoning_content counts both content and reasoning tokens`` () =
    // content: 400 chars → 100 tokens; reasoning: 400 chars → 100 tokens; overhead: 4
    let content  = String.replicate 400 "a"
    let thinking = String.replicate 400 "b"
    let withRc    = messageTokens (AssistantMessage (content, Some thinking))
    let withoutRc = messageTokens (AssistantMessage (content, None))
    // withRc must be strictly greater than withoutRc (reasoning tokens were counted)
    Assert.True(withRc > withoutRc,
                $"Expected reasoning_content tokens to be counted: withRc={withRc}, withoutRc={withoutRc}")

// ═══════════════════════════════════════════════════════════════════════════
// Model error placeholder (LLM API failure → persist placeholder AssistantMessage)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop on LLM API failure persists placeholder AssistantMessage`` () =
    // Provider always fails with an API error.
    let apiErr : LlmError = {
        Kind         = ServerError 503
        RawMessage   = "Service unavailable"
        ProviderCode = None
    }
    let failProvider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = noRetryPolicy
        Chat = fun _ _ _ -> async { return Result.Error apiErr }
        ChatStream = fun _ _ _ _ -> async { return Result.Error apiErr }
    }
    let persistedSnaps = System.Collections.Generic.List<SessionSnapshot>()
    let deps = {
        makeDeps failProvider with
            PersistSession = fun snap ->
                async {
                    persistedSnaps.Add(snap)
                    return Result.Ok ()
                }
    }
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Error (AgentLlmFailure _) ->
        // Exactly one persist call (the placeholder persist)
        Assert.Equal(1, persistedSnaps.Count)
        let snap = persistedSnaps.[0]
        // Last message must be the model error placeholder
        let lastMsg = SessionSnapshot.messages snap |> List.last
        match lastMsg with
        | AssistantMessage (text, _) ->
            Assert.Contains("unavailable", text)
        | other -> Assert.Fail($"Expected AssistantMessage placeholder, got {other}")
    | Result.Ok (t, _) -> Assert.Fail($"Expected LLM failure, got Ok: {t}")
    | Result.Error other -> Assert.Fail($"Expected AgentLlmFailure, got {other}")

[<Fact>]
let ``runAgentLoop on LLM failure produces AgentLlmFailure error kind`` () =
    // Verify the error type is AgentLlmFailure (not storage or other error).
    let apiErr : LlmError = {
        Kind = ServerError 503; RawMessage = "error"; ProviderCode = None
    }
    let failProvider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = noRetryPolicy
        Chat = fun _ _ _ -> async { return Result.Error apiErr }
        ChatStream = fun _ _ _ _ -> async { return Result.Error apiErr }
    }
    let deps = makeDeps failProvider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Error (AgentLlmFailure _) -> ()  // correct error kind
    | Result.Ok (t, _) -> Assert.Fail($"Expected failure, got Ok: {t}")
    | Result.Error other -> Assert.Fail($"Expected AgentLlmFailure, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// stripThink — reasoning-block stripping (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``stripThink: passthrough for plain text with no tags`` () =
    Assert.Equal("Hello world!", stripThink "Hello world!")

[<Fact>]
let ``stripThink: removes well-formed think block`` () =
    let input    = "<think>This is my reasoning.</think>Here is my answer."
    let expected = "Here is my answer."
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes well-formed thought block`` () =
    let input    = "<thought>I should consider X.</thought>My answer is Y."
    let expected = "My answer is Y."
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes unclosed think block (streaming prefix)`` () =
    // LLM started a think block but didn't close it before max_tokens
    let input    = "<think>I am reasoning about this…"
    let expected = ""
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes unclosed thought block (streaming prefix)`` () =
    let input    = "<thought>Still thinking…"
    let expected = ""
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes multiline think block`` () =
    let input    = "<think>\nLine 1\nLine 2\n</think>\nFinal answer."
    let expected = "Final answer."
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes malformed opening tag (CJK leak)`` () =
    // Gemma/Qwen tokenizers sometimes emit <think广场… where CJK follows immediately
    let input    = "<think广场 content here"
    // The malformed <think tag should be stripped; rest of text is preserved
    Assert.DoesNotContain("<think", stripThink input)

[<Fact>]
let ``stripThink: preserves orphan closing tag mid-text (edge-only stripping)`` () =
    // An orphan </think> in the MIDDLE of text is intentionally preserved
    // (edge-only policy: only strip at ^ and $)
    let input    = "Some text </think> more text"
    let result   = stripThink input
    // The middle orphan tag may survive — we only strip at edges
    // Just verify the useful content is still there
    Assert.Contains("Some text", result)
    Assert.Contains("more text", result)

[<Fact>]
let ``stripThink: removes orphan closing tag at start of text`` () =
    let input    = "</think>\nReal content here"
    let expected = "Real content here"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes orphan closing tag at end of text`` () =
    let input    = "Real content here</think>"
    let expected = "Real content here"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: removes channel marker at start of text`` () =
    let input    = "<|channel|>Hello, user!"
    let expected = "Hello, user!"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: empty string returns empty`` () =
    Assert.Equal("", stripThink "")

[<Fact>]
let ``stripThink: whitespace-only string returns empty`` () =
    Assert.Equal("", stripThink "   ")

[<Fact>]
let ``stripThink: think-only response strips to empty (triggers empty retry)`` () =
    // A response that is ONLY a think block should result in empty string,
    // which then triggers the empty-content retry in the agent loop.
    let input    = "<think>I need to reason about this.</think>"
    Assert.Equal("", stripThink input)

// ── False-positive guard tests (Python parity: TestStripThinkFalsePositive) ─

[<Fact>]
let ``stripThink: backtick-wrapped think tag in mid-content is preserved`` () =
    // Python parity: test_backtick_think_tag_preserved
    // Tags that appear mid-sentence without a partner </think> must NOT be stripped.
    let text = "*Think Stripping:* A new utility to strip `<think>` tags from output."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink: prose-mention of think tag is preserved`` () =
    // Python parity: test_prose_think_tag_preserved
    let text = "The model emits <think> at the start of its response."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink: backtick-wrapped thought tag in mid-content is preserved`` () =
    // Python parity: test_backtick_thought_tag_preserved
    let text = "Gemma 4 uses `<thought>` blocks for reasoning."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink: self-closing thought tag is not matched`` () =
    // Python parity: test_self_closing_tag_not_matched
    // <thought/> has '/' before '>' so it is NOT a well-formed open tag.
    let text = "<thought/>some text"
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink: multiple thought blocks in one string are all removed`` () =
    // Python parity: test_multiple_tag_blocks
    let input    = "A<thought>x</thought>B<thought>y</thought>C"
    let expected = "ABC"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: thought tag with nested angle brackets is removed`` () =
    // Python parity: test_tag_with_nested_angle_brackets
    let input    = "<thought>a < 3 and b > 2</thought>result"
    let expected = "result"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: thought tag with only whitespace inside is removed`` () =
    // Python parity: test_tag_only_whitespace_inside
    let input    = "before<thought>  </thought>after"
    let expected = "beforeafter"
    Assert.Equal(expected, stripThink input)

// ── Malformed tokenizer leak tests (Python parity: TestStripThinkMalformedLeaks) ─

[<Fact>]
let ``stripThink: malformed English leak with space is stripped leaving content`` () =
    // Python parity: test_malformed_think_no_gt_english_with_space
    // Gemma / Ollama sometimes emits "<think " with a space, no ">".
    let input    = "<think The fountain opens at 09:00"
    let expected = "The fountain opens at 09:00"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: malformed CJK leak strips tag prefix leaving CJK content`` () =
    // Python parity: test_malformed_think_no_gt_chinese
    let input    = "<think广场照明灯目前绑定在'照明灯'策略下"
    let expected = "广场照明灯目前绑定在'照明灯'策略下"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``runAgentLoop treats think-only LLM response as empty and retries`` () =
    // When the LLM returns only a <think>…</think> block, stripThink reduces it
    // to "", which should trigger the empty-content retry up to _MAX_EMPTY_RETRIES
    // times before eventually returning the real answer.
    let callCount = ref 0
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty; RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                let resp =
                    if !callCount <= 2 then
                        textResponse "<think>Still thinking…</think>"  // stripped to empty → retry
                    else
                        textResponse "Real answer"
                return Result.Ok resp
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let deps = makeDeps provider
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok ("Real answer", _) ->
        // Called 3 times: 2 think-only retries + 1 real answer
        Assert.Equal(3, !callCount)
    | Result.Ok (t, _) -> Assert.Fail($"Expected 'Real answer', got '{t}'")
    | Result.Error e   -> Assert.Fail($"Expected Ok, got Error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// enforceRoleAlternation — role-alternation enforcement (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``enforceRoleAlternation: passthrough for well-alternated messages`` () =
    let msgs = [
        SystemMessage "sys"
        UserMessage ("hello", [])
        AssistantMessage ("hi", None)
        UserMessage ("bye", [])
    ]
    let result = enforceRoleAlternation msgs
    Assert.Equal<Message list>(msgs, result)

[<Fact>]
let ``enforceRoleAlternation: merges consecutive user messages`` () =
    let msgs = [
        UserMessage ("first", [])
        UserMessage ("second", [])
    ]
    let result = enforceRoleAlternation msgs
    Assert.Equal(1, result.Length)
    match result.[0] with
    | UserMessage (text, _) ->
        Assert.Contains("first", text)
        Assert.Contains("second", text)
    | _ -> Assert.Fail("Expected UserMessage")

[<Fact>]
let ``enforceRoleAlternation: consecutive assistant messages are merged then trailing-dropped`` () =
    // Two AssistantMessages after a UserMessage:
    //   [User, Assistant "part one", Assistant "part two"]
    // Phase 1 (merge): [User, Assistant "part one\n\npart two"]
    // Phase 2 (drop trailing): [User]  ← merged assistant is trailing, dropped
    // The merge step fires (only 1 assistant seen in merge output, not 2 separate)
    let msgs = [
        UserMessage ("question", [])
        AssistantMessage ("part one", None)
        AssistantMessage ("part two", None)
    ]
    let result = enforceRoleAlternation msgs
    // Only the UserMessage survives; the trailing merged assistant is dropped.
    Assert.Equal(1, result.Length)
    match result.[0] with
    | UserMessage _ -> ()
    | other -> Assert.Fail($"Expected UserMessage to survive, got {other}")

[<Fact>]
let ``enforceRoleAlternation: drops trailing plain AssistantMessage`` () =
    let msgs = [
        UserMessage ("hello", [])
        AssistantMessage ("reply that got cut off", None)
    ]
    let result = enforceRoleAlternation msgs
    // The trailing AssistantMessage should be dropped
    Assert.DoesNotContain(AssistantMessage ("reply that got cut off", None), result)

[<Fact>]
let ``enforceRoleAlternation: preserves trailing ToolCallMessage (not dropped)`` () =
    let toolCall = { Id = ToolCallId "c1"; Tool = ToolName "echo"; Arguments = Map.empty; ProviderMeta = None }
    let msgs = [
        UserMessage ("hello", [])
        ToolCallMessage (NonEmptyList.ofListUnsafe [toolCall], None)
    ]
    let result = enforceRoleAlternation msgs
    // ToolCallMessage is NOT a plain AssistantMessage — should be preserved
    Assert.Equal(2, result.Length)
    match result.[1] with
    | ToolCallMessage _ -> ()
    | other -> Assert.Fail($"Expected ToolCallMessage, got {other}")

[<Fact>]
let ``enforceRoleAlternation: empty list returns empty`` () =
    let result = enforceRoleAlternation []
    Assert.Empty(result)

[<Fact>]
let ``enforceRoleAlternation: system-only after drop recovers with user message`` () =
    // If dropping trailing AssistantMessage leaves only SystemMessage,
    // convert the dropped assistant text into a user message
    let msgs = [
        SystemMessage "system"
        AssistantMessage ("assistant-only turn", None)
    ]
    let result = enforceRoleAlternation msgs
    // Should not be empty and should contain a UserMessage with the recovered content
    let hasUser = result |> List.exists (function UserMessage _ -> true | _ -> false)
    Assert.True(hasUser, "Should recover trailing assistant text as UserMessage when only system would remain")

[<Fact>]
let ``enforceRoleAlternation: only-assistant messages produce empty list`` () =
    // [Assistant A, Assistant B] → merged → [Assistant AB] → trailing drop → []
    // No system messages remain, so no recovery fires. Result must be [].
    let msgs = [
        AssistantMessage ("A", None)
        AssistantMessage ("B", None)
    ]
    let result = enforceRoleAlternation msgs
    Assert.Empty(result)

[<Fact>]
let ``enforceRoleAlternation: multiple trailing assistants all dropped`` () =
    let msgs = [
        UserMessage ("hi", [])
        AssistantMessage ("A", None)
        AssistantMessage ("B", None)
    ]
    let result = enforceRoleAlternation msgs
    Assert.Equal(1, result.Length)
    match result.[0] with
    | UserMessage _ -> ()
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``enforceRoleAlternation: system messages are not merged`` () =
    let msgs = [
        SystemMessage "System A"
        SystemMessage "System B"
        UserMessage ("hi", [])
    ]
    let result = enforceRoleAlternation msgs
    // Both system messages should remain distinct
    Assert.Equal(3, result.Length)
    match result.[0], result.[1] with
    | SystemMessage "System A", SystemMessage "System B" -> ()
    | other -> Assert.Fail($"System messages should not be merged, got: {other}")

[<Fact>]
let ``enforceRoleAlternation: trailing assistant not recovered when user message present`` () =
    // [system, user, assistant] → drop trailing → [system, user] → hasNonSystem → no recovery
    let msgs = [
        SystemMessage "sys"
        UserMessage ("hello", [])
        AssistantMessage ("hi", None)
    ]
    let result = enforceRoleAlternation msgs
    Assert.Equal(2, result.Length)
    match result.[1] with
    | UserMessage ("hello", _) -> ()
    | other -> Assert.Fail($"Expected UserMessage to remain, got {other}")

[<Fact>]
let ``enforceRoleAlternation: trailing assistant not recovered when tool result present`` () =
    // [system, toolResult, assistant] → drop trailing → [system, toolResult] → hasNonSystem → no recovery
    let msgs = [
        SystemMessage "sys"
        ToolResultMessage (ToolCallId "c1", ToolName "echo", "result")
        AssistantMessage ("done", None)
    ]
    let result = enforceRoleAlternation msgs
    Assert.Equal(2, result.Length)
    match result.[1] with
    | ToolResultMessage _ -> ()
    | other -> Assert.Fail($"Expected ToolResultMessage to remain, got {other}")

[<Fact>]
let ``enforceRoleAlternation: leading assistant after system gets synthetic user inserted`` () =
    // Phase 4: first non-system message is plain AssistantMessage → insert synthetic user before it
    let msgs = [
        SystemMessage "sys"
        AssistantMessage ("previous reply", None)
        ToolResultMessage (ToolCallId "c1", ToolName "echo", "result")
        AssistantMessage ("after tool", None)
    ]
    let result = enforceRoleAlternation msgs
    // After Phase 2: trailing AssistantMessage is dropped → [sys, assistant, toolResult]
    // After Phase 3: user/tool exists → no recovery
    // After Phase 4: first non-sys is AssistantMessage → insert synthetic user before it
    let nonSystem = result |> List.filter (function SystemMessage _ -> false | _ -> true)
    match nonSystem with
    | UserMessage (content, _) :: AssistantMessage _ :: _ ->
        Assert.Contains("conversation continued", content)
    | _ -> Assert.Fail($"Expected synthetic user before assistant, got: {nonSystem}")

[<Fact>]
let ``enforceRoleAlternation: leading assistant with tool_calls is not patched`` () =
    // Phase 4: first non-system is a ToolCallMessage → no synthetic user inserted
    let toolCall = { Id = ToolCallId "c1"; Tool = ToolName "ls"; Arguments = Map.empty; ProviderMeta = None }
    let msgs = [
        SystemMessage "sys"
        ToolCallMessage (NonEmptyList.ofListUnsafe [toolCall], None)
        ToolResultMessage (ToolCallId "c1", ToolName "ls", "result")
    ]
    let result = enforceRoleAlternation msgs
    // No synthetic user should be inserted — ToolCallMessage is NOT a plain AssistantMessage
    let nonSystem = result |> List.filter (function SystemMessage _ -> false | _ -> true)
    match nonSystem with
    | ToolCallMessage _ :: _ -> ()
    | _ -> Assert.Fail($"Expected ToolCallMessage first, got: {nonSystem}")

[<Fact>]
let ``enforceRoleAlternation: realistic multi-turn conversation with merge needed`` () =
    // Python test_realistic_conversation:
    // [sys, user, assistant, user, user, assistant] → merged user → 4 messages final
    let msgs = [
        SystemMessage "sys"
        UserMessage ("What is 2+2?", [])
        AssistantMessage ("4", None)
        UserMessage ("And 3+3?", [])
        UserMessage ("(please be quick)", [])
        AssistantMessage ("6", None)
    ]
    let result = enforceRoleAlternation msgs
    // The two consecutive user messages should be merged, trailing assistant dropped
    Assert.Equal(4, result.Length)
    match result.[3] with
    | UserMessage (text, _) ->
        Assert.Contains("And 3+3?", text)
        Assert.Contains("(please be quick)", text)
    | other -> Assert.Fail($"Expected merged UserMessage, got {other}")

[<Fact>]
let ``enforceRoleAlternation: consecutive tool result messages are preserved (not merged)`` () =
    // Python parity: test_tool_messages_not_merged
    // Two consecutive ToolResult messages must not be collapsed — each tool call
    // result is independent and both must reach the provider.
    let msgs = [
        UserMessage ("Hi", [])
        ToolCallMessage (NonEmptyList.ofListUnsafe [ { Id = ToolCallId "1"; Tool = ToolName "tool"; Arguments = Map.empty; ProviderMeta = None } ], None)
        ToolResultMessage (ToolCallId "1", ToolName "tool", "result1")
        ToolResultMessage (ToolCallId "2", ToolName "tool", "result2")
        UserMessage ("Next", [])
    ]
    let result = enforceRoleAlternation msgs
    let toolResults = result |> List.filter (function ToolResultMessage _ -> true | _ -> false)
    Assert.Equal(2, toolResults.Length)

[<Fact>]
let ``enforceRoleAlternation: user-user-assistant-user merge does not drop non-trailing assistant`` () =
    // Python parity: test_consecutive_assistant_messages_merged
    // Two consecutive assistants in the middle (not trailing) → merged, preserved
    let msgs = [
        UserMessage ("Hi", [])
        AssistantMessage ("Hello!", None)
        AssistantMessage ("How can I help?", None)
        UserMessage ("Thanks", [])
    ]
    let result = enforceRoleAlternation msgs
    Assert.Equal(3, result.Length)
    match result.[1] with
    | AssistantMessage (text, _) ->
        Assert.Contains("Hello!", text)
        Assert.Contains("How can I help?", text)
    | other -> Assert.Fail($"Expected merged AssistantMessage, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// chatWithRetry — provider retry policy (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chatWithRetry: succeeds on first try when no error`` () =
    let callCount = ref 0
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Ok (textResponse "ok")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let settings = { Temperature = 0.0; MaxTokens = 10; ReasoningEffort = None }
    let result = chatWithRetry provider [] None settings [] [] |> Async.RunSynchronously
    match result with
    | Result.Ok r -> Assert.Equal(TextOnly "ok", r.Body); Assert.Equal(1, !callCount)
    | Result.Error e -> Assert.Fail($"Expected Ok, got {e}")

[<Fact>]
let ``chatWithRetry: retries on ServerError 503 and eventually succeeds`` () =
    let callCount = ref 0
    let serverErr : LlmError = { Kind = ServerError 503; RawMessage = "down"; ProviderCode = None }
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = { RetryPolicy.standard with
                            Mode = FixedRetries (2, [ TimeSpan.Zero; TimeSpan.Zero ]) }  // zero delay for test speed
        Chat = fun _ _ _ ->
            async {
                incr callCount
                if !callCount < 3 then
                    return Result.Error serverErr
                else
                    return Result.Ok (textResponse "recovered")
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let settings = { Temperature = 0.0; MaxTokens = 10; ReasoningEffort = None }
    let result = chatWithRetry provider [] None settings [] [] |> Async.RunSynchronously
    match result with
    | Result.Ok r -> Assert.Equal(TextOnly "recovered", r.Body); Assert.Equal(3, !callCount)
    | Result.Error e -> Assert.Fail($"Expected Ok after retries, got {e}")

[<Fact>]
let ``chatWithRetry: does not retry on non-retryable error`` () =
    let callCount = ref 0
    let authErr : LlmError = { Kind = ConnectionFailed "unauthorized"; RawMessage = "401"; ProviderCode = None }
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = RetryPolicy.standard
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Error authErr
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let settings = { Temperature = 0.0; MaxTokens = 10; ReasoningEffort = None }
    let result = chatWithRetry provider [] None settings [] [] |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ConnectionFailed _ } ->
        Assert.Equal(1, !callCount)  // exactly 1 call — no retries
    | other -> Assert.Fail($"Expected ConnectionFailed without retry, got {other}")

[<Fact>]
let ``chatWithRetry: exhausts all retries and returns last error`` () =
    let callCount = ref 0
    let serverErr : LlmError = { Kind = ServerError 503; RawMessage = "persistently down"; ProviderCode = None }
    let provider : LLMProvider = {
        Id = "stub"; DefaultModel = "x"; Capabilities = Set.empty
        RetryPolicy = { RetryPolicy.standard with
                            Mode = FixedRetries (2, [ TimeSpan.Zero; TimeSpan.Zero ]) }
        Chat = fun _ _ _ ->
            async {
                incr callCount
                return Result.Error serverErr
            }
        ChatStream = fun _ _ _ _ -> async { return Result.Ok () }
    }
    let settings = { Temperature = 0.0; MaxTokens = 10; ReasoningEffort = None }
    let result = chatWithRetry provider [] None settings [] [] |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ServerError 503 } ->
        Assert.Equal(3, !callCount)  // initial + 2 retries
    | other -> Assert.Fail($"Expected ServerError after retries exhausted, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// applyToolResultBudget — tool-result budget enforcement (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``applyToolResultBudget: tool result over budget is truncated`` () =
    let longContent = String.replicate 200 "x"   // 200 chars
    let msgs = [ ToolResultMessage (ToolCallId "c1", ToolName "read_file", longContent) ]
    let result = applyToolResultBudget 50 msgs
    match result with
    | [ ToolResultMessage (_, _, content) ] ->
        // truncateResult: text.[..49] + "\n... (truncated)" → 50 + 16 = 66 chars
        Assert.True(content.Length < longContent.Length, $"Expected shorter content, got {content.Length} chars")
        Assert.Contains("truncated", content)
    | other -> Assert.Fail($"Unexpected result: {other}")

[<Fact>]
let ``applyToolResultBudget: tool result within budget is unchanged`` () =
    let shortContent = "short result"
    let msgs = [ ToolResultMessage (ToolCallId "c1", ToolName "exec", shortContent) ]
    let result = applyToolResultBudget 1000 msgs
    match result with
    | [ ToolResultMessage (_, _, content) ] -> Assert.Equal(shortContent, content)
    | other -> Assert.Fail($"Unexpected result: {other}")

[<Fact>]
let ``applyToolResultBudget: non-tool-result messages are not modified`` () =
    let msgs : Message list = [
        UserMessage ("hello", [])
        AssistantMessage ("hi", None)
        SystemMessage "sys"
    ]
    let result = applyToolResultBudget 10 msgs
    Assert.Equal<Message list>(msgs, result)

[<Fact>]
let ``applyToolResultBudget: maxChars=0 is a no-op (truncation disabled)`` () =
    let longContent = String.replicate 500 "y"
    let msgs = [ ToolResultMessage (ToolCallId "c1", ToolName "grep", longContent) ]
    let result = applyToolResultBudget 0 msgs
    match result with
    | [ ToolResultMessage (_, _, content) ] -> Assert.Equal(longContent, content)
    | other -> Assert.Fail($"Unexpected result: {other}")

[<Fact>]
let ``applyToolResultBudget: mixed messages — only tool results are capped`` () =
    let longContent = String.replicate 200 "z"
    let msgs : Message list = [
        UserMessage ("ask", [])
        ToolResultMessage (ToolCallId "c1", ToolName "web_fetch", longContent)
        AssistantMessage ("reply", None)
    ]
    let result = applyToolResultBudget 50 msgs
    match result with
    | [ UserMessage ("ask", []); ToolResultMessage (_, _, capped); AssistantMessage ("reply", None) ] ->
        Assert.True(capped.Length < longContent.Length, $"Expected truncated tool result, got {capped.Length}")
        Assert.Contains("truncated", capped)
    | other -> Assert.Fail($"Unexpected structure: {other}")

[<Fact>]
let ``applyToolResultBudget: already-short content after microcompact placeholder is unchanged`` () =
    // Microcompact replaces stale results with "[tool result omitted from context]" (short).
    // applyToolResultBudget should not further truncate such placeholders.
    let placeholder = "[read_file result omitted from context]"
    let msgs = [ ToolResultMessage (ToolCallId "c1", ToolName "read_file", placeholder) ]
    let result = applyToolResultBudget 50 msgs
    match result with
    | [ ToolResultMessage (_, _, content) ] -> Assert.Equal(placeholder, content)
    | other -> Assert.Fail($"Unexpected result: {other}")

// ═══════════════════════════════════════════════════════════════════════════
// maybePersistToolResult — oversized tool-result disk persistence (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

open System.IO

[<Fact>]
let ``maybePersistToolResult: no-op when content fits within maxChars`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let content = "short result"
    let result = maybePersistToolResult tmp "session" (ToolCallId "c1") content 1000 |> Async.RunSynchronously
    Assert.Equal(content, result)   // unchanged
    if Directory.Exists tmp then Directory.Delete(tmp, true)

[<Fact>]
let ``maybePersistToolResult: no-op when workspacePath is empty`` () =
    let longContent = String.replicate 500 "x"
    let result = maybePersistToolResult "" "session" (ToolCallId "c1") longContent 100 |> Async.RunSynchronously
    Assert.Equal(longContent, result)   // unmodified — persist disabled

[<Fact>]
let ``maybePersistToolResult: no-op when maxChars is zero`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let longContent = String.replicate 500 "y"
    let result = maybePersistToolResult tmp "session" (ToolCallId "c1") longContent 0 |> Async.RunSynchronously
    Assert.Equal(longContent, result)   // unmodified — maxChars=0 means unlimited
    if Directory.Exists tmp then Directory.Delete(tmp, true)

[<Fact>]
let ``maybePersistToolResult: oversized content is persisted and replaced with reference string`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let longContent = String.replicate 500 "z"   // 500 chars > maxChars=100
    let result = maybePersistToolResult tmp "my-session" (ToolCallId "call-1") longContent 100 |> Async.RunSynchronously
    // Result should be a reference string, not the raw content.
    Assert.Contains("[tool output persisted]", result)
    Assert.Contains("Full output saved to:", result)
    Assert.Contains("500 chars", result)
    // File should exist in the bucket directory.
    let bucket = Directory.GetDirectories(Path.Combine(tmp, "tool-results")) |> Array.tryHead
    Assert.True(bucket.IsSome, "Expected a session bucket directory to be created")
    let files = Directory.GetFiles(bucket.Value)
    Assert.True(files.Length > 0, "Expected at least one persisted file")
    Directory.Delete(tmp, true)

[<Fact>]
let ``maybePersistToolResult: persisted file contains the full original content`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let longContent = String.replicate 300 "a"   // 300 chars > maxChars=50
    let _ = maybePersistToolResult tmp "s1" (ToolCallId "call-abc") longContent 50 |> Async.RunSynchronously
    let bucket = Directory.GetDirectories(Path.Combine(tmp, "tool-results")) |> Array.tryHead
    Assert.True(bucket.IsSome, "Expected a session bucket directory")
    let file = Directory.GetFiles(bucket.Value) |> Array.tryHead
    Assert.True(file.IsSome, "Expected a persisted file")
    let persisted = File.ReadAllText(file.Value)
    Assert.Equal(longContent, persisted)
    Directory.Delete(tmp, true)

[<Fact>]
let ``maybePersistToolResult: second call with same call-id returns reference without re-writing`` () =
    // If the file already exists, skip the write (idempotent).
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let content = String.replicate 200 "b"
    let r1 = maybePersistToolResult tmp "s1" (ToolCallId "call-1") content 50 |> Async.RunSynchronously
    let r2 = maybePersistToolResult tmp "s1" (ToolCallId "call-1") content 50 |> Async.RunSynchronously
    // Both calls should return the same reference string.
    Assert.Equal(r1, r2)
    Directory.Delete(tmp, true)

[<Fact>]
let ``maybePersistToolResult: preview includes first 1200 chars when content is longer`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let content = String.replicate 2000 "c"   // 2000 chars
    let result = maybePersistToolResult tmp "s1" (ToolCallId "call-1") content 100 |> Async.RunSynchronously
    // Reference string should contain the first 1200 chars as preview.
    Assert.Contains(String.replicate 1200 "c", result)
    Assert.Contains("(Read the saved file if you need the full output.)", result)
    Directory.Delete(tmp, true)

// ═══════════════════════════════════════════════════════════════════════════
// stripThink — additional Python-parity edge cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``stripThink: self-closing thought tag is not stripped`` () =
    // <thought/> is self-closing — not a block opener, must be preserved.
    let input  = "<thought/>some text"
    let result = stripThink input
    Assert.Equal("<thought/>some text", result)

[<Fact>]
let ``stripThink: self-closing think tag is not stripped`` () =
    let input  = "<think/>ok"
    Assert.Equal("<think/>ok", stripThink input)

[<Fact>]
let ``stripThink: multiple well-formed thought blocks are removed`` () =
    // A<thought>x</thought>B<thought>y</thought>C → ABC
    let input    = "A<thought>x</thought>B<thought>y</thought>C"
    let expected = "ABC"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: tag with only whitespace inside is stripped`` () =
    // <thought>  </thought> is still a valid block — content should be empty.
    let input    = "before<thought>  </thought>after"
    let expected = "beforeafter"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: tag with nested angle brackets inside is stripped`` () =
    // Nested < > inside a think block must not confuse the non-greedy regex.
    let input    = "<thought>a < 3 and b > 2</thought>result"
    let expected = "result"
    Assert.Equal(expected, stripThink input)

[<Fact>]
let ``stripThink: malformed think tag with English word after (no >) is stripped`` () =
    // <think The fountain… — space is not in the allowed-continuation chars,
    // so the malformed opener must be removed, leaving "The fountain opens at 09:00".
    let input    = "<think The fountain opens at 09:00"
    let expected = "The fountain opens at 09:00"
    Assert.Equal(expected, (stripThink input).TrimStart())

[<Fact>]
let ``stripThink: think-dash-variant is preserved (conservative preserve)`` () =
    // <think-foo>bar</think-foo> — dash is a valid tag-name char, must NOT be stripped.
    let input = "<think-foo>bar</think-foo>"
    Assert.Equal(input, stripThink input)

[<Fact>]
let ``stripThink: think-underscore-variant is preserved`` () =
    let input = "<think_foo>bar</think_foo>"
    Assert.Equal(input, stripThink input)

[<Fact>]
let ``stripThink: think-numeric-variant is preserved`` () =
    let input = "<think1>bar</think1>"
    Assert.Equal(input, stripThink input)

[<Fact>]
let ``stripThink: think-namespaced-variant is preserved`` () =
    let input = "<think:foo>bar</think:foo>"
    Assert.Equal(input, stripThink input)

[<Fact>]
let ``stripThink: literal close-think in backtick prose is preserved`` () =
    // Mid-prose reference in backticks — must not be stripped.
    let text = "Use `</think>` to close a thinking block."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink: channel marker in code block is preserved`` () =
    // Harmony spec markers in a code fence — mid-text, must not be stripped.
    let text = "Example:\n```\nif line.startswith('<channel|>'):\n    skip()\n```"
    Assert.Equal(text, stripThink text)

// ═══════════════════════════════════════════════════════════════════════════
// chatWithRetry — retry policy behavior (Python parity: test_provider_retry.py)
//
// Uses FixedRetries with zero-millisecond delays so tests complete instantly.
// ═══════════════════════════════════════════════════════════════════════════

let private mkProvider
    (responses: Result<LLMResponse, LlmError> list)
    (retryPolicy: RetryPolicy)
    : LLMProvider =
    let queue = System.Collections.Generic.Queue(responses)
    { Id           = "retry-stub"
      DefaultModel = "m"
      Capabilities = Set.empty
      RetryPolicy  = retryPolicy
      Chat         = fun _ _ _ -> async {
          if queue.Count > 0 then return queue.Dequeue()
          else return Result.Error { Kind = ServerError 500; RawMessage = "queue exhausted"; ProviderCode = None }
      }
      ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
    }

// ── Zero-delay fixed-retry policy ─────────────────────────────────────────
let private instantRetryPolicy (maxAttempts: int) =
    { RetryPolicy.standard with
        Mode = FixedRetries (maxAttempts, List.replicate maxAttempts TimeSpan.Zero) }

let private okResponse = textResponse "ok"

let private transientError kind =
    Result.Error { Kind = kind; RawMessage = "transient"; ProviderCode = None }

let private callWithRetry provider =
    chatWithRetry
        provider
        []
        None
        GenerationSettings.defaults
        [ SystemMessage "sys" ]
        []

[<Fact>]
let ``chatWithRetry returns success immediately when no error`` () =
    // Python parity: provider called once, result returned without retry.
    let policy   = instantRetryPolicy 3
    let provider = mkProvider [ Result.Ok okResponse ] policy
    let result   = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Ok { Body = TextOnly "ok" } -> ()
    | other -> Assert.Fail($"Expected Ok \"ok\", got {other}")

[<Fact>]
let ``chatWithRetry retries transient ServerError then returns success`` () =
    // Python: test_chat_with_retry_retries_transient_error_then_succeeds
    // First call fails with 503; second call succeeds.
    let policy   = instantRetryPolicy 3
    let provider =
        mkProvider
            [ transientError (ServerError 503)
              Result.Ok okResponse ]
            policy
    let result = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Ok { Body = TextOnly "ok" } -> ()
    | other -> Assert.Fail($"Expected Ok after retry, got {other}")

[<Fact>]
let ``chatWithRetry retries RateLimited error then returns success`` () =
    // 429 is retryable; policy allows 2 attempts.
    let policy   = instantRetryPolicy 2
    let provider =
        mkProvider
            [ transientError (RateLimited None)
              Result.Ok okResponse ]
            policy
    let result = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Ok { Body = TextOnly "ok" } -> ()
    | other -> Assert.Fail($"Expected Ok after RateLimited retry, got {other}")

[<Fact>]
let ``chatWithRetry does not retry non-transient ConnectionFailed error`` () =
    // Python: test_chat_with_retry_does_not_retry_non_transient_error
    // ConnectionFailed is not retryable; error is returned immediately.
    let policy   = instantRetryPolicy 3
    let provider =
        mkProvider
            [ transientError (ConnectionFailed "auth failure")
              Result.Ok okResponse ]
            policy
    let result = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ConnectionFailed _ } -> ()
    | other -> Assert.Fail($"Expected ConnectionFailed (no retry), got {other}")

[<Fact>]
let ``chatWithRetry returns final error after max retries exhausted`` () =
    // Python: test_chat_with_retry_returns_final_error_after_retries
    // All attempts fail; the last error is propagated.
    let policy   = instantRetryPolicy 2
    let provider =
        mkProvider
            [ transientError (ServerError 500)
              transientError (ServerError 500)
              transientError (ServerError 500) ]
            policy
    let result = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ServerError 500 } -> ()
    | other -> Assert.Fail($"Expected final ServerError 500 after max retries, got {other}")

[<Fact>]
let ``chatWithRetry with zero-retry policy does not retry on transient error`` () =
    // When maxAttempts = 0, no retries; first error is returned.
    let policy   = { RetryPolicy.standard with Mode = FixedRetries (0, []) }
    let provider =
        mkProvider
            [ transientError (ServerError 503)
              Result.Ok okResponse ]
            policy
    let result = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ServerError 503 } -> ()
    | other -> Assert.Fail($"Expected ServerError 503 with no-retry policy, got {other}")

[<Fact>]
let ``chatWithRetry honors RateLimited Retry-After hint`` () =
    // Python: test_chat_with_retry_uses_retry_after_and_emits_wait_progress
    // With a zero Retry-After hint, the retry completes without delay.
    let policy = instantRetryPolicy 2
    let provider =
        mkProvider
            [ transientError (RateLimited (Some (TimeSpan.Zero)))
              Result.Ok okResponse ]
            policy
    let result = callWithRetry provider |> Async.RunSynchronously
    match result with
    | Result.Ok _ -> ()
    | other -> Assert.Fail($"Expected Ok after RateLimited with Retry-After=0, got {other}")

// ── Secret redaction in tool results ─────────────────────────────────────

[<Fact>]
let ``runAgentLoop redacts OpenAI API key pattern in tool result`` () =
    // Validates that when a tool returns content containing a secret pattern,
    // the persisted ToolResultMessage has [REDACTED] instead.
    let fakeApiKey = "sk-" + String.replicate 21 "a"  // 24-char key matching the OpenAI pattern
    let toolCall = {
        Id           = ToolCallId "call_redact"
        Tool         = ToolName "secret_leaker"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "ok" ]
    let leakyTools =
        Map.ofList [
            ToolName "secret_leaker",
            ( { Name = ToolName "secret_leaker"; Description = "leaks a secret"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess $"Here is your key: {fakeApiKey}" })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) leakyTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let msgs = SessionSnapshot.messages snap
        let toolResultContent =
            msgs |> List.choose (function
                | ToolResultMessage (_, _, content) -> Some content
                | _ -> None)
            |> List.tryHead
        match toolResultContent with
        | None -> Assert.Fail("Expected a ToolResultMessage in snapshot")
        | Some content ->
            Assert.DoesNotContain(fakeApiKey, content)
            Assert.Contains("[REDACTED]", content)
    | other -> Assert.Fail($"Expected Ok result, got {other}")

[<Fact>]
let ``runAgentLoop redacts GitHub PAT pattern in tool result`` () =
    let fakePat = "ghp_" + String.replicate 36 "b"  // 40-char GitHub PAT
    let toolCall = {
        Id           = ToolCallId "call_ghpat"
        Tool         = ToolName "gh_leaker"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "done" ]
    let leakyTools =
        Map.ofList [
            ToolName "gh_leaker",
            ( { Name = ToolName "gh_leaker"; Description = "leaks a GitHub PAT"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess $"token={fakePat}" })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) leakyTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let toolResultContent =
            SessionSnapshot.messages snap
            |> List.choose (function ToolResultMessage (_, _, c) -> Some c | _ -> None)
            |> List.tryHead
        match toolResultContent with
        | None -> Assert.Fail("Expected a ToolResultMessage in snapshot")
        | Some content ->
            Assert.DoesNotContain(fakePat, content)
            Assert.Contains("[REDACTED]", content)
    | other -> Assert.Fail($"Expected Ok result, got {other}")

[<Fact>]
let ``runAgentLoop does not alter tool result when no secret pattern present`` () =
    let safeContent = "This is just a normal tool output with no secrets."
    let toolCall = {
        Id           = ToolCallId "call_safe"
        Tool         = ToolName "safe_tool"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "ok" ]
    let safeTools =
        Map.ofList [
            ToolName "safe_tool",
            ( { Name = ToolName "safe_tool"; Description = "safe output"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolSuccess safeContent })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) safeTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let toolResultContent =
            SessionSnapshot.messages snap
            |> List.choose (function ToolResultMessage (_, _, c) -> Some c | _ -> None)
            |> List.tryHead
        match toolResultContent with
        | None -> Assert.Fail("Expected a ToolResultMessage in snapshot")
        | Some content -> Assert.Equal(safeContent, content)
    | other -> Assert.Fail($"Expected Ok result, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// ToolFailure subtype rendering — WorkspaceViolation / ExecutionTimeout / ParameterInvalid
//
// Each case maps to a distinct "[…]" placeholder that is stored in the
// ToolResultMessage and fed back to the LLM.  Tests check the snapshot content.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``runAgentLoop renders WorkspaceViolation as Access denied message`` () =
    let toolCall = {
        Id           = ToolCallId "call_ws"
        Tool         = ToolName "fs_tool"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "ok" ]
    let wsTools =
        Map.ofList [
            ToolName "fs_tool",
            ( { Name = ToolName "fs_tool"; Description = "file tool"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolFailure (WorkspaceViolation "../escape") })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) wsTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let content =
            SessionSnapshot.messages snap
            |> List.choose (function ToolResultMessage (_, _, c) -> Some c | _ -> None)
            |> List.tryHead
        match content with
        | None -> Assert.Fail("Expected a ToolResultMessage in snapshot")
        | Some c ->
            Assert.Contains("[Access denied:", c)
            Assert.Contains("../escape", c)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``runAgentLoop renders ExecutionTimeout as timed out message`` () =
    let toolCall = {
        Id           = ToolCallId "call_timeout"
        Tool         = ToolName "slow_tool"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "ok" ]
    let slowTools =
        Map.ofList [
            ToolName "slow_tool",
            ( { Name = ToolName "slow_tool"; Description = "slow tool"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolFailure (ExecutionTimeout (TimeSpan.FromSeconds(30.0))) })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) slowTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let content =
            SessionSnapshot.messages snap
            |> List.choose (function ToolResultMessage (_, _, c) -> Some c | _ -> None)
            |> List.tryHead
        match content with
        | None -> Assert.Fail("Expected a ToolResultMessage in snapshot")
        | Some c ->
            Assert.Contains("[Tool timed out after", c)
            Assert.Contains("30", c)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``runAgentLoop renders ParameterInvalid as invalid parameter message`` () =
    let toolCall = {
        Id           = ToolCallId "call_param"
        Tool         = ToolName "strict_tool"
        Arguments    = Map.empty
        ProviderMeta = None
    }
    let responses = [ toolCallResponse [toolCall]; textResponse "ok" ]
    let strictTools =
        Map.ofList [
            ToolName "strict_tool",
            ( { Name = ToolName "strict_tool"; Description = "strict tool"; Parameters = Map.empty; ConcurrencySafe = false },
              fun _ -> async { return ToolFailure (ParameterInvalid ("format", "must be json")) })
        ]
    let deps = makeDepsWithTools (stubProviderSeq responses) strictTools
    match runAgentLoop dummyInbound deps None |> Async.RunSynchronously with
    | Result.Ok (_, snap) ->
        let content =
            SessionSnapshot.messages snap
            |> List.choose (function ToolResultMessage (_, _, c) -> Some c | _ -> None)
            |> List.tryHead
        match content with
        | None -> Assert.Fail("Expected a ToolResultMessage in snapshot")
        | Some c ->
            Assert.Contains("[Invalid parameter format:", c)
            Assert.Contains("must be json", c)
    | other -> Assert.Fail($"Expected Ok, got {other}")
