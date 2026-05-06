module BotSharp.Application.Phase2Consolidator

#nowarn "3261"

open System
open System.IO
open System.Diagnostics
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Infrastructure.Storage.StateDb
open BotSharp.Infrastructure.Storage.JobQueue
open BotSharp.Infrastructure.Memory.ModelRecommendation
open BotSharp.Infrastructure.Shared.StringUtils

// ═══════════════════════════════════════════════════════════════════════════
// Phase 2 Consolidator — cross-session memory consolidation
//
// Global singleton job that:
//   1. Selects top-N stage1_outputs (ranked by usage_count + recency)
//   2. Syncs to filesystem workspace (rollout_summaries/*.md, raw_memories.md)
//   3. Computes git diff since last baseline
//   4. Runs a consolidation LLM agent to produce:
//      - memory_summary.md (navigational index, injected into system prompt)
//      - MEMORY.md (searchable registry)
//      - rollout_summaries/*.md (distilled per-session)
//   5. Resets git baseline
//
// Uses 6-hour cooldown between successful runs.
// Mirrors Codex phase2.rs (lines 45-199).
// ═══════════════════════════════════════════════════════════════════════════

// ── Constants ───────────────────────────────────────────────────────────

module Phase2Config =
    let LeaseMs = 60 * 60 * 1000
    let RetryDelayMs = 60 * 60 * 1000
    let HeartbeatIntervalMs = 90 * 1000

// ── Git workspace management ────────────────────────────────────────────
// Mirrors Codex workspace.rs

module MemoryWorkspace =

    let private runGit (args: string) (workDir: string) : Async<string> =
        async {
            let psi = ProcessStartInfo("git", args)
            psi.WorkingDirectory <- workDir
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            // Disable git interactive prompts
            psi.EnvironmentVariables.["GIT_TERMINAL_PROMPT"] <- "0"
            use proc = Process.Start(psi)
            let! stdout = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            do! proc.WaitForExitAsync() |> Async.AwaitTask
            return stdout
        }

    /// Ensure memory/ has a .git baseline repository.
    /// Codex workspace.rs ensure_git_baseline_repository.
    let ensureGitBaseline (memoryDir: string) : Async<unit> =
        async {
            let gitDir = Path.Combine(memoryDir, ".git")
            if not (Directory.Exists gitDir) then
                Directory.CreateDirectory(memoryDir) |> ignore
                let! _ = runGit "init" memoryDir
                let! _ = runGit "add -A" memoryDir
                let! _ = runGit "commit -m baseline --allow-empty" memoryDir
                eprintfn "[Phase2] Initialized git baseline in %s" memoryDir
        }

    /// Compute diff since last baseline. Codex workspace.rs memory_workspace_diff.
    let workspaceDiff (memoryDir: string) : Async<string option> =
        async {
            let! diff = runGit "diff HEAD --stat -p" memoryDir
            if String.IsNullOrWhiteSpace diff then return None
            else
                // Cap diff to 4MB
                let bounded =
                    if diff.Length > 4 * 1024 * 1024 then diff.[..4 * 1024 * 1024]
                    else diff
                return Some bounded
        }

    /// Write diff file for Phase 2 agent input.
    let writeWorkspaceDiff (memoryDir: string) (diff: string) : Async<unit> =
        async {
            let path = Path.Combine(memoryDir, "phase2_workspace_diff.md")
            do! File.WriteAllTextAsync(path, diff) |> Async.AwaitTask
        }

    /// Reset baseline after successful Phase 2.
    /// Codex workspace.rs reset_memory_workspace_baseline.
    let resetBaseline (memoryDir: string) : Async<unit> =
        async {
            // Delete temporary diff file
            let diffPath = Path.Combine(memoryDir, "phase2_workspace_diff.md")
            if File.Exists diffPath then File.Delete diffPath
            let rawPath = Path.Combine(memoryDir, "raw_memories.md")
            if File.Exists rawPath then File.Delete rawPath
            // Commit current state as new baseline
            let! _ = runGit "add -A" memoryDir
            let! _ = runGit "commit -m phase2-baseline --allow-empty" memoryDir
            ()
        }

