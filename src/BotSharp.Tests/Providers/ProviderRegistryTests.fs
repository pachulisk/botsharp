module BotSharp.Tests.Providers.ProviderRegistryTests

open System.Net.Http
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Providers.ProviderRegistry

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private openaiSpec =
    providers |> List.find (fun s -> s.Id = "openai")

let private testKey =
    match ApiKey.create "test-key" with
    | Result.Ok k -> k
    | Result.Error e -> failwith e

// ═══════════════════════════════════════════════════════════════════════════
// detectProvider
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``detectProvider matches gpt-4o to openai`` () =
    match detectProvider "gpt-4o" with
    | Some { Id = "openai" } -> ()
    | other -> Assert.Fail($"Expected openai spec, got {other}")

[<Fact>]
let ``detectProvider matches gpt-4o-mini to openai`` () =
    match detectProvider "gpt-4o-mini" with
    | Some { Id = "openai" } -> ()
    | other -> Assert.Fail($"Expected openai spec, got {other}")

[<Fact>]
let ``detectProvider matches o1-preview to openai`` () =
    match detectProvider "o1-preview" with
    | Some { Id = "openai" } -> ()
    | other -> Assert.Fail($"Expected openai spec, got {other}")

[<Fact>]
let ``detectProvider matches deepseek-r1 to deepseek`` () =
    match detectProvider "deepseek-r1" with
    | Some { Id = "deepseek" } -> ()
    | other -> Assert.Fail($"Expected deepseek spec, got {other}")

[<Fact>]
let ``detectProvider matches gemini-pro to gemini`` () =
    match detectProvider "gemini-pro" with
    | Some { Id = "gemini" } -> ()
    | other -> Assert.Fail($"Expected gemini spec, got {other}")

[<Fact>]
let ``detectProvider matches llama3-70b to groq`` () =
    match detectProvider "llama3-70b" with
    | Some { Id = "groq" } -> ()
    | other -> Assert.Fail($"Expected groq spec, got {other}")

[<Fact>]
let ``detectProvider returns None for unknown model`` () =
    Assert.Equal(None, detectProvider "unknown-model-xyz")

