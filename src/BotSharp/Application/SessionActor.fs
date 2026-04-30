module BotSharp.Application.SessionActor

open System
open System.Collections.Concurrent
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Application.MemoryConsolidator
open BotSharp.Infrastructure.Tools.MyTool

// ═══════════════════════════════════════════════════════════════════════════
// Per-session MailboxProcessor actor
//
// Each active session runs in its own MailboxProcessor.  Messages are
// processed one at a time per session (no intra-session concurrency) but
// multiple sessions run concurrently.
//
// SessionActorMsg DU:
//   ProcessInput  — run a complete agent turn and reply with the result
//   GetSnapshot   — return the current session snapshot (non-mutating)
//   Shutdown      — stop the actor gracefully
// ═══════════════════════════════════════════════════════════════════════════

// ═══════════════════════════════════════════════════════════════════════════
// Agent result — parsed at the coordinator boundary, not validated at display
//
// StreamedResponse: text was already printed via OnDelta; display is done.
// PlainResponse:    text was never shown; consumer must display it.
//
// Why a DU rather than a bool flag: a consumer that receives StreamedResponse
// structurally cannot accidentally re-display it — there is no Content field
// to print. Make illegal states (double-print) unrepresentable.
// ═══════════════════════════════════════════════════════════════════════════

type AgentResult =
    | PlainResponse    of text: string   // not streamed; display via port.Send
    | StreamedResponse of text: string   // already shown via OnDelta; suppress display

type SessionActorMsg =
    | ProcessInput       of InboundMessage * AsyncReplyChannel<Result<string * SessionSnapshot, AgentError>>
    | GetSnapshot        of AsyncReplyChannel<SessionSnapshot option>
    | RequestConsolidate of AsyncReplyChannel<Result<ConsolidationResult, AgentError>>
    | GetLastUsage       of AsyncReplyChannel<TokenUsage option>
    | Shutdown

