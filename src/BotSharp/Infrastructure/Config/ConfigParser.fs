module BotSharp.Infrastructure.Config.ConfigParser

open System
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.Json

// ═══════════════════════════════════════════════════════════════════════════
// Config JSON decoder
//
// All top-level keys are optional; missing keys use BotSharpConfig.defaults.
// Malformed values (wrong type, invalid URL, etc.) accumulate into the error
// list returned as Result<BotSharpConfig, ParseError list>.
//
// Expected JSON shape (all fields optional):
// {
//   "default_model":          "gpt-4o-mini",
//   "default_provider":       "openai",
//   "temperature":            0.7,
//   "max_tokens":             4096,
//   "workspace_path":         "~/.botsharp/workspace",
//   "api_keys":               { "openai": "sk-..." },
//   "base_urls":              { "openai": "https://..." },
//   "mcp_servers":            { "name": { "type": "stdio", "command": "...", ... } },
//   "allow_from":             ["*"],
//   "brave_api_key":          "...",
//   "memory_window_size":     50,
//   "max_iterations":         40,
//   "max_tool_result_chars":  16000,
//   "context_window_tokens":  65536,
//   "context_block_limit":    null,
//   "max_iterations_message": null,
//   "fail_on_tool_error":     false,
//   "disabled_skills":        ["summarize"],
//   "session_ttl_minutes":    0,
//   "timezone":               "Asia/Shanghai",
//   "reasoning_effort":       "medium",
//   "telegram":               { "token": "...", ... },
//   "ws":                     { "enabled": true, "port": 8765, "token": "..." }
// }
// ═══════════════════════════════════════════════════════════════════════════

let private parseFloat (name: string) (el: JsonElement) : float option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetDouble())
    | _ -> None

let private parseMcpServer (name: string) (el: JsonElement) : Result<string * McpServerEntry, ParseError> =
    result {
        let! kind = requireString "type" el
        // Common per-server fields (Python: tool_timeout, enabled_tools)
        let toolTimeout  = tryGetInt  "tool_timeout"  el |> Option.defaultValue 30
                           |> fun v -> if v <= 0 then 30 else v
        let enabledTools =
            match tryGetArray "enabled_tools" el with
            | None -> ["*"]
            | Some arr ->
                arr |> List.choose (fun v ->
                    if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj else None)
        let! connection =
            match kind with
            | "stdio" ->
                result {
                    let! cmd = requireString "command" el
                    let args =
                        tryGetArray "args" el
                        |> Option.defaultValue []
                        |> List.choose (fun v ->
                            if v.ValueKind = JsonValueKind.String then
                                match v.GetString() with
                                | null -> None
                                | s    -> Some s
                            else None)
                    let envEntries =
                        tryGetObject "env" el
                        |> Option.map (fun obj ->
                            obj.EnumerateObject()
                            |> Seq.choose (fun p ->
                                if p.Value.ValueKind = JsonValueKind.String then
                                    match p.Value.GetString() with
                                    | null -> None
                                    | s    -> Some (p.Name, s)
                                else None)
                            |> Map.ofSeq)
                        |> Option.defaultValue Map.empty
                    return StdioServer(cmd, args, envEntries)
                }
            | "http" ->
                result {
                    let! urlStr = requireString "url" el
                    let! url =
                        try Ok (Uri urlStr)
                        with ex -> Error (SchemaError ($"mcp_servers.{name}.url", ex.Message))
                    let headers =
                        tryGetObject "headers" el
                        |> Option.map (fun obj ->
                            obj.EnumerateObject()
                            |> Seq.choose (fun p ->
                                if p.Value.ValueKind = JsonValueKind.String then
                                    match p.Value.GetString() with
                                    | null -> None
                                    | s    -> Some (p.Name, s)
                                else None)
                            |> Map.ofSeq)
                        |> Option.defaultValue Map.empty
                    return HttpServer(url, headers)
                }
            | other ->
                Error (SchemaError ($"mcp_servers.{name}.type",
                                    $"unknown type '{other}', expected 'stdio' or 'http'"))
        return (name, { Connection = connection; ToolTimeout = toolTimeout; EnabledTools = enabledTools })
    }

