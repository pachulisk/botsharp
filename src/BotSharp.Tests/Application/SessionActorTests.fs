module BotSharp.Tests.Application.SessionActorTests

open System
open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Stub helpers (mirrors AgentLoopTests pattern)
// ═══════════════════════════════════════════════════════════════════════════

let private stubProvider (text: string) : LLMProvider = {
    Id           = "stub"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async {
        return Result.Ok {
            Body             = TextOnly text
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
            FinishReason     = None
        }
    }
    // ChatStream emits a ContentDelta so Body = TextOnly text (non-empty); an empty
    // stream would now surface as EmptyResponse error rather than a silent empty string.
    ChatStream   = fun _ _ _ emitter -> async {
        do! emitter (ContentDelta (TextDelta text))
        return Result.Ok ()
    }
}

let private errorProvider : LLMProvider = {
    Id           = "error"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async {
        return Result.Error { Kind = ServerError 503; RawMessage = "simulated provider error"; ProviderCode = None }
    }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

let private makeDeps (provider: LLMProvider) : AgentDependencies =
    let mutable stored : SessionSnapshot option = None
    { Provider          = provider
      Tools             = Map.empty
      LoadSession       = fun sid -> async {
          return Result.Ok (match stored with
                            | Some s -> s
                            | None   -> SessionSnapshot.empty sid DateTimeOffset.UtcNow)
      }
      PersistSession    = fun snap -> async { stored <- Some snap; return Result.Ok () }
      BuildSystemPrompt = fun _ _ -> async { return "stub" }
      Config            = BotSharpConfig.defaults
      StreamHook        = NoStreaming
      Hook              = AgentHook.none
      CronService       = None
      LastTokenUsage    = ref None
      CurrentIteration  = ref 0
      RuleEngine        = None
      FallbackProviders = []
      OpenStateDb       = None }

let private makeInbound (text: string) (sid: string) : InboundMessage = {
    Channel            = ChannelId "cli"
    Sender             = UserId "user"
    Chat               = ChatId sid
    Input              = ChatMessage (text, [])
    Metadata           = Map.empty
    SessionKeyOverride = None
}

// ═══════════════════════════════════════════════════════════════════════════
// AgentCoordinator — initial state (no actors spawned yet)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GetSnapshot returns None when session has never been active`` () =
    let deps  = makeDeps (stubProvider "hi")
    let coord = AgentCoordinator(deps)
    let sid   = SessionId "test:new-session"
    let snap  = coord.GetSnapshot(sid) |> Async.RunSynchronously
    Assert.True(snap.IsNone, "Expected None for a session that has never been routed")

[<Fact>]
let ``Consolidate returns ConsolidationSkipped for unknown session`` () =
    let deps  = makeDeps (stubProvider "hi")
    let coord = AgentCoordinator(deps)
    let sid   = SessionId "test:unknown"
    let result = coord.Consolidate(sid) |> Async.RunSynchronously
    match result with
    | Result.Ok ConsolidationSkipped -> ()
    | other -> Assert.Fail($"Expected ConsolidationSkipped, got {other}")

[<Fact>]
let ``GetLastUsage returns None for unknown session`` () =
    let deps  = makeDeps (stubProvider "hi")
    let coord = AgentCoordinator(deps)
    let usage = coord.GetLastUsage(SessionId "test:no-session") |> Async.RunSynchronously
    Assert.True(usage.IsNone, "Expected None when session has never been active")

[<Fact>]
let ``GetLastUsage returns Some after routing a message that triggers an LLM call`` () =
    let deps  = makeDeps (stubProvider "response text")
    let coord = AgentCoordinator(deps)
    let _     = coord.Route(makeInbound "hello" "usage-chat") |> Async.RunSynchronously
    let usage = coord.GetLastUsage(SessionId "cli:usage-chat") |> Async.RunSynchronously
    Assert.True(usage.IsSome, "Expected Some TokenUsage after a successful LLM call")

[<Fact>]
let ``GetActiveSessionIds is empty before any routing`` () =
    let deps  = makeDeps (stubProvider "hi")
    let coord = AgentCoordinator(deps)
    let ids   = coord.GetActiveSessionIds()
    Assert.Empty(ids)

// ═══════════════════════════════════════════════════════════════════════════
// AgentCoordinator — after routing a message
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Route returns PlainResponse when StreamHook is NoStreaming`` () =
    let deps     = makeDeps (stubProvider "hello from stub")
    let coord    = AgentCoordinator(deps)
    let inbound  = makeInbound "ping" "chat1"
    let result   = coord.Route(inbound) |> Async.RunSynchronously
    match result with
    | Result.Ok (PlainResponse text) -> Assert.Equal("hello from stub", text)
    | other -> Assert.Fail($"Expected PlainResponse, got {other}")

[<Fact>]
let ``Route creates a live actor for the session`` () =
    let deps     = makeDeps (stubProvider "ok")
    let coord    = AgentCoordinator(deps)
    let inbound  = makeInbound "hi" "chat2"
    let _ = coord.Route(inbound) |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    Assert.NotEmpty(ids)

[<Fact>]
let ``GetSnapshot returns Some after a message is routed`` () =
    let deps     = makeDeps (stubProvider "ok")
    let coord    = AgentCoordinator(deps)
    let inbound  = makeInbound "hello" "chat3"
    let _ = coord.Route(inbound) |> Async.RunSynchronously
    // Wait briefly for the actor to finish persisting
    Async.Sleep 50 |> Async.RunSynchronously
    let snap = coord.GetSnapshot(SessionId "cli:chat3") |> Async.RunSynchronously
    Assert.True(snap.IsSome, "Expected Some snapshot after routing a message")

[<Fact>]
let ``two different chats produce two separate active session IDs`` () =
    let deps  = makeDeps (stubProvider "ok")
    let coord = AgentCoordinator(deps)
    let _ = coord.Route(makeInbound "hi" "chat-a") |> Async.RunSynchronously
    let _ = coord.Route(makeInbound "hi" "chat-b") |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    Assert.True(ids.Count >= 2, $"Expected at least 2 active sessions, got {ids.Count}")

[<Fact>]
let ``same chat routed twice uses the same actor (singleton per session)`` () =
    let deps  = makeDeps (stubProvider "pong")
    let coord = AgentCoordinator(deps)
    let _ = coord.Route(makeInbound "msg1" "chat-x") |> Async.RunSynchronously
    let _ = coord.Route(makeInbound "msg2" "chat-x") |> Async.RunSynchronously
    // Only one session ID for the same chat
    let ids = coord.GetActiveSessionIds()
    let count = ids |> Set.filter (fun (SessionId id) -> id.Contains("chat-x")) |> Set.count
    Assert.Equal(1, count)

// ═══════════════════════════════════════════════════════════════════════════
// AgentCoordinator.ShutdownAll
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ShutdownAll clears the active session registry`` () =
    let deps  = makeDeps (stubProvider "ok")
    let coord = AgentCoordinator(deps)
    let _ = coord.Route(makeInbound "hi" "chat-shutdown") |> Async.RunSynchronously
    Assert.NotEmpty(coord.GetActiveSessionIds())
    coord.ShutdownAll()
    Assert.Empty(coord.GetActiveSessionIds())

[<Fact>]
let ``Route returns AgentError when provider returns error`` () =
    let deps   = makeDeps errorProvider
    let coord  = AgentCoordinator(deps)
    let result = coord.Route(makeInbound "ping" "err-chat") |> Async.RunSynchronously
    match result with
    | Result.Error (AgentLlmFailure { Kind = ServerError 503 }) -> ()
    | other -> Assert.Fail($"Expected AgentLlmFailure(ServerError 503), got {other}")

[<Fact>]
let ``Route returns StreamedResponse when StreamHook is StreamingHook`` () =
    // With StreamingHook, the coordinator marks the response as already-streamed.
    let hook =
        StreamingHook(
            onDelta    = (fun _ -> async { () }),
            onStreamEnd = (fun _ -> async { () }))
    let deps  = { makeDeps (stubProvider "hi") with StreamHook = hook }
    let coord = AgentCoordinator(deps)
    let result = coord.Route(makeInbound "ping" "stream-chat") |> Async.RunSynchronously
    match result with
    | Result.Ok (StreamedResponse _) -> ()
    | other -> Assert.Fail($"Expected StreamedResponse, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Actor-level edge cases: actor exists but has no snapshot yet
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``Consolidate returns ConsolidationSkipped for actor with no snapshot`` () =
    // A failed Route creates the actor but leaves lastSnap = None.
    // Subsequent Consolidate hits the actor-level None branch, not coordinator-level.
    let deps   = makeDeps errorProvider
    let coord  = AgentCoordinator(deps)
    let _ = coord.Route(makeInbound "ping" "err-sess") |> Async.RunSynchronously
    let sid    = SessionId "cli:err-sess"
    let result = coord.Consolidate(sid) |> Async.RunSynchronously
    match result with
    | Result.Ok ConsolidationSkipped -> ()
    | other -> Assert.Fail($"Expected ConsolidationSkipped for actor with no snapshot, got {other}")

[<Fact>]
let ``GetSnapshot returns None for actor that was created by a failed route`` () =
    let deps   = makeDeps errorProvider
    let coord  = AgentCoordinator(deps)
    let _ = coord.Route(makeInbound "ping" "err-chat-snap") |> Async.RunSynchronously
    Async.Sleep 50 |> Async.RunSynchronously
    let snap = coord.GetSnapshot(SessionId "cli:err-chat-snap") |> Async.RunSynchronously
    Assert.True(snap.IsNone, "Expected None snapshot after a failed route")

[<Fact>]
let ``ShutdownAll on coordinator with no active sessions does not throw`` () =
    let deps  = makeDeps (stubProvider "ok")
    let coord = AgentCoordinator(deps)
    // No sessions spawned — ShutdownAll must be a no-op
    coord.ShutdownAll()
    Assert.Empty(coord.GetActiveSessionIds())

[<Fact>]
let ``Route with SessionKeyOverride uses the override as the session ID`` () =
    // sessionId: Some key -> SessionId key (ignores channel+chat)
    let deps   = makeDeps (stubProvider "ok")
    let coord  = AgentCoordinator(deps)
    let inbound = { makeInbound "hi" "chat99" with SessionKeyOverride = Some (SessionId "custom-session-key") }
    let _ = coord.Route(inbound) |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    // Actor registered under the override key, not "cli:chat99"
    Assert.True (ids.Contains(SessionId "custom-session-key"), "Override key must be used as session ID")
    Assert.False(ids.Contains(SessionId "cli:chat99"), "Default key must NOT be registered when override is set")

[<Fact>]
let ``Consolidate returns ConsolidationSkipped when snapshot has fewer messages than MemoryWindowSize`` () =
    // Route one message → actor stores snapshot (~2 messages: user + assistant).
    // With MemoryWindowSize = 100, unconsolidated (2) < 100 → consolidate returns ConsolidationSkipped.
    // This tests the RequestConsolidate arm: lastSnap = Some snap but consolidate itself skips.
    let deps  = { makeDeps (stubProvider "hi") with
                    Config = { BotSharpConfig.defaults with MemoryWindowSize = 100 } }
    let coord = AgentCoordinator(deps)
    let _ = coord.Route(makeInbound "ping" "few-msgs-sess") |> Async.RunSynchronously
    let sid   = SessionId "cli:few-msgs-sess"
    let result = coord.Consolidate(sid) |> Async.RunSynchronously
    match result with
    | Result.Ok ConsolidationSkipped -> ()
    | other -> Assert.Fail($"Expected ConsolidationSkipped for too-few messages, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// unified_session — Python parity: AgentLoop._dispatch() key-rewriting
// When UnifiedSession = true, all messages (without SessionKeyOverride) are
// routed to the single "unified:default" session.
// ═══════════════════════════════════════════════════════════════════════════

let private makeDepsUnified () : AgentDependencies =
    let mutable stored : SessionSnapshot option = None
    { Provider          = stubProvider "ok"
      Tools             = Map.empty
      LoadSession       = fun sid -> async {
          return Result.Ok (match stored with
                            | Some s -> s
                            | None   -> SessionSnapshot.empty sid DateTimeOffset.UtcNow)
      }
      PersistSession    = fun snap -> async { stored <- Some snap; return Result.Ok () }
      BuildSystemPrompt = fun _ _ -> async { return "stub" }
      Config            = { BotSharpConfig.defaults with UnifiedSession = true }
      StreamHook        = NoStreaming
      Hook              = AgentHook.none
      CronService       = None
      LastTokenUsage    = ref None
      CurrentIteration  = ref 0
      RuleEngine        = None
      FallbackProviders = []
      OpenStateDb       = None }

[<Fact>]
let ``unified_session routes all messages to 'unified:default' session`` () =
    // Python parity: test_unified_session_rewrites_key_to_unified_default
    let deps  = makeDepsUnified ()
    let coord = AgentCoordinator(deps)
    let _  = coord.Route(makeInbound "hi" "chat-111") |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    Assert.True(ids.Contains(SessionId "unified:default"), "unified_session must route to unified:default")
    Assert.False(ids.Contains(SessionId "cli:chat-111"), "Original session key must not be registered when unified_session=true")

[<Fact>]
let ``unified_session merges messages from different channels into one session`` () =
    // Python parity: test_unified_session_different_channels_share_same_key
    let deps  = makeDepsUnified ()
    let coord = AgentCoordinator(deps)
    // Route messages from different channels
    let msg1 = { makeInbound "hi" "chat-A" with Channel = ChannelId "telegram" }
    let msg2 = { makeInbound "hi" "chat-B" with Channel = ChannelId "discord" }
    let _ = coord.Route(msg1) |> Async.RunSynchronously
    let _ = coord.Route(msg2) |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    // Only one session must exist
    Assert.Equal(1, ids.Count)
    Assert.True(ids.Contains(SessionId "unified:default"), "Both channels must share unified:default")

[<Fact>]
let ``unified_session disabled preserves normal channel:chat routing`` () =
    // Python parity: test_unified_session_disabled_preserves_original_key
    let deps  = makeDeps (stubProvider "ok")   // UnifiedSession = false (default)
    let coord = AgentCoordinator(deps)
    let _  = coord.Route(makeInbound "hi" "chat-999") |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    Assert.True(ids.Contains(SessionId "cli:chat-999"), "Normal routing must use channel:chat key")
    Assert.False(ids.Contains(SessionId "unified:default"), "unified:default must not appear when feature is disabled")

[<Fact>]
let ``unified_session respects existing SessionKeyOverride`` () =
    // Python parity: test_unified_session_respects_existing_override
    // When a message already has an explicit SessionKeyOverride, it wins over unified routing.
    let deps  = makeDepsUnified ()
    let coord = AgentCoordinator(deps)
    let inbound = { makeInbound "hi" "chat-111" with
                        SessionKeyOverride = Some (SessionId "telegram:thread:42") }
    let _ = coord.Route(inbound) |> Async.RunSynchronously
    let ids = coord.GetActiveSessionIds()
    Assert.True(ids.Contains(SessionId "telegram:thread:42"), "Override must win over unified_session routing")
    Assert.False(ids.Contains(SessionId "unified:default"), "unified:default must not be used when override is present")
