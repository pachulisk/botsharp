module BotSharp.Application.HeartbeatService

open System
open System.IO
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// HeartbeatService — periodic agent wake-up to check for tasks
//
// Two-phase protocol:
//   Phase 1 (decision): reads HEARTBEAT.md and asks the LLM — via a virtual
//   tool call — to decide `skip` or `run`.  Using a typed tool call rather
//   than free-text parsing prevents unreliable keyword matching.
//
//   Phase 2 (execution): only triggered when Phase 1 returns RunHeartbeat.
//   The `onExecute` callback routes the task text through the full agent loop.
//
// Type-driven: HeartbeatDecision DU makes skip/run structurally distinct.
// The MailboxProcessor holds the cancellation token; Stop() cancels it.
// ═══════════════════════════════════════════════════════════════════════════

// ── Virtual heartbeat tool (used in Phase 1 only) ─────────────────────────

let private heartbeatDecisionSpec : ToolSpec = {
    Name            = ToolName "heartbeat"
    Description     = "Report heartbeat decision after reviewing the task list."
    Parameters      = Map.ofList [
        "action", { Type = JsString; Description = "skip = nothing to do now, run = active tasks pending"; Required = true }
        "tasks",  { Type = JsString; Description = "Natural-language summary of pending tasks (required when action = run)"; Required = false }
    ]
    ConcurrencySafe = false  // internal decision tool — never run concurrently
}

// ── Phase 1: LLM decision ────────────────────────────────────────────────

/// Ask the LLM to read HEARTBEAT.md content and decide whether to act.
/// Returns HeartbeatDecision — either SkipHeartbeat or RunHeartbeat with tasks.
let private decide
    (provider  : LLMProvider)
    (model     : string)
    (heartbeat : string)
    : Async<HeartbeatDecision> =
    async {
        let settings : GenerationSettings = {
            Temperature     = 0.0
            MaxTokens       = 512
            ReasoningEffort = None
        }
        let messages = [
            UserMessage ("You are a heartbeat agent. Review the HEARTBEAT.md content and call the heartbeat tool.", [])
            UserMessage ($"Review the following HEARTBEAT.md and decide whether there are active tasks.\n\n{heartbeat}", [])
        ]
        let! response = provider.Chat settings messages [ heartbeatDecisionSpec ]
        match response with
        | Result.Error _ ->
            // On LLM error, conservatively skip rather than crash.
            return SkipHeartbeat
        | Result.Ok llmResponse ->
            match llmResponse.Body with
            | WithToolCalls (_, calls) ->
                let call = NonEmptyList.head calls
                let action =
                    match call.Arguments.TryFind "action" with
                    | Some v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                        v.GetString() |> Option.ofObj |> Option.defaultValue "skip"
                    | _ -> "skip"
                let tasks =
                    match call.Arguments.TryFind "tasks" with
                    | Some v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                        match v.GetString() with
                        | null | "" -> []
                        | s         -> [ s ]
                    | _ -> []
                match action.ToLowerInvariant() with
                | "run" -> return RunHeartbeat tasks
                | _     -> return SkipHeartbeat
            | TextOnly _ | Empty ->
                // No tool call — LLM chose not to act.
                return SkipHeartbeat
    }

// ── HeartbeatService ─────────────────────────────────────────────────────

/// Callback invoked when the heartbeat decides to run.
/// Receives the list of task descriptions from the LLM's decision.
/// Returns Some response text on success, None if nothing to do.
type OnHeartbeatExecute = string list -> Async<string option>

/// Callback invoked after execution to deliver the result to the user.
type OnHeartbeatNotify = string -> Async<unit>

type HeartbeatService(
        workspacePath    : string,
        provider         : LLMProvider,
        model            : string,
        onExecute        : OnHeartbeatExecute,
        onNotify         : OnHeartbeatNotify,
        intervalSeconds  : int) =

    let cts = new System.Threading.CancellationTokenSource()

    let heartbeatFile () = Path.Combine(workspacePath, "HEARTBEAT.md")

    let readHeartbeat () : string option =
        let path = heartbeatFile ()
        if File.Exists path then
            try Some (File.ReadAllText path)
            with _ -> None
        else None

    let tick () : Async<unit> =
        async {
            match readHeartbeat () with
            | None -> ()   // HEARTBEAT.md absent or unreadable — skip silently
            | Some content ->
                let! decision = decide provider model content
                match decision with
                | SkipHeartbeat -> ()
                | RunHeartbeat tasks ->
                    let! resultOpt = onExecute tasks
                    match resultOpt with
                    | None   -> ()
                    | Some r -> do! onNotify r
        }

    let loop () : Async<unit> =
        async {
            while not cts.Token.IsCancellationRequested do
                try
                    do! Async.Sleep (intervalSeconds * 1000)
                    if not cts.Token.IsCancellationRequested then
                        do! tick ()
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    // Log and continue; don't let one bad tick kill the service.
                    eprintfn "[heartbeat] error: %s" ex.Message
        }

    // ── Public API ────────────────────────────────────────────────────────

    member _.Start() : unit =
        Async.Start(loop (), cts.Token)

    member _.Stop() : unit =
        cts.Cancel()

    /// Manually trigger one heartbeat tick (for testing/debugging).
    member _.TriggerNow() : Async<unit> = tick ()
