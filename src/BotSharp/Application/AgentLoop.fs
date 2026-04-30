module BotSharp.Application.AgentLoop

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Domain.StateMachine
open BotSharp.Infrastructure.Shared.AsyncResult
open BotSharp.Infrastructure.Shared.StringUtils
open BotSharp.Application.ContextBuilder

// ═══════════════════════════════════════════════════════════════════════════
// Agent dependencies (injectable record-of-functions)
// ═══════════════════════════════════════════════════════════════════════════

type AgentDependencies = {
    Provider          : LLMProvider
    Tools             : Map<ToolName, ToolSpec * (Map<string, System.Text.Json.JsonElement> -> Async<ToolResult>)>
    LoadSession       : SessionId -> Async<Result<SessionSnapshot, StorageError>>
    PersistSession    : SessionSnapshot -> Async<Result<unit, StorageError>>
    BuildSystemPrompt : string option -> string -> Async<string>   // channel → workspacePath → prompt text
    Config            : BotSharpConfig
    StreamHook        : AgentStreamHook
    Hook              : AgentHook                  // lifecycle callbacks (use AgentHook.none for no-op)
    CronService       : BotSharp.Infrastructure.Cron.CronService.CronService option
    LastTokenUsage    : TokenUsage option ref      // written after each LLM call; read by my tool
    CurrentIteration  : int ref                    // written at start of each AwaitingLLM step; read by my tool
    RuleEngine        : BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option
    FallbackProviders : LLMProvider list           // ordered fallback providers when primary fails
}

