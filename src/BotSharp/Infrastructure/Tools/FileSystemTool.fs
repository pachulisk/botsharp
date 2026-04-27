module BotSharp.Infrastructure.Tools.FileSystemTool

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// File-system tool implementations
//
// Safety rule: all paths must resolve to children of workspacePath.
// Attempts to escape via "../" etc. return WorkspaceViolation.
// ═══════════════════════════════════════════════════════════════════════════

// ── Read-before-edit tracking (matches Python nanobot's file_state.py) ───
// Tracks which files have been read (absolute path → last-known mtime + SHA256).
// editFile and writeFile warn when a file hasn't been read in the current
// session or has been modified since the last read.
// SHA256 hash is used to suppress false-positive "file changed" warnings when
// the mtime changes but the content is identical (e.g. `touch file`).
// This is global mutable state (shared across sessions), same trade-off as Python.

type private ReadRecord = {
    Mtime       : int64
    ContentHash : string   // SHA256 hex of the full file at read time (for mtime-only changes)
    Offset      : int
    Limit       : int
    CanDedup    : bool     // false after a write (forces full re-read next time)
    ReadAt      : DateTimeOffset
}

let private readState = ConcurrentDictionary<string, ReadRecord>()

/// Compute SHA256 hex of a file (returns empty string on failure).
let private hashFile (path: string) : string =
    try
        use sha  = SHA256.Create()
        use fs   = File.OpenRead(path)
        let bytes = sha.ComputeHash(fs)
        Convert.ToHexString(bytes).ToLowerInvariant()
    with _ -> ""

/// Record that a file was successfully read (with offset/limit for dedup).
let recordFileRead (fullPath: string) (offset: int) (limit: int) =
    try
        let mtime = (File.GetLastWriteTimeUtc(fullPath)).Ticks
        let hash  = hashFile fullPath
        readState[fullPath] <- {
            Mtime = mtime; ContentHash = hash
            Offset = offset; Limit = limit
            CanDedup = true; ReadAt = DateTimeOffset.UtcNow }
    with _ -> ()   // best-effort; don't let tracking failures surface

/// Record that a file was written (updates mtime + hash; marks CanDedup = false).
let recordFileWrite (fullPath: string) =
    try
        let mtime = (File.GetLastWriteTimeUtc(fullPath)).Ticks
        let hash  = hashFile fullPath
        readState[fullPath] <- {
            Mtime = mtime; ContentHash = hash
            Offset = 1; Limit = 2000
            CanDedup = false; ReadAt = DateTimeOffset.UtcNow }
    with _ -> ()

/// Check if a file has been read and is still fresh (for edit-before-read warnings).
/// Returns None if OK, or Some warning string.
/// When mtime changes but content hash is the same (e.g. `touch`), treat as unchanged
/// to avoid false-positive "file modified" warnings — matching Python's file_state behaviour.
let checkFileRead (fullPath: string) : string option =
    match readState.TryGetValue(fullPath) with
    | false, _ ->
        Some "Warning: file has not been read yet. Read it first with read_file before editing."
    | true, record ->
        try
            let currentMtime = (File.GetLastWriteTimeUtc(fullPath)).Ticks
            if currentMtime <> record.Mtime then
                // mtime changed — check content hash before warning
                let currentHash = hashFile fullPath
                if currentHash <> "" && currentHash = record.ContentHash then
                    // Identical content despite mtime change (e.g. touch) — update mtime silently
                    readState[fullPath] <- { record with Mtime = currentMtime }
                    None
                else
                    Some "Warning: file has changed since it was last read. Re-read it with read_file before editing."
            else
                // mtime unchanged — still check content hash to catch sub-second in-place writes
                // (Python: "mtime unchanged - still check content hash to detect quick modifications")
                let currentHash = hashFile fullPath
                if record.ContentHash <> "" && currentHash <> "" && currentHash <> record.ContentHash then
                    Some "Warning: file has changed since it was last read. Re-read it with read_file before editing."
                else None
        with _ -> None   // if we can't check mtime, don't block the edit

/// Check if a file is unchanged since last read with the same offset/limit (dedup check).
/// Returns true if the read is a duplicate (same content, same range) so we can skip.
let private isUnchangedRead (fullPath: string) (offset: int) (limit: int) : bool =
    match readState.TryGetValue(fullPath) with
    | false, _ -> false
    | true, record ->
        if not record.CanDedup then false
        elif record.Offset <> offset || record.Limit <> limit then false
        else
            try
                let currentMtime = (File.GetLastWriteTimeUtc(fullPath)).Ticks
                if currentMtime = record.Mtime then true
                else
                    // mtime changed — check hash
                    let currentHash = hashFile fullPath
                    if currentHash <> "" && currentHash = record.ContentHash then
                        // Same content despite mtime change; update mtime and allow dedup
                        readState[fullPath] <- { record with Mtime = currentMtime; CanDedup = false }
                        true   // content is identical; caller can skip re-reading
                    else
                        readState[fullPath] <- { record with CanDedup = false }
                        false
            with _ -> false

let private checkWorkspace (workspacePath: string) (requestedPath: string) : Result<string, ToolError> =
    let fullPath = Path.GetFullPath(Path.Combine(workspacePath, requestedPath))
    let workspace = Path.GetFullPath(workspacePath)
    if fullPath.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) then
        Result.Ok fullPath
    else
        Result.Error (WorkspaceViolation requestedPath)

// ── Device path blocking (mirrors Python nanobot's _is_blocked_device) ───
// Prevents read_file from opening device files that could hang or produce
// infinite output (/dev/random, /dev/zero, /dev/stdin, etc.).

let private blockedDevicePaths =
    Set.ofList
        [ "/dev/zero"; "/dev/random"; "/dev/urandom"; "/dev/full"
          "/dev/stdin"; "/dev/stdout"; "/dev/stderr"
          "/dev/tty"; "/dev/console"
          "/dev/fd/0"; "/dev/fd/1"; "/dev/fd/2" ]

/// True when the path (or its canonical form) is a known dangerous device.
/// Checks the explicit block-list, /proc/self/fd/[012], and any /dev/* path.
let private isBlockedDevice (rawPath: string) : bool =
    if Set.contains rawPath blockedDevicePaths then true
    else
        let resolved =
            try IO.Path.GetFullPath(rawPath) with _ -> rawPath
        if Set.contains resolved blockedDevicePaths then true
        elif Regex.IsMatch(rawPath,    @"^/proc/(\d+|self)/fd/[012]$") then true
        elif Regex.IsMatch(resolved,   @"^/proc/(\d+|self)/fd/[012]$") then true
        else resolved.StartsWith("/dev/", StringComparison.Ordinal)

// ── Glob / Grep helpers ───────────────────────────────────────────────────

/// Directories that are always skipped during recursive enumeration.
let private noiseDirs =
    set [ ".git"; "node_modules"; "__pycache__"; ".pytest_cache"
          "obj"; "bin"; ".vs"; ".idea"; "dist"; "build"; "coverage"
          ".next"; ".nuxt"; ".svelte-kit"; "venv"; ".venv"
          // Python-ecosystem noise (mirrors Python's ListDirTool._IGNORE_DIRS)
          ".tox"; ".mypy_cache"; ".ruff_cache"; "htmlcov" ]

/// Convert a glob pattern (supporting ** and ?) to a .NET regex string.
/// ** matches any path segment sequence; * matches within one segment; ? matches one char.
let private globToRegex (pattern: string) : string =
    let sb = StringBuilder("^")
    let mutable i = 0
    while i < pattern.Length do
        match pattern.[i] with
        | '*' when i + 1 < pattern.Length && pattern.[i + 1] = '*' ->
            sb.Append(".*") |> ignore
            i <- i + 2
            if i < pattern.Length && (pattern.[i] = '/' || pattern.[i] = '\\') then i <- i + 1
        | '*' ->
            sb.Append("[^/\\\\]*") |> ignore
            i <- i + 1
        | '?' ->
            sb.Append("[^/\\\\]") |> ignore
            i <- i + 1
        | c ->
            sb.Append(Regex.Escape(string c)) |> ignore
            i <- i + 1
    sb.Append("$").ToString()

