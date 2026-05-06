module BotSharp.Infrastructure.Memory.ModelRecommendation

open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Model recommendation for two-phase memory pipeline
//
// Three-level fallback: config value → provider recommendation → DefaultModel
//
// Phase 1 (extraction): cheapest model in the same family — cost priority
// Phase 2 (consolidation): strongest model in the same family — quality priority
//
// Mirrors Codex memories/write/lib.rs:78-106 model selection logic.
// ═══════════════════════════════════════════════════════════════════════════

/// Per-provider recommended model pair for Phase 1 (cheap) and Phase 2 (strong).
let recommendedModels : Map<string, struct(string * string)> =
    Map.ofList [
        // International
        "openai",        struct("gpt-4o-mini", "gpt-4o")
        "azure-openai",  struct("gpt-4o-mini", "gpt-4o")
        "anthropic",     struct("claude-haiku-4-5-20251001", "claude-sonnet-4-5-20251001")
        "gemini",        struct("gemini-2.0-flash", "gemini-2.5-pro")
        "mistral",       struct("mistral-small-latest", "mistral-large-latest")
        "xai",           struct("grok-3-mini", "grok-3")
        "cohere",        struct("command-r", "command-r-plus")
        // China
        "deepseek",      struct("deepseek-v4-flash", "deepseek-v4-pro")
        "zhipu",         struct("glm-4-flash", "glm-4-plus")
        "dashscope",     struct("qwen-turbo", "qwen-max")
        "qianfan",       struct("ernie-speed-pro", "ernie-4.0-turbo")
        "doubao",        struct("doubao-1.5-lite-32k", "doubao-1.5-pro-256k")
        "moonshot",      struct("moonshot-v1-8k", "moonshot-v1-128k")
        "lingyiwanwu",   struct("yi-lightning", "yi-large")
        "stepfun",       struct("step-2-flash", "step-2-16k")
        "baichuan",      struct("Baichuan4-Air", "Baichuan4")
        "minimax",       struct("MiniMax-Text-01", "MiniMax-M1")
        "spark",         struct("spark-lite", "spark-max")
        "xiaomi-mimo",   struct("MiMo-v2.5-Lite", "MiMo-v2.5-Pro")
        // Inference acceleration / open-source hosting
        "groq",          struct("llama-3.3-70b-versatile", "llama-3.3-70b-versatile")
        "together",      struct("meta-llama/Llama-3.1-8B-Instruct-Turbo", "meta-llama/Llama-3.1-70B-Instruct-Turbo")
        "fireworks",     struct("accounts/fireworks/models/llama-v3p1-8b-instruct", "accounts/fireworks/models/llama-v3p1-70b-instruct")
        // Local
        "ollama",        struct("qwen3:8b", "qwen3:32b")
        "lmstudio",      struct("qwen3:8b", "qwen3:32b")
    ]

/// Resolve Phase 1 model: config → recommendation table → DefaultModel.
let resolvePhase1Model (config: BotSharpConfig) : string =
    config.Phase1Model
    |> Option.orElseWith (fun () ->
        recommendedModels
        |> Map.tryFind config.DefaultProvider
        |> Option.map (fun struct(p1, _) -> p1))
    |> Option.defaultValue config.DefaultModel

/// Resolve Phase 2 model: config → recommendation table → DefaultModel.
let resolvePhase2Model (config: BotSharpConfig) : string =
    config.Phase2Model
    |> Option.orElseWith (fun () ->
        recommendedModels
        |> Map.tryFind config.DefaultProvider
        |> Option.map (fun struct(_, p2) -> p2))
    |> Option.defaultValue config.DefaultModel

/// Log the resolved models at startup.
let logModelSelection (config: BotSharpConfig) : unit =
    let p1 = resolvePhase1Model config
    let p2 = resolvePhase2Model config
    let p1Source =
        match config.Phase1Model with
        | Some _ -> "configured"
        | None ->
            match recommendedModels |> Map.tryFind config.DefaultProvider with
            | Some _ -> sprintf "recommended for %s" config.DefaultProvider
            | None -> "fallback to DefaultModel"
    let p2Source =
        match config.Phase2Model with
        | Some _ -> "configured"
        | None ->
            match recommendedModels |> Map.tryFind config.DefaultProvider with
            | Some _ -> sprintf "recommended for %s" config.DefaultProvider
            | None -> "fallback to DefaultModel"
    eprintfn "[memory] Phase 1 model: %s (%s)" p1 p1Source
    eprintfn "[memory] Phase 2 model: %s (%s)" p2 p2Source
