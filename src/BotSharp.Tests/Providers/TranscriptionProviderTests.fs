module BotSharp.Tests.Providers.TranscriptionProviderTests

open System
open System.Net.Http
open Xunit
open BotSharp.Infrastructure.Providers.TranscriptionProvider

// ═══════════════════════════════════════════════════════════════════════════
// TranscriptionProvider unit tests
//
// Pure config constructors are tested directly.
// transcribe edge-cases (empty key, missing file) are testable without
// hitting a real HTTP endpoint.
// ═══════════════════════════════════════════════════════════════════════════

// ── defaultGroqConfig ─────────────────────────────────────────────────────

[<Fact>]
let ``defaultGroqConfig sets correct endpoint and model`` () =
    let cfg = defaultGroqConfig "test-key"
    Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", cfg.ApiUrl)
    Assert.Equal("whisper-large-v3", cfg.Model)
    Assert.Equal("test-key", cfg.ApiKey)

// ── defaultOpenAIConfig ───────────────────────────────────────────────────

[<Fact>]
let ``defaultOpenAIConfig sets correct endpoint and model`` () =
    let cfg = defaultOpenAIConfig "sk-test"
    Assert.Equal("https://api.openai.com/v1/audio/transcriptions", cfg.ApiUrl)
    Assert.Equal("whisper-1", cfg.Model)
    Assert.Equal("sk-test", cfg.ApiKey)

// ── transcribe edge cases ─────────────────────────────────────────────────

[<Fact>]
let ``transcribe returns empty string when ApiKey is empty`` () =
    use client = new HttpClient()
    let cfg = { ApiUrl = "https://example.com"; ApiKey = ""; Model = "whisper-1" }
    let result = transcribe client cfg "/nonexistent/file.ogg" |> Async.RunSynchronously
    Assert.Equal("", result)

[<Fact>]
let ``transcribe returns empty string when file does not exist`` () =
    use client = new HttpClient()
    let cfg = defaultGroqConfig "test-key"
    let result = transcribe client cfg "/nonexistent/audio.ogg" |> Async.RunSynchronously
    Assert.Equal("", result)
