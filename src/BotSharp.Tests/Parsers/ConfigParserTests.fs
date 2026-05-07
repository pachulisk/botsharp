module BotSharp.Tests.Parsers.ConfigParserTests

open System
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Config.ConfigParser

// ═══════════════════════════════════════════════════════════════════════════
// Helper
// ═══════════════════════════════════════════════════════════════════════════

let private parseJson (json: string) =
    use doc = JsonDocument.Parse(json)
    parseConfig doc

// ═══════════════════════════════════════════════════════════════════════════
// Defaults
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``empty JSON object uses defaults`` () =
    match parseJson "{}" with
    | Ok cfg ->
        Assert.Equal(BotSharpConfig.defaults.DefaultModel, cfg.DefaultModel)
        Assert.Equal(BotSharpConfig.defaults.MaxTokens, cfg.MaxTokens)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

// ═══════════════════════════════════════════════════════════════════════════
// Top-level scalar overrides
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``explicit fields override defaults`` () =
    let json = """{"default_model":"claude-3","temperature":0.3,"max_tokens":2048}"""
    match parseJson json with
    | Ok cfg ->
        Assert.Equal("claude-3", cfg.DefaultModel)
        Assert.Equal(0.3, cfg.Temperature)
        Assert.Equal(2048, cfg.MaxTokens)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

// ═══════════════════════════════════════════════════════════════════════════
// api_keys
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``api_keys are parsed`` () =
    let json = """{"api_keys":{"openai":"sk-test1234567890abcdefghijklmnopqrst"}}"""
    match parseJson json with
    | Ok cfg -> Assert.True(cfg.ApiKeys.ContainsKey("openai"), "Expected 'openai' key in ApiKeys")
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

// ═══════════════════════════════════════════════════════════════════════════
// allow_from
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allow_from wildcard yields AnyoneAllowed`` () =
    let json = """{"allow_from":["*"]}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(AnyoneAllowed, cfg.AllowFrom)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``allow_from specific IDs yields AllowedSet`` () =
    let json = """{"allow_from":["user1","user2"]}"""
    match parseJson json with
    | Ok cfg ->
        Assert.Equal(AllowedSet (Set.ofList ["user1"; "user2"]), cfg.AllowFrom)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

// ═══════════════════════════════════════════════════════════════════════════
// mcp_servers
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``mcp_servers stdio parses correctly`` () =
    let json = """{"mcp_servers":{"test":{"type":"stdio","command":"python","args":["-m","server"]}}}"""
    match parseJson json with
    | Ok cfg ->
        match cfg.McpServers.TryFind("test") with
        | Some e ->
            match e.Connection with
            | StdioServer (cmd, args, env) ->
                Assert.Equal("python", cmd)
                Assert.Equal<string list>(["-m"; "server"], args)
                Assert.True(Map.isEmpty env, "Expected empty env map")
                Assert.Equal(30, e.ToolTimeout)
                Assert.Equal<string list>(["*"], e.EnabledTools)
            | other -> Assert.Fail($"Expected StdioServer, got {other}")
        | None -> Assert.Fail("Expected 'test' key in McpServers")
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``mcp_servers http parses correctly`` () =
    let json = """{"mcp_servers":{"test":{"type":"http","url":"http://localhost:8080"}}}"""
    match parseJson json with
    | Ok cfg ->
        match cfg.McpServers.TryFind("test") with
        | Some e ->
            match e.Connection with
            | HttpServer (url, headers) ->
                Assert.Equal(Uri("http://localhost:8080"), url)
                Assert.True(Map.isEmpty headers, "Expected empty headers map")
            | other -> Assert.Fail($"Expected HttpServer, got {other}")
        | None -> Assert.Fail("Expected 'test' key in McpServers")
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``mcp_servers unknown type yields error`` () =
    let json = """{"mcp_servers":{"test":{"type":"grpc"}}}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected non-empty error list")
    | Ok cfg -> Assert.Fail($"Expected Error, got Ok with McpServers: {cfg.McpServers}")

// ═══════════════════════════════════════════════════════════════════════════
// telegram section
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``telegram section parses correctly`` () =
    let json = """{"telegram":{"token":"123456789:ABCdefGHIjklMNOpqrsTUVwxyz1234567890"}}"""
    match parseJson json with
    | Ok cfg ->
        Assert.True(cfg.Telegram.IsSome, "Expected Telegram config to be Some")
        let tg = cfg.Telegram.Value
        Assert.True(tg.Streaming, "Expected Streaming to default to true")
        Assert.Equal(OpenPolicy, tg.GroupPolicy)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``telegram connection_pool_size of zero yields error`` () =
    let json = """{"telegram":{"token":"123456789:ABCdefGHIjklMNOpqrsTUVwxyz1234567890","connection_pool_size":0}}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected non-empty error list")
    | Ok cfg -> Assert.Fail($"Expected Error, got Ok with Telegram: {cfg.Telegram}")

// ═══════════════════════════════════════════════════════════════════════════
// reasoning_effort
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``reasoning_effort low parses to Low`` () =
    let json = """{"reasoning_effort":"low"}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(Some Low, cfg.ReasoningEffort)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``reasoning_effort medium parses to Medium`` () =
    let json = """{"reasoning_effort":"medium"}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(Some Medium, cfg.ReasoningEffort)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``reasoning_effort high parses to High`` () =
    let json = """{"reasoning_effort":"high"}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(Some High, cfg.ReasoningEffort)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``reasoning_effort adaptive parses to Adaptive`` () =
    let json = """{"reasoning_effort":"adaptive"}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(Some Adaptive, cfg.ReasoningEffort)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``reasoning_effort absent defaults to None`` () =
    let json = """{}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(None, cfg.ReasoningEffort)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``reasoning_effort unknown value yields error`` () =
    let json = """{"reasoning_effort":"extreme"}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected non-empty error list")
    | Ok cfg -> Assert.Fail($"Expected Error for unknown reasoning_effort, got Ok: {cfg.ReasoningEffort}")

// ═══════════════════════════════════════════════════════════════════════════
// max_tool_result_chars
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``max_tool_result_chars parses correctly`` () =
    let json = """{"max_tool_result_chars":8192}"""
    match parseJson json with
    | Ok cfg -> Assert.Equal(8192, cfg.MaxToolResultChars)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``max_tool_result_chars absent uses default`` () =
    match parseJson "{}" with
    | Ok cfg  -> Assert.Equal(BotSharpConfig.defaults.MaxToolResultChars, cfg.MaxToolResultChars)
    | Error e -> Assert.Fail($"Expected Ok: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// ws (WebSocket server config)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ws absent yields None`` () =
    match parseJson "{}" with
    | Ok cfg  -> Assert.Equal(None, cfg.Ws)
    | Error e -> Assert.Fail($"Expected Ok: {e}")

[<Fact>]
let ``ws enabled=false yields None`` () =
    let json = """{"ws":{"enabled":false,"port":9000}}"""
    match parseJson json with
    | Ok cfg  -> Assert.Equal(None, cfg.Ws)
    | Error e -> Assert.Fail($"Expected Ok: {e}")

[<Fact>]
let ``ws enabled=true parses port and Enabled`` () =
    let json = """{"ws":{"enabled":true,"port":9876}}"""
    match parseJson json with
    | Ok cfg ->
        match cfg.Ws with
        | None   -> Assert.Fail("Expected Ws to be Some")
        | Some w ->
            Assert.Equal(9876,  w.Port)
            Assert.Equal(true,  w.Enabled)
            Assert.Equal(None,  w.Token)
    | Error e -> Assert.Fail($"Expected Ok: {e}")

[<Fact>]
let ``ws port absent uses default 8765`` () =
    let json = """{"ws":{"enabled":true}}"""
    match parseJson json with
    | Ok cfg ->
        match cfg.Ws with
        | None   -> Assert.Fail("Expected Ws to be Some")
        | Some w -> Assert.Equal(8765, w.Port)
    | Error e -> Assert.Fail($"Expected Ok: {e}")

[<Fact>]
let ``ws token parses to ApiKey`` () =
    let json = """{"ws":{"enabled":true,"port":8765,"token":"my-ws-secret-key"}}"""
    match parseJson json with
    | Ok cfg ->
        match cfg.Ws with
        | None   -> Assert.Fail("Expected Ws to be Some")
        | Some w ->
            match w.Token with
            | None   -> Assert.Fail("Expected Token to be Some")
            | Some t -> Assert.Equal("my-ws-secret-key", ApiKey.value t)
    | Error e -> Assert.Fail($"Expected Ok: {e}")

[<Fact>]
let ``ws invalid port yields error`` () =
    let json = """{"ws":{"enabled":true,"port":99999}}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected error for out-of-range port")
    | Ok cfg     -> Assert.Fail($"Expected Error for port 99999, got Ok: {cfg.Ws}")

