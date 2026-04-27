module BotSharp.Infrastructure.Tools.MyTool

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// "my" tool — inspect and annotate agent runtime configuration
//
// Provides `check` (read config/scratchpad) and `set` (write scratchpad).
//
// Config fields are read-only: AgentDependencies is immutable per turn.
//
// Scratchpad is a JSON object at {workspacePath}/scratchpad.json.
// It persists across sessions and is readable/writable with `set`.
// This is equivalent to Python's `_runtime_vars` scratchpad, but
// file-backed rather than in-memory (more persistent, not less).
//
// Keys supported by `check`:
//   model, provider, temperature, max_tokens, workspace, max_iterations,
//   max_tool_result_chars, context_window_tokens, memory_window_size,
//   reasoning_effort, _last_usage, scratchpad, scratchpad.<key>
// ═══════════════════════════════════════════════════════════════════════════

// ── Scratchpad helpers ────────────────────────────────────────────────────

let private scratchpadPath (workspacePath: string) =
    Path.Combine(workspacePath, "scratchpad.json")

/// Load the scratchpad (a flat key→string map). Returns empty map on missing/corrupt file.
let private loadScratchpad (workspacePath: string) : Map<string, string> =
    try
        let path = scratchpadPath workspacePath
        if not (File.Exists path) then Map.empty
        else
            use doc = JsonDocument.Parse(File.ReadAllText path)
            doc.RootElement.EnumerateObject()
            |> Seq.choose (fun p ->
                if p.Value.ValueKind = JsonValueKind.String then Some (p.Name, p.Value.GetString() |> Unchecked.nonNull)
                else None)
            |> Map.ofSeq
    with _ -> Map.empty

/// Persist the scratchpad to disk (creates workspace dir if needed).
let private saveScratchpad (workspacePath: string) (pad: Map<string, string>) : Result<unit, string> =
    try
        Directory.CreateDirectory(workspacePath) |> ignore
        use ms = new MemoryStream()
        use w  = new Utf8JsonWriter(ms, JsonWriterOptions(Indented = true))
        w.WriteStartObject()
        for kv in pad do w.WriteString(kv.Key, kv.Value)
        w.WriteEndObject()
        w.Flush()
        File.WriteAllBytes(scratchpadPath workspacePath, ms.ToArray())
        Ok ()
    with ex -> Error ex.Message

// ── Config field reader ───────────────────────────────────────────────────

