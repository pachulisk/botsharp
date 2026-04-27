module BotSharp.Tests.Infrastructure.MyToolTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.MyTool

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private makeArgs (pairs: (string * string) list) : Map<string, JsonElement> =
    pairs |> List.map (fun (k, v) -> k, jsonStr v) |> Map.ofList

/// Create a fresh temp workspace dir and remove it after the test.
let private withTempWorkspace (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"my-tool-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

let private testConfig (wp: string) = {
    BotSharpConfig.defaults with
        DefaultModel     = "test-model"
        DefaultProvider  = "test-provider"
        Temperature      = 0.5
        MaxTokens        = 2048
        WorkspacePath    = wp
        MaxIterations    = 10
        MaxToolResultChars = 5000
        ContextWindowTokens = 128000
        MemoryWindowSize = 15
        ReasoningEffort  = Some Medium
}

/// Like testConfig but with MyToolAllowSet = true (for tests that exercise the 'set' action).
let private testConfigWithSet (wp: string) = { testConfig wp with MyToolAllowSet = true }

let private run (cfg: BotSharpConfig) args =
    executeMyTool cfg (fun () -> None) (fun () -> 0) args |> Async.RunSynchronously

let private runWithUsage (cfg: BotSharpConfig) (usage: TokenUsage option) args =
    executeMyTool cfg (fun () -> usage) (fun () -> 0) args |> Async.RunSynchronously

let private runWithIter (cfg: BotSharpConfig) (iter: int) args =
    executeMyTool cfg (fun () -> None) (fun () -> iter) args |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// check (no key) — full overview
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check with no key returns full config overview`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfig wp
        let args = makeArgs ["action", "check"]
        match run cfg args with
        | ToolSuccess text ->
            Assert.Contains("test-model", text)
            Assert.Contains("test-provider", text)
            Assert.Contains("0.5", text)
            Assert.Contains("2048", text)
            Assert.Contains(wp, text)
            Assert.Contains("10", text)      // max_iterations
            Assert.Contains("5000", text)    // max_tool_result_chars
            Assert.Contains("128000", text)  // context_window_tokens
            Assert.Contains("15", text)      // memory_window_size
            Assert.Contains("medium", text)  // reasoning_effort
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// check (key) — individual config fields
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check model key returns model value`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "model"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("test-model", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check provider key returns provider value`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "provider"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("test-provider", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check workspace key returns workspace path`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "workspace"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains(wp, text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check temperature key returns temperature value`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "temperature"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("0.5", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check max_tokens key returns token limit`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "max_tokens"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("2048", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check max_iterations key returns iteration limit`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "max_iterations"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("10", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check max_tool_result_chars key returns char limit`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "max_tool_result_chars"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("5000", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check memory_window_size key returns window size`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "memory_window_size"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("15", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check context_window_tokens returns value when set`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "context_window_tokens"]
        match run (testConfig wp) args with
        | ToolSuccess text ->
            Assert.Contains("128000", text)
            Assert.DoesNotContain("disabled", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check context_window_tokens shows disabled when 0`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ContextWindowTokens = 0 }
        let args = makeArgs ["action", "check"; "key", "context_window_tokens"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("disabled", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check reasoning_effort returns configured level`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "reasoning_effort"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("medium", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check reasoning_effort shows not-set when None`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ReasoningEffort = None }
        let args = makeArgs ["action", "check"; "key", "reasoning_effort"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("not set", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check unknown key returns helpful error message`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "nonexistent_field"]
        match run (testConfig wp) args with
        | ToolSuccess text ->
            Assert.Contains("Unknown key", text)
            Assert.Contains("nonexistent_field", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// set action — scratchpad write
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``set stores a value in the scratchpad`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        let setArgs = makeArgs ["action", "set"; "key", "todo"; "value", "finish the report"]
        match run cfg setArgs with
        | ToolSuccess text -> Assert.Contains("todo", text)
        | other -> Assert.Fail($"Expected ToolSuccess from set, got {other}")
        // Now verify with check
        let checkArgs = makeArgs ["action", "check"; "key", "scratchpad.todo"]
        match run cfg checkArgs with
        | ToolSuccess text -> Assert.Contains("finish the report", text)
        | other -> Assert.Fail($"Expected ToolSuccess from check, got {other}")
    )

[<Fact>]
let ``set with scratchpad. prefix stores correctly`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        let setArgs = makeArgs ["action", "set"; "key", "scratchpad.note"; "value", "remember this"]
        match run cfg setArgs with
        | ToolSuccess _ -> ()
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        let checkArgs = makeArgs ["action", "check"; "key", "scratchpad.note"]
        match run cfg checkArgs with
        | ToolSuccess text -> Assert.Contains("remember this", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``set with empty value removes the scratchpad entry`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        // First set it
        run cfg (makeArgs ["action", "set"; "key", "temp"; "value", "hello"]) |> ignore
        // Then remove it
        match run cfg (makeArgs ["action", "set"; "key", "temp"; "value", ""]) with
        | ToolSuccess text -> Assert.Contains("Removed", text)
        | other -> Assert.Fail($"Expected ToolSuccess from remove, got {other}")
        // Verify gone
        match run cfg (makeArgs ["action", "check"; "key", "scratchpad.temp"]) with
        | ToolSuccess text -> Assert.Contains("not set", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check scratchpad shows all entries`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        run cfg (makeArgs ["action", "set"; "key", "a"; "value", "alpha"]) |> ignore
        run cfg (makeArgs ["action", "set"; "key", "b"; "value", "beta"])  |> ignore
        let checkArgs = makeArgs ["action", "check"; "key", "scratchpad"]
        match run cfg checkArgs with
        | ToolSuccess text ->
            Assert.Contains("alpha", text)
            Assert.Contains("beta", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check overview includes non-empty scratchpad`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        run cfg (makeArgs ["action", "set"; "key", "reminder"; "value", "call Bob"]) |> ignore
        let checkArgs = makeArgs ["action", "check"]
        match run cfg checkArgs with
        | ToolSuccess text -> Assert.Contains("call Bob", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``set without key returns helpful message`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "set"; "value", "oops"]
        match run (testConfigWithSet wp) args with
        | ToolSuccess text -> Assert.Contains("key", text.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// Spec registration
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allTools returns one tool with name 'my'`` () =
    withTempWorkspace (fun wp ->
        let tools = allTools (testConfig wp) (fun () -> None) (fun () -> 0)
        Assert.Equal(1, tools.Length)
        let (spec, _) = tools[0]
        Assert.Equal("my", spec.Name |> (fun (ToolName n) -> n))
    )

// ═══════════════════════════════════════════════════════════════════════════
// _last_usage key
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check _last_usage returns not-yet message when no LLM call made`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "_last_usage"]
        match run (testConfig wp) args with
        | ToolSuccess text -> Assert.Contains("no LLM call yet", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check _last_usage returns token counts when usage is available`` () =
    withTempWorkspace (fun wp ->
        let usage = { PromptTokens = 1234; CompletionTokens = 567; CachedTokens = 89 }
        let args = makeArgs ["action", "check"; "key", "_last_usage"]
        match runWithUsage (testConfig wp) (Some usage) args with
        | ToolSuccess text ->
            Assert.Contains("1234", text)
            Assert.Contains("567", text)
            Assert.Contains("89", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check overview includes _last_usage line`` () =
    withTempWorkspace (fun wp ->
        let usage = { PromptTokens = 100; CompletionTokens = 50; CachedTokens = 0 }
        let args = makeArgs ["action", "check"]
        match runWithUsage (testConfig wp) (Some usage) args with
        | ToolSuccess text ->
            Assert.Contains("_last_usage", text)
            Assert.Contains("100", text)
            Assert.Contains("50", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// _current_iteration key
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check _current_iteration returns iteration index`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "_current_iteration"]
        match runWithIter (testConfig wp) 3 args with
        | ToolSuccess text ->
            Assert.Contains("_current_iteration", text)
            Assert.Contains("3", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check overview includes _current_iteration line`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"]
        match runWithIter (testConfig wp) 7 args with
        | ToolSuccess text ->
            Assert.Contains("_current_iteration", text)
            Assert.Contains("7", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``set with empty key after stripping scratchpad prefix returns helpful message`` () =
    withTempWorkspace (fun wp ->
        // "scratchpad." stripped leaves empty key → should return error message
        let args = makeArgs ["action", "set"; "key", "scratchpad."; "value", "something"]
        match run (testConfigWithSet wp) args with
        | ToolSuccess text -> Assert.Contains("empty", text.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")
    )

[<Fact>]
let ``check scratchpad key prefix returns not-set for absent entry`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "scratchpad.nonexistent"]
        match run (testConfig wp) args with
        | ToolSuccess text ->
            Assert.Contains("not set", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check scratchpad shows empty when no entries`` () =
    withTempWorkspace (fun wp ->
        let args = makeArgs ["action", "check"; "key", "scratchpad"]
        match run (testConfig wp) args with
        | ToolSuccess text ->
            Assert.Contains("empty", text.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``set missing action arg returns ToolFailure`` () =
    withTempWorkspace (fun wp ->
        // No "action" key — requireStringArg "action" returns Error
        let args = makeArgs ["key", "foo"; "value", "bar"]
        match run (testConfig wp) args with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing action, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// reasoning_effort — Low, High, Adaptive variants
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check reasoning_effort low returns 'low'`` () =
    withTempWorkspace (fun wp ->
        let cfg  = { testConfig wp with ReasoningEffort = Some Low }
        let args = makeArgs ["action", "check"; "key", "reasoning_effort"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("low", text)
        | other -> Assert.Fail($"Expected ToolSuccess with 'low', got {other}")
    )

[<Fact>]
let ``check reasoning_effort high returns 'high'`` () =
    withTempWorkspace (fun wp ->
        let cfg  = { testConfig wp with ReasoningEffort = Some High }
        let args = makeArgs ["action", "check"; "key", "reasoning_effort"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("high", text)
        | other -> Assert.Fail($"Expected ToolSuccess with 'high', got {other}")
    )

[<Fact>]
let ``check reasoning_effort adaptive returns 'adaptive'`` () =
    withTempWorkspace (fun wp ->
        let cfg  = { testConfig wp with ReasoningEffort = Some Adaptive }
        let args = makeArgs ["action", "check"; "key", "reasoning_effort"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("adaptive", text)
        | other -> Assert.Fail($"Expected ToolSuccess with 'adaptive', got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// check — plain key fallback (no 'scratchpad.' prefix)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check plain key without scratchpad prefix finds stored entry`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        // Store via set (no prefix — goes to scratchpad)
        run cfg (makeArgs ["action", "set"; "key", "memo"; "value", "buy milk"]) |> ignore
        // Check via plain key (not "scratchpad.memo") — hits the plain-key fallback branch
        let args = makeArgs ["action", "check"; "key", "memo"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("buy milk", text)
        | other -> Assert.Fail($"Expected ToolSuccess with 'buy milk', got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// loadScratchpad — non-string JSON value filter
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check scratchpad skips non-string JSON values in scratchpad file`` () =
    // loadScratchpad: p.Value.ValueKind <> JsonValueKind.String → else None (skipped)
    withTempWorkspace (fun wp ->
        let cfg = testConfig wp
        // Write a scratchpad file that contains mixed value types
        let padPath = Path.Combine(wp, "scratchpad.json")
        File.WriteAllText(padPath, """{"good":"hello","bad":42,"also_bad":true}""")
        let args = makeArgs ["action", "check"; "key", "scratchpad"]
        match run cfg args with
        | ToolSuccess text ->
            // Only "good" (string) should appear
            Assert.Contains("hello", text)
            // Numeric/boolean values are filtered out; "bad" key won't be in output
            Assert.DoesNotContain("42", text)
            Assert.DoesNotContain("also_bad", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// checkAll — ContextWindowTokens = 0 shows "disabled" in full overview
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check full overview shows context_window_tokens disabled when zero`` () =
    // checkAll: cfg.ContextWindowTokens <= 0 → "0 (disabled)" in the full overview
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ContextWindowTokens = 0 }
        let args = makeArgs ["action", "check"]
        match run cfg args with
        | ToolSuccess text -> Assert.Contains("disabled", text)
        | other -> Assert.Fail($"Expected ToolSuccess with 'disabled', got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// checkAll — new config fields (fail_on_tool_error, disabled_skills,
//            session_ttl_minutes, timezone)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``check full overview includes fail_on_tool_error`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with FailOnToolError = true }
        match run cfg (makeArgs ["action", "check"]) with
        | ToolSuccess text -> Assert.Contains("fail_on_tool_error: True", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check fail_on_tool_error key directly`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with FailOnToolError = false }
        match run cfg (makeArgs ["action", "check"; "key", "fail_on_tool_error"]) with
        | ToolSuccess text -> Assert.Contains("fail_on_tool_error", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check full overview shows disabled_skills when non-empty`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DisabledSkills = ["summarize"; "weather"] }
        match run cfg (makeArgs ["action", "check"]) with
        | ToolSuccess text ->
            Assert.Contains("disabled_skills", text)
            Assert.Contains("summarize", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check full overview shows disabled_skills as none when empty`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DisabledSkills = [] }
        match run cfg (makeArgs ["action", "check"]) with
        | ToolSuccess text ->
            Assert.Contains("disabled_skills: (none)", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check session_ttl_minutes key directly shows disabled when zero`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with SessionTtlMinutes = 0 }
        match run cfg (makeArgs ["action", "check"; "key", "session_ttl_minutes"]) with
        | ToolSuccess text -> Assert.Contains("disabled", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check session_ttl_minutes key directly shows value when nonzero`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with SessionTtlMinutes = 90 }
        match run cfg (makeArgs ["action", "check"; "key", "session_ttl_minutes"]) with
        | ToolSuccess text -> Assert.Contains("90", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check timezone key shows system local when None`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with Timezone = None }
        match run cfg (makeArgs ["action", "check"; "key", "timezone"]) with
        | ToolSuccess text -> Assert.Contains("system local", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check timezone key shows IANA name when configured`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with Timezone = Some "Asia/Tokyo" }
        match run cfg (makeArgs ["action", "check"; "key", "timezone"]) with
        | ToolSuccess text -> Assert.Contains("Asia/Tokyo", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── heartbeat config fields ───────────────────────────────────────────────────

[<Fact>]
let ``check full overview includes heartbeat_enabled`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with HeartbeatEnabled = false }
        match run cfg (makeArgs ["action", "check"]) with
        | ToolSuccess text -> Assert.Contains("heartbeat_enabled: False", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check heartbeat_enabled key directly`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with HeartbeatEnabled = false }
        match run cfg (makeArgs ["action", "check"; "key", "heartbeat_enabled"]) with
        | ToolSuccess text -> Assert.Contains("heartbeat_enabled", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check heartbeat_interval_seconds key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with HeartbeatIntervalSeconds = 600 }
        match run cfg (makeArgs ["action", "check"; "key", "heartbeat_interval_seconds"]) with
        | ToolSuccess text -> Assert.Contains("600", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check heartbeat_keep_recent_messages key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with HeartbeatKeepRecentMessages = 16 }
        match run cfg (makeArgs ["action", "check"; "key", "heartbeat_keep_recent_messages"]) with
        | ToolSuccess text -> Assert.Contains("16", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── exec_timeout_seconds config field ────────────────────────────────────────

[<Fact>]
let ``check exec_timeout_seconds shows tool default label when 0`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ExecTimeoutSeconds = 0 }
        match run cfg (makeArgs ["action", "check"; "key", "exec_timeout_seconds"]) with
        | ToolSuccess text -> Assert.Contains("tool default", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check exec_timeout_seconds shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ExecTimeoutSeconds = 120 }
        match run cfg (makeArgs ["action", "check"; "key", "exec_timeout_seconds"]) with
        | ToolSuccess text -> Assert.Contains("120", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── web_search_provider, dream config fields ───────────────────────────────────

[<Fact>]
let ``check all shows web_search_provider as auto when None`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebSearchProvider = None }
        match run cfg (makeArgs ["action", "check"]) with
        | ToolSuccess text -> Assert.Contains("web_search_provider", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_search_provider key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebSearchProvider = Some "tavily" }
        match run cfg (makeArgs ["action", "check"; "key", "web_search_provider"]) with
        | ToolSuccess text -> Assert.Contains("tavily", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check dream_model_override key shows none when unset`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DreamModelOverride = None }
        match run cfg (makeArgs ["action", "check"; "key", "dream_model_override"]) with
        | ToolSuccess text -> Assert.Contains("dream_model_override", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check dream_model_override key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DreamModelOverride = Some "claude-haiku-4-5" }
        match run cfg (makeArgs ["action", "check"; "key", "dream_model_override"]) with
        | ToolSuccess text -> Assert.Contains("claude-haiku-4-5", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check dream_interval_hours key shows 0 (disabled) for default`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DreamIntervalHours = 0 }
        match run cfg (makeArgs ["action", "check"; "key", "dream_interval_hours"]) with
        | ToolSuccess text -> Assert.Contains("disabled", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check dream_interval_hours key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DreamIntervalHours = 8 }
        match run cfg (makeArgs ["action", "check"; "key", "dream_interval_hours"]) with
        | ToolSuccess text -> Assert.Contains("8", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_proxy_url key shows none for default`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebProxyUrl = None }
        match run cfg (makeArgs ["action", "check"; "key", "web_proxy_url"]) with
        | ToolSuccess text -> Assert.Contains("none", text.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_proxy_url key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebProxyUrl = Some "http://proxy.local:8080" }
        match run cfg (makeArgs ["action", "check"; "key", "web_proxy_url"]) with
        | ToolSuccess text -> Assert.Contains("proxy.local", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_search_timeout key shows default value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebSearchTimeout = 30 }
        match run cfg (makeArgs ["action", "check"; "key", "web_search_timeout"]) with
        | ToolSuccess text -> Assert.Contains("30", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_search_timeout key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebSearchTimeout = 90 }
        match run cfg (makeArgs ["action", "check"; "key", "web_search_timeout"]) with
        | ToolSuccess text -> Assert.Contains("90", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_search_max_results key shows default value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebSearchMaxResults = 5 }
        match run cfg (makeArgs ["action", "check"; "key", "web_search_max_results"]) with
        | ToolSuccess text -> Assert.Contains("5", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check web_search_max_results key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with WebSearchMaxResults = 10 }
        match run cfg (makeArgs ["action", "check"; "key", "web_search_max_results"]) with
        | ToolSuccess text -> Assert.Contains("10", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check dream_max_batch_size key shows default value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with DreamMaxBatchSize = 20 }
        match run cfg (makeArgs ["action", "check"; "key", "dream_max_batch_size"]) with
        | ToolSuccess text -> Assert.Contains("20", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check exec_path_append key shows none for default`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ExecPathAppend = "" }
        match run cfg (makeArgs ["action", "check"; "key", "exec_path_append"]) with
        | ToolSuccess text -> Assert.Contains("none", text.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check exec_path_append key shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ExecPathAppend = "/opt/mytools" }
        match run cfg (makeArgs ["action", "check"; "key", "exec_path_append"]) with
        | ToolSuccess text -> Assert.Contains("/opt/mytools", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ═══════════════════════════════════════════════════════════════════════════
// my_tool_allow_set — gate on scratchpad write
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``set returns disabled message when my_tool_allow_set is false`` () =
    withTempWorkspace (fun wp ->
        // Default config has MyToolAllowSet = false
        let cfg = testConfig wp
        let args = makeArgs ["action", "set"; "key", "memo"; "value", "hello"]
        match run cfg args with
        | ToolSuccess text ->
            Assert.Contains("disabled", text.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolSuccess with disabled message, got {other}")
    )

[<Fact>]
let ``set works when my_tool_allow_set is true`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp  // MyToolAllowSet = true
        let setArgs  = makeArgs ["action", "set"; "key", "note"; "value", "hello"]
        let checkArgs = makeArgs ["action", "check"; "key", "scratchpad.note"]
        run cfg setArgs |> ignore
        match run cfg checkArgs with
        | ToolSuccess text -> Assert.Contains("hello", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check my_tool_allow_set key shows false by default`` () =
    withTempWorkspace (fun wp ->
        match run (testConfig wp) (makeArgs ["action", "check"; "key", "my_tool_allow_set"]) with
        | ToolSuccess text -> Assert.Contains("my_tool_allow_set: False", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check my_tool_allow_set key shows true when enabled`` () =
    withTempWorkspace (fun wp ->
        let cfg = testConfigWithSet wp
        match run cfg (makeArgs ["action", "check"; "key", "my_tool_allow_set"]) with
        | ToolSuccess text -> Assert.Contains("my_tool_allow_set: True", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── ssrf_whitelist inspector ─────────────────────────────────────────────────

[<Fact>]
let ``check ssrf_whitelist shows (none) when empty`` () =
    withTempWorkspace (fun wp ->
        match run (testConfig wp) (makeArgs ["action", "check"; "key", "ssrf_whitelist"]) with
        | ToolSuccess text -> Assert.Contains("(none)", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check ssrf_whitelist shows configured CIDRs`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with SsrfWhitelist = ["10.0.0.0/8"; "192.168.1.0/24"] }
        match run cfg (makeArgs ["action", "check"; "key", "ssrf_whitelist"]) with
        | ToolSuccess text ->
            Assert.Contains("10.0.0.0/8", text)
            Assert.Contains("192.168.1.0/24", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── context_block_limit ───────────────────────────────────────────────────────

[<Fact>]
let ``check context_block_limit shows auto when None`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ContextBlockLimit = None }
        match run cfg (makeArgs ["action", "check"; "key", "context_block_limit"]) with
        | ToolSuccess text -> Assert.Contains("(auto", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check context_block_limit shows value when Some`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ContextBlockLimit = Some 100 }
        match run cfg (makeArgs ["action", "check"; "key", "context_block_limit"]) with
        | ToolSuccess text -> Assert.Contains("100", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── max_iterations_message ────────────────────────────────────────────────────

[<Fact>]
let ``check max_iterations_message shows default when None`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with MaxIterationsMessage = None }
        match run cfg (makeArgs ["action", "check"; "key", "max_iterations_message"]) with
        | ToolSuccess text -> Assert.Contains("(default", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check max_iterations_message shows set when Some`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with MaxIterationsMessage = Some "I have reached my limit." }
        match run cfg (makeArgs ["action", "check"; "key", "max_iterations_message"]) with
        | ToolSuccess text -> Assert.Contains("I have reached my limit", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── exec_allowed_env_keys ─────────────────────────────────────────────────────

[<Fact>]
let ``check exec_allowed_env_keys shows no restriction when empty`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ExecAllowedEnvKeys = [] }
        match run cfg (makeArgs ["action", "check"; "key", "exec_allowed_env_keys"]) with
        | ToolSuccess text -> Assert.Contains("no restriction", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

[<Fact>]
let ``check exec_allowed_env_keys shows configured keys`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ExecAllowedEnvKeys = ["GOPATH"; "JAVA_HOME"] }
        match run cfg (makeArgs ["action", "check"; "key", "exec_allowed_env_keys"]) with
        | ToolSuccess text ->
            Assert.Contains("GOPATH", text)
            Assert.Contains("JAVA_HOME", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )

// ── api_host ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``check api_host shows configured value`` () =
    withTempWorkspace (fun wp ->
        let cfg = { testConfig wp with ApiHost = "0.0.0.0" }
        match run cfg (makeArgs ["action", "check"; "key", "api_host"]) with
        | ToolSuccess text -> Assert.Contains("0.0.0.0", text)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    )
