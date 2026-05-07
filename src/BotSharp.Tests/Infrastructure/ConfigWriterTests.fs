module BotSharp.Tests.Infrastructure.ConfigWriterTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Config.ConfigParser
open BotSharp.Infrastructure.Config.ConfigWriter

// ═══════════════════════════════════════════════════════════════════════════
// serializeConfig — JSON round-trip with ConfigParser
//
// The key invariant: any config that ConfigParser can produce, ConfigWriter
// can serialize, and the result can be parsed back to an equal config.
// ═══════════════════════════════════════════════════════════════════════════

let private roundTrip (cfg: BotSharpConfig) : Result<BotSharpConfig, ParseError list> =
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    parseConfig doc

[<Fact>]
let ``serializeConfig produces valid JSON`` () =
    let json = serializeConfig BotSharpConfig.defaults
    // Should not throw
    use doc = JsonDocument.Parse(json)
    Assert.NotNull(doc)

[<Fact>]
let ``defaults round-trip through serialize then parse`` () =
    match roundTrip BotSharpConfig.defaults with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok cfg ->
        Assert.Equal(BotSharpConfig.defaults.DefaultModel,    cfg.DefaultModel)
        Assert.Equal(BotSharpConfig.defaults.DefaultProvider, cfg.DefaultProvider)
        Assert.Equal(BotSharpConfig.defaults.Temperature,     cfg.Temperature)
        Assert.Equal(BotSharpConfig.defaults.MaxTokens,       cfg.MaxTokens)
        Assert.Equal(BotSharpConfig.defaults.MemoryWindowSize, cfg.MemoryWindowSize)
        Assert.Equal(BotSharpConfig.defaults.MaxIterations,   cfg.MaxIterations)

[<Fact>]
let ``custom scalar fields survive round-trip`` () =
    let cfg = { BotSharpConfig.defaults with
                    DefaultModel    = "claude-opus-4-6"
                    DefaultProvider = "anthropic"
                    Temperature     = 0.3
                    MaxTokens       = 8192
                    MemoryWindowSize = 100
                    MaxIterations   = 20 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        Assert.Equal("claude-opus-4-6", parsed.DefaultModel)
        Assert.Equal("anthropic",       parsed.DefaultProvider)
        Assert.Equal(0.3,               parsed.Temperature)
        Assert.Equal(8192,              parsed.MaxTokens)
        Assert.Equal(100,               parsed.MemoryWindowSize)
        Assert.Equal(20,                parsed.MaxIterations)

[<Fact>]
let ``api_keys survive round-trip`` () =
    let key = ApiKey.create "sk-test-key-1234" |> Result.toOption |> Option.get
    let cfg = { BotSharpConfig.defaults with ApiKeys = Map.ofList [ "openai", key ] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        Assert.True(parsed.ApiKeys.ContainsKey("openai"))
        Assert.Equal("sk-test-key-1234", ApiKey.value parsed.ApiKeys["openai"])

[<Fact>]
let ``AnyoneAllowed survives round-trip`` () =
    let cfg = { BotSharpConfig.defaults with AllowFrom = AnyoneAllowed }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  -> Assert.Equal(AnyoneAllowed, parsed.AllowFrom)

[<Fact>]
let ``serializeConfig output contains expected top-level keys`` () =
    let json = serializeConfig BotSharpConfig.defaults
    use doc  = JsonDocument.Parse(json)
    let root = doc.RootElement
    for key in [ "default_model"; "default_provider"; "temperature"; "max_tokens";
                 "workspace_path"; "memory_window_size"; "max_iterations";
                 "api_keys"; "allow_from" ] do
        Assert.True(root.TryGetProperty(key) |> fst, $"Missing key: {key}")

[<Fact>]
let ``reasoning_effort Some High survives round-trip`` () =
    let cfg = { BotSharpConfig.defaults with ReasoningEffort = Some High }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  -> Assert.Equal(Some High, parsed.ReasoningEffort)

[<Fact>]
let ``reasoning_effort None does not emit key`` () =
    let cfg = { BotSharpConfig.defaults with ReasoningEffort = None }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("reasoning_effort") |> fst,
                 "Expected reasoning_effort to be absent when None")

[<Fact>]
let ``max_tool_result_chars survives round-trip`` () =
    let cfg = { BotSharpConfig.defaults with MaxToolResultChars = 8192 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  -> Assert.Equal(8192, parsed.MaxToolResultChars)

[<Fact>]
let ``serializeConfig emits max_tool_result_chars key`` () =
    let json = serializeConfig BotSharpConfig.defaults
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.TryGetProperty("max_tool_result_chars") |> fst,
                "Expected max_tool_result_chars key in serialized JSON")

// ── Telegram serialization ────────────────────────────────────────────────────

let private makeTelegramConfig token =
    { Token              = TelegramBotToken.create token |> function Result.Ok t -> t | Error e -> failwith e
      AllowFrom          = AnyoneAllowed
      Proxy              = None
      ReplyToMessage     = false
      ReactEmoji         = Some "👀"
      GroupPolicy        = MentionPolicy
      ConnectionPoolSize = 8
      PoolTimeout        = TimeSpan.FromSeconds(30.0)
      Streaming          = true
      InlineKeyboards    = false
      StreamEditInterval = TimeSpan.FromSeconds(0.5) }

[<Fact>]
let ``telegram config survives round-trip`` () =
    let tg  = makeTelegramConfig "123456789:AABBCCDDEEFFaabbccddeeff_abcdefghijk"
    let cfg = { BotSharpConfig.defaults with Telegram = Some tg }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.Telegram with
        | None    -> Assert.Fail("Expected Telegram config to be present after round-trip")
        | Some t  ->
            Assert.Equal(TelegramBotToken.value tg.Token, TelegramBotToken.value t.Token)
            Assert.Equal(AnyoneAllowed,  t.AllowFrom)
            Assert.Equal(MentionPolicy,  t.GroupPolicy)
            Assert.Equal(tg.Streaming,   t.Streaming)

[<Fact>]
let ``telegram absent from JSON when None`` () =
    let cfg  = { BotSharpConfig.defaults with Telegram = None }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("telegram") |> fst,
                 "Expected 'telegram' key to be absent when Telegram = None")

