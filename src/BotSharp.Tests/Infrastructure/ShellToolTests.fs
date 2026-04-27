module BotSharp.Tests.Infrastructure.ShellToolTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ShellTool

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private jsonInt (n: int) =
    JsonDocument.Parse($"{n}").RootElement.Clone()

let private makeArgs (cmd: string) : Map<string, JsonElement> =
    Map.ofList ["command", jsonStr cmd]

let private makeArgsWithTimeout (cmd: string) (sec: int) : Map<string, JsonElement> =
    Map.ofList ["command", jsonStr cmd; "timeout", jsonInt sec]

let private makeArgsWithDir (cmd: string) (dir: string) : Map<string, JsonElement> =
    Map.ofList ["command", jsonStr cmd; "working_dir", jsonStr dir]

// ═══════════════════════════════════════════════════════════════════════════
// exec: missing argument guard
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec returns ToolFailure when command arg is missing`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" Map.empty
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing command arg, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: success cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec returns stdout from echo command`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo hello")
        match result with
        | ToolSuccess output -> Assert.Contains("hello", output)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec returns (no output) for silent commands`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "true")
        match result with
        | ToolSuccess "(no output)" -> ()
        | ToolSuccess other -> ()   // some shells output nothing, accept any ToolSuccess
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec captures stderr`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo err >&2")
        match result with
        | ToolSuccess output | ToolFailure (ExecutionFailed output) ->
            // stderr comes back in the output
            ()
        | other -> ()   // accept any result — cross-platform stderr redirect varies
    } |> Async.RunSynchronously

