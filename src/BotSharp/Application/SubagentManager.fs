module BotSharp.Application.SubagentManager

open System
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop

// ═══════════════════════════════════════════════════════════════════════════
// SubagentManager — fire-and-forget background agent execution
//
// When a subagent is spawned:
//   1. A fresh ephemeral session (no stored history) is created.
//   2. `runAgentLoop` handles the full multi-turn LLM + tool loop.
//   3. On completion, the result is announced back via the `onComplete`
//      callback as an InboundMessage, which the main coordinator routes
//      to the origin session so the user sees the summary.
//
// Type-driven decisions:
//   • SpawnTool explicitly provides originChannel + originChat — no mutable
//     session context. The LLM knows where to report because the system
//     prompt documents the current channel/chat.
//   • Subagent deps strip out spawn/message tools (no recursive spawning;
//     no send-to-user — the result arrives as an announcement).
//   • Sessions are ephemeral (in-memory only); the subagent cannot corrupt
//     the main session's history.
// ═══════════════════════════════════════════════════════════════════════════

/// Callback invoked when a subagent finishes. The announcement is injected
/// into the coordinator as an inbound message targeting the origin session.
type OnSubagentComplete = InboundMessage -> Async<unit>

/// Build subagent-specific AgentDependencies from the base deps:
///   • Fresh in-memory session (no disk I/O for session state)
///   • NoStreaming (subagents run in background, no live output)
///   • Tools filtered to remove spawn/message (no recursive spawning)
///   • CronService = None (subagents don't schedule cron jobs)
let private buildSubagentDeps (base_: AgentDependencies) : AgentDependencies =
    let excluded = Set.ofList [ ToolName "spawn"; ToolName "message" ]
    let filteredTools =
        base_.Tools |> Map.filter (fun name _ -> not (excluded.Contains name))
    { base_ with
        Tools          = filteredTools
        LoadSession    = fun sid -> async { return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow) }
        PersistSession = fun _   -> async { return Result.Ok () }
        StreamHook     = NoStreaming   // background; no live streaming to CLI
        CronService    = None }

type SubagentManager(baseDeps: AgentDependencies, onComplete: OnSubagentComplete) =

    let subDeps = buildSubagentDeps baseDeps

    let announce
        (taskId       : string)
        (label        : string)
        (task         : string)
        (result       : Result<string, AgentError>)
        (originChannel: ChannelId)
        (originChat   : ChatId)
        : Async<unit> =
        async {
            let status, body =
                match result with
                | Result.Ok text ->
                    "completed successfully", text
                | Result.Error e ->
                    "failed", $"{e}"
            // The announcement text instructs the main agent to summarize
            // for the user — same pattern as the Python implementation.
            let content =
                $"""[Subagent '{label}' {status}]

Task: {task}

Result:
{body}

Summarize this naturally for the user. Keep it brief (1-2 sentences). Do not mention technical details like "subagent" or task IDs."""
            let msg : InboundMessage = {
                Channel            = originChannel
                Sender             = UserId "subagent"
                Chat               = originChat
                Input              = ChatMessage (content, [])
                Metadata           = Map.ofList [ "source", "subagent"; "task_id", taskId ]
                SessionKeyOverride = None
            }
            do! onComplete msg
        }

    let runSubagent
        (taskId       : string)
        (task         : string)
        (label        : string)
        (originChannel: ChannelId)
        (originChat   : ChatId)
        : Async<unit> =
        async {
            // Fresh ephemeral session per subagent — isolated from the main session.
            let inbound : InboundMessage = {
                Channel            = ChannelId "subagent"
                Sender             = UserId    "spawn"
                Chat               = ChatId    $"subagent-{taskId}"
                Input              = ChatMessage (task, [])
                Metadata           = Map.ofList [ "task_id", taskId; "label", label ]
                SessionKeyOverride = Some (SessionId $"subagent:{taskId}")
            }
            let! result = runAgentLoop inbound subDeps None
            let finalResult =
                match result with
                | Result.Ok (text, _) -> Result.Ok text
                | Result.Error e      -> Result.Error e
            do! announce taskId label task finalResult originChannel originChat
        }

    // ── Public API ────────────────────────────────────────────────────────

    /// Spawn a subagent to execute `task` in the background.
    /// Returns immediately with a confirmation string.
    member _.Spawn
        (task         : string,
         label        : string option,
         originChannel: ChannelId,
         originChat   : ChatId)
        : Async<string> =
        async {
            let taskId = Guid.NewGuid().ToString("N").[..7]
            let displayLabel =
                label
                |> Option.defaultWith (fun () ->
                    if task.Length <= 30 then task
                    else task.[..29] + "...")
            Async.Start (
                async {
                    try
                        do! runSubagent taskId task displayLabel originChannel originChat
                    with ex ->
                        eprintfn "[subagent %s] unhandled error: %s" taskId ex.Message
                })
            return $"Subagent [{displayLabel}] started (id: {taskId}). I'll notify you when it completes."
        }