/// Walk a directory tree, yielding all files with their mtime.
/// Skips noise directories and directories that escape the root.
/// Walk yielding (path, isDir, mtime). Skips noise directories.
let private walkEntries (includeFiles: bool) (includeDirs: bool) (root: string) : seq<string * bool * float> =
    let rootFull = Path.GetFullPath(root)
    seq {
        let queue = System.Collections.Generic.Queue<string>()
        queue.Enqueue(rootFull)
        while queue.Count > 0 do
            let dir = queue.Dequeue()
            try
                for subDir in Directory.EnumerateDirectories(dir) do
                    let name = match Path.GetFileName(subDir) with null -> "" | n -> n
                    if not (noiseDirs.Contains(name)) &&
                       Path.GetFullPath(subDir).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) then
                        if includeDirs then
                            let mtime = try (DirectoryInfo subDir).LastWriteTimeUtc.ToFileTimeUtc() |> float with _ -> 0.0
                            yield subDir, true, mtime
                        queue.Enqueue(subDir)
                if includeFiles then
                    for file in Directory.EnumerateFiles(dir) do
                        if Path.GetFullPath(file).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) then
                            let mtime = try (FileInfo file).LastWriteTimeUtc.ToFileTimeUtc() |> float with _ -> 0.0
                            yield file, false, mtime
            with _ -> ()
    }

let private walkFiles (root: string) : seq<string * float> =
    walkEntries true false root |> Seq.map (fun (p, _, m) -> p, m)

/// True if the first 4 KB of a file looks like binary content.
/// Mirrors Python's _is_binary: null byte → binary; non-text-char ratio > 20% → binary.
/// Non-text chars: byte < 9 or (13 < byte < 32), i.e. control chars excluding \t (9),
/// \n (10), \v (11), \f (12), \r (13).
let private isBinaryFile (path: string) : bool =
    try
        use fs  = File.OpenRead(path)
        let buf = Array.zeroCreate<byte> 4096
        let n   = fs.Read(buf, 0, buf.Length)
        if n = 0 then false
        else
            let sample = buf.[..n - 1]
            if sample |> Array.exists (fun b -> b = 0uy) then true
            else
                let nonText = sample |> Array.sumBy (fun b ->
                    if b < 9uy || (b > 13uy && b < 32uy) then 1 else 0)
                float nonText / float n > 0.2
    with _ -> false

/// True if the raw bytes look like a PDF (magic bytes: %PDF- = 0x25 0x50 0x44 0x46 0x2D).
/// Mirrors Python's ReadFileTool which dispatches to _read_pdf for .pdf extension.
let private isPdf (data: byte[]) : bool =
    data.Length >= 5
    && data.[0] = 0x25uy && data.[1] = 0x50uy && data.[2] = 0x44uy
    && data.[3] = 0x46uy && data.[4] = 0x2Duy  // %PDF-

/// True if the raw bytes look like an Office Open XML file (ZIP with magic bytes PK).
/// Covers .docx/.xlsx/.pptx (all ZIP-based).
let private isOfficeOpenXml (data: byte[]) : bool =
    data.Length >= 4
    && data.[0] = 0x50uy && data.[1] = 0x4Buy && data.[2] = 0x03uy && data.[3] = 0x04uy

/// Detect image MIME type from magic bytes (mirrors Python's detect_image_mime).
/// Recognises PNG, JPEG, GIF 87a/89a, and WEBP.
let private detectImageMime (data: byte[]) : string option =
    if data.Length >= 8
       && data.[0] = 0x89uy && data.[1] = 0x50uy && data.[2] = 0x4Euy && data.[3] = 0x47uy
       && data.[4] = 0x0Duy && data.[5] = 0x0Auy && data.[6] = 0x1Auy && data.[7] = 0x0Auy then
        Some "image/png"
    elif data.Length >= 3
         && data.[0] = 0xFFuy && data.[1] = 0xD8uy && data.[2] = 0xFFuy then
        Some "image/jpeg"
    elif data.Length >= 6
         && data.[0] = 0x47uy && data.[1] = 0x49uy && data.[2] = 0x46uy
         && (data.[3..5] = [|0x38uy; 0x37uy; 0x61uy|]   // GIF87a
             || data.[3..5] = [|0x38uy; 0x39uy; 0x61uy|]) then  // GIF89a
        Some "image/gif"
    elif data.Length >= 12
         && data.[0] = 0x52uy && data.[1] = 0x49uy && data.[2] = 0x46uy && data.[3] = 0x46uy
         && data.[8] = 0x57uy && data.[9] = 0x45uy && data.[10] = 0x42uy && data.[11] = 0x50uy then
        Some "image/webp"
    else None

// ── Tool specs ────────────────────────────────────────────────────────────

let readFileSpec : ToolSpec = {
    Name            = ToolName "read_file"
    Description     = "Read the contents of a file inside the workspace. Use offset and limit for large files."
    Parameters      = Map.ofList [
        "path",   { Type = JsString; Description = "File path relative to workspace root"; Required = true }
        "offset", { Type = JsNumber; Description = "1-based line number to start reading from (default 1)"; Required = false }
        "limit",  { Type = JsNumber; Description = "Maximum number of lines to read (default 2000)"; Required = false }
    ]
    ConcurrencySafe = true   // read-only
}

let writeFileSpec : ToolSpec = {
    Name            = ToolName "write_file"
    Description     = "Write or overwrite a file inside the workspace."
    Parameters      = Map.ofList [
        "path",    { Type = JsString; Description = "File path relative to workspace root"; Required = true }
        "content", { Type = JsString; Description = "File content to write"; Required = true }
    ]
    ConcurrencySafe = false  // mutates files
}

let listDirSpec : ToolSpec = {
    Name            = ToolName "list_dir"
    Description     = "List files and directories inside a workspace directory. Use recursive=true for a full tree (skips noise dirs). Filtered entries are sorted alphabetically."
    Parameters      = Map.ofList [
        "path",        { Type = JsString;  Description = "Directory path relative to workspace root (default: .)"; Required = false }
        "recursive",   { Type = JsBoolean; Description = "Recursively list all entries (default false)"; Required = false }
        "max_entries", { Type = JsNumber;  Description = "Maximum entries to return (default 500)"; Required = false }
    ]
    ConcurrencySafe = true   // read-only
}

// ── edit_file quote-normalization fallback ────────────────────────────────
// Mirrors Python's _normalize_quotes: curly quotes → straight quotes for
// fuzzy matching. Length-preserving (each curly char maps to the same-width
// straight char), so normalized-content indices are valid in original content.

/// Replace curly/typographic quotes with ASCII equivalents for fuzzy matching.
let private normalizeQuotes (s: string) : string =
    s.Replace('\u2018', '\'').Replace('\u2019', '\'')   // ' '  → '
     .Replace('\u201c', '"' ).Replace('\u201d', '"' )   // " "  → "

/// Leading whitespace (spaces and tabs) of a single line.
let private leadingWs (line: string) : string =
    let content = line.TrimStart(' ', '\t')
    let wsLen   = line.Length - content.Length
    if wsLen = 0 then "" else line.[..wsLen - 1]