/// Decode a config JSON document into BotSharpConfig.
/// Missing optional fields use defaults; malformed present fields accumulate
/// into the ParseError list.
let parseConfig (doc: JsonDocument) : Result<BotSharpConfig, ParseError list> =
    let el     = doc.RootElement
    let errs   = System.Collections.Generic.List<ParseError>()
    let d      = BotSharpConfig.defaults

    let defaultModel    = tryGetString "default_model"    el |> Option.defaultValue d.DefaultModel
    let defaultProvider = tryGetString "default_provider" el |> Option.defaultValue d.DefaultProvider
    let temperature     = parseFloat  "temperature"       el |> Option.defaultValue d.Temperature
    let maxTokens       = tryGetInt   "max_tokens"        el |> Option.defaultValue d.MaxTokens
    let workspacePath   =
        tryGetString "workspace_path" el
        |> Option.map expandPath
        |> Option.defaultValue d.WorkspacePath
    let memoryWindowSize   = tryGetInt "memory_window_size"     el |> Option.defaultValue d.MemoryWindowSize
    let maxIterations      = tryGetInt "max_iterations"         el |> Option.defaultValue d.MaxIterations
    let subagentMaxIterations = tryGetInt "subagent_max_iterations" el |> Option.defaultValue d.SubagentMaxIterations
    let maxMessages        = tryGetInt "max_messages"            el |> Option.defaultValue d.MaxMessages
    let maxToolResultChars   = tryGetInt "max_tool_result_chars"  el |> Option.defaultValue d.MaxToolResultChars
    let contextWindowTokens  = tryGetInt "context_window_tokens" el |> Option.defaultValue d.ContextWindowTokens
    let contextBlockLimit      = tryGetInt    "context_block_limit"    el  // None = use computed budget
    let maxIterationsMessage   = tryGetString "max_iterations_message" el  // None = default template
    let failOnToolError        = tryGetBool   "fail_on_tool_error"     el |> Option.defaultValue d.FailOnToolError
    let disabledSkills =
        tryGetArray "disabled_skills" el
        |> Option.defaultValue []
        |> List.choose (fun v ->
            if v.ValueKind = JsonValueKind.String then
                match v.GetString() with
                | null | "" -> None
                | s         -> Some s
            else None)
    let sessionTtlMinutes  = tryGetInt    "session_ttl_minutes"  el |> Option.defaultValue d.SessionTtlMinutes
    let sessionCleanupDays = tryGetInt    "session_cleanup_days" el |> Option.defaultValue d.SessionCleanupDays
    let timezone           = tryGetString "timezone"             el  // None = use system local timezone
    let execTimeoutSeconds = tryGetInt    "exec_timeout_seconds" el |> Option.defaultValue d.ExecTimeoutSeconds
    let execSandbox        = tryGetString "exec_sandbox"         el |> Option.defaultValue d.ExecSandbox
    // heartbeat sub-object (Python: heartbeat.enabled / interval_s / keep_recent_messages)
    let (heartbeatEnabled, heartbeatIntervalSeconds, heartbeatKeepRecentMessages) =
        match tryGetObject "heartbeat" el with
        | None ->
            (d.HeartbeatEnabled, d.HeartbeatIntervalSeconds, d.HeartbeatKeepRecentMessages)
        | Some hb ->
            let enabled  = tryGetBool "enabled"  hb |> Option.defaultValue d.HeartbeatEnabled
            let interval =
                // Python key is interval_s; also accept interval_seconds for convenience
                (tryGetInt "interval_s" hb |> Option.orElse (tryGetInt "interval_seconds" hb))
                |> Option.defaultValue d.HeartbeatIntervalSeconds
            let keepRecent = tryGetInt "keep_recent_messages" hb |> Option.defaultValue d.HeartbeatKeepRecentMessages
            (enabled, interval, keepRecent)

    let webSearchProvider =
        tryGetString "web_search_provider" el
        |> Option.bind (fun s -> let t = s.Trim().ToLowerInvariant() in if t = "" then None else Some t)

    let restrictToWorkspace = tryGetBool "restrict_to_workspace" el |> Option.defaultValue d.RestrictToWorkspace
    let providerRetryMode =
        tryGetString "provider_retry_mode" el
        |> Option.map (fun s -> s.Trim().ToLowerInvariant())
        |> Option.filter (fun s -> s = "standard" || s = "persistent")
        |> Option.defaultValue d.ProviderRetryMode

    let unifiedSession = tryGetBool "unified_session" el |> Option.defaultValue d.UnifiedSession

    // web_proxy / web_search_timeout (Python: web.proxy, web.search.timeout)
    let webProxyUrl =
        tryGetString "web_proxy" el
        |> Option.orElse (tryGetString "web_proxy_url" el)
        |> Option.bind (fun s -> if s.Trim() = "" then None else Some (s.Trim()))
    let webSearchTimeout =
        tryGetInt "web_search_timeout" el |> Option.defaultValue d.WebSearchTimeout
        |> fun v -> if v <= 0 then d.WebSearchTimeout else v

    // dream sub-object (Python: dream.model_override / max_iterations / interval_h / max_batch_size)
    let (dreamModelOverride, dreamMaxIterations, dreamIntervalHours, dreamMaxBatchSize) =
        match tryGetObject "dream" el with
        | None ->
            (d.DreamModelOverride, d.DreamMaxIterations, d.DreamIntervalHours, d.DreamMaxBatchSize)
        | Some dr ->
            let modelOverride =
                tryGetString "model_override" dr
                |> Option.orElse (tryGetString "model" dr)    // Python accepts "model" alias
                |> Option.bind (fun s -> if s.Trim() = "" then None else Some (s.Trim()))
            let maxIter   = tryGetInt "max_iterations"  dr |> Option.defaultValue d.DreamMaxIterations
            let interval  = tryGetInt "interval_h"      dr |> Option.defaultValue d.DreamIntervalHours
            let batchSize = tryGetInt "max_batch_size"  dr |> Option.defaultValue d.DreamMaxBatchSize
                            |> fun v -> if v <= 0 then d.DreamMaxBatchSize else v
            (modelOverride, maxIter, interval, batchSize)

    // web search max_results (Python: web.search.max_results)
    let webSearchMaxResults =
        tryGetInt "web_search_max_results" el |> Option.defaultValue d.WebSearchMaxResults
        |> fun v -> if v <= 0 then d.WebSearchMaxResults else v

    // exec path_append (Python: exec.path_append)
    let execPathAppend =
        tryGetString "exec_path_append" el
        |> Option.orElse (tryGetString "path_append" el)
        |> Option.defaultValue d.ExecPathAppend

    // exec_allowed_env_keys (Python: exec.allowed_env_keys); [] = pass all env vars
    let execAllowedEnvKeys =
        match tryGetArray "exec_allowed_env_keys" el
              |> Option.orElse (tryGetArray "allowed_env_keys" el) with
        | None -> d.ExecAllowedEnvKeys
        | Some arr ->
            arr |> List.choose (fun v ->
                if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj else None)

    // channels sub-fields (Python: channels.send_tool_hints / send_progress / send_max_retries)
    let sendToolHints  = tryGetBool "send_tool_hints"  el |> Option.defaultValue d.SendToolHints
    let sendProgress   = tryGetBool "send_progress"    el |> Option.defaultValue d.SendProgress
    let sendMaxRetries = tryGetInt  "send_max_retries" el |> Option.defaultValue d.SendMaxRetries
                         |> fun v -> if v < 0 then d.SendMaxRetries else v

    // my_tool_allow_set (Python: my_tool.allow_set; default false)
    let myToolAllowSet = tryGetBool "my_tool_allow_set" el |> Option.defaultValue d.MyToolAllowSet

    // ssrf_whitelist (Python: tools.ssrf_whitelist); [] = no CIDR exemptions
    let ssrfWhitelist =
        match tryGetArray "ssrf_whitelist" el with
        | None -> d.SsrfWhitelist
        | Some arr ->
            arr |> List.choose (fun v ->
                if v.ValueKind = JsonValueKind.String then
                    match v.GetString() with
                    | null | "" -> None
                    | s         -> Some (s.Trim())
                else None)

    // file_read_max_chars (Python: tools.file_read_max_chars; default 131072)
    // Negative or zero values are clamped to the default.
    let fileReadMaxChars =
        match tryGetInt "file_read_max_chars" el with
        | Some v when v > 0 -> v
        | Some _            -> d.FileReadMaxChars   // clamp invalid to default
        | None              -> d.FileReadMaxChars

    // system_prompt_append (Python: system_prompt_append); None = no extra text
    let systemPromptAppend =
        match tryGetString "system_prompt_append" el with
        | Some s when s.Trim() <> "" -> Some s
        | _                          -> None

    // web_search_api_key (Python: tools.web.search.api_key; "" = use env var)
    let webSearchApiKey  = tryGetString "web_search_api_key"  el |> Option.defaultValue d.WebSearchApiKey

    // web_search_base_url (Python: tools.web.search.base_url; "" = use env var)
    let webSearchBaseUrl = tryGetString "web_search_base_url" el |> Option.defaultValue d.WebSearchBaseUrl

    // exec_enable (Python: tools.exec.enable; true = register exec tool)
    let execEnable = tryGetBool "exec_enable" el |> Option.defaultValue d.ExecEnable

    // web_enable (Python: tools.web.enable; true = register web tools)
    let webEnable    = tryGetBool "web_enable"    el |> Option.defaultValue d.WebEnable

    // my_tool_enable (Python: tools.my.enable; true = register the 'my' tool)
    let myToolEnable = tryGetBool "my_tool_enable" el |> Option.defaultValue d.MyToolEnable

    // transcription_provider (Python: channels.transcription_provider; "groq" = default)
    let transcriptionProvider = tryGetString "transcription_provider" el |> Option.defaultValue d.TranscriptionProvider

    // transcription_language (Python: channels.transcription_language; None = auto-detect)
    let transcriptionLanguage =
        match tryGetString "transcription_language" el with
        | Some s when s.Trim() <> "" -> Some (s.Trim())
        | _ -> None

    // dream_annotate_line_ages (Python: dream.annotate_line_ages; true = annotate with git-blame ages)
    let dreamAnnotateLineAges = tryGetBool "dream_annotate_line_ages" el |> Option.defaultValue d.DreamAnnotateLineAges

    // provider_extra_headers (Python: providers.<id>.extra_headers)
    // Format: { "openai": { "My-Header": "value" }, "anthropic": { ... } }
    let providerExtraHeaders =
        match tryGetObject "provider_extra_headers" el with
        | None -> Map.empty
        | Some obj ->
            obj.EnumerateObject()
            |> Seq.choose (fun providerProp ->
                if providerProp.Value.ValueKind = JsonValueKind.Object then
                    let headers =
                        providerProp.Value.EnumerateObject()
                        |> Seq.choose (fun hdr ->
                            if hdr.Value.ValueKind = JsonValueKind.String then
                                match hdr.Value.GetString() with
                                | null | "" -> None
                                | v         -> Some (hdr.Name, v)
                            else None)
                        |> Map.ofSeq
                    if Map.isEmpty headers then None
                    else Some (providerProp.Name, headers)
                else None)
            |> Map.ofSeq

    // api_port (Python: api.port; None = only started via --api-port CLI flag)
    let apiPort =
        match tryGetInt "api_port" el with
        | Some v when v >= 0 && v < 65536 -> Some v
        | _ -> None

    // api_timeout_seconds (Python: api.timeout; default 120; 0 = no timeout)
    let apiTimeoutSeconds =
        match tryGetInt "api_timeout_seconds" el with
        | Some v when v >= 0 -> v
        | _ -> d.ApiTimeoutSeconds

    // api_host (Python: api.host; default "127.0.0.1")
    let apiHost =
        match tryGetString "api_host" el with
        | Some h when h.Trim() <> "" -> h.Trim()
        | _ -> d.ApiHost

    let apiKeys =
        match tryGetObject "api_keys" el with
        | None -> Map.empty
        | Some obj ->
            obj.EnumerateObject()
            |> Seq.choose (fun prop ->
                if prop.Value.ValueKind = JsonValueKind.String then
                    match prop.Value.GetString() with
                    | null -> None
                    | raw  ->
                        match ApiKey.create raw with
                        | Ok key -> Some (prop.Name, key)
                        | Error msg ->
                            errs.Add(SchemaError ($"api_keys.{prop.Name}", msg))
                            None
                else
                    errs.Add(SchemaError ($"api_keys.{prop.Name}", "expected string"))
                    None)
            |> Map.ofSeq

    let baseUrls =
        match tryGetObject "base_urls" el with
        | None -> Map.empty
        | Some obj ->
            obj.EnumerateObject()
            |> Seq.choose (fun prop ->
                if prop.Value.ValueKind = JsonValueKind.String then
                    match prop.Value.GetString() with
                    | null | "" -> None
                    | url       -> Some (prop.Name, url)
                else None)
            |> Map.ofSeq

    let mcpServers =
        match tryGetObject "mcp_servers" el with
        | None -> Map.empty
        | Some obj ->
            obj.EnumerateObject()
            |> Seq.choose (fun prop ->
                match parseMcpServer prop.Name prop.Value with
                | Ok (k, v) -> Some (k, v)
                | Error e   -> errs.Add(e); None)
            |> Map.ofSeq

    // Parse allow_from once at load time into AllowList DU.
    // AnyoneAllowed when "*" is present; AllowedSet otherwise.
    // No caller ever inspects the raw string list again.
    let allowFrom =
        match tryGetArray "allow_from" el with
        | None ->
            d.AllowFrom    // AnyoneAllowed (default)
        | Some items ->
            items
            |> List.choose (fun v ->
                if v.ValueKind = JsonValueKind.String then
                    match v.GetString() with
                    | null -> None
                    | s    -> Some s
                else None)
            |> AllowList.parse

    let braveApiKey =
        tryGetString "brave_api_key" el
        |> Option.bind (fun s ->
            match ApiKey.create s with
            | Ok k  -> Some k
            | Error _ -> None)

    let reasoningEffort =
        tryGetString "reasoning_effort" el
        |> Option.bind (fun s ->
            match s.Trim().ToLowerInvariant() with
            | "low"      -> Some Low
            | "medium"   -> Some Medium
            | "high"     -> Some High
            | "adaptive" -> Some Adaptive
            | other ->
                errs.Add(SchemaError ("reasoning_effort",
                                     $"unknown value '{other}', expected low/medium/high/adaptive"))
                None)

    // ── Parse optional [telegram] section ─────────────────────────────────────
    let telegramConfig =
        match tryGetObject "telegram" el with
        | None -> None
        | Some tg ->
            match tryGetString "token" tg with
            | None -> None   // no token → silently skip telegram
            | Some rawToken ->
                match TelegramBotToken.create rawToken with
                | Error msg ->
                    errs.Add(SchemaError ("telegram.token", msg))
                    None
                | Ok token ->
                    let tgAllowFrom =
                        match tryGetArray "allow_from" tg with
                        | None -> AnyoneAllowed
                        | Some items ->
                            items
                            |> List.choose (fun v ->
                                if v.ValueKind = JsonValueKind.String then
                                    match v.GetString() with
                                    | null -> None
                                    | s    -> Some s
                                else None)
                            |> AllowList.parse

                    let proxy =
                        tryGetString "proxy" tg
                        |> Option.bind (fun s ->
                            try Some (Uri(s))
                            with ex ->
                                errs.Add(SchemaError ("telegram.proxy", ex.Message))
                                None)

                    let groupPolicy =
                        match tryGetString "group_policy" tg with
                        | Some "mention" -> MentionPolicy
                        | _              -> OpenPolicy   // default open

                    let parseBool (name: string) def =
                        match (tg : JsonElement).TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.True  -> true
                        | true, v when v.ValueKind = JsonValueKind.False -> false
                        | _ -> def

                    let parseFloatField (name: string) def =
                        match (tg : JsonElement).TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
                        | _ -> def

                    let parseIntField (name: string) def =
                        match (tg : JsonElement).TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                        | _ -> def

                    let connectionPoolSize =
                        let raw = parseIntField "connection_pool_size" 8
                        if raw <= 0 then
                            errs.Add(SchemaError ("telegram.connection_pool_size", sprintf "must be > 0, got %d" raw))
                            8
                        else raw

                    Some {
                        Token              = token
                        AllowFrom          = tgAllowFrom
                        Proxy              = proxy
                        ReplyToMessage     = parseBool "reply_to_message" false
                        ReactEmoji         = tryGetString "react_emoji" tg
                        GroupPolicy        = groupPolicy
                        ConnectionPoolSize = connectionPoolSize
                        PoolTimeout        = TimeSpan.FromSeconds (parseFloatField "pool_timeout"         30.0)
                        Streaming          = parseBool "streaming" true
                        InlineKeyboards    = parseBool "inline_keyboards" false
                        StreamEditInterval = TimeSpan.FromSeconds (parseFloatField "stream_edit_interval" 0.5)
                    }

    // ── Parse optional [ws] section ───────────────────────────────────────────
    let wsConfig =
        match tryGetObject "ws" el with
        | None -> None
        | Some ws ->
            let enabled =
                match (ws : JsonElement).TryGetProperty("enabled") with
                | true, v when v.ValueKind = JsonValueKind.True  -> true
                | true, v when v.ValueKind = JsonValueKind.False -> false
                | _ -> false   // must opt-in explicitly
            if not enabled then None
            else
                let port =
                    match (ws : JsonElement).TryGetProperty("port") with
                    | true, v when v.ValueKind = JsonValueKind.Number ->
                        let raw = v.GetInt32()
                        if raw < 1 || raw > 65535 then
                            errs.Add(SchemaError ("ws.port", sprintf "must be 1–65535, got %d" raw))
                            8765
                        else raw
                    | _ -> 8765   // default port
                let token =
                    match tryGetString "token" ws with
                    | None -> None
                    | Some raw ->
                        match ApiKey.create raw with
                        | Ok k   -> Some k
                        | Error e ->
                            errs.Add(SchemaError ("ws.token", e))
                            None
                Some { Port = port; Token = token; Enabled = true }

    // ── Parse optional [inter_agent] section ────────────────────────────────
    // ── Parse optional [discord] section ─────────────────────────────────────
    let discordConfig =
        match tryGetObject "discord" el with
        | None -> None
        | Some dc ->
            match tryGetString "token" dc with
            | None -> None
            | Some token ->
                let allowFrom =
                    match tryGetArray "allow_from" dc with
                    | None -> AnyoneAllowed
                    | Some elems ->
                        let ids = elems |> List.choose (fun (e: JsonElement) -> if e.ValueKind = JsonValueKind.String then e.GetString() |> Option.ofObj else None)
                        if ids |> List.exists (fun s -> s = "*") then AnyoneAllowed
                        else AllowedSet (Set.ofList ids)
                Some { Token = token; AllowFrom = allowFrom } : DiscordChannelConfig option

    let interAgentConfig =
        match tryGetObject "inter_agent" el with
        | None -> None
        | Some ia ->
            let enabled = match tryGetBool "enabled" ia with Some b -> b | None -> false
            if not enabled then None
            else
                let cfg : InterAgentChannelConfig = {
                    Enabled             = true
                    Port                = match tryGetInt "port" ia with Some p -> p | None -> 18800
                    InstanceName        = match tryGetString "instance_name" ia with Some s -> s | None -> ""
                    AuditWebhookUrl     = tryGetString "audit_webhook_url" ia
                    MaxRoundsPerSession = match tryGetInt "max_rounds_per_session" ia with Some n -> n | None -> 30
                    TaskTtlSeconds      = match tryGetInt "task_ttl_seconds" ia with Some n -> n | None -> 3600
                }
                Some cfg

    if errs.Count > 0 then
        Error (errs |> Seq.toList)
    else
        Ok {
            DefaultModel     = defaultModel
            DefaultProvider  = defaultProvider
            Temperature      = temperature
            MaxTokens        = maxTokens
            WorkspacePath    = workspacePath
            ApiKeys          = apiKeys
            BaseUrls         = baseUrls
            McpServers       = mcpServers
            AllowFrom        = allowFrom
            BraveApiKey      = braveApiKey
            MemoryWindowSize   = memoryWindowSize
            MaxIterations      = maxIterations
            SubagentMaxIterations = subagentMaxIterations
            MaxMessages        = maxMessages
            MaxToolResultChars   = maxToolResultChars
            ReasoningEffort      = reasoningEffort
            Telegram             = telegramConfig
            Ws                   = wsConfig
            ContextWindowTokens  = contextWindowTokens
            ContextBlockLimit    = contextBlockLimit
            MaxIterationsMessage = maxIterationsMessage
            FailOnToolError      = failOnToolError
            DisabledSkills       = disabledSkills
            SessionTtlMinutes    = sessionTtlMinutes
            SessionCleanupDays   = sessionCleanupDays
            Timezone             = timezone
            ExecTimeoutSeconds   = execTimeoutSeconds
            HeartbeatEnabled              = heartbeatEnabled
            HeartbeatIntervalSeconds      = heartbeatIntervalSeconds
            HeartbeatKeepRecentMessages   = heartbeatKeepRecentMessages
            DreamModelOverride  = dreamModelOverride
            DreamMaxIterations  = dreamMaxIterations
            DreamIntervalHours  = dreamIntervalHours
            DreamMaxBatchSize   = dreamMaxBatchSize
            WebSearchProvider   = webSearchProvider
            WebSearchMaxResults = webSearchMaxResults
            RestrictToWorkspace = restrictToWorkspace
            ProviderRetryMode   = providerRetryMode
            UnifiedSession      = unifiedSession
            WebProxyUrl         = webProxyUrl
            WebSearchTimeout    = webSearchTimeout
            ExecPathAppend      = execPathAppend
            ExecAllowedEnvKeys  = execAllowedEnvKeys
            ExecSandbox         = execSandbox
            SendToolHints  = sendToolHints
            SendProgress   = sendProgress
            SendMaxRetries = sendMaxRetries
            MyToolAllowSet     = myToolAllowSet
            SsrfWhitelist      = ssrfWhitelist
            FileReadMaxChars   = fileReadMaxChars
            SystemPromptAppend = systemPromptAppend
            WebSearchApiKey    = webSearchApiKey
            WebSearchBaseUrl   = webSearchBaseUrl
            ExecEnable             = execEnable
            WebEnable              = webEnable
            MyToolEnable           = myToolEnable
            TranscriptionProvider  = transcriptionProvider
            TranscriptionLanguage  = transcriptionLanguage
            DreamAnnotateLineAges  = dreamAnnotateLineAges
            ProviderExtraHeaders   = providerExtraHeaders
            ApiPort                = apiPort
            ApiTimeoutSeconds      = apiTimeoutSeconds
            ApiHost                = apiHost
            Discord                = discordConfig
            InterAgent             = interAgentConfig
            FallbackModels         = tryGetArray "fallback_models" el |> Option.defaultValue [] |> List.choose (fun (e: JsonElement) -> if e.ValueKind = JsonValueKind.String then e.GetString() |> Option.ofObj else None)
        }
