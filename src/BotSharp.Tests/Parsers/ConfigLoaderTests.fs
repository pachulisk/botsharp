module BotSharp.Tests.Parsers.ConfigLoaderTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Config.ConfigLoader

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"cfgloader-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

let private writeJson (dir: string) (name: string) (json: string) : string =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, json)
    path

// ═══════════════════════════════════════════════════════════════════════════
// defaultConfigPath
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``defaultConfigPath contains .botsharp directory segment`` () =
    Assert.Contains(".botsharp", defaultConfigPath)

[<Fact>]
let ``defaultConfigPath ends with config.json`` () =
    Assert.True(defaultConfigPath.EndsWith("config.json"), $"Expected path ending in config.json, got: {defaultConfigPath}")

// ═══════════════════════════════════════════════════════════════════════════
// loadConfig — non-existent file returns defaults
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadConfig returns defaults when file does not exist`` () =
    let nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")
    let result = loadConfig nonExistentPath |> Async.RunSynchronously
    match result with
    | Result.Ok cfg ->
        Assert.Equal(BotSharpConfig.defaults.DefaultModel,    cfg.DefaultModel)
        Assert.Equal(BotSharpConfig.defaults.DefaultProvider, cfg.DefaultProvider)
        Assert.Equal(BotSharpConfig.defaults.Temperature,     cfg.Temperature)
        Assert.Equal(BotSharpConfig.defaults.MaxTokens,       cfg.MaxTokens)
    | Result.Error e -> Assert.Fail($"Expected defaults for missing file, got Error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// loadConfig — valid JSON files
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadConfig parses minimal valid JSON as defaults`` () =
    withTempDir (fun dir ->
        let path = writeJson dir "cfg.json" "{}"
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg ->
            Assert.Equal(BotSharpConfig.defaults.DefaultModel, cfg.DefaultModel)
        | Result.Error e -> Assert.Fail($"Expected Ok for empty JSON, got Error: {e}"))

[<Fact>]
let ``loadConfig reads default_model from file`` () =
    withTempDir (fun dir ->
        let json = """{"default_model": "gpt-4-turbo"}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal("gpt-4-turbo", cfg.DefaultModel)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads default_provider from file`` () =
    withTempDir (fun dir ->
        let json = """{"default_provider": "anthropic"}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal("anthropic", cfg.DefaultProvider)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads temperature from file`` () =
    withTempDir (fun dir ->
        let json = """{"temperature": 0.3}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(0.3, cfg.Temperature)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads max_tokens from file`` () =
    withTempDir (fun dir ->
        let json = """{"max_tokens": 8192}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(8192, cfg.MaxTokens)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads workspace_path from file`` () =
    withTempDir (fun dir ->
        // Use JsonSerializer to correctly escape the path value (handles backslashes on Windows)
        let escapedDir = System.Text.Json.JsonSerializer.Serialize(dir)   // includes surrounding quotes
        let json = $"""{{"workspace_path": {escapedDir}}}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(dir, cfg.WorkspacePath)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads api_keys map from file`` () =
    withTempDir (fun dir ->
        let json = """{"api_keys": {"openai": "sk-test-1234"}}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg ->
            Assert.True(cfg.ApiKeys.ContainsKey("openai"), "Expected 'openai' key in ApiKeys")
            let keyVal = ApiKey.value cfg.ApiKeys.["openai"]
            Assert.Equal("sk-test-1234", keyVal)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads allow_from wildcard`` () =
    withTempDir (fun dir ->
        let json = """{"allow_from": ["*"]}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg ->
            match cfg.AllowFrom with
            | AnyoneAllowed -> ()
            | other -> Assert.Fail($"Expected AnyoneAllowed, got {other}")
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads memory_window_size from file`` () =
    withTempDir (fun dir ->
        let json = """{"memory_window_size": 25}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(25, cfg.MemoryWindowSize)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

// ═══════════════════════════════════════════════════════════════════════════
// loadConfig — invalid JSON syntax
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadConfig returns Error for invalid JSON syntax`` () =
    withTempDir (fun dir ->
        let path = writeJson dir "cfg.json" "{not valid json!!"
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error msg -> Assert.Contains("Invalid JSON", msg)
        | Result.Ok _      -> Assert.Fail("Expected Error for invalid JSON syntax"))

[<Fact>]
let ``loadConfig returns Error for truncated JSON`` () =
    withTempDir (fun dir ->
        let path = writeJson dir "cfg.json" """{"default_model": """
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error msg -> Assert.Contains("Invalid JSON", msg)
        | Result.Ok _      -> Assert.Fail("Expected Error for truncated JSON"))

// ═══════════════════════════════════════════════════════════════════════════
// loadConfig — schema validation errors
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadConfig returns Error with field name for schema violation`` () =
    withTempDir (fun dir ->
        // reasoning_effort must be "low" | "medium" | "high" | "max" | "none"
        let json = """{"reasoning_effort": "super-duper"}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error msg -> Assert.Contains("reasoning_effort", msg)
        | Result.Ok _      -> Assert.Fail("Expected Error for invalid reasoning_effort"))

[<Fact>]
let ``loadConfig returns Error with semicolon-joined message for multiple violations`` () =
    withTempDir (fun dir ->
        // Invalid ws.port (0 is out of range) + invalid reasoning_effort
        let json = """{"reasoning_effort": "bad", "ws": {"enabled": true, "port": 99999}}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error msg ->
            // Should mention reasoning_effort; multiple errors joined by "; "
            Assert.Contains("reasoning_effort", msg)
        | Result.Ok _ -> Assert.Fail("Expected Error for multiple schema violations"))

// ═══════════════════════════════════════════════════════════════════════════
// loadConfig — round-trip: write via ConfigWriter, read back via loadConfig
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadConfig round-trips DefaultModel via ConfigWriter`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "round-trip.json")
        let original = { BotSharpConfig.defaults with DefaultModel = "claude-opus-4-6" }
        // Write via ConfigWriter
        let writeResult =
            BotSharp.Infrastructure.Config.ConfigWriter.saveConfig path original
            |> Async.RunSynchronously
        match writeResult with
        | Result.Error e -> Assert.Fail($"ConfigWriter failed: {e}")
        | Result.Ok () ->
        // Read back via loadConfig
        let readResult = loadConfig path |> Async.RunSynchronously
        match readResult with
        | Result.Error e -> Assert.Fail($"loadConfig failed: {e}")
        | Result.Ok cfg  -> Assert.Equal("claude-opus-4-6", cfg.DefaultModel))

[<Fact>]
let ``loadConfig round-trips Temperature via ConfigWriter`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "round-trip.json")
        let original = { BotSharpConfig.defaults with Temperature = 1.23 }
        let writeResult =
            BotSharp.Infrastructure.Config.ConfigWriter.saveConfig path original
            |> Async.RunSynchronously
        match writeResult with
        | Result.Error e -> Assert.Fail($"ConfigWriter failed: {e}")
        | Result.Ok () ->
        let readResult = loadConfig path |> Async.RunSynchronously
        match readResult with
        | Result.Error e -> Assert.Fail($"loadConfig failed after ConfigWriter: {e}")
        | Result.Ok cfg  -> Assert.InRange(cfg.Temperature, 1.229, 1.231))

[<Fact>]
let ``loadConfig reads allow_from with specific users as AllowedSet`` () =
    withTempDir (fun dir ->
        let json = """{"allow_from": ["alice", "bob"]}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok cfg ->
            match cfg.AllowFrom with
            | AllowedSet uids ->
                Assert.Equal(2, uids.Count)
                Assert.True(uids.Contains("alice"), "Expected 'alice' in AllowedSet")
                Assert.True(uids.Contains("bob"),   "Expected 'bob' in AllowedSet")
            | AnyoneAllowed -> Assert.Fail("Expected AllowedSet, got AnyoneAllowed"))

[<Fact>]
let ``loadConfig reads max_iterations from file`` () =
    withTempDir (fun dir ->
        let json = """{"max_iterations": 5}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(5, cfg.MaxIterations)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads base_urls from file`` () =
    withTempDir (fun dir ->
        let json = """{"base_urls": {"openai": "https://api.example.com/v1"}}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok cfg ->
            Assert.True(cfg.BaseUrls.ContainsKey("openai"), "Expected 'openai' key in BaseUrls")
            Assert.Equal("https://api.example.com/v1", cfg.BaseUrls.["openai"]))

[<Fact>]
let ``loadConfig reads max_tool_result_chars from file`` () =
    withTempDir (fun dir ->
        let json = """{"max_tool_result_chars": 2000}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(2000, cfg.MaxToolResultChars)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads context_window_tokens from file`` () =
    withTempDir (fun dir ->
        let json = """{"context_window_tokens": 128000}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Ok cfg -> Assert.Equal(128000, cfg.ContextWindowTokens)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``loadConfig reads brave_api_key from file`` () =
    withTempDir (fun dir ->
        let json = """{"brave_api_key": "sk-brave-test"}"""
        let path = writeJson dir "cfg.json" json
        let result = loadConfig path |> Async.RunSynchronously
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok cfg ->
            match cfg.BraveApiKey with
            | Some key -> Assert.Equal("sk-brave-test", ApiKey.value key)
            | None     -> Assert.Fail("Expected BraveApiKey to be set"))