/// When a line-trimmed match is found at a different indentation level, adjust
/// new_str so it has the same extra indentation as the actual file text.
/// Mirrors Python's _reindent_like_match.
let private reindentLikeMatch (oldStr: string) (actualLines: string list) (newStr: string) : string =
    let oldLines = oldStr.Split('\n') |> Array.toList
    if oldLines.Length <> actualLines.Length then newStr
    else
    // Comparable pairs: both non-empty and their stripped+normalized content matches
    let comparable =
        List.zip oldLines actualLines
        |> List.filter (fun (o, a) -> o.Trim() <> "" && a.Trim() <> "")
        |> List.filter (fun (o, a) ->
            normalizeQuotes (o.Trim()) = normalizeQuotes (a.Trim()))
    if comparable.IsEmpty then newStr
    else
    let (firstOld, firstActual) = comparable.[0]
    let oldWs    = leadingWs firstOld
    let actualWs = leadingWs firstActual
    if actualWs = oldWs then newStr
    else
    // delta = extra indentation actual has beyond old_str
    let delta =
        if oldWs = "" then actualWs
        elif actualWs.StartsWith(oldWs, StringComparison.Ordinal) then actualWs.[oldWs.Length..]
        else ""   // actual has LESS indentation than old — don't adjust
    if delta = "" then newStr
    else
    newStr.Split('\n')
    |> Array.map (fun line -> if line = "" then line else delta + line)
    |> String.concat "\n"

/// Find all start-indices of sub in s.
let private findAllOccurrences (s: string) (sub: string) : int list =
    let rec loop start acc =
        let i = s.IndexOf(sub, start, StringComparison.Ordinal)
        if i < 0 then List.rev acc
        else loop (i + sub.Length) (i :: acc)
    loop 0 []

/// Convert a 0-based character position in content to a 1-based line number.
let private charPosToLineNum (content: string) (charPos: int) : int =
    let safe = min charPos content.Length
    content.[..safe - 1] |> Seq.filter ((=) '\n') |> Seq.length |> (+) 1

/// Format a list of 1-based line numbers as a preview suffix like " at line 5, 12, 23, ...".
let private lineNumSuffix (lineNums: int list) : string =
    if lineNums.IsEmpty then ""
    else
        let top = lineNums |> List.truncate 3 |> List.map (fun n -> $"line {n}") |> String.concat ", "
        let rest = if lineNums.Length > 3 then ", ..." else ""
        $" at {top}{rest}"

/// Apply span replacements right-to-left so earlier indices stay valid.
/// Delete-line cleanup (mirrors Python's edit_file): when deleting (replacement=""),
/// if the matched text does not end with '\n' but the next character is '\n', consume
/// that extra '\n' so no blank line is left behind.
let private applyReplacementsRtl (content: string) (spanLen: int) (positions: int list) (replacement: string) : string =
    let sb = StringBuilder(content)
    for pos in (positions |> List.sortDescending) do
        let effectiveSpan =
            if replacement = ""
               && spanLen > 0
               && content.[pos + spanLen - 1] <> '\n'   // match text doesn't end with \n
               && pos + spanLen < content.Length
               && content.[pos + spanLen] = '\n' then    // next char IS \n
                spanLen + 1   // consume the trailing newline
            else
                spanLen
        sb.Remove(pos, effectiveSpan) |> ignore
        sb.Insert(pos, replacement) |> ignore
    sb.ToString()

// ── edit_file line-trimmed fallback ──────────────────────────────────────
// Mirrors Python's _find_trim_matches: treats each line as matching when its
// trimmed content equals the trimmed content of the corresponding old_str line.
// Handles indentation drift where the LLM produces old_str with slightly
// different leading/trailing whitespace than what's actually in the file.

/// Try line-trimmed replacement of oldStr in content.
/// Returns Some (updatedContent, matchCount, lineNumbers) if any window matched, None otherwise.
/// lineNumbers is a list of 1-based line numbers where matches were found.
/// Optionally also normalizes quotes in the trimmed comparison (Python's 4th strategy).
let private applyTrimReplacement (content: string) (oldStr: string) (newStr: string) (replaceAll: bool) (normalizeQ: bool) : (string * int * int list) option =
    let oldLines     = oldStr.Split('\n') |> Array.toList
    let n            = oldLines.Length
    let contentLines = content.Split('\n') |> Array.toList
    if n = 0 || contentLines.Length < n then None
    else
    let normalize  = if normalizeQ then normalizeQuotes else id
    let strippedOld = oldLines |> List.map (fun l -> normalize (l.Trim()))
    let matchIndices =
        [ for i in 0 .. contentLines.Length - n do
            let comparable = contentLines |> List.skip i |> List.take n |> List.map (fun l -> normalize (l.Trim()))
            if comparable = strippedOld then yield i ]
    if matchIndices.IsEmpty then None
    else
    let count     = matchIndices.Length
    let lineNums  = matchIndices |> List.map (fun i -> i + 1)   // convert to 1-based
    // Apply one replacement: get actual matched lines, reindent new_str to match their
    // indentation (mirrors Python's _reindent_like_match), then splice in.
    let applyOne (lines: string array) (i: int) =
        let actualLines = lines.[i .. i + n - 1] |> Array.toList
        let adjusted    = reindentLikeMatch oldStr actualLines newStr
        let newLines    = adjusted.Split('\n')
        let before = if i > 0 then lines.[..i-1] else [||]
        let after  = if i + n <= lines.Length - 1 then lines.[i+n..] else [||]
        Array.append (Array.append before newLines) after
    if replaceAll then
        // Replace from last to first so earlier indices stay valid.
        let mutable lines = contentLines |> Array.ofList
        for i in (matchIndices |> List.sortDescending) do
            lines <- applyOne lines i
        Some (lines |> String.concat "\n", count, lineNums)
    else
        let i = matchIndices.[0]
        Some (applyOne (contentLines |> Array.ofList) i |> String.concat "\n", 1, lineNums)

/// Compute a simple character-overlap similarity ratio between two strings.
/// Approximates Python's difflib.SequenceMatcher.ratio() for filename comparison.
/// Returns a value in [0.0, 1.0]; 1.0 means identical (case-insensitive).
let private filenameSimilarity (a: string) (b: string) : float =
    if a.Length = 0 && b.Length = 0 then 1.0
    elif a.Length = 0 || b.Length = 0 then 0.0
    else
        let aLow = a.ToLowerInvariant()
        let bLow = b.ToLowerInvariant()
        // Count characters that appear in both strings (with multiplicity).
        let aFreq = aLow |> Seq.groupBy id |> Seq.map (fun (c, s) -> c, Seq.length s) |> Map.ofSeq
        let bFreq = bLow |> Seq.groupBy id |> Seq.map (fun (c, s) -> c, Seq.length s) |> Map.ofSeq
        let common =
            aFreq |> Map.fold (fun acc c cnt ->
                acc + min cnt (bFreq |> Map.tryFind c |> Option.defaultValue 0)) 0
        2.0 * float common / float (a.Length + b.Length)

/// Build "Did you mean?" suggestions for a missing file path.
/// Scans sibling files in the same directory and returns up to 3 with similarity ≥ 0.6.
/// Mirrors Python's _file_not_found_msg (difflib.get_close_matches, cutoff=0.6).
let private didYouMean (fullPath: string) (relPath: string) : string =
    let dir = Path.GetDirectoryName(fullPath) |> Option.ofObj |> Option.defaultValue ""
    if dir = "" || not (Directory.Exists dir) then ""
    else
        let targetName = Path.GetFileName(relPath) |> Unchecked.nonNull
        let suggestions =
            Directory.GetFiles(dir)
            |> Array.map (fun f -> Path.GetFileName(f) |> Unchecked.nonNull)
            |> Array.choose (fun name ->
                let ratio = filenameSimilarity targetName name
                if ratio >= 0.6 then Some (ratio, name) else None)
            |> Array.sortByDescending fst
            |> Array.truncate 3
            |> Array.map (fun (_, name) ->
                let dirPart = Path.GetDirectoryName(relPath) |> Option.ofObj |> Option.defaultValue ""
                if dirPart = "" then name
                else $"{dirPart.Replace('\\', '/')}/{name}")
        if suggestions.Length = 0 then ""
        else "\nDid you mean: " + (suggestions |> String.concat ", ") + "?"

/// Collapse internal whitespace in each line to single spaces (Python parity).
/// Used for the "whitespace differs" near-match hint.
let private collapseInternalWhitespace (text: string) : string =
    text.Split('\n')
    |> Array.map (fun line ->
        System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " "))
    |> String.concat "\n"