/// Format a single BotSharpConfig field as "key: value".
/// Returns None when the key is unrecognised (caller tries scratchpad next).
let private checkConfigField (cfg: BotSharpConfig) (key: string) : string option =
    match key.Trim().ToLowerInvariant() with
    | "model"                 -> Some $"model: {cfg.DefaultModel}"
    | "provider"              -> Some $"provider: {cfg.DefaultProvider}"
    | "temperature"           -> Some $"temperature: {cfg.Temperature}"
    | "max_tokens"            -> Some $"max_tokens: {cfg.MaxTokens}"
    | "workspace"             -> Some $"workspace: {cfg.WorkspacePath}"
    | "max_iterations"        -> Some $"max_iterations: {cfg.MaxIterations}"
    | "max_tool_result_chars" -> Some $"max_tool_result_chars: {cfg.MaxToolResultChars}"
    | "context_window_tokens" ->
        let v = if cfg.ContextWindowTokens <= 0 then "0 (disabled)" else string cfg.ContextWindowTokens
        Some $"context_window_tokens: {v}"
    | "memory_window_size"    -> Some $"memory_window_size: {cfg.MemoryWindowSize}"
    | "reasoning_effort"      ->
        let v =
            match cfg.ReasoningEffort with
            | Some Low      -> "low"
            | Some Medium   -> "medium"
            | Some High     -> "high"
            | Some Adaptive -> "adaptive"
            | None          -> "(not set — model default)"
        Some $"reasoning_effort: {v}"
    | "fail_on_tool_error"    -> Some $"fail_on_tool_error: {cfg.FailOnToolError}"
    | "disabled_skills"       ->
        let v = if cfg.DisabledSkills.IsEmpty then "(none)" else String.concat ", " cfg.DisabledSkills
        Some $"disabled_skills: {v}"
    | "session_ttl_minutes"   ->
        let v = if cfg.SessionTtlMinutes = 0 then "0 (disabled)" else string cfg.SessionTtlMinutes
        Some $"session_ttl_minutes: {v}"
    | "timezone"              ->
        let v = cfg.Timezone |> Option.defaultValue "(system local)"
        Some $"timezone: {v}"
    | "heartbeat_enabled"           -> Some $"heartbeat_enabled: {cfg.HeartbeatEnabled}"
    | "heartbeat_interval_seconds"  -> Some $"heartbeat_interval_seconds: {cfg.HeartbeatIntervalSeconds}"
    | "heartbeat_keep_recent_messages" -> Some $"heartbeat_keep_recent_messages: {cfg.HeartbeatKeepRecentMessages}"
    | "exec_timeout_seconds"        ->
        let v = if cfg.ExecTimeoutSeconds = 0 then "0 (use tool default: 60 s)" else string cfg.ExecTimeoutSeconds
        Some $"exec_timeout_seconds: {v}"
    | "restrict_to_workspace"       -> Some $"restrict_to_workspace: {cfg.RestrictToWorkspace}"
    | "provider_retry_mode"         -> Some $"provider_retry_mode: {cfg.ProviderRetryMode}"
    | "unified_session"             -> Some $"unified_session: {cfg.UnifiedSession}"
    | "web_search_provider"         ->
        let v = cfg.WebSearchProvider |> Option.defaultValue "(auto: brave if key set, else duckduckgo)"
        Some $"web_search_provider: {v}"
    | "dream_model_override"        ->
        let v = cfg.DreamModelOverride |> Option.defaultValue "(none — uses default_model)"
        Some $"dream_model_override: {v}"
    | "dream_max_iterations"        -> Some $"dream_max_iterations: {cfg.DreamMaxIterations}"
    | "dream_interval_hours"        ->
        let v = if cfg.DreamIntervalHours = 0 then "0 (disabled)" else string cfg.DreamIntervalHours
        Some $"dream_interval_hours: {v}"
    | "web_proxy_url" | "web_proxy"   ->
        let v = cfg.WebProxyUrl |> Option.defaultValue "(none — direct connection)"
        Some $"web_proxy_url: {v}"
    | "web_search_timeout"          -> Some $"web_search_timeout: {cfg.WebSearchTimeout}"
    | "web_search_max_results"      -> Some $"web_search_max_results: {cfg.WebSearchMaxResults}"
    | "dream_max_batch_size"        -> Some $"dream_max_batch_size: {cfg.DreamMaxBatchSize}"
    | "exec_path_append"            ->
        let v = if cfg.ExecPathAppend = "" then "(none)" else cfg.ExecPathAppend
        Some $"exec_path_append: {v}"
    | "exec_sandbox"                ->
        let v = if cfg.ExecSandbox = "" then "(none — no sandbox)" else cfg.ExecSandbox
        Some $"exec_sandbox: {v}"
    | "send_tool_hints"             -> Some $"send_tool_hints: {cfg.SendToolHints}"
    | "send_progress"               -> Some $"send_progress: {cfg.SendProgress}"
    | "send_max_retries"            -> Some $"send_max_retries: {cfg.SendMaxRetries}"
    | "my_tool_allow_set"           -> Some $"my_tool_allow_set: {cfg.MyToolAllowSet}"
    | "ssrf_whitelist"              ->
        let v = if cfg.SsrfWhitelist.IsEmpty then "(none)" else String.concat ", " cfg.SsrfWhitelist
        Some $"ssrf_whitelist: {v}"
    | "file_read_max_chars"         -> Some $"file_read_max_chars: {cfg.FileReadMaxChars}"
    | "system_prompt_append"        ->
        let v = cfg.SystemPromptAppend |> Option.defaultValue "(none)"
        Some $"system_prompt_append: {v}"
    | "web_search_api_key"          ->
        let v = if cfg.WebSearchApiKey = "" then "(env var)" else "(set)"
        Some $"web_search_api_key: {v}"
    | "web_search_base_url"         ->
        let v = if cfg.WebSearchBaseUrl = "" then "(env var)" else cfg.WebSearchBaseUrl
        Some $"web_search_base_url: {v}"
    | "exec_enable"                 -> Some $"exec_enable: {cfg.ExecEnable}"
    | "web_enable"                  -> Some $"web_enable: {cfg.WebEnable}"
    | "my_tool_enable"              -> Some $"my_tool_enable: {cfg.MyToolEnable}"
    | "transcription_provider"      -> Some $"transcription_provider: {cfg.TranscriptionProvider}"
    | "transcription_language"      ->
        let v = cfg.TranscriptionLanguage |> Option.defaultValue "(auto)"
        Some $"transcription_language: {v}"
    | "dream_annotate_line_ages"    -> Some $"dream_annotate_line_ages: {cfg.DreamAnnotateLineAges}"
    | "provider_extra_headers"      ->
        let v =
            if Map.isEmpty cfg.ProviderExtraHeaders then "(none)"
            else
                cfg.ProviderExtraHeaders
                |> Map.toList
                |> List.map (fun (pid, hdrs) ->
                    let hdrStr = hdrs |> Map.toList |> List.map (fun (k, v) -> $"{k}=...") |> String.concat ", "
                    $"{pid}({hdrStr})")
                |> String.concat "; "
        Some $"provider_extra_headers: {v}"
    | "api_port"                    ->
        let v = cfg.ApiPort |> Option.map string |> Option.defaultValue "(CLI flag only)"
        Some $"api_port: {v}"
    | "api_timeout_seconds"         -> Some $"api_timeout_seconds: {cfg.ApiTimeoutSeconds}"
    | "api_host"                    -> Some $"api_host: {cfg.ApiHost}"
    | "context_block_limit"         ->
        let v = cfg.ContextBlockLimit |> Option.map string |> Option.defaultValue "(auto — computed from context_window_tokens)"
        Some $"context_block_limit: {v}"
    | "max_iterations_message"      ->
        let v = cfg.MaxIterationsMessage |> Option.defaultValue "(default — generic message)"
        Some $"max_iterations_message: {v}"
    | "exec_allowed_env_keys"       ->
        let v = if cfg.ExecAllowedEnvKeys.IsEmpty then "(all — no restriction)" else String.concat ", " cfg.ExecAllowedEnvKeys
        Some $"exec_allowed_env_keys: {v}"
    | _ -> None

