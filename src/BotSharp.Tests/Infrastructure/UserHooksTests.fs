module BotSharp.Tests.Infrastructure.UserHooksTests

open System
open System.IO
open Xunit
open BotSharp.Infrastructure.Hooks.UserHooks

// ═══════════════════════════════════════════════════════════════════════════
// UserHooks unit tests
//
// Tests for matchesToolName (pure) and loadHooksConfig (file I/O).
// buildUserHook behaviour is covered indirectly by the AgentLoop integration
// tests; here we focus on the parsing layer.
// ═══════════════════════════════════════════════════════════════════════════

/// Write a hooks.json to a temp directory and call loadHooksConfig.
let private withHooks (json: string) (test: HooksConfig -> unit) =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        File.WriteAllText(Path.Combine(tmp, "hooks.json"), json)
        test (loadHooksConfig tmp)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

// ── matchesToolName ──────────────────────────────────────────────────────

[<Fact>]
let ``matchesToolName "*" matches any tool name`` () =
    Assert.True(matchesToolName "*" "shell")
    Assert.True(matchesToolName "*" "write_file")
    Assert.True(matchesToolName "*" "")

[<Fact>]
let ``matchesToolName exact match is case-insensitive`` () =
    Assert.True(matchesToolName "shell" "shell")
    Assert.True(matchesToolName "Shell" "shell")
    Assert.True(matchesToolName "SHELL" "SHELL")
    Assert.False(matchesToolName "shell" "write_file")

[<Fact>]
let ``matchesToolName prefix glob matches tools starting with prefix`` () =
    Assert.True(matchesToolName  "write_*" "write_file")
    Assert.True(matchesToolName  "write_*" "write_notebook")
    Assert.False(matchesToolName "write_*" "read_file")
    Assert.False(matchesToolName "write_*" "filewrite")

[<Fact>]
let ``matchesToolName prefix glob is case-insensitive`` () =
    Assert.True(matchesToolName "Write_*" "write_file")
    Assert.True(matchesToolName "write_*" "Write_File")

[<Fact>]
let ``matchesToolName non-glob pattern does not match prefix`` () =
    // "shell" should NOT match "shell_exec" (exact match only)
    Assert.False(matchesToolName "shell" "shell_exec")

// ── loadHooksConfig ──────────────────────────────────────────────────────

[<Fact>]
let ``loadHooksConfig returns empty when hooks.json does not exist`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let cfg = loadHooksConfig tmp
        Assert.Empty(cfg.Hooks)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``loadHooksConfig parses a PreToolUse hook with match pattern`` () =
    let json =
        "{ \"hooks\": { \"PreToolUse\": [" +
        "{ \"match\": \"shell\", \"command\": \"echo pre-shell\" }" +
        "] } }"
    withHooks json (fun cfg ->
        Assert.Equal(1, cfg.Hooks.Length)
        let h = cfg.Hooks.[0]
        Assert.Equal("PreToolUse", h.Event)
        Assert.Equal(Some "shell", h.Match)
        Assert.Equal("echo pre-shell", h.Command))

[<Fact>]
let ``loadHooksConfig parses hooks across multiple event types`` () =
    let json =
        "{ \"hooks\": {" +
        "\"PreToolUse\":  [{ \"command\": \"echo pre\"  }]," +
        "\"PostToolUse\": [{ \"command\": \"echo post\" }]," +
        "\"Stop\":        [{ \"command\": \"echo stop\" }]" +
        "} }"
    withHooks json (fun cfg ->
        Assert.Equal(3, cfg.Hooks.Length)
        let events = cfg.Hooks |> List.map (fun h -> h.Event) |> List.sort
        Assert.Equal<string list>([ "PostToolUse"; "PreToolUse"; "Stop" ], events))

[<Fact>]
let ``loadHooksConfig hook without match field has None match`` () =
    let json = "{ \"hooks\": { \"PostToolUse\": [{ \"command\": \"echo done\" }] } }"
    withHooks json (fun cfg ->
        Assert.Equal(1, cfg.Hooks.Length)
        Assert.Equal(None, cfg.Hooks.[0].Match))

[<Fact>]
let ``loadHooksConfig skips hooks with empty command`` () =
    let json =
        "{ \"hooks\": { \"PreToolUse\": [" +
        "{ \"command\": \"\" }," +
        "{ \"command\": \"echo valid\" }" +
        "] } }"
    withHooks json (fun cfg ->
        Assert.Equal(1, cfg.Hooks.Length)
        Assert.Equal("echo valid", cfg.Hooks.[0].Command))

[<Fact>]
let ``loadHooksConfig returns empty on malformed JSON`` () =
    let json = "this is not json {{{"
    withHooks json (fun cfg -> Assert.Empty(cfg.Hooks))

[<Fact>]
let ``loadHooksConfig returns empty when hooks key is missing`` () =
    let json = "{ \"other\": \"value\" }"
    withHooks json (fun cfg -> Assert.Empty(cfg.Hooks))

[<Fact>]
let ``loadHooksConfig parses PreSendMessage hook`` () =
    let json =
        "{ \"hooks\": { \"PreSendMessage\": [" +
        "{ \"command\": \"cat /dev/stdin | jq .\" }" +
        "] } }"
    withHooks json (fun cfg ->
        Assert.Equal(1, cfg.Hooks.Length)
        Assert.Equal("PreSendMessage", cfg.Hooks.[0].Event))