/// Character-frequency similarity ratio for arbitrary text blocks.
/// Approximates Python's difflib.SequenceMatcher.ratio() on short strings.
let private textSimilarity (a: string) (b: string) : float =
    if a.Length = 0 && b.Length = 0 then 1.0
    elif a.Length = 0 || b.Length = 0 then 0.0
    else
        let aFreq = a |> Seq.groupBy id |> Seq.map (fun (c, s) -> c, Seq.length s) |> Map.ofSeq
        let bFreq = b |> Seq.groupBy id |> Seq.map (fun (c, s) -> c, Seq.length s) |> Map.ofSeq
        let common =
            aFreq |> Map.fold (fun acc c cnt ->
                acc + min cnt (bFreq |> Map.tryFind c |> Option.defaultValue 0)) 0
        2.0 * float common / float (a.Length + b.Length)

/// Find the best matching window of lines in content for old_str.
/// Returns (ratio, 0-based startLine, windowLines).
/// Mirrors Python's _best_window sliding-window approach.
let private bestWindow (oldStr: string) (content: string) : float * int * string list =
    let contentLines = content.Split('\n') |> Array.toList
    let window       = max 1 (oldStr.Split('\n').Length)
    let mutable bestRatio = -1.0
    let mutable bestStart = 0
    let mutable bestLines : string list = []
    for i in 0 .. max 0 (contentLines.Length - window) do
        let slice    = contentLines |> List.skip i |> List.truncate window
        let ratio    = textSimilarity oldStr (slice |> String.concat "\n")
        if ratio > bestRatio then
            bestRatio <- ratio
            bestStart <- i
            bestLines <- slice
    bestRatio, bestStart, bestLines

/// Generate a diagnostic hint when all 4 strategies fail to find old_str.
/// Mirrors Python's _not_found_msg: best-window near-match diff + _diagnose_near_match hints.
let private diagnoseNoMatch (oldStr: string) (content: string) : string =
    let norm = oldStr.Replace("\r\n", "\n")
    let con  = content.Replace("\r\n", "\n")
    // Near-match hints (Python's _diagnose_near_match)
    let hints = ResizeArray<string>()
    if con.IndexOf(norm, StringComparison.OrdinalIgnoreCase) >= 0 then
        hints.Add("letter case differs")
    let colNorm = collapseInternalWhitespace norm
    let colCon  = collapseInternalWhitespace con
    if colCon.Contains(colNorm) && not (con.Contains(norm)) then
        hints.Add("whitespace differs")
    let oldTrimmed = norm.TrimEnd('\n')
    if oldTrimmed <> norm && con.Contains(oldTrimmed, StringComparison.Ordinal) then
        hints.Add("trailing newline in old_str")
    if (normalizeQuotes con).Contains(normalizeQuotes norm) && not (con.Contains(norm)) then
        hints.Add("quote style differs")
    // Best-window check
    let ratio, startLine, windowLines = bestWindow norm con
    if ratio > 0.5 then
        let hintText =
            if hints.Count > 0 then "\nPossible cause: " + (hints |> Seq.toList |> String.concat ", ") + "."
            else ""
        let oldLines = norm.Split('\n')
        let diffLines =
            [ yield "--- old_str (provided)"
              yield $"+++ file content (line {startLine + 1})"
              for l in oldLines    do yield $"- {l}"
              for l in windowLines do yield $"+ {l}" ]
        let diffText = diffLines |> String.concat "\n"
        $"Best match ({ratio:P0} similar) at line {startLine + 1}:{hintText}\n{diffText}"
    elif hints.Count > 0 then
        let hintList = hints |> Seq.toList |> String.concat ", "
        $"Possible cause: {hintList}. Copy the exact text from read_file and try again."
    else
        "No similar text found. Verify the file content with read_file."

let editFileSpec : ToolSpec = {
    Name            = ToolName "edit_file"
    Description     = "Replace a specific substring in a file. If old_str appears more than once, " +
                      "provide more context to make it unique or set replace_all=true. " +
                      "If old_str is empty and the file does not exist, creates the file with new_str content."
    Parameters      = Map.ofList [
        "path",        { Type = JsString;  Description = "File path relative to workspace root"; Required = true }
        "old_str",     { Type = JsString;  Description = "Exact string to find and replace (empty to create a new file)"; Required = true }
        "new_str",     { Type = JsString;  Description = "Replacement string"; Required = true }
        "replace_all", { Type = JsBoolean; Description = "Replace all occurrences instead of requiring uniqueness (default false)"; Required = false }
    ]
    ConcurrencySafe = false  // mutates files
}

// ── Implementations ───────────────────────────────────────────────────────

let readFile (workspacePath: string) (maxReadChars: int) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "path" args with
        | Error e -> return ToolFailure e
        | Ok relPath ->
            // Device-path guard (mirrors Python's _is_blocked_device check):
            // block paths that could hang or produce infinite output before
            // we even resolve them against the workspace.
            if isBlockedDevice relPath then
                return ToolFailure (ExecutionFailed $"Reading {relPath} is blocked (device path that could hang or produce infinite output).")
            else
            match checkWorkspace workspacePath relPath with
            | Error e -> return ToolFailure e
            | Ok fullPath ->
                if isBlockedDevice fullPath then
                    return ToolFailure (ExecutionFailed $"Reading {fullPath} is blocked (device path that could hang or produce infinite output).")
                else
                // Reject directories: mirrors Python's "if not fp.is_file()".
                if Directory.Exists fullPath then
                    return ToolFailure (ExecutionFailed $"Not a file: {relPath}")
                else
                let offset = tryIntArg "offset" args |> Option.defaultValue 1 |> max 1
                let limit  = tryIntArg "limit"  args |> Option.defaultValue 2000 |> max 1
                try
                    // Dedup: if the same range of the same unchanged file was already read,
                    // return a short stub instead of repeating the full content.
                    if isUnchangedRead fullPath offset limit then
                        return ToolSuccess $"(File {relPath} is unchanged since last read — same content as before.)"
                    else
                    // Read raw bytes first — mirrors Python's fp.read_bytes() before decode.
                    // This lets us detect empty files, image MIME types, and binary content
                    // before attempting UTF-8 decode (Python parity).
                    let! rawBytes = File.ReadAllBytesAsync(fullPath) |> Async.AwaitTask
                    if rawBytes.Length = 0 then
                        return ToolSuccess $"(Empty file: {relPath})"
                    else
                    // Detect PDF by magic bytes (mirrors Python's _read_pdf dispatch).
                    // Python uses pymupdf; F# has no PDF library dependency, so we return
                    // a descriptive stub rather than silently failing with "binary file".
                    if isPdf rawBytes then
                        let ext = (Path.GetExtension(relPath) |> Unchecked.nonNull).ToLowerInvariant()
                        return ToolSuccess $"(PDF file: {relPath}. PDF text extraction is not supported in this build. Convert to text with a PDF tool or use a PDF reader.)"
                    else
                    // Detect Office Open XML by ZIP magic bytes (.docx/.xlsx/.pptx).
                    if isOfficeOpenXml rawBytes then
                        let ext = (Path.GetExtension(relPath) |> Unchecked.nonNull).ToLowerInvariant()
                        let docType =
                            match ext with
                            | ".docx" -> "Word document"
                            | ".xlsx" -> "Excel workbook"
                            | ".pptx" -> "PowerPoint presentation"
                            | _       -> "Office Open XML document"
                        return ToolSuccess $"({docType}: {relPath}. Binary Office format — text extraction not supported. Export to plain text or CSV first.)"
                    else
                    // Detect image by magic bytes (mirrors Python's detect_image_mime).
                    // F# ToolResult is string-only so we return a descriptive text message
                    // rather than base64 image blocks (Python returns native image blocks).
                    match detectImageMime rawBytes with
                    | Some mime ->
                        return ToolSuccess $"(Image file: {relPath} — MIME: {mime}. Use a multimodal request to view images.)"
                    | None ->
                    // Attempt strict UTF-8 decode (mirrors Python's raw.decode("utf-8")).
                    // Build a custom UTF-8 Encoding with DecoderExceptionFallback so invalid
                    // byte sequences raise DecoderFallbackException rather than silently
                    // substituting U+FFFD replacement characters.
                    let strictUtf8 = UTF8Encoding(false, true)   // throwOnInvalidBytes=true
                    let textResult =
                        try
                            Result.Ok (strictUtf8.GetString(rawBytes))
                        with :? DecoderFallbackException ->
                            Result.Error ()
                    match textResult with
                    | Result.Error () ->
                        return ToolSuccess $"Error: Cannot read binary file {relPath} (MIME: unknown). Only UTF-8 text and images are supported."
                    | Result.Ok rawText ->
                    // Normalize CRLF → LF (mirrors Python's replace("\r\n","\n") before splitlines).
                    let content = rawText.Replace("\r\n", "\n")
                    // Split into lines (trailing newline produces an empty trailing element; drop it
                    // so "a\nb\n".Split('\n') = ["a";"b";""] → 3 not 3 logical lines).
                    let allLines =
                        let arr = content.Split('\n')
                        // If last element is empty due to trailing newline, treat it as EOF marker.
                        if arr.Length > 0 && arr.[arr.Length - 1] = "" then arr.[..arr.Length - 2]
                        else arr
                    let total   = allLines.Length
                    if total = 0 then
                        return ToolSuccess $"(Empty file: {relPath})"
                    elif offset > total then
                        return ToolFailure (ExecutionFailed $"offset {offset} is beyond end of file ({total} lines)")
                    else
                        let startIdx = offset - 1   // 1-based → 0-based
                        let endIdx   = min (startIdx + limit - 1) (total - 1)
                        // Format lines as "N| content" (mirrors Python: f"{n}| {line}")
                        let numbered =
                            allLines.[startIdx..endIdx]
                            |> Array.mapi (fun i line -> $"{offset + i}| {line}")
                        let joined = numbered |> String.concat "\n"
                        let result =
                            if joined.Length > maxReadChars then
                                joined.[..maxReadChars - 1] + $"\n\n(truncated at {maxReadChars} chars)"
                            else joined
                        let note =
                            if endIdx < total - 1 then
                                $"\n\n(Showing lines {offset}–{endIdx + 1} of {total}. Use offset={endIdx + 2} to continue.)"
                            else
                                $"\n\n(End of file — {total} lines total)"
                        recordFileRead fullPath offset limit   // track read for dedup + edit warnings
                        return ToolSuccess (result + note)
                with
                | :? FileNotFoundException -> return ToolFailure (ExecutionFailed $"File not found: {relPath}")
                | ex -> return ToolFailure (ExecutionFailed ex.Message)
    }

