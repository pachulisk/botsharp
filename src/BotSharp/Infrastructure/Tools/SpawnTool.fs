module BotSharp.Infrastructure.Tools.SpawnTool

open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser
open BotSharp.Application.SubagentManager

// ═══════════════════════════════════════════════════════════════════════════
// SpawnTool — agent-facing tool to launch background subagents
//
// The agent calls this to offload a complex or long-running task to a
// subagent. The subagent runs the full agent loop (with file/shell/web tools)
// and reports back when complete.
//
// Design:
//   • `channel` and `chat` are required explicit parameters — no mutable
//     session context. The LLM must supply the origin destination so the
//     subagent can announce its result to the right place.
//   • `label` is optional for display; defaults to a truncated task string.
//   • Spawn does not block: it starts the subagent and returns immediately.
// ═══════════════════════════════════════════════════════════════════════════

let spawnToolSpec : ToolSpec = {
    Name            = ToolName "spawn"
    Description     = """Spawn a background subagent to handle a complex or time-consuming task.
The subagent runs independently and reports back when done.
Provide channel and chat so the result is delivered to the correct conversation.
Do NOT use spawn for simple tasks — only for tasks that need extended tool use."""
    Parameters      = Map.ofList [
        "task",    { Type = JsString; Description = "Full description of the task to complete"; Required = true }
        "label",   { Type = JsString; Description = "Short label for the task (shown in progress/completion messages)"; Required = false }
        "channel", { Type = JsString; Description = "Channel ID for the completion announcement (e.g. cli)"; Required = true }
        "chat",    { Type = JsString; Description = "Chat ID for the completion announcement (e.g. cli-session)"; Required = true }
    ]
    ConcurrencySafe = false  // spawns a new agent process; ordering matters
}

let executeSpawn (mgr: SubagentManager) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "task"    args,
              requireStringArg "channel" args,
              requireStringArg "chat"    args with
        | Error e, _, _ | _, Error e, _ | _, _, Error e ->
            return ToolFailure e
        | Ok task, Ok channelRaw, Ok chatRaw ->
            let label   = tryStringArg "label" args
            let channel = ChannelId channelRaw
            let chat    = ChatId    chatRaw
            let! confirmation = mgr.Spawn(task, label, channel, chat)
            return ToolSuccess confirmation
    }

/// All spawn tools as a (spec, execute) pair, bound to the given SubagentManager.
let allTools (mgr: SubagentManager)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ spawnToolSpec, executeSpawn mgr ]
