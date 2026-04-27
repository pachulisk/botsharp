module BotSharp.Infrastructure.Config.ConfigWriter

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Config serializer — mirrors the JSON schema read by ConfigParser.
//
// Intentionally written using Utf8JsonWriter (low-level) rather than
// System.Text.Json serializer attributes so the output key names exactly
// match ConfigParser's expectations without a custom naming policy.
// ═══════════════════════════════════════════════════════════════════════════

/// Serialize a BotSharpConfig to a JSON string that ConfigParser can read back.
let serializeConfig (cfg: BotSharpConfig) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms, JsonWriterOptions(Indented = true))
    w.WriteStartObject()
    w.WriteString("default_model",      cfg.DefaultModel)
    w.WriteString("default_provider",   cfg.DefaultProvider)
    w.WriteNumber("temperature",        cfg.Temperature)
    w.WriteNumber("max_tokens",         cfg.MaxTokens)
    w.WriteString("workspace_path",     cfg.WorkspacePath)
    w.WriteNumber("memory_window_size",    cfg.MemoryWindowSize)
    w.WriteNumber("max_iterations",        cfg.MaxIterations)
    w.WriteNumber("max_tool_result_chars", cfg.MaxToolResultChars)
    if cfg.ContextWindowTokens > 0 then
        w.WriteNumber("context_window_tokens", cfg.ContextWindowTokens)
    match cfg.ContextBlockLimit with
    | Some limit -> w.WriteNumber("context_block_limit", limit)
    | None       -> ()
    match cfg.MaxIterationsMessage with
    | Some msg -> w.WriteString("max_iterations_message", msg)
    | None     -> ()
    if cfg.FailOnToolError then
        w.WriteBoolean("fail_on_tool_error", true)
    // disabled_skills (omit if empty — matches Python's default_factory=list)
    if not (List.isEmpty cfg.DisabledSkills) then
        w.WriteStartArray("disabled_skills")
        for name in cfg.DisabledSkills do w.WriteStringValue(name)
        w.WriteEndArray()
    // session_ttl_minutes (omit when 0 — 0 = disabled, matches Python default)
    if cfg.SessionTtlMinutes > 0 then
        w.WriteNumber("session_ttl_minutes", cfg.SessionTtlMinutes)
    // timezone (omit when None — None = system local, matches Python default "UTC" behaviour)
    match cfg.Timezone with
    | Some tz -> w.WriteString("timezone", tz)
    | None    -> ()
    // exec_timeout_seconds (omit when 0 — 0 = use tool default 60 s)
    if cfg.ExecTimeoutSeconds > 0 then
        w.WriteNumber("exec_timeout_seconds", cfg.ExecTimeoutSeconds)

    // heartbeat (omit when all values are defaults — matches Python's default_factory=HeartbeatConfig)
    let d = BotSharpConfig.defaults
    if cfg.HeartbeatEnabled           <> d.HeartbeatEnabled
       || cfg.HeartbeatIntervalSeconds   <> d.HeartbeatIntervalSeconds
       || cfg.HeartbeatKeepRecentMessages <> d.HeartbeatKeepRecentMessages then
        w.WriteStartObject("heartbeat")
        w.WriteBoolean("enabled",              cfg.HeartbeatEnabled)
        w.WriteNumber("interval_s",            cfg.HeartbeatIntervalSeconds)
        w.WriteNumber("keep_recent_messages",  cfg.HeartbeatKeepRecentMessages)
        w.WriteEndObject()

    // web_search_provider (omit when None / default)
    match cfg.WebSearchProvider with
    | Some p -> w.WriteString("web_search_provider", p)
    | None   -> ()

    // restrict_to_workspace (omit when false — matches Python default)
    if cfg.RestrictToWorkspace then
        w.WriteBoolean("restrict_to_workspace", true)

    // provider_retry_mode (omit when "standard" — matches Python default)
    if cfg.ProviderRetryMode <> d.ProviderRetryMode then
        w.WriteString("provider_retry_mode", cfg.ProviderRetryMode)

    // unified_session (omit when false — matches Python default)
    if cfg.UnifiedSession then
        w.WriteBoolean("unified_session", true)

    // web_proxy (omit when None — None = no proxy)
    match cfg.WebProxyUrl with
    | Some proxy -> w.WriteString("web_proxy", proxy)
    | None       -> ()

    // web_search_timeout (omit when default — 30 s)
    if cfg.WebSearchTimeout <> d.WebSearchTimeout then
        w.WriteNumber("web_search_timeout", cfg.WebSearchTimeout)

    // web_search_max_results (omit when default — 5)
    if cfg.WebSearchMaxResults <> d.WebSearchMaxResults then
        w.WriteNumber("web_search_max_results", cfg.WebSearchMaxResults)

    // exec_path_append (omit when empty — matches Python default)
    if cfg.ExecPathAppend <> d.ExecPathAppend then
        w.WriteString("exec_path_append", cfg.ExecPathAppend)

    // exec_sandbox (omit when empty — "" = no sandbox; Python: exec.sandbox)
    if cfg.ExecSandbox <> d.ExecSandbox then
        w.WriteString("exec_sandbox", cfg.ExecSandbox)

    // dream (omit when all values are defaults)
    if cfg.DreamModelOverride  <> d.DreamModelOverride
       || cfg.DreamMaxIterations  <> d.DreamMaxIterations
       || cfg.DreamIntervalHours  <> d.DreamIntervalHours
       || cfg.DreamMaxBatchSize   <> d.DreamMaxBatchSize then
        w.WriteStartObject("dream")
        match cfg.DreamModelOverride with
        | Some m -> w.WriteString("model_override", m)
        | None   -> ()
        w.WriteNumber("max_iterations", cfg.DreamMaxIterations)
        w.WriteNumber("interval_h",     cfg.DreamIntervalHours)
        if cfg.DreamMaxBatchSize <> d.DreamMaxBatchSize then
            w.WriteNumber("max_batch_size", cfg.DreamMaxBatchSize)
        w.WriteEndObject()

    // api_keys object
    w.WriteStartObject("api_keys")
    for kv in cfg.ApiKeys do
        w.WriteString(kv.Key, ApiKey.value kv.Value)
    w.WriteEndObject()

    // base_urls object (omit if empty)
    if not (Map.isEmpty cfg.BaseUrls) then
        w.WriteStartObject("base_urls")
        for kv in cfg.BaseUrls do w.WriteString(kv.Key, kv.Value)
        w.WriteEndObject()

    // allow_from array
    let allowFrom =
        match cfg.AllowFrom with
        | AnyoneAllowed   -> [| "*" |]
        | AllowedSet uids -> uids |> Set.toArray |> Array.map id
    w.WriteStartArray("allow_from")
    for s in allowFrom do w.WriteStringValue(s)
    w.WriteEndArray()

    // brave_api_key (omit if absent)
    match cfg.BraveApiKey with
    | Some k -> w.WriteString("brave_api_key", ApiKey.value k)
    | None   -> ()

    // reasoning_effort (omit if absent — None means use model default)
    match cfg.ReasoningEffort with
    | Some Low      -> w.WriteString("reasoning_effort", "low")
    | Some Medium   -> w.WriteString("reasoning_effort", "medium")
    | Some High     -> w.WriteString("reasoning_effort", "high")
    | Some Adaptive -> w.WriteString("reasoning_effort", "adaptive")
    | None          -> ()

    // telegram (omit if not configured)
    match cfg.Telegram with
    | None -> ()
    | Some tg ->
        w.WriteStartObject("telegram")
        w.WriteString("token", TelegramBotToken.value tg.Token)
        w.WriteStartArray("allow_from")
        match tg.AllowFrom with
        | AnyoneAllowed   -> w.WriteStringValue("*")
        | AllowedSet uids -> for uid in uids do w.WriteStringValue(uid)
        w.WriteEndArray()
        match tg.Proxy with
        | Some uri -> w.WriteString("proxy", uri.ToString())
        | None     -> ()
        w.WriteBoolean("reply_to_message",   tg.ReplyToMessage)
        match tg.ReactEmoji with
        | Some emoji -> w.WriteString("react_emoji", emoji)
        | None       -> ()
        w.WriteString("group_policy", match tg.GroupPolicy with MentionPolicy -> "mention" | OpenPolicy -> "open")
        w.WriteNumber("connection_pool_size", tg.ConnectionPoolSize)
        w.WriteNumber("pool_timeout",         tg.PoolTimeout.TotalSeconds)
        w.WriteBoolean("streaming",           tg.Streaming)
        w.WriteBoolean("inline_keyboards",    tg.InlineKeyboards)
        w.WriteNumber("stream_edit_interval", tg.StreamEditInterval.TotalSeconds)
        w.WriteEndObject()

    // exec_allowed_env_keys (omit when empty — [] = pass all env vars)
    if not (List.isEmpty cfg.ExecAllowedEnvKeys) then
        w.WriteStartArray("exec_allowed_env_keys")
        for k in cfg.ExecAllowedEnvKeys do w.WriteStringValue(k)
        w.WriteEndArray()

    // send_tool_hints (omit when false — false = suppress, matches Python default)
    if cfg.SendToolHints then
        w.WriteBoolean("send_tool_hints", true)
    // send_progress (omit when true — true = stream, matches Python default)
    if not cfg.SendProgress then
        w.WriteBoolean("send_progress", false)
    // send_max_retries (omit when 3 — matches Python default)
    if cfg.SendMaxRetries <> d.SendMaxRetries then
        w.WriteNumber("send_max_retries", cfg.SendMaxRetries)

    // my_tool_allow_set (omit when false — false = read-only, matches Python default)
    if cfg.MyToolAllowSet then
        w.WriteBoolean("my_tool_allow_set", true)

    // ssrf_whitelist (omit when empty — [] = no exemptions, matches Python default)
    if not (List.isEmpty cfg.SsrfWhitelist) then
        w.WriteStartArray("ssrf_whitelist")
        for cidr in cfg.SsrfWhitelist do w.WriteStringValue(cidr)
        w.WriteEndArray()

    // file_read_max_chars (omit when default 131072 — matches Python default)
    if cfg.FileReadMaxChars <> BotSharpConfig.defaults.FileReadMaxChars then
        w.WriteNumber("file_read_max_chars", cfg.FileReadMaxChars)

    // system_prompt_append (omit when None — None = no extra text, matches Python default)
    match cfg.SystemPromptAppend with
    | Some txt when txt.Trim() <> "" -> w.WriteString("system_prompt_append", txt)
    | _ -> ()

    // web_search_api_key (omit when empty — "" = use env var, matches Python default)
    if cfg.WebSearchApiKey <> "" then
        w.WriteString("web_search_api_key", cfg.WebSearchApiKey)

    // web_search_base_url (omit when empty — "" = use env var, matches Python default)
    if cfg.WebSearchBaseUrl <> "" then
        w.WriteString("web_search_base_url", cfg.WebSearchBaseUrl)

    // exec_enable (omit when true — true = enabled, matches Python default)
    if not cfg.ExecEnable then
        w.WriteBoolean("exec_enable", false)

    // web_enable (omit when true — true = enabled, matches Python default)
    if not cfg.WebEnable then
        w.WriteBoolean("web_enable", false)

    // my_tool_enable (omit when true — true = enabled, matches Python default)
    if not cfg.MyToolEnable then
        w.WriteBoolean("my_tool_enable", false)

    // transcription_provider (omit when "groq" — matches Python default)
    if cfg.TranscriptionProvider <> BotSharpConfig.defaults.TranscriptionProvider then
        w.WriteString("transcription_provider", cfg.TranscriptionProvider)

    // transcription_language (omit when None — None = auto-detect, matches Python default)
    match cfg.TranscriptionLanguage with
    | Some lang when lang.Trim() <> "" -> w.WriteString("transcription_language", lang)
    | _ -> ()

    // dream_annotate_line_ages (omit when true — true = annotate, matches Python default)
    if not cfg.DreamAnnotateLineAges then
        w.WriteBoolean("dream_annotate_line_ages", false)

    // provider_extra_headers (omit when empty — {} = no custom headers per provider)
    if not (Map.isEmpty cfg.ProviderExtraHeaders) then
        w.WriteStartObject("provider_extra_headers")
        for providerKv in cfg.ProviderExtraHeaders do
            w.WriteStartObject(providerKv.Key)
            for headerKv in providerKv.Value do
                w.WriteString(headerKv.Key, headerKv.Value)
            w.WriteEndObject()
        w.WriteEndObject()

    // api_port (omit when None — None = only via CLI flag)
    match cfg.ApiPort with
    | Some p -> w.WriteNumber("api_port", p)
    | None   -> ()

    // api_timeout_seconds (omit when default 120 — matches Python default)
    if cfg.ApiTimeoutSeconds <> BotSharpConfig.defaults.ApiTimeoutSeconds then
        w.WriteNumber("api_timeout_seconds", cfg.ApiTimeoutSeconds)

    // api_host (omit when "127.0.0.1" — matches Python default)
    if cfg.ApiHost <> BotSharpConfig.defaults.ApiHost then
        w.WriteString("api_host", cfg.ApiHost)

    // mcp_servers (omit if empty)
    if not (Map.isEmpty cfg.McpServers) then
        w.WriteStartObject("mcp_servers")
        for kv in cfg.McpServers do
            w.WriteStartObject(kv.Key)
            let entry = kv.Value
            match entry.Connection with
            | StdioServer (cmd, args, env) ->
                w.WriteString("type", "stdio")
                w.WriteString("command", cmd)
                w.WriteStartArray("args")
                for a in args do w.WriteStringValue(a)
                w.WriteEndArray()
                if not (Map.isEmpty env) then
                    w.WriteStartObject("env")
                    for e in env do w.WriteString(e.Key, e.Value)
                    w.WriteEndObject()
            | HttpServer (url, headers) ->
                w.WriteString("type", "http")
                w.WriteString("url", url.ToString())
                if not (Map.isEmpty headers) then
                    w.WriteStartObject("headers")
                    for h in headers do w.WriteString(h.Key, h.Value)
                    w.WriteEndObject()
            // tool_timeout (omit when default — 30 s)
            if entry.ToolTimeout <> 30 then
                w.WriteNumber("tool_timeout", entry.ToolTimeout)
            // enabled_tools (omit when ["*"] — default = all tools)
            if entry.EnabledTools <> ["*"] then
                w.WriteStartArray("enabled_tools")
                for t in entry.EnabledTools do w.WriteStringValue(t)
                w.WriteEndArray()
            w.WriteEndObject()
        w.WriteEndObject()

    // ws (omit if not configured)
    match cfg.Ws with
    | None -> ()
    | Some ws ->
        w.WriteStartObject("ws")
        w.WriteBoolean("enabled", ws.Enabled)
        w.WriteNumber("port",    ws.Port)
        match ws.Token with
        | Some t -> w.WriteString("token", ApiKey.value t)
        | None   -> ()
        w.WriteEndObject()

    w.WriteEndObject()
    w.Flush()
    Text.Encoding.UTF8.GetString(ms.ToArray())

/// Write a BotSharpConfig to a file, creating parent directories as needed.
let saveConfig (path: string) (cfg: BotSharpConfig) : Async<Result<unit, string>> =
    async {
        try
            let expanded = expandPath path
            let dir = Path.GetDirectoryName(expanded) |> Option.ofObj
            match dir with
            | Some d when d.Length > 0 -> Directory.CreateDirectory(d) |> ignore
            | _ -> ()
            let json = serializeConfig cfg
            do! File.WriteAllTextAsync(expanded, json) |> Async.AwaitTask
            return Result.Ok ()
        with ex ->
            return Result.Error ex.Message
    }
