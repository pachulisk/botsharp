module BotSharp.Tests.Infrastructure.MemoryModuleTests

open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Memory.CitationParser
open BotSharp.Infrastructure.Memory.ModelRecommendation

// ═══════════════════════════════════════════════════════════════════════════
// CitationParser tests — pure parse functions
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseCitation returns None when no citation block present`` () =
    let result = parseCitation "Just some plain text with no citation."
    Assert.True(result.IsNone)

[<Fact>]
let ``parseCitation returns None for empty citation block`` () =
    let text = "<mem-citation>\n\n</mem-citation>"
    let result = parseCitation text
    Assert.True(result.IsNone)

[<Fact>]
let ``parseCitation parses single valid entry`` () =
    let text = "<mem-citation>\nMEMORY.md:12-15|note=[user prefers dark theme]\n</mem-citation>"
    match parseCitation text with
    | None -> Assert.Fail("Expected Some citation")
    | Some c ->
        Assert.Equal(1, c.Entries.Length)
        let e = c.Entries.[0]
        Assert.Equal("MEMORY.md", e.Path)
        Assert.Equal(12, e.LineStart)
        Assert.Equal(15, e.LineEnd)
        Assert.Equal("user prefers dark theme", e.Note)

[<Fact>]
let ``parseCitation parses multiple entries`` () =
    let text =
        "<mem-citation>\n" +
        "MEMORY.md:1-5|note=[project context]\n" +
        "rollout_summaries/deploy.md:3-8|note=[deploy procedure]\n" +
        "</mem-citation>"
    match parseCitation text with
    | None -> Assert.Fail("Expected Some citation")
    | Some c -> Assert.Equal(2, c.Entries.Length)

[<Fact>]
let ``parseCitation skips malformed lines without note delimiter`` () =
    let text = "<mem-citation>\nBAD_LINE_NO_NOTE\nMEMORY.md:1-3|note=[ok]\n</mem-citation>"
    match parseCitation text with
    | None -> Assert.Fail("Expected Some citation")
    | Some c -> Assert.Equal(1, c.Entries.Length)

[<Fact>]
let ``parseCitation returns None when end tag before start tag`` () =
    let text = "</mem-citation>some text<mem-citation>"
    Assert.True((parseCitation text).IsNone)

[<Fact>]
let ``parseCitation handles path with colon (e.g. Windows-style or nested path)`` () =
    // LastIndexOf(':') picks the range colon, not earlier ones in path
    let text = "<mem-citation>\ndir/sub/file.md:10-20|note=[nested]\n</mem-citation>"
    match parseCitation text with
    | None -> Assert.Fail("Expected Some citation")
    | Some c ->
        let e = c.Entries.[0]
        Assert.Equal("dir/sub/file.md", e.Path)
        Assert.Equal(10, e.LineStart)
        Assert.Equal(20, e.LineEnd)

// ── stripCitation ─────────────────────────────────────────────────────────

[<Fact>]
let ``stripCitation removes citation block from text`` () =
    let text = "Answer text.\n<mem-citation>\nMEMORY.md:1-3|note=[x]\n</mem-citation>"
    let visible, citation = stripCitation text
    Assert.Equal("Answer text.", visible)
    Assert.True(citation.IsSome)

[<Fact>]
let ``stripCitation returns full text unchanged when no citation block`` () =
    let text = "No citation here."
    let visible, citation = stripCitation text
    Assert.Equal("No citation here.", visible)
    Assert.True(citation.IsNone)

[<Fact>]
let ``stripCitation preserves text after citation block`` () =
    let text = "Before.\n<mem-citation>\nMEMORY.md:1-2|note=[n]\n</mem-citation>\nAfter."
    let visible, _ = stripCitation text
    // Both before and after parts are included in visible (trimmed)
    Assert.Contains("Before.", visible)
    Assert.Contains("After.", visible)

// ═══════════════════════════════════════════════════════════════════════════
// ModelRecommendation tests — three-level fallback logic
// ═══════════════════════════════════════════════════════════════════════════

/// Build a config with the given provider (no model overrides).
let private mkConfig (provider: string) : BotSharpConfig =
    { BotSharpConfig.defaults with
        DefaultProvider = provider
        DefaultModel    = "fallback-model"
        Phase1Model     = None
        Phase2Model     = None }

[<Fact>]
let ``resolvePhase1Model uses Phase1Model config when set`` () =
    let cfg = { mkConfig "openai" with Phase1Model = Some "my-phase1-model" }
    Assert.Equal("my-phase1-model", resolvePhase1Model cfg)

[<Fact>]
let ``resolvePhase2Model uses Phase2Model config when set`` () =
    let cfg = { mkConfig "openai" with Phase2Model = Some "my-phase2-model" }
    Assert.Equal("my-phase2-model", resolvePhase2Model cfg)

[<Fact>]
let ``resolvePhase1Model falls back to provider table for known provider`` () =
    let cfg = mkConfig "anthropic"
    let model = resolvePhase1Model cfg
    // Table maps anthropic Phase1 → claude-haiku-* (cheap model)
    Assert.Contains("haiku", model)

[<Fact>]
let ``resolvePhase2Model falls back to provider table for known provider`` () =
    let cfg = mkConfig "anthropic"
    let model = resolvePhase2Model cfg
    // Table maps anthropic Phase2 → claude-sonnet-* (strong model)
    Assert.Contains("sonnet", model)

[<Fact>]
let ``resolvePhase1Model uses DefaultModel for unknown provider`` () =
    let cfg = mkConfig "unknown-provider"
    Assert.Equal("fallback-model", resolvePhase1Model cfg)

[<Fact>]
let ``resolvePhase2Model uses DefaultModel for unknown provider`` () =
    let cfg = mkConfig "unknown-provider"
    Assert.Equal("fallback-model", resolvePhase2Model cfg)

[<Fact>]
let ``resolvePhase1Model is different from Phase2 model for same provider`` () =
    // For providers with distinct Phase1/Phase2 models (e.g. openai: mini vs full)
    let cfg = mkConfig "openai"
    let p1 = resolvePhase1Model cfg
    let p2 = resolvePhase2Model cfg
    // openai: gpt-4o-mini vs gpt-4o — different models
    Assert.NotEqual<string>(p1, p2)

[<Fact>]
let ``recommendedModels contains all major international providers`` () =
    let expected = [ "openai"; "anthropic"; "gemini"; "mistral"; "xai"; "cohere" ]
    for provider in expected do
        Assert.True(Map.containsKey provider recommendedModels,
                    $"Missing provider: {provider}")

[<Fact>]
let ``recommendedModels contains major China providers`` () =
    let expected = [ "deepseek"; "moonshot"; "dashscope"; "zhipu"; "doubao" ]
    for provider in expected do
        Assert.True(Map.containsKey provider recommendedModels,
                    $"Missing China provider: {provider}")

[<Fact>]
let ``resolvePhase1Model config override takes precedence over table`` () =
    // Even when the provider is in the table, explicit config wins
    let cfg = { mkConfig "openai" with Phase1Model = Some "custom-cheap-model" }
    Assert.Equal("custom-cheap-model", resolvePhase1Model cfg)