[<Fact>]
let ``telegram allow_from AllowedSet survives round-trip`` () =
    let tg  = { makeTelegramConfig "123456789:AABBCCDDEEFFaabbccddeeff_abcdefghijk" with
                    AllowFrom = AllowedSet (Set.ofList ["111"; "222"]) }
    let cfg = { BotSharpConfig.defaults with Telegram = Some tg }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.Telegram with
        | None   -> Assert.Fail("Expected Telegram config")
        | Some t ->
            match t.AllowFrom with
            | AllowedSet ids -> Assert.Equal<Set<string>>(Set.ofList ["111"; "222"], ids)
            | AnyoneAllowed  -> Assert.Fail("Expected AllowedSet")

// ── WsConfig serialization ────────────────────────────────────────────────────

[<Fact>]
let ``ws config (no token) survives round-trip`` () =
    let ws  = { Port = 9876; Token = None; Enabled = true }
    let cfg = { BotSharpConfig.defaults with Ws = Some ws }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.Ws with
        | None   -> Assert.Fail("Expected Ws config to be present after round-trip")
        | Some w ->
            Assert.Equal(9876, w.Port)
            Assert.Equal(true, w.Enabled)
            Assert.Equal(None, w.Token)

[<Fact>]
let ``ws config with token survives round-trip`` () =
    let token = ApiKey.create "my-ws-secret-key" |> Result.toOption |> Option.get
    let ws    = { Port = 8765; Token = Some token; Enabled = true }
    let cfg   = { BotSharpConfig.defaults with Ws = Some ws }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.Ws with
        | None   -> Assert.Fail("Expected Ws config to be present after round-trip")
        | Some w ->
            Assert.Equal(8765, w.Port)
            Assert.Equal(true, w.Enabled)
            match w.Token with
            | None   -> Assert.Fail("Expected token to be present")
            | Some t -> Assert.Equal("my-ws-secret-key", ApiKey.value t)

[<Fact>]
let ``ws absent from JSON when None`` () =
    let cfg  = { BotSharpConfig.defaults with Ws = None }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("ws") |> fst,
                 "Expected 'ws' key to be absent when Ws = None")

[<Fact>]
let ``ws disabled in config is not surfaced after round-trip`` () =
    // ConfigParser only returns Some WsConfig when enabled = true.
    // A disabled ws section round-trips to None.
    let ws  = { Port = 8765; Token = None; Enabled = false }
    let cfg = { BotSharpConfig.defaults with Ws = Some ws }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    match parseConfig doc with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  -> Assert.Equal(None, parsed.Ws)

// ── base_urls serialization ───────────────────────────────────────────────────

[<Fact>]
let ``base_urls emitted when non-empty`` () =
    let cfg = { BotSharpConfig.defaults with BaseUrls = Map.ofList [ "openai", "https://my-proxy.example.com/v1" ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.TryGetProperty("base_urls") |> fst,
                "Expected base_urls key when BaseUrls is non-empty")
    let bu = doc.RootElement.GetProperty("base_urls")
    Assert.Equal("https://my-proxy.example.com/v1", bu.GetProperty("openai").GetString())

[<Fact>]
let ``base_urls absent from JSON when empty`` () =
    let cfg  = { BotSharpConfig.defaults with BaseUrls = Map.empty }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("base_urls") |> fst,
                 "Expected base_urls to be absent when BaseUrls is empty")

// ── brave_api_key serialization ───────────────────────────────────────────────

[<Fact>]
let ``brave_api_key emitted when Some`` () =
    let key = ApiKey.create "brave-test-key" |> Result.toOption |> Option.get
    let cfg = { BotSharpConfig.defaults with BraveApiKey = Some key }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.TryGetProperty("brave_api_key") |> fst,
                "Expected brave_api_key key when BraveApiKey is Some")
    Assert.Equal("brave-test-key", doc.RootElement.GetProperty("brave_api_key").GetString())

