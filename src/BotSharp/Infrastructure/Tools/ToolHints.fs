module BotSharp.Infrastructure.Tools.ToolHints

open System
open System.Text.RegularExpressions
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Tool hint formatting
//
// Mirrors Python nanobot.utils.tool_hints.format_tool_hints.
//
// Produces concise human-readable descriptions of tool calls:
//   read foo.txt
//   write src/app.py
//   $ npm install
//   search "async F#"
//   mcp_github::get_issue("…/123")
//   read foo.txt × 3    ← repeated tool, deduplicated
//
// Pure functions — no I/O, no side effects.
// ═══════════════════════════════════════════════════════════════════════════

// ── Path abbreviation ────────────────────────────────────────────────────────

let private homeDir =
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/')

/// Abbreviate a URL to ≤ maxLen chars: domain + "…/" + basename.
let private abbreviateUrl (url: string) (maxLen: int) : string =
    if url.Length <= maxLen then url
    else
        // Try to keep scheme://domain/…/basename
        let slashIdx = url.IndexOf("://")
        if slashIdx < 0 then
            url.[..maxLen - 2] + "…"
        else
            let afterScheme = url.[slashIdx + 3..]
            let domainEnd   = afterScheme.IndexOf('/')
            let domain      = if domainEnd < 0 then afterScheme else afterScheme.[..domainEnd - 1]
            let pathPart    = if domainEnd < 0 then "" else afterScheme.[domainEnd..]
            let segments    = pathPart.TrimEnd('/').Split('/') |> Array.filter (fun s -> s.Length > 0)
            let basename    = if segments.Length > 0 then segments.[segments.Length - 1] else ""
            let prefix      = url.[..slashIdx + 2] + domain   // e.g. "https://github.com"
            if basename = "" then prefix.[..maxLen - 2] + "…"
            else $"{prefix}/…/{basename}"

/// Abbreviate a file path to ≤ maxLen chars, mirroring Python abbreviate_path.
let abbreviatePath (path: string) (maxLen: int) : string =
    if path = null || path = "" then path
    elif path.StartsWith("http://") || path.StartsWith("https://") then
        abbreviateUrl path maxLen
    else
        let normalized = path.Replace('\\', '/')
        let normalized =
            if normalized.StartsWith(homeDir + "/") then "~" + normalized.[homeDir.Length..]
            elif normalized = homeDir then "~"
            else normalized
        if normalized.Length <= maxLen then normalized
        else
            let parts = normalized.TrimEnd('/').Split('/') |> Array.toList
            match parts with
            | []  -> normalized.[..maxLen - 2] + "…"
            | [_] -> normalized.[..maxLen - 2] + "…"
            | _ ->
                let basename = List.last parts
                let parents  = parts.[..parts.Length - 2]
                // Budget: maxLen − "…/" (2) − "/" before basename (1) − basename length
                let mutable budget = maxLen - 2 - 1 - basename.Length
                let mutable kept   = []
                for seg in List.rev parents do
                    let needed = seg.Length + 1   // segment + "/"
                    if budget >= needed then
                        kept  <- seg :: kept
                        budget <- budget - needed
                if kept.IsEmpty then $"…/{basename}"
                else "…/" + String.concat "/" kept + "/" + basename

// ── Regex for finding paths in shell commands ────────────────────────────────

// Matches double-quoted, single-quoted, or bare absolute/home paths in a command string.
let private pathInCmdRe =
    Regex(
        "\"(?<double>(?:[A-Za-z]:[/\\\\]|~/|/)[^\"]+)\""
        + "|'(?<single>(?:[A-Za-z]:[/\\\\]|~/|/)[^']+)'"
        + "|(?<bare>(?:[A-Za-z]:[/\\\\]|~/|(?<=\\s)/)[^\\s;&|<>\"']+)",
        RegexOptions.Compiled)

let private abbreviateCommand (cmd: string) (maxLen: int) : string =
    let abbreviated =
        pathInCmdRe.Replace(cmd, fun m ->
            let dbl  = m.Groups.["double"]
            let sng  = m.Groups.["single"]
            let bare = m.Groups.["bare"]
            if dbl.Success  then $"\"{abbreviatePath dbl.Value  25}\""
            elif sng.Success then $"'{abbreviatePath sng.Value  25}'"
            else abbreviatePath bare.Value 25)
    if abbreviated.Length <= maxLen then abbreviated
    else abbreviated.[..maxLen - 2] + "…"

// ── Per-tool formatting registry ─────────────────────────────────────────────

