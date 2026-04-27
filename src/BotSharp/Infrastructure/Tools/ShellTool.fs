module BotSharp.Infrastructure.Tools.ShellTool

open System
open System.Diagnostics
open System.Text.Json
open System.Text.RegularExpressions
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// Shell execution tool
//
// Runs a shell command in a subprocess with a configurable timeout.
// Dangerous command detection prevents accidentally destructive commands;
// the check is heuristic (not a security boundary).
//
// Behavioural parity with Python's ExecTool:
//   • Always returns ToolSuccess with output+exit-code — even for non-zero
//     exits. Returning ToolFailure would hide stderr from the LLM, preventing
//     it from diagnosing the error. Only infrastructure failures (process
//     wouldn't start, blocked command) return ToolFailure.
//   • Output truncated at 10 000 chars using symmetric head+tail truncation
//     so the LLM sees both the start and end of large outputs.
//   • working_dir parameter lets the agent change the CWD without a `cd`.
// ═══════════════════════════════════════════════════════════════════════════

// ── Dangerous command patterns ────────────────────────────────────────────
// Matches Python's deny_patterns list.  Heuristic only — not a sandbox.

let private dangerousPatterns =
    [| @"\brm\s+-[rRfFqQ]*[rR][fF]"             // rm -rf / rm -Rf / rm -fr
       @"\brm\s+-[rRfFqQ]*[rR]\b"               // rm -r (recursive without f)
       @"\bdel\s+/[fq]\b"                        // Windows del /f /q
       @"\brmdir\s+/s\b"                         // Windows rmdir /s
       @"(?:^|[;&|]\s*)format\b"                 // format (standalone command)
       @"\b(mkfs|diskpart)\b"                    // filesystem / disk tools
       @"\bdd\s+if="                             // disk dump
       @">\s*/dev/sd"                            // overwrite block device
       @"\b(shutdown|reboot|poweroff|halt)\b"    // system power management
       @":\(\)\s*\{.*\};\s*:"                    // fork bomb
       @"\bsudo\s+rm\s+-[rf]{1,2}\s+/"          // root rm -rf
       // Guard BotSharp internal state files (mirrors Python #2989):
       // history.jsonl and .dream_cursor are managed by the runtime;
       // direct writes corrupt cursor format and crash /dream.
       @">>?\s*\S*(?:history\.jsonl|\.dream_cursor)"                    // > / >> redirect
       @"\btee\b[^|;&<>]*(?:history\.jsonl|\.dream_cursor)"             // tee / tee -a
       @"\b(?:cp|mv)\b(?:\s+[^\s|;&<>]+)+\s+\S*(?:history\.jsonl|\.dream_cursor)"  // cp/mv target
       @"\bdd\b[^|;&<>]*\bof=\S*(?:history\.jsonl|\.dream_cursor)"     // dd of=
       @"\bsed\s+-i[^|;&<>]*(?:history\.jsonl|\.dream_cursor)"         // sed -i
    |]

// ── SSRF protection for shell commands ───────────────────────────────────
// Mirrors Python's shell_tool._guard_command SSRF check (contains_internal_url).
// Extracts http(s):// URLs from the command and blocks those whose host matches
// a private/internal IP pattern.  DNS resolution is deliberately avoided to
// keep this synchronous; direct-IP and localhost patterns are caught instead.

/// True if the hostname portion of a URL is a known private/internal address.
/// Covers localhost, loopback (127.x.x.x), RFC-1918 private ranges, and the
/// cloud metadata endpoint (169.254.169.254).  Does not do DNS lookup.
let private isInternalHost (host: string) : bool =
    let h = host.Trim().ToLowerInvariant().TrimEnd('.')
    if h = "localhost" || h = "::1" then true
    else
        // Try numeric IP
        match Net.IPAddress.TryParse(h) with
        | false, _ -> false
        | true, addr ->
            let b = addr.GetAddressBytes()
            match addr.AddressFamily with
            | Net.Sockets.AddressFamily.InterNetwork ->
                match b.[0], b.[1] with
                | 0uy, _            -> true  // 0.0.0.0/8
                | 10uy, _           -> true  // 10.0.0.0/8
                | 127uy, _          -> true  // 127.0.0.0/8 (loopback)
                | 169uy, 254uy      -> true  // 169.254.0.0/16 (link-local / cloud metadata)
                | 172uy, s when s >= 16uy && s <= 31uy -> true  // 172.16.0.0/12
                | 192uy, 168uy      -> true  // 192.168.0.0/16
                | 100uy, s when s >= 64uy && s <= 127uy -> true  // 100.64.0.0/10 CGNAT
                | _                 -> false
            | Net.Sockets.AddressFamily.InterNetworkV6 ->
                // ::1 loopback, fc00::/7 unique-local, fe80::/10 link-local
                (b.Length >= 1 && (b.[0] &&& 0xFEuy) = 0xFCuy)
                || (b.Length >= 2 && b.[0] = 0xFEuy && (b.[1] &&& 0xC0uy) = 0x80uy)
                || (b.Length = 16 && b.[15] = 1uy && b.[..14] |> Array.forall ((=) 0uy))
            | _ -> false