[<Fact>]
let ``brave_api_key absent from JSON when None`` () =
    let cfg  = { BotSharpConfig.defaults with BraveApiKey = None }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("brave_api_key") |> fst,
                 "Expected brave_api_key to be absent when BraveApiKey is None")

// ── context_window_tokens serialization ──────────────────────────────────────

[<Fact>]
let ``context_window_tokens emitted when non-zero`` () =
    let cfg = { BotSharpConfig.defaults with ContextWindowTokens = 128000 }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.TryGetProperty("context_window_tokens") |> fst,
                "Expected context_window_tokens key when non-zero")
    Assert.Equal(128000, doc.RootElement.GetProperty("context_window_tokens").GetInt32())

[<Fact>]
let ``context_window_tokens absent from JSON when zero`` () =
    let cfg  = { BotSharpConfig.defaults with ContextWindowTokens = 0 }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("context_window_tokens") |> fst,
                 "Expected context_window_tokens to be absent when 0")

// ── reasoning_effort: all variants ───────────────────────────────────────────

[<Fact>]
let ``reasoning_effort Some Low emits 'low'`` () =
    let cfg  = { BotSharpConfig.defaults with ReasoningEffort = Some Low }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.Equal("low", doc.RootElement.GetProperty("reasoning_effort").GetString())

[<Fact>]
let ``reasoning_effort Some Medium emits 'medium'`` () =
    let cfg  = { BotSharpConfig.defaults with ReasoningEffort = Some Medium }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString())

[<Fact>]
let ``reasoning_effort Some Adaptive emits 'adaptive'`` () =
    let cfg  = { BotSharpConfig.defaults with ReasoningEffort = Some Adaptive }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.Equal("adaptive", doc.RootElement.GetProperty("reasoning_effort").GetString())

// ── saveConfig — write to disk ────────────────────────────────────────────────

[<Fact>]
let ``saveConfig writes a file that ConfigParser can load`` () =
    let dir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let path = Path.Combine(dir, "config.json")
    try
        let result = saveConfig path BotSharpConfig.defaults |> Async.RunSynchronously
        Assert.Equal(Result.Ok (), result)
        Assert.True(File.Exists(path), "Expected config file to be written")
        use doc = JsonDocument.Parse(File.ReadAllText(path))
        match parseConfig doc with
        | Error errs -> Assert.Fail($"Written config could not be parsed: {errs}")
        | Ok cfg     -> Assert.Equal(BotSharpConfig.defaults.DefaultModel, cfg.DefaultModel)
    finally
        try Directory.Delete(dir, true) with _ -> ()

// ── mcp_servers serialization ─────────────────────────────────────────────────

/// Helper: wrap a McpServerConfig in a default McpServerEntry.
let private mcpEntry conn = { Connection = conn; ToolTimeout = 30; EnabledTools = ["*"] }

[<Fact>]
let ``stdio mcp_server emitted with correct type field`` () =
    let entry = StdioServer ("npx", [ "-y"; "server" ], Map.empty) |> mcpEntry
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "my-server", entry ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.TryGetProperty("mcp_servers") |> fst,
                "Expected mcp_servers key")
    let srvEl = doc.RootElement.GetProperty("mcp_servers").GetProperty("my-server")
    Assert.Equal("stdio", srvEl.GetProperty("type").GetString())
    Assert.Equal("npx",   srvEl.GetProperty("command").GetString())

[<Fact>]
let ``stdio mcp_server with env vars emits env object`` () =
    let entry = StdioServer ("node", [ "server.js" ], Map.ofList [ "API_KEY", "secret"; "DEBUG", "1" ]) |> mcpEntry
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "env-server", entry ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let srvEl = doc.RootElement.GetProperty("mcp_servers").GetProperty("env-server")
    Assert.True(srvEl.TryGetProperty("env") |> fst, "Expected env object in stdio server")
    let envEl = srvEl.GetProperty("env")
    Assert.Equal("secret", envEl.GetProperty("API_KEY").GetString())
    Assert.Equal("1",      envEl.GetProperty("DEBUG").GetString())

[<Fact>]
let ``http mcp_server emitted with correct type and url fields`` () =
    let url   = Uri("https://mcp.example.com/v1")
    let entry = HttpServer (url, Map.empty) |> mcpEntry
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "http-server", entry ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let srvEl = doc.RootElement.GetProperty("mcp_servers").GetProperty("http-server")
    Assert.Equal("http", srvEl.GetProperty("type").GetString())
    Assert.Equal("https://mcp.example.com/v1", srvEl.GetProperty("url").GetString())

[<Fact>]
let ``http mcp_server with headers emits headers object`` () =
    let url   = Uri("https://mcp.example.com/v1")
    let entry = HttpServer (url, Map.ofList [ "Authorization", "Bearer token123" ]) |> mcpEntry
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "auth-server", entry ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let srvEl = doc.RootElement.GetProperty("mcp_servers").GetProperty("auth-server")
    Assert.True(srvEl.TryGetProperty("headers") |> fst, "Expected headers object in http server")
    Assert.Equal("Bearer token123", srvEl.GetProperty("headers").GetProperty("Authorization").GetString())

