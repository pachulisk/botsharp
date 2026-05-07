module BotSharp.Tests.Domain.TypesTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.StateMachine

// ═══════════════════════════════════════════════════════════════════════════
// SessionSnapshot invariant tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``SessionSnapshot.empty has zero messages and lastConsolidated`` () =
    let s = SessionSnapshot.empty (SessionId "test") DateTimeOffset.UtcNow
    Assert.Equal(0, SessionSnapshot.messageCount s)
    Assert.Equal(0, SessionSnapshot.lastConsolidated s)

[<Fact>]
let ``SessionSnapshot.create succeeds when lastConsolidated is within bounds`` () =
    let msgs = [ AssistantMessage ("hello", None); AssistantMessage ("world", None) ]
    let result = SessionSnapshot.create (SessionId "s") msgs 1 DateTimeOffset.UtcNow DateTimeOffset.UtcNow
    Assert.True(Result.isOk result)

[<Fact>]
let ``SessionSnapshot.create fails when lastConsolidated exceeds message count`` () =
    let msgs = [ AssistantMessage ("hello", None) ]
    let result = SessionSnapshot.create (SessionId "s") msgs 5 DateTimeOffset.UtcNow DateTimeOffset.UtcNow
    Assert.True(Result.isError result)

[<Fact>]
let ``SessionSnapshot.create fails when lastConsolidated is negative`` () =
    let result = SessionSnapshot.create (SessionId "s") [] -1 DateTimeOffset.UtcNow DateTimeOffset.UtcNow
    Assert.True(Result.isError result)

[<Fact>]
let ``SessionSnapshot.append increases message count by 1`` () =
    let s  = SessionSnapshot.empty (SessionId "s") DateTimeOffset.UtcNow
    let s2 = SessionSnapshot.append (AssistantMessage ("hi", None)) s
    Assert.Equal(1, SessionSnapshot.messageCount s2)

[<Fact>]
let ``SessionSnapshot.append preserves immutability`` () =
    let s  = SessionSnapshot.empty (SessionId "s") DateTimeOffset.UtcNow
    let _  = SessionSnapshot.append (AssistantMessage ("hi", None)) s
    Assert.Equal(0, SessionSnapshot.messageCount s)  // original unchanged

[<Property>]
let ``SessionSnapshot.advanceConsolidated cannot go backwards`` (n: NonNegativeInt) =
    let msgs = List.replicate 20 (AssistantMessage ("x", None))
    let start = min n.Get 10
    match SessionSnapshot.create (SessionId "s") msgs start DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
    | Error _ -> true  // invalid start, skip
    | Ok s ->
        let lower = if start > 0 then start - 1 else 0
        let result = SessionSnapshot.advanceConsolidated lower s
        start = 0 || Result.isError result

[<Property>]
let ``SessionSnapshot.create is valid iff 0 ≤ lastConsolidated ≤ messageCount``
    (msgCount: PositiveInt) (lastConsolidated: int) =
    let msgs = List.replicate msgCount.Get (AssistantMessage ("x", None))
    let result = SessionSnapshot.create (SessionId "s") msgs lastConsolidated DateTimeOffset.UtcNow DateTimeOffset.UtcNow
    let expected = lastConsolidated >= 0 && lastConsolidated <= msgCount.Get
    expected = Result.isOk result

// ═══════════════════════════════════════════════════════════════════════════
// StateMachine transition tests
// ═══════════════════════════════════════════════════════════════════════════

let dummyInbound =
    { Channel = ChannelId "cli"
      Sender  = UserId "user"
      Chat    = ChatId "direct"
      Input   = ChatMessage ("hello", [])
      Metadata = Map.empty
      SessionKeyOverride = None }

let dummyRequest : LLMRequest =
    { Messages = []; Tools = []; Model = "gpt-4o-mini"; Settings = GenerationSettings.defaults }

[<Fact>]
let ``Idle + MessageReceived → BuildingPrompt`` () =
    let next = transition Idle (MessageReceived dummyInbound)
    match next with
    | BuildingPrompt _ -> ()
    | other -> Assert.Fail($"Expected BuildingPrompt, got {other}")