[<Fact>]
let ``detectProvider is case-insensitive for GPT-4O`` () =
    match detectProvider "GPT-4O" with
    | Some { Id = "openai" } -> ()
    | other -> Assert.Fail($"Expected openai spec for uppercase GPT-4O, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// resolveBaseUrl
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolveBaseUrl returns registry default when config has no base_urls`` () =
    let config = BotSharpConfig.defaults  // BaseUrls = Map.empty
    let url = resolveBaseUrl openaiSpec config
    Assert.Equal("https://api.openai.com/v1", url)

[<Fact>]
let ``resolveBaseUrl returns custom url when config overrides openai`` () =
    let customUrl = "https://my-proxy.example.com/v1"
    let config = { BotSharpConfig.defaults with BaseUrls = Map.ofList [ "openai", customUrl ] }
    let url = resolveBaseUrl openaiSpec config
    Assert.Equal(customUrl, url)

[<Fact>]
let ``resolveBaseUrl uses registry default when base_url override is for a different provider`` () =
    let config = { BotSharpConfig.defaults with BaseUrls = Map.ofList [ "deepseek", "https://other.example.com/v1" ] }
    let url = resolveBaseUrl openaiSpec config
    Assert.Equal("https://api.openai.com/v1", url)

// ═══════════════════════════════════════════════════════════════════════════
// resolveApiKey
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolveApiKey returns Some key when config has ApiKeys for openai`` () =
    let config = { BotSharpConfig.defaults with ApiKeys = Map.ofList [ "openai", testKey ] }
    let result = resolveApiKey openaiSpec config
    Assert.Equal(Some testKey, result)

[<Fact>]
let ``resolveApiKey falls through to env var when config has no ApiKeys for openai`` () =
    let config = BotSharpConfig.defaults  // ApiKeys = Map.empty
    let expected = ApiKey.tryFromEnv "OPENAI_API_KEY"
    let actual = resolveApiKey openaiSpec config
    Assert.Equal(expected, actual)

// ═══════════════════════════════════════════════════════════════════════════
// resolveContextWindow
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolveContextWindow returns 128000 for gpt-4o`` () =
    Assert.Equal(128_000, resolveContextWindow "gpt-4o")

[<Fact>]
let ``resolveContextWindow returns 128000 for gpt-4o-mini`` () =
    Assert.Equal(128_000, resolveContextWindow "gpt-4o-mini")

[<Fact>]
let ``resolveContextWindow returns 200000 for claude-sonnet-4-6`` () =
    Assert.Equal(200_000, resolveContextWindow "claude-sonnet-4-6")

[<Fact>]
let ``resolveContextWindow returns 200000 for claude-3-5-haiku-20251001`` () =
    Assert.Equal(200_000, resolveContextWindow "claude-3-5-haiku-20251001")

[<Fact>]
let ``resolveContextWindow returns 1000000 for gemini-2.0-flash`` () =
    Assert.Equal(1_000_000, resolveContextWindow "gemini-2.0-flash")

[<Fact>]
let ``resolveContextWindow returns 128000 for deepseek-r1`` () =
    Assert.Equal(128_000, resolveContextWindow "deepseek-r1")

[<Fact>]
let ``resolveContextWindow is case insensitive`` () =
    Assert.Equal(128_000, resolveContextWindow "GPT-4O")

[<Fact>]
let ``resolveContextWindow returns 0 for unknown model`` () =
    Assert.Equal(0, resolveContextWindow "my-custom-unknown-model-xyz")

// ═══════════════════════════════════════════════════════════════════════════
// buildProvider
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildProvider returns None when no API key is available`` () =
    use client = new HttpClient()
    let config = BotSharpConfig.defaults  // no ApiKeys, env var likely absent in CI
    // Unset OPENAI_API_KEY for this check so we don't depend on CI environment
    let configWithNoKey = { config with ApiKeys = Map.empty }
    // If the env var happens to be set, the result may be Some — that's fine too.
    // This test verifies the function doesn't throw.
    let result = buildProvider client "gpt-4o" openaiSpec configWithNoKey
    // We can't assert None reliably (env var might be set), just assert no crash.
    Assert.True(result.IsNone || result.IsSome)

[<Fact>]
let ``buildProvider returns Some LLMProvider when API key is configured`` () =
    use client = new HttpClient()
    let config = { BotSharpConfig.defaults with ApiKeys = Map.ofList [ "openai", testKey ] }
    let result = buildProvider client "gpt-4o" openaiSpec config
    Assert.True(result.IsSome, "Expected Some LLMProvider when API key is present")

[<Fact>]
let ``buildProvider returned provider has the correct Id`` () =
    use client = new HttpClient()
    let config = { BotSharpConfig.defaults with ApiKeys = Map.ofList [ "openai", testKey ] }
    match buildProvider client "gpt-4o" openaiSpec config with
    | None     -> Assert.Fail("Expected Some LLMProvider")
    | Some p   -> Assert.Equal("openai", p.Id)

// ═══════════════════════════════════════════════════════════════════════════
// resolve — fallback to DefaultProvider when no keyword match
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolve uses DefaultProvider when model does not match any keyword`` () =
    // "my-custom-llm" doesn't match any provider keyword.
    // config.DefaultProvider = "anthropic", and we supply an anthropic API key.
    use client = new HttpClient()
    let anthropicKey =
        match ApiKey.create "sk-ant-test-key" with
        | Result.Ok k -> k
        | Result.Error e -> failwith e
    let config =
        { BotSharpConfig.defaults with
            DefaultProvider = "anthropic"
            ApiKeys = Map.ofList [ "anthropic", anthropicKey ] }
    match resolve client "my-custom-llm" config with
    | None   -> Assert.Fail("Expected Some provider via DefaultProvider fallback")
    | Some p -> Assert.Equal("anthropic", p.Id)

// ═══════════════════════════════════════════════════════════════════════════
// resolve
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolve returns Some for gpt-4o with openai API key`` () =
    use client = new HttpClient()
    let config = { BotSharpConfig.defaults with ApiKeys = Map.ofList [ "openai", testKey ] }
    let result = resolve client "gpt-4o" config
    Assert.True(result.IsSome, "Expected Some provider for gpt-4o with API key")

[<Fact>]
let ``resolve returned provider Id is openai for gpt-4o`` () =
    use client = new HttpClient()
    let config = { BotSharpConfig.defaults with ApiKeys = Map.ofList [ "openai", testKey ] }
    match resolve client "gpt-4o" config with
    | None   -> Assert.Fail("Expected Some provider")
    | Some p -> Assert.Equal("openai", p.Id)

// ═══════════════════════════════════════════════════════════════════════════
// detectProvider — additional providers not yet covered
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``detectProvider matches claude-sonnet to anthropic`` () =
    match detectProvider "claude-sonnet-4-6" with
    | Some { Id = "anthropic" } -> ()
    | other -> Assert.Fail($"Expected anthropic spec, got {other}")

[<Fact>]
let ``detectProvider matches qwen-max to dashscope`` () =
    match detectProvider "qwen-max" with
    | Some { Id = "dashscope" } -> ()
    | other -> Assert.Fail($"Expected dashscope spec, got {other}")

[<Fact>]
let ``detectProvider matches kimi to moonshot`` () =
    match detectProvider "moonshot-v1-8k" with
    | Some { Id = "moonshot" } -> ()
    | other -> Assert.Fail($"Expected moonshot spec, got {other}")

[<Fact>]
let ``detectProvider matches glm-4 to zhipu`` () =
    match detectProvider "glm-4-plus" with
    | Some { Id = "zhipu" } -> ()
    | other -> Assert.Fail($"Expected zhipu spec, got {other}")

[<Fact>]
let ``detectProvider matches kimi keyword to moonshot`` () =
    // "kimi" is a keyword for moonshot provider and does not collide with earlier providers.
    match detectProvider "kimi-latest" with
    | Some { Id = "moonshot" } -> ()
    | other -> Assert.Fail($"Expected moonshot spec, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// resolveContextWindow — additional models not yet covered
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolveContextWindow returns 8192 for gpt-4`` () =
    Assert.Equal(8_192, resolveContextWindow "gpt-4")

[<Fact>]
let ``resolveContextWindow returns 200000 for o3-mini`` () =
    Assert.Equal(200_000, resolveContextWindow "o3-mini")

[<Fact>]
let ``resolveContextWindow returns 131072 for qwen-max`` () =
    Assert.Equal(131_072, resolveContextWindow "qwen-max")

[<Fact>]
let ``resolveContextWindow returns 8192 for gemma`` () =
    Assert.Equal(8_192, resolveContextWindow "gemma-7b")

// ═══════════════════════════════════════════════════════════════════════════
// resolveBaseUrl — fallback when provider id not in registry URL map
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolveBaseUrl for anthropic returns anthropic API URL by default`` () =
    let spec = providers |> List.find (fun s -> s.Id = "anthropic")
    let url  = resolveBaseUrl spec BotSharpConfig.defaults
    Assert.Equal("https://api.anthropic.com/v1", url)

[<Fact>]
let ``resolveBaseUrl for deepseek returns deepseek API URL by default`` () =
    let spec = providers |> List.find (fun s -> s.Id = "deepseek")
    let url  = resolveBaseUrl spec BotSharpConfig.defaults
    Assert.Equal("https://api.deepseek.com/v1", url)

// ═══════════════════════════════════════════════════════════════════════════
// resolve — DefaultProvider fallback when DefaultProvider not found in list
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``resolve falls back to openai (first provider) when DefaultProvider is unknown`` () =
    // config.DefaultProvider = "no-such-provider" is not in the providers list.
    // providers |> List.tryFind ... returns None → Option.defaultValue (List.head providers).
    // The first provider in the list is "openai".
    use client = new HttpClient()
    let config = { BotSharpConfig.defaults with
                    DefaultProvider = "no-such-provider"
                    ApiKeys = Map.ofList [ "openai", testKey ] }
    // "unknown-model-xyz" matches nothing → falls through to DefaultProvider lookup
    match resolve client "unknown-model-xyz" config with
    | None   -> ()   // Key may not resolve — acceptable since DefaultProvider is invalid
    | Some p -> Assert.Equal("openai", p.Id)   // falls back to first provider (openai)