let private liftStorage (m: Async<Result<'a, StorageError>>) : AsyncResult<'a, AgentError> =
    async {
        let! r = m
        return Result.mapError AgentStorageFailure r
    }

let private liftLlm (m: Async<Result<'a, LlmError>>) : AsyncResult<'a, AgentError> =
    async {
        let! r = m
        return Result.mapError AgentLlmFailure r
    }

// ═══════════════════════════════════════════════════════════════════════════
// Tool dispatch
// ═══════════════════════════════════════════════════════════════════════════

/// Truncate a tool result content string if it exceeds config.MaxToolResultChars.
/// Matches Python nanobot's truncate_text behaviour (prefix + "\n... (truncated)").
let private truncateResult (maxChars: int) (text: string) : string =
    if maxChars <= 0 || text.Length <= maxChars then text
    else text.[..maxChars - 1] + "\n... (truncated)"

// ═══════════════════════════════════════════════════════════════════════════
// Oversized tool-result persistence
//
// Mirrors Python nanobot.utils.helpers.maybe_persist_tool_result.
// When a tool result exceeds MaxToolResultChars, the full text is written to
// {workspacePath}/tool-results/{sessionKey}/{toolCallId}.txt and the in-memory
// content is replaced with a short reference string + preview.  This keeps
// the context window manageable while preserving the full output on disk so
// the agent can read_file it if needed.
//
// Falls back to the original content if the write fails (disk full, etc.).
// ═══════════════════════════════════════════════════════════════════════════

let private _TOOL_RESULT_PREVIEW_CHARS  = 1200
let private _TOOL_RESULTS_DIR           = "tool-results"
let private _TOOL_RESULT_RETENTION_SECS = 86400.0    // 24 hours
let private _TOOL_RESULT_MAX_BUCKETS    = 10

/// Replace filesystem-unsafe characters with underscores.
let private safeFilename (name: string) : string =
    System.Text.RegularExpressions.Regex.Replace(name, @"[^\w\-\.]", "_")

/// Delete session-result buckets that are old or exceed the max-bucket count.
/// Best-effort: failures are silently swallowed (mirrors Python's try/except).
let private cleanupToolResultBuckets (root: string) (currentBucket: string) : unit =
    try
        let cutoffUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - int64 _TOOL_RESULT_RETENTION_SECS
        let mkUnix dir =
            try DateTimeOffset(System.IO.Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero).ToUnixTimeSeconds()
            with _ -> 0L
        let siblings =
            System.IO.Directory.GetDirectories(root)
            |> Array.filter ((<>) currentBucket)
        // Delete expired buckets.
        for dir in siblings do
            if mkUnix dir < cutoffUnix then
                System.IO.Directory.Delete(dir, true)
        // Trim to _TOOL_RESULT_MAX_BUCKETS total (keep newest).
        let keep = max (_TOOL_RESULT_MAX_BUCKETS - 1) 0
        let remaining =
            System.IO.Directory.GetDirectories(root)
            |> Array.filter ((<>) currentBucket)
            |> Array.sortByDescending mkUnix
        for dir in remaining |> Array.skip (min keep remaining.Length) do
            System.IO.Directory.Delete(dir, true)
    with _ -> ()

let private renderToolResultReference
    (path           : string)
    (originalSize   : int)
    (preview        : string)
    (truncatedPreview : bool)
    : string =
    let core = $"[tool output persisted]\nFull output saved to: {path}\nOriginal size: {originalSize} chars\nPreview:\n{preview}"
    if truncatedPreview then core + "\n...\n(Read the saved file if you need the full output.)"
    else core

/// Persist an oversized tool result to disk and return a reference + preview string.
/// Mirrors Python nanobot.utils.helpers.maybe_persist_tool_result.
/// No-op when workspacePath is empty, maxChars ≤ 0, or content fits within maxChars.
let maybePersistToolResult
    (workspacePath : string)
    (sessionKey    : string)
    (toolCallId    : ToolCallId)
    (content       : string)
    (maxChars      : int)
    : Async<string> =
    async {
        if workspacePath = "" || maxChars <= 0 || content.Length <= maxChars then
            return content
        else
            try
                let (ToolCallId callId) = toolCallId
                let root   = System.IO.Path.Combine(workspacePath, _TOOL_RESULTS_DIR)
                let bucket = System.IO.Path.Combine(root, safeFilename sessionKey)
                System.IO.Directory.CreateDirectory(bucket) |> ignore
                cleanupToolResultBuckets root bucket
                let path = System.IO.Path.Combine(bucket, safeFilename callId + ".txt")
                if not (System.IO.File.Exists path) then
                    // Atomic write via temp-then-move (mirrors Python _write_text_atomic).
                    let tmp = System.IO.Path.Combine(bucket,
                                  "." + safeFilename callId + "." + Guid.NewGuid().ToString("N") + ".tmp")
                    do! System.IO.File.WriteAllTextAsync(tmp, content) |> Async.AwaitTask
                    System.IO.File.Move(tmp, path)
                let previewLen  = min _TOOL_RESULT_PREVIEW_CHARS content.Length
                let preview     = content.[..previewLen - 1]
                let truncated   = content.Length > _TOOL_RESULT_PREVIEW_CHARS
                return renderToolResultReference path content.Length preview truncated
            with _ ->
                // Persist failed (disk full, permissions, etc.) — fall back gracefully.
                return content
    }

/// Replace empty or whitespace-only tool results with a short marker.
/// Mirrors Python runner.ensure_nonempty_tool_result — some providers
/// (e.g. Anthropic) reject tool results with empty content.
let ensureNonEmptyResult (toolName: ToolName) (result: ToolResult) : ToolResult =
    let (ToolName n) = toolName
    let placeholder  = $"({n} completed with no output)"
    match result with
    | ToolSuccess text when String.IsNullOrWhiteSpace text -> ToolSuccess placeholder
    | other -> other

// ─── External lookup throttle ────────────────────────────────────────────────
// Mirrors Python runner.repeated_external_lookup_error:
// identical web_fetch (same URL) and web_search (same query) calls are blocked
// after _MAX_REPEAT_EXTERNAL_LOOKUPS attempts within the same agent turn.
// Prevents infinite loops where the agent keeps fetching the same stale page.

let private _MAX_REPEAT_EXTERNAL_LOOKUPS = 2

/// Derive a stable de-duplication signature for external lookup calls.
/// Returns None for tools that are not subject to throttling.
let externalLookupSignature (call: ToolCall) : string option =
    let getStr key =
        call.Arguments |> Map.tryFind key
        |> Option.bind (fun el ->
            if el.ValueKind = System.Text.Json.JsonValueKind.String then
                match el.GetString() with
                | null | "" -> None
                | s         -> Some (s.Trim().ToLowerInvariant())
            else None)
    match call.Tool with
    | ToolName "web_fetch" ->
        getStr "url" |> Option.map (fun url -> $"web_fetch:{url}")
    | ToolName "web_search" ->
        (getStr "query" |> Option.orElse (getStr "search_term"))
        |> Option.map (fun q -> $"web_search:{q}")
    | _ -> None

/// Check whether this call is a repeated external lookup that should be blocked.
/// Mutates `counts` by incrementing the signature's count.
/// Returns Some ToolResult if the call should be blocked, None if it should proceed.
let private checkRepeatedExternalLookup
    (counts : Dictionary<string, int>)
    (call   : ToolCall)
    : ToolResult option =
    match externalLookupSignature call with
    | None -> None
    | Some lookupKey ->
        let current = if counts.ContainsKey(lookupKey) then counts[lookupKey] else 0
        counts[lookupKey] <- current + 1
        if current + 1 <= _MAX_REPEAT_EXTERNAL_LOOKUPS then
            None  // still within budget — proceed normally
        else
            Some (ToolFailure (ExecutionFailed
                "Error: repeated external lookup blocked. Use the results you already have to answer, or try a meaningfully different query or source."))

let private executeTool
    (deps      : AgentDependencies)
    (counts    : Dictionary<string, int>)
    (sessionId : SessionId)
    (call      : ToolCall)
    : Async<ToolCall * ToolResult> =
    async {
        // Short-circuit repeated external lookups before hitting the network.
        match checkRepeatedExternalLookup counts call with
        | Some blocked -> return (call, blocked)
        | None ->
        match deps.Tools.TryFind call.Tool with
        | None ->
            return (call, ToolFailure (ToolNotFound call.Tool))
        | Some (_, execute) ->
            let! result = execute call.Arguments
            // Persist oversized results to disk; replace with a short reference + preview.
            // Mirrors Python maybe_persist_tool_result (nanobot.utils.helpers).
            // This runs BEFORE truncation so the full content is written to disk.
            let! persistedResult =
                let (SessionId key) = sessionId
                match result with
                | ToolSuccess text ->
                    async {
                        let! persisted = maybePersistToolResult deps.Config.WorkspacePath key call.Id text deps.Config.MaxToolResultChars
                        return ToolSuccess persisted
                    }
                | other -> async { return other }
            // Cap tool result size to prevent context-window flooding.
            // Applies after persist so the reference string is also bounded.
            let capped =
                let cap = deps.Config.MaxToolResultChars
                match persistedResult with
                | ToolSuccess text       -> ToolSuccess (truncateResult cap text)
                | ToolFailure (ExecutionFailed msg) ->
                    ToolFailure (ExecutionFailed (truncateResult cap msg))
                | other -> other
            // Ensure non-empty content (Anthropic and some providers reject empty results).
            let safe = ensureNonEmptyResult call.Tool capped
            return (call, safe)
    }

/// Partition tool calls into execution batches, matching Python's _partition_tool_batches.
/// Consecutive concurrent-safe tools are grouped together (run in parallel).
/// Non-concurrent-safe tools always get their own batch (run exclusively/sequentially).
let partitionToolBatches (deps: AgentDependencies) (calls: ToolCall list) : ToolCall list list =
    let isSafe (call: ToolCall) =
        match deps.Tools |> Map.tryFind call.Tool with
        | Some (spec, _) -> spec.ConcurrencySafe
        | None -> false   // unknown tool — treat as non-safe
    let batches  = System.Collections.Generic.List<ToolCall list>()
    let current  = System.Collections.Generic.List<ToolCall>()
    for call in calls do
        if isSafe call then
            current.Add(call)
        else
            if current.Count > 0 then
                batches.Add(List.ofSeq current)
                current.Clear()
            batches.Add([call])
    if current.Count > 0 then
        batches.Add(List.ofSeq current)
    List.ofSeq batches

let private executeAllTools
    (deps      : AgentDependencies)
    (counts    : Dictionary<string, int>)
    (sessionId : SessionId)
    (calls     : ToolCall list)
    : Async<(ToolCall * ToolResult) list> =
    async {
        let batches = partitionToolBatches deps calls
        let results = System.Collections.Generic.List<ToolCall * ToolResult>()
        for batch in batches do
            // Concurrent-safe batches run in parallel; single-item batches run alone.
            let! batchResults = batch |> List.map (executeTool deps counts sessionId) |> Async.Parallel
            results.AddRange(batchResults)
        return List.ofSeq results
    }

let private allToolSpecs (deps: AgentDependencies) : ToolSpec list =
    deps.Tools |> Map.toList |> List.map (fun (_, (spec, _)) -> spec)

// ═══════════════════════════════════════════════════════════════════════════
// Context window trimming + micro-compaction
//
// Token estimation uses a character-count heuristic (4 chars ≈ 1 token for
// typical English text, matching BPE averages). This avoids a tiktoken
// dependency while providing a useful safety valve — the goal is to prevent
// context-window errors, not to compute exact billing tokens.
//
// Mirrors Python runner._trim_messages_to_fit:
//   • System messages are always kept.
//   • Oldest non-system messages are dropped first.
//   • The remaining window always starts with a user message.
//   • Budget = context_window_tokens - max_tokens - 1024 (safety buffer).
//
// Micro-compaction (mirrors Python runner._microcompact):
//   • For read-heavy tools (read_file, exec, grep, glob, web_fetch, web_search,
//     list_dir) keep the MICROCOMPACT_KEEP_RECENT most recent full results.
//   • Older results with ≥ MICROCOMPACT_MIN_CHARS are replaced with a
//     one-line "[{tool} result omitted from context]" placeholder.
//   • Applied before context-window trimming so the budget benefits immediately.
// ═══════════════════════════════════════════════════════════════════════════

let private _SNIP_BUFFER = 1024

/// Tools whose results are worth compressing when they become stale in context.
/// Mirrors Python's _COMPACTABLE_TOOLS set.
let private compactableTools =
    Set.ofList [ ToolName "read_file"; ToolName "exec";       ToolName "grep"
                 ToolName "glob";      ToolName "web_fetch";  ToolName "web_search"
                 ToolName "list_dir" ]

let private _MICROCOMPACT_KEEP_RECENT = 10   // keep N most-recent full results per tool
let private _MICROCOMPACT_MIN_CHARS   = 500  // only compact results ≥ this length

/// Replace stale large tool results with one-line placeholders to save tokens.
/// Keeps the MICROCOMPACT_KEEP_RECENT most-recent full results per tool name.
/// Mirrors Python runner._microcompact.
let microcompact (messages: Message list) : Message list =
    // Count how many times each compactable tool appears (oldest-first scan)
    let toolCounts =
        messages |> List.choose (function
            | ToolResultMessage (_, name, _) when compactableTools.Contains name -> Some name
            | _ -> None)
        |> List.countBy id
        |> Map.ofList

    // If every compactable tool appears ≤ KEEP_RECENT times, nothing to do.
    if toolCounts |> Map.forall (fun _ count -> count <= _MICROCOMPACT_KEEP_RECENT) then
        messages
    else
        // Track how many recent occurrences we've seen (counted from the END).
        // We do a single right-to-left pass: recentSeen[name] = occurrences seen so far
        // from the right. The Nth from the right (N > KEEP_RECENT) becomes a placeholder.
        // Uses List.mapFold to properly thread the accumulator state — capturing
        // `let mutable` inside a List.map lambda is unreliable in F#.
        let (reversedResult, _) =
            messages
            |> List.rev
            |> List.mapFold (fun (seenMap: Map<ToolName, int>) msg ->
                match msg with
                | ToolResultMessage (id, name, content) when
                    compactableTools.Contains name &&
                    content.Length >= _MICROCOMPACT_MIN_CHARS ->
                    let seen    = seenMap |> Map.tryFind name |> Option.defaultValue 0
                    let seenMap' = seenMap |> Map.add name (seen + 1)
                    if seen >= _MICROCOMPACT_KEEP_RECENT then
                        let (ToolName n) = name
                        (ToolResultMessage (id, name, $"[{n} result omitted from context]"), seenMap')
                    else
                        (ToolResultMessage (id, name, content), seenMap')
                | other -> (other, seenMap)
            ) Map.empty
        List.rev reversedResult

// ═══════════════════════════════════════════════════════════════════════════
// Tool-result budget enforcement (applied after micro-compaction)
//
// Mirrors Python runner._apply_tool_result_budget:
//   Re-cap every ToolResultMessage in the history to MaxToolResultChars.
//   Necessary because the snapshot may have been written in a prior session
//   with a different (larger) config, or the config was changed after the
//   result was stored.  Applying the cap again here is cheap and idempotent.
//   Applied AFTER microcompact so compact placeholders (which are short) are
//   never accidentally re-truncated.
// ═══════════════════════════════════════════════════════════════════════════

/// Re-apply MaxToolResultChars to every ToolResultMessage in the list.
/// Mirrors Python runner._apply_tool_result_budget.
/// No-op when maxChars ≤ 0 (truncation disabled).
let applyToolResultBudget (maxChars: int) (messages: Message list) : Message list =
    if maxChars <= 0 then messages
    else
        messages |> List.map (function
            | ToolResultMessage (id, name, content) ->
                ToolResultMessage (id, name, truncateResult maxChars content)
            | other -> other)

/// Rough token estimate via character count heuristic (4 chars ≈ 1 token).
let estimateTokens (text: string) : int = max 1 (text.Length / 4)

/// Estimate the token cost of a single message.
let messageTokens (msg: Message) : int =
    match msg with
    | SystemMessage s            -> estimateTokens s + 4
    | UserMessage (s, _)         -> estimateTokens s + 4
    | AssistantMessage (s, rcOpt) ->
        let rcTokens = rcOpt |> Option.map estimateTokens |> Option.defaultValue 0
        estimateTokens s + rcTokens + 4
    | ToolCallMessage (calls, _) ->
        calls |> NonEmptyList.toList
        |> List.sumBy (fun c ->
            let args = c.Arguments |> Map.toList |> List.sumBy (fun (k,v) -> k.Length + 10 + v.ToString().Length)
            4 + args)
    | ToolResultMessage (_, _, s) -> estimateTokens s + 4

/// Trim messages to fit within the context window budget.
/// Keeps system messages; drops oldest non-system messages first.
/// Returns messages unchanged if ContextWindowTokens = 0 and no contextBlockLimit.
/// contextBlockLimit (when Some) directly overrides the computed budget (mirrors Python context_block_limit).
let trimToContextWindow (contextWindowTokens: int) (maxTokens: int) (contextBlockLimit: int option) (messages: Message list) : Message list =
    let budget =
        match contextBlockLimit with
        | Some limit -> limit   // explicit override takes precedence (Python parity)
        | None ->
            if contextWindowTokens <= 0 then 0   // 0 = no trimming
            else contextWindowTokens - maxTokens - _SNIP_BUFFER
    if budget <= 0 then messages
        else
            let totalEst = messages |> List.sumBy messageTokens
            if totalEst <= budget then messages
            else
                let system    = messages |> List.filter (fun m -> match m with SystemMessage _ -> true | _ -> false)
                let nonSystem = messages |> List.filter (fun m -> match m with SystemMessage _ -> false | _ -> true)
                let sysBudget = system |> List.sumBy messageTokens
                let remaining = max 128 (budget - sysBudget)
                // Keep most-recent messages that fit within remaining budget
                let mutable kept = []
                let mutable used = 0
                for msg in List.rev nonSystem do
                    let cost = messageTokens msg
                    if kept.IsEmpty || used + cost <= remaining then
                        kept <- msg :: kept
                        used <- used + cost
                // Ensure window starts with a user message
                let keepFromUser =
                    kept |> List.tryFindIndex (fun m -> match m with UserMessage _ -> true | _ -> false)
                let trimmed =
                    match keepFromUser with
                    | None   -> kept |> List.tryLast |> Option.map List.singleton |> Option.defaultValue []
                    | Some i -> kept |> List.skip i
                system @ trimmed

// ═══════════════════════════════════════════════════════════════════════════
// Message-list sanitization (applied after trimming, before LLM call)
//
// Mirrors Python runner._drop_orphan_tool_results and
// runner._backfill_missing_tool_results, which guard against provider
// API rejections that occur when:
//   • A ToolResultMessage exists with no prior ToolCallMessage for that id
//     (can happen when context-window trimming drops the tool-call turn).
//   • A ToolCallMessage exists with no subsequent ToolResultMessage
//     (can happen if a turn was interrupted mid-execution).
// ═══════════════════════════════════════════════════════════════════════════

/// Drop ToolResultMessages that have no matching ToolCallMessage earlier in
/// the list.  Mirrors Python runner._drop_orphan_tool_results.
let dropOrphanToolResults (messages: Message list) : Message list =
    // Collect all call IDs declared by ToolCallMessages seen so far.
    let mutable declared : Set<ToolCallId> = Set.empty
    let mutable hasOrphan = false
    for msg in messages do
        match msg with
        | ToolCallMessage (nel, _) ->
            for call in NonEmptyList.toList nel do
                declared <- declared.Add(call.Id)
        | ToolResultMessage (id, _, _) when not (declared.Contains id) ->
            hasOrphan <- true
        | _ -> ()
    if not hasOrphan then messages
    else
        let mutable seen : Set<ToolCallId> = Set.empty
        messages |> List.filter (fun msg ->
            match msg with
            | ToolCallMessage (nel, _) ->
                for call in NonEmptyList.toList nel do
                    seen <- seen.Add(call.Id)
                true
            | ToolResultMessage (id, _, _) -> seen.Contains id
            | _ -> true)

/// Insert a "[Tool result unavailable]" placeholder for any ToolCallMessage
/// whose tool IDs have no subsequent ToolResultMessage.
/// Mirrors Python runner._backfill_missing_tool_results.
let backfillMissingToolResults (messages: Message list) : Message list =
    let _BACKFILL_CONTENT = "[Tool result unavailable — call was interrupted or lost]"

    // Pass 1: collect all call IDs and which are fulfilled.
    let mutable allCalls    : (int * ToolCall) list = []
    let mutable fulfilled   : Set<ToolCallId>       = Set.empty
    let mutable idx = 0
    for msg in messages do
        match msg with
        | ToolCallMessage (nel, _) ->
            for call in NonEmptyList.toList nel do
                allCalls <- allCalls @ [(idx, call)]
        | ToolResultMessage (id, _, _) ->
            fulfilled <- fulfilled.Add(id)
        | _ -> ()
        idx <- idx + 1

    let missing = allCalls |> List.filter (fun (_, call) -> not (fulfilled.Contains call.Id))
    if missing.IsEmpty then messages
    else
        // Insert synthetic ToolResultMessage immediately after the turn that
        // contains the orphaned ToolCallMessage (after any existing results).
        let arr = Array.ofList messages
        // Build insertion points: msgIdx → list of missing calls in that turn
        let byMsg =
            missing
            |> List.groupBy fst
            |> Map.ofList

        // Walk forward and collect with insertions
        let result = System.Collections.Generic.List<Message>()
        let mutable i = 0
        for msg in arr do
            result.Add(msg)
            // After each ToolCallMessage, insert placeholders for missing results
            match msg with
            | ToolCallMessage _ ->
                match byMsg.TryFind i with
                | Some calls ->
                    for (_, call) in calls do
                        result.Add(ToolResultMessage (call.Id, call.Tool, _BACKFILL_CONTENT))
                | None -> ()
            | _ -> ()
            i <- i + 1
        List.ofSeq result

// ═══════════════════════════════════════════════════════════════════════════
// Role-alternation enforcement (applied after trimming, before LLM call)
//
// Mirrors Python providers.base.LLMProvider._enforce_role_alternation:
//   • Merge consecutive user messages into one (content concatenated).
//   • Merge consecutive assistant text messages into one.
//   • Drop trailing AssistantMessage turns (prefill not supported by most
//     providers; trailing ToolCallMessage is kept because tool results follow).
//   • If dropping trailing assistant messages leaves only SystemMessage entries,
//     recover by converting the last dropped assistant turn into a UserMessage
//     so the request remains valid.
// ═══════════════════════════════════════════════════════════════════════════

/// Synthetic user message inserted when a leading-assistant patch is needed.
/// Mirrors Python providers.base._SYNTHETIC_USER_CONTENT.
let [<Literal>] private syntheticUserContent = "(conversation continued)"

/// Merge consecutive same-role user/assistant messages and drop trailing
/// plain AssistantMessages.  Mirrors Python LLMProvider._enforce_role_alternation.
let enforceRoleAlternation (messages: Message list) : Message list =
    if messages.IsEmpty then messages
    else
        // Phase 1: merge consecutive same-role user/assistant messages.
        let merged =
            let acc = System.Collections.Generic.List<Message>()
            for msg in messages do
                match msg, (if acc.Count > 0 then Some acc.[acc.Count - 1] else None) with
                // Consecutive UserMessages — concatenate text content.
                | UserMessage (curr, _), Some (UserMessage (prev, prevMedia)) ->
                    let combined = if curr = "" then prev elif prev = "" then curr else prev + "\n\n" + curr
                    acc.[acc.Count - 1] <- UserMessage (combined, prevMedia)
                // Consecutive AssistantMessages (plain text) — concatenate.
                | AssistantMessage (curr, currRc), Some (AssistantMessage (prev, prevRc)) ->
                    let combined = if curr = "" then prev elif prev = "" then curr else prev + "\n\n" + curr
                    let rc = match currRc with Some _ -> currRc | None -> prevRc
                    acc.[acc.Count - 1] <- AssistantMessage (combined, rc)
                // All other messages are appended as-is (system, tool-call, tool-result, etc.)
                | other, _ ->
                    acc.Add(other)
            List.ofSeq acc

        // Phase 2: drop trailing plain AssistantMessages (prefill not supported).
        let mutable lastPopped : Message option = None
        let mutable tail = List.rev merged
        while (match tail with AssistantMessage _ :: _ -> true | _ -> false) do
            match tail with
            | AssistantMessage _ as m :: rest ->
                lastPopped <- Some m
                tail <- rest
            | _ -> ()
        let trimmed = List.rev tail

        // Phase 3: recover if we stripped all non-system messages AND the list is non-empty.
        // Without this, some providers (e.g. Zhipu/GLM) reject system-only requests.
        // Guard: only recover when trimmed is non-empty (i.e. system messages remain).
        // If trimmed is empty (all messages were assistant), return [] — no recovery.
        let sysOnlyRemains =
            not trimmed.IsEmpty &&
            trimmed |> List.forall (fun m -> match m with SystemMessage _ -> true | _ -> false)
        let afterPhase3 =
            if sysOnlyRemains then
                match lastPopped with
                | Some (AssistantMessage (text, _)) ->
                    // Convert last dropped assistant into a user message so request is valid.
                    trimmed @ [ UserMessage (text, []) ]
                | _ -> trimmed
            else
                trimmed

        // Phase 4: safety net — if the first non-system message is a bare AssistantMessage
        // (no tool_calls), insert a synthetic user message before it.
        // Providers like GLM reject system→assistant with error 1214.
        // Mirrors Python: `merged.insert(i, {"role": "user", "content": _SYNTHETIC_USER_CONTENT})`.
        let firstNonSystemIdx =
            afterPhase3
            |> List.tryFindIndex (fun m -> match m with SystemMessage _ -> false | _ -> true)
        match firstNonSystemIdx with
        | Some idx when (match afterPhase3.[idx] with AssistantMessage _ -> true | _ -> false) ->
            let before = List.take idx afterPhase3
            let after  = List.skip idx afterPhase3
            before @ [ UserMessage (syntheticUserContent, []) ] @ after
        | _ ->
            afterPhase3

// stripThink is imported from BotSharp.Infrastructure.Shared.StringUtils (see `open` above).
// It removes <think>…</think> / <thought>…</thought> reasoning blocks before text
// is shown to the user or persisted to history.
// Mirrors Python nanobot.utils.helpers.strip_think.

// ═══════════════════════════════════════════════════════════════════════════
// Provider retry wrapper
//
// Mirrors Python provider.chat_with_retry — applies the RetryPolicy attached
// to the LLMProvider so that transient errors (rate-limit 429, server 5xx)
// are retried with the configured backoff schedule.
//
// Non-retryable errors (auth failure, bad request, quota exceeded) propagate
// immediately.  Streaming retry is not implemented here: retrying a partial
// stream would re-emit duplicate content to the user.
// ═══════════════════════════════════════════════════════════════════════════

/// Invoke provider.Chat with retry according to provider.RetryPolicy.
/// Mirrors Python LLMProvider.chat_with_retry.
/// Try a single provider with its retry policy.
let private chatWithRetrySingle
    (provider : LLMProvider)
    (settings : GenerationSettings)
    (messages : Message list)
    (tools    : ToolSpec list)
    : Async<Result<LLMResponse, LlmError>> =
    let delays =
        match provider.RetryPolicy.Mode with
        | FixedRetries (_, ds) -> ds |> List.map (fun d -> int d.TotalMilliseconds)
        | Persistent limit ->
            [ for i in 0..9 -> min (1000 * (pown 2 i)) (min 30000 (int limit.TotalMilliseconds / 2)) ]
    let rec go remaining remainingDelays =
        async {
            let! result = provider.Chat settings messages tools
            match result, remaining, remainingDelays with
            | Error err, left, delay :: rest when LlmError.shouldRetry err && left > 0 ->
                let waitMs =
                    match err.Kind with
                    | RateLimited (Some after) -> max (int after.TotalMilliseconds) delay
                    | _                        -> delay
                do! Async.Sleep waitMs
                return! go (left - 1) rest
            | _ -> return result
        }
    go (List.length delays) delays

/// Try the primary provider, then fallback providers in order.
/// Each provider gets its full retry budget before moving to the next.
let chatWithRetry
    (primary   : LLMProvider)
    (fallbacks : LLMProvider list)
    (settings  : GenerationSettings)
    (messages  : Message list)
    (tools     : ToolSpec list)
    : Async<Result<LLMResponse, LlmError>> =
    async {
        let! result = chatWithRetrySingle primary settings messages tools
        match result with
        | Ok _ -> return result
        | Error primaryErr ->
            let rec tryFallbacks remaining =
                async {
                    match remaining with
                    | [] -> return Error primaryErr
                    | fb :: rest ->
                        let primaryId = (primary : LLMProvider).Id
                        let fbId = (fb : LLMProvider).Id
                        eprintfn "[Fallback] Primary provider '%s' failed, trying '%s'" primaryId fbId
                        let! fbResult = chatWithRetrySingle fb settings messages tools
                        match fbResult with
                        | Ok _ -> return fbResult
                        | Error _ -> return! tryFallbacks rest
                }
            if fallbacks.IsEmpty then return result
            else return! tryFallbacks fallbacks
    }

// ═══════════════════════════════════════════════════════════════════════════
// Main agent loop (state-machine driven)
// ═══════════════════════════════════════════════════════════════════════════

let private _MAX_LENGTH_RECOVERIES = 3
let private _LENGTH_RECOVERY_PROMPT =
    "Output limit reached. Continue exactly where you left off \
— no recap, no apology. Break remaining work into smaller steps if needed."

/// Parity with Python _MAX_EMPTY_RETRIES.
/// How many times to silently retry when the LLM returns whitespace-only text.
let private _MAX_EMPTY_RETRIES = 2

/// Prompt appended for finalization retries (blank text or empty body).
/// Mirrors Python build_finalization_retry_message().
let private _FINALIZATION_PROMPT =
    "Please provide your response to the user based on the conversation above."

/// Run one complete agent turn starting from the AwaitingLLM state.
/// Returns (finalText, updatedSnapshot).
/// `iterIdx` is the zero-based iteration counter (for AgentHookContext).
/// `lengthRecoveries` counts how many length-recovery continuations have occurred
/// in this turn so far; reset to 0 at the start of each fresh tool round.
/// `emptyContentRetries` counts blank-text responses retried without a new tool round;
/// reset to 0 when entering a new tool round (mirrors Python empty_content_retries).
/// `externalLookupCounts` tracks how many times each external lookup signature
/// has been called; shared across all rounds in the same agent turn.
let rec private iterate
    (deps                  : AgentDependencies)
    (snap                  : SessionSnapshot)
    (state                 : AgentState)
    (iterIdx               : int)
    (lengthRecoveries      : int)
    (emptyContentRetries   : int)
    (externalLookupCounts  : Dictionary<string, int>)
    : AsyncResult<string * SessionSnapshot, AgentError> =
    asyncResult {
        match state with

        | AwaitingLLM (req, _iter) ->
            // Update iteration counter so MyTool._current_iteration reflects current step.
            deps.CurrentIteration.Value <- iterIdx
            let allSpecs = allToolSpecs deps
            // Pipeline order matches Python runner (two-pass orphan repair):
            //   1. dropOrphanToolResults (pass 1)  — clean up before microcompact
            //   2. backfillMissingToolResults (pass 1) — add synthetic results for unanswered calls
            //   3. microcompact  — replace stale large results with placeholders
            //   4. applyToolResultBudget — re-cap all tool result sizes
            //   5. trimToContextWindow — drop oldest messages to fit token budget
            //   6. dropOrphanToolResults (pass 2) — snipping may create new orphans
            //   7. backfillMissingToolResults (pass 2) — cover calls left without results by snip
            //   8. enforceRoleAlternation — merge consecutive same-role; drop trailing assistant
            let trimmedMessages =
                req.Messages
                |> dropOrphanToolResults
                |> backfillMissingToolResults
                |> microcompact
                |> applyToolResultBudget deps.Config.MaxToolResultChars
                |> trimToContextWindow deps.Config.ContextWindowTokens deps.Config.MaxTokens deps.Config.ContextBlockLimit
                |> dropOrphanToolResults
                |> backfillMissingToolResults
                |> enforceRoleAlternation   // merge consecutive same-role messages; drop trailing assistant
            let fullReq  = { req with
                                Messages = trimmedMessages
                                Tools    = allSpecs
                                Model    = deps.Config.DefaultModel
                                Settings = { req.Settings with
                                                Temperature     = deps.Config.Temperature
                                                MaxTokens       = deps.Config.MaxTokens
                                                ReasoningEffort = deps.Config.ReasoningEffort } }

            // Create per-iteration hook context and fire BeforeIteration.
            let hookCtx = AgentHook.mkContext iterIdx req.Messages
            do! deps.Hook.BeforeIteration hookCtx |> AsyncResult.ofAsync

            let! response =
                match deps.StreamHook with
                | NoStreaming ->
                    // Non-streaming: apply retry policy (mirrors Python chat_with_retry).
                    liftLlm (chatWithRetry deps.Provider deps.FallbackProviders fullReq.Settings fullReq.Messages fullReq.Tools)

                | StreamingHook (onDelta, onStreamEnd) ->
                    asyncResult {
                        let mutable textAcc        = ""
                        let mutable thinkingAcc    = ""
                        let mutable streamUsage    = { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }
                        let mutable streamFinishReason : FinishReason option = None
                        // Per-wire-index tool call buffers: index → (id, name, accumulated args JSON)
                        let toolBuffers = Dictionary<int, ToolCallId * ToolName * string>()

                        let emitter evt =
                            async {
                                match evt with
                                | ContentDelta (TextDelta t) ->
                                    textAcc <- textAcc + t
                                    do! onDelta t
                                    // Notify the hook of each streaming delta.
                                    do! deps.Hook.OnStream hookCtx t
                                | ContentDelta (ThinkingDelta t) ->
                                    thinkingAcc <- thinkingAcc + t
                                | ContentDelta (ToolArgDelta (idx, chunk)) ->
                                    match toolBuffers.TryGetValue(idx) with
                                    | true, (id, name, buf) ->
                                        toolBuffers[idx] <- (id, name, buf + chunk)
                                    | false, _ -> ()   // orphaned arg chunk before ToolCallStarted — ignore
                                | ToolCallStarted (idx, id, name) ->
                                    match toolBuffers.TryGetValue(idx) with
                                    | true, (_, _, existingBuf) ->
                                        toolBuffers[idx] <- (id, name, existingBuf)
                                    | false, _ ->
                                        toolBuffers[idx] <- (id, name, "")
                                // Explicit cases instead of | _ -> () so FS0025 catches future StreamEvent additions
                                | StreamError _       -> ()   // non-fatal: adapter already continued reading
                                | ToolCallCompleted _ -> ()   // not emitted by current OpenAI-compat provider
                                | StreamCompleted r   -> streamUsage <- r.Usage   // capture token counts from final chunk
                                | StreamFinished reason ->
                                    // Stop chunk — capture finish_reason for length-recovery check.
                                    streamFinishReason <-
                                        match reason with
                                        | "stop"           -> Some Stop
                                        | "length"         -> Some Length
                                        | "tool_calls"     -> Some ToolCalls
                                        | "content_filter" -> Some ContentFilter
                                        | other            -> Some (OtherReason other)
                            }

                        do! liftLlm (deps.Provider.ChatStream fullReq.Settings fullReq.Messages fullReq.Tools emitter)

                        let reasoningOpt = if thinkingAcc = "" then None else Some thinkingAcc
                        let textOpt      = if textAcc = "" then None else Some textAcc

                        let body =
                            if toolBuffers.Count = 0 then
                                if textAcc = "" then Empty else TextOnly textAcc
                            else
                                let calls =
                                    toolBuffers
                                    |> Seq.sortBy (fun kv -> kv.Key)
                                    |> Seq.map (fun kv ->
                                        let (id, name, argsRaw) = kv.Value
                                        let arguments =
                                            try
                                                if argsRaw = "" then Map.empty
                                                else
                                                    use doc = JsonDocument.Parse(argsRaw)
                                                    if doc.RootElement.ValueKind = JsonValueKind.Object then
                                                        doc.RootElement.EnumerateObject()
                                                        |> Seq.map (fun p -> p.Name, p.Value.Clone())
                                                        |> Map.ofSeq
                                                    else Map.empty
                                            with :? JsonException -> Map.empty   // truncated stream — degrade gracefully
                                        { Id = id; Tool = name; Arguments = arguments; ProviderMeta = None })
                                    |> Seq.toList
                                match NonEmptyList.ofList calls with
                                | Ok nel  -> WithToolCalls (textOpt, nel)
                                | Error _ -> if textAcc = "" then Empty else TextOnly textAcc

                        return { Body             = body
                                 ReasoningContent  = reasoningOpt
                                 ThinkingBlocks    = []
                                 Usage             = streamUsage
                                 FinishReason      = streamFinishReason }
                    }

            // Record token usage for the my tool's _last_usage key.
            deps.LastTokenUsage.Value <- Some response.Usage

            // Assert LLM response fact into rule engine.
            match deps.RuleEngine with
            | None -> ()
            | Some engine ->
                let status =
                    match response.Body with
                    | Empty -> "empty"
                    | _     -> "ok"
                let finishReason =
                    match response.FinishReason with
                    | Some Stop         -> "stop"
                    | Some Length       -> "length"
                    | Some ContentFilter -> "content_filter"
                    | Some ToolCalls    -> "tool_calls"
                    | _                 -> ""
                let totalTokens = response.Usage.PromptTokens + response.Usage.CompletionTokens
                BotSharp.Infrastructure.Rules.RuleEngine.assertLlmResponse
                    engine status "" finishReason totalTokens _iter

            // Populate the hook context with the LLM response.
            hookCtx.Response <- Some response
            match response.Body with
            | WithToolCalls (_, nel) ->
                hookCtx.ToolCalls <- NonEmptyList.toList nel
            | _ -> ()

            // Signal stream-end; no-op when NoStreaming.
            let notifyStreamEnd hasTools =
                async {
                    match deps.StreamHook with
                    | NoStreaming               -> ()
                    | StreamingHook (_, onEnd)  -> do! onEnd hasTools
                    do! deps.Hook.OnStreamEnd hookCtx hasTools
                }

            match response.Body with
            | Empty ->
                // An empty response body (HTTP 200, zero tokens).
                // Common cause: provider doesn't support stream_options.include_usage.
                // Recovery: append a finalization prompt and retry once (non-streaming).
                // Mirrors Python runner._request_finalization_retry.
                let retryMessages = trimmedMessages @ [ UserMessage (_FINALIZATION_PROMPT, []) ]
                let retrySettings = fullReq.Settings  // same settings
                let! retryResp = liftLlm (chatWithRetry deps.Provider deps.FallbackProviders retrySettings retryMessages [])
                match retryResp.Body with
                | TextOnly text when text.Trim() <> "" ->
                    // Recovery succeeded — treat as a normal text response.
                    do! notifyStreamEnd false |> AsyncResult.ofAsync
                    do! deps.Hook.AfterIteration hookCtx |> AsyncResult.ofAsync
                    return! iterate deps snap (transition state (LlmRespondedWithText (text, retryResp.ReasoningContent))) (iterIdx + 1) lengthRecoveries 0 externalLookupCounts
                | _ ->
                    // Still empty (or a different non-text body) after retry — fail.
                    let emptyErr =
                        { Kind         = EmptyResponse "请检查 base_url、model 名称和 API Key 是否与当前 provider 匹配"
                          RawMessage   = "stream produced no content (Empty body); finalization retry also failed"
                          ProviderCode = None }
                    return! AsyncResult.ofResult (Error (AgentLlmFailure emptyErr))

            | TextOnly rawText ->
                // Strip reasoning-trace blocks before any further processing.
                // Mirrors Python runner._strip_think applied to TextOnly content.
                // Done here (not in Finalizing) so the empty-retry and length-recovery
                // checks see the stripped text — a think-only response should be
                // treated as empty and trigger the empty-content retry.
                let text = stripThink rawText
                // Empty-response retry: if the LLM returns blank text (whitespace only),
                // append a finalization prompt and retry non-streaming (mirrors Python
                // runner._request_finalization_retry for the empty_content_retries path).
                if text.Trim() = "" && emptyContentRetries < _MAX_EMPTY_RETRIES then
                    do! deps.Hook.AfterIteration hookCtx |> AsyncResult.ofAsync
                    let retryMessages = trimmedMessages @ [ UserMessage (_FINALIZATION_PROMPT, []) ]
                    let! retryResp = liftLlm (chatWithRetry deps.Provider deps.FallbackProviders fullReq.Settings retryMessages [])
                    let retryText = stripThink (match retryResp.Body with TextOnly t -> t | _ -> "")
                    if retryText.Trim() <> "" then
                        // Finalization retry recovered a non-blank response — proceed normally.
                        return! iterate deps snap (transition state (LlmRespondedWithText (retryText, retryResp.ReasoningContent))) (iterIdx + 1) lengthRecoveries 0 externalLookupCounts
                    else
                        // Still blank — try again (increments counter for next pass).
                        return! iterate deps snap (AwaitingLLM (req, _iter)) (iterIdx + 1) lengthRecoveries (emptyContentRetries + 1) externalLookupCounts
                // Length recovery: if the provider stopped because max_tokens was reached,
                // append the partial response + a continuation prompt and loop.
                // Mirrors Python runner's length_recovery_count / _MAX_LENGTH_RECOVERIES logic.
                elif response.FinishReason = Some Length && text.Trim() <> "" && lengthRecoveries < _MAX_LENGTH_RECOVERIES then
                    do! deps.Hook.AfterIteration hookCtx |> AsyncResult.ofAsync
                    let partialMsg    = AssistantMessage (text, response.ReasoningContent)
                    let recoveryMsg   = UserMessage (_LENGTH_RECOVERY_PROMPT, [])
                    let extMessages   = trimmedMessages @ [ partialMsg; recoveryMsg ]
                    let extReq        = { req with Messages = extMessages }
                    let recoveryState = AwaitingLLM (extReq, _iter + 1)
                    return! iterate deps snap recoveryState (iterIdx + 1) (lengthRecoveries + 1) 0 externalLookupCounts
                else
                    // In streaming: prints the trailing newline after the last token.
                    // In non-streaming: no-op (text will be shown by startCli via PlainResponse).
                    do! notifyStreamEnd false |> AsyncResult.ofAsync
                    do! deps.Hook.AfterIteration hookCtx |> AsyncResult.ofAsync
                    return! iterate deps snap (transition state (LlmRespondedWithText (text, response.ReasoningContent))) (iterIdx + 1) 0 0 externalLookupCounts

            | WithToolCalls (prefixText, nel) ->
                // should_execute_tools gate (mirrors Python's LLMResponse.should_execute_tools #3220):
                // Only execute tool calls when finish_reason is ToolCalls, Stop, or None (unset).
                // Gateway-injected calls under ContentFilter / refusal / error are blocked here
                // to prevent infinite loops when the provider refuses to honour the calls.
                let shouldExecute =
                    match response.FinishReason with
                    | None | Some Stop | Some ToolCalls -> true
                    | _ -> false

                if not shouldExecute then
                    // Treat as a plain text response (use prefix text if any, or empty string).
                    let text = prefixText |> Option.defaultValue ""
                    do! notifyStreamEnd false |> AsyncResult.ofAsync
                    do! deps.Hook.AfterIteration hookCtx |> AsyncResult.ofAsync
                    return! iterate deps snap (transition state (LlmRespondedWithText (text, response.ReasoningContent))) (iterIdx + 1) 0 0 externalLookupCounts
                else

                // Streaming: prefix text was already printed token-by-token by the emitter;
                //            do NOT re-emit it here — that would double-print.
                // Non-streaming: prefix text before tool calls is not surfaced at this layer;
                //                the final assistant reply is returned as PlainResponse.
                // onStreamEnd true is currently a no-op in CliChannel (only false prints "\n").
                do! notifyStreamEnd true |> AsyncResult.ofAsync
                do! deps.Hook.AfterIteration hookCtx |> AsyncResult.ofAsync
                // Persist the ToolCallMessage to the snapshot so subsequent turns can see it.
                // Mirrors Python's _save_turn which saves assistant+tool_calls messages.
                let snap' = SessionSnapshot.append (ToolCallMessage (nel, response.ReasoningContent)) snap
                return! iterate deps snap' (transition state (LlmRespondedWithTools (nel, response.ReasoningContent))) (iterIdx + 1) 0 0 externalLookupCounts

        | ExecutingTools (_, _, iter) when iter >= deps.Config.MaxIterations ->
            // Max iterations reached: go directly to Finalizing rather than through the
            // state machine's ToolsExecuted path (which would do another LLM call with
            // pending tool_calls but no tool_result messages — an invalid API request).
            // MaxIterationsMessage (when set) is a custom string; {maxIterations} is substituted.
            // Mirrors Python's spec.max_iterations_message.format(max_iterations=N) logic.
            let maxMsg =
                match deps.Config.MaxIterationsMessage with
                | Some tmpl -> tmpl.Replace("{maxIterations}", string deps.Config.MaxIterations)
                | None      -> $"(stopped after {iter} iterations)"
            return! iterate deps snap (Finalizing (maxMsg, None)) iterIdx 0 0 externalLookupCounts

        | ExecutingTools (nel, _, _) ->
            // Fire BeforeExecuteTools before dispatching tool calls.
            let hookCtx = AgentHook.mkContext iterIdx (SessionSnapshot.messages snap)
            hookCtx.ToolCalls <- NonEmptyList.toList nel
            do! deps.Hook.BeforeExecuteTools hookCtx |> AsyncResult.ofAsync
            let! results = executeAllTools deps externalLookupCounts (SessionSnapshot.id snap) (NonEmptyList.toList nel) |> AsyncResult.ofAsync
            hookCtx.ToolResults <- results
            // fail_on_tool_error: when enabled, any ToolFailure immediately aborts the loop.
            // Mirrors Python's spec.fail_on_tool_error which raises an exception on first error.
            if deps.Config.FailOnToolError then
                match results |> List.tryPick (fun (_, r) -> match r with ToolFailure e -> Some e | _ -> None) with
                | Some err -> return! AsyncResult.ofResult (Error (AgentToolFailure err))
                | None     -> ()
            // Persist tool result messages to the snapshot so subsequent turns can reference them.
            // Mirrors Python's _save_turn which saves tool-role messages with content truncation.
            let snap' =
                results |> List.fold (fun s (call, res) ->
                    let content =
                        match res with
                        | ToolSuccess c -> c
                        | ToolFailure e ->
                            match e with
                            | ToolNotFound (ToolName n) -> $"[Tool not found: {n}]"
                            | ParameterMissing f        -> $"[Missing parameter: {f}]"
                            | ParameterInvalid (f, r)   -> $"[Invalid parameter {f}: {r}]"
                            | ExecutionFailed msg       -> $"[Tool failed: {msg}]"
                            | ExecutionTimeout t        -> $"[Tool timed out after {t.TotalSeconds}s]"
                            | WorkspaceViolation p      -> $"[Access denied: {p}]"
                    SessionSnapshot.append (ToolResultMessage (call.Id, call.Tool, content)) s
                ) snap
            // Rule engine: assert tool results and check for triggered actions.
            let ruleStop =
                match deps.RuleEngine with
                | None -> None
                | Some engine ->
                    let iter = match state with ExecutingTools (_, _, i) -> i | _ -> 0
                    results |> List.iter (fun (call, result) ->
                        let (ToolName toolName) = call.Tool
                        let status, errorStr =
                            match result with
                            | ToolSuccess _ -> "success", ""
                            | ToolFailure e ->
                                match e with
                                | ToolNotFound (ToolName n)  -> "failure", $"Tool not found: {n}"
                                | ParameterMissing f         -> "failure", $"Missing parameter: {f}"
                                | ParameterInvalid (f, r)    -> "failure", $"Invalid parameter {f}: {r}"
                                | ExecutionFailed msg        -> "failure", msg
                                | ExecutionTimeout t         -> "failure", $"Timed out after {t.TotalSeconds}s"
                                | WorkspaceViolation p       -> "failure", $"Access denied: {p}"
                        BotSharp.Infrastructure.Rules.RuleEngine.assertToolResult engine toolName status errorStr iter
                        // Also assert tool-timeout fact for timeout-specific rules
                        match result with
                        | ToolFailure (ExecutionTimeout t) ->
                            BotSharp.Infrastructure.Rules.RuleEngine.assertToolTimeout engine toolName iter t.TotalSeconds
                        | _ -> ())
                    let actions = BotSharp.Infrastructure.Rules.RuleEngine.evaluate engine
                    actions |> List.tryPick (function
                        | BotSharp.Infrastructure.Rules.RuleEngine.StopLoop reason -> Some reason
                        | _ -> None)
            // Reset lengthRecoveries to 0 when entering a new tool round.
            // externalLookupCounts is shared across rounds (not reset here).
            let nextState =
                match ruleStop with
                | Some reason ->
                    eprintfn "[RuleEngine] %s" reason
                    Finalizing (reason, None)
                | None ->
                    transition state (ToolsExecuted results)
            return! iterate deps snap' nextState iterIdx 0 0 externalLookupCounts

        | Finalizing (text, rcOpt) ->
            // Apply the FinalizeContent pipeline — a hook may transform or suppress the reply.
            let hookCtx = AgentHook.mkContext iterIdx (SessionSnapshot.messages snap)
            hookCtx.FinalContent <- Some text
            let finalText = deps.Hook.FinalizeContent hookCtx (Some text) |> Option.defaultValue text
            let snap2 = SessionSnapshot.append (AssistantMessage (finalText, rcOpt)) snap
            return (finalText, snap2)

        | Idle | BuildingPrompt _ | Consolidating _ ->
            return! AsyncResult.ofResult (Error SessionActorStopped)
    }

/// Parity with Python _PERSISTED_MODEL_ERROR_PLACEHOLDER.
/// Inserted into the session when the LLM API call fails so that the next turn
/// has proper role alternation (UserMessage → placeholder → next UserMessage).
let private _MODEL_ERROR_PLACEHOLDER =
    "[Assistant reply unavailable due to model error.]"

/// Run `iterate` and, on an LLM API failure, append a placeholder AssistantMessage
/// to the snapshot and persist it (best-effort) before propagating the error.
/// This mirrors Python runner._append_model_error_placeholder.
let private iterateWithErrorRecovery
    (deps   : AgentDependencies)
    (snap1  : SessionSnapshot)
    (state1 : AgentState)
    (externalLookupCounts : Dictionary<string, int>)
    : AsyncResult<string * SessionSnapshot, AgentError> =
    async {
        let! result = iterate deps snap1 state1 0 0 0 externalLookupCounts
        match result with
        | Ok _ -> return result

        | Error (AgentLlmFailure _ as err) ->
            // Append placeholder only when the last message is NOT already an
            // un-tool-called AssistantMessage (mirrors Python's guard check).
            let lastIsCleanAssistant =
                SessionSnapshot.messages snap1
                |> List.tryLast
                |> Option.exists (function AssistantMessage _ -> true | _ -> false)
            let snapWithPlaceholder =
                if lastIsCleanAssistant then snap1
                else SessionSnapshot.append (AssistantMessage (_MODEL_ERROR_PLACEHOLDER, None)) snap1
            // Best-effort persist — ignore storage errors; don't mask the LLM failure.
            let! _ = deps.PersistSession snapWithPlaceholder
            return Error err

        | Error _ -> return result
    }

/// Run a complete agent turn for an inbound message.
/// Loads the session, runs the loop, persists the updated session.
let runAgentLoop
    (inbound        : InboundMessage)
    (deps           : AgentDependencies)
    (pendingSummary : string option)
    : AsyncResult<string * SessionSnapshot, AgentError> =
    asyncResult {
        let sid = sessionId inbound

        let! snap = liftStorage (deps.LoadSession sid)

        let (ChannelId channelName) = inbound.Channel
        let! systemPrompt = deps.BuildSystemPrompt (Some channelName) deps.Config.WorkspacePath |> AsyncResult.ofAsync
        let allSpecs = allToolSpecs deps

        // Build the LLM request from the CURRENT snapshot (without the new userMsg)
        // so that buildRequest's trailing userMsg addition is the first occurrence.
        // snap1 (with userMsg appended) is used for iterate + persistence only.
        // pendingSummary (if Some) injects [Resumed Session] into the runtime context —
        // mirrors Python's session_summary that is consumed once per turn.
        let req = buildRequest systemPrompt snap inbound deps.Config allSpecs pendingSummary

        let userMsg =
            match inbound.Input with
            | ChatMessage (text, media) -> UserMessage (text, media)
            | Command _ -> UserMessage ("", [])
        let snap1 = SessionSnapshot.append userMsg snap

        let state0 = transition Idle (MessageReceived inbound)
        let state1 = transition state0 (PromptBuilt req)

        // Reset rule engine facts for the new turn (rules are preserved).
        deps.RuleEngine |> Option.iter BotSharp.Infrastructure.Rules.RuleEngine.resetTurn
        // Shared external lookup count table — persists across tool rounds in one turn.
        let externalLookupCounts = Dictionary<string, int>()
        let! (text, snap2) = iterateWithErrorRecovery deps snap1 state1 externalLookupCounts

        do! liftStorage (deps.PersistSession snap2)

        return (text, snap2)
    }