/// Full config overview.
let private checkAll
    (cfg              : BotSharpConfig)
    (workspacePath    : string)
    (getLastUsage     : unit -> TokenUsage option)
    (getCurrentIter   : unit -> int)
    : string =
    let ctxWindow =
        if cfg.ContextWindowTokens <= 0 then "0 (disabled)" else string cfg.ContextWindowTokens
    let reasoningStr =
        match cfg.ReasoningEffort with
        | Some Low      -> "low"
        | Some Medium   -> "medium"
        | Some High     -> "high"
        | Some Adaptive -> "adaptive"
        | None          -> "(not set — model default)"
    let lastUsageStr =
        match getLastUsage() with
        | None   -> "(no LLM call yet this session)"
        | Some u -> $"prompt={u.PromptTokens}, completion={u.CompletionTokens}, cached={u.CachedTokens}"
    let disabledSkillsStr =
        if cfg.DisabledSkills.IsEmpty then "(none)"
        else String.concat ", " cfg.DisabledSkills
    let sessionTtlStr =
        if cfg.SessionTtlMinutes = 0 then "0 (disabled)" else string cfg.SessionTtlMinutes
    let timezoneStr = cfg.Timezone |> Option.defaultValue "(system local)"
    let execTimeoutStr = if cfg.ExecTimeoutSeconds = 0 then "0 (tool default)" else string cfg.ExecTimeoutSeconds
    let webSearchStr = cfg.WebSearchProvider |> Option.defaultValue "(auto)"
    let webProxyStr  = cfg.WebProxyUrl |> Option.defaultValue "(none)"
    let execPathStr    = if cfg.ExecPathAppend = "" then "(none)" else cfg.ExecPathAppend
    let execSandboxStr = if cfg.ExecSandbox   = "" then "(none — no sandbox)" else cfg.ExecSandbox
    let dreamModelStr = cfg.DreamModelOverride |> Option.defaultValue "(none)"
    let dreamIntervalStr = if cfg.DreamIntervalHours = 0 then "0 (disabled)" else string cfg.DreamIntervalHours
    let ssrfStr            = if cfg.SsrfWhitelist.IsEmpty then "(none)" else String.concat ", " cfg.SsrfWhitelist
    let systemPromptAppStr = cfg.SystemPromptAppend |> Option.defaultValue "(none)"
    let webSearchApiKeyStr    = if cfg.WebSearchApiKey = "" then "(env var)" else "(set)"
    let webSearchBaseUrlStr   = if cfg.WebSearchBaseUrl = "" then "(env var)" else cfg.WebSearchBaseUrl
    let transcriptionLangStr  = cfg.TranscriptionLanguage |> Option.defaultValue "(auto)"
    let providerExtraHeadersStr =
        if Map.isEmpty cfg.ProviderExtraHeaders then "(none)"
        else
            cfg.ProviderExtraHeaders
            |> Map.toList
            |> List.map (fun (pid, hdrs) ->
                let hdrStr = hdrs |> Map.toList |> List.map (fun (k, _) -> $"{k}=...") |> String.concat ", "
                $"{pid}({hdrStr})")
            |> String.concat "; "
    let apiPortStr = cfg.ApiPort |> Option.map string |> Option.defaultValue "(CLI flag only)"
    let contextBlockLimitStr = cfg.ContextBlockLimit |> Option.map string |> Option.defaultValue "(auto)"
    let maxIterationsMsgStr  = cfg.MaxIterationsMessage |> Option.map (fun _ -> "(set)") |> Option.defaultValue "(default)"
    let execAllowedEnvStr    = if cfg.ExecAllowedEnvKeys.IsEmpty then "(all — no restriction)" else String.concat ", " cfg.ExecAllowedEnvKeys
    let configLines =
        [ $"model: {cfg.DefaultModel}"
          $"provider: {cfg.DefaultProvider}"
          $"temperature: {cfg.Temperature}"
          $"max_tokens: {cfg.MaxTokens}"
          $"workspace: {cfg.WorkspacePath}"
          $"max_iterations: {cfg.MaxIterations}"
          $"max_tool_result_chars: {cfg.MaxToolResultChars}"
          $"context_window_tokens: {ctxWindow}"
          $"memory_window_size: {cfg.MemoryWindowSize}"
          $"reasoning_effort: {reasoningStr}"
          $"fail_on_tool_error: {cfg.FailOnToolError}"
          $"disabled_skills: {disabledSkillsStr}"
          $"session_ttl_minutes: {sessionTtlStr}"
          $"timezone: {timezoneStr}"
          $"exec_timeout_seconds: {execTimeoutStr}"
          $"restrict_to_workspace: {cfg.RestrictToWorkspace}"
          $"provider_retry_mode: {cfg.ProviderRetryMode}"
          $"unified_session: {cfg.UnifiedSession}"
          $"web_search_provider: {webSearchStr}"
          $"web_search_max_results: {cfg.WebSearchMaxResults}"
          $"web_proxy_url: {webProxyStr}"
          $"web_search_timeout: {cfg.WebSearchTimeout}"
          $"exec_path_append: {execPathStr}"
          $"exec_sandbox: {execSandboxStr}"
          $"heartbeat_enabled: {cfg.HeartbeatEnabled}"
          $"heartbeat_interval_seconds: {cfg.HeartbeatIntervalSeconds}"
          $"heartbeat_keep_recent_messages: {cfg.HeartbeatKeepRecentMessages}"
          $"dream_model_override: {dreamModelStr}"
          $"dream_max_iterations: {cfg.DreamMaxIterations}"
          $"dream_max_batch_size: {cfg.DreamMaxBatchSize}"
          $"dream_interval_hours: {dreamIntervalStr}"
          $"send_tool_hints: {cfg.SendToolHints}"
          $"send_progress: {cfg.SendProgress}"
          $"send_max_retries: {cfg.SendMaxRetries}"
          $"my_tool_allow_set: {cfg.MyToolAllowSet}"
          $"ssrf_whitelist: {ssrfStr}"
          $"file_read_max_chars: {cfg.FileReadMaxChars}"
          $"system_prompt_append: {systemPromptAppStr}"
          $"web_search_api_key: {webSearchApiKeyStr}"
          $"web_search_base_url: {webSearchBaseUrlStr}"
          $"exec_enable: {cfg.ExecEnable}"
          $"web_enable: {cfg.WebEnable}"
          $"my_tool_enable: {cfg.MyToolEnable}"
          $"transcription_provider: {cfg.TranscriptionProvider}"
          $"transcription_language: {transcriptionLangStr}"
          $"dream_annotate_line_ages: {cfg.DreamAnnotateLineAges}"
          $"provider_extra_headers: {providerExtraHeadersStr}"
          $"api_port: {apiPortStr}"
          $"api_timeout_seconds: {cfg.ApiTimeoutSeconds}"
          $"api_host: {cfg.ApiHost}"
          $"context_block_limit: {contextBlockLimitStr}"
          $"max_iterations_message: {maxIterationsMsgStr}"
          $"exec_allowed_env_keys: {execAllowedEnvStr}"
          $"_last_usage: {lastUsageStr}"
          $"_current_iteration: {getCurrentIter()}" ]
    let pad = loadScratchpad workspacePath
    let padSection =
        if pad.IsEmpty then ""
        else
            let entries = pad |> Map.toList |> List.map (fun (k, v) -> $"  {k}: {v}") |> String.concat "\n"
            $"\nscratchpad:\n{entries}"
    String.concat "\n" configLines + padSection