// ── base_urls ────────────────────────────────────────────────────────────────

[<Fact>]
let ``base_urls parses provider-url map`` () =
    let json = """{"base_urls":{"openai":"https://my-proxy.example.com/v1","anthropic":"https://other.example.com"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        Assert.Equal("https://my-proxy.example.com/v1", cfg.BaseUrls["openai"])
        Assert.Equal("https://other.example.com",        cfg.BaseUrls["anthropic"])

[<Fact>]
let ``base_urls absent defaults to empty map`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Empty(cfg.BaseUrls)

[<Fact>]
let ``base_urls skips empty-string values`` () =
    let json = """{"base_urls":{"openai":"","anthropic":"https://ok.example.com"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        // Empty string for openai is silently dropped
        Assert.False(cfg.BaseUrls.ContainsKey("openai"), "Empty URL should be dropped")
        Assert.Equal("https://ok.example.com", cfg.BaseUrls["anthropic"])

// ── brave_api_key ────────────────────────────────────────────────────────────

[<Fact>]
let ``brave_api_key parses to Some ApiKey`` () =
    let json = """{"brave_api_key":"brave-key-abc123"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.BraveApiKey with
        | None   -> Assert.Fail("Expected Some BraveApiKey")
        | Some k -> Assert.Equal("brave-key-abc123", ApiKey.value k)

[<Fact>]
let ``brave_api_key absent defaults to None`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.BraveApiKey)

// ── context_window_tokens ────────────────────────────────────────────────────

[<Fact>]
let ``context_window_tokens parses positive integer`` () =
    let json = """{"context_window_tokens":128000}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(128000, cfg.ContextWindowTokens)

[<Fact>]
let ``context_window_tokens absent defaults to zero`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(0, cfg.ContextWindowTokens)

// ── api_keys — error branches ─────────────────────────────────────────────────

[<Fact>]
let ``api_keys with non-string value yields error`` () =
    // The int value triggers the `else errs.Add(SchemaError(..., "expected string"))` branch.
    let json = """{"api_keys":{"openai":42}}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected non-empty error list")
    | Ok cfg -> Assert.Fail($"Expected Error for non-string api key value, got Ok: {cfg.ApiKeys}")

[<Fact>]
let ``api_keys with empty string key yields error`` () =
    // ApiKey.create "" returns Error → errs.Add(SchemaError) → Error result.
    let json = """{"api_keys":{"openai":""}}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected non-empty error list for empty api key")
    | Ok cfg -> Assert.Fail($"Expected Error for empty api key string, got Ok: {cfg.ApiKeys}")

// ── allow_from — edge cases ───────────────────────────────────────────────────

[<Fact>]
let ``allow_from empty array yields AllowedSet with empty set`` () =
    // Empty list → AllowList.parse [] → AllowedSet Set.empty (NOT AnyoneAllowed)
    let json = """{"allow_from":[]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(AllowedSet Set.empty, cfg.AllowFrom)

// ── mcp_servers — additional branches ────────────────────────────────────────

[<Fact>]
let ``mcp_servers stdio with non-empty env map parses env`` () =
    let json = """{"mcp_servers":{"srv":{"type":"stdio","command":"python","env":{"KEY":"VALUE"}}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("srv") with
        | Some e ->
            match e.Connection with
            | StdioServer (_, _, env) ->
                Assert.True(env.ContainsKey("KEY"), "Expected 'KEY' in env map")
                Assert.Equal("VALUE", env["KEY"])
            | other -> Assert.Fail($"Expected StdioServer connection, got {other}")
        | None -> Assert.Fail("Expected 'srv' in McpServers")

[<Fact>]
let ``mcp_servers http with non-empty headers map parses headers`` () =
    let json = """{"mcp_servers":{"api":{"type":"http","url":"http://localhost:8080","headers":{"X-Auth":"token123"}}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("api") with
        | Some e ->
            match e.Connection with
            | HttpServer (_, headers) ->
                Assert.True(headers.ContainsKey("X-Auth"), "Expected 'X-Auth' in headers")
                Assert.Equal("token123", headers["X-Auth"])
            | other -> Assert.Fail($"Expected HttpServer connection, got {other}")
        | None -> Assert.Fail("Expected 'api' in McpServers")

[<Fact>]
let ``mcp_servers http with invalid URL yields error`` () =
    // Uri("not a url") throws → SchemaError added → Error result.
    let json = """{"mcp_servers":{"bad":{"type":"http","url":"not a url"}}}"""
    match parseJson json with
    | Error errs -> Assert.True(errs.Length > 0, "Expected non-empty error list for invalid URL")
    | Ok cfg -> Assert.Fail($"Expected Error for invalid http URL, got Ok: {cfg.McpServers}")

[<Fact>]
let ``mcp_servers unix socket parses correctly`` () =
    let json = """{"mcp_servers":{"sock":{"type":"unix","socket_path":"/tmp/myserver.sock"}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("sock") with
        | None -> Assert.Fail("Expected 'sock' key in McpServers")
        | Some entry ->
            match entry.Connection with
            | UnixSocketServer path -> Assert.Equal("/tmp/myserver.sock", path)
            | other -> Assert.Fail($"Expected UnixSocketServer, got {other}")

[<Fact>]
let ``mcp_servers unix socket missing socket_path is skipped`` () =
    // socket_path is required; missing it → error added, server entry skipped
    let json = """{"mcp_servers":{"bad":{"type":"unix"}}}"""
    match parseJson json with
    | Error _errs -> ()   // acceptable — requireString "socket_path" returns MissingField
    | Ok cfg -> Assert.Equal(0, cfg.McpServers.Count)

// ── telegram — additional branches ────────────────────────────────────────────

[<Fact>]
let ``telegram section without token field is silently skipped`` () =
    // tryGetString "token" tg returns None → silently returns None (no error added).
    let json = """{"telegram":{"polling_interval":5}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok (telegram without token is silently skipped), got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.Telegram)

[<Fact>]
let ``telegram group_policy mention parses to MentionPolicy`` () =
    let json = """{"telegram":{"token":"123456789:ABCdefGHIjklMNOpqrsTUVwxyz1234567890","group_policy":"mention"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Telegram with
        | None    -> Assert.Fail("Expected Telegram to be Some")
        | Some tg -> Assert.Equal(MentionPolicy, tg.GroupPolicy)

[<Fact>]
let ``telegram reply_to_message true parses to true`` () =
    let json = """{"telegram":{"token":"123456789:ABCdefGHIjklMNOpqrsTUVwxyz1234567890","reply_to_message":true}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Telegram with
        | None    -> Assert.Fail("Expected Telegram to be Some")
        | Some tg -> Assert.True(tg.ReplyToMessage, "Expected reply_to_message to be true")

// ── ws — missing enabled field defaults to false (None) ──────────────────────

[<Fact>]
let ``ws with enabled field absent defaults to not enabled`` () =
    // When "enabled" key is absent, the | _ -> false arm triggers → wsConfig = None.
    let json = """{"ws":{"port":9000}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.Ws)

// ── brave_api_key — empty string silently returns None ───────────────────────

[<Fact>]
let ``brave_api_key empty string silently yields None (no error)`` () =
    // ApiKey.create "" returns Error → Option.bind returns None → braveApiKey = None.
    // Crucially, no error is added to errs (the braveApiKey path does not accumulate errors).
    let json = """{"brave_api_key":""}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok (empty brave key silently dropped), got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.BraveApiKey)

// ── disabled_skills ───────────────────────────────────────────────────────────

[<Fact>]
let ``disabled_skills absent defaults to empty list`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Empty(cfg.DisabledSkills)

[<Fact>]
let ``disabled_skills empty array parses to empty list`` () =
    let json = """{"disabled_skills":[]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Empty(cfg.DisabledSkills)

[<Fact>]
let ``disabled_skills array of names parses correctly`` () =
    let json = """{"disabled_skills":["summarize","skill-creator","weather"]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        Assert.Equal<string list>(["summarize"; "skill-creator"; "weather"], cfg.DisabledSkills)

[<Fact>]
let ``disabled_skills silently drops empty strings from array`` () =
    let json = """{"disabled_skills":["alpha","","beta"]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>(["alpha"; "beta"], cfg.DisabledSkills)

// ── session_ttl_minutes ───────────────────────────────────────────────────────

[<Fact>]
let ``session_ttl_minutes absent defaults to 0`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(0, cfg.SessionTtlMinutes)

[<Fact>]
let ``session_ttl_minutes parses to configured value`` () =
    let json = """{"session_ttl_minutes":120}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(120, cfg.SessionTtlMinutes)

// ── timezone ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``timezone absent defaults to None`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.Timezone)

[<Fact>]
let ``timezone parses IANA string`` () =
    let json = """{"timezone":"Asia/Shanghai"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "Asia/Shanghai", cfg.Timezone)

// ── exec_timeout_seconds ──────────────────────────────────────────────────────

[<Fact>]
let ``exec_timeout_seconds absent defaults to 0`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(0, cfg.ExecTimeoutSeconds)

[<Fact>]
let ``exec_timeout_seconds parses to configured value`` () =
    let json = """{"exec_timeout_seconds":120}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(120, cfg.ExecTimeoutSeconds)

// ── heartbeat ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``heartbeat absent uses defaults`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        Assert.True(cfg.HeartbeatEnabled, "default HeartbeatEnabled should be true")
        Assert.Equal(1800, cfg.HeartbeatIntervalSeconds)
        Assert.Equal(8, cfg.HeartbeatKeepRecentMessages)

[<Fact>]
let ``heartbeat enabled false disables heartbeat`` () =
    let json = """{"heartbeat":{"enabled":false}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.HeartbeatEnabled)

[<Fact>]
let ``heartbeat interval_s is parsed`` () =
    let json = """{"heartbeat":{"interval_s":600}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(600, cfg.HeartbeatIntervalSeconds)

[<Fact>]
let ``heartbeat keep_recent_messages is parsed`` () =
    let json = """{"heartbeat":{"keep_recent_messages":16}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(16, cfg.HeartbeatKeepRecentMessages)

// ── dream ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``dream absent uses defaults`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        Assert.Equal(None, cfg.DreamModelOverride)
        Assert.Equal(15, cfg.DreamMaxIterations)
        Assert.Equal(0, cfg.DreamIntervalHours)

[<Fact>]
let ``dream model_override is parsed`` () =
    let json = """{"dream":{"model_override":"claude-haiku-4-5"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "claude-haiku-4-5", cfg.DreamModelOverride)

[<Fact>]
let ``dream model alias is accepted`` () =
    let json = """{"dream":{"model":"gpt-4o-mini"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "gpt-4o-mini", cfg.DreamModelOverride)

[<Fact>]
let ``dream max_iterations is parsed`` () =
    let json = """{"dream":{"max_iterations":5}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(5, cfg.DreamMaxIterations)

[<Fact>]
let ``dream interval_h is parsed`` () =
    let json = """{"dream":{"interval_h":4}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(4, cfg.DreamIntervalHours)

// ── web_search_provider ───────────────────────────────────────────────────────

[<Fact>]
let ``web_search_provider absent defaults to None`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.WebSearchProvider)

[<Fact>]
let ``web_search_provider brave is parsed`` () =
    let json = """{"web_search_provider":"brave"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "brave", cfg.WebSearchProvider)

[<Fact>]
let ``web_search_provider duckduckgo is parsed`` () =
    let json = """{"web_search_provider":"duckduckgo"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "duckduckgo", cfg.WebSearchProvider)

[<Fact>]
let ``web_search_provider tavily is parsed`` () =
    let json = """{"web_search_provider":"tavily"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "tavily", cfg.WebSearchProvider)

[<Fact>]
let ``web_search_provider searxng is parsed`` () =
    let json = """{"web_search_provider":"searxng"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "searxng", cfg.WebSearchProvider)

[<Fact>]
let ``web_search_provider uppercased is normalised to lowercase`` () =
    let json = """{"web_search_provider":"Brave"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "brave", cfg.WebSearchProvider)

[<Fact>]
let ``web_search_provider empty string becomes None`` () =
    let json = """{"web_search_provider":""}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.WebSearchProvider)

// ── restrict_to_workspace ─────────────────────────────────────────────────────

[<Fact>]
let ``restrict_to_workspace absent defaults to false`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.RestrictToWorkspace)

[<Fact>]
let ``restrict_to_workspace true is parsed`` () =
    let json = """{"restrict_to_workspace":true}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.RestrictToWorkspace)

[<Fact>]
let ``restrict_to_workspace false is parsed`` () =
    let json = """{"restrict_to_workspace":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.RestrictToWorkspace)

// ── provider_retry_mode ───────────────────────────────────────────────────────

[<Fact>]
let ``provider_retry_mode absent defaults to standard`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("standard", cfg.ProviderRetryMode)

[<Fact>]
let ``provider_retry_mode persistent is parsed`` () =
    let json = """{"provider_retry_mode":"persistent"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("persistent", cfg.ProviderRetryMode)

[<Fact>]
let ``provider_retry_mode standard is parsed`` () =
    let json = """{"provider_retry_mode":"standard"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("standard", cfg.ProviderRetryMode)

[<Fact>]
let ``provider_retry_mode unknown value falls back to standard`` () =
    let json = """{"provider_retry_mode":"aggressive"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("standard", cfg.ProviderRetryMode)

// ── unified_session ───────────────────────────────────────────────────────────

[<Fact>]
let ``unified_session absent defaults to false`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.UnifiedSession)

[<Fact>]
let ``unified_session true is parsed`` () =
    let json = """{"unified_session":true}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.UnifiedSession)

// ── web_proxy / web_search_timeout ────────────────────────────────────────────

[<Fact>]
let ``web_proxy absent defaults to None`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.WebProxyUrl)

[<Fact>]
let ``web_proxy is parsed`` () =
    let json = """{"web_proxy":"http://proxy.example.com:8080"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "http://proxy.example.com:8080", cfg.WebProxyUrl)

[<Fact>]
let ``web_proxy_url alias is parsed`` () =
    let json = """{"web_proxy_url":"socks5://localhost:1080"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "socks5://localhost:1080", cfg.WebProxyUrl)

[<Fact>]
let ``web_proxy empty string is treated as None`` () =
    let json = """{"web_proxy":""}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.WebProxyUrl)

[<Fact>]
let ``web_search_timeout absent defaults to 30`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(30, cfg.WebSearchTimeout)

[<Fact>]
let ``web_search_timeout is parsed`` () =
    let json = """{"web_search_timeout":60}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(60, cfg.WebSearchTimeout)

[<Fact>]
let ``web_search_timeout zero or negative falls back to default`` () =
    let json = """{"web_search_timeout":0}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(30, cfg.WebSearchTimeout)

// ── web_search_max_results ─────────────────────────────────────────────────────

[<Fact>]
let ``web_search_max_results absent defaults to 5`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(5, cfg.WebSearchMaxResults)

[<Fact>]
let ``web_search_max_results is parsed`` () =
    let json = """{"web_search_max_results":10}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(10, cfg.WebSearchMaxResults)

[<Fact>]
let ``web_search_max_results zero or negative falls back to default`` () =
    let json = """{"web_search_max_results":0}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(5, cfg.WebSearchMaxResults)

// ── dream_max_batch_size ────────────────────────────────────────────────────────

[<Fact>]
let ``dream max_batch_size absent defaults to 20`` () =
    let json = """{"dream":{}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(20, cfg.DreamMaxBatchSize)

[<Fact>]
let ``dream max_batch_size is parsed`` () =
    let json = """{"dream":{"max_batch_size":30}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(30, cfg.DreamMaxBatchSize)

[<Fact>]
let ``dream max_batch_size zero or negative falls back to default`` () =
    let json = """{"dream":{"max_batch_size":0}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(20, cfg.DreamMaxBatchSize)

// ── exec_path_append ────────────────────────────────────────────────────────────

[<Fact>]
let ``exec_path_append absent defaults to empty string`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("", cfg.ExecPathAppend)

[<Fact>]
let ``exec_path_append is parsed`` () =
    let json = """{"exec_path_append":"/usr/local/bin:/opt/tools"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("/usr/local/bin:/opt/tools", cfg.ExecPathAppend)

[<Fact>]
let ``path_append alias is parsed`` () =
    let json = """{"path_append":"/opt/tools"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("/opt/tools", cfg.ExecPathAppend)

// ── mcp_servers: tool_timeout / enabled_tools ─────────────────────────────────

[<Fact>]
let ``mcp_server tool_timeout defaults to 30 when absent`` () =
    let json = """{"mcp_servers":{"s":{"type":"stdio","command":"cmd"}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("s") with
        | Some e -> Assert.Equal(30, e.ToolTimeout)
        | None   -> Assert.Fail("Expected 's' in McpServers")

[<Fact>]
let ``mcp_server tool_timeout is parsed`` () =
    let json = """{"mcp_servers":{"s":{"type":"stdio","command":"cmd","tool_timeout":60}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("s") with
        | Some e -> Assert.Equal(60, e.ToolTimeout)
        | None   -> Assert.Fail("Expected 's' in McpServers")

[<Fact>]
let ``mcp_server enabled_tools defaults to ["*"] when absent`` () =
    let json = """{"mcp_servers":{"s":{"type":"stdio","command":"cmd"}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("s") with
        | Some e -> Assert.Equal<string list>(["*"], e.EnabledTools)
        | None   -> Assert.Fail("Expected 's' in McpServers")

[<Fact>]
let ``mcp_server enabled_tools filter list is parsed`` () =
    let json = """{"mcp_servers":{"s":{"type":"stdio","command":"cmd","enabled_tools":["read","write"]}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.McpServers.TryFind("s") with
        | Some e -> Assert.Equal<string list>(["read"; "write"], e.EnabledTools)
        | None   -> Assert.Fail("Expected 's' in McpServers")

// ── exec_allowed_env_keys ─────────────────────────────────────────────────────

[<Fact>]
let ``exec_allowed_env_keys absent defaults to empty list`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>([], cfg.ExecAllowedEnvKeys)

[<Fact>]
let ``exec_allowed_env_keys is parsed`` () =
    let json = """{"exec_allowed_env_keys":["HOME","PATH","GOPATH"]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>(["HOME"; "PATH"; "GOPATH"], cfg.ExecAllowedEnvKeys)

[<Fact>]
let ``allowed_env_keys alias is parsed`` () =
    let json = """{"allowed_env_keys":["JAVA_HOME"]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>(["JAVA_HOME"], cfg.ExecAllowedEnvKeys)

// ── send_tool_hints / send_progress / send_max_retries ───────────────────────

[<Fact>]
let ``send_tool_hints absent defaults to false`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.SendToolHints)

[<Fact>]
let ``send_tool_hints true is parsed`` () =
    let json = """{"send_tool_hints":true}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.SendToolHints)

[<Fact>]
let ``send_progress absent defaults to true`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.SendProgress)

[<Fact>]
let ``send_progress false is parsed`` () =
    let json = """{"send_progress":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.SendProgress)

[<Fact>]
let ``send_max_retries absent defaults to 3`` () =
    let json = """{}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(3, cfg.SendMaxRetries)

[<Fact>]
let ``send_max_retries custom value is parsed`` () =
    let json = """{"send_max_retries":5}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(5, cfg.SendMaxRetries)

[<Fact>]
let ``send_max_retries negative value is clamped to default`` () =
    let json = """{"send_max_retries":-1}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(BotSharpConfig.defaults.SendMaxRetries, cfg.SendMaxRetries)

// ── my_tool_allow_set ─────────────────────────────────────────────────────────

[<Fact>]
let ``my_tool_allow_set absent defaults to false`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.MyToolAllowSet)

[<Fact>]
let ``my_tool_allow_set true is parsed`` () =
    let json = """{"my_tool_allow_set":true}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.MyToolAllowSet)

// ── ssrf_whitelist ────────────────────────────────────────────────────────────

[<Fact>]
let ``ssrf_whitelist absent defaults to empty list`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>([], cfg.SsrfWhitelist)

[<Fact>]
let ``ssrf_whitelist CIDR entries are parsed`` () =
    let json = """{"ssrf_whitelist":["10.0.0.0/8","192.168.1.0/24"]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>(["10.0.0.0/8"; "192.168.1.0/24"], cfg.SsrfWhitelist)

[<Fact>]
let ``ssrf_whitelist empty array parses to empty list`` () =
    let json = """{"ssrf_whitelist":[]}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal<string list>([], cfg.SsrfWhitelist)

// ── file_read_max_chars ───────────────────────────────────────────────────────

[<Fact>]
let ``file_read_max_chars absent defaults to 131072`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(131_072, cfg.FileReadMaxChars)

[<Fact>]
let ``file_read_max_chars custom value is parsed`` () =
    let json = """{"file_read_max_chars":65536}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(65536, cfg.FileReadMaxChars)

[<Fact>]
let ``file_read_max_chars zero is clamped to default`` () =
    let json = """{"file_read_max_chars":0}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(131_072, cfg.FileReadMaxChars)

[<Fact>]
let ``file_read_max_chars negative is clamped to default`` () =
    let json = """{"file_read_max_chars":-1}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(131_072, cfg.FileReadMaxChars)

// ── system_prompt_append ─────────────────────────────────────────────────────

[<Fact>]
let ``system_prompt_append absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.SystemPromptAppend)

[<Fact>]
let ``system_prompt_append string is parsed as Some`` () =
    let json = """{"system_prompt_append":"Always reply in French."}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "Always reply in French.", cfg.SystemPromptAppend)

[<Fact>]
let ``system_prompt_append empty string becomes None`` () =
    let json = """{"system_prompt_append":""}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.SystemPromptAppend)

[<Fact>]
let ``system_prompt_append whitespace-only string becomes None`` () =
    let json = """{"system_prompt_append":"   "}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.SystemPromptAppend)

// ── web_search_api_key ────────────────────────────────────────────────────────

[<Fact>]
let ``web_search_api_key absent defaults to empty string`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("", cfg.WebSearchApiKey)

[<Fact>]
let ``web_search_api_key value is parsed`` () =
    let json = """{"web_search_api_key":"tvly-abc123"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("tvly-abc123", cfg.WebSearchApiKey)

// ── web_search_base_url ───────────────────────────────────────────────────────

[<Fact>]
let ``web_search_base_url absent defaults to empty string`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("", cfg.WebSearchBaseUrl)

[<Fact>]
let ``web_search_base_url value is parsed`` () =
    let json = """{"web_search_base_url":"http://localhost:8888"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("http://localhost:8888", cfg.WebSearchBaseUrl)

// ── exec_enable ───────────────────────────────────────────────────────────────

[<Fact>]
let ``exec_enable absent defaults to true`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.ExecEnable)

[<Fact>]
let ``exec_enable false is parsed`` () =
    let json = """{"exec_enable":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.ExecEnable)

// ── web_enable ────────────────────────────────────────────────────────────────

[<Fact>]
let ``web_enable absent defaults to true`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.WebEnable)

[<Fact>]
let ``web_enable false is parsed`` () =
    let json = """{"web_enable":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.WebEnable)

// ── my_tool_enable ────────────────────────────────────────────────────────────

[<Fact>]
let ``my_tool_enable absent defaults to true`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.MyToolEnable)

[<Fact>]
let ``my_tool_enable false is parsed`` () =
    let json = """{"my_tool_enable":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.MyToolEnable)

// ── transcription_provider ────────────────────────────────────────────────────

[<Fact>]
let ``transcription_provider absent defaults to groq`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("groq", cfg.TranscriptionProvider)

[<Fact>]
let ``transcription_provider openai is parsed`` () =
    let json = """{"transcription_provider":"openai"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("openai", cfg.TranscriptionProvider)

// ── transcription_language ────────────────────────────────────────────────────

[<Fact>]
let ``transcription_language absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.TranscriptionLanguage)

[<Fact>]
let ``transcription_language value is parsed as Some`` () =
    let json = """{"transcription_language":"zh"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "zh", cfg.TranscriptionLanguage)

[<Fact>]
let ``transcription_language empty string becomes None`` () =
    let json = """{"transcription_language":""}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.TranscriptionLanguage)

// ── dream_annotate_line_ages ──────────────────────────────────────────────────

[<Fact>]
let ``dream_annotate_line_ages absent defaults to true`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.DreamAnnotateLineAges)

[<Fact>]
let ``dream_annotate_line_ages false is parsed`` () =
    let json = """{"dream_annotate_line_ages":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.DreamAnnotateLineAges)

// ── provider_extra_headers ────────────────────────────────────────────────────

[<Fact>]
let ``provider_extra_headers absent defaults to empty map`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(Map.isEmpty cfg.ProviderExtraHeaders)

[<Fact>]
let ``provider_extra_headers nested object is parsed`` () =
    let json = """{"provider_extra_headers":{"openai":{"X-Custom-Header":"myvalue"}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        Assert.Equal(1, cfg.ProviderExtraHeaders.Count)
        let openaiHeaders = cfg.ProviderExtraHeaders |> Map.find "openai"
        Assert.Equal(1, openaiHeaders.Count)
        Assert.Equal("myvalue", openaiHeaders |> Map.find "X-Custom-Header")

[<Fact>]
let ``provider_extra_headers multiple providers are parsed`` () =
    let json = """{"provider_extra_headers":{"openai":{"H1":"v1"},"anthropic":{"H2":"v2","H3":"v3"}}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        Assert.Equal(2, cfg.ProviderExtraHeaders.Count)
        Assert.Equal(1, (cfg.ProviderExtraHeaders |> Map.find "openai").Count)
        Assert.Equal(2, (cfg.ProviderExtraHeaders |> Map.find "anthropic").Count)

[<Fact>]
let ``provider_extra_headers empty object defaults to empty map`` () =
    let json = """{"provider_extra_headers":{}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(Map.isEmpty cfg.ProviderExtraHeaders)

// ── api_port ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``api_port absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.ApiPort)

[<Fact>]
let ``api_port value is parsed as Some`` () =
    let json = """{"api_port":8080}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some 8080, cfg.ApiPort)

[<Fact>]
let ``api_port zero is parsed as Some 0`` () =
    let json = """{"api_port":0}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some 0, cfg.ApiPort)

// ── api_timeout_seconds ───────────────────────────────────────────────────────

[<Fact>]
let ``api_timeout_seconds absent defaults to 120`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(120, cfg.ApiTimeoutSeconds)

[<Fact>]
let ``api_timeout_seconds custom value is parsed`` () =
    let json = """{"api_timeout_seconds":300}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(300, cfg.ApiTimeoutSeconds)

[<Fact>]
let ``api_timeout_seconds zero is parsed`` () =
    let json = """{"api_timeout_seconds":0}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(0, cfg.ApiTimeoutSeconds)

// ── api_host ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``api_host absent defaults to 127.0.0.1`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("localhost", cfg.ApiHost)

[<Fact>]
let ``api_host custom value is parsed`` () =
    let json = """{"api_host":"0.0.0.0"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("0.0.0.0", cfg.ApiHost)

[<Fact>]
let ``api_host empty string falls back to default`` () =
    let json = """{"api_host":""}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("localhost", cfg.ApiHost)

// ── exec_sandbox ──────────────────────────────────────────────────────────────

[<Fact>]
let ``exec_sandbox absent defaults to empty string`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("", cfg.ExecSandbox)

[<Fact>]
let ``exec_sandbox bwrap is parsed`` () =
    let json = """{"exec_sandbox":"bwrap"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal("bwrap", cfg.ExecSandbox)

// ── subagent_max_iterations ────────────────────────────────────────────────────

[<Fact>]
let ``subagent_max_iterations absent defaults to 15`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(15, cfg.SubagentMaxIterations)

[<Fact>]
let ``subagent_max_iterations value is parsed`` () =
    let json = """{"subagent_max_iterations":25}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(25, cfg.SubagentMaxIterations)

// ── context_block_limit ────────────────────────────────────────────────────────

[<Fact>]
let ``context_block_limit absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.ContextBlockLimit)

[<Fact>]
let ``context_block_limit value is parsed as Some`` () =
    let json = """{"context_block_limit":50}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some 50, cfg.ContextBlockLimit)

// ── max_iterations_message ────────────────────────────────────────────────────

[<Fact>]
let ``max_iterations_message absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.MaxIterationsMessage)

[<Fact>]
let ``max_iterations_message value is parsed as Some`` () =
    let json = """{"max_iterations_message":"Stopped after too many steps."}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "Stopped after too many steps.", cfg.MaxIterationsMessage)

// ── fail_on_tool_error ────────────────────────────────────────────────────────

[<Fact>]
let ``fail_on_tool_error absent defaults to false`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.FailOnToolError)

[<Fact>]
let ``fail_on_tool_error true is parsed`` () =
    let json = """{"fail_on_tool_error":true}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.FailOnToolError)

// ── session_cleanup_days ──────────────────────────────────────────────────────

[<Fact>]
let ``session_cleanup_days absent defaults to 0`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(0, cfg.SessionCleanupDays)

[<Fact>]
let ``session_cleanup_days value is parsed`` () =
    let json = """{"session_cleanup_days":30}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(30, cfg.SessionCleanupDays)

// ── max_messages ──────────────────────────────────────────────────────────────

[<Fact>]
let ``max_messages absent defaults to 0 (unlimited)`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(0, cfg.MaxMessages)

[<Fact>]
let ``max_messages value is parsed`` () =
    let json = """{"max_messages":200}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(200, cfg.MaxMessages)

// ── memory_citation_tracking ─────────────────────────────────────────────────

[<Fact>]
let ``memory_citation_tracking absent defaults to true`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.MemoryCitationTracking)

[<Fact>]
let ``memory_citation_tracking false is parsed`` () =
    let json = """{"memory_citation_tracking":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.MemoryCitationTracking)

// ── memory_citation_strip ─────────────────────────────────────────────────────

[<Fact>]
let ``memory_citation_strip absent defaults to false`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.MemoryCitationStrip)

[<Fact>]
let ``memory_citation_strip true is parsed`` () =
    let json = """{"memory_citation_strip":true}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.MemoryCitationStrip)

// ── phase2_enabled ────────────────────────────────────────────────────────────

[<Fact>]
let ``phase2_enabled absent defaults to true`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Phase2Enabled)

[<Fact>]
let ``phase2_enabled false is parsed`` () =
    let json = """{"phase2_enabled":false}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.False(cfg.Phase2Enabled)

// ── rlm_max_depth ─────────────────────────────────────────────────────────────

[<Fact>]
let ``rlm_max_depth absent defaults to 1`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(1, cfg.RlmMaxDepth)

[<Fact>]
let ``rlm_max_depth value is parsed`` () =
    let json = """{"rlm_max_depth":3}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(3, cfg.RlmMaxDepth)

// ── hook_timeout ──────────────────────────────────────────────────────────────

[<Fact>]
let ``hook_timeout absent defaults to 30`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(30, cfg.HookTimeout)

[<Fact>]
let ``hook_timeout value is parsed`` () =
    let json = """{"hook_timeout":60}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(60, cfg.HookTimeout)

// ── memory_summary_token_limit ────────────────────────────────────────────────

[<Fact>]
let ``memory_summary_token_limit absent defaults to 5000`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(5000, cfg.MemorySummaryTokenLimit)

[<Fact>]
let ``memory_summary_token_limit value is parsed`` () =
    let json = """{"memory_summary_token_limit":3000}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(3000, cfg.MemorySummaryTokenLimit)

// ── phase1_min_idle_minutes ───────────────────────────────────────────────────

[<Fact>]
let ``phase1_min_idle_minutes absent defaults to 30`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(30, cfg.Phase1MinIdleMinutes)

[<Fact>]
let ``phase1_min_idle_minutes value is parsed`` () =
    let json = """{"phase1_min_idle_minutes":60}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(60, cfg.Phase1MinIdleMinutes)

// ── phase2_cooldown_hours ─────────────────────────────────────────────────────

[<Fact>]
let ``phase2_cooldown_hours absent defaults to 6`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(6, cfg.Phase2CooldownHours)

[<Fact>]
let ``phase2_cooldown_hours value is parsed`` () =
    let json = """{"phase2_cooldown_hours":12}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(12, cfg.Phase2CooldownHours)

// ── phase1_model / phase2_model ───────────────────────────────────────────────

[<Fact>]
let ``phase1_model absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.Phase1Model)

[<Fact>]
let ``phase1_model value is parsed as Some`` () =
    let json = """{"phase1_model":"claude-haiku-4-5"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "claude-haiku-4-5", cfg.Phase1Model)

[<Fact>]
let ``phase2_model absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(None, cfg.Phase2Model)

[<Fact>]
let ``phase2_model value is parsed as Some`` () =
    let json = """{"phase2_model":"claude-sonnet-4-6"}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.Equal(Some "claude-sonnet-4-6", cfg.Phase2Model)

// ── discord section ────────────────────────────────────────────────────────────

[<Fact>]
let ``discord section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Discord.IsNone)

[<Fact>]
let ``discord section without token is silently skipped`` () =
    let json = """{"discord":{"allow_from":["*"]}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Discord.IsNone)

[<Fact>]
let ``discord section with token parses correctly`` () =
    let json = """{"discord":{"token":"Bot.my-token-abc"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Discord with
        | None -> Assert.Fail("Expected Some DiscordChannelConfig")
        | Some dc ->
            Assert.Equal("Bot.my-token-abc", dc.Token)
            Assert.Equal(AnyoneAllowed, dc.AllowFrom)

[<Fact>]
let ``discord allow_from wildcard becomes AnyoneAllowed`` () =
    let json = """{"discord":{"token":"tok","allow_from":["*"]}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Discord with
        | None -> Assert.Fail("Expected Some DiscordChannelConfig")
        | Some dc -> Assert.Equal(AnyoneAllowed, dc.AllowFrom)

[<Fact>]
let ``discord allow_from specific users becomes AllowedSet`` () =
    let json = """{"discord":{"token":"tok","allow_from":["123456789","987654321"]}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Discord with
        | None -> Assert.Fail("Expected Some DiscordChannelConfig")
        | Some dc ->
            match dc.AllowFrom with
            | AllowedSet s -> Assert.Equal(2, s.Count)
            | AnyoneAllowed -> Assert.Fail("Expected AllowedSet, got AnyoneAllowed")

// ── slack section ──────────────────────────────────────────────────────────────

[<Fact>]
let ``slack section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Slack.IsNone)

[<Fact>]
let ``slack section without bot_token or app_token is silently skipped`` () =
    let json = """{"slack":{"bot_token":"xoxb-token"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Slack.IsNone)

[<Fact>]
let ``slack section with both tokens parses correctly`` () =
    let json = """{"slack":{"bot_token":"xoxb-bot","app_token":"xapp-app"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Slack with
        | None -> Assert.Fail("Expected Some SlackChannelConfig")
        | Some sl ->
            Assert.Equal("xoxb-bot", sl.BotToken)
            Assert.Equal("xapp-app", sl.AppToken)
            Assert.True(sl.ReplyInThread)   // default

[<Fact>]
let ``slack reply_in_thread false is parsed`` () =
    let json = """{"slack":{"bot_token":"xoxb-bot","app_token":"xapp-app","reply_in_thread":false}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Slack with
        | None -> Assert.Fail("Expected Some SlackChannelConfig")
        | Some sl -> Assert.False(sl.ReplyInThread)

// ── feishu section ─────────────────────────────────────────────────────────────

[<Fact>]
let ``feishu section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Feishu.IsNone)

[<Fact>]
let ``feishu section without app_id is silently skipped`` () =
    let json = """{"feishu":{"app_secret":"secret"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Feishu.IsNone)

[<Fact>]
let ``feishu section with app_id and app_secret parses correctly`` () =
    let json = """{"feishu":{"app_id":"cli_abc","app_secret":"secret123"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Feishu with
        | None -> Assert.Fail("Expected Some FeishuChannelConfig")
        | Some fs ->
            Assert.Equal("cli_abc", fs.AppId)
            Assert.Equal("secret123", fs.AppSecret)
            Assert.Equal("", fs.VerificationToken)    // default
            Assert.Equal(19800, fs.WebhookPort)        // default

[<Fact>]
let ``feishu webhook_port is parsed correctly`` () =
    let json = """{"feishu":{"app_id":"id","app_secret":"sec","webhook_port":9999}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Feishu with
        | None -> Assert.Fail("Expected Some FeishuChannelConfig")
        | Some fs -> Assert.Equal(9999, fs.WebhookPort)

// ── dingtalk section ───────────────────────────────────────────────────────────

[<Fact>]
let ``dingtalk section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.DingTalk.IsNone)

[<Fact>]
let ``dingtalk section without client_secret is silently skipped`` () =
    let json = """{"dingtalk":{"client_id":"my-client"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.DingTalk.IsNone)

[<Fact>]
let ``dingtalk section with client_id and client_secret parses correctly`` () =
    let json = """{"dingtalk":{"client_id":"my-id","client_secret":"my-secret"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.DingTalk with
        | None -> Assert.Fail("Expected Some DingTalkChannelConfig")
        | Some dt ->
            Assert.Equal("my-id", dt.ClientId)
            Assert.Equal("my-secret", dt.ClientSecret)
            Assert.Equal(19801, dt.WebhookPort)   // default

[<Fact>]
let ``dingtalk webhook_port custom value is parsed`` () =
    let json = """{"dingtalk":{"client_id":"id","client_secret":"sec","webhook_port":8888}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.DingTalk with
        | None -> Assert.Fail("Expected Some DingTalkChannelConfig")
        | Some dt -> Assert.Equal(8888, dt.WebhookPort)

// ── email section ──────────────────────────────────────────────────────────────

[<Fact>]
let ``email section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Email.IsNone)

[<Fact>]
let ``email section without all required fields is silently skipped`` () =
    let json = """{"email":{"username":"user@example.com","password":"pass","imap_host":"imap.example.com"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Email.IsNone)

[<Fact>]
let ``email section with all required fields parses correctly`` () =
    let json = """{"email":{"username":"u@x.com","password":"p","imap_host":"imap.x.com","smtp_host":"smtp.x.com"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Email with
        | None -> Assert.Fail("Expected Some EmailChannelConfig")
        | Some em ->
            Assert.Equal("u@x.com", em.Username)
            Assert.Equal("p", em.Password)
            Assert.Equal("imap.x.com", em.ImapHost)
            Assert.Equal("smtp.x.com", em.SmtpHost)
            Assert.Equal(993,  em.ImapPort)    // default
            Assert.Equal(587,  em.SmtpPort)    // default
            Assert.True(em.ImapUseSsl)          // default
            Assert.True(em.SmtpUseTls)          // default
            Assert.Equal(30, em.PollSeconds)    // default

[<Fact>]
let ``email imap_port and smtp_port custom values are parsed`` () =
    let json = """{"email":{"username":"u","password":"p","imap_host":"imap","smtp_host":"smtp","imap_port":143,"smtp_port":25}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Email with
        | None -> Assert.Fail("Expected Some EmailChannelConfig")
        | Some em ->
            Assert.Equal(143, em.ImapPort)
            Assert.Equal(25, em.SmtpPort)

// ── whatsapp section ───────────────────────────────────────────────────────────

[<Fact>]
let ``whatsapp section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.WhatsApp.IsNone)

[<Fact>]
let ``whatsapp section without access_token is silently skipped`` () =
    let json = """{"whatsapp":{"phone_number_id":"12345"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.WhatsApp.IsNone)

[<Fact>]
let ``whatsapp section with phone_number_id and access_token parses correctly`` () =
    let json = """{"whatsapp":{"phone_number_id":"12345","access_token":"EAAtoken"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.WhatsApp with
        | None -> Assert.Fail("Expected Some WhatsAppChannelConfig")
        | Some wa ->
            Assert.Equal("12345", wa.PhoneNumberId)
            Assert.Equal("EAAtoken", wa.AccessToken)
            Assert.Equal("", wa.VerifyToken)     // default
            Assert.Equal(19802, wa.WebhookPort)  // default

[<Fact>]
let ``whatsapp verify_token and webhook_port are parsed correctly`` () =
    let json = """{"whatsapp":{"phone_number_id":"p","access_token":"t","verify_token":"vt","webhook_port":9000}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.WhatsApp with
        | None -> Assert.Fail("Expected Some WhatsAppChannelConfig")
        | Some wa ->
            Assert.Equal("vt", wa.VerifyToken)
            Assert.Equal(9000, wa.WebhookPort)

// ── mochat section ─────────────────────────────────────────────────────────────

[<Fact>]
let ``mochat section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.MoChat.IsNone)

[<Fact>]
let ``mochat section without claw_token is silently skipped`` () =
    let json = """{"mochat":{"base_url":"http://localhost:9527"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.MoChat.IsNone)

[<Fact>]
let ``mochat section with base_url and claw_token parses correctly`` () =
    let json = """{"mochat":{"base_url":"http://host:9527","claw_token":"mytoken"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.MoChat with
        | None -> Assert.Fail("Expected Some MoChatChannelConfig")
        | Some mc ->
            Assert.Equal("http://host:9527", mc.BaseUrl)
            Assert.Equal("mytoken", mc.ClawToken)
            Assert.Equal(5, mc.PollSeconds)   // default

[<Fact>]
let ``mochat poll_interval_seconds custom value is parsed`` () =
    let json = """{"mochat":{"base_url":"http://h","claw_token":"t","poll_interval_seconds":15}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.MoChat with
        | None -> Assert.Fail("Expected Some MoChatChannelConfig")
        | Some mc -> Assert.Equal(15, mc.PollSeconds)

// ── qq section ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``qq section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.QQ.IsNone)

[<Fact>]
let ``qq section without secret is silently skipped`` () =
    let json = """{"qq":{"app_id":"myapp"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.QQ.IsNone)

[<Fact>]
let ``qq section with app_id and secret parses correctly`` () =
    let json = """{"qq":{"app_id":"myapp","secret":"mysecret"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.QQ with
        | None -> Assert.Fail("Expected Some QQChannelConfig")
        | Some qq ->
            Assert.Equal("myapp", qq.AppId)
            Assert.Equal("mysecret", qq.Secret)
            Assert.False(qq.Sandbox)   // default

[<Fact>]
let ``qq sandbox true is parsed`` () =
    let json = """{"qq":{"app_id":"id","secret":"sec","sandbox":true}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.QQ with
        | None -> Assert.Fail("Expected Some QQChannelConfig")
        | Some qq -> Assert.True(qq.Sandbox)

// ── matrix section ─────────────────────────────────────────────────────────────

[<Fact>]
let ``matrix section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Matrix.IsNone)

[<Fact>]
let ``matrix section without access_token is silently skipped`` () =
    let json = """{"matrix":{"homeserver":"https://matrix.org","user_id":"@bot:matrix.org"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Matrix.IsNone)

[<Fact>]
let ``matrix section with all required fields parses correctly`` () =
    let json = """{"matrix":{"homeserver":"https://m.org","user_id":"@bot:m.org","access_token":"syt_token"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Matrix with
        | None -> Assert.Fail("Expected Some MatrixChannelConfig")
        | Some mx ->
            Assert.Equal("https://m.org", mx.Homeserver)
            Assert.Equal("@bot:m.org", mx.UserId)
            Assert.Equal("syt_token", mx.AccessToken)
            Assert.Equal(AnyoneAllowed, mx.AllowFrom)

// ── telnet section ─────────────────────────────────────────────────────────────

[<Fact>]
let ``telnet section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.Telnet.IsNone)

[<Fact>]
let ``telnet section present with default port parses correctly`` () =
    let json = """{"telnet":{}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Telnet with
        | None -> Assert.Fail("Expected Some TelnetChannelConfig")
        | Some tn ->
            Assert.Equal(2323, tn.Port)       // default
            Assert.Equal(AnyoneAllowed, tn.AllowFrom)

[<Fact>]
let ``telnet port custom value is parsed`` () =
    let json = """{"telnet":{"port":9999}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Telnet with
        | None -> Assert.Fail("Expected Some TelnetChannelConfig")
        | Some tn -> Assert.Equal(9999, tn.Port)

[<Fact>]
let ``telnet allow_from specific users becomes AllowedSet`` () =
    let json = """{"telnet":{"allow_from":["admin","developer"]}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.Telnet with
        | None -> Assert.Fail("Expected Some TelnetChannelConfig")
        | Some tn ->
            match tn.AllowFrom with
            | AllowedSet s -> Assert.Equal(2, s.Count)
            | AnyoneAllowed -> Assert.Fail("Expected AllowedSet, got AnyoneAllowed")

// ── inter_agent section ────────────────────────────────────────────────────────

[<Fact>]
let ``inter_agent section absent defaults to None`` () =
    match parseJson "{}" with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.InterAgent.IsNone)

[<Fact>]
let ``inter_agent section with enabled false defaults to None`` () =
    let json = """{"inter_agent":{"enabled":false,"port":18800}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg -> Assert.True(cfg.InterAgent.IsNone)

[<Fact>]
let ``inter_agent section with enabled true parses correctly`` () =
    let json = """{"inter_agent":{"enabled":true}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.InterAgent with
        | None -> Assert.Fail("Expected Some InterAgentChannelConfig")
        | Some ia ->
            Assert.True(ia.Enabled)
            Assert.Equal(18800, ia.Port)              // default
            Assert.Equal("", ia.InstanceName)         // default
            Assert.Equal(30, ia.MaxRoundsPerSession)  // default
            Assert.Equal(3600, ia.TaskTtlSeconds)     // default
            Assert.True(ia.AuditWebhookUrl.IsNone)   // default

[<Fact>]
let ``inter_agent port and instance_name are parsed correctly`` () =
    let json = """{"inter_agent":{"enabled":true,"port":9000,"instance_name":"prod-agent"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.InterAgent with
        | None -> Assert.Fail("Expected Some InterAgentChannelConfig")
        | Some ia ->
            Assert.Equal(9000, ia.Port)
            Assert.Equal("prod-agent", ia.InstanceName)

[<Fact>]
let ``inter_agent audit_webhook_url is parsed as Some`` () =
    let json = """{"inter_agent":{"enabled":true,"audit_webhook_url":"https://audit.example.com/hook"}}"""
    match parseJson json with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok cfg ->
        match cfg.InterAgent with
        | None -> Assert.Fail("Expected Some InterAgentChannelConfig")
        | Some ia -> Assert.Equal(Some "https://audit.example.com/hook", ia.AuditWebhookUrl)
