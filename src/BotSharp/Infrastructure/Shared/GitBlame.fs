module BotSharp.Infrastructure.Shared.GitBlame

open System
open System.Diagnostics
open System.IO

// ═══════════════════════════════════════════════════════════════════════════
// GitBlame — per-line age annotation via `git blame --porcelain`
//
// Python parity: nanobot.utils.gitstore.GitStore.line_ages()
//
// Used by MemoryConsolidator to annotate MEMORY.md lines with their age
// (days since last git commit touched that line) when DreamAnnotateLineAges
// is enabled.  Lines older than _STALE_THRESHOLD_DAYS get a suffix like:
//   "## My Section  ← 30d"
//
// Behaviour when git is unavailable or fails:
//   • Returns [] — caller falls back to un-annotated content.
// ═══════════════════════════════════════════════════════════════════════════

/// Age of a single line based on git blame.
type LineAge = { AgeDays: int }

/// Days threshold above which a line is considered stale.
/// Mirrors Python nanobot.agent.memory._STALE_THRESHOLD_DAYS.
let staleThresholdDays = 14

// ── Internal helpers ──────────────────────────────────────────────────────

/// Run `git blame --porcelain <relPath>` in `workingDir`.
/// Returns the raw stdout string, or None if the process fails.
let private runGitBlame (workingDir: string) (relPath: string) : string option =
    try
        let psi = ProcessStartInfo("git", $"blame --porcelain \"{relPath}\"")
        psi.WorkingDirectory          <- workingDir
        psi.RedirectStandardOutput   <- true
        psi.RedirectStandardError    <- true
        psi.UseShellExecute          <- false
        psi.CreateNoWindow           <- true
        use proc = Process.Start(psi) |> Unchecked.nonNull
        let stdout = proc.StandardOutput.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode = 0 && stdout.Length > 0 then Some stdout
        else None
    with _ -> None

/// Parse `git blame --porcelain` output into a list of Unix timestamps
/// (one per blamed line, in order).
///
/// Each blamed block has the form:
///   <sha> <orig> <final> <count>
///   author-time <unix-timestamp>
///   ...
///   \t<line content>
///
/// We collect one `author-time` per `\t`-prefixed line (the content line).
let private parseBlameTimestamps (raw: string) : int64 list =
    let lines = raw.Split('\n')
    let mutable timestamps : int64 list = []
    let mutable currentTimestamp : int64 = 0L
    for line in lines do
        if line.StartsWith("author-time ", StringComparison.Ordinal) then
            match Int64.TryParse(line.["author-time ".Length..]) with
            | true, ts -> currentTimestamp <- ts
            | false, _ -> ()
        elif line.StartsWith("\t", StringComparison.Ordinal) then
            // This is the actual line content — emit the accumulated timestamp.
            timestamps <- currentTimestamp :: timestamps
            currentTimestamp <- 0L
    List.rev timestamps

/// Convert a Unix timestamp to age in days relative to UTC now.
let private ageDays (unixTs: int64) : int =
    let committedAt = DateTimeOffset.FromUnixTimeSeconds(unixTs)
    let diff = DateTimeOffset.UtcNow - committedAt
    max 0 (int diff.TotalDays)

// ── Public API ────────────────────────────────────────────────────────────

/// Return the age (in days) of each line in `relPath` within `workspacePath`,
/// using `git blame --porcelain`.
///
/// Returns `[]` if:
///   • No `.git` directory found in `workspacePath`.
///   • The file does not exist or is empty.
///   • `git blame` fails (git not installed, file not tracked, etc.).
///
/// Mirrors Python GitStore.line_ages().
let lineAges (workspacePath: string) (relPath: string) : LineAge list =
    let gitDir = Path.Combine(workspacePath, ".git")
    if not (Directory.Exists gitDir || File.Exists gitDir) then []
    else
        let fullPath = Path.Combine(workspacePath, relPath)
        if not (File.Exists fullPath) then []
        elif (FileInfo fullPath).Length = 0L then []
        else
            match runGitBlame workspacePath relPath with
            | None -> []
            | Some raw ->
                parseBlameTimestamps raw
                |> List.map (fun ts -> { AgeDays = ageDays ts })

/// Annotate a multi-line string with per-line age suffixes.
///
/// Non-blank lines whose age exceeds `staleThresholdDays` get a suffix:
///   "some content  ← 30d"
///
/// If `ages` is empty, or line count mismatches (uncommitted working-tree
/// edits), returns `content` unchanged — Python parity for safety.
///
/// Mirrors Python MemoryStore._annotate_with_ages().
let annotateContent (content: string) (ages: LineAge list) : string =
    if ages.IsEmpty then content
    else
        let hadTrailing = content.EndsWith("\n")
        let lines = content.Split('\n') |> Array.toList
        // Remove the spurious empty element that Split creates when content ends with \n.
        let lines =
            match List.rev lines with
            | "" :: rest -> List.rev rest
            | _          -> lines
        if lines.Length <> ages.Length then content   // length mismatch — skip
        else
            let annotated =
                List.map2 (fun line (age: LineAge) ->
                    if String.IsNullOrWhiteSpace(line) then line
                    elif age.AgeDays > staleThresholdDays then $"{line}  \u2190 {age.AgeDays}d"
                    else line) lines ages
            let joined = String.concat "\n" annotated
            if hadTrailing then joined + "\n" else joined