// ── Tool spec ─────────────────────────────────────────────────────────────

let myToolSpec : ToolSpec = {
    Name        = ToolName "my"
    Description =
        "Check your own runtime config and session scratchpad; write to scratchpad.\n" +
        "Actions:\n" +
        "- check (no key): full config overview + scratchpad.\n" +
        "- check (key): read a specific value. Config keys: model, provider, temperature, " +
        "max_tokens, workspace, max_iterations, max_tool_result_chars, context_window_tokens, " +
        "memory_window_size, reasoning_effort, fail_on_tool_error, disabled_skills, " +
        "session_ttl_minutes, timezone, exec_timeout_seconds, exec_path_append, web_search_provider, web_search_max_results, web_proxy_url, web_search_timeout, " +
        "heartbeat_enabled, heartbeat_interval_seconds, " +
        "heartbeat_keep_recent_messages, dream_model_override, dream_max_iterations, dream_max_batch_size, dream_interval_hours, " +
        "send_tool_hints, send_progress, send_max_retries, " +
        "my_tool_allow_set, ssrf_whitelist, " +
        "file_read_max_chars, system_prompt_append, " +
        "web_search_api_key, web_search_base_url, exec_enable, web_enable, my_tool_enable, " +
        "transcription_provider, transcription_language, dream_annotate_line_ages, " +
        "provider_extra_headers, api_port, api_timeout_seconds, api_host, " +
        "context_block_limit, max_iterations_message, exec_allowed_env_keys, " +
        "_last_usage, _current_iteration. " +
        "Scratchpad: 'scratchpad' (all entries) or 'scratchpad.<key>' (one entry).\n" +
        "- set (key, value): store a note in your scratchpad (persists across sessions).\n" +
        "  Prefix key with 'scratchpad.' or use a plain key (all set values go to scratchpad).\n" +
        "  To delete an entry, set it to an empty string.\n" +
        "When to use:\n" +
        "- User asks about your model or settings → check that key.\n" +
        "- Large task ahead → check context_window_tokens and max_iterations first.\n" +
        "- Need to remember something across turns → set to store in scratchpad.\n" +
        "- Check token usage from the last LLM call → check _last_usage.\n" +
        "- Check your current iteration index within this turn → check _current_iteration.\n" +
        "Note: config fields (model, temperature, etc.) are read-only — they reflect\n" +
        "the server config and cannot be changed at runtime."
    Parameters      = Map.ofList [
        "action", { Type = JsEnum ["check"; "set"]
                    Description = "Action: check (read) or set (write scratchpad)"
                    Required    = true }
        "key",    { Type = JsString
                    Description = "Key to check or set. For check: config field or 'scratchpad'/'scratchpad.<key>'. " +
                                  "For set: any string key (stored in scratchpad)."
                    Required    = false }
        "value",  { Type = JsString
                    Description = "Value to store (for set action). Empty string removes the key."
                    Required    = false }
    ]
    ConcurrencySafe = false  // 'set' writes to scratchpad; not idempotent-concurrent
}

