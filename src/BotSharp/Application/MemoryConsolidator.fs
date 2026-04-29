module BotSharp.Application.MemoryConsolidator

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Infrastructure.Shared.AsyncResult
open BotSharp.Infrastructure.Shared.StringUtils
open BotSharp.Infrastructure.Shared.GitBlame
open BotSharp.Application.AgentLoop

// ═══════════════════════════════════════════════════════════════════════════
// Memory consolidator
//
// When the number of unconsolidated messages exceeds config.MemoryWindowSize,
// the consolidator asks the LLM to produce two artefacts via a tool call:
//   1. A history entry  → appended to {workspace}/memory/HISTORY.md
//   2. A memory update  → overwrites   {workspace}/memory/MEMORY.md
//
// The LLM is asked to call the `save_memory` tool so structured data comes
// back as a JSON tool call rather than free text — no regex parsing needed.
// If the LLM returns plain text instead (no tool call), we fall back to the
// marker-based parser (==HISTORY== / ==MEMORY==) for resilience.
//
// The history log (HISTORY.md) is a human-readable, grep-able record of past
// sessions.  MEMORY.md is the long-term "state of the world" injected into
// the system prompt of every new session for cross-session continuity.
// ═══════════════════════════════════════════════════════════════════════════

let private memoryDir   (wp: string) = Path.Combine(wp, "memory")
let private memoryFile  (wp: string) = Path.Combine(wp, "memory", "MEMORY.md")
let private historyFile (wp: string) = Path.Combine(wp, "memory", "HISTORY.md")
let private cursorFile  (wp: string) = Path.Combine(wp, "memory", ".dream_cursor")

let private readMemory (wp: string) : Async<string> =
    async {
        let path = memoryFile wp
        if File.Exists path then
            return! File.ReadAllTextAsync(path) |> Async.AwaitTask
        else
            return ""
    }

let private writeMemory (wp: string) (content: string) : Async<unit> =
    async {
        Directory.CreateDirectory(memoryDir wp) |> ignore
        do! File.WriteAllTextAsync(memoryFile wp, content) |> Async.AwaitTask
    }

let private appendHistory (wp: string) (entry: string) : Async<unit> =
    async {
        Directory.CreateDirectory(memoryDir wp) |> ignore
        let line = entry.TrimEnd() + "\n\n"
        do! File.AppendAllTextAsync(historyFile wp, line) |> Async.AwaitTask
    }

/// Write the cursor position to memory/.dream_cursor (Python parity).
/// Tracks which message index was last consolidated so buildSystemPrompt
/// can slice HISTORY.md correctly.
let private writeCursor (wp: string) (index: int) : Async<unit> =
    async {
        Directory.CreateDirectory(memoryDir wp) |> ignore
        do! File.WriteAllTextAsync(cursorFile wp, string index) |> Async.AwaitTask
    }

// ── Tool spec for structured consolidation ────────────────────────────────

/// The LLM must call this tool to return a structured consolidation result.
/// Using a tool call avoids free-text parsing and works with all providers.
let private saveMemorySpec : ToolSpec = {
    Name            = ToolName "save_memory"
    Description     = "Save the memory consolidation result to persistent storage."
    Parameters      = Map.ofList [
        "history_entry", {
            Type        = JsString
            Description = "2-5 sentences summarising key events, decisions, and outcomes. Start with the approximate date/time. Include enough detail for grep searches."
            Required    = true
        }
        "memory_update", {
            Type        = JsString
            Description = "Full updated long-term memory as Markdown. Include all existing facts plus any new ones. If nothing changed, return the current memory unchanged."
            Required    = true
        }
    ]
    ConcurrencySafe = false  // internal consolidation tool — never run concurrently
}

// ── Message formatting ────────────────────────────────────────────────────