let writeFile (workspacePath: string) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "path" args, requireStringArg "content" args with
        | Error e, _ | _, Error e -> return ToolFailure e
        | Ok relPath, Ok content ->
            match checkWorkspace workspacePath relPath with
            | Error e -> return ToolFailure e
            | Ok fullPath ->
                try
                    let dir =
                        match Path.GetDirectoryName(fullPath) with
                        | null -> fullPath
                        | d    -> d
                    if not (Directory.Exists dir) then
                        Directory.CreateDirectory(dir) |> ignore
                    do! File.WriteAllTextAsync(fullPath, content) |> Async.AwaitTask
                    recordFileWrite fullPath
                    return ToolSuccess $"Written {content.Length} characters to {relPath}"
                with ex -> return ToolFailure (ExecutionFailed ex.Message)
    }

let listDir (workspacePath: string) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        let relPath   = tryStringArg "path" args |> Option.defaultValue "."
        let recursive = tryBoolArg  "recursive"   args |> Option.defaultValue false
        let maxEntries = tryIntArg  "max_entries" args |> Option.defaultValue 500 |> max 1
        match checkWorkspace workspacePath relPath with
        | Error e -> return ToolFailure e
        | Ok fullPath ->
            try
                if File.Exists fullPath then
                    return ToolFailure (ExecutionFailed $"Not a directory: {relPath}")
                elif not (Directory.Exists fullPath) then
                    return ToolFailure (ExecutionFailed $"Directory not found: {relPath}")
                else
                    let items = System.Collections.Generic.List<string>()
                    let mutable total = 0
                    if recursive then
                        // Recursive walk, skipping noise dirs
                        let allEntries =
                            walkEntries true true fullPath
                            |> Seq.map (fun (p, isDir, _) ->
                                let rel = Path.GetRelativePath(fullPath, p).Replace('\\', '/')
                                if isDir then rel + "/" else rel)
                            |> Seq.sort
                        for entry in allEntries do
                            total <- total + 1
                            if items.Count < maxEntries then items.Add(entry)
                    else
                        // Non-recursive: only immediate children, sorted
                        let children =
                            Directory.GetFileSystemEntries(fullPath)
                            |> Array.filter (fun p ->
                                let name = match Path.GetFileName(p) with null -> "" | n -> n
                                not (noiseDirs.Contains(name)))
                            |> Array.sort
                        for p in children do
                            total <- total + 1
                            if items.Count < maxEntries then
                                let name = match Path.GetFileName(p) with null -> p | n -> n
                                // Mirrors Python list_dir non-recursive: emoji prefix for dirs/files
                                let entry = if Directory.Exists p then $"📁 {name}" else $"📄 {name}"
                                items.Add(entry)
                    if total = 0 then
                        return ToolSuccess $"Directory {relPath} is empty"
                    else
                        let result = String.concat "\n" items
                        let note   =
                            if total > maxEntries then
                                $"\n\n(truncated, showing first {maxEntries} of {total} entries)"
                            else ""
                        return ToolSuccess (result + note)
            with ex -> return ToolFailure (ExecutionFailed ex.Message)
    }

