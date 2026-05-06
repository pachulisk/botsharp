module BotSharp.Tests.Infrastructure.RlmToolTests

open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.RlmTool

// ═══════════════════════════════════════════════════════════════════════════
// RlmTool unit tests
//
// resolveRlmChildModel has 3-level fallback:
//   1. RlmChildModel config field (explicit override)
//   2. Phase1 model from recommendedModels table (cheap model for known provider)
//   3. DefaultModel (ultimate fallback)
// ═══════════════════════════════════════════════════════════════════════════

/// Build a minimal BotSharpConfig with given provider and no overrides.
let private mkConfig (provider: string) : BotSharpConfig =
    { BotSharpConfig.defaults with
        DefaultProvider = provider
        DefaultModel    = "fallback-model"
        RlmChildModel   = None }

[<Fact>]
let ``resolveRlmChildModel uses RlmChildModel config when set`` () =
    let cfg = { mkConfig "openai" with RlmChildModel = Some "my-custom-rlm-model" }
    Assert.Equal("my-custom-rlm-model", resolveRlmChildModel cfg)

[<Fact>]
let ``resolveRlmChildModel falls back to cheap Phase1 model for anthropic`` () =
    let cfg = mkConfig "anthropic"
    let model = resolveRlmChildModel cfg
    // Phase1 for anthropic → claude-haiku-* (cheap model)
    Assert.Contains("haiku", model)

[<Fact>]
let ``resolveRlmChildModel falls back to cheap Phase1 model for openai`` () =
    let cfg = mkConfig "openai"
    let model = resolveRlmChildModel cfg
    // Phase1 for openai → gpt-4o-mini (cheap model)
    Assert.Contains("mini", model)

[<Fact>]
let ``resolveRlmChildModel falls back to DefaultModel for unknown provider`` () =
    let cfg = mkConfig "unknown-provider"
    Assert.Equal("fallback-model", resolveRlmChildModel cfg)

[<Fact>]
let ``resolveRlmChildModel config override wins over provider table`` () =
    let cfg = { mkConfig "anthropic" with RlmChildModel = Some "override-model" }
    Assert.Equal("override-model", resolveRlmChildModel cfg)