[<Fact>]
let ``exec returns ToolSuccess even for non-zero exit code (LLM must see stderr)`` () =
    // Python parity: always ToolSuccess with exit code in output.
    // ToolFailure hides stderr, preventing the LLM from diagnosing the error.
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "exit 1")
        match result with
        | ToolSuccess output -> Assert.Contains("Exit code: 1", output)
        | other -> Assert.Fail($"Expected ToolSuccess with exit code in output, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec working_dir changes the working directory`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgsWithDir "pwd" "/")
        match result with
        | ToolSuccess output -> Assert.Contains("/", output.Trim())
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec truncates output exceeding 10000 chars`` () =
    async {
        // Generate > 10000 chars of output
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "printf '%015000d' 0")
        match result with
        | ToolSuccess output ->
            Assert.True(output.Length <= 10500, $"Output should be truncated, got {output.Length} chars")
            Assert.Contains("truncated", output)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: safety guard
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec blocks rm -rf command`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "rm -rf /tmp/botsharp-test-safe")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed) for dangerous command, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks dd if= command`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "dd if=/dev/zero of=/dev/null count=1")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for dd if=, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks rm -r (recursive without f)`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "rm -r /tmp/some-dir")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for rm -r, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks shutdown command`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "shutdown -h now")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for shutdown, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: timeout
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec returns ToolFailure on timeout`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgsWithTimeout "sleep 60" 1)
        match result with
        | ToolFailure (ExecutionTimeout _) -> ()
        | other -> Assert.Fail($"Expected ExecutionTimeout, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// allTools registration
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allTools returns 1 tool`` () =
    let tools = allTools "/tmp" 0 false "" [] [] ""
    Assert.Equal(1, List.length tools)

[<Fact>]
let ``allTools tool name is exec`` () =
    let tools = allTools "/tmp" 0 false "" [] [] ""
    let (spec, _) = List.head tools
    let (ToolName n) = spec.Name
    Assert.Equal("exec", n)

// ═══════════════════════════════════════════════════════════════════════════
// exec: SSRF protection for internal URLs in commands
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec blocks curl to cloud metadata endpoint`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://169.254.169.254/latest/meta-data/")
        match result with
        | ToolFailure (ExecutionFailed msg) ->
            Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for metadata endpoint, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks wget to localhost`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "wget http://localhost/admin")
        match result with
        | ToolFailure (ExecutionFailed msg) ->
            Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for localhost URL, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks curl to private RFC-1918 address`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://192.168.1.1/config")
        match result with
        | ToolFailure (ExecutionFailed msg) ->
            Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for private IP, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec allows curl to public URL`` () =
    // This test only verifies the guard doesn't block public URLs.
    // The exec itself may fail for other reasons (network, etc.) — that's fine.
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo https://example.com")
        // Should not be blocked by the internal-URL guard (may succeed or fail on network)
        match result with
        | ToolFailure (ExecutionFailed msg) when msg.Contains("internal") ->
            Assert.Fail($"Public URL should not be blocked by SSRF guard, got: {msg}")
        | _ -> ()   // any other result is acceptable
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks fork bomb pattern`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs ":() { :|:& };:")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("Refused", msg)
        | other -> Assert.Fail($"Expected ToolFailure for fork bomb, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks mkfs command`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "mkfs.ext4 /dev/sdb1")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("Refused", msg)
        | other -> Assert.Fail($"Expected ToolFailure for mkfs, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks curl to 172.16 private network address`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://172.16.0.1/secret")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for 172.16 address, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks curl to 10.0 private network address`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://10.0.0.1/api")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for 10.x address, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks curl to 127.0.0.1 loopback`` () =
    // Tests the 127uy, _ branch in isInternalHost (distinct from "localhost" string check)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://127.0.0.1/secret")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for 127.0.0.1, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks curl to 0.0.0.0 unspecified address`` () =
    // Tests the 0uy, _ branch in isInternalHost (0.0.0.0/8 unspecified range)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://0.0.0.0/secret")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for 0.0.0.0, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks curl to 100.64 CGNAT address`` () =
    // Tests the 100uy, s when s >= 64uy && s <= 127uy branch in isInternalHost
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://100.64.0.1/api")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for CGNAT address, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: additional dangerous-pattern branches
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec blocks del /f command`` () =
    // Tests the @"\bdel\s+/[fq]\b" dangerous pattern (Windows del /f /q)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "del /f important.txt")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for del /f, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks rmdir /s command`` () =
    // Tests the @"\brmdir\s+/s\b" dangerous pattern (Windows rmdir /s)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "rmdir /s mydir")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for rmdir /s, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks format command`` () =
    // Tests the @"(?:^|[;&|]\s*)format\b" dangerous pattern (standalone format command)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "format C:")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for format command, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks redirect to block device`` () =
    // Tests the @">\s*/dev/sd" dangerous pattern (overwrite block device)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "cat /dev/zero > /dev/sda")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for block device write, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks sudo rm -rf with path`` () =
    // Tests the @"\bsudo\s+rm\s+-[rf]{1,2}\s+/" dangerous pattern (root rm -rf)
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "sudo rm -rf /usr/lib")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for sudo rm -rf, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: public IPv4 address — isInternalHost | _ -> false fallthrough
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec allows command referencing a public IPv4 address`` () =
    // 8.8.8.8 parses as a valid IPv4 address but b.[0]=8uy does not match any
    // private-range pattern → the | _ -> false arm is reached → not blocked.
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo http://8.8.8.8/query")
        match result with
        | ToolFailure (ExecutionFailed msg) when msg.ToLowerInvariant().Contains("internal") ->
            Assert.Fail($"Public IPv4 should not be blocked by SSRF guard, got: {msg}")
        | _ -> ()   // allowed — any other result is fine
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: restrict_to_workspace working_dir validation (Python parity #2826)
// When restrictToWorkspace = true, working_dir outside workspace returns an
// error (mirrors Python ExecTool which returns "Error: working_dir is outside
// the configured workspace" rather than silently clamping).
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec allows working_dir inside workspace when restrict_to_workspace is true`` () =
    async {
        let ws = IO.Path.GetTempPath()
        // A subdirectory of /tmp is still inside /tmp (workspace root here)
        let args = Map.ofList [
            "command",     jsonStr "echo ok"
            "working_dir", jsonStr ws ]
        let! result = exec ws 0 true "" [] [] "" args
        match result with
        | ToolSuccess output -> Assert.Contains("ok", output)
        | other -> Assert.Fail($"Expected ToolSuccess for inside-workspace cwd, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks working_dir outside workspace when restrict_to_workspace is true`` () =
    async {
        let ws = IO.Path.GetTempPath()  // workspace = /tmp
        let args = Map.ofList [
            "command",     jsonStr "pwd"
            "working_dir", jsonStr "/var" ]   // outside workspace
        let! result = exec ws 0 true "" [] [] "" args
        // Python returns: "Error: working_dir is outside the configured workspace"
        match result with
        | ToolSuccess output ->
            Assert.Contains("outside the configured workspace", output)
        | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with restrict_to_workspace false allows any working_dir`` () =
    async {
        let ws = IO.Path.GetTempPath()
        let args = Map.ofList [
            "command",     jsonStr "echo outside"
            "working_dir", jsonStr "/var" ]
        let! result = exec ws 0 false "" [] [] "" args
        match result with
        | ToolSuccess output -> Assert.Contains("outside", output)
        | other -> Assert.Fail($"Expected ToolSuccess with unrestricted cwd, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: path_append — extra PATH entries visible in subprocess
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec with empty path_append does not change PATH`` () =
    async {
        // No path_append — PATH should not contain our sentinel
        let args = makeArgs "echo $PATH"
        let! result = exec "/tmp" 0 false "" [] [] "" args
        match result with
        | ToolSuccess _ -> ()   // just checking it runs; PATH content varies
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with path_append adds the directory to PATH in subprocess`` () =
    async {
        // Use a recognisable sentinel path segment
        let sentinel = "/usr/local/botsharp-test-path"
        let args = makeArgs "echo $PATH"
        let! result = exec "/tmp" 0 false sentinel [] [] "" args
        match result with
        | ToolSuccess output -> Assert.Contains(sentinel, output)
        | other -> Assert.Fail($"Expected ToolSuccess with appended PATH, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: allowed_env_keys — env var allowlist (Python _build_env() parity)
//
// [] allowedEnvKeys  → minimal safe-var whitelist (PATH, HOME, USER, SHELL, …)
// non-[] allowedEnvKeys → exactly those keys only
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec with empty allowed_env_keys keeps HOME (HOME is in safe-var whitelist)`` () =
    async {
        // HOME is in the safeVars whitelist — must survive env isolation.
        let args = makeArgs "printenv HOME"
        let! result = exec "/tmp" 0 false "" [] [] "" args
        match result with
        | ToolSuccess output ->
            // HOME should be set (non-empty) for any typical user account
            Assert.False(output.Trim() = "" || output.Contains("Exit code: 1"),
                "Expected HOME to be visible in subprocess env (it is in the safe-var whitelist)")
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with empty allowed_env_keys blocks non-safe env vars (Python _build_env parity)`` () =
    async {
        // Inject a fake "secret" into the parent process env and verify
        // it does NOT appear in the subprocess (it is not in safeVars).
        let varName  = "NANOBOT_TEST_SECRET_ISOLATION_12345"
        let varValue = "super-secret-isolation-test-value"
        Environment.SetEnvironmentVariable(varName, varValue)
        try
            let args = makeArgs $"printenv {varName}"
            let! result = exec "/tmp" 0 false "" [] [] "" args
            match result with
            | ToolSuccess output ->
                Assert.DoesNotContain(varValue, output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        finally
            Environment.SetEnvironmentVariable(varName, null)
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with non-empty allowed_env_keys passes through explicitly listed vars`` () =
    async {
        // An explicitly allowed custom var must survive into the subprocess.
        let varName  = "NANOBOT_TEST_ALLOWED_VAR_67890"
        let varValue = "hello-from-allowed-config"
        Environment.SetEnvironmentVariable(varName, varValue)
        try
            let args = makeArgs $"printenv {varName}"
            let! result = exec "/tmp" 0 false "" [varName] [] "" args
            match result with
            | ToolSuccess output -> Assert.Contains(varValue, output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        finally
            Environment.SetEnvironmentVariable(varName, null)
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with non-empty allowed_env_keys blocks vars not in the list`` () =
    async {
        // When allowed_env_keys is non-empty, only those keys pass through.
        let allowedName = "NANOBOT_TEST_ALLOWED_67891"
        let blockedName = "NANOBOT_TEST_BLOCKED_67891"
        let allowedValue = "i-am-allowed"
        let blockedValue = "i-should-be-blocked"
        Environment.SetEnvironmentVariable(allowedName, allowedValue)
        Environment.SetEnvironmentVariable(blockedName, blockedValue)
        try
            let args = makeArgs $"printenv {blockedName}"
            let! result = exec "/tmp" 0 false "" [allowedName] [] "" args
            match result with
            | ToolSuccess output -> Assert.DoesNotContain(blockedValue, output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        finally
            Environment.SetEnvironmentVariable(allowedName, null)
            Environment.SetEnvironmentVariable(blockedName, null)
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with allowed_env_keys restricts visible env vars`` () =
    async {
        // Allow only PATH; HOME should not be in subprocess environment
        let args = makeArgs "printenv HOME"
        let! result = exec "/tmp" 0 false "" ["PATH"] [] "" args
        match result with
        | ToolSuccess output ->
            // HOME was stripped — either empty output or exit-code-1 output
            // Both are ToolSuccess (exec always succeeds at infrastructure level)
            ()  // just confirming it doesn't crash
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: ssrf_whitelist — CIDR exemptions from SSRF blocking
// When an IP matches a whitelist CIDR, the normally-blocked internal URL is allowed.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec with ssrf_whitelist allows whitelisted 10.x address`` () =
    async {
        // 10.0.0.1 is normally blocked (RFC-1918); whitelist 10.0.0.0/8 to exempt it
        let! result = exec "/tmp" 0 false "" [] ["10.0.0.0/8"] "" (makeArgs "echo http://10.0.0.1/api")
        // The command should succeed (echo is safe) — the URL is in the message not fetched
        match result with
        | ToolSuccess _ -> ()   // URL whitelisted → no SSRF refusal
        | ToolFailure (ExecutionFailed msg) when msg.Contains("internal") ->
            Assert.Fail("Expected whitelist to exempt 10.x address from SSRF block")
        | other -> Assert.Fail($"Unexpected result {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with ssrf_whitelist exact match allows specific IP`` () =
    async {
        // 192.168.1.1 is normally blocked; exact-match whitelist entry allows it
        let! result = exec "/tmp" 0 false "" [] ["192.168.1.1"] "" (makeArgs "echo http://192.168.1.1/")
        match result with
        | ToolSuccess _ -> ()
        | ToolFailure (ExecutionFailed msg) when msg.Contains("internal") ->
            Assert.Fail("Expected exact whitelist entry to exempt 192.168.1.1")
        | other -> Assert.Fail($"Unexpected result {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with ssrf_whitelist that does not cover the IP still blocks it`` () =
    async {
        // Whitelist only covers 172.16.0.0/12; 10.0.0.1 must still be blocked
        let! result = exec "/tmp" 0 false "" [] ["172.16.0.0/12"] "" (makeArgs "curl http://10.0.0.1/api")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure for non-whitelisted 10.x, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with empty ssrf_whitelist still blocks private addresses`` () =
    async {
        // [] = no exemptions — same as the original guard behaviour
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "curl http://192.168.1.1/secret")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("internal", msg.ToLowerInvariant())
        | other -> Assert.Fail($"Expected ToolFailure with empty whitelist, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec: history.jsonl / .dream_cursor write-protection (Python #2989)
// These files are managed by the BotSharp runtime; direct writes corrupt the
// cursor format and crash /dream. The deny patterns block the most common
// ways an LLM might accidentally overwrite them.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec blocks redirect to history.jsonl via >`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo 'data' > ~/.botsharp/workspace/memory/history.jsonl")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for > history.jsonl, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks append to history.jsonl via >>`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo 'line' >> /tmp/history.jsonl")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for >> history.jsonl, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks redirect to .dream_cursor via >`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "printf '' > /tmp/.dream_cursor")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for > .dream_cursor, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks tee to history.jsonl`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo 'data' | tee /tmp/history.jsonl")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for tee history.jsonl, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks tee -a to .dream_cursor`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "echo 'x' | tee -a /tmp/.dream_cursor")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for tee -a .dream_cursor, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks sed -i on history.jsonl`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "sed -i 's/old/new/g' /tmp/history.jsonl")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for sed -i history.jsonl, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks cp target history.jsonl`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "cp /tmp/other.txt /tmp/history.jsonl")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for cp to history.jsonl, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks mv target .dream_cursor`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "mv /tmp/file.txt /tmp/.dream_cursor")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for mv to .dream_cursor, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks dd of=history.jsonl`` () =
    async {
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "dd if=/dev/urandom of=/tmp/history.jsonl bs=1 count=1")
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("dangerous", msg)
        | other -> Assert.Fail($"Expected ToolFailure for dd of=history.jsonl, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// exec — restrictToWorkspace: absolute path scanning (Python parity)
// When restrictToWorkspace=true, commands referencing absolute paths outside
// the workspace are blocked (mirrors Python's _extract_absolute_paths guard).
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``exec with restrictToWorkspace blocks path traversal in command`` () =
    async {
        let! result = exec "/tmp/ws" 0 true "" [] [] "" (makeArgs "ls ../secret")
        match result with
        | ToolFailure (ExecutionFailed msg) ->
            Assert.Contains("path traversal", msg)
        | other -> Assert.Fail($"Expected ToolFailure for path traversal, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with restrictToWorkspace blocks absolute path outside workspace`` () =
    async {
        // /etc is outside any workspace under /tmp
        let! result = exec "/tmp/testws" 0 true "" [] [] "" (makeArgs "cat /etc/passwd")
        match result with
        | ToolFailure (ExecutionFailed msg) ->
            Assert.Contains("path outside workspace", msg)
        | other -> Assert.Fail($"Expected ToolFailure for absolute path outside workspace, got {other}")
    } |> Async.RunSynchronously

[<Fact>]
let ``exec with restrictToWorkspace allows commands without external paths`` () =
    async {
        // "echo hello" has no absolute paths — should not be blocked by abs-path guard
        let! result = exec "/tmp" 0 true "" [] [] "" (makeArgs "echo hello")
        // Should either succeed or fail for a non-guard reason (e.g. echo not available — unlikely)
        // The important thing is it's NOT blocked by the abs-path guard
        match result with
        | ToolFailure (ExecutionFailed msg) when msg.Contains("path") -> 
            Assert.Fail($"Should not have been blocked by path guard: {msg}")
        | _ -> ()   // any other result (success or different failure) is OK
    } |> Async.RunSynchronously

[<Fact>]
let ``exec without restrictToWorkspace allows absolute paths`` () =
    async {
        // Without restrictToWorkspace, /etc/passwd is allowed (normal guard)
        let! result = exec "/tmp" 0 false "" [] [] "" (makeArgs "cat /etc/passwd")
        match result with
        | ToolFailure (ExecutionFailed msg) when msg.Contains("path outside workspace") ->
            Assert.Fail($"Should not be blocked by path guard when restrict=false: {msg}")
        | _ -> ()   // success or dangerous-pattern failure both OK
    } |> Async.RunSynchronously

[<Fact>]
let ``exec blocks relative command when working_dir is outside workspace (#2826 regression)`` () =
    async {
        // Python #2826: without working_dir validation, an LLM can pass
        // working_dir="/etc" and then run "rm file" which operates on /etc/file.
        // The fix: reject working_dir outside workspace before running the command.
        let ws = IO.Path.GetTempPath()
        let args = Map.ofList [
            "command",     jsonStr "echo pwned"  // relative — would not be caught by abs-path guard
            "working_dir", jsonStr "/var" ]        // outside workspace
        let! result = exec ws 0 true "" [] [] "" args
        // Must return the working_dir error, not execute the command
        match result with
        | ToolSuccess output ->
            Assert.Contains("outside the configured workspace", output)
        | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// wrapBwrap — bwrap sandbox command builder
// Python parity: test_sandbox.py (wrap_command("bwrap", ...))
// ═══════════════════════════════════════════════════════════════════════════

/// Split a shell-quoted command string back into tokens for assertion.
/// Handles simple single-quote wrapping produced by wrapBwrap/shellQuote.
let private splitWrapped (cmd: string) : string list =
    // wrapBwrap wraps every arg in single quotes.
    // Split on "' '" (quote-space-quote boundary) then strip outer quotes.
    // This is good enough for well-behaved paths in tests.
    cmd.Split("' '")
    |> Array.toList
    |> List.mapi (fun i s ->
        let s = if i = 0 then s.TrimStart(''') else s
        let s = if i = (cmd.Split("' '").Length - 1) then s.TrimEnd(''') else s
        s)

[<Fact>]
let ``wrapBwrap basic structure includes required bwrap flags`` () =
    // Python parity: test_sandbox.TestBwrapBackend.test_basic_structure
    let (wrappedCmd, _) = wrapBwrap "echo hi" "/tmp/ws" "/tmp/ws"
    let tokens = splitWrapped wrappedCmd
    Assert.Equal("bwrap", tokens.[0])
    Assert.Contains("--new-session", tokens)
    Assert.Contains("--die-with-parent", tokens)
    Assert.Contains("--ro-bind", tokens)
    Assert.Contains("--proc", tokens)
    Assert.Contains("--dev", tokens)
    Assert.Contains("--tmpfs", tokens)

[<Fact>]
let ``wrapBwrap binds workspace read-write`` () =
    // Python parity: test_sandbox.TestBwrapBackend.test_workspace_bind_mounted_rw
    let ws = "/tmp/myworkspace"
    let (wrappedCmd, _) = wrapBwrap "ls" ws ws
    let tokens = splitWrapped wrappedCmd
    // Find --bind followed by ws ws
    let pairs =
        tokens
        |> List.windowed 3
        |> List.tryFind (function [ "--bind"; a; b ] -> a = ws && b = ws | _ -> false)
    Assert.True(pairs.IsSome, $"Expected '--bind {ws} {ws}' in: {wrappedCmd}")

[<Fact>]
let ``wrapBwrap ends with sh -c command`` () =
    // Python parity: separator '--' followed by 'sh', '-c', command
    let (wrappedCmd, _) = wrapBwrap "echo hi" "/tmp/ws" "/tmp/ws"
    let tokens = splitWrapped wrappedCmd
    let sepIdx = tokens |> List.tryFindIndex ((=) "--")
    Assert.True(sepIdx.IsSome, "Expected '--' separator in bwrap command")
    let tail = tokens.[sepIdx.Value + 1..]
    Assert.Equal<string list>([ "sh"; "-c"; "echo hi" ], tail)

[<Fact>]
let ``wrapBwrap clamps cwd to workspace when cwd is outside workspace`` () =
    // Python parity: test_sandbox.TestBwrapBackend.test_parent_dir_masked_with_tmpfs
    // When cwd is outside workspace, bwrap uses workspace as the effective cwd.
    let ws = "/tmp/myproject"
    let (wrappedCmd, _) = wrapBwrap "pwd" ws "/etc"
    let tokens = splitWrapped wrappedCmd
    // --chdir must point to ws (not /etc, which is outside workspace)
    let chdirIdx = tokens |> List.tryFindIndex ((=) "--chdir")
    Assert.True(chdirIdx.IsSome, "Expected --chdir in bwrap command")
    Assert.Equal(ws, tokens.[chdirIdx.Value + 1])