let editFile (workspacePath: string) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "path" args,
              requireStringArg "old_str" args,
              requireStringArg "new_str" args with
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> return ToolFailure e
        | Ok relPath, Ok oldStr, Ok newStr ->
            if relPath.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) then
                return ToolSuccess
                    "Error: This is a Jupyter notebook. Use the notebook_edit tool instead of edit_file."
            else
            match checkWorkspace workspacePath relPath with
            | Error e -> return ToolFailure e
            | Ok fullPath ->
                let replaceAll = tryBoolArg "replace_all" args |> Option.defaultValue false
                try
                    // File-size protection: reject files larger than 1 GiB (mirrors Python).
                    // Avoids loading enormous files entirely into memory for edits.
                    let sizeCheck =
                        if File.Exists(fullPath) then
                            let sizeBytesL = FileInfo(fullPath).Length
                            let maxSizeL   = 1_073_741_824L   // 1 GiB
                            if sizeBytesL > maxSizeL then
                                let gib = float sizeBytesL / (1024.0 * 1024.0 * 1024.0)
                                Some $"File too large to edit ({gib:F1} GiB). Maximum is 1 GiB."
                            else None
                        else None
                    if sizeCheck.IsSome then
                        return ToolFailure (ExecutionFailed sizeCheck.Value)
                    else
                    // Create-file semantics: old_str="" and file doesn't exist → create
                    if oldStr = "" then
                        if not (File.Exists fullPath) then
                            let dir =
                                match Path.GetDirectoryName(fullPath) with
                                | null -> fullPath
                                | d    -> d
                            if not (Directory.Exists dir) then
                                Directory.CreateDirectory(dir) |> ignore
                            do! File.WriteAllTextAsync(fullPath, newStr) |> Async.AwaitTask
                            recordFileWrite fullPath
                            return ToolSuccess $"Created {relPath}"
                        else
                            let! existing = File.ReadAllTextAsync(fullPath) |> Async.AwaitTask
                            if existing.Trim() <> "" then
                                return ToolFailure (ExecutionFailed $"Cannot create file — {relPath} already exists and is not empty.")
                            else
                                do! File.WriteAllTextAsync(fullPath, newStr) |> Async.AwaitTask
                                recordFileWrite fullPath
                                return ToolSuccess $"Written to {relPath}"
                    else
                        // Warn if the file wasn't read first or has changed since the last read.
                        let readWarning = checkFileRead fullPath
                        let prefix = match readWarning with Some w -> w + "\n" | None -> ""
                        // Explicit existence check: Async.AwaitTask wraps FileNotFoundException
                        // in AggregateException, so pattern matching on FileNotFoundException
                        // in the with-handler doesn't fire reliably.
                        if not (File.Exists fullPath) then
                            let suggestion = didYouMean fullPath relPath
                            return ToolFailure (ExecutionFailed $"File not found: {relPath}.{suggestion}")
                        else
                        let! rawContent = File.ReadAllTextAsync(fullPath) |> Async.AwaitTask

                        // ── CRLF detection: normalize for processing, restore before write ─
                        let usesCrlf = rawContent.Contains("\r\n")
                        let content  = if usesCrlf then rawContent.Replace("\r\n", "\n") else rawContent

                        // ── Trailing whitespace stripping (skip for markdown) ──────────────
                        // Mirrors Python's EditFileTool._strip_trailing_ws (skips .md/.mdx).
                        let markdownExt =
                            let ext = (Path.GetExtension(relPath) |> Unchecked.nonNull).ToLowerInvariant()
                            ext = ".md" || ext = ".mdx" || ext = ".markdown"
                        let newStr =
                            if markdownExt then newStr
                            else newStr.Split('\n') |> Array.map (fun l -> l.TrimEnd()) |> String.concat "\n"

                        // ── 4-strategy fallback chain (mirrors Python's _find_matches) ─────
                        // 1. Exact substring match
                        // 2. Line-trimmed match (handles indentation drift)
                        // 3. Line-trimmed + quote-normalized match (combines both)
                        // 4. Quote-only normalization (length-preserving, no line split)

                        let exactPositions = findAllOccurrences content oldStr

                        // Strategies 2&3: line-trimmed (optionally with quote normalization).
                        // Also applies reindentLikeMatch for indentation-drift correction.
                        let trimResult =
                            if exactPositions.IsEmpty then
                                match applyTrimReplacement content oldStr newStr replaceAll false with
                                | Some r -> Some (r, " (line-trimmed match)")
                                | None   ->
                                    match applyTrimReplacement content oldStr newStr replaceAll true with
                                    | Some r -> Some (r, " (line-trimmed + quote-normalized match)")
                                    | None   -> None
                            else None

                        let writeContent (text: string) =
                            let final = if usesCrlf then text.Replace("\n", "\r\n") else text
                            File.WriteAllTextAsync(fullPath, final) |> Async.AwaitTask

                        match trimResult with
                        | Some ((updated, count, lineNums), label) ->
                            if count > 1 && not replaceAll then
                                let hint = lineNumSuffix lineNums
                                return ToolFailure (ExecutionFailed
                                    $"old_str appears {count} times in {relPath}{hint} (line-trimmed). \
Provide more context to make it unique, or set replace_all=true.")
                            else
                                do! writeContent updated
                                recordFileWrite fullPath
                                let plural = if count = 1 then "1 occurrence" else $"{count} occurrence(s)"
                                return ToolSuccess $"{prefix}Replaced {plural} in {relPath}{label}"
                        | None ->

                        // Strategy 1 (exact) or 4 (quote-only normalization)
                        let (positions, matchLabel) =
                            if not exactPositions.IsEmpty then
                                exactPositions, ""
                            else
                                let normContent = normalizeQuotes content
                                let normOld     = normalizeQuotes oldStr
                                findAllOccurrences normContent normOld, " (quote-normalized match)"

                        match positions with
                        | [] ->
                            let hint = diagnoseNoMatch oldStr content
                            return ToolFailure (ExecutionFailed
                                $"old_str not found in {relPath}. {hint}")
                        | _ ->
                            let occurrences = positions.Length
                            let spanLen     = oldStr.Length
                            if occurrences > 1 && not replaceAll then
                                let lineNums  = positions |> List.map (charPosToLineNum content)
                                let hint      = lineNumSuffix lineNums
                                return ToolFailure (ExecutionFailed
                                    $"old_str appears {occurrences} times in {relPath}{hint}. \
Provide more context to make it unique, or set replace_all=true.")
                            elif replaceAll then
                                let updated = applyReplacementsRtl content spanLen positions newStr
                                do! writeContent updated
                                recordFileWrite fullPath
                                return ToolSuccess $"{prefix}Replaced {occurrences} occurrence(s) in {relPath}{matchLabel}"
                            else
                                let updated = applyReplacementsRtl content spanLen positions newStr
                                do! writeContent updated
                                recordFileWrite fullPath
                                return ToolSuccess $"{prefix}Replaced 1 occurrence in {relPath}{matchLabel}"
                with
                | ex -> return ToolFailure (ExecutionFailed ex.Message)
    }

let globSpec : ToolSpec = {
    Name            = ToolName "glob"
    Description     = "Find files or directories matching a glob pattern (supports * and **). Results sorted newest-first. Skips .git, node_modules, __pycache__, etc."
    Parameters      = Map.ofList [
        "pattern",    { Type = JsString; Description = "Glob pattern, e.g. '*.fs' or 'src/**/*.ts'"; Required = true }
        "path",       { Type = JsString; Description = "Directory to search from (default: workspace root)"; Required = false }
        "head_limit", { Type = JsNumber; Description = "Maximum results to return (default 250; 0 = unlimited)"; Required = false }
        "max_results",{ Type = JsNumber; Description = "Legacy alias for head_limit"; Required = false }
        "offset",     { Type = JsNumber; Description = "Skip first N results before applying head_limit (for pagination)"; Required = false }
        "entry_type", { Type = JsEnum ["files"; "dirs"; "both"]
                        Description = "Match files, directories, or both (default: files)"; Required = false }
    ]
    ConcurrencySafe = true   // read-only filesystem scan
}

let grepSpec : ToolSpec = {
    Name            = ToolName "grep"
    Description     = "Search file contents with a regex pattern. output_mode: 'files_with_matches' (default), 'content' (matching lines), 'count'. Use fixed_strings=true to search plain text without regex interpretation. Use type= to filter by file type (e.g. 'py', 'ts', 'fs', 'md', 'json')."
    Parameters      = Map.ofList [
        "pattern",          { Type = JsString;  Description = "Regex or plain-text pattern to search for"; Required = true }
        "path",             { Type = JsString;  Description = "File or directory to search (default: workspace root)"; Required = false }
        "glob",             { Type = JsString;  Description = "Optional glob filter, e.g. '*.fs' or 'src/**/*.ts'"; Required = false }
        "type",             { Type = JsString;  Description = "File type shorthand: 'py', 'ts', 'js', 'fs', 'md', 'json', 'go', 'rs', 'sh', 'yaml', etc."; Required = false }
        "case_insensitive", { Type = JsBoolean; Description = "Case-insensitive search (default false)"; Required = false }
        "fixed_strings",    { Type = JsBoolean; Description = "Treat pattern as plain text, not regex (default false)"; Required = false }
        "output_mode",      { Type = JsEnum ["content"; "files_with_matches"; "count"]
                              Description = "Output mode (default: files_with_matches)"; Required = false }
        "context_before",   { Type = JsNumber;  Description = "Lines of context before each match (content mode only)"; Required = false }
        "context_after",    { Type = JsNumber;  Description = "Lines of context after each match (content mode only)"; Required = false }
        "head_limit",       { Type = JsNumber;  Description = "Maximum results (default 250; 0 = unlimited)"; Required = false }
        "max_results",      { Type = JsNumber;  Description = "Legacy alias for head_limit in files_with_matches or count mode"; Required = false }
        "max_matches",      { Type = JsNumber;  Description = "Legacy alias for head_limit in content mode"; Required = false }
        "offset",           { Type = JsNumber;  Description = "Skip first N results before applying head_limit (for pagination)"; Required = false }
    ]
    ConcurrencySafe = true   // read-only content search
}