// ── Execute ───────────────────────────────────────────────────────────────

let executeMyTool
    (cfg             : BotSharpConfig)
    (getLastUsage    : unit -> TokenUsage option)
    (getCurrentIter  : unit -> int)
    (args            : Map<string, JsonElement>)
    : Async<ToolResult> =
    async {
        match requireStringArg "action" args with
        | Error e -> return ToolFailure e
        | Ok action ->
            match action.Trim().ToLowerInvariant() with

            | "set" ->
                if not cfg.MyToolAllowSet then
                    return ToolSuccess "The 'set' action is disabled by configuration (my_tool_allow_set = false). Ask your administrator to enable it."
                else
                match requireStringArg "key" args with
                | Error _ ->
                    return ToolSuccess "set requires a 'key' argument."
                | Ok rawKey ->
                    // Strip leading "scratchpad." prefix if present
                    let k = rawKey.TrimStart().ToLowerInvariant()
                    let scratchKey =
                        if k.StartsWith("scratchpad.") then k.["scratchpad.".Length..]
                        else k
                    if scratchKey.Length = 0 then
                        return ToolSuccess "Key cannot be empty."
                    else
                        let value = tryStringArg "value" args |> Option.defaultValue ""
                        let pad   = loadScratchpad cfg.WorkspacePath
                        let pad2  =
                            if value = "" then Map.remove scratchKey pad
                            else Map.add scratchKey value pad
                        match saveScratchpad cfg.WorkspacePath pad2 with
                        | Ok () ->
                            if value = "" then
                                return ToolSuccess $"Removed scratchpad.{scratchKey}"
                            else
                                return ToolSuccess $"Set scratchpad.{scratchKey} = {value}"
                        | Error msg ->
                            return ToolFailure (ExecutionFailed $"Could not save scratchpad: {msg}")

            | _ ->   // "check" or anything else
                let key = tryStringArg "key" args
                match key with
                | None ->
                    return ToolSuccess (checkAll cfg cfg.WorkspacePath getLastUsage getCurrentIter)
                | Some raw ->
                    let k = raw.Trim().ToLowerInvariant()
                    // _last_usage: token counts from the most recent LLM call this session.
                    if k = "_last_usage" then
                        let text =
                            match getLastUsage() with
                            | None   -> "_last_usage: (no LLM call yet this session)"
                            | Some u -> $"_last_usage: prompt={u.PromptTokens}, completion={u.CompletionTokens}, cached={u.CachedTokens}"
                        return ToolSuccess text
                    // _current_iteration: zero-based index of the current AwaitingLLM step.
                    elif k = "_current_iteration" then
                        return ToolSuccess $"_current_iteration: {getCurrentIter()}"
                    else
                    // Config field?
                    match checkConfigField cfg k with
                    | Some v -> return ToolSuccess v
                    | None ->
                        // Scratchpad?
                        let pad = loadScratchpad cfg.WorkspacePath
                        if k = "scratchpad" then
                            if pad.IsEmpty then
                                return ToolSuccess "scratchpad: (empty)"
                            else
                                let lines = pad |> Map.toList |> List.map (fun (sk, sv) -> $"{sk}: {sv}") |> String.concat "\n"
                                return ToolSuccess $"scratchpad:\n{lines}"
                        elif k.StartsWith("scratchpad.") then
                            let sk = k.["scratchpad.".Length..]
                            match pad.TryFind sk with
                            | Some v -> return ToolSuccess $"scratchpad.{sk}: {v}"
                            | None   -> return ToolSuccess $"scratchpad.{sk}: (not set)"
                        else
                            // Try scratchpad as plain key fallback
                            match pad.TryFind k with
                            | Some v -> return ToolSuccess $"scratchpad.{k}: {v}"
                            | None   ->
                                return ToolSuccess (
                                    $"Unknown key '{raw}'. Config keys: model, provider, temperature, " +
                                    "max_tokens, workspace, max_iterations, max_tool_result_chars, " +
                                    "context_window_tokens, memory_window_size, reasoning_effort, " +
                                    "web_search_provider, web_proxy_url, web_search_timeout, " +
                                    "_last_usage, _current_iteration. " +
                                    "Scratchpad: 'scratchpad' or 'scratchpad.<key>'.")
    }

/// (spec, execute) pair — pass cfg, a last-usage getter, and a current-iteration
/// getter at call site. Both getters are closures over ref cells written by AgentLoop.
let allTools
    (cfg            : BotSharpConfig)
    (getLastUsage   : unit -> TokenUsage option)
    (getCurrentIter : unit -> int)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ myToolSpec, executeMyTool cfg getLastUsage getCurrentIter ]