/// Format descriptor: (preferred arg keys, template, isPath, isCommand)
type private FmtSpec = string list * string * bool * bool

let private knownFormats : Map<string, FmtSpec> =
    Map.ofList [
        "read_file",  (["path"; "file_path"],     "read {}",     true,  false)
        "write_file", (["path"; "file_path"],      "write {}",    true,  false)
        "edit_file",  (["file_path"; "path"],      "edit {}",     true,  false)
        "glob",       (["pattern"],                "glob \"{}\"", false, false)
        "grep",       (["pattern"],                "grep \"{}\"", false, false)
        "exec",       (["command"],                "$ {}",        false, true)
        "web_search", (["query"],                  "search \"{}\"", false, false)
        "web_fetch",  (["url"],                    "fetch {}",    true,  false)
        "list_dir",   (["path"],                   "ls {}",       true,  false)
    ]

let private extractArg (args: Map<string, string>) (keyArgs: string list) : string option =
    keyArgs |> List.tryPick (fun k -> args.TryFind k |> Option.filter (fun v -> v.Length > 0))
    |> Option.orElse (args |> Map.toSeq |> Seq.map snd |> Seq.tryFind (fun v -> v.Length > 0))

let private fmtKnown (args: Map<string, string>) (spec: FmtSpec) (toolName: string) : string =
    let keyArgs, template, isPath, isCmd = spec
    match extractArg args keyArgs with
    | None     -> toolName
    | Some raw ->
        let formatted =
            if isPath then abbreviatePath raw 40
            elif isCmd then abbreviateCommand raw 40
            else raw
        template.Replace("{}", formatted)

let private fmtMcp (args: Map<string, string>) (toolName: string) : string =
    // MCP names are like mcp_github__get_issue or mcp_github_get_issue
    let rest = toolName.[4..]   // strip "mcp_"
    let server, tool =
        if rest.Contains("__") then
            let parts = rest.Split("__", 2)
            parts.[0], parts.[1]
        else
            let parts = rest.Split('_')
            if parts.Length > 1 then parts.[0], String.concat "_" parts.[1..]
            else rest, ""
    if tool = "" then toolName
    else
        match args |> Map.toSeq |> Seq.tryFind (fun (_, v) -> v.Length > 0) with
        | None         -> $"{server}::{tool}"
        | Some (_, v)  -> $"{server}::{tool}(\"{abbreviatePath v 40}\")"

let private fmtFallback (args: Map<string, string>) (toolName: string) : string =
    match args |> Map.toSeq |> Seq.tryFind (fun (_, v) -> v.Length > 0) with
    | None ->
        toolName
    | Some (_, v) ->
        if v.Length > 40 then $"{toolName}(\"{abbreviatePath v 40}\")"
        else $"{toolName}(\"{v}\")"

// ── Public API ────────────────────────────────────────────────────────────────

/// Format a list of tool calls into a concise comma-separated hint string.
/// Identical tool hints are collapsed with a × count (e.g. "read foo.txt × 3").
/// Mirrors Python nanobot.utils.tool_hints.format_tool_hints.
let formatToolHints (calls: ToolCall list) : string =
    if calls.IsEmpty then ""
    else
        let rendered =
            calls |> List.map (fun call ->
                let (ToolName name) = call.Tool
                // Convert arguments Map<string,JsonValue> → Map<string,string> for matching.
                // Arguments in our domain are Map<string, System.Text.Json.JsonElement>.
                // We only need string values for formatting.
                let args =
                    call.Arguments
                    |> Map.toSeq
                    |> Seq.choose (fun (k, v) ->
                        // Python parity: only string-typed arguments are included.
                        // Non-string types (numbers, booleans, arrays) are excluded from
                        // formatting so the fallback shows only the tool name.
                        if v.ValueKind = System.Text.Json.JsonValueKind.String then
                            let s = v.GetString()
                            if s <> null && s.Length > 0 then Some (k, s) else None
                        else None)
                    |> Map.ofSeq
                match knownFormats.TryFind name with
                | Some spec -> fmtKnown args spec name
                | None when name.StartsWith("mcp_") -> fmtMcp args name
                | None -> fmtFallback args name)

        // Deduplicate consecutive identical hints with × count
        let deduplicated =
            rendered
            |> List.fold (fun acc hint ->
                match acc with
                | (h, c) :: rest when h = hint -> (h, c + 1) :: rest
                | _ -> (hint, 1) :: acc) []
            |> List.rev

        deduplicated
        |> List.map (fun (hint, count) ->
            if count > 1 then $"{hint} × {count}" else hint)
        |> String.concat ", "