[<Fact>]
let ``http mcp_server round-trips through ConfigParser`` () =
    let url   = Uri("https://mcp.example.com/v1")
    let entry = HttpServer (url, Map.ofList [ "X-Api-Key", "key123" ]) |> mcpEntry
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "http-srv", entry ] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.McpServers.TryFind("http-srv") with
        | None -> Assert.Fail("Expected http-srv in parsed McpServers")
        | Some e ->
            match e.Connection with
            | HttpServer (parsedUrl, parsedHeaders) ->
                Assert.Equal(url.ToString(), parsedUrl.ToString())
                Assert.Equal("key123", parsedHeaders["X-Api-Key"])
            | other -> Assert.Fail($"Expected HttpServer connection, got {other}")

[<Fact>]
let ``mcp_servers absent from JSON when empty`` () =
    let cfg  = { BotSharpConfig.defaults with McpServers = Map.empty }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("mcp_servers") |> fst,
                 "Expected mcp_servers to be absent when McpServers is empty")

// ── allow_from AllowedSet (top-level) ────────────────────────────────────────

[<Fact>]
let ``AllowedSet survives round-trip for top-level allow_from`` () =
    // The | AllowedSet uids -> uids |> Set.toArray |> Array.map id branch
    let cfg = { BotSharpConfig.defaults with AllowFrom = AllowedSet (Set.ofList ["alice"; "bob"]) }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.AllowFrom with
        | AllowedSet ids -> Assert.Equal<Set<string>>(Set.ofList ["alice"; "bob"], ids)
        | AnyoneAllowed  -> Assert.Fail("Expected AllowedSet, got AnyoneAllowed")

// ── Telegram optional fields ──────────────────────────────────────────────────

[<Fact>]
let ``telegram proxy Some survives round-trip`` () =
    // The | Some uri -> w.WriteString("proxy", uri.ToString()) branch
    let tg  = { makeTelegramConfig "123456789:AABBCCDDEEFFaabbccddeeff_abcdefghijk" with
                    Proxy = Some (Uri("http://proxy.example.com:8080")) }
    let cfg = { BotSharpConfig.defaults with Telegram = Some tg }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Parse failed: {errs}")
    | Ok parsed  ->
        match parsed.Telegram with
        | None   -> Assert.Fail("Expected Telegram config")
        | Some t ->
            match t.Proxy with
            | None   -> Assert.Fail("Expected Proxy to be Some after round-trip")
            | Some u -> Assert.Contains("proxy.example.com", u.ToString())

[<Fact>]
let ``telegram GroupPolicy OpenPolicy serializes to open`` () =
    // The | OpenPolicy -> "open" branch in the match tg.GroupPolicy with expression
    let tg  = { makeTelegramConfig "123456789:AABBCCDDEEFFaabbccddeeff_abcdefghijk" with
                    GroupPolicy = OpenPolicy }
    let cfg = { BotSharpConfig.defaults with Telegram = Some tg }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let tgEl = doc.RootElement.GetProperty("telegram")
    Assert.Equal("open", tgEl.GetProperty("group_policy").GetString())

[<Fact>]
let ``telegram ReactEmoji None does not emit react_emoji key`` () =
    // The | None -> () branch for ReactEmoji
    let tg  = { makeTelegramConfig "123456789:AABBCCDDEEFFaabbccddeeff_abcdefghijk" with
                    ReactEmoji = None }
    let cfg = { BotSharpConfig.defaults with Telegram = Some tg }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let tgEl = doc.RootElement.GetProperty("telegram")
    Assert.False(tgEl.TryGetProperty("react_emoji") |> fst,
                 "Expected react_emoji to be absent when ReactEmoji = None")

// ── disabled_skills ───────────────────────────────────────────────────────────

[<Fact>]
let ``disabled_skills empty list does not emit key`` () =
    // Default is [] — omitted from JSON to keep config files minimal.
    let cfg  = { BotSharpConfig.defaults with DisabledSkills = [] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("disabled_skills") |> fst,
                 "Expected disabled_skills to be absent when list is empty")

[<Fact>]
let ``disabled_skills non-empty list round-trips correctly`` () =
    let cfg  = { BotSharpConfig.defaults with DisabledSkills = ["summarize"; "skill-creator"] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal<string list>(cfg.DisabledSkills, parsed.DisabledSkills)

[<Fact>]
let ``disabled_skills single name round-trips correctly`` () =
    let cfg  = { BotSharpConfig.defaults with DisabledSkills = ["weather"] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal<string list>(cfg.DisabledSkills, parsed.DisabledSkills)

// ── session_ttl_minutes ───────────────────────────────────────────────────────

[<Fact>]
let ``session_ttl_minutes zero does not emit key`` () =
    let cfg  = { BotSharpConfig.defaults with SessionTtlMinutes = 0 }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("session_ttl_minutes") |> fst,
                 "Expected session_ttl_minutes absent when 0")

[<Fact>]
let ``session_ttl_minutes positive value round-trips correctly`` () =
    let cfg  = { BotSharpConfig.defaults with SessionTtlMinutes = 60 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(60, parsed.SessionTtlMinutes)

// ── timezone ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``timezone None does not emit key`` () =
    let cfg  = { BotSharpConfig.defaults with Timezone = None }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("timezone") |> fst,
                 "Expected timezone absent when None")

[<Fact>]
let ``timezone Some round-trips correctly`` () =
    let cfg  = { BotSharpConfig.defaults with Timezone = Some "America/New_York" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "America/New_York", parsed.Timezone)

// ── exec_timeout_seconds ──────────────────────────────────────────────────────

[<Fact>]
let ``exec_timeout_seconds zero does not emit key`` () =
    let cfg  = { BotSharpConfig.defaults with ExecTimeoutSeconds = 0 }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("exec_timeout_seconds") |> fst,
                 "Expected exec_timeout_seconds absent when 0")

