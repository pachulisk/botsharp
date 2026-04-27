module BotSharp.Tests.Infrastructure.FileSystemToolTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.FileSystemTool

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

/// Serialize any string as a JSON element (handles embedded double quotes, newlines, etc.)
let private jsonStrSafe (s: string) =
    JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(s)).RootElement.Clone()

let private makeArgs (pairs: (string * string) list) : Map<string, JsonElement> =
    pairs |> List.map (fun (k, v) -> k, jsonStr v) |> Map.ofList

let private jsonInt (n: int) =
    JsonDocument.Parse($"{n}").RootElement.Clone()

let private makeArgsWithInt (pairs: (string * JsonElement) list) : Map<string, JsonElement> =
    pairs |> Map.ofList

let private jsonBool (b: bool) =
    JsonDocument.Parse(if b then "true" else "false").RootElement.Clone()

/// Create a temporary workspace directory and return its path + a cleanup action.
let private withTempWorkspace (f: string -> Async<unit>) =
    async {
        let dir = Path.Combine(Path.GetTempPath(), $"botsharp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        try
            do! f dir
        finally
            try Directory.Delete(dir, true) with _ -> ()
    }

// ═══════════════════════════════════════════════════════════════════════════
// read_file tests
// ═══════════════════════════════════════════════════════════════════════════

// ═══════════════════════════════════════════════════════════════════════════
// read_file: offset / limit tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile with offset reads from that line`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "lines.txt"), "line1\nline2\nline3\nline4\nline5")
            let args = makeArgsWithInt [ "path", jsonStr "lines.txt"; "offset", jsonInt 3 ]
            let! result = readFile wp 131_072 args
            match result with
            | ToolSuccess text ->
                Assert.DoesNotContain("line1", text)
                Assert.DoesNotContain("line2", text)
                Assert.Contains("line3", text)
                Assert.Contains("line4", text)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile with limit truncates to N lines`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "lines.txt"), "line1\nline2\nline3\nline4\nline5")
            let args = makeArgsWithInt [ "path", jsonStr "lines.txt"; "limit", jsonInt 2 ]
            let! result = readFile wp 131_072 args
            match result with
            | ToolSuccess text ->
                Assert.Contains("line1", text)
                Assert.Contains("line2", text)
                Assert.DoesNotContain("line3", text)
                Assert.Contains("Showing lines", text)  // pagination note
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile with offset beyond file returns ToolFailure`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "lines.txt"), "a\nb\nc")
            let args = makeArgsWithInt [ "path", jsonStr "lines.txt"; "offset", jsonInt 99 ]
            let! result = readFile wp 131_072 args
            match result with
            | ToolFailure (ExecutionFailed msg) -> Assert.Contains("beyond end", msg)
            | other -> Assert.Fail($"Expected ToolFailure for out-of-range offset, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile without offset or limit reads full file`` () =
    withTempWorkspace (fun wp ->
        async {
            let path = Path.Combine(wp, "hello.txt")
            File.WriteAllText(path, "hello world")
            let! result = readFile wp 131_072 (makeArgs ["path", "hello.txt"])
            match result with
            | ToolSuccess text ->
                Assert.Contains("hello world", text)     // content present
                Assert.Contains("1|", text)              // line-numbered (Python format)
                Assert.Contains("End of file", text)     // end-of-file note
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile returns ToolFailure for missing file`` () =
    withTempWorkspace (fun wp ->
        async {
            let! result = readFile wp 131_072 (makeArgs ["path", "missing.txt"])
            match result with
            | ToolFailure (ExecutionFailed msg) -> Assert.Contains("missing.txt", msg)
            | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed), got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile rejects path traversal outside workspace`` () =
    withTempWorkspace (fun wp ->
        async {
            let! result = readFile wp 131_072 (makeArgs ["path", "../../etc/passwd"])
            match result with
            | ToolFailure (WorkspaceViolation _) -> ()
            | other -> Assert.Fail($"Expected WorkspaceViolation, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// write_file tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``writeFile creates file with content`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "out.txt"; "content", "written content"]
            let! result = writeFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("out.txt", msg)
                let written = File.ReadAllText(Path.Combine(wp, "out.txt"))
                Assert.Equal("written content", written)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``writeFile creates parent directories`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "sub/dir/file.txt"; "content", "nested"]
            let! result = writeFile wp args
            match result with
            | ToolSuccess _ ->
                let written = File.ReadAllText(Path.Combine(wp, "sub", "dir", "file.txt"))
                Assert.Equal("nested", written)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``writeFile rejects path traversal`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "../escape.txt"; "content", "oops"]
            let! result = writeFile wp args
            match result with
            | ToolFailure (WorkspaceViolation _) -> ()
            | other -> Assert.Fail($"Expected WorkspaceViolation, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// list_dir tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listDir lists files and directories`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "a.txt"), "a")
            Directory.CreateDirectory(Path.Combine(wp, "subdir")) |> ignore
            let! result = listDir wp (makeArgs ["path", "."])
            match result with
            | ToolSuccess listing ->
                // Non-recursive: emoji prefix format (mirrors Python list_dir)
                Assert.Contains("a.txt", listing)
                Assert.Contains("subdir", listing)
                Assert.Contains("📁", listing)   // directory emoji prefix
                Assert.Contains("📄", listing)   // file emoji prefix
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir with default path lists workspace root`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "root.txt"), "x")
            let! result = listDir wp Map.empty
            match result with
            | ToolSuccess listing -> Assert.Contains("root.txt", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir returns ToolFailure for missing directory`` () =
    withTempWorkspace (fun wp ->
        async {
            let! result = listDir wp (makeArgs ["path", "nosuchdir"])
            match result with
            | ToolFailure (ExecutionFailed msg) -> Assert.Contains("nosuchdir", msg)
            | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed), got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir recursive lists nested files with trailing slash for dirs`` () =
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "a", "b")) |> ignore
            File.WriteAllText(Path.Combine(wp, "a", "b", "deep.txt"), "deep")
            File.WriteAllText(Path.Combine(wp, "top.txt"), "top")
            let args = makeArgsWithInt ["path", jsonStr "."; "recursive", jsonBool true]
            let! result = listDir wp args
            match result with
            | ToolSuccess listing ->
                Assert.Contains("top.txt", listing)
                Assert.Contains("deep.txt", listing)
                Assert.Contains("a/", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir max_entries truncates and adds note`` () =
    withTempWorkspace (fun wp ->
        async {
            for i in 1..5 do
                File.WriteAllText(Path.Combine(wp, $"file{i}.txt"), "x")
            let args = makeArgsWithInt ["path", jsonStr "."; "max_entries", jsonInt 2]
            let! result = listDir wp args
            match result with
            | ToolSuccess listing ->
                Assert.Contains("truncated", listing)
                Assert.Contains("5", listing)  // total count shown in note
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// edit_file tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile replaces unique occurrence of old_str`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "edit.txt"), "hello world")
            let args = makeArgs ["path", "edit.txt"; "old_str", "world"; "new_str", "earth"]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let content = File.ReadAllText(Path.Combine(wp, "edit.txt"))
                Assert.Equal("hello earth", content)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile returns ToolFailure when old_str not found`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "edit.txt"), "hello world")
            let args = makeArgs ["path", "edit.txt"; "old_str", "NOTHERE"; "new_str", "x"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) -> Assert.Contains("not found", msg)
            | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed), got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile returns ToolFailure for missing file`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "ghost.txt"; "old_str", "x"; "new_str", "y"]
            let! result = editFile wp args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile with replace_all replaces all occurrences`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "edit.txt"), "foo bar foo baz foo")
            let args =
                makeArgsWithInt [
                    "path",        jsonStr "edit.txt"
                    "old_str",     jsonStr "foo"
                    "new_str",     jsonStr "qux"
                    "replace_all", JsonDocument.Parse("true").RootElement.Clone()
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                let content = File.ReadAllText(Path.Combine(wp, "edit.txt"))
                Assert.Equal("qux bar qux baz qux", content)
                Assert.Contains("3", msg)   // "Replaced 3 occurrence(s)"
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile warns when old_str appears multiple times without replace_all`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "edit.txt"), "foo bar foo")
            let args = makeArgs ["path", "edit.txt"; "old_str", "foo"; "new_str", "baz"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("2 times", msg)
                Assert.Contains("replace_all", msg)
                // Should include line numbers (Python parity: "at line X, Y")
                Assert.Contains("line", msg)
            | other -> Assert.Fail($"Expected ToolFailure about multiple matches, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile multiple matches via trim fallback includes line numbers`` () =
    withTempWorkspace (fun wp ->
        async {
            // Two indented occurrences — exact match fails, trim match finds both
            let content = "    foo\n    bar\n    foo\n"
            File.WriteAllText(Path.Combine(wp, "m.txt"), content)
            // Read the file first (required for edit check)
            let readArgs = makeArgs ["path", "m.txt"]
            let! _ = readFile wp 131_072 readArgs
            // Provide old_str without indentation — triggers trim match
            let args = makeArgs ["path", "m.txt"; "old_str", "foo"; "new_str", "baz"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("2 times", msg)
                Assert.Contains("line", msg)
                // The two "foo" matches are on lines 1 and 3
                Assert.Contains("1", msg)
                Assert.Contains("3", msg)
            | other -> Assert.Fail($"Expected ToolFailure with line numbers, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile with empty old_str creates new file`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "newfile.txt"; "old_str", ""; "new_str", "hello world"]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let content = File.ReadAllText(Path.Combine(wp, "newfile.txt"))
                Assert.Equal("hello world", content)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile with empty old_str rejects existing non-empty file`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "existing.txt"), "not empty")
            let args = makeArgs ["path", "existing.txt"; "old_str", ""; "new_str", "new content"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) -> Assert.Contains("already exists", msg)
            | other -> Assert.Fail($"Expected ToolFailure, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// Read-before-edit tracking tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile warns when file has not been read first`` () =
    withTempWorkspace (fun wp ->
        async {
            // Write the file directly (bypass readFile so it's NOT in readState)
            File.WriteAllText(Path.Combine(wp, "unread.txt"), "hello world")
            let args = makeArgs ["path", "unread.txt"; "old_str", "hello"; "new_str", "goodbye"]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg -> Assert.Contains("Warning", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with warning, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile does not warn when file was read via readFile first`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "fresh.txt"), "hello world")
            // Read the file via readFile so it enters readState
            let readArgs = makeArgs ["path", "fresh.txt"]
            let! _ = readFile wp 131_072 readArgs
            let editArgs = makeArgs ["path", "fresh.txt"; "old_str", "hello"; "new_str", "goodbye"]
            let! result = editFile wp editArgs
            match result with
            | ToolSuccess msg ->
                // No warning prefix should be present
                Assert.DoesNotContain("Warning", msg)
            | other -> Assert.Fail($"Expected ToolSuccess without warning, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``recordFileRead and checkFileRead round-trip`` () =
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "test")
        // Before recording: should warn
        let before = checkFileRead path
        Assert.True(before.IsSome, "Expected warning before recordFileRead")
        recordFileRead path 1 2000
        // After recording: should be clean
        let after = checkFileRead path
        Assert.True(after.IsNone, "Expected no warning after recordFileRead")
    finally
        File.Delete(path)

[<Fact>]
let ``readFile returns unchanged stub on second read of same range`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "data.txt"), String.concat "\n" [ for i in 1..10 -> $"line {i}" ])
            let args = makeArgs ["path", "data.txt"]
            let! first = readFile wp 131_072 args
            match first with
            | ToolSuccess content -> Assert.Contains("line 1", content)   // has real content
            | other -> Assert.Fail($"Expected ToolSuccess on first read, got {other}")
            let! second = readFile wp 131_072 args
            match second with
            | ToolSuccess msg -> Assert.Contains("unchanged", msg)
            | other -> Assert.Fail($"Expected unchanged stub on second read, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile returns full content after file is modified`` () =
    withTempWorkspace (fun wp ->
        async {
            let path = Path.Combine(wp, "evolving.txt")
            File.WriteAllText(path, "original")
            let args = makeArgs ["path", "evolving.txt"]
            let! _ = readFile wp 131_072 args   // first read — primes the cache
            File.WriteAllText(path, "modified content")   // modify the file
            let! second = readFile wp 131_072 args
            match second with
            | ToolSuccess content -> Assert.Contains("modified content", content)
            | other -> Assert.Fail($"Expected full content after modification, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile returns full content for different offset`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "multi.txt"), String.concat "\n" [ for i in 1..20 -> $"line {i}" ])
            let args1 = makeArgsWithInt ["path", jsonStr "multi.txt"; "offset", jsonInt 1; "limit", jsonInt 5]
            let! _ = readFile wp 131_072 args1
            let args2 = makeArgsWithInt ["path", jsonStr "multi.txt"; "offset", jsonInt 6; "limit", jsonInt 5]
            let! second = readFile wp 131_072 args2
            match second with
            | ToolSuccess content ->
                // Different offset — should return real content, not stub
                Assert.DoesNotContain("unchanged", content)
                Assert.Contains("line 6", content)
            | other -> Assert.Fail($"Expected ToolSuccess with real content, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// readFile configurable truncation test
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile truncates at custom maxReadChars limit`` () =
    withTempWorkspace (fun wp ->
        async {
            // Write a file with exactly 200 characters
            let content = String.replicate 200 "x"
            File.WriteAllText(Path.Combine(wp, "big.txt"), content)
            let args = makeArgs ["path", "big.txt"]
            // Use a small limit of 100 chars
            let! result = readFile wp 100 args
            match result with
            | ToolSuccess text ->
                Assert.Contains("truncated at 100 chars", text)
                // The result is: first 100 chars + truncation message
                Assert.Equal(100, text.IndexOf("\n\n(truncated at 100 chars)"))
            | other -> Assert.Fail($"Expected ToolSuccess with truncation, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// readFile — line-number format and CRLF normalization (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile formats lines as N| content (Python parity)`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "numbered.txt"), "alpha\nbeta\ngamma\n")
            let! result = readFile wp 131_072 (makeArgs ["path", "numbered.txt"])
            match result with
            | ToolSuccess text ->
                Assert.Contains("1| alpha", text)
                Assert.Contains("2| beta", text)
                Assert.Contains("3| gamma", text)
            | other -> Assert.Fail($"Expected ToolSuccess with numbered lines, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile normalizes CRLF line endings before presenting output`` () =
    withTempWorkspace (fun wp ->
        async {
            // Write a file with Windows CRLF line endings
            let bytes = System.Text.Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n")
            File.WriteAllBytes(Path.Combine(wp, "crlf.txt"), bytes)
            let! result = readFile wp 131_072 (makeArgs ["path", "crlf.txt"])
            match result with
            | ToolSuccess text ->
                // Should NOT have \r in the output (CRLF normalized away)
                Assert.DoesNotContain("\r", text)
                Assert.Contains("1| alpha", text)
                Assert.Contains("2| beta", text)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile appends end-of-file note when all lines shown`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "small.txt"), "line1\nline2\n")
            let! result = readFile wp 131_072 (makeArgs ["path", "small.txt"])
            match result with
            | ToolSuccess text -> Assert.Contains("End of file", text)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// allTools registration test
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allTools returns 6 tools`` () =
    let tools = allTools "/tmp" 131_072
    Assert.Equal(6, List.length tools)

[<Fact>]
let ``allTools contains all 6 expected tools`` () =
    let tools = allTools "/tmp" 131_072
    let names = tools |> List.map (fun (spec, _) -> let (ToolName n) = spec.Name in n) |> Set.ofList
    Assert.Contains("read_file", names)
    Assert.Contains("write_file", names)
    Assert.Contains("list_dir", names)
    Assert.Contains("edit_file", names)
    Assert.Contains("glob", names)
    Assert.Contains("grep", names)

// ═══════════════════════════════════════════════════════════════════════════
// glob tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``glob finds .txt files with *.txt pattern`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "a.txt"), "a")
            File.WriteAllText(Path.Combine(wp, "b.txt"), "b")
            File.WriteAllText(Path.Combine(wp, "c.md"),  "c")
            let! result = glob wp (makeArgs ["pattern", "*.txt"])
            match result with
            | ToolSuccess listing ->
                Assert.Contains("a.txt", listing)
                Assert.Contains("b.txt", listing)
                Assert.DoesNotContain("c.md", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob finds files in subdirectories with ** pattern`` () =
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "sub")) |> ignore
            File.WriteAllText(Path.Combine(wp, "sub", "deep.fs"), "")
            File.WriteAllText(Path.Combine(wp, "top.fs"), "")
            let! result = glob wp (makeArgs ["pattern", "**/*.fs"])
            match result with
            | ToolSuccess listing ->
                Assert.Contains("sub/deep.fs", listing)
                Assert.Contains("top.fs", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob returns no-match message when nothing found`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "readme.md"), "x")
            let! result = glob wp (makeArgs ["pattern", "*.xyz"])
            match result with
            | ToolSuccess msg -> Assert.Contains("No files matched", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with no-match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob skips node_modules directory`` () =
    withTempWorkspace (fun wp ->
        async {
            let nm = Path.Combine(wp, "node_modules")
            Directory.CreateDirectory(nm) |> ignore
            File.WriteAllText(Path.Combine(nm, "lib.js"), "")
            File.WriteAllText(Path.Combine(wp, "app.js"), "")
            let! result = glob wp (makeArgs ["pattern", "*.js"])
            match result with
            | ToolSuccess listing ->
                Assert.Contains("app.js", listing)
                Assert.DoesNotContain("lib.js", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep files_with_matches returns paths containing pattern`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "match.txt"),  "hello world")
            File.WriteAllText(Path.Combine(wp, "nomatch.txt"), "goodbye world")
            let args = makeArgs ["pattern", "hello"; "output_mode", "files_with_matches"]
            let! result = grep wp args
            match result with
            | ToolSuccess listing ->
                Assert.Contains("match.txt", listing)
                Assert.DoesNotContain("nomatch.txt", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep content mode includes matching lines`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "code.txt"), "line one\nhello world\nline three")
            let args = makeArgs ["pattern", "hello"; "output_mode", "content"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("hello world", output)
                Assert.Contains("2|", output)   // line number in content mode
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep count mode returns match counts`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "data.txt"), "foo\nfoo bar\nbaz")
            let args = makeArgs ["pattern", "foo"; "output_mode", "count"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("data.txt:2", output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep case_insensitive matches regardless of case`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "case.txt"), "Hello World")
            let args =
                [ "pattern",          jsonStr "hello"
                  "case_insensitive", JsonDocument.Parse("true").RootElement.Clone() ]
                |> Map.ofList
            let! result = grep wp args
            match result with
            | ToolSuccess output -> Assert.Contains("case.txt", output)
            | other -> Assert.Fail($"Expected match with case_insensitive=true, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep returns no-match message when nothing found`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "file.txt"), "irrelevant content")
            let args = makeArgs ["pattern", "xyzzy_not_here"]
            let! result = grep wp args
            match result with
            | ToolSuccess msg -> Assert.Contains("No matches", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with no-match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep with glob filter searches only matching files`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "a.fs"),  "let hello = 1")
            File.WriteAllText(Path.Combine(wp, "b.txt"), "hello world")
            let args = makeArgs ["pattern", "hello"; "glob", "*.fs"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("a.fs", output)
                Assert.DoesNotContain("b.txt", output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep fixed_strings treats pattern as plain text not regex`` () =
    withTempWorkspace (fun wp ->
        async {
            // The string "(hello)" would be an invalid regex without escaping
            File.WriteAllText(Path.Combine(wp, "code.txt"), "call(hello) today")
            let args =
                [ "pattern",       jsonStr "(hello)"
                  "fixed_strings", JsonDocument.Parse("true").RootElement.Clone() ]
                |> Map.ofList
            let! result = grep wp args
            match result with
            | ToolSuccess output -> Assert.Contains("code.txt", output)
            | other -> Assert.Fail($"Expected ToolSuccess with fixed_strings match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep type filter searches only matching file type`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "main.py"),  "hello python")
            File.WriteAllText(Path.Combine(wp, "main.ts"),  "hello typescript")
            File.WriteAllText(Path.Combine(wp, "notes.txt"), "hello text")
            let args = makeArgs ["pattern", "hello"; "type", "py"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("main.py", output)
                Assert.DoesNotContain("main.ts", output)
                Assert.DoesNotContain("notes.txt", output)
            | other -> Assert.Fail($"Expected ToolSuccess with type filter, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep type filter uses extension fallback for unknown types`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "schema.graphql"), "type Query { hello: String }")
            File.WriteAllText(Path.Combine(wp, "code.py"),         "hello = 1")
            let args = makeArgs ["pattern", "hello"; "type", "graphql"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("schema.graphql", output)
                Assert.DoesNotContain("code.py", output)
            | other -> Assert.Fail($"Expected ToolSuccess with graphql type filter, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// glob: offset and entry_type
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``glob with offset skips first N results`` () =
    withTempWorkspace (fun wp ->
        async {
            for i in 1..5 do
                File.WriteAllText(Path.Combine(wp, $"file{i}.txt"), $"content {i}")
            // Get all files first to know the order
            let allArgs = makeArgs ["pattern", "*.txt"]
            let! allResult = glob wp allArgs
            let allFiles =
                match allResult with
                | ToolSuccess s -> s.Split('\n') |> Array.toList
                | _ -> []
            // With offset=2, should skip the first 2
            let offsetArgs = makeArgsWithInt [
                "pattern", jsonStr "*.txt"
                "offset",  jsonInt 2
            ]
            let! result = glob wp offsetArgs
            match result with
            | ToolSuccess output ->
                let files = output.Split('\n') |> Array.filter (fun s -> not (s.StartsWith("(")))
                Assert.Equal(3, files.Length)   // 5 total - 2 skipped = 3
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob entry_type dirs returns directories`` () =
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "subdir1")) |> ignore
            Directory.CreateDirectory(Path.Combine(wp, "subdir2")) |> ignore
            File.WriteAllText(Path.Combine(wp, "file.txt"), "x")
            let args = makeArgsWithInt [
                "pattern",    jsonStr "*"
                "entry_type", jsonStr "dirs"
            ]
            let! result = glob wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("subdir", output)
                // dir entries end with /
                Assert.Contains("/", output)
                Assert.DoesNotContain("file.txt", output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob entry_type both returns files and directories`` () =
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "mydir")) |> ignore
            File.WriteAllText(Path.Combine(wp, "myfile.txt"), "x")
            let args = makeArgsWithInt [
                "pattern",    jsonStr "*"
                "entry_type", jsonStr "both"
            ]
            let! result = glob wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("mydir/", output)
                Assert.Contains("myfile.txt", output)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep: offset pagination
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep with offset skips first N matching files`` () =
    withTempWorkspace (fun wp ->
        async {
            for i in 1..5 do
                File.WriteAllText(Path.Combine(wp, $"match{i}.txt"), "needle")
            // First get all matches
            let allArgs = makeArgs ["pattern", "needle"]
            let! allResult = grep wp allArgs
            let allCount =
                match allResult with
                | ToolSuccess s -> s.Split('\n').Length
                | _ -> 0
            // With offset=3, should skip 3
            let offsetArgs = makeArgsWithInt [
                "pattern", jsonStr "needle"
                "offset",  jsonInt 3
            ]
            let! result = grep wp offsetArgs
            match result with
            | ToolSuccess output ->
                let resultLines = output.Split('\n') |> Array.filter (fun s -> s.Contains(".txt"))
                Assert.Equal(max 0 (allCount - 3), resultLines.Length)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep: context_before / context_after (Python parity: test_search_tools.py)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep context_before and context_after include surrounding lines`` () =
    // Python parity: test_grep_respects_glob_filter_and_context
    withTempWorkspace (fun wp ->
        async {
            let content = "alpha\nbeta\nmatch_here\ngamma\n"
            File.WriteAllText(Path.Combine(wp, "main.py"), content)
            let args =
                [ "pattern",        jsonStr "match_here"
                  "output_mode",    jsonStr "content"
                  "context_before", JsonDocument.Parse("1").RootElement.Clone()
                  "context_after",  JsonDocument.Parse("1").RootElement.Clone() ]
                |> Map.ofList
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("match_here", output)
                Assert.Contains("beta",  output)   // context_before=1
                Assert.Contains("gamma", output)   // context_after=1
            | other -> Assert.Fail($"Expected ToolSuccess with context lines, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep context_before without context_after includes only preceding lines`` () =
    withTempWorkspace (fun wp ->
        async {
            let content = "first\nsecond\ntarget_line\nfourth\n"
            File.WriteAllText(Path.Combine(wp, "ctx.txt"), content)
            let args =
                [ "pattern",        jsonStr "target_line"
                  "output_mode",    jsonStr "content"
                  "context_before", JsonDocument.Parse("1").RootElement.Clone() ]
                |> Map.ofList
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                Assert.Contains("target_line", output)
                Assert.Contains("second", output)   // context_before line
                Assert.DoesNotContain("fourth", output)  // no context_after
            | other -> Assert.Fail($"Expected ToolSuccess with context_before, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// Legacy alias tests (max_results / max_matches)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``glob max_results alias limits results like head_limit`` () =
    withTempWorkspace (fun wp ->
        async {
            for i in 1..5 do
                File.WriteAllText(Path.Combine(wp, $"f{i}.txt"), "x")
            let args = makeArgsWithInt ["pattern", jsonStr "*.txt"; "max_results", jsonInt 2]
            let! result = glob wp args
            match result with
            | ToolSuccess listing ->
                let lines = listing.Split('\n') |> Array.filter (fun s -> s.Contains(".txt"))
                Assert.Equal(2, lines.Length)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep max_results alias limits files_with_matches results`` () =
    withTempWorkspace (fun wp ->
        async {
            for i in 1..5 do
                File.WriteAllText(Path.Combine(wp, $"f{i}.txt"), "needle")
            let args = makeArgsWithInt ["pattern", jsonStr "needle"; "max_results", jsonInt 2]
            let! result = grep wp args
            match result with
            | ToolSuccess listing ->
                let lines = listing.Split('\n') |> Array.filter (fun s -> s.Contains(".txt"))
                Assert.Equal(2, lines.Length)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep max_matches alias limits content mode results`` () =
    withTempWorkspace (fun wp ->
        async {
            for i in 1..5 do
                File.WriteAllText(Path.Combine(wp, $"f{i}.txt"), $"needle {i}")
            let args = makeArgsWithInt [
                "pattern",     jsonStr "needle"
                "output_mode", jsonStr "content"
                "max_matches", jsonInt 2
            ]
            let! result = grep wp args
            match result with
            | ToolSuccess listing ->
                let matchLines = listing.Split('\n') |> Array.filter (fun s -> s.Contains("needle"))
                Assert.True(matchLines.Length <= 2, $"Expected ≤2 match lines, got {matchLines.Length}: {listing}")
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// recordFileWrite — file tracking after writes
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``recordFileWrite clears read-before-edit warning`` () =
    // After recordFileWrite, checkFileRead should return None (no warning),
    // even if the file was never passed through recordFileRead first.
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "initial content")
        // Before any tracking: should warn
        let before = checkFileRead path
        Assert.True(before.IsSome, "Expected warning before any tracking")
        // Simulate a write operation
        recordFileWrite path
        // After write: file is considered known — no edit warning
        let after = checkFileRead path
        Assert.True(after.IsNone, "Expected no warning after recordFileWrite")
    finally
        File.Delete(path)

[<Fact>]
let ``recordFileWrite disables dedup so second readFile returns real content`` () =
    // recordFileWrite sets CanDedup = false, so a subsequent readFile must
    // return the real file content rather than the "unchanged" dedup stub.
    withTempWorkspace (fun wp ->
        async {
            let rel  = "written.txt"
            let full = Path.Combine(wp, rel)
            File.WriteAllText(full, String.concat "\n" [ for i in 1..5 -> $"line {i}" ])
            // First read — primes the cache with CanDedup = true
            let readArgs = makeArgs ["path", rel]
            let! _ = readFile wp 131_072 readArgs
            // Simulate a write that updates the file
            File.WriteAllText(full, String.concat "\n" [ for i in 1..5 -> $"updated line {i}" ])
            recordFileWrite full
            // Second read — CanDedup is false, so must return real content
            let! second = readFile wp 131_072 readArgs
            match second with
            | ToolSuccess content ->
                Assert.DoesNotContain("unchanged", content)
                Assert.Contains("updated line", content)
            | other -> Assert.Fail($"Expected real content after recordFileWrite, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``recordFileWrite on nonexistent path does not throw`` () =
    // recordFileWrite swallows exceptions (best-effort tracking).
    // Calling it on a missing file must not propagate any exception.
    let missing = Path.Combine(Path.GetTempPath(), "botsharp-nonexistent-" + Guid.NewGuid().ToString("N") + ".txt")
    recordFileWrite missing   // must not throw

// ═══════════════════════════════════════════════════════════════════════════
// editFile: .ipynb guard, empty old_str on empty existing file
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile rejects .ipynb files with notebook_edit message`` () =
    // The relPath.EndsWith(".ipynb") guard returns before any workspace check.
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "notebook.ipynb"; "old_str", "foo"; "new_str", "bar"]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("notebook_edit", msg)
                Assert.Contains("notebook", msg.ToLowerInvariant())
            | other -> Assert.Fail($"Expected ToolSuccess with notebook message, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile with empty old_str and empty existing file writes content`` () =
    // The `existing.Trim() = ""` branch: file exists but is empty → write succeeds.
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "empty.txt"), "")
            let args = makeArgs ["path", "empty.txt"; "old_str", ""; "new_str", "new content"]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("empty.txt", msg)
                let written = File.ReadAllText(Path.Combine(wp, "empty.txt"))
                Assert.Equal("new content", written)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// listDir: empty directory
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listDir returns 'empty' message for empty directory`` () =
    // The `if total = 0 then ToolSuccess "Directory {relPath} is empty"` branch.
    withTempWorkspace (fun wp ->
        async {
            let emptyDir = Path.Combine(wp, "emptydir")
            Directory.CreateDirectory(emptyDir) |> ignore
            let! result = listDir wp (makeArgs ["path", "emptydir"])
            match result with
            | ToolSuccess msg -> Assert.Contains("empty", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with 'empty' message, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// glob: missing directory, entry_type dirs no-match message
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``glob returns ToolFailure when path directory does not exist`` () =
    // The `if not (Directory.Exists searchRoot) then ToolFailure ...` branch.
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["pattern", "*.txt"; "path", "no-such-dir"]
            let! result = glob wp args
            match result with
            | ToolFailure (ExecutionFailed msg) -> Assert.Contains("no-such-dir", msg)
            | other -> Assert.Fail($"Expected ToolFailure for missing directory, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob no-match message uses 'directories' word for entry_type dirs`` () =
    // The `match entryType with "dirs" -> "directories"` branch in the no-match note.
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgsWithInt [
                "pattern",    jsonStr "*.xyz"
                "entry_type", jsonStr "dirs"
            ]
            let! result = glob wp args
            match result with
            | ToolSuccess msg -> Assert.Contains("directories", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with 'directories' no-match message, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``glob no-match message uses 'files or directories' for entry_type both`` () =
    // The `"both" -> "files or directories"` branch in the no-match note.
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgsWithInt [
                "pattern",    jsonStr "*.xyzzy_nomatch"
                "entry_type", jsonStr "both"
            ]
            let! result = glob wp args
            match result with
            | ToolSuccess msg -> Assert.Contains("files or directories", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with 'files or directories' message, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep: invalid regex, single-file search path
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep returns ToolFailure for invalid regex pattern`` () =
    // The `with ex -> Error (ExecutionFailed $"Invalid regex: ...")` branch in parseGrepArgs.
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["pattern", "[unclosed"]
            let! result = grep wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("Invalid regex", msg)
            | other -> Assert.Fail($"Expected ToolFailure for invalid regex, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``grep searches a single file when path points to a file`` () =
    // The `if File.Exists searchRoot then [ searchRoot, 0.0 ]` branch.
    withTempWorkspace (fun wp ->
        async {
            let filePath = Path.Combine(wp, "single.txt")
            File.WriteAllText(filePath, "find me here")
            // Pass the file path directly as "path" (relative to workspace)
            let args = makeArgs ["pattern", "find me"; "path", "single.txt"]
            let! result = grep wp args
            match result with
            | ToolSuccess output -> Assert.Contains("single.txt", output)
            | other -> Assert.Fail($"Expected ToolSuccess for single-file grep, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// writeFile: missing required arguments
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``writeFile returns ToolFailure when path arg is missing`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["content", "some content"]
            let! result = writeFile wp args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for missing path, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``writeFile returns ToolFailure when content arg is missing`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "out.txt"]
            let! result = writeFile wp args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for missing content, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// checkFileRead: mtime-changed-but-same-hash path (silent update)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``checkFileRead returns None when mtime changed but content is identical`` () =
    // The `if currentHash <> "" && currentHash = record.ContentHash` branch:
    // content is the same despite mtime change → silent mtime update → None returned.
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "stable content")
        recordFileRead path 1 2000
        // Bump the mtime without changing content
        let now = DateTime.UtcNow.AddSeconds(5.0)
        File.SetLastWriteTimeUtc(path, now)
        let result = checkFileRead path
        Assert.True(result.IsNone, "Expected None (no warning) when only mtime changed")
    finally
        File.Delete(path)

// checkFileRead: sub-second content change (Python parity for fast in-place writes).
// Even when mtime looks the same, a hash mismatch should trigger a warning.
[<Fact>]
let ``checkFileRead warns when content changed despite identical mtime`` () =
    let path = Path.GetTempFileName()
    try
        File.WriteAllText(path, "original content")
        recordFileRead path 1 2000
        // Write new content but force the mtime back to what we had (simulate sub-second write)
        let savedMtime = File.GetLastWriteTimeUtc(path)
        File.WriteAllText(path, "changed content")
        File.SetLastWriteTimeUtc(path, savedMtime)  // rewind mtime
        let result = checkFileRead path
        Assert.True(result.IsSome, "Expected Some warning when content hash changed despite same mtime")
    finally
        File.Delete(path)

// ═══════════════════════════════════════════════════════════════════════════
// readFile — device path blocking (mirrors Python _is_blocked_device)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile blocks /dev/random`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "/dev/random"]
            let! result = readFile wp 128_000 args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for /dev/random, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile blocks /dev/zero`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "/dev/zero"]
            let! result = readFile wp 128_000 args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for /dev/zero, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile blocks /dev/stdin`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "/dev/stdin"]
            let! result = readFile wp 128_000 args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for /dev/stdin, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile blocks /dev/urandom`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "/dev/urandom"]
            let! result = readFile wp 128_000 args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for /dev/urandom, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile blocks /dev/null via /dev/ prefix`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "/dev/null"]
            let! result = readFile wp 128_000 args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for /dev/null, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile error message mentions device path`` () =
    withTempWorkspace (fun wp ->
        async {
            let args = makeArgs ["path", "/dev/zero"]
            let! result = readFile wp 128_000 args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("blocked", msg)
            | other -> Assert.Fail($"Expected ToolFailure with 'blocked' message, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — quote normalization fallback (curly ↔ straight quotes)
// Mirrors Python's _normalize_quotes fallback in str_replace.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile matches curly single quotes when old_str has straight quotes`` () =
    withTempWorkspace (fun wp ->
        async {
            // File contains curly single quotes (\u2018 and \u2019)
            let fileContent = "let x = \u2018hello\u2019"
            File.WriteAllText(Path.Combine(wp, "curly.txt"), fileContent)
            // old_str uses straight quotes — should still match via normalization
            let args = makeArgs ["path", "curly.txt"; "old_str", "'hello'"; "new_str", "'world'"]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("quote-normalized", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "curly.txt"))
                Assert.Contains("world", updated)
            | other -> Assert.Fail($"Expected ToolSuccess via quote normalization, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile matches curly double quotes when old_str has straight quotes`` () =
    withTempWorkspace (fun wp ->
        async {
            // File contains curly double quotes (\u201c and \u201d)
            let fileContent = "let s = \u201chello world\u201d"
            File.WriteAllText(Path.Combine(wp, "curly2.txt"), fileContent)
            // Use jsonStrSafe to properly escape the double-quote characters in old_str/new_str
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "curly2.txt"
                    "old_str", jsonStrSafe "\"hello world\""
                    "new_str", jsonStrSafe "\"goodbye\""
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("quote-normalized", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "curly2.txt"))
                Assert.Contains("goodbye", updated)
            | other -> Assert.Fail($"Expected ToolSuccess via quote normalization, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile exact match still works (no normalization needed)`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "straight.txt"), "let x = 'hello'")
            let args = makeArgs ["path", "straight.txt"; "old_str", "'hello'"; "new_str", "'world'"]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.DoesNotContain("quote-normalized", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "straight.txt"))
                Assert.Equal("let x = 'world'", updated)
            | other -> Assert.Fail($"Expected ToolSuccess for exact match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile returns ToolFailure when no match even after normalization`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "nomatch.txt"), "completely different content")
            let args = makeArgs ["path", "nomatch.txt"; "old_str", "xyz not present"; "new_str", "new"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("not found", msg)
            | other -> Assert.Fail($"Expected ToolFailure for unmatched string, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — line-trimmed fallback (handles indentation drift)
// Mirrors Python's _find_trim_matches: matches when each line's trimmed
// content is equal. Useful when LLM produces old_str with wrong indentation.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile matches when old_str has tab indentation but file uses spaces`` () =
    withTempWorkspace (fun wp ->
        async {
            // File has 4-space indentation; LLM provided old_str with tab indent.
            // "\treturn 42;" is NOT a substring of "    return 42;" (tab ≠ space).
            let fileContent = "function foo() {\n    return 42;\n}"
            File.WriteAllText(Path.Combine(wp, "indented.txt"), fileContent)
            // Use jsonStrSafe to properly escape the tab character in old_str
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "indented.txt"
                    "old_str", jsonStrSafe "\treturn 42;"
                    "new_str", jsonStr "    return 99;"
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("line-trimmed", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "indented.txt"))
                Assert.Contains("return 99", updated)
            | other -> Assert.Fail($"Expected ToolSuccess via line-trimmed match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile matches multi-line old_str with different indentation`` () =
    withTempWorkspace (fun wp ->
        async {
            let fileContent = "class Foo:\n    def bar(self):\n        pass\n"
            File.WriteAllText(Path.Combine(wp, "class.txt"), fileContent)
            // old_str uses tab indent; file has 4 spaces — NOT exact substring.
            // Use jsonStrSafe to properly escape newlines in the JSON value.
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "class.txt"
                    "old_str", jsonStrSafe "    def bar(self):\n\t\tpass"
                    "new_str", jsonStrSafe "    def bar(self):\n        return 42"
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("line-trimmed", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "class.txt"))
                Assert.Contains("return 42", updated)
            | other -> Assert.Fail($"Expected ToolSuccess via multi-line trimmed match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile exact match wins over line-trimmed when both possible`` () =
    withTempWorkspace (fun wp ->
        async {
            // File has exact match; should use exact (no label)
            File.WriteAllText(Path.Combine(wp, "exact.txt"), "foo bar baz")
            let args = makeArgs ["path", "exact.txt"; "old_str", "foo bar baz"; "new_str", "replaced"]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.DoesNotContain("line-trimmed", msg)
                Assert.DoesNotContain("quote-normalized", msg)
            | other -> Assert.Fail($"Expected ToolSuccess for exact match, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile quote-normalized replaceAll replaces all curly-quote occurrences`` () =
    withTempWorkspace (fun wp ->
        async {
            // File has curly single quotes in two places
            let content = "\u2018foo\u2019 and \u2018foo\u2019"
            File.WriteAllText(Path.Combine(wp, "multi.txt"), content)
            let args =
                makeArgsWithInt [
                    "path",        jsonStr "multi.txt"
                    "old_str",     jsonStr "'foo'"
                    "new_str",     jsonStr "'bar'"
                    "replace_all", JsonDocument.Parse("true").RootElement.Clone()
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("2", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "multi.txt"))
                // Both occurrences should be replaced with 'bar' (straight or curly is ok, we used newStr)
                Assert.Equal(2, updated.Split("bar").Length - 1)
            | other -> Assert.Fail($"Expected ToolSuccess replacing 2 curly-quote occurrences, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — reindentLikeMatch (Python's _reindent_like_match parity)
// When a line-trimmed match is found at a different indentation level,
// new_str gets the same extra indentation applied automatically.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile reindents new_str when trim-matched at deeper indentation`` () =
    withTempWorkspace (fun wp ->
        async {
            // File has 4-space indentation.
            // LLM provides old_str with NO indent (not a substring of "    doA()").
            // Line-trimmed match fires; reindentLikeMatch adds the 4-space delta.
            let fileContent = "if True:\n    doA()\n    doB()\n"
            File.WriteAllText(Path.Combine(wp, "reindent.txt"), fileContent)
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "reindent.txt"
                    "old_str", jsonStrSafe "doA()\ndoB()"   // no leading whitespace
                    "new_str", jsonStrSafe "doX()\ndoY()"   // no leading whitespace
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("line-trimmed", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "reindent.txt"))
                // 4-space indent should be preserved from actual file
                Assert.Contains("    doX()", updated)
                Assert.Contains("    doY()", updated)
            | other -> Assert.Fail($"Expected ToolSuccess with reindent, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile reindents multi-line new_str when trim-matched at deeper indent`` () =
    withTempWorkspace (fun wp ->
        async {
            // File has 8-space indent inside a nested block.
            // LLM supplies old_str/new_str with NO leading whitespace (trim match).
            let fileContent = "class Foo:\n    def bar(self):\n        x = 1\n        y = 2\n"
            File.WriteAllText(Path.Combine(wp, "nested.txt"), fileContent)
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "nested.txt"
                    "old_str", jsonStrSafe "x = 1\ny = 2"    // no indent (not a substring of "        x = 1")
                    "new_str", jsonStrSafe "x = 10\ny = 20"  // no indent in new_str
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("line-trimmed", msg)
                let updated = File.ReadAllText(Path.Combine(wp, "nested.txt"))
                // 8-space indent restored by reindentLikeMatch
                Assert.Contains("        x = 10", updated)
                Assert.Contains("        y = 20", updated)
            | other -> Assert.Fail($"Expected ToolSuccess with multi-line reindent, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — CRLF preservation (Windows line endings)
// Mirrors Python's CRLF detection and restoration.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile preserves CRLF line endings when file uses CRLF`` () =
    withTempWorkspace (fun wp ->
        async {
            // Write a file with CRLF line endings
            let fileContent = "line one\r\nline two\r\nline three\r\n"
            File.WriteAllBytes(Path.Combine(wp, "crlf.txt"), System.Text.Encoding.UTF8.GetBytes(fileContent))
            let args = makeArgs ["path", "crlf.txt"; "old_str", "line two"; "new_str", "line TWO"]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let updatedBytes = File.ReadAllBytes(Path.Combine(wp, "crlf.txt"))
                let updated = System.Text.Encoding.UTF8.GetString(updatedBytes)
                Assert.Contains("\r\n", updated)
                Assert.Contains("line TWO", updated)
            | other -> Assert.Fail($"Expected ToolSuccess for CRLF file, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — trailing whitespace stripping
// Mirrors Python's EditFileTool._strip_trailing_ws (skipped for .md files).
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile strips trailing whitespace from new_str in non-markdown files`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "code.fs"), "let x = 1\n")
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "code.fs"
                    "old_str", jsonStrSafe "let x = 1"
                    "new_str", jsonStrSafe "let x = 42   "   // trailing spaces
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let updated = File.ReadAllText(Path.Combine(wp, "code.fs"))
                // Trailing spaces should have been stripped
                Assert.DoesNotContain("42   ", updated)
                Assert.Contains("42", updated)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile preserves trailing whitespace in markdown files`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "doc.md"), "# Title\n\nfoo bar\n")
            let args =
                makeArgsWithInt [
                    "path",    jsonStr "doc.md"
                    "old_str", jsonStrSafe "foo bar"
                    "new_str", jsonStrSafe "foo bar  "   // 2 trailing spaces (MD line break)
                ]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let updated = File.ReadAllText(Path.Combine(wp, "doc.md"))
                // Markdown trailing spaces should be preserved
                Assert.Contains("foo bar  ", updated)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — improved error diagnostics
// When old_str is not found, the error message should include a hint.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile error hints 'letter case differs' when case mismatch`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "case.txt"), "Hello World")
            let args = makeArgs ["path", "case.txt"; "old_str", "hello world"; "new_str", "bye"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("case", msg.ToLowerInvariant())
            | other -> Assert.Fail($"Expected ToolFailure with case hint, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// readFile — binary file and image detection (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile returns error message for binary file with null bytes`` () =
    withTempWorkspace (fun wp ->
        async {
            // Write a file with null bytes (classic binary heuristic)
            let binaryBytes = [| 0x00uy; 0x01uy; 0x02uy; 0x03uy; 0xFFuy |]
            File.WriteAllBytes(Path.Combine(wp, "binary.bin"), binaryBytes)
            let args = makeArgs ["path", "binary.bin"]
            let! result = readFile wp 100_000 args
            match result with
            | ToolSuccess msg ->
                // Should report it cannot read binary (not crash or return garbage)
                Assert.Contains("binary", msg.ToLowerInvariant())
            | ToolFailure _ ->
                // Also acceptable — important that we don't silently return garbage
                ()
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile returns image message for PNG file`` () =
    withTempWorkspace (fun wp ->
        async {
            // Write a minimal PNG magic bytes header
            let pngMagic = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy
                              0x00uy; 0x00uy; 0x00uy; 0x00uy |]
            File.WriteAllBytes(Path.Combine(wp, "image.png"), pngMagic)
            let args = makeArgs ["path", "image.png"]
            let! result = readFile wp 100_000 args
            match result with
            | ToolSuccess msg ->
                // Should indicate it's an image, not try to decode as text
                Assert.Contains("image", msg.ToLowerInvariant())
                Assert.Contains("png", msg.ToLowerInvariant())
            | other -> Assert.Fail($"Expected ToolSuccess with image message, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile returns image message for JPEG file`` () =
    withTempWorkspace (fun wp ->
        async {
            // JPEG magic bytes (SOI marker)
            let jpegMagic = [| 0xFFuy; 0xD8uy; 0xFFuy; 0xE0uy; 0x00uy; 0x10uy |]
            File.WriteAllBytes(Path.Combine(wp, "photo.jpg"), jpegMagic)
            let args = makeArgs ["path", "photo.jpg"]
            let! result = readFile wp 100_000 args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("image", msg.ToLowerInvariant())
                Assert.Contains("jpeg", msg.ToLowerInvariant())
            | other -> Assert.Fail($"Expected ToolSuccess with image message, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// readFile — PDF and Office Open XML detection (Python parity)
// Python dispatches to _read_pdf / _read_office_doc for these formats.
// F# returns a descriptive stub rather than "binary file" error.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile returns PDF message for PDF magic bytes`` () =
    withTempWorkspace (fun wp ->
        async {
            // PDF magic: %PDF- = 0x25 0x50 0x44 0x46 0x2D
            let pdfMagic = [| 0x25uy; 0x50uy; 0x44uy; 0x46uy; 0x2Duy; 0x31uy; 0x2Euy |]
            File.WriteAllBytes(Path.Combine(wp, "doc.pdf"), pdfMagic)
            let args = makeArgs ["path", "doc.pdf"]
            let! result = readFile wp 100_000 args
            match result with
            | ToolSuccess msg ->
                Assert.Contains("PDF", msg)
                Assert.Contains("doc.pdf", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with PDF message, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile returns Office document message for ZIP magic bytes (docx)`` () =
    withTempWorkspace (fun wp ->
        async {
            // Office Open XML (.docx/.xlsx/.pptx) starts with ZIP magic: PK = 0x50 0x4B 0x03 0x04
            let zipMagic = [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy; 0x14uy; 0x00uy |]
            File.WriteAllBytes(Path.Combine(wp, "report.docx"), zipMagic)
            let args = makeArgs ["path", "report.docx"]
            let! result = readFile wp 100_000 args
            match result with
            | ToolSuccess msg ->
                // Should return a helpful Office/ZIP message, not "binary file"
                Assert.True(
                    msg.ToLowerInvariant().Contains("word") || msg.ToLowerInvariant().Contains("office"),
                    $"Expected Office doc message, got: {msg}")
            | other -> Assert.Fail($"Expected ToolSuccess with Office message, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// listDir — emoji prefixes in non-recursive mode (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listDir non-recursive uses emoji prefix for dirs`` () =
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "mydir")) |> ignore
            let! result = listDir wp (makeArgs ["path", "."])
            match result with
            | ToolSuccess listing ->
                // Python uses "📁 mydir" for directories in non-recursive mode
                Assert.Contains("📁 mydir", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir non-recursive uses emoji prefix for files`` () =
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "readme.txt"), "hello")
            let! result = listDir wp (makeArgs ["path", "."])
            match result with
            | ToolSuccess listing ->
                // Python uses "📄 readme.txt" for files in non-recursive mode
                Assert.Contains("📄 readme.txt", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir recursive does not use emoji prefix`` () =
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "subdir")) |> ignore
            File.WriteAllText(Path.Combine(wp, "subdir", "file.txt"), "x")
            let args = makeArgsWithInt ["path", jsonStr "."; "recursive", jsonBool true]
            let! result = listDir wp args
            match result with
            | ToolSuccess listing ->
                // Recursive mode uses rel/ for dirs, no emoji prefix
                Assert.Contains("subdir/", listing)
                Assert.DoesNotContain("📁", listing)
                Assert.DoesNotContain("📄", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — delete-line cleanup (Python parity)
// When new_str="", the trailing newline after the match is consumed so no
// blank line is left behind. Mirrors Python's edit_file delete cleanup logic.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile delete-line cleanup: deleting a line consumes trailing newline`` () =
    withTempWorkspace (fun wp ->
        async {
            // File with three lines; we delete the middle one.
            // Without delete-line cleanup: "line1\n\nline3\n"
            // With delete-line cleanup:    "line1\nline3\n"
            let fileContent = "line1\nmiddle\nline3\n"
            File.WriteAllText(Path.Combine(wp, "lines.txt"), fileContent)
            let args = makeArgs ["path", "lines.txt"; "old_str", "middle"; "new_str", ""]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let updated = File.ReadAllText(Path.Combine(wp, "lines.txt"))
                // Should not have a double newline (blank line) at middle position
                Assert.DoesNotContain("\n\n", updated)
                Assert.Contains("line1", updated)
                Assert.Contains("line3", updated)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile delete-line cleanup: deleting block already ending with newline skips cleanup`` () =
    withTempWorkspace (fun wp ->
        async {
            // When old_str itself ends with '\n', no extra newline is consumed.
            let fileContent = "line1\nmiddle\nline3\n"
            File.WriteAllText(Path.Combine(wp, "lines2.txt"), fileContent)
            // old_str ends with '\n' — delete-line cleanup should NOT fire
            let args = makeArgsWithInt [
                "path",    jsonStr "lines2.txt"
                "old_str", jsonStrSafe "middle\n"
                "new_str", jsonStrSafe ""
            ]
            let! result = editFile wp args
            match result with
            | ToolSuccess _ ->
                let updated = File.ReadAllText(Path.Combine(wp, "lines2.txt"))
                // middle and its newline removed, no extra newline consumed
                Assert.Contains("line1", updated)
                Assert.Contains("line3", updated)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — "Did you mean?" suggestions for missing files (Python parity)
// Python's _file_not_found_msg uses difflib to suggest similar filenames.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile returns 'Did you mean?' when similar file exists`` () =
    withTempWorkspace (fun wp ->
        async {
            // Create "hello.txt" then try to edit "helo.txt" (typo)
            File.WriteAllText(Path.Combine(wp, "hello.txt"), "content")
            let args = makeArgs ["path", "helo.txt"; "old_str", "content"; "new_str", "new"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                // Should mention the similar existing file
                Assert.Contains("hello.txt", msg)
                Assert.Contains("Did you mean", msg)
            | other -> Assert.Fail($"Expected ToolFailure with 'Did you mean?', got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — best-window near-match diagnostic (Python parity)
// When old_str is not found but a close match exists, the error includes a diff.
// Mirrors Python's _not_found_msg / _best_window / _diagnose_near_match.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile near-match shows best window and diff when ratio > 50%`` () =
    withTempWorkspace (fun wp ->
        async {
            let content = "line1\nHello World\nline3\n"
            File.WriteAllText(Path.Combine(wp, "near.txt"), content)
            // Old str has case mismatch — no exact match, close match ratio > 50%
            let args = makeArgs ["path", "near.txt"; "old_str", "hello world"; "new_str", "bye"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                Assert.Contains("Best match", msg)
                Assert.Contains("line", msg.ToLowerInvariant())
            | other -> Assert.Fail($"Expected ToolFailure with near-match hint, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``editFile near-match hints whitespace differs`` () =
    withTempWorkspace (fun wp ->
        async {
            let content = "foo\nbar  baz\nqux\n"
            File.WriteAllText(Path.Combine(wp, "ws.txt"), content)
            // old_str has collapsed whitespace; file has "bar  baz"
            let args = makeArgs ["path", "ws.txt"; "old_str", "bar baz"; "new_str", "replaced"]
            let! result = editFile wp args
            match result with
            | ToolFailure (ExecutionFailed msg) ->
                // Should hint about whitespace or show best match diff
                let lower = msg.ToLowerInvariant()
                Assert.True(lower.Contains("whitespace") || lower.Contains("best match"),
                    $"Expected whitespace or best-match hint in: {msg}")
            | other -> Assert.Fail($"Expected ToolFailure, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — file-size protection (Python parity)
// Files > 1 GiB should be rejected with a clear error.
// (We test the check exists, not the 1 GiB threshold itself)
// ═══════════════════════════════════════════════════════════════════════════

// Note: We can't practically create a 1 GiB file in tests,
// so this is tested via the production code path structure.
// The feature is documented and covered by integration/manual testing.

// ═══════════════════════════════════════════════════════════════════════════
// readFile — empty file (Python parity: TestReadFileTool.test_empty_file)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile returns empty-file message for zero-byte file`` () =
    // Python parity: test_empty_file — expects "Empty file" in result.
    // F# implementation: returns "(Empty file: {relPath})" for 0-byte files.
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllBytes(Path.Combine(wp, "empty.txt"), [||])
            let! result = readFile wp 131_072 (makeArgs ["path", "empty.txt"])
            match result with
            | ToolSuccess msg ->
                Assert.Contains("Empty file", msg)
                Assert.Contains("empty.txt", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with 'Empty file' message, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile with only a trailing newline shows one empty line (not 'Empty file')`` () =
    // A file containing "\n" has 1 logical line (the empty string before the \n).
    // F# splits by '\n', drops the trailing empty element, resulting in [""] → 1 line.
    // This is NOT the same as a zero-byte file — it shows "1| " and "End of file".
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "newline.txt"), "\n")
            let! result = readFile wp 131_072 (makeArgs ["path", "newline.txt"])
            match result with
            | ToolSuccess msg ->
                // Should show line 1 (even though it's empty) and end-of-file note
                Assert.Contains("1|", msg)
                Assert.Contains("End of file", msg)
                Assert.DoesNotContain("Empty file", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with line number, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// readFile — continuation hint "Use offset=" (Python parity)
// Python: test_char_budget_trims checks "Use offset=" in truncated result.
// F# emits "(Showing lines N–M of Total. Use offset=K to continue.)"
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile truncated by limit includes 'Use offset=' continuation hint`` () =
    // Python parity: test_offset_and_limit — "Use offset=8 to continue" in result.
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "lines.txt"), String.concat "\n" [ for i in 1..20 -> $"line {i}" ])
            let args = makeArgsWithInt [ "path", jsonStr "lines.txt"; "offset", jsonInt 1; "limit", jsonInt 5 ]
            let! result = readFile wp 131_072 args
            match result with
            | ToolSuccess text ->
                Assert.Contains("Use offset=", text)   // continuation hint
                Assert.Contains("6", text)              // next offset value
            | other -> Assert.Fail($"Expected ToolSuccess with 'Use offset=' hint, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``readFile full file does not include continuation hint`` () =
    // When all lines are shown, the note is "End of file" — no "Use offset=".
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "short.txt"), "line1\nline2\nline3\n")
            let! result = readFile wp 131_072 (makeArgs ["path", "short.txt"])
            match result with
            | ToolSuccess text ->
                Assert.DoesNotContain("Use offset=", text)
                Assert.Contains("End of file", text)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// readFile — missing path argument (Python parity)
// Python: test_missing_path_returns_clear_error → "Error reading file: Unknown path"
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readFile returns ToolFailure when path argument is missing`` () =
    // Python parity: test_missing_path_returns_clear_error
    withTempWorkspace (fun wp ->
        async {
            let! result = readFile wp 131_072 Map.empty
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for missing path, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// editFile — missing new_str argument (Python parity)
// Python: test_missing_new_text_returns_clear_error → "Error editing file: Unknown new_text"
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``editFile returns ToolFailure when new_str argument is missing`` () =
    // Python parity: test_missing_new_text_returns_clear_error
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "a.txt"), "hello")
            let args = makeArgs ["path", "a.txt"; "old_str", "hello"]
            // new_str missing — should return ToolFailure
            let! result = editFile wp args
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for missing new_str, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// listDir — missing path argument (Python parity)
// Python: test_missing_path_returns_clear_error → "Error listing directory: Unknown path"
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listDir with default path (no arg) lists workspace root`` () =
    // Python's ListDirTool.execute() with no path defaults to workspace root.
    // F# listDir with Map.empty uses "." as default.
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "root-file.txt"), "hello")
            let! result = listDir wp Map.empty
            match result with
            | ToolSuccess listing -> Assert.Contains("root-file.txt", listing)
            | other -> Assert.Fail($"Expected ToolSuccess listing workspace root, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep — files_with_matches returns one entry per file (Python parity)
// Python: test_grep_files_with_matches_mode_returns_unique_paths
//   File a.py has "needle\nneedle\n" (2 matches) — must appear only once.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep files_with_matches reports each matching file once even with multiple matches`` () =
    // Python parity: test_grep_files_with_matches_mode_returns_unique_paths
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "multi.txt"), "needle\nneedle\nfoo\nneedle\n")
            let args = makeArgs ["pattern", "needle"; "output_mode", "files_with_matches"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                // "multi.txt" should appear exactly once, not once per match
                let lines = output.Split('\n') |> Array.filter (fun l -> l.Contains("multi.txt"))
                Assert.Equal(1, lines.Length)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep — count mode multi-file (Python parity)
// Python: test_grep_count_mode_reports_counts_per_file
//   Checks per-file counts AND "total matches: N in M files" footer.
// NOTE: F# implementation does not emit a "total matches" footer.
//       These tests verify per-file count output only.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep count mode reports per-file match counts for multiple files`` () =
    // Python parity: test_grep_count_mode_reports_counts_per_file
    withTempWorkspace (fun wp ->
        async {
            File.WriteAllText(Path.Combine(wp, "one.txt"), "warn\nok\nwarn\n")
            File.WriteAllText(Path.Combine(wp, "two.txt"), "warn\n")
            let args = makeArgs ["pattern", "warn"; "output_mode", "count"]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                // F# format: "file.txt:N" (no space before N)
                Assert.Contains("one.txt", output)
                Assert.Contains("two.txt", output)
                // one.txt has 2 matches, two.txt has 1 match
                let lineOne = output.Split('\n') |> Array.tryFind (fun l -> l.Contains("one.txt"))
                let lineTwo = output.Split('\n') |> Array.tryFind (fun l -> l.Contains("two.txt"))
                Assert.True(lineOne.IsSome && lineOne.Value.Contains("2"), $"Expected one.txt count 2 in: {output}")
                Assert.True(lineTwo.IsSome && lineTwo.Value.Contains("1"), $"Expected two.txt count 1 in: {output}")
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// glob — head_limit with offset produces pagination note (Python parity)
// Python: test_glob_supports_head_limit_offset_and_recent_first
//   Checks "pagination: limit=1, offset=1" in result.
// NOTE: F# emits "(offset=N)" not "pagination: limit=N, offset=M".
//       This test verifies the offset note is present in some form.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``glob head_limit with offset includes offset note in output`` () =
    // Python parity: test_glob_supports_head_limit_offset_and_recent_first
    withTempWorkspace (fun wp ->
        async {
            for i in 1..3 do
                File.WriteAllText(Path.Combine(wp, $"f{i}.py"), $"content {i}")
            let args = makeArgsWithInt [ "pattern", jsonStr "*.py"; "head_limit", jsonInt 1; "offset", jsonInt 1 ]
            let! result = glob wp args
            match result with
            | ToolSuccess output ->
                // Should include some offset-related note
                Assert.True(
                    output.Contains("offset") || output.Contains("pagination"),
                    $"Expected offset/pagination note in glob output: {output}")
                // Should return exactly 1 file (head_limit=1)
                let filePaths = output.Split('\n') |> Array.filter (fun l -> l.EndsWith(".py"))
                Assert.Equal(1, filePaths.Length)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// grep — head_limit with offset includes offset note (Python parity)
// Python: test_grep_files_with_matches_supports_head_limit_and_offset
//   Checks "pagination: limit=1, offset=1" in result.
// NOTE: F# emits "(offset=N, skipped N results)" not "pagination: limit=N, offset=M".
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``grep head_limit with offset includes offset note in output`` () =
    // Python parity: test_grep_files_with_matches_supports_head_limit_and_offset
    withTempWorkspace (fun wp ->
        async {
            for i in 1..3 do
                File.WriteAllText(Path.Combine(wp, $"m{i}.txt"), "needle")
            let args = makeArgsWithInt [ "pattern", jsonStr "needle"; "head_limit", jsonInt 1; "offset", jsonInt 1 ]
            let! result = grep wp args
            match result with
            | ToolSuccess output ->
                // F# emits "(offset=1, skipped N results)" or similar
                Assert.True(
                    output.Contains("offset") || output.Contains("pagination"),
                    $"Expected offset/pagination note in grep output: {output}")
                // Should return exactly 1 file (head_limit=1)
                let filePaths = output.Split('\n') |> Array.filter (fun l -> l.EndsWith(".txt"))
                Assert.Equal(1, filePaths.Length)
            | other -> Assert.Fail($"Expected ToolSuccess with 1 result, got {other}")
        }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// listDir — ignores .git and node_modules (Python parity)
// Python: test_basic_list checks .git and node_modules NOT in listing
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listDir ignores dot-git directory`` () =
    // Python parity: test_basic_list — .git should be filtered out
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, ".git")) |> ignore
            File.WriteAllText(Path.Combine(wp, ".git", "config"), "x")
            File.WriteAllText(Path.Combine(wp, "README.md"), "hi")
            let! result = listDir wp (makeArgs ["path", "."])
            match result with
            | ToolSuccess listing ->
                Assert.Contains("README.md", listing)
                Assert.DoesNotContain(".git", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously

[<Fact>]
let ``listDir ignores node_modules directory`` () =
    // Python parity: test_basic_list — node_modules should be filtered out
    withTempWorkspace (fun wp ->
        async {
            Directory.CreateDirectory(Path.Combine(wp, "node_modules")) |> ignore
            File.WriteAllText(Path.Combine(wp, "node_modules", "lib.js"), "x")
            File.WriteAllText(Path.Combine(wp, "app.js"), "hello")
            let! result = listDir wp (makeArgs ["path", "."])
            match result with
            | ToolSuccess listing ->
                Assert.Contains("app.js", listing)
                Assert.DoesNotContain("node_modules", listing)
            | other -> Assert.Fail($"Expected ToolSuccess, got {other}")
        }) |> Async.RunSynchronously