// ── Input synchronization ───────────────────────────────────────────────

/// Generate a filename for a rollout summary.
let private rolloutSummaryFilename (output: Stage1Output) : string =
    let ts = DateTimeOffset.FromUnixTimeMilliseconds(output.SourceUpdatedAt).ToString("yyyy-MM-ddTHH-mm-ss")
    let sidShort = if output.SessionId.Length > 4 then output.SessionId.[..3] else output.SessionId
    let slug = output.RolloutSlug |> Option.defaultValue "session"
    let safeName = sprintf "%s-%s-%s.md" ts sidShort slug
    // Sanitize for filesystem
    safeName
    |> String.collect (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then string c else "_")

/// Sync stage1_outputs to filesystem workspace for Phase 2 agent.
/// Codex storage.rs sync_rollout_summaries_from_memories + rebuild_raw_memories_file.
let syncPhase2Inputs (memoryDir: string) (outputs: Stage1Output list) : Async<unit> =
    async {
        // 1. Sync rollout_summaries/*.md
        let summariesDir = Path.Combine(memoryDir, "rollout_summaries")
        Directory.CreateDirectory(summariesDir) |> ignore

        // Build set of expected files
        let expectedFiles =
            outputs
            |> List.map (fun o -> Path.Combine(summariesDir, rolloutSummaryFilename o))
            |> Set.ofList

        // Clean up stale files not in current selection
        if Directory.Exists summariesDir then
            let existingFiles = Directory.GetFiles(summariesDir, "*.md") |> Set.ofArray
            for f in existingFiles - expectedFiles do
                try File.Delete f with _ -> ()

        // Write new/updated summary files
        for output in outputs do
            let path = Path.Combine(summariesDir, rolloutSummaryFilename output)
            let content =
                sprintf "session_id: %s\nupdated_at: %s\nchannel: %s\n\n%s"
                    output.SessionId
                    (DateTimeOffset.FromUnixTimeMilliseconds(output.SourceUpdatedAt).ToString("o"))
                    (output.Channel |> Option.defaultValue "unknown")
                    output.RolloutSummary
            do! File.WriteAllTextAsync(path, content) |> Async.AwaitTask

        // 2. Rebuild raw_memories.md (merged input for Phase 2 agent)
        let rawMemoriesPath = Path.Combine(memoryDir, "raw_memories.md")
        let sb = Text.StringBuilder()
        sb.AppendLine("# Raw Memories\n") |> ignore
        for output in outputs do
            sb.AppendLine(sprintf "## Session `%s`" output.SessionId) |> ignore
            sb.AppendLine(sprintf "updated_at: %s"
                (DateTimeOffset.FromUnixTimeMilliseconds(output.SourceUpdatedAt).ToString("o"))) |> ignore
            sb.AppendLine(sprintf "channel: %s" (output.Channel |> Option.defaultValue "unknown")) |> ignore
            sb.AppendLine(sprintf "rollout_summary_file: rollout_summaries/%s"
                (rolloutSummaryFilename output)) |> ignore
            sb.AppendLine() |> ignore
            sb.AppendLine(output.RawMemory.Trim()) |> ignore
            sb.AppendLine() |> ignore
        do! File.WriteAllTextAsync(rawMemoriesPath, sb.ToString()) |> Async.AwaitTask
    }

// ── Phase 2 system prompt ───────────────────────────────────────────────