[<Fact>]
let ``exec_timeout_seconds positive value round-trips correctly`` () =
    let cfg  = { BotSharpConfig.defaults with ExecTimeoutSeconds = 300 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(300, parsed.ExecTimeoutSeconds)

// ── heartbeat ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``heartbeat defaults do not emit heartbeat object`` () =
    // When all heartbeat values are defaults, the object should be omitted.
    let cfg  = BotSharpConfig.defaults   // defaults: enabled=true, interval=1800, keepRecent=8
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("heartbeat") |> fst,
                 "Expected 'heartbeat' absent when all values are defaults")

[<Fact>]
let ``heartbeat enabled false round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with HeartbeatEnabled = false }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.False(parsed.HeartbeatEnabled)

[<Fact>]
let ``heartbeat interval_s round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with HeartbeatIntervalSeconds = 600 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(600, parsed.HeartbeatIntervalSeconds)

[<Fact>]
let ``heartbeat keep_recent_messages round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with HeartbeatKeepRecentMessages = 16 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(16, parsed.HeartbeatKeepRecentMessages)

// ── dream ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``dream defaults do not emit dream object`` () =
    let cfg  = BotSharpConfig.defaults
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("dream") |> fst,
                 "Expected 'dream' absent when all values are defaults")

[<Fact>]
let ``dream model_override round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with DreamModelOverride = Some "claude-haiku-4-5" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "claude-haiku-4-5", parsed.DreamModelOverride)

[<Fact>]
let ``dream max_iterations round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with DreamMaxIterations = 5 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(5, parsed.DreamMaxIterations)

[<Fact>]
let ``dream interval_h round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with DreamIntervalHours = 4 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(4, parsed.DreamIntervalHours)

// ── web_search_provider ───────────────────────────────────────────────────────

[<Fact>]
let ``web_search_provider None does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // WebSearchProvider = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_search_provider") |> fst,
                 "Expected web_search_provider absent when None")

[<Fact>]
let ``web_search_provider brave round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchProvider = Some "brave" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "brave", parsed.WebSearchProvider)

[<Fact>]
let ``web_search_provider duckduckgo round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchProvider = Some "duckduckgo" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "duckduckgo", parsed.WebSearchProvider)

[<Fact>]
let ``web_search_provider tavily round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchProvider = Some "tavily" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "tavily", parsed.WebSearchProvider)

// ── restrict_to_workspace ─────────────────────────────────────────────────────

[<Fact>]
let ``restrict_to_workspace false does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // RestrictToWorkspace = false
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("restrict_to_workspace") |> fst,
                 "Expected restrict_to_workspace absent when false")

[<Fact>]
let ``restrict_to_workspace true round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with RestrictToWorkspace = true }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.True(parsed.RestrictToWorkspace)

// ── provider_retry_mode ───────────────────────────────────────────────────────

[<Fact>]
let ``provider_retry_mode standard does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ProviderRetryMode = "standard"
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("provider_retry_mode") |> fst,
                 "Expected provider_retry_mode absent when standard")

[<Fact>]
let ``provider_retry_mode persistent round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ProviderRetryMode = "persistent" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("persistent", parsed.ProviderRetryMode)

// ── unified_session ───────────────────────────────────────────────────────────

[<Fact>]
let ``unified_session false does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // UnifiedSession = false
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("unified_session") |> fst,
                 "Expected unified_session absent when false")

[<Fact>]
let ``unified_session true round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with UnifiedSession = true }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.True(parsed.UnifiedSession)

// ── saveConfig error path ─────────────────────────────────────────────────────

[<Fact>]
let ``saveConfig returns Result.Error when path is a directory`` () =
    // Writing to an existing directory path should fail with an IOException.
    // The with ex -> Result.Error ex.Message branch is exercised here.
    let result =
        saveConfig (Path.GetTempPath()) BotSharpConfig.defaults
        |> Async.RunSynchronously
    match result with
    | Result.Error _ -> ()   // expected — can't write a file to a directory path
    | Result.Ok ()   -> Assert.Fail("Expected Result.Error when path is a directory")

// ── web_proxy / web_search_timeout ────────────────────────────────────────────