/// Create and start a MailboxProcessor that handles one session.
/// Each actor gets its own LastTokenUsage and CurrentIteration refs so concurrent
/// sessions don't overwrite each other's stats. The my tool closure captures these refs.
let createSessionActor
    (sid  : SessionId)
    (deps : AgentDependencies)
    : MailboxProcessor<SessionActorMsg> =
    // Per-actor mutable cells: AgentLoop writes after each LLM call; my tool reads them.
    let actorLastUsage   : TokenUsage option ref = ref None
    let actorCurrentIter : int ref               = ref 0
    let deps' = {
        deps with
            LastTokenUsage   = actorLastUsage
            CurrentIteration = actorCurrentIter
            Tools =
                deps.Tools
                |> Map.add (ToolName "my")
                    (myToolSpec, executeMyTool deps.Config
                        (fun () -> actorLastUsage.Value)
                        (fun () -> actorCurrentIter.Value))
    }
    MailboxProcessor.Start(fun inbox ->
        // pendingSummary: set after consolidation; consumed (injected once) on the next
        // ProcessInput turn and then cleared. Mirrors Python's session_summary flow.
        let rec loop (lastSnap: SessionSnapshot option) (pendingSummary: string option) = async {
            let! msg = inbox.Receive()
            match msg with
            | Shutdown ->
                return ()

            | GetSnapshot channel ->
                channel.Reply lastSnap
                return! loop lastSnap pendingSummary

            | GetLastUsage channel ->
                channel.Reply actorLastUsage.Value
                return! loop lastSnap pendingSummary

            | RequestConsolidate channel ->
                // Type-driven: if no snapshot exists the absence IS the answer —
                // return ConsolidationSkipped rather than fabricating a SessionId.
                match lastSnap with
                | None ->
                    channel.Reply (Result.Ok ConsolidationSkipped)
                    return! loop lastSnap pendingSummary
                | Some snap ->
                    let! result = consolidate snap deps'
                    // Reply immediately so callers don't block on persistence.
                    channel.Reply result
                    // On success, advance lastConsolidated in the snapshot, persist,
                    // and record the history entry as the pending summary for the next turn.
                    let (newSnap, newSummary) =
                        match result with
                        | Result.Ok (Consolidated (historyEntry, _, newIndex)) ->
                            let s =
                                SessionSnapshot.advanceConsolidated newIndex snap
                                |> function
                                   | Result.Ok s -> s
                                   | Error _     -> snap   // fallback: keep old index
                            let summary = if historyEntry.Trim() <> "" then Some historyEntry else pendingSummary
                            (s, summary)
                        | _ -> (snap, pendingSummary)
                    let! _ = deps'.PersistSession newSnap
                    return! loop (Some newSnap) newSummary

            | ProcessInput (inbound, channel) ->
                // /new: force-consolidate all messages → clear session → return early.
                // Mirrors nanobot's _consolidate_memory(archive_all=True) + session.clear().
                match inbound.Input with
                | Command NewSession ->
                    let sid = sessionId inbound
                    // Load old session and archive all messages to MEMORY.md + HISTORY.md
                    let! loadResult = deps'.LoadSession sid
                    match loadResult with
                    | Result.Ok oldSnap ->
                        let! _ = forceConsolidate oldSnap deps'
                        // Clear the session: persist an empty snapshot (overwrites the JSONL file)
                        let emptySnap = SessionSnapshot.empty sid DateTimeOffset.UtcNow
                        let! _ = deps'.PersistSession emptySnap
                        channel.Reply (Result.Ok ("New session started.", emptySnap))
                        return! loop (Some emptySnap) None
                    | Result.Error _ ->
                        // No existing session — just create empty
                        let emptySnap = SessionSnapshot.empty sid DateTimeOffset.UtcNow
                        channel.Reply (Result.Ok ("New session started.", emptySnap))
                        return! loop (Some emptySnap) None
                | Command ClearHistory ->
                    // /clear: wipe history, but CLIPS checks if we should consolidate first.
                    // Port of nanobot#3467 + CLIPS safety check.
                    let sid = sessionId inbound
                    let unconsolidated =
                        match lastSnap with
                        | Some snap -> SessionSnapshot.messageCount snap - SessionSnapshot.lastConsolidated snap
                        | None -> 0
                    // Ask CLIPS if we should force-consolidate before clearing
                    let shouldArchiveFirst =
                        match deps'.RuleEngine with
                        | Some engine ->
                            BotSharp.Infrastructure.Rules.RuleEngine.assertSessionClearRequest engine unconsolidated
                            let result = BotSharp.Infrastructure.Rules.RuleEngine.shouldConsolidateBeforeClear engine
                            BotSharp.Infrastructure.Rules.RuleEngine.resetTurn engine
                            result
                        | None -> unconsolidated > 20   // fallback: same threshold without CLIPS
                    if shouldArchiveFirst then
                        match lastSnap with
                        | Some snap ->
                            eprintfn "[/clear] %d unconsolidated messages — archiving to MEMORY.md first" unconsolidated
                            let! _ = forceConsolidate snap deps'
                            ()
                        | None -> ()
                    let emptySnap = SessionSnapshot.empty sid DateTimeOffset.UtcNow
                    let! _ = deps'.PersistSession emptySnap
                    let msg =
                        if shouldArchiveFirst then $"Archived {unconsolidated} messages to MEMORY.md, then cleared."
                        else "History cleared."
                    channel.Reply (Result.Ok (msg, emptySnap))
                    return! loop (Some emptySnap) None
                | Command (ShowHistory countOpt) ->
                    // /history [n]: show recent messages from current session.
                    // Port of nanobot#3466.
                    let n = countOpt |> Option.defaultValue 10
                    let historyText =
                        match lastSnap with
                        | None -> "(no active session)"
                        | Some snap ->
                            let msgs = SessionSnapshot.messages snap
                            let recent = if msgs.Length <= n then msgs else msgs |> List.skip (msgs.Length - n)
                            if recent.IsEmpty then "(empty session)"
                            else
                                recent
                                |> List.mapi (fun i msg ->
                                    let role, content =
                                        match msg with
                                        | UserMessage (c, _)          -> "user", c
                                        | AssistantMessage (c, _)     -> "assistant", c
                                        | SystemMessage c             -> "system", c
                                        | ToolCallMessage _           -> "tool_calls", "(tool calls)"
                                        | ToolResultMessage (_, ToolName n, c) -> $"tool:{n}", (if c.Length > 100 then c.[..99] + "..." else c)
                                    let preview = if content.Length > 200 then content.[..199] + "..." else content
                                    $"[{i+1}] {role}: {preview}")
                                |> String.concat "\n"
                    channel.Reply (Result.Ok (historyText, lastSnap |> Option.defaultValue (SessionSnapshot.empty (SessionId "none") DateTimeOffset.UtcNow)))
                    return! loop lastSnap pendingSummary
                | _ -> ()
                // Consume pendingSummary this turn (inject into runtime context, then clear).
                let! result = runAgentLoop inbound deps' pendingSummary
                match result with
                | Result.Ok (text, snap) ->
                    channel.Reply (Result.Ok (text, snap))
                    // Auto-consolidate when unconsolidated messages exceed MemoryWindowSize.
                    // Runs after replying so it doesn't delay the response, but before the
                    // next message is processed (the actor is still busy during this step).
                    let! (newSnap, newSummary) =
                        if needsConsolidation snap deps'.Config then
                            async {
                                let! consolidationResult = consolidate snap deps'
                                match consolidationResult with
                                | Result.Ok (Consolidated (historyEntry, _, newIndex)) ->
                                    match SessionSnapshot.advanceConsolidated newIndex snap with
                                    | Result.Ok s ->
                                        let! _ = deps'.PersistSession s
                                        let summary = if historyEntry.Trim() <> "" then Some historyEntry else None
                                        return (s, summary)
                                    | Error _ -> return (snap, None)
                                | _ -> return (snap, None)
                            }
                        else
                            async { return (snap, None) }   // summary consumed; clear it
                    return! loop (Some newSnap) newSummary
                | Result.Error e ->
                    channel.Reply (Result.Error e)
                    return! loop lastSnap pendingSummary   // keep summary for retry
        }
        loop None None)