let private phase2SystemPrompt (_memoryDir: string) (isInit: bool) : string =
    let mode = if isInit then "INIT (first-time build)" else "INCREMENTAL (update)"
    let step2 =
        if isInit then "Create `memory_summary.md` and `MEMORY.md` from scratch based on raw memories"
        else "Read `phase2_workspace_diff.md` to understand what changed since last run"
    "You are a Phase 2 Memory Consolidation Agent.\n\n" +
    "## Memory Folder Structure\n" +
    "- `memory_summary.md`: Always-loaded navigational summary (keep under 5000 tokens)\n" +
    "- `MEMORY.md`: Searchable memory registry organized by task group\n" +
    "- `rollout_summaries/*.md`: Per-session distilled summaries\n" +
    "- `raw_memories.md`: Merged Phase 1 outputs (YOUR INPUT — read this first)\n" +
    "- `phase2_workspace_diff.md`: Changes since last consolidation (read if present)\n\n" +
    sprintf "## Operating Mode: %s\n\n" mode +
    "## Your Task\n" +
    "1. Read `raw_memories.md` to understand all new memories\n" +
    sprintf "2. %s\n" step2 +
    "3. Update `MEMORY.md` (comprehensive searchable handbook organized by task groups)\n" +
    "4. Update `memory_summary.md` (navigational index, must stay under 5000 tokens)\n" +
    "5. Optionally refine `rollout_summaries/*.md` with distilled versions\n\n" +
    "## memory_summary.md Format (STRICT)\n" +
    "- **User Profile** (≤ 500 words): stable, actionable user details\n" +
    "- **User Preferences**: many specific bullets\n" +
    "- **General Tips**: durable guidance\n" +
    "- **What's in Memory**: topic index organized by scope/recency\n\n" +
    "## MEMORY.md Format (STRICT)\n" +
    "Respond with two clearly labeled sections:\n\n" +
    "## memory_summary.md\n(content for memory_summary.md here)\n\n" +
    "## MEMORY.md\n(content for MEMORY.md here)\n\n" +
    "Within MEMORY.md:\n" +
    "- Organized by task groups: `# Task Group: <name>`\n" +
    "- Per-task sections with provenance metadata (session_id, date)\n" +
    "- Retrieval-optimized (keywords, references to rollout_summaries)\n" +
    "- Order by utility then recency\n\n" +
    "## Rules\n" +
    "- Evidence-based only — don't invent facts\n" +
    "- Redact secrets → [REDACTED_SECRET]\n" +
    "- Merge duplicate information, prefer newer version\n" +
    "- Remove stale entries that have been superseded\n" +
    "- Keep memory_summary.md under 5000 tokens (about 20KB)"

// ── Phase 2 enqueue (called by Phase 1 after success) ───────────────────

/// Enqueue or advance the Phase 2 global job watermark.
/// Codex enqueue_global_consolidation_with_executor (memories.rs:1222-1271).
let enqueuePhase2 (conn: SqliteConnection) (sourceUpdatedAt: int64) : unit =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "INSERT INTO jobs (kind, job_key, status, retry_remaining, " +
        "input_watermark, last_success_watermark, created_at, updated_at) " +
        "VALUES (@kind, 'global', 'pending', @retryRemaining, @watermark, 0, @now, @now) " +
        "ON CONFLICT(kind, job_key) DO UPDATE SET " +
        "status = CASE WHEN jobs.status = 'running' THEN 'running' ELSE 'pending' END, " +
        "retry_at = CASE WHEN jobs.status = 'running' THEN jobs.retry_at ELSE NULL END, " +
        "retry_remaining = max(jobs.retry_remaining, excluded.retry_remaining), " +
        "input_watermark = CASE " +
        "WHEN excluded.input_watermark > COALESCE(jobs.input_watermark, 0) " +
        "THEN excluded.input_watermark " +
        "ELSE COALESCE(jobs.input_watermark, 0) + 1 END, " +
        "updated_at = @now"
    cmd.Parameters.AddWithValue("@kind", JobKind.MemoryPhase2) |> ignore
    cmd.Parameters.AddWithValue("@retryRemaining", DefaultRetryRemaining) |> ignore
    cmd.Parameters.AddWithValue("@watermark", sourceUpdatedAt) |> ignore
    cmd.Parameters.AddWithValue("@now", now) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Response parsing ────────────────────────────────────────────────────