[<Fact>]
let ``web_proxy absent when None`` () =
    let cfg = BotSharpConfig.defaults  // WebProxyUrl = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_proxy") |> fst,
                 "Expected web_proxy absent when None")

[<Fact>]
let ``web_proxy round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebProxyUrl = Some "http://proxy.example.com:8080" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "http://proxy.example.com:8080", parsed.WebProxyUrl)

[<Fact>]
let ``web_search_timeout absent when default (30)`` () =
    let cfg = BotSharpConfig.defaults  // WebSearchTimeout = 30
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_search_timeout") |> fst,
                 "Expected web_search_timeout absent when default")

[<Fact>]
let ``web_search_timeout round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchTimeout = 60 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(60, parsed.WebSearchTimeout)

// ── web_search_max_results ─────────────────────────────────────────────────────

[<Fact>]
let ``web_search_max_results absent when default (5)`` () =
    let cfg = BotSharpConfig.defaults  // WebSearchMaxResults = 5
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_search_max_results") |> fst,
                 "Expected web_search_max_results absent when default")

[<Fact>]
let ``web_search_max_results round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchMaxResults = 10 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(10, parsed.WebSearchMaxResults)

// ── dream_max_batch_size ────────────────────────────────────────────────────────

[<Fact>]
let ``dream max_batch_size absent when default (20)`` () =
    let cfg = BotSharpConfig.defaults  // DreamMaxBatchSize = 20
    let json = serializeConfig cfg
    // Dream object is omitted entirely when all sub-fields are default
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("dream") |> fst,
                 "Expected dream absent when all values are defaults")

[<Fact>]
let ``dream max_batch_size round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with DreamMaxBatchSize = 30 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(30, parsed.DreamMaxBatchSize)

// ── exec_path_append ────────────────────────────────────────────────────────────

[<Fact>]
let ``exec_path_append absent when empty`` () =
    let cfg = BotSharpConfig.defaults  // ExecPathAppend = ""
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("exec_path_append") |> fst,
                 "Expected exec_path_append absent when empty")

[<Fact>]
let ``exec_path_append round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ExecPathAppend = "/usr/local/bin:/opt/tools" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("/usr/local/bin:/opt/tools", parsed.ExecPathAppend)

// ── mcp_server tool_timeout / enabled_tools ────────────────────────────────────

[<Fact>]
let ``mcp_server tool_timeout absent when default (30)`` () =
    let entry = { Connection = StdioServer("cmd", [], Map.empty); ToolTimeout = 30; EnabledTools = ["*"] }
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "s", entry ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let srvEl = doc.RootElement.GetProperty("mcp_servers").GetProperty("s")
    Assert.False(srvEl.TryGetProperty("tool_timeout") |> fst,
                 "Expected tool_timeout absent when default")

[<Fact>]
let ``mcp_server tool_timeout round-trips correctly`` () =
    let entry = { Connection = StdioServer("cmd", [], Map.empty); ToolTimeout = 60; EnabledTools = ["*"] }
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "s", entry ] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  ->
        match parsed.McpServers.TryFind("s") with
        | Some e -> Assert.Equal(60, e.ToolTimeout)
        | None   -> Assert.Fail("Expected 's' in McpServers")

[<Fact>]
let ``mcp_server enabled_tools absent when star`` () =
    let entry = { Connection = StdioServer("cmd", [], Map.empty); ToolTimeout = 30; EnabledTools = ["*"] }
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "s", entry ] }
    let json = serializeConfig cfg
    use doc  = JsonDocument.Parse(json)
    let srvEl = doc.RootElement.GetProperty("mcp_servers").GetProperty("s")
    Assert.False(srvEl.TryGetProperty("enabled_tools") |> fst,
                 "Expected enabled_tools absent when [\"*\"]")

[<Fact>]
let ``mcp_server enabled_tools filter list round-trips correctly`` () =
    let entry = { Connection = StdioServer("cmd", [], Map.empty); ToolTimeout = 30; EnabledTools = ["read"; "write"] }
    let cfg = { BotSharpConfig.defaults with McpServers = Map.ofList [ "s", entry ] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  ->
        match parsed.McpServers.TryFind("s") with
        | Some e -> Assert.Equal<string list>(["read"; "write"], e.EnabledTools)
        | None   -> Assert.Fail("Expected 's' in McpServers")

// ── exec_allowed_env_keys ─────────────────────────────────────────────────────

[<Fact>]
let ``exec_allowed_env_keys absent when empty list`` () =
    let cfg = BotSharpConfig.defaults  // ExecAllowedEnvKeys = []
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("exec_allowed_env_keys") |> fst,
                 "Expected exec_allowed_env_keys absent when empty")

[<Fact>]
let ``exec_allowed_env_keys round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ExecAllowedEnvKeys = ["HOME"; "PATH"; "GOPATH"] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal<string list>(["HOME"; "PATH"; "GOPATH"], parsed.ExecAllowedEnvKeys)

// ── send_tool_hints ───────────────────────────────────────────────────────────

[<Fact>]
let ``send_tool_hints false does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // SendToolHints = false
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("send_tool_hints") |> fst,
                 "Expected send_tool_hints absent when false")

