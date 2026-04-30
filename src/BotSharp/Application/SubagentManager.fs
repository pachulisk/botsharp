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
        CronService    = None
        // Port of nanobot#3532: subagent uses SubagentMaxIterations from config
        Config         = { base_.Config with MaxIterations = base_.Config.SubagentMaxIterations } }

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
                | Result.Ok (text, _) ->
                    // Track if subagent hit its iteration budget
                    if text.Contains("stopped after") then
                        baseDeps.RuleEngine |> Option.iter (fun engine ->
                            BotSharp.Infrastructure.Rules.RuleEngine.assertSubagentBudgetExhausted
                                engine taskId subDeps.Config.MaxIterations
                            BotSharp.Infrastructure.Rules.RuleEngine.evaluate engine |> ignore)
                    Result.Ok text
                | Result.Error e -> Result.Error e
            do! announce taskId label task finalResult originChannel originChat
        }

    // ── Public API ────────────────────────────────────────────────────────

    /// Run a single subagent step synchronously and return the result.
    /// Used by LongTaskTool for its meta-ReAct loop (each step is short: 8 iterations).
    /// Extra tool specs are injected alongside the standard subagent tools
    /// (e.g., handoff/complete signal tools).
    member _.RunStep
        (extraToolPairs : (ToolSpec * (Map<string, System.Text.Json.JsonElement> -> Async<ToolResult>)) list,
         systemPrompt   : string,
         userMessage    : string)
        : Async<Result<string, AgentError>> =
        async {
            let taskId = Guid.NewGuid().ToString("N").[..7]
            // Build deps with extra tools injected
            let extraMap =
                extraToolPairs
                |> List.map (fun (spec, exec) -> spec.Name, (spec, exec))
                |> Map.ofList
            let stepDeps =
                { subDeps with
                    Tools = Map.fold (fun acc k v -> Map.add k v acc) subDeps.Tools extraMap
                    Config = { subDeps.Config with MaxIterations = 8 } }
            let inbound : InboundMessage = {
                Channel            = ChannelId "long_task"
                Sender             = UserId    "long_task"
                Chat               = ChatId    $"long-task-{taskId}"
                Input              = ChatMessage (userMessage, [])
                Metadata           = Map.empty
                SessionKeyOverride = Some (SessionId $"long-task:{taskId}")
            }
            // Override system prompt by wrapping BuildSystemPrompt
            let stepDepsWithPrompt =
                { stepDeps with
                    BuildSystemPrompt = fun _ _ -> async { return systemPrompt } }
            let! result = runAgentLoop inbound stepDepsWithPrompt None
            match result with
            | Result.Ok (text, _) -> return Result.Ok text
            | Result.Error e      -> return Result.Error e
        }

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