let private urlRegex = Regex(@"https?://([^/\s""';|<>:]+)", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

// ── SSRF whitelist: CIDR matching ────────────────────────────────────────────
// Supports IPv4 CIDR (e.g. "10.0.0.0/8") and plain hostnames/IPs.
// A host is whitelisted if it matches at least one entry in the list.

/// True if an IPv4 address bytes matches a CIDR string like "10.0.0.0/8".
let private cidrMatchesIPv4 (cidr: string) (addrBytes: byte[]) : bool =
    match cidr.Split('/') with
    | [| ipStr; prefixStr |] ->
        match Net.IPAddress.TryParse(ipStr.Trim()), Int32.TryParse(prefixStr.Trim()) with
        | (true, baseAddr), (true, prefix) when prefix >= 0 && prefix <= 32 ->
            let baseBytes = baseAddr.GetAddressBytes()
            if baseBytes.Length <> 4 || addrBytes.Length <> 4 then false
            else
                let toU32 (b: byte[]) =
                    (uint32 b.[0] <<< 24) ||| (uint32 b.[1] <<< 16) ||| (uint32 b.[2] <<< 8) ||| uint32 b.[3]
                let mask = if prefix = 0 then 0u else (0xFFFFFFFFu <<< (32 - prefix))
                (toU32 addrBytes &&& mask) = (toU32 baseBytes &&& mask)
        | _ -> false
    | _ -> false

/// True if the host string is covered by at least one whitelist entry.
/// Entries may be CIDRs ("10.0.0.0/8") or exact hostnames/IPs ("192.168.1.1").
let private isWhitelisted (ssrfWhitelist: string list) (host: string) : bool =
    if List.isEmpty ssrfWhitelist then false
    else
        let h = host.Trim().ToLowerInvariant().TrimEnd('.')
        ssrfWhitelist |> List.exists (fun entry ->
            let e = entry.Trim()
            // Plain exact match (hostname or IP without prefix)
            if not (e.Contains('/')) then
                e.Equals(h, StringComparison.OrdinalIgnoreCase)
            else
                // CIDR match — only valid for numeric IPs
                match Net.IPAddress.TryParse(h) with
                | false, _ -> false
                | true, addr ->
                    match addr.AddressFamily with
                    | Net.Sockets.AddressFamily.InterNetwork ->
                        cidrMatchesIPv4 e (addr.GetAddressBytes())
                    | _ -> false)

/// Returns Some errorMessage if the command contains an internal/private URL; None otherwise.
/// `ssrfWhitelist` — CIDR/hostname entries exempted from blocking.
let private guardInternalUrl (ssrfWhitelist: string list) (cmd: string) : string option =
    urlRegex.Matches(cmd)
    |> Seq.tryPick (fun m ->
        let host = m.Groups.[1].Value
        if isInternalHost host && not (isWhitelisted ssrfWhitelist host) then
            Some $"Refused: command targets an internal/private URL ({m.Value})"
        else None)

/// Returns Some errorMessage when the command is refused; None when safe to run.
let private guardCommand (ssrfWhitelist: string list) (cmd: string) : string option =
    match dangerousPatterns |> Array.tryPick (fun p ->
        if Regex.IsMatch(cmd, p, RegexOptions.IgnoreCase) then
            Some $"Refused: command matches a dangerous pattern ({p})"
        else None)
    with
    | Some _ as refusal -> refusal
    | None -> guardInternalUrl ssrfWhitelist cmd

// ── Sandbox helpers (mirrors Python's sandbox.py / exec_tool._bwrap) ─────
// Only "bwrap" is supported; any other value is silently ignored (matches
// Python: unknown sandbox backend raises ValueError, but "" is "no sandbox").
// On non-Linux platforms bwrap is typically unavailable; the wrapped command
// will fail at runtime the same way it would in Python.

/// Shell-quote a single argument (simple single-quote wrapping).
let private shellQuote (s: string) : string =
    "'" + s.Replace("'", "'\\''") + "'"

/// Build a bwrap-wrapped command string for the given workspace + cwd.
/// Mirrors Python nanobot's sandbox._bwrap() / wrap_command("bwrap", ...).
/// Exposed as `internal` so unit tests can verify the token structure without
/// actually invoking bwrap (which is Linux-only).
let internal wrapBwrap (command: string) (workspace: string) (cwd: string) : string * string =
    let ws =
        try IO.Path.GetFullPath(workspace)
        with _ -> workspace
    let sandboxCwd =
        try
            let resolvedCwd = IO.Path.GetFullPath(cwd)
            if resolvedCwd.StartsWith(ws, StringComparison.Ordinal) then resolvedCwd
            else ws
        with _ -> ws

    let required = [ "/usr" ]
    let optional = [ "/bin"; "/lib"; "/lib64"; "/etc/alternatives"
                     "/etc/ssl/certs"; "/etc/resolv.conf"; "/etc/ld.so.cache" ]

    let parts = System.Collections.Generic.List<string>()
    parts.Add "bwrap"
    parts.Add "--new-session"
    parts.Add "--die-with-parent"
    for p in required do
        parts.Add "--ro-bind"; parts.Add p; parts.Add p
    for p in optional do
        parts.Add "--ro-bind-try"; parts.Add p; parts.Add p
    parts.AddRange [
        "--proc"; "/proc"; "--dev"; "/dev"; "--tmpfs"; "/tmp"
        "--tmpfs"; IO.Path.GetDirectoryName(ws)  // mask parent (config dir)
        "--dir";  ws
        "--bind"; ws; ws
        "--chdir"; sandboxCwd
        "--"; "sh"; "-c"; command
    ]
    // Build shell-quoted command string (mirrors Python shlex.join)
    let wrappedCmd = parts |> Seq.map shellQuote |> String.concat " "
    wrappedCmd, ws   // (wrappedCommand, newCwd)

// ── Output helpers ────────────────────────────────────────────────────────

let private maxOutput = 10_000

/// Symmetric truncation: keeps the first half and last half so the LLM
/// sees both the beginning and the end of large outputs.
let private truncateOutput (s: string) : string =
    if s.Length <= maxOutput then s
    else
        let half   = maxOutput / 2
        let dropped = s.Length - maxOutput
        s.[..half - 1] + $"\n\n... ({dropped:N0} chars truncated) ...\n\n" + s.[s.Length - half..]

// ── Tool spec + implementation ────────────────────────────────────────────

let execSpec : ToolSpec = {
    Name            = ToolName "exec"
    Description     =
        "Execute a shell command and return its output. " +
        "Prefer read_file/write_file/edit_file over cat/echo/sed, " +
        "and grep/glob over shell find/grep. " +
        "Use -y/--yes to avoid interactive prompts. " +
        "Output truncated at 10 000 chars; timeout defaults to 60 s (max 600 s)."
    Parameters      = Map.ofList [
        "command",     { Type = JsString; Description = "Shell command to run"; Required = true }
        "working_dir", { Type = JsString; Description = "Working directory (default: workspace root)"; Required = false }
        "timeout",     { Type = JsNumber; Description = "Timeout in seconds (default 60, max 600)"; Required = false }
    ]
    ConcurrencySafe = false  // exclusive: shell commands can have arbitrary side effects
}

/// Execute a shell command.
/// `defaultTimeoutSec` — config-level default timeout (0 = use built-in default of 60 s).
/// `restrictToWorkspace` — when true, clamp working_dir to workspace subtree (Python: tools.exec.restrict_to_workspace).
/// `pathAppend` — colon-separated path entries appended to PATH for the subprocess (Python: exec.path_append; "" = no change).
/// `allowedEnvKeys` — if non-empty, only these env var names are passed to the subprocess (Python: exec.allowed_env_keys).
/// `ssrfWhitelist` — CIDR/hostname entries exempted from the SSRF URL block (Python: tools.ssrf_whitelist; [] = no exemptions).
/// `sandbox` — sandbox backend: "" (none) or "bwrap" (Linux; Python: exec.sandbox).
/// The per-call "timeout" argument takes precedence; both are capped at 600 s.
let exec (workspacePath: string) (defaultTimeoutSec: int) (restrictToWorkspace: bool) (pathAppend: string) (allowedEnvKeys: string list) (ssrfWhitelist: string list) (sandbox: string) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "command" args with
        | Error e -> return ToolFailure e
        | Ok command ->
            match guardCommand ssrfWhitelist command with
            | Some msg -> return ToolFailure (ExecutionFailed msg)
            | None ->
                let builtInDefault = if defaultTimeoutSec > 0 then defaultTimeoutSec else 60
                let timeoutSec = tryIntArg "timeout" args |> Option.defaultValue builtInDefault |> min 600
                let requestedCwd =
                    tryStringArg "working_dir" args
                    |> Option.defaultValue workspacePath
                // When restrictToWorkspace is true, validate that working_dir is within
                // the workspace subtree (Python: restrict_to_workspace, #2826).
                // Python returns an error when working_dir is outside workspace;
                // clamping silently would hide the problem from the LLM.
                let cwdResult =
                    if not restrictToWorkspace then
                        Ok requestedCwd
                    else
                        let normalWs  = IO.Path.GetFullPath(workspacePath)
                        let normalCwd = IO.Path.GetFullPath(requestedCwd)
                        if normalCwd.StartsWith(normalWs, StringComparison.OrdinalIgnoreCase) || normalCwd = normalWs then
                            Ok normalCwd    // inside workspace — allow as-is
                        else
                            Error "Error: working_dir is outside the configured workspace"
                match cwdResult with
                | Error msg -> return ToolSuccess msg   // mimic Python (returns string, not ToolFailure)
                | Ok cwd ->

                // When restrictToWorkspace is true, also scan for absolute paths embedded
                // in the command string (Python: _extract_absolute_paths + _guard_command).
                // Blocks commands that reference paths outside the workspace, e.g. `cat /etc/passwd`.
                let absPathBlock =
                    if not restrictToWorkspace then None
                    else
                        let normalWs = IO.Path.GetFullPath(workspacePath)
                        // Path traversal (Python: "..\\" or "../" check)
                        if command.Contains("../") || command.Contains("..\\") then
                            Some "Refused: command blocked by safety guard (path traversal detected)"
                        else
                            // POSIX absolute paths (e.g. /etc/passwd) and home paths (~/secret)
                            let posixRx = Regex(@"(?:^|[\s|>'""])(/[^\s""'>;|<]+)")
                            let homeRx  = Regex(@"(?:^|[\s|>'""])(~/[^\s""'>;|<]*)")
                            let homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                            let checkRaw (raw: string) =
                                try
                                    let expanded = raw.Trim().Replace("~/", homeDir + "/")
                                    let p = IO.Path.GetFullPath(expanded)
                                    // Block if: absolute AND not inside workspace AND workspace not inside it
                                    if IO.Path.IsPathRooted(p)
                                       && not (p.StartsWith(normalWs, StringComparison.Ordinal))
                                       && not (normalWs.StartsWith(p, StringComparison.Ordinal)) then
                                        Some $"Refused: command blocked by safety guard (path outside workspace: {raw.Trim()})"
                                    else None
                                with _ -> None
                            let allPaths =
                                [ for m in posixRx.Matches(command) -> m.Groups.[1].Value
                                  for m in homeRx.Matches(command)  -> m.Groups.[1].Value ]
                            allPaths |> List.tryPick checkRaw

                match absPathBlock with
                | Some msg -> return ToolFailure (ExecutionFailed msg)
                | None ->
                // Sandbox wrapping: if sandbox="bwrap" (Linux), wrap the command with bwrap.
                // This mirrors Python: if self.sandbox: command = wrap_command(...)
                // On platforms without bwrap the wrapped command will fail at runtime.
                let (effectiveCommand, effectiveCwd) =
                    if sandbox.Trim() = "bwrap" then
                        wrapBwrap command workspacePath cwd
                    else
                        command, cwd
                let psi =
                    ProcessStartInfo(
                        FileName               = "/bin/sh",
                        WorkingDirectory       = effectiveCwd,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true)
                // Env isolation — mirrors Python's _build_env():
                //   [] allowedEnvKeys  → minimal safe-var whitelist (strips secrets from inherited env)
                //   non-[] allowedEnvKeys → exactly those keys only
                // Always strips everything not in the allowed set so that parent-process
                // secrets (API keys, tokens) never leak into the subprocess.
                let safeVars =
                    Set.ofList ["PATH";"HOME";"USER";"SHELL";"LANG";"LANGUAGE";"LC_ALL";"LC_CTYPE";"TMPDIR";"TMP";"TERM"]
                let allowedSet =
                    if List.isEmpty allowedEnvKeys then safeVars
                    else Set.ofList allowedEnvKeys
                let keysToRemove =
                    psi.Environment.Keys
                    |> Seq.filter (fun k -> not (Set.contains k allowedSet))
                    |> Seq.toList  // materialize before removing (can't remove while iterating)
                for k in keysToRemove do
                    psi.Environment.Remove(k) |> ignore
                // Append extra PATH entries when configured (Python: exec.path_append).
                if pathAppend.Trim() <> "" then
                    let currentPath = psi.Environment |> Seq.tryFind (fun kv -> kv.Key = "PATH") |> Option.map (fun kv -> kv.Value) |> Option.defaultValue (Environment.GetEnvironmentVariable("PATH") |> Option.ofObj |> Option.defaultValue "")
                    psi.Environment["PATH"] <- $"{currentPath}:{pathAppend.Trim(':')}"
                psi.ArgumentList.Add("-c")
                psi.ArgumentList.Add(effectiveCommand)
                try
                    match Process.Start(psi) with
                    | null ->
                        return ToolFailure (ExecutionFailed "Failed to start process")
                    | proc ->
                    use proc = proc
                    use cts = new System.Threading.CancellationTokenSource(timeoutSec * 1000)
                    let stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token)
                    let stderrTask = proc.StandardError.ReadToEndAsync(cts.Token)
                    let! exited =
                        async {
                            try
                                do! proc.WaitForExitAsync(cts.Token) |> Async.AwaitTask
                                return true
                            with :? OperationCanceledException ->
                                return false
                        }
                    if not exited then
                        try proc.Kill(true) with _ -> ()
                        return ToolFailure (ExecutionTimeout (TimeSpan.FromSeconds(float timeoutSec)))
                    else
                        let! stdout = stdoutTask |> Async.AwaitTask
                        let! stderr = stderrTask |> Async.AwaitTask
                        let exitCode = proc.ExitCode
                        // Build output like Python's ExecTool: stdout + STDERR: + exit code.
                        // Always ToolSuccess so the LLM can see stderr and diagnose failures;
                        // ToolFailure is reserved for infrastructure-level failures above.
                        let parts =
                            [ if stdout.Trim() <> "" then yield stdout.TrimEnd()
                              if stderr.Trim() <> "" then yield $"STDERR:\n{stderr.TrimEnd()}"
                              yield $"\nExit code: {exitCode}" ]
                        let raw    = if parts.IsEmpty then "(no output)" else String.concat "\n" parts
                        let output = truncateOutput raw
                        return ToolSuccess output
                with ex ->
                    return ToolFailure (ExecutionFailed ex.Message)
    }

/// Build the exec tool pair, wiring config-level default timeout, workspace restriction, PATH append, env allowlist, SSRF whitelist, and sandbox.
/// `defaultTimeoutSec` — from BotSharpConfig.ExecTimeoutSeconds (0 = use 60 s built-in default).
/// `restrictToWorkspace` — from BotSharpConfig.RestrictToWorkspace.
/// `pathAppend` — from BotSharpConfig.ExecPathAppend ("" = no modification).
/// `allowedEnvKeys` — from BotSharpConfig.ExecAllowedEnvKeys ([] = minimal safe-var whitelist; Python _build_env() parity).
/// `ssrfWhitelist` — from BotSharpConfig.SsrfWhitelist ([] = no exemptions).
/// `sandbox` — from BotSharpConfig.ExecSandbox ("" = no sandbox; "bwrap" = Linux bwrap isolation).
let allTools (workspacePath: string) (defaultTimeoutSec: int) (restrictToWorkspace: bool) (pathAppend: string) (allowedEnvKeys: string list) (ssrfWhitelist: string list) (sandbox: string)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ execSpec, exec workspacePath defaultTimeoutSec restrictToWorkspace pathAppend allowedEnvKeys ssrfWhitelist sandbox ]
