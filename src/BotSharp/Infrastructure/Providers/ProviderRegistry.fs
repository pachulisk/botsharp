module BotSharp.Infrastructure.Providers.ProviderRegistry

open System.Net.Http
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Providers.OpenAICompatAdapter

// ═══════════════════════════════════════════════════════════════════════════
// Provider registry
//
// Each ProviderSpec describes a known LLM provider: which environment-variable
// holds the API key, which base URL to POST to, which model keywords identify
// it, and which capabilities it supports.
//
// detectProvider matches a requested model name against the keyword lists and
// returns the matching spec (first match wins).  When no spec matches, it
// falls through to the configured default provider.
// ═══════════════════════════════════════════════════════════════════════════

let providers : NonEmptyList<ProviderSpec> =
    // ── Standard providers (matched by model-name keyword) ──────────────────
    NonEmptyList.create
        { Id           = "openai"
          Keywords     = [ "gpt-"; "o1-"; "o3-"; "o4-" ]
          Backend      = OpenAICompatBackend
          IsGateway    = false
          Capabilities = Set.ofList [ FunctionCalling; VisionInput; Streaming; StreamUsageTracking ]
          ThinkingStyle = Some ReasoningEffortParam
          EnvKeyName   = "OPENAI_API_KEY" }
        [ { Id           = "anthropic"
            Keywords     = [ "claude" ]
            Backend      = OpenAICompatBackend   // Anthropic has an OpenAI-compat endpoint
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; VisionInput; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "ANTHROPIC_API_KEY" }

          { Id           = "deepseek"
            Keywords     = [ "deepseek-v4-"; "deepseek-r"; "deepseek-chat"; "deepseek-reasoner"; "deepseek-v4-flash"; "deepseek-v4-pro"; "deepseek" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; ExtendedThinking; Streaming; StreamUsageTracking ]
            ThinkingStyle = Some ReasoningSplit
            EnvKeyName   = "DEEPSEEK_API_KEY" }

          { Id           = "gemini"
            Keywords     = [ "gemini" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; VisionInput; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "GEMINI_API_KEY" }

          { Id           = "dashscope"
            Keywords     = [ "qwen" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; VisionInput; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "DASHSCOPE_API_KEY" }

          { Id           = "moonshot"
            Keywords     = [ "moonshot"; "kimi" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "MOONSHOT_API_KEY" }

          { Id           = "minimax"
            Keywords     = [ "minimax" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "MINIMAX_API_KEY" }

          { Id           = "zhipu"
            Keywords     = [ "glm"; "zai" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "ZAI_API_KEY" }

          { Id           = "xiaomi-mimo"
            Keywords     = [ "mimo" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; ExtendedThinking; Streaming ]
            ThinkingStyle = Some ReasoningSplit
            EnvKeyName   = "MIMO_API_KEY" }

          { Id           = "groq"
            Keywords     = [ "llama"; "mixtral-8x7b"; "gemma"; "groq" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "GROQ_API_KEY" }

          // ── Gateways (detected by model prefix or API key, route any model) ─────

          { Id           = "openrouter"
            Keywords     = [ "openrouter" ]
            Backend      = OpenAICompatBackend
            IsGateway    = true
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "OPENROUTER_API_KEY" }

          { Id           = "siliconflow"
            Keywords     = [ "siliconflow" ]
            Backend      = OpenAICompatBackend
            IsGateway    = true
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "OPENAI_API_KEY" }

          { Id           = "aihubmix"
            Keywords     = [ "aihubmix" ]
            Backend      = OpenAICompatBackend
            IsGateway    = true
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "OPENAI_API_KEY" }

          { Id           = "volcengine"
            Keywords     = [ "doubao"; "skylark" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "VOLCENGINE_API_KEY" }

          { Id           = "mistral"
            Keywords     = [ "mistral"; "codestral"; "pixtral" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "MISTRAL_API_KEY" }

          { Id           = "together"
            Keywords     = [ "together" ]
            Backend      = OpenAICompatBackend
            IsGateway    = true
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "TOGETHER_API_KEY" }

          { Id           = "perplexity"
            Keywords     = [ "sonar" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "PERPLEXITY_API_KEY" }

          // ── Local deployment ─────────────────────────────────────────────────────

          { Id           = "ollama"
            Keywords     = [ "ollama" ]
            Backend      = OpenAICompatBackend
            IsGateway    = false
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "" }       // Ollama doesn't require an API key

          { Id           = "vllm"
            Keywords     = [ "vllm" ]
            Backend      = OpenAICompatBackend
            IsGateway    = true       // routes any model through vLLM
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "" }       // vLLM doesn't require an API key

          { Id           = "lmstudio"
            Keywords     = [ "lmstudio"; "lm-studio" ]
            Backend      = OpenAICompatBackend
            IsGateway    = true
            Capabilities = Set.ofList [ FunctionCalling; Streaming ]
            ThinkingStyle = None
            EnvKeyName   = "" }       // LM Studio doesn't require an API key
        ]

/// Known base URLs for each provider (used when no custom base_url is set)
let private baseUrls : Map<string, string> =
    Map.ofList [
        "openai",      "https://api.openai.com/v1"
        "anthropic",   "https://api.anthropic.com/v1"
        "openrouter",  "https://openrouter.ai/api/v1"
        "deepseek",    "https://api.deepseek.com/v1"
        "gemini",      "https://generativelanguage.googleapis.com/v1beta/openai"
        "groq",        "https://api.groq.com/openai/v1"
        "dashscope",   "https://dashscope.aliyuncs.com/compatible-mode/v1"
        "moonshot",    "https://api.moonshot.ai/v1"
        "minimax",     "https://api.minimax.io/v1"
        "zhipu",       "https://open.bigmodel.cn/api/paas/v4"
        "xiaomi-mimo", "https://token-plan-cn.xiaomimimo.com/v1"
        "siliconflow", "https://api.siliconflow.cn/v1"
        "aihubmix",    "https://aihubmix.com/v1"
        "ollama",      "http://localhost:11434/v1"
        "volcengine",  "https://ark.cn-beijing.volces.com/api/v3"
        "mistral",     "https://api.mistral.ai/v1"
        "together",    "https://api.together.xyz/v1"
        "perplexity",  "https://api.perplexity.ai"
        "vllm",        "http://localhost:8000/v1"
        "lmstudio",    "http://localhost:1234/v1"
    ]

// ── Known context window sizes ────────────────────────────────────────────
// Used when config.ContextWindowTokens = 0 (auto-detect mode).
// Entries are (keyword, contextWindowTokens) pairs; first keyword match wins.
// When a model isn't listed the caller falls back to config or 0 (no trim).
let private knownContextWindows : (string * int) list = [
    // OpenAI
    "gpt-4o",         128_000
    "gpt-4-turbo",    128_000
    "gpt-4",            8_192
    "gpt-3.5-turbo",  16_385
    "o1-mini",        128_000
    "o1-preview",     128_000
    "o1",             200_000
    "o3-mini",        200_000
    "o3",             200_000
    "o4-mini",        200_000
    // Anthropic
    "claude-3-5-sonnet",  200_000
    "claude-3-5-haiku",   200_000
    "claude-3-7-sonnet",  200_000
    "claude-4",           200_000
    "claude-sonnet",      200_000
    "claude-haiku",       200_000
    "claude-opus",        200_000
    "claude",             200_000
    // Google
    "gemini-2.0",      1_000_000
    "gemini-1.5-pro",  2_000_000
    "gemini-1.5-flash",1_000_000
    "gemini-1.0-pro",     32_768
    "gemini",           1_000_000
    // DeepSeek
    "deepseek-v4-pro",   65_536    // 64K max output (reasoning model)
    "deepseek-v4-flash", 65_536
    "deepseek-r1",       128_000
    "deepseek",          128_000
    // Qwen
    "qwen-long",     1_000_000
    "qwen-max",        131_072
    "qwen-plus",       131_072
    "qwen",            131_072
    // Groq / open models
    "llama-3.3",       131_072
    "llama-3.1",       131_072
    "llama-3",         131_072
    "llama",            32_768
    "mixtral",          32_768
    "gemma",             8_192
    // Xiaomi MiMo
    "mimo-v2.5-pro",     1_048_576
    "mimo-v2-pro",       1_048_576
    "mimo-v2-omni",        262_144
    "mimo-v2-flash",       262_144
    "mimo",                262_144
    // Moonshot / Kimi
    "moonshot-v1-128k",  128_000
    "moonshot",          128_000
    // Volcengine (Doubao)
    "doubao-pro",        128_000
    "doubao",             32_000
    "skylark",            32_000
    // Mistral
    "mistral-large",     128_000
    "mistral-medium",     32_000
    "codestral",          32_000
    "pixtral",           128_000
    "mistral",            32_000
    // Misc
    "minimax",           245_760
    "glm-4",             128_000
    "sonar-pro",         200_000
    "sonar",             127_072
]

/// Auto-detect the context window size for a model.
/// Returns 0 when the model isn't in the known list (disables trimming).
/// The explicit config value takes priority — this is only used when
/// config.ContextWindowTokens = 0.
let resolveContextWindow (model: string) : int =
    let lower = model.ToLowerInvariant()
    knownContextWindows
    |> List.tryFind (fun (kw, _) -> lower.Contains(kw))
    |> Option.map snd
    |> Option.defaultValue 0

/// Find the ProviderSpec whose keywords appear in the model name.
/// Returns None if no spec matches (caller should use the default provider).
let detectProvider (model: string) : ProviderSpec option =
    let lowerModel = model.ToLowerInvariant()
    providers |> NonEmptyList.tryFind (fun spec ->
        spec.Keywords |> List.exists (fun kw -> lowerModel.Contains(kw.ToLowerInvariant())))

/// Resolve the base URL for a provider.
/// Config's base_urls takes priority; falls back to the registry default table.
let resolveBaseUrl (spec: ProviderSpec) (config: BotSharpConfig) : string =
    match config.BaseUrls |> Map.tryFind spec.Id with
    | Some url -> url
    | None     -> baseUrls |> Map.tryFind spec.Id |> Option.defaultValue "https://api.openai.com/v1"

/// Look up the API key for a provider, first from config, then from env vars.
let resolveApiKey (spec: ProviderSpec) (config: BotSharpConfig) : ApiKey option =
    match config.ApiKeys |> Map.tryFind spec.Id with
    | Some k -> Some k
    | None   -> ApiKey.tryFromEnv spec.EnvKeyName

/// Resolve per-provider extra HTTP headers from config.
/// Returns the header map for the given provider, or empty if none configured.
let resolveExtraHeaders (spec: ProviderSpec) (config: BotSharpConfig) : Map<string, string> =
    config.ProviderExtraHeaders |> Map.tryFind spec.Id |> Option.defaultValue Map.empty

/// Build an LLMProvider for the given spec, using a shared HttpClient.
/// Returns None when no API key is available.
///
/// `streamHealthCheck` — called periodically during SSE streaming.
/// Receives (idleSeconds, totalSeconds, chunksReceived) and returns Some(reason) to abort.
/// This enables CLIPS rules to evaluate stream health (timeout is just one rule).
let buildProvider
    (client            : HttpClient)
    (model             : string)
    (spec              : ProviderSpec)
    (config            : BotSharpConfig)
    (streamHealthCheck : int -> int -> int -> string option)
    : LLMProvider option =
    match resolveApiKey spec config with
    | None     -> None
    | Some key ->
        let baseUrl = resolveBaseUrl spec config
        let userOverrodeBaseUrl = config.BaseUrls |> Map.containsKey spec.Id
        let caps =
            if userOverrodeBaseUrl then spec.Capabilities |> Set.remove StreamUsageTracking
            else spec.Capabilities
        let extraHeaders = resolveExtraHeaders spec config
        Some (createProvider client spec.Id baseUrl key model caps config.ProviderRetryMode extraHeaders streamHealthCheck)

/// Resolve a provider for the requested model.
/// Falls back to the configured default provider if no keyword match is found.
let resolve
    (client            : HttpClient)
    (model             : string)
    (config            : BotSharpConfig)
    (streamHealthCheck : int -> int -> int -> string option)
    : LLMProvider option =
    let healthCheck = streamHealthCheck
    let spec =
        match detectProvider model with
        | Some s -> s
        | None   ->
            providers |> NonEmptyList.tryFind (fun s -> s.Id = config.DefaultProvider)
            |> Option.defaultValue (NonEmptyList.head providers)
    buildProvider client model spec config healthCheck