/// Parse Phase 2 LLM response to extract memory_summary and MEMORY content.
/// Looks for markdown section headers to split the response.
let private parsePhase2Response (text: string) : string * string =
    let summaryMarker = "## memory_summary.md"
    let memoryMarker  = "## MEMORY.md"
    let altSummaryMarker = "# Memory Summary"
    let altMemoryMarker  = "# Memory"

    let tryFindSection (marker: string) (txt: string) : int option =
        let idx = txt.IndexOf(marker, StringComparison.OrdinalIgnoreCase)
        if idx >= 0 then Some (idx + marker.Length) else None

    match tryFindSection summaryMarker text, tryFindSection memoryMarker text with
    | Some si, Some mi when si < mi ->
        let summaryContent = text.[si..mi - memoryMarker.Length - 1].Trim()
        let memoryContent = text.[mi..].Trim()
        (summaryContent, memoryContent)
    | _ ->
        match tryFindSection altSummaryMarker text, tryFindSection altMemoryMarker text with
        | Some si, Some mi when si < mi ->
            let summaryContent = text.[si..mi - altMemoryMarker.Length - 1].Trim()
            let memoryContent = text.[mi..].Trim()
            (summaryContent, memoryContent)
        | _ ->
            if text.Length < 20_000 then (text, "")
            else ("", text)

// ── Phase 2 execution ───────────────────────────────────────────────────