let private formatMessage (i: int) (msg: Message) : string =
    let role =
        match msg with
        | SystemMessage _     -> "System"
        | UserMessage _       -> "User"
        | AssistantMessage (_, _) -> "Assistant"
        | ToolCallMessage (_, _) -> "Tool call"
        | ToolResultMessage _ -> "Tool result"
    let content =
        match msg with
        | SystemMessage c              -> c.[..min 200 (c.Length - 1)]
        | UserMessage (c, _)           -> c.[..min 200 (c.Length - 1)]
        | AssistantMessage (c, _)      -> c.[..min 200 (c.Length - 1)]
        | ToolCallMessage (nel, _)     -> sprintf "%d tool call(s)" (NonEmptyList.length nel)
        | ToolResultMessage (_, n, c)  ->
            let (ToolName nm) = n
            sprintf "%s: %s" nm (c.[..min 100 (c.Length - 1)])
    sprintf "%d. [%s] %s" (i + 1) role content

/// Optionally annotate MEMORY.md content with per-line git blame ages.
/// When DreamAnnotateLineAges = true and git blame succeeds, non-blank lines
/// older than staleThresholdDays get a suffix like "← 30d".
/// Mirrors Python MemoryStore._annotate_with_ages().
let private maybeAnnotate (workspacePath: string) (annotateEnabled: bool) (content: string) : string =
    if not annotateEnabled || content = "" then content
    else
        let ages = lineAges workspacePath "memory/MEMORY.md"
        annotateContent content ages

/// Build the consolidation prompt. The LLM is instructed to call `save_memory`.
let private consolidationPrompt (messages: Message list) (currentMemory: string) : string =
    let lines   = messages |> List.mapi formatMessage
    let history = String.concat "\n" lines
    $"""Process this conversation and call the save_memory tool with your consolidation.

## Current Long-term Memory
{if currentMemory = "" then "(empty)" else currentMemory}

## Conversation to Consolidate
{history}"""

// ── Response extraction ───────────────────────────────────────────────────

/// Try to extract (historyEntry, memoryUpdate) from a `save_memory` tool call.
let private extractFromToolCall (call: ToolCall) : (string * string) option =
    let get name =
        match call.Arguments.TryFind name with
        | Some el when el.ValueKind = JsonValueKind.String ->
            match el.GetString() with
            | null | "" -> None
            | s         -> Some s
        | _ -> None
    match get "history_entry", get "memory_update" with
    | Some h, Some m -> Some (h, m)
    | Some h, None   -> Some (h, "")
    | _              -> None

/// Fallback: parse marker-delimited sections from plain-text LLM response.
/// Used when the LLM returns text instead of a tool call.
let private parseConsolidationResponse (raw: string) (currentMemory: string) : string * string =
    let markerHistory = "==HISTORY=="
    let markerMemory  = "==MEMORY=="
    let hi = raw.IndexOf(markerHistory, StringComparison.OrdinalIgnoreCase)
    let mi = raw.IndexOf(markerMemory,  StringComparison.OrdinalIgnoreCase)
    if hi >= 0 && mi > hi then
        let historyEntry = raw.[hi + markerHistory.Length..mi - 1].Trim()
        let memoryUpdate = raw.[mi + markerMemory.Length..].Trim()
        historyEntry, memoryUpdate
    else
        // Treat the whole response as a history entry, keep existing memory.
        raw.Trim(), currentMemory

// ── Public API ────────────────────────────────────────────────────────────

/// Check if consolidation is needed based on unconsolidated message count.
let needsConsolidation (snap: SessionSnapshot) (config: BotSharpConfig) : bool =
    let unconsolidatedCount =
        SessionSnapshot.messageCount snap - SessionSnapshot.lastConsolidated snap
    unconsolidatedCount >= config.MemoryWindowSize