// ── Type-driven grep domain types ────────────────────────────────────────
//
// These three types eliminate invalid state that previously leaked as raw
// strings and ints throughout the grep implementation:
//
//   GrepOutputMode  — "files_with_matches"/"content"/"count" as a DU.
//                     FS0025 fires if a new mode is added without handling it.
//                     The previous | _ -> catch-all silently defaulted to Content.
//
//   ResultLimit     — head_limit=0 used to mean "unlimited" (dual semantics
//                     embedded in a raw int).  The DU makes this explicit.
//
//   ContextLines    — ctxBefore/ctxAfter always travel as a pair; bundled here
//                     so the grep inner loop receives a single typed value.

/// Discriminated union for grep output mode.
/// The no-catch-all match in grepFiles gives FS0025 protection.
type private GrepOutputMode =
    | FilesWithMatches
    | Content
    | Count

/// Parse an output_mode string at the arg boundary.
/// Unknown values default to FilesWithMatches (lenient, not a catch-all in matches).
let private parseGrepOutputMode (s: string) : GrepOutputMode =
    match s.Trim().ToLowerInvariant() with
    | "content" -> Content
    | "count"   -> Count
    | _         -> FilesWithMatches

/// Encoded result limit: Limited n (n > 0) or Unlimited (head_limit = 0).
type private ResultLimit =
    | Limited   of n: int
    | Unlimited