[<Fact>]
let ``send_tool_hints true round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with SendToolHints = true }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.True(parsed.SendToolHints)

// ── send_progress ─────────────────────────────────────────────────────────────

[<Fact>]
let ``send_progress true does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // SendProgress = true
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("send_progress") |> fst,
                 "Expected send_progress absent when true (default)")

[<Fact>]
let ``send_progress false round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with SendProgress = false }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.False(parsed.SendProgress)

// ── send_max_retries ──────────────────────────────────────────────────────────

[<Fact>]
let ``send_max_retries default (3) does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // SendMaxRetries = 3
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("send_max_retries") |> fst,
                 "Expected send_max_retries absent when default (3)")

[<Fact>]
let ``send_max_retries non-default round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with SendMaxRetries = 5 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(5, parsed.SendMaxRetries)

// ── my_tool_allow_set ─────────────────────────────────────────────────────────

[<Fact>]
let ``my_tool_allow_set false does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // MyToolAllowSet = false
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("my_tool_allow_set") |> fst,
                 "Expected my_tool_allow_set absent when false")

[<Fact>]
let ``my_tool_allow_set true round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with MyToolAllowSet = true }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.True(parsed.MyToolAllowSet)

// ── ssrf_whitelist ────────────────────────────────────────────────────────────

[<Fact>]
let ``ssrf_whitelist absent when empty list`` () =
    let cfg = BotSharpConfig.defaults  // SsrfWhitelist = []
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("ssrf_whitelist") |> fst,
                 "Expected ssrf_whitelist absent when empty")

[<Fact>]
let ``ssrf_whitelist round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with SsrfWhitelist = ["10.0.0.0/8"; "192.168.1.0/24"] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal<string list>(["10.0.0.0/8"; "192.168.1.0/24"], parsed.SsrfWhitelist)

// ── file_read_max_chars ───────────────────────────────────────────────────────

[<Fact>]
let ``file_read_max_chars default does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // FileReadMaxChars = 131072
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("file_read_max_chars") |> fst,
                 "Expected file_read_max_chars absent when default (131072)")

[<Fact>]
let ``file_read_max_chars non-default round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with FileReadMaxChars = 65536 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(65536, parsed.FileReadMaxChars)

// ── system_prompt_append ─────────────────────────────────────────────────────

[<Fact>]
let ``system_prompt_append None does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // SystemPromptAppend = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("system_prompt_append") |> fst,
                 "Expected system_prompt_append absent when None")

[<Fact>]
let ``system_prompt_append Some round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with SystemPromptAppend = Some "Always reply in French." }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "Always reply in French.", parsed.SystemPromptAppend)

// ── web_search_api_key ────────────────────────────────────────────────────────

[<Fact>]
let ``web_search_api_key empty does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // WebSearchApiKey = ""
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_search_api_key") |> fst,
                 "Expected web_search_api_key absent when empty")

[<Fact>]
let ``web_search_api_key non-empty round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchApiKey = "tvly-abc123" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("tvly-abc123", parsed.WebSearchApiKey)

// ── web_search_base_url ───────────────────────────────────────────────────────

[<Fact>]
let ``web_search_base_url empty does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // WebSearchBaseUrl = ""
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_search_base_url") |> fst,
                 "Expected web_search_base_url absent when empty")

[<Fact>]
let ``web_search_base_url non-empty round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebSearchBaseUrl = "http://localhost:8888" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("http://localhost:8888", parsed.WebSearchBaseUrl)

// ── exec_enable ───────────────────────────────────────────────────────────────

[<Fact>]
let ``exec_enable true does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ExecEnable = true
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("exec_enable") |> fst,
                 "Expected exec_enable absent when true (default)")

[<Fact>]
let ``exec_enable false round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ExecEnable = false }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.False(parsed.ExecEnable)

// ── web_enable ────────────────────────────────────────────────────────────────

[<Fact>]
let ``web_enable true does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // WebEnable = true
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("web_enable") |> fst,
                 "Expected web_enable absent when true (default)")

[<Fact>]
let ``web_enable false round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with WebEnable = false }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.False(parsed.WebEnable)

// ── my_tool_enable ────────────────────────────────────────────────────────────

[<Fact>]
let ``my_tool_enable true does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // MyToolEnable = true
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("my_tool_enable") |> fst,
                 "Expected my_tool_enable absent when true (default)")

[<Fact>]
let ``my_tool_enable false round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with MyToolEnable = false }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.False(parsed.MyToolEnable)

// ── transcription_provider ────────────────────────────────────────────────────

[<Fact>]
let ``transcription_provider default (groq) does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // TranscriptionProvider = "groq"
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("transcription_provider") |> fst,
                 "Expected transcription_provider absent when default (groq)")

[<Fact>]
let ``transcription_provider non-default round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with TranscriptionProvider = "openai" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("openai", parsed.TranscriptionProvider)

// ── transcription_language ────────────────────────────────────────────────────

[<Fact>]
let ``transcription_language None does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // TranscriptionLanguage = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("transcription_language") |> fst,
                 "Expected transcription_language absent when None")