[<Fact>]
let ``BuildingPrompt + PromptBuilt → AwaitingLLM at iteration 0`` () =
    let next = transition (BuildingPrompt []) (PromptBuilt dummyRequest)
    match next with
    | AwaitingLLM (_, 0) -> ()
    | other -> Assert.Fail($"Expected AwaitingLLM(_, 0), got {other}")

[<Fact>]
let ``AwaitingLLM + LlmRespondedWithText → Finalizing`` () =
    let next = transition (AwaitingLLM (dummyRequest, 0)) (LlmRespondedWithText ("done", None))
    match next with
    | Finalizing ("done", None) -> ()
    | other -> Assert.Fail($"Expected Finalizing \"done\", got {other}")

[<Fact>]
let ``Finalizing + ResponseSent → Idle`` () =
    let next = transition (Finalizing ("ok", None)) ResponseSent
    match next with
    | Idle -> ()
    | other -> Assert.Fail($"Expected Idle, got {other}")

[<Fact>]
let ``Illegal transition Idle + ResponseSent returns Idle unchanged`` () =
    let next = transition Idle ResponseSent
    Assert.Equal(Idle, next)

[<Fact>]
let ``ExecutingTools + ToolsExecuted [] → Finalizing (empty results = stop signal)`` () =
    let dummyCall = { Id = ToolCallId "c1"; Tool = ToolName "dummy"; Arguments = Map.empty; ProviderMeta = None }
    let calls = NonEmptyList.singleton dummyCall
    let pending = []
    let state = ExecutingTools (calls, pending, 10)  // any iteration count
    let next = transition state (ToolsExecuted [])
    match next with
    | Finalizing (msg, _) -> Assert.Contains("stopped", msg)
    | other -> Assert.Fail($"Expected Finalizing, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// SessionId derivation
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``sessionId uses channel:chat when no override`` () =
    let msg = { dummyInbound with
                    Channel = ChannelId "telegram"
                    Chat    = ChatId "42" }
    let (SessionId sid) = sessionId msg
    Assert.Equal("telegram:42", sid)

[<Fact>]
let ``sessionId uses override when provided`` () =
    let msg = { dummyInbound with SessionKeyOverride = Some (SessionId "custom") }
    let (SessionId sid) = sessionId msg
    Assert.Equal("custom", sid)

// ═══════════════════════════════════════════════════════════════════════════
// ApiKey invariant
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ApiKey.create rejects empty string`` () =
    Assert.True(Result.isError (ApiKey.create ""))

[<Fact>]
let ``ApiKey.create rejects whitespace`` () =
    Assert.True(Result.isError (ApiKey.create "   "))

[<Fact>]
let ``ApiKey.create accepts non-empty string`` () =
    let r = ApiKey.create "sk-test123"
    Assert.True(Result.isOk r)
    match r with
    | Ok key -> Assert.Equal("sk-test123", ApiKey.value key)
    | Error _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// NonEmptyList module
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``NonEmptyList.singleton creates a list with one element`` () =
    let nel = NonEmptyList.singleton 42
    Assert.Equal(42, nel.Head)
    Assert.Empty(nel.Tail)

[<Fact>]
let ``NonEmptyList.create builds head + tail correctly`` () =
    let nel = NonEmptyList.create "a" ["b"; "c"]
    Assert.Equal("a", nel.Head)
    Assert.Equal<string list>(["b"; "c"], nel.Tail)

[<Fact>]
let ``NonEmptyList.toList returns head :: tail`` () =
    let nel = NonEmptyList.create 1 [2; 3]
    Assert.Equal<int list>([1; 2; 3], NonEmptyList.toList nel)

[<Fact>]
let ``NonEmptyList.length counts head + tail elements`` () =
    let nel = NonEmptyList.create "x" ["y"; "z"]
    Assert.Equal(3, NonEmptyList.length nel)

[<Fact>]
let ``NonEmptyList.length of singleton is 1`` () =
    Assert.Equal(1, NonEmptyList.length (NonEmptyList.singleton "only"))

[<Fact>]
let ``NonEmptyList.map applies function to all elements`` () =
    let nel    = NonEmptyList.create 1 [2; 3]
    let mapped = NonEmptyList.map ((*) 10) nel
    Assert.Equal(10, mapped.Head)
    Assert.Equal<int list>([20; 30], mapped.Tail)

[<Fact>]
let ``NonEmptyList.ofList Ok for non-empty list`` () =
    match NonEmptyList.ofList [1; 2; 3] with
    | Ok nel -> Assert.Equal(1, nel.Head)
    | Error _ -> Assert.Fail("Expected Ok for non-empty list")

[<Fact>]
let ``NonEmptyList.ofList Error for empty list`` () =
    match NonEmptyList.ofList ([] : int list) with
    | Ok _ -> Assert.Fail("Expected Error for empty list")
    | Error _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// LlmError.shouldRetry — retry decision table
// ═══════════════════════════════════════════════════════════════════════════

let private mkErr kind : LlmError = { Kind = kind; RawMessage = ""; ProviderCode = None }

[<Fact>]
let ``shouldRetry RateLimited returns true`` () =
    Assert.True(LlmError.shouldRetry (mkErr (RateLimited None)))

[<Fact>]
let ``shouldRetry ServerError returns true`` () =
    Assert.True(LlmError.shouldRetry (mkErr (ServerError 503)))

[<Fact>]
let ``shouldRetry Timeout returns true`` () =
    Assert.True(LlmError.shouldRetry (mkErr (Timeout StreamIdleTimeout)))

[<Fact>]
let ``shouldRetry MalformedResponse returns false`` () =
    Assert.False(LlmError.shouldRetry (mkErr (MalformedResponse (JsonParseError ("bad json", 0)))))

[<Fact>]
let ``shouldRetry ContextTooLong returns false`` () =
    Assert.False(LlmError.shouldRetry (mkErr ContextTooLong))

[<Fact>]
let ``shouldRetry QuotaExceeded returns false`` () =
    Assert.False(LlmError.shouldRetry (mkErr QuotaExceeded))

[<Fact>]
let ``shouldRetry ModelNotFound returns false`` () =
    Assert.False(LlmError.shouldRetry (mkErr (ModelNotFound "gpt-99")))

[<Fact>]
let ``shouldRetry EmptyResponse returns false`` () =
    // EmptyResponse indicates a misconfigured endpoint; retrying will not help.
    Assert.False(LlmError.shouldRetry (mkErr (EmptyResponse "hint text")))

// ═══════════════════════════════════════════════════════════════════════════
// AllowList.parse
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AllowList.parse with wildcard yields AnyoneAllowed`` () =
    match AllowList.parse ["*"] with
    | AnyoneAllowed -> ()
    | other -> Assert.Fail($"Expected AnyoneAllowed, got {other}")

[<Fact>]
let ``AllowList.parse wildcard among other values still yields AnyoneAllowed`` () =
    match AllowList.parse ["alice"; "*"; "bob"] with
    | AnyoneAllowed -> ()
    | other -> Assert.Fail($"Expected AnyoneAllowed when '*' present, got {other}")

[<Fact>]
let ``AllowList.parse specific IDs yields AllowedSet`` () =
    match AllowList.parse ["alice"; "bob"] with
    | AllowedSet s -> Assert.True(s.Contains "alice" && s.Contains "bob")
    | other -> Assert.Fail($"Expected AllowedSet, got {other}")

[<Fact>]
let ``AllowList.parse empty list yields empty AllowedSet`` () =
    match AllowList.parse [] with
    | AllowedSet s -> Assert.Empty(s)
    | other -> Assert.Fail($"Expected empty AllowedSet, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// LocalFilePath
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``LocalFilePath.create rejects empty path`` () =
    Assert.True(Result.isError (LocalFilePath.create ""))

[<Fact>]
let ``LocalFilePath.create rejects whitespace path`` () =
    Assert.True(Result.isError (LocalFilePath.create "   "))

[<Fact>]
let ``LocalFilePath.create rejects relative path`` () =
    Assert.True(Result.isError (LocalFilePath.create "relative/path.txt"))

[<Fact>]
let ``LocalFilePath.create accepts absolute path`` () =
    let r = LocalFilePath.create "/tmp/test.txt"
    Assert.True(Result.isOk r)

[<Fact>]
let ``LocalFilePath.value round-trips the path`` () =
    match LocalFilePath.create "/tmp/test.txt" with
    | Ok lp  -> Assert.Equal("/tmp/test.txt", LocalFilePath.value lp)
    | Error e -> Assert.Fail($"Expected Ok, got Error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// SessionSnapshot.unconsolidated and clear
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``SessionSnapshot.unconsolidated returns only messages after lastConsolidated`` () =
    let msgs = [ UserMessage ("q1", []); AssistantMessage ("a1", None); UserMessage ("q2", []) ]
    match SessionSnapshot.create (SessionId "s") msgs 2 DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
    | Error e -> Assert.Fail($"Unexpected Error: {e}")
    | Ok snap ->
        let unc = SessionSnapshot.unconsolidated snap
        Assert.Equal<Message list>([ UserMessage ("q2", []) ], unc)

[<Fact>]
let ``SessionSnapshot.unconsolidated is empty when all messages are consolidated`` () =
    let msgs = [ AssistantMessage ("hi", None) ]
    match SessionSnapshot.create (SessionId "s") msgs 1 DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
    | Error e -> Assert.Fail($"Unexpected Error: {e}")
    | Ok snap ->
        Assert.Empty(SessionSnapshot.unconsolidated snap)

[<Fact>]
let ``SessionSnapshot.clear resets messages to empty and lastConsolidated to 0`` () =
    let msgs = [ UserMessage ("q", []); AssistantMessage ("a", None) ]
    match SessionSnapshot.create (SessionId "s") msgs 1 DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
    | Error e -> Assert.Fail($"Unexpected Error: {e}")
    | Ok snap ->
        let cleared = SessionSnapshot.clear snap
        Assert.Equal(0, SessionSnapshot.messageCount cleared)
        Assert.Equal(0, SessionSnapshot.lastConsolidated cleared)

// ═══════════════════════════════════════════════════════════════════════════
// NonEmptyList.head function
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``NonEmptyList.head returns the first element`` () =
    let nel = NonEmptyList.create 42 [43; 44]
    Assert.Equal(42, NonEmptyList.head nel)

[<Fact>]
let ``NonEmptyList.head on singleton returns its only element`` () =
    let nel = NonEmptyList.singleton "only"
    Assert.Equal("only", NonEmptyList.head nel)

// ═══════════════════════════════════════════════════════════════════════════
// AllowList.permits
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AllowList.permits AnyoneAllowed returns true for any user`` () =
    Assert.True(AllowList.permits (UserId "alice") AnyoneAllowed)

[<Fact>]
let ``AllowList.permits AllowedSet returns true for listed user`` () =
    let list = AllowedSet (Set.ofList ["alice"; "bob"])
    Assert.True(AllowList.permits (UserId "alice") list)

[<Fact>]
let ``AllowList.permits AllowedSet returns false for unlisted user`` () =
    let list = AllowedSet (Set.ofList ["alice"])
    Assert.False(AllowList.permits (UserId "charlie") list)

[<Fact>]
let ``AllowList.permits empty AllowedSet returns false`` () =
    Assert.False(AllowList.permits (UserId "alice") (AllowedSet Set.empty))

// ═══════════════════════════════════════════════════════════════════════════
// MessageRef
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``MessageRef.create and value round-trip`` () =
    let ref_ = MessageRef.create "msg-123"
    Assert.Equal("msg-123", MessageRef.value ref_)

// ═══════════════════════════════════════════════════════════════════════════
// SessionSnapshot.createdAt / updatedAt / id accessors
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``SessionSnapshot.createdAt reflects the creation timestamp`` () =
    let now = DateTimeOffset.UtcNow
    let s = SessionSnapshot.empty (SessionId "s") now
    // createdAt should be exactly the time passed to empty
    Assert.Equal(now, SessionSnapshot.createdAt s)

[<Fact>]
let ``SessionSnapshot.id returns the session ID`` () =
    let sid = SessionId "test-session"
    let s = SessionSnapshot.empty sid DateTimeOffset.UtcNow
    Assert.Equal(sid, SessionSnapshot.id s)

[<Fact>]
let ``SessionSnapshot.messages returns the message list`` () =
    let s = SessionSnapshot.empty (SessionId "s") DateTimeOffset.UtcNow
    let s2 = SessionSnapshot.append (AssistantMessage ("hello", None)) s
    let msgs = SessionSnapshot.messages s2
    Assert.Equal<Message list>([ AssistantMessage ("hello", None) ], msgs)

// ═══════════════════════════════════════════════════════════════════════════
// expandPath
// ═══════════════════════════════════════════════════════════════════════════

open System.IO

[<Fact>]
let ``expandPath leaves absolute path unchanged`` () =
    let path = "/usr/local/bin/botsharp"
    Assert.Equal(path, expandPath path)

[<Fact>]
let ``expandPath expands ~ to the user home directory`` () =
    let result = expandPath "~/config.json"
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    Assert.True(result.StartsWith(home), $"Expected result to start with home dir '{home}', got '{result}'")

[<Fact>]
let ``expandPath tilde-only path does not crash`` () =
    // "~" without a following path component: p.[2..] = "" so result is just home dir
    let result = expandPath "~"
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    Assert.True(result.StartsWith(home) || result = home)

// ═══════════════════════════════════════════════════════════════════════════
// AgentHook.compose — fan-out, error isolation, pipeline, WantsStreaming
// Python parity: tests/agent/test_hook_composite.py
// ═══════════════════════════════════════════════════════════════════════════

/// Helper: build an AgentHookContext for tests.
let private hookCtx () = AgentHook.mkContext 0 []

[<Fact>]
let ``AgentHook.compose empty list returns the none hook`` () =
    // Python parity: test_composite_empty_hooks_no_ops
    let composed = AgentHook.compose []
    // Compose of [] returns none (WantsStreaming = false, all callbacks no-op)
    Assert.False(composed.WantsStreaming)
    // Should not throw
    composed.BeforeIteration (hookCtx ()) |> Async.RunSynchronously

[<Fact>]
let ``AgentHook.compose single hook returns that hook unchanged`` () =
    // F# fast path: | [one] -> one (no wrapping needed)
    let hook = { AgentHook.none with WantsStreaming = true }
    let composed = AgentHook.compose [hook]
    Assert.True(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose fans out BeforeIteration to all hooks in order`` () =
    // Python parity: test_composite_fans_out_before_iteration
    let calls = System.Collections.Generic.List<string>()
    let mkHook label = { AgentHook.none with
                            BeforeIteration = fun _ -> async { calls.Add(label) } }
    let composed = AgentHook.compose [ mkHook "A"; mkHook "B" ]
    composed.BeforeIteration (hookCtx ()) |> Async.RunSynchronously
    Assert.Equal<string list>(["A"; "B"], Seq.toList calls)

[<Fact>]
let ``AgentHook.compose fans out all async callbacks`` () =
    // Python parity: test_composite_fans_out_all_async_methods
    let events = System.Collections.Generic.List<string>()
    let mkHook label = { AgentHook.none with
                            BeforeIteration    = fun _ -> async { events.Add($"before:{label}") }
                            OnStream           = fun _ _ -> async { events.Add($"stream:{label}") }
                            OnStreamEnd        = fun _ _ -> async { events.Add($"end:{label}") }
                            BeforeExecuteTools = fun _ -> async { events.Add($"tools:{label}") }
                            AfterIteration     = fun _ -> async { events.Add($"after:{label}") } }
    let composed = AgentHook.compose [ mkHook "1"; mkHook "2" ]
    let ctx = hookCtx ()
    composed.BeforeIteration ctx |> Async.RunSynchronously
    composed.OnStream ctx "d" |> Async.RunSynchronously
    composed.OnStreamEnd ctx false |> Async.RunSynchronously
    composed.BeforeExecuteTools ctx |> Async.RunSynchronously
    composed.AfterIteration ctx |> Async.RunSynchronously
    Assert.Equal<string list>(
        [ "before:1"; "before:2"
          "stream:1"; "stream:2"
          "end:1"; "end:2"
          "tools:1"; "tools:2"
          "after:1"; "after:2" ],
        Seq.toList events)

[<Fact>]
let ``AgentHook.compose error isolation — faulty BeforeIteration does not block second hook`` () =
    // Python parity: test_composite_error_isolation_before_iteration
    let called = System.Collections.Generic.List<string>()
    let bad  = { AgentHook.none with BeforeIteration = fun _ -> async { failwith "boom" } }
    let good = { AgentHook.none with BeforeIteration = fun _ -> async { called.Add("good") } }
    let composed = AgentHook.compose [ bad; good ]
    // Should not throw; good hook still runs
    composed.BeforeIteration (hookCtx ()) |> Async.RunSynchronously
    Assert.Equal<string list>(["good"], Seq.toList called)

[<Fact>]
let ``AgentHook.compose error isolation — faulty OnStream does not block second hook`` () =
    // Python parity: test_composite_error_isolation_on_stream
    let deltas = System.Collections.Generic.List<string>()
    let bad  = { AgentHook.none with OnStream = fun _ _ -> async { failwith "stream-boom" } }
    let good = { AgentHook.none with OnStream = fun _ delta -> async { deltas.Add(delta) } }
    let composed = AgentHook.compose [ bad; good ]
    composed.OnStream (hookCtx ()) "hello" |> Async.RunSynchronously
    Assert.Equal<string list>(["hello"], Seq.toList deltas)

[<Fact>]
let ``AgentHook.compose error isolation — faulty hooks do not block remaining async callbacks`` () =
    // Python parity: test_composite_error_isolation_all_async
    let calls = System.Collections.Generic.List<string>()
    let bad = { AgentHook.none with
                    OnStreamEnd        = fun _ _ -> async { failwith "err" }
                    BeforeExecuteTools = fun _ -> async { failwith "err" }
                    AfterIteration     = fun _ -> async { failwith "err" } }
    let good = { AgentHook.none with
                     OnStreamEnd        = fun _ _ -> async { calls.Add("end") }
                     BeforeExecuteTools = fun _ -> async { calls.Add("tools") }
                     AfterIteration     = fun _ -> async { calls.Add("after") } }
    let composed = AgentHook.compose [ bad; good ]
    let ctx = hookCtx ()
    composed.OnStreamEnd ctx false |> Async.RunSynchronously
    composed.BeforeExecuteTools ctx |> Async.RunSynchronously
    composed.AfterIteration ctx |> Async.RunSynchronously
    Assert.Equal<string list>(["end"; "tools"; "after"], Seq.toList calls)

[<Fact>]
let ``AgentHook.compose FinalizeContent is a pipeline — each hook receives previous output`` () =
    // Python parity: test_composite_finalize_content_pipeline and test_composite_finalize_content_ordering
    let steps = System.Collections.Generic.List<string>()
    let upper = { AgentHook.none with
                      FinalizeContent = fun _ s ->
                          let v = Option.defaultValue "" s
                          steps.Add("upper:" + v)
                          s |> Option.map (fun x -> x.ToUpperInvariant()) }
    let suffix = { AgentHook.none with
                       FinalizeContent = fun _ s ->
                           let v = Option.defaultValue "" s
                           steps.Add("suffix:" + v)
                           s |> Option.map (fun x -> x + "!") }
    let composed = AgentHook.compose [ upper; suffix ]
    let result = composed.FinalizeContent (hookCtx ()) (Some "hello")
    Assert.Equal(Some "HELLO!", result)
    Assert.Equal<string list>(["upper:hello"; "suffix:HELLO"], Seq.toList steps)

[<Fact>]
let ``AgentHook.compose FinalizeContent None passthrough`` () =
    // Python parity: test_composite_finalize_content_none_passthrough
    let composed = AgentHook.compose [ AgentHook.none ]
    let result = composed.FinalizeContent (hookCtx ()) None
    Assert.Equal(None, result)

[<Fact>]
let ``AgentHook.compose WantsStreaming any-semantics — true when any hook wants streaming`` () =
    // Python parity: test_composite_wants_streaming_any_true
    let no  = AgentHook.none
    let yes = { AgentHook.none with WantsStreaming = true }
    let composed = AgentHook.compose [ no; yes; no ]
    Assert.True(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose WantsStreaming all false remains false`` () =
    // Python parity: test_composite_wants_streaming_all_false
    let composed = AgentHook.compose [ AgentHook.none; AgentHook.none ]
    Assert.False(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose WantsStreaming empty list returns false`` () =
    // Python parity: test_composite_wants_streaming_empty
    let composed = AgentHook.compose []
    Assert.False(composed.WantsStreaming)

[<Fact>]
let ``AgentHook.compose nesting — composite wrapping another composite fans out correctly`` () =
    // Python parity: test_composite_can_wrap_another_composite
    let calls = System.Collections.Generic.List<string>()
    let inner = { AgentHook.none with BeforeIteration = fun _ -> async { calls.Add("inner") } }
    let wrapped = AgentHook.compose [ AgentHook.compose [ inner ] ]
    wrapped.BeforeIteration (hookCtx ()) |> Async.RunSynchronously
    Assert.Equal<string list>(["inner"], Seq.toList calls)

// ═══════════════════════════════════════════════════════════════════════════
// TokenUsage.formatUsage — Python parity: test_build_status.py
// ═══════════════════════════════════════════════════════════════════════════

let private mkUsage p c cached = { PromptTokens = p; CompletionTokens = c; CachedTokens = cached }

[<Fact>]
let ``TokenUsage.formatUsage shows cache hit rate when cached tokens present`` () =
    // Python parity: test_status_shows_cache_hit_rate
    let u = mkUsage 2000 300 1200   // 1200/2000 = 60%
    let s = TokenUsage.formatUsage u
    Assert.Contains("60% cached", s)
    Assert.Contains("2000 in / 300 out", s)

[<Fact>]
let ``TokenUsage.formatUsage omits cache info when cached tokens are zero`` () =
    // Python parity: test_status_no_cache_info / test_status_zero_cached_tokens
    let u = mkUsage 2000 300 0
    let s = TokenUsage.formatUsage u
    Assert.Contains("2000 in / 300 out", s)
    Assert.DoesNotContain("cached", s)

[<Fact>]
let ``TokenUsage.formatUsage shows 100% when all tokens are cached`` () =
    // Python parity: test_status_100_percent_cached
    let u = mkUsage 1000 100 1000   // 1000/1000 = 100%
    let s = TokenUsage.formatUsage u
    Assert.Contains("100% cached", s)

[<Fact>]
let ``TokenUsage.formatUsage omits cache info when prompt tokens are zero`` () =
    // Guard against division by zero when prompt tokens not yet recorded
    let u = mkUsage 0 0 0
    let s = TokenUsage.formatUsage u
    Assert.DoesNotContain("cached", s)

// ── TokenTracker ─────────────────────────────────────────────────────────────

[<Fact>]
let ``TokenTracker.empty creates tracker with given context window`` () =
    let t = TokenTracker.empty 128_000
    Assert.Equal(128_000, t.ContextWindow)

[<Fact>]
let ``TokenTracker.empty has zero total usage`` () =
    let t = TokenTracker.empty 128_000
    Assert.Equal(0, t.TotalUsage.PromptTokens)
    Assert.Equal(0, t.TotalUsage.CompletionTokens)

[<Fact>]
let ``TokenTracker.empty has no LastUsage`` () =
    let t = TokenTracker.empty 128_000
    Assert.Equal(None, t.LastUsage)

[<Fact>]
let ``TokenTracker.currentUsageEstimate with no LastUsage returns EstimatedPending`` () =
    let t = { TokenTracker.empty 128_000 with EstimatedPending = 500 }
    Assert.Equal(500, TokenTracker.currentUsageEstimate t)

[<Fact>]
let ``TokenTracker.currentUsageEstimate with LastUsage returns prompt + completion + pending`` () =
    let usage = mkUsage 1000 200 0
    let t = { TokenTracker.empty 128_000 with LastUsage = Some usage; EstimatedPending = 300 }
    Assert.Equal(1500, TokenTracker.currentUsageEstimate t)

[<Fact>]
let ``TokenTracker.recordApiUsage updates LastUsage and resets EstimatedPending`` () =
    let t = { TokenTracker.empty 128_000 with EstimatedPending = 999 }
    let usage = mkUsage 1000 200 0
    let t2 = TokenTracker.recordApiUsage usage t
    Assert.Equal(Some usage, t2.LastUsage)
    Assert.Equal(0, t2.EstimatedPending)

[<Fact>]
let ``TokenTracker.recordApiUsage accumulates TotalUsage`` () =
    let t = TokenTracker.empty 128_000
    let u1 = mkUsage 500 100 0
    let u2 = mkUsage 300 50  0
    let t2 = TokenTracker.recordApiUsage u1 t |> TokenTracker.recordApiUsage u2
    Assert.Equal(800, t2.TotalUsage.PromptTokens)
    Assert.Equal(150, t2.TotalUsage.CompletionTokens)

[<Fact>]
let ``TokenTracker.addPendingEstimate accumulates pending tokens`` () =
    let t = TokenTracker.empty 128_000
    let t2 = t |> TokenTracker.addPendingEstimate 100 |> TokenTracker.addPendingEstimate 200
    Assert.Equal(300, t2.EstimatedPending)

[<Fact>]
let ``TokenTracker.contextRemainingPercent returns 100 when context window is 0`` () =
    let t = TokenTracker.empty 0
    Assert.Equal(100, TokenTracker.contextRemainingPercent t)

[<Fact>]
let ``TokenTracker.contextRemainingPercent returns 100 when no tokens used`` () =
    let t = TokenTracker.empty 128_000
    Assert.Equal(100, TokenTracker.contextRemainingPercent t)

[<Fact>]
let ``TokenTracker.contextRemainingPercent decreases as usage grows`` () =
    let t = TokenTracker.empty 32_000
    let usage = mkUsage 20_000 0 0
    let t2 = TokenTracker.recordApiUsage usage t
    let pct = TokenTracker.contextRemainingPercent t2
    Assert.True(pct < 100, sprintf "Expected <100%% remaining, got %d%%" pct)

[<Fact>]
let ``TokenTracker.shouldCompactByTokens returns false when context window is 0`` () =
    let t = TokenTracker.empty 0
    Assert.False(TokenTracker.shouldCompactByTokens t)

[<Fact>]
let ``TokenTracker.shouldCompactByTokens returns false when usage is below 80 percent`` () =
    let t = TokenTracker.empty 128_000
    // 60% of context window (well below 80% threshold)
    let usage = mkUsage (128_000 * 60 / 100) 0 0
    let t2 = TokenTracker.recordApiUsage usage t
    Assert.False(TokenTracker.shouldCompactByTokens t2)

[<Fact>]
let ``TokenTracker.shouldCompactByTokens returns true when usage meets 80 percent`` () =
    let window = 128_000
    let t = TokenTracker.empty window
    // 81% usage: 81 * 128000 / 100 = 103680
    let usage = mkUsage (window * 81 / 100) 0 0
    let t2 = TokenTracker.recordApiUsage usage t
    Assert.True(TokenTracker.shouldCompactByTokens t2)