module private ResultLimit =
    let ofInt (n: int) : ResultLimit =
        if n <= 0 then Unlimited else Limited n

    /// Apply limit to a list; also returns whether results were truncated.
    let applyList (limit: ResultLimit) (items: 'a list) : 'a list * bool =
        match limit with
        | Unlimited -> items, false
        | Limited n -> List.truncate n items, items.Length > n

    /// Apply limit to a lazy seq (for glob where we may have millions of files).
    let applySeq (limit: ResultLimit) (seq: seq<'a>) : seq<'a> =
        match limit with
        | Unlimited  -> seq
        | Limited n  -> Seq.truncate n seq

    let exceeds (limit: ResultLimit) (count: int) : bool =
        match limit with
        | Unlimited -> false
        | Limited n -> count >= n

    let noteIfTruncated (limit: ResultLimit) (truncated: bool) : string =
        match limit, truncated with
        | Limited n, true -> $"\n\n(showing first {n} results; use head_limit=0 for all)"
        | _               -> ""

/// Context lines bundled as a unit — Before and After always belong together.
type private ContextLines = { Before: int; After: int }

/// Fully validated grep arguments, parsed at the tool boundary.
/// The grep implementation only sees typed data; no string discrimination inside.
type private GrepArgs = {
    Pattern     : Regex          // compiled, never raw string
    RelPath     : string
    GlobRe      : Regex option   // compiled, never raw pattern
    TypeFilter  : string option
    OutputMode  : GrepOutputMode // DU, never string
    Context     : ContextLines   // bundled, never two separate ints
    Limit       : ResultLimit    // DU, never 0-means-unlimited raw int
    Offset      : int            // skip first N results (pagination)
}

/// Parse and validate all grep arguments at the boundary.
/// Returns Ok (args, searchRoot) or Error (first parsing failure).
let private parseGrepArgs
    (workspacePath: string)
    (args: Map<string, JsonElement>)
    : Result<GrepArgs * string, ToolError> =
    match requireStringArg "pattern" args with
    | Error e -> Error e
    | Ok rawPattern ->
        let relPath      = tryStringArg "path" args |> Option.defaultValue "."
        let globFilter   = tryStringArg "glob" args
        let typeFilter   = tryStringArg "type" args
        let caseInsens   = tryBoolArg "case_insensitive" args |> Option.defaultValue false
        let fixedStrings = tryBoolArg "fixed_strings"    args |> Option.defaultValue false
        let outputMode   = tryStringArg "output_mode"    args |> Option.defaultValue "files_with_matches"
                           |> parseGrepOutputMode
        let ctxBefore    = tryIntArg "context_before" args |> Option.defaultValue 0
        let ctxAfter     = tryIntArg "context_after"  args |> Option.defaultValue 0
        // head_limit takes precedence; fall back to legacy max_matches (content) / max_results (other modes)
        let limit        =
            match tryIntArg "head_limit" args with
            | Some n -> n
            | None   ->
                let legacyAlias =
                    match tryStringArg "output_mode" args |> Option.defaultValue "files_with_matches" with
                    | "content" -> tryIntArg "max_matches" args
                    | _         -> tryIntArg "max_results" args
                legacyAlias |> Option.defaultValue 250
            |> ResultLimit.ofInt
        let offset       = tryIntArg "offset"         args |> Option.defaultValue 0 |> max 0

        match checkWorkspace workspacePath relPath with
        | Error e -> Error e
        | Ok searchRoot ->
            let reOpts          = if caseInsens then RegexOptions.IgnoreCase else RegexOptions.None
            let effectivePattern = if fixedStrings then Regex.Escape(rawPattern) else rawPattern
            try
                let regex  = Regex(effectivePattern, reOpts)
                let globRe = globFilter |> Option.map (fun g -> Regex(globToRegex g, RegexOptions.IgnoreCase))
                Ok ({ Pattern    = regex
                      RelPath    = relPath
                      GlobRe     = globRe
                      TypeFilter = typeFilter
                      OutputMode = outputMode
                      Context    = { Before = ctxBefore; After = ctxAfter }
                      Limit      = limit
                      Offset     = offset }, searchRoot)
            with ex ->
                Error (ExecutionFailed $"Invalid regex: {ex.Message}")

// ── File type shorthand → glob patterns ──────────────────────────────────

/// Map from file type shorthand to a set of glob patterns.
/// Matches Python's _TYPE_GLOB_MAP in search.py.
let private typeGlobMap : Map<string, string list> =
    [ "py",       ["*.py"; "*.pyi"]
      "python",   ["*.py"; "*.pyi"]
      "js",       ["*.js"; "*.jsx"; "*.mjs"; "*.cjs"]
      "ts",       ["*.ts"; "*.tsx"; "*.mts"; "*.cts"]
      "tsx",      ["*.tsx"]
      "jsx",      ["*.jsx"]
      "json",     ["*.json"]
      "md",       ["*.md"; "*.mdx"]
      "markdown", ["*.md"; "*.mdx"]
      "go",       ["*.go"]
      "rs",       ["*.rs"]
      "rust",     ["*.rs"]
      "java",     ["*.java"]
      "sh",       ["*.sh"; "*.bash"]
      "yaml",     ["*.yaml"; "*.yml"]
      "yml",      ["*.yaml"; "*.yml"]
      "toml",     ["*.toml"]
      "sql",      ["*.sql"]
      "html",     ["*.html"; "*.htm"]
      "css",      ["*.css"; "*.scss"; "*.sass"]
      "fs",       ["*.fs"; "*.fsi"; "*.fsx"]
      "fsharp",   ["*.fs"; "*.fsi"; "*.fsx"]
      "cs",       ["*.cs"]
      "csharp",   ["*.cs"]
      "rb",       ["*.rb"]
      "ruby",     ["*.rb"]
      "kt",       ["*.kt"; "*.kts"]
      "swift",    ["*.swift"]
      "cpp",      ["*.cpp"; "*.cc"; "*.cxx"; "*.hpp"; "*.h"]
      "c",        ["*.c"; "*.h"] ] |> Map.ofList

/// Check whether a file name matches a type shorthand.
let private matchesFileType (typeName: string) (fileName: string) : bool =
    let key = typeName.Trim().ToLowerInvariant()
    let patterns =
        match typeGlobMap.TryFind key with
        | Some ps -> ps
        | None    -> [ $"*.{key}" ]   // fallback: treat as extension
    patterns |> List.exists (fun pattern ->
        Regex.IsMatch(fileName.ToLowerInvariant(), globToRegex pattern, RegexOptions.IgnoreCase))

// ── Implementations (glob / grep) ─────────────────────────────────────────

let glob (workspacePath: string) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "pattern" args with
        | Error e -> return ToolFailure e
        | Ok pattern ->
            let relPath    = tryStringArg "path" args |> Option.defaultValue "."
            // head_limit takes precedence; max_results is the legacy alias
            let limit      =
                match tryIntArg "head_limit" args with
                | Some n -> n
                | None   -> tryIntArg "max_results" args |> Option.defaultValue 250
                |> ResultLimit.ofInt
            let offset     = tryIntArg "offset" args |> Option.defaultValue 0 |> max 0
            let entryType  = tryStringArg "entry_type" args |> Option.defaultValue "files"
            let inclFiles  = entryType = "files" || entryType = "both"
            let inclDirs   = entryType = "dirs"  || entryType = "both"
            match checkWorkspace workspacePath relPath with
            | Error e -> return ToolFailure e
            | Ok searchRoot ->
                if not (Directory.Exists searchRoot) then
                    return ToolFailure (ExecutionFailed $"Directory not found: {relPath}")
                else
                    try
                        let re   = Regex(globToRegex pattern, RegexOptions.IgnoreCase)
                        let all  =
                            walkEntries inclFiles inclDirs searchRoot
                            |> Seq.choose (fun (path, isDir, mtime) ->
                                let rel = Path.GetRelativePath(searchRoot, path).Replace('\\', '/')
                                if re.IsMatch(rel) then
                                    let display = if isDir then rel + "/" else rel
                                    Some (display, mtime)
                                else None)
                            |> Seq.sortWith (fun (pa, ma) (pb, mb) ->
                                let c = compare mb ma   // descending mtime
                                if c <> 0 then c else compare pa pb)
                            |> Seq.toList
                        // Apply offset pagination before head_limit
                        let paged = if offset > 0 && offset < all.Length then all.[offset..] else all
                        let (matches, truncated) = ResultLimit.applyList limit paged
                        let paths = matches |> List.map fst
                        if paths.IsEmpty then
                            let skipNote = if offset > 0 then $" (after offset={offset})" else ""
                            let entryWord = match entryType with "dirs" -> "directories" | "both" -> "files or directories" | _ -> "files"
                            return ToolSuccess $"No {entryWord} matched pattern '{pattern}'{skipNote}"
                        else
                            let offsetNote = if offset > 0 then $"\n(offset={offset})" else ""
                            return ToolSuccess (String.concat "\n" paths + ResultLimit.noteIfTruncated limit truncated + offsetNote)
                    with ex ->
                        return ToolFailure (ExecutionFailed ex.Message)
    }

let grep (workspacePath: string) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        // All parsing + validation happens here at the boundary.
        // The implementation below only sees typed GrepArgs — no string discrimination.
        match parseGrepArgs workspacePath args with
        | Error e -> return ToolFailure e
        | Ok (ga, searchRoot) ->
            try
                // Collect candidate files, applying glob and type filters
                let files =
                    if File.Exists searchRoot then
                        [ searchRoot, 0.0 ]
                    elif Directory.Exists searchRoot then
                        walkFiles searchRoot
                        |> Seq.filter (fun (path, _) ->
                            let passGlob =
                                match ga.GlobRe with
                                | None    -> true
                                | Some gr ->
                                    let rel = Path.GetRelativePath(searchRoot, path).Replace('\\', '/')
                                    gr.IsMatch(rel)
                            let passType =
                                match ga.TypeFilter with
                                | None   -> true
                                | Some t -> matchesFileType t (Path.GetFileName(path) |> Unchecked.nonNull)
                            passGlob && passType)
                        |> Seq.toList
                    else []

                let resultParts  = System.Collections.Generic.List<string>()
                let mutable count = 0
                let mutable skipped = 0   // track offset skipping

                match ga.OutputMode with
                | FilesWithMatches | Count ->
                    // For these modes: collect ALL matches first, then sort by mtime
                    // (newest first, ties broken by path) — mirrors Python's grep sort.
                    let allMatches = System.Collections.Generic.List<struct(string * float * string)>()
                    for (filePath, mtime) in files do
                        if not (isBinaryFile filePath) then
                            try
                                let lines = File.ReadAllLines(filePath)
                                let rel   = Path.GetRelativePath(searchRoot, filePath).Replace('\\', '/')
                                let disp  = if File.Exists searchRoot then ga.RelPath else rel
                                match ga.OutputMode with
                                | FilesWithMatches ->
                                    if lines |> Array.exists ga.Pattern.IsMatch then
                                        allMatches.Add(struct(disp, mtime, ""))
                                | Count ->
                                    let n = lines |> Array.filter ga.Pattern.IsMatch |> Array.length
                                    if n > 0 then
                                        allMatches.Add(struct(disp, mtime, string n))
                                | Content -> ()   // unreachable; pattern-matched above
                            with _ -> ()
                    // Sort by mtime descending, then by path ascending (Python parity)
                    let sorted =
                        allMatches
                        |> Seq.toArray
                        |> Array.sortWith (fun (struct(pa, ma, _)) (struct(pb, mb, _)) ->
                            let c = compare mb ma   // descending mtime
                            if c <> 0 then c else compare pa pb)
                    for struct(disp, _, extra) in sorted do
                        if not (ResultLimit.exceeds ga.Limit count) then
                            if skipped < ga.Offset then skipped <- skipped + 1
                            else
                                let entry =
                                    match ga.OutputMode with
                                    | Count -> $"{disp}:{extra}"
                                    | _     -> disp
                                resultParts.Add(entry)
                                count <- count + 1

                | Content ->
                    for (filePath, _) in files do
                        if not (ResultLimit.exceeds ga.Limit count) && not (isBinaryFile filePath) then
                            try
                                let lines = File.ReadAllLines(filePath)
                                let rel   = Path.GetRelativePath(searchRoot, filePath).Replace('\\', '/')
                                let disp  = if File.Exists searchRoot then ga.RelPath else rel
                                for lineIdx in 0 .. lines.Length - 1 do
                                    if not (ResultLimit.exceeds ga.Limit count) && ga.Pattern.IsMatch(lines.[lineIdx]) then
                                        if skipped < ga.Offset then skipped <- skipped + 1
                                        else
                                            let startLine = max 0 (lineIdx - ga.Context.Before)
                                            let endLine   = min (lines.Length - 1) (lineIdx + ga.Context.After)
                                            let block = StringBuilder()
                                            block.AppendLine($"{disp}:{lineIdx + 1}") |> ignore
                                            for ctxIdx in startLine .. endLine do
                                                let marker = if ctxIdx = lineIdx then ">" else " "
                                                block.AppendLine($"{marker} {ctxIdx + 1}| {lines.[ctxIdx]}") |> ignore
                                            resultParts.Add(block.ToString().TrimEnd())
                                            count <- count + 1
                            with _ -> ()

                if resultParts.Count = 0 then
                    let skipNote = if skipped > 0 then $" (skipped {skipped} results)" else ""
                    return ToolSuccess $"No matches for '{ga.Pattern}'{skipNote}"
                else
                    // No string comparison here — DU match drives the separator choice
                    let separator =
                        match ga.OutputMode with
                        | Content -> "\n\n"
                        | FilesWithMatches | Count -> "\n"
                    let offsetNote = if ga.Offset > 0 then $"\n(offset={ga.Offset}, skipped {skipped} results)" else ""
                    let note = ResultLimit.noteIfTruncated ga.Limit (ResultLimit.exceeds ga.Limit count)
                    return ToolSuccess (String.concat separator resultParts + note + offsetNote)
            with ex ->
                return ToolFailure (ExecutionFailed ex.Message)
    }

// ── Tool registry ─────────────────────────────────────────────────────────

/// All file-system tools as a list of (spec, execute) pairs.
let allTools (workspacePath: string) (maxReadChars: int)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ readFileSpec,  readFile  workspacePath maxReadChars
      writeFileSpec, writeFile workspacePath
      listDirSpec,   listDir   workspacePath
      editFileSpec,  editFile  workspacePath
      globSpec,      glob      workspacePath
      grepSpec,      grep      workspacePath ]