[<Fact>]
let ``transcription_language Some round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with TranscriptionLanguage = Some "zh" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "zh", parsed.TranscriptionLanguage)

// ── dream_annotate_line_ages ──────────────────────────────────────────────────

[<Fact>]
let ``dream_annotate_line_ages true does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // DreamAnnotateLineAges = true
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("dream_annotate_line_ages") |> fst,
                 "Expected dream_annotate_line_ages absent when true (default)")

[<Fact>]
let ``dream_annotate_line_ages false round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with DreamAnnotateLineAges = false }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.False(parsed.DreamAnnotateLineAges)

// ── provider_extra_headers ────────────────────────────────────────────────────

[<Fact>]
let ``provider_extra_headers empty does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ProviderExtraHeaders = Map.empty
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("provider_extra_headers") |> fst,
                 "Expected provider_extra_headers absent when empty")

[<Fact>]
let ``provider_extra_headers single provider round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with
                    ProviderExtraHeaders = Map.ofList [ "openai", Map.ofList [ "X-Custom", "myval" ] ] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  ->
        Assert.Equal(1, parsed.ProviderExtraHeaders.Count)
        let hdrs = parsed.ProviderExtraHeaders |> Map.find "openai"
        Assert.Equal("myval", hdrs |> Map.find "X-Custom")

[<Fact>]
let ``provider_extra_headers multiple providers round-trip correctly`` () =
    let cfg = { BotSharpConfig.defaults with
                    ProviderExtraHeaders = Map.ofList [
                        "openai",    Map.ofList [ "H1", "v1" ]
                        "anthropic", Map.ofList [ "H2", "v2"; "H3", "v3" ] ] }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  ->
        Assert.Equal(2, parsed.ProviderExtraHeaders.Count)
        Assert.Equal(1, (parsed.ProviderExtraHeaders |> Map.find "openai").Count)
        Assert.Equal(2, (parsed.ProviderExtraHeaders |> Map.find "anthropic").Count)

// ── api_port ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``api_port None does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ApiPort = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("api_port") |> fst,
                 "Expected api_port absent when None")

[<Fact>]
let ``api_port Some round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ApiPort = Some 8080 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some 8080, parsed.ApiPort)

// ── api_timeout_seconds ───────────────────────────────────────────────────────

[<Fact>]
let ``api_timeout_seconds default does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ApiTimeoutSeconds = 120
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("api_timeout_seconds") |> fst,
                 "Expected api_timeout_seconds absent when 120 (default)")

[<Fact>]
let ``api_timeout_seconds non-default round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ApiTimeoutSeconds = 300 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(300, parsed.ApiTimeoutSeconds)

[<Fact>]
let ``api_timeout_seconds zero round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ApiTimeoutSeconds = 0 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(0, parsed.ApiTimeoutSeconds)

// ── api_host ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``api_host default does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ApiHost = "127.0.0.1"
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("api_host") |> fst,
                 "Expected api_host absent when 127.0.0.1 (default)")

[<Fact>]
let ``api_host non-default round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ApiHost = "0.0.0.0" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("0.0.0.0", parsed.ApiHost)

// ── exec_sandbox ──────────────────────────────────────────────────────────────

[<Fact>]
let ``exec_sandbox absent when empty`` () =
    let cfg = BotSharpConfig.defaults  // ExecSandbox = ""
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("exec_sandbox") |> fst,
                 "Expected exec_sandbox absent when empty (default)")

[<Fact>]
let ``exec_sandbox round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ExecSandbox = "bwrap" }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal("bwrap", parsed.ExecSandbox)

// ── context_block_limit ───────────────────────────────────────────────────────

[<Fact>]
let ``context_block_limit None does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // ContextBlockLimit = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("context_block_limit") |> fst,
                 "Expected context_block_limit absent when None")

[<Fact>]
let ``context_block_limit Some round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with ContextBlockLimit = Some 512 }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some 512, parsed.ContextBlockLimit)

// ── max_iterations_message ────────────────────────────────────────────────────

[<Fact>]
let ``max_iterations_message None does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // MaxIterationsMessage = None
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("max_iterations_message") |> fst,
                 "Expected max_iterations_message absent when None")

[<Fact>]
let ``max_iterations_message Some round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with MaxIterationsMessage = Some "Too many steps." }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.Equal(Some "Too many steps.", parsed.MaxIterationsMessage)

// ── fail_on_tool_error ────────────────────────────────────────────────────────

[<Fact>]
let ``fail_on_tool_error false does not emit key`` () =
    let cfg = BotSharpConfig.defaults  // FailOnToolError = false
    let json = serializeConfig cfg
    use doc = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("fail_on_tool_error") |> fst,
                 "Expected fail_on_tool_error absent when false")

[<Fact>]
let ``fail_on_tool_error true round-trips correctly`` () =
    let cfg = { BotSharpConfig.defaults with FailOnToolError = true }
    match roundTrip cfg with
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")
    | Ok parsed  -> Assert.True(parsed.FailOnToolError)