/// Internal: run consolidation with optional force flag.
/// force = true: consolidate ALL messages regardless of MemoryWindowSize threshold.
///   Used by /new (mirrors nanobot's _consolidate_memory(archive_all=True)).
let private consolidateImpl
    (force : bool)
    (snap  : SessionSnapshot)
    (deps  : AgentDependencies)
    : AsyncResult<ConsolidationResult, AgentError> =
    asyncResult {
        let hasMessages = SessionSnapshot.messageCount snap > SessionSnapshot.lastConsolidated snap
        if not force && not (needsConsolidation snap deps.Config) then
            return ConsolidationSkipped
        elif not hasMessages then
            return ConsolidationSkipped
        else
            let wp          = deps.Config.WorkspacePath
            let! currentMem = readMemory wp |> AsyncResult.ofAsync
            // Annotate MEMORY.md lines with git blame ages if enabled.
            // Mirrors Python MemoryStore._annotate_with_ages() called in run().
            let annotatedMem = maybeAnnotate wp deps.Config.DreamAnnotateLineAges currentMem
            let toSummarize = SessionSnapshot.unconsolidated snap
            let prompt      = consolidationPrompt toSummarize annotatedMem

            // Use DreamModelOverride if configured; otherwise fall back to DefaultModel.
            // This lets users run consolidation with a cheaper model (Python: dream.model_override).
            let dreamModel =
                deps.Config.DreamModelOverride
                |> Option.defaultValue deps.Config.DefaultModel

            let request : LLMRequest = {
                Messages = [
                    UserMessage ("You are a memory consolidation agent. Call the save_memory tool with your consolidation of the conversation.", [])
                    UserMessage (prompt, [])
                ]
                Tools    = [ saveMemorySpec ]
                Model    = dreamModel
                Settings = { Temperature     = 0.3
                             MaxTokens       = 2048
                             ReasoningEffort = None }
            }

            let! response =
                async {
                    let! r = chatWithRetry deps.Provider request.Settings request.Messages request.Tools
                    return Result.mapError AgentLlmFailure r
                }

            let historyEntry, memoryUpdate =
                match response.Body with
                | WithToolCalls (_, calls) ->
                    // Prefer the first save_memory call; fall back to text parsing if args malformed.
                    let callOpt =
                        calls
                        |> NonEmptyList.toList
                        |> List.tryFind (fun c -> c.Tool = ToolName "save_memory")
                        |> Option.bind extractFromToolCall
                    match callOpt with
                    | Some (h, m) -> h, m
                    | None        -> parseConsolidationResponse "" currentMem   // no useful tool call
                | TextOnly raw ->
                    // Fallback: LLM returned text; parse markers
                    parseConsolidationResponse raw currentMem
                | Empty ->
                    "", currentMem

            // Strip think-blocks from the history entry before persisting.
            // Mirrors Python memory.append_history which calls strip_think before writing.
            let historyEntry = stripThink historyEntry

            // Persist to disk
            if historyEntry <> "" then
                do! appendHistory wp historyEntry |> AsyncResult.ofAsync
            if memoryUpdate <> currentMem && memoryUpdate <> "" then
                do! writeMemory wp memoryUpdate |> AsyncResult.ofAsync

            let newIndex = SessionSnapshot.messageCount snap
            // Write cursor position (Python parity: memory/.dream_cursor tracks last consolidated index)
            do! writeCursor wp newIndex |> AsyncResult.ofAsync

            return Consolidated (historyEntry, Some memoryUpdate, newIndex)
    }

/// Run consolidation if the unconsolidated message count exceeds MemoryWindowSize.
let consolidate
    (snap  : SessionSnapshot)
    (deps  : AgentDependencies)
    : AsyncResult<ConsolidationResult, AgentError> =
    consolidateImpl false snap deps

/// Force-consolidate ALL unconsolidated messages regardless of threshold.
/// Used by /new to archive the conversation before clearing the session.
/// Mirrors nanobot's _consolidate_memory(archive_all=True).
let forceConsolidate
    (snap  : SessionSnapshot)
    (deps  : AgentDependencies)
    : AsyncResult<ConsolidationResult, AgentError> =
    consolidateImpl true snap deps
