module BotSharp.Infrastructure.Hooks.UserHooks

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// User-configurable shell hooks
//
// Users define hooks in {workspace}/hooks.json. Each hook specifies a shell
// command that runs at a specific lifecycle event (PreToolUse, PostToolUse,
// PreSendMessage, Stop).
//
// Hook commands receive context via environment variables (TOOL_NAME,
// TOOL_ARGS, FINAL_CONTENT, etc.) and control behavior via exit codes:
//   exit 0 = success (proceed normally)
//   exit ≠ 0 = block (PreToolUse blocks tool; PreSendMessage suppresses reply)
//
// Mirrors Claude Code's hook system. Loaded at startup, composed with the
// built-in AgentHook via AgentHook.compose.
// ═══════════════════════════════════════════════════════════════════════════

// ── Types ───────────────────────────────────────────────────────────────

type HookDefinition = {
    Event   : string        // "PreToolUse" | "PostToolUse" | "PreSendMessage" | "Stop"
    Match   : string option // tool name glob (PreToolUse/PostToolUse only); None = all
    Command : string        // shell command
}

type HooksConfig = {
    Hooks : HookDefinition list
}

// ── Glob matching ───────────────────────────────────────────────────────

/// Match a tool name against a pattern.
/// "*" = all, "shell" = exact, "write_*" = prefix glob.
let matchesToolName (pattern: string) (toolName: string) : bool =
    if pattern = "*" then true
    elif pattern.EndsWith("*") then
        toolName.StartsWith(pattern.[..pattern.Length - 2], StringComparison.OrdinalIgnoreCase)
    else
        String.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase)

// ── Hook loader ─────────────────────────────────────────────────────────

/// Load hooks from {workspace}/hooks.json.
/// Returns empty config if file doesn't exist or is malformed.
let loadHooksConfig (workspacePath: string) : HooksConfig =
    let path = Path.Combine(workspacePath, "hooks.json")
    if not (File.Exists path) then { Hooks = [] }
    else
        try
            let json = File.ReadAllText(path)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            match root.TryGetProperty("hooks") with
            | false, _ -> { Hooks = [] }
            | true, hooksObj ->
                let hooks = Collections.Generic.List<HookDefinition>()
                for eventName in [ "PreToolUse"; "PostToolUse"; "PreSendMessage"; "Stop" ] do
                    match hooksObj.TryGetProperty(eventName) with
                    | false, _ -> ()
                    | true, arr when arr.ValueKind = JsonValueKind.Array ->
                        for i in 0 .. arr.GetArrayLength() - 1 do
                            let el = arr[i]
                            let command =
                                match el.TryGetProperty("command") with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> ""
                            if command <> "" then
                                let matchPattern =
                                    match el.TryGetProperty("match") with
                                    | true, v when v.ValueKind = JsonValueKind.String ->
                                        match v.GetString() with null | "" -> None | s -> Some s
                                    | _ -> None
                                hooks.Add({
                                    Event = eventName
                                    Match = matchPattern
                                    Command = command
                                })
                    | _ -> ()
                { Hooks = List.ofSeq hooks }
        with ex ->
            eprintfn "[Hooks] Failed to parse hooks.json: %s" ex.Message
            { Hooks = [] }

// ── Command executor ────────────────────────────────────────────────────

/// Execute a hook shell command with environment variables.
/// Returns (exitCode, stdout, stderr).
let private executeHookCommand
    (command    : string)
    (env        : Map<string, string>)
    (workingDir : string)
    (timeoutMs  : int)
    : Async<int * string * string> =
    async {
        let psi = ProcessStartInfo("/bin/sh", "-c " + command)
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        for kv in env do
            psi.Environment[kv.Key] <- kv.Value
        try
            match Process.Start(psi) with
            | null ->
                eprintfn "[Hook] Failed to start process: %s" command
                return (1, "", "Process.Start returned null")
            | proc ->
            use proc = proc
            use cts = new Threading.CancellationTokenSource(timeoutMs)
            let! stdout = proc.StandardOutput.ReadToEndAsync(cts.Token) |> Async.AwaitTask
            let! stderr = proc.StandardError.ReadToEndAsync(cts.Token) |> Async.AwaitTask
            do! proc.WaitForExitAsync(cts.Token) |> Async.AwaitTask
            return (proc.ExitCode, stdout, stderr)
        with
        | :? OperationCanceledException ->
            eprintfn "[Hook] Command timed out after %ds: %s" (timeoutMs / 1000) command
            return (1, "", "Hook timed out")
        | ex ->
            eprintfn "[Hook] Command failed: %s" ex.Message
            return (1, "", ex.Message)
    }

// ── Environment builders ────────────────────────────────────────────────

let private baseEnv (workspacePath: string) (iteration: int) : Map<string, string> =
    Map.ofList [
        "WORKSPACE", workspacePath
        "ITERATION", string iteration
    ]