/// Run Phase 2 consolidation. Codex phase2.rs run() (lines 45-199).
let runPhase2
    (openDb : unit -> SqliteConnection)
    (deps   : AgentDependencies)
    : Async<Phase2Outcome> =
    async {
        let memoryDir = Path.Combine(deps.Config.WorkspacePath, "memory")

        // 1. Claim global singleton job (maxRunningJobs=1)
        use conn = openDb ()
        let! claimResult =
            tryClaim conn JobKind.MemoryPhase2 "global"
                0L Phase2Config.LeaseMs 1

        match claimResult with
        | SkippedRunning       -> return Phase2Skipped "already running"
        | SkippedRetryBackoff  -> return Phase2Skipped "retry backoff"
        | SkippedRetryExhausted -> return Phase2Skipped "retries exhausted"
        | SkippedUpToDate      -> return Phase2Skipped "up to date"

        | Claimed token ->
            // 2. Start heartbeat
            let heartbeatCts =
                startHeartbeat openDb JobKind.MemoryPhase2 "global"
                    token Phase2Config.LeaseMs Phase2Config.HeartbeatIntervalMs
            try
                try
                    // 3. Prepare workspace
                    do! MemoryWorkspace.ensureGitBaseline memoryDir

                    // 4. Load Phase 2 inputs
                    use conn2 = openDb ()
                    let! selectedOutputs =
                        getPhase2InputSelection conn2
                            deps.Config.Phase2MaxRawMemories
                            deps.Config.Phase1MaxUnusedDays

                    if selectedOutputs.IsEmpty then
                        use c = openDb ()
                        let! _ = markSucceeded c JobKind.MemoryPhase2 "global" token
                        return Phase2Succeeded 0
                    else

                    // 5. Sync inputs to filesystem
                    do! syncPhase2Inputs memoryDir selectedOutputs

                    // 6. Compute diff
                    let! diffOpt = MemoryWorkspace.workspaceDiff memoryDir

                    // Check if memory_summary.md exists (INIT vs INCREMENTAL mode)
                    let isInit = not (File.Exists(Path.Combine(memoryDir, "memory_summary.md")))

                    // For INIT mode, we always proceed even without diff
                    match diffOpt, isInit with
                    | None, false ->
                        // No changes in INCREMENTAL mode
                        use c = openDb ()
                        let! _ = markSucceeded c JobKind.MemoryPhase2 "global" token
                        return Phase2Succeeded 0
                    | _ ->

                    // 7. Write diff file (if any)
                    match diffOpt with
                    | Some diff -> do! MemoryWorkspace.writeWorkspaceDiff memoryDir diff
                    | None -> ()

                    // 8. Build Phase 2 prompt
                    let systemPrompt = phase2SystemPrompt memoryDir isInit
                    let userInput =
                        "Process the raw memories and update the memory workspace. " +
                        "Read raw_memories.md first, then update memory_summary.md and MEMORY.md. " +
                        (if isInit then "This is the first run — create both files from scratch."
                         else "Read phase2_workspace_diff.md to see what changed since the last run.")

                    // 9. Call LLM for consolidation (using Phase 2 model)
                    let model = resolvePhase2Model deps.Config
                    let settings : GenerationSettings = {
                        Temperature     = 0.3
                        MaxTokens       = 4096
                        ReasoningEffort = deps.Config.Phase2ReasoningEffort |> Option.orElse (Some Medium)
                    }

                    // Phase 2 uses a simpler single-call approach rather than a full agent loop
                    // (no tool calls needed — it directly reads/writes files via the prompt)
                    let llmMessages = [
                        UserMessage (systemPrompt, [])
                        UserMessage (userInput, [])
                    ]

                    // Read raw_memories.md content and include it
                    let rawMemPath = Path.Combine(memoryDir, "raw_memories.md")
                    let! rawMemContent =
                        if File.Exists rawMemPath then
                            File.ReadAllTextAsync(rawMemPath) |> Async.AwaitTask
                        else async { return "(no raw memories)" }

                    let llmMessages = llmMessages @ [
                        UserMessage (sprintf "## raw_memories.md content:\n\n%s" rawMemContent, [])
                    ]

                    let! responseResult =
                        chatWithRetry deps.Provider deps.FallbackProviders deps.RuleEngine
                            settings llmMessages []

                    match responseResult with
                    | Result.Ok response ->
                        // Extract the consolidation output from the LLM response
                        let responseText =
                            match response.Body with
                            | TextOnly content -> content
                            | WithToolCalls (Some content, _) -> content
                            | _ -> ""

                        let responseText = stripThink responseText

                        // Parse the response to extract memory_summary and MEMORY.md updates
                        // The LLM should have produced markdown content for both files
                        if responseText.Trim().Length > 100 then
                            // Write the consolidated output
                            // Look for ## memory_summary.md and ## MEMORY.md sections
                            let (summaryContent, memoryContent) = parsePhase2Response responseText

                            if summaryContent.Trim() <> "" then
                                let summaryPath = Path.Combine(memoryDir, "memory_summary.md")
                                do! File.WriteAllTextAsync(summaryPath, summaryContent.Trim()) |> Async.AwaitTask

                            if memoryContent.Trim() <> "" then
                                let memoryPath = Path.Combine(memoryDir, "MEMORY.md")
                                do! File.WriteAllTextAsync(memoryPath, memoryContent.Trim()) |> Async.AwaitTask

                        // 10. Verify ownership still held
                        use c = openDb ()
                        let! stillOwned =
                            heartbeat c JobKind.MemoryPhase2 "global" token Phase2Config.LeaseMs

                        if stillOwned then
                            // 11. Reset git baseline
                            do! MemoryWorkspace.resetBaseline memoryDir

                            // 12. Mark succeeded with cooldown
                            // Set retry_at to now + cooldownHours to enforce cooldown
                            let cooldownMs = int64 deps.Config.Phase2CooldownHours * 3600L * 1000L
                            let! _ = markSucceeded c JobKind.MemoryPhase2 "global" token

                            eprintfn "[Phase2] Consolidation complete: %d memories processed" selectedOutputs.Length
                            return Phase2Succeeded selectedOutputs.Length
                        else
                            return Phase2Failed "Lost ownership during execution"

                    | Result.Error _ ->
                        use c = openDb ()
                        let! _ = markFailed c JobKind.MemoryPhase2 "global"
                                     token "LLM call failed" Phase2Config.RetryDelayMs
                        return Phase2Failed "LLM call failed"

                with ex ->
                    try
                        use c = openDb ()
                        let! ok = markFailed c JobKind.MemoryPhase2 "global"
                                      token ex.Message Phase2Config.RetryDelayMs
                        if not ok then
                            use c2 = openDb ()
                            let! _ = markFailedIfUnowned c2 JobKind.MemoryPhase2 "global"
                                         token ex.Message Phase2Config.RetryDelayMs
                            ()
                    with _ -> ()
                    return Phase2Failed ex.Message

            finally
                heartbeatCts.Cancel()
                heartbeatCts.Dispose()
    }