// ═══════════════════════════════════════════════════════════════════════════
// Agent coordinator — manages the actor-per-session pool
// ═══════════════════════════════════════════════════════════════════════════

type AgentCoordinator(deps: AgentDependencies) =
    let actors = ConcurrentDictionary<SessionId, MailboxProcessor<SessionActorMsg>>()

    /// Get or create the actor for a given session ID.
    member private _.GetOrCreate(sid: SessionId) =
        actors.GetOrAdd(sid, fun _ -> createSessionActor sid deps)

    /// Route an inbound message to the correct session actor and await the reply.
    /// Parses the raw (text, snapshot) pair into AgentResult at this boundary:
    ///   WantsStreaming = true  → StreamedResponse (text already shown via OnDelta)
    ///   WantsStreaming = false → PlainResponse     (consumer must display the text)
    member this.Route(inbound: InboundMessage) : Async<Result<AgentResult, AgentError>> =
        async {
            // unified_session: when enabled, route all messages to "unified:default" —
            // unless the message carries an explicit SessionKeyOverride (e.g. Telegram thread).
            // Python parity: AgentLoop._dispatch() key-rewriting logic.
            let sid =
                if deps.Config.UnifiedSession && inbound.SessionKeyOverride.IsNone then
                    SessionId "unified:default"
                else
                    sessionId inbound
            let actor = this.GetOrCreate(sid)
            let! result = actor.PostAndAsyncReply(fun ch -> ProcessInput(inbound, ch))
            match result with
            | Result.Ok (text, _) ->
                let agentResult =
                    match deps.StreamHook with
                    | StreamingHook _ -> StreamedResponse text   // text already shown via OnDelta
                    | NoStreaming      -> PlainResponse text      // consumer must display it
                return Result.Ok agentResult
            | Result.Error e -> return Result.Error e
        }

    /// Get the current snapshot for a session (returns None if not yet active).
    member this.GetSnapshot(sid: SessionId) : Async<SessionSnapshot option> =
        async {
            match actors.TryGetValue(sid) with
            | false, _ -> return None
            | true, actor ->
                return! actor.PostAndAsyncReply(fun ch -> GetSnapshot ch)
        }

    /// Force a memory consolidation for a session.
    /// Returns ConsolidationSkipped if the session has no snapshot or fewer
    /// unconsolidated messages than MemoryWindowSize.
    member _.Consolidate(sid: SessionId) : Async<Result<ConsolidationResult, AgentError>> =
        async {
            match actors.TryGetValue(sid) with
            | false, _ ->
                return Result.Ok ConsolidationSkipped   // no active session
            | true, actor ->
                return! actor.PostAndAsyncReply(fun ch -> RequestConsolidate ch)
        }

    /// Get the last LLM token usage recorded for a session.
    /// Returns None when the session has no active actor or hasn't made an LLM call yet.
    member _.GetLastUsage(sid: SessionId) : Async<TokenUsage option> =
        async {
            match actors.TryGetValue(sid) with
            | false, _ -> return None
            | true, actor ->
                return! actor.PostAndAsyncReply(fun ch -> GetLastUsage ch)
        }

    /// Return the set of session IDs that currently have a live actor in memory.
    /// Used by AutoCompactService to skip sessions that may be actively processing.
    member _.GetActiveSessionIds() : Set<SessionId> =
        actors.Keys |> Set.ofSeq

    /// Shut down all session actors (called on application exit).
    member _.ShutdownAll() =
        for kv in actors do
            kv.Value.Post Shutdown
        actors.Clear()