let private toolEnv (call: ToolCall) : Map<string, string> =
    let (ToolName name) = call.Tool
    let argsJson =
        try
            use ms = new MemoryStream()
            use w = new Utf8JsonWriter(ms)
            w.WriteStartObject()
            for kv in call.Arguments do
                w.WritePropertyName(kv.Key)
                kv.Value.WriteTo(w)
            w.WriteEndObject()
            w.Flush()
            Text.Encoding.UTF8.GetString(ms.ToArray())
        with _ -> "{}"
    let mutable env = Map.ofList [
        "TOOL_NAME", name
        "TOOL_ARGS", argsJson
    ]
    // Flatten string/number/bool args as TOOL_ARG_<name>
    for kv in call.Arguments do
        let value : string | null =
            match kv.Value.ValueKind with
            | JsonValueKind.String -> kv.Value.GetString()
            | JsonValueKind.Number -> kv.Value.GetRawText()
            | JsonValueKind.True -> "true"
            | JsonValueKind.False -> "false"
            | _ -> null
        if not (isNull value) then
            env <- env |> Map.add (sprintf "TOOL_ARG_%s" kv.Key) (value |> string)
    env

let private resultEnv (result: ToolResult) : Map<string, string> =
    match result with
    | ToolSuccess content ->
        Map.ofList [ "TOOL_RESULT", content; "TOOL_ERROR", "" ]
    | ToolFailure err ->
        let errMsg =
            match err with
            | ExecutionFailed msg -> msg
            | ExecutionTimeout t -> sprintf "Timed out after %gs" t.TotalSeconds
            | ParameterMissing f -> sprintf "Missing parameter: %s" f
            | ParameterInvalid (f, r) -> sprintf "Invalid %s: %s" f r
            | ToolNotFound (ToolName n) -> sprintf "Tool not found: %s" n
            | WorkspaceViolation p -> sprintf "Access denied: %s" p
        Map.ofList [ "TOOL_RESULT", ""; "TOOL_ERROR", errMsg ]

let private mergeEnv (maps: Map<string, string> list) : Map<string, string> =
    maps |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m) Map.empty

// ── AgentHook builder ───────────────────────────────────────────────────

/// Build an AgentHook that executes user-defined shell commands.
let buildUserHook (config: HooksConfig) (workspacePath: string) (timeoutMs: int) : AgentHook =
    if config.Hooks.IsEmpty then AgentHook.none
    else

    let preToolUse   = config.Hooks |> List.filter (fun h -> h.Event = "PreToolUse")
    let postToolUse  = config.Hooks |> List.filter (fun h -> h.Event = "PostToolUse")
    let preSendMsg   = config.Hooks |> List.filter (fun h -> h.Event = "PreSendMessage")
    let stopHooks    = config.Hooks |> List.filter (fun h -> h.Event = "Stop")

    { AgentHook.none with

        BeforeToolCall = fun ctx call ->
            async {
                let (ToolName toolName) = call.Tool
                let matching = preToolUse |> List.filter (fun h ->
                    match h.Match with
                    | None -> true
                    | Some pat -> matchesToolName pat toolName)
                let env = mergeEnv [ baseEnv workspacePath ctx.Iteration; toolEnv call ]
                let mutable blocked = None
                for hook in matching do
                    if blocked.IsNone then
                        let! (exitCode, _stdout, stderr) = executeHookCommand hook.Command env workspacePath timeoutMs
                        if exitCode <> 0 then
                            let msg = if stderr.Trim() <> "" then stderr.Trim() else sprintf "Hook blocked (exit %d)" exitCode
                            blocked <- Some msg
                match blocked with
                | Some msg -> return Result.Error msg
                | None -> return Result.Ok ()
            }

        AfterToolCall = fun ctx call result ->
            async {
                let (ToolName toolName) = call.Tool
                let matching = postToolUse |> List.filter (fun h ->
                    match h.Match with
                    | None -> true
                    | Some pat -> matchesToolName pat toolName)
                if not matching.IsEmpty then
                    let env = mergeEnv [ baseEnv workspacePath ctx.Iteration; toolEnv call; resultEnv result ]
                    for hook in matching do
                        let! (exitCode, _stdout, stderr) = executeHookCommand hook.Command env workspacePath timeoutMs
                        if exitCode <> 0 then
                            eprintfn "[Hook] PostToolUse for %s failed (exit %d): %s" toolName exitCode stderr
                return result
            }

        PreSendMessage = fun ctx text ->
            async {
                if preSendMsg.IsEmpty then return Some text
                else
                    let env = mergeEnv [ baseEnv workspacePath ctx.Iteration; Map.ofList [ "FINAL_CONTENT", text ] ]
                    let mutable suppressed = false
                    for hook in preSendMsg do
                        if not suppressed then
                            let! (exitCode, _stdout, stderr) = executeHookCommand hook.Command env workspacePath timeoutMs
                            if exitCode <> 0 then
                                eprintfn "[Hook] PreSendMessage suppressed reply (exit %d): %s" exitCode stderr
                                suppressed <- true
                    if suppressed then return None else return Some text
            }

        OnTurnComplete = fun ctx ->
            async {
                if not stopHooks.IsEmpty then
                    let env = baseEnv workspacePath ctx.Iteration
                    for hook in stopHooks do
                        let! (exitCode, _stdout, stderr) = executeHookCommand hook.Command env workspacePath timeoutMs
                        if exitCode <> 0 then
                            eprintfn "[Hook] Stop hook failed (exit %d): %s" exitCode stderr
            }
    }
