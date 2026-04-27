module BotSharp.Tests.Infrastructure.ToolHintsTests

open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolHints

// ─── Helpers ─────────────────────────────────────────────────────────────────

let private mkCall (name: string) (args: (string * string) list) : ToolCall =
    let jargs =
        args
        |> List.map (fun (k, v) ->
            // Use JsonSerializer.Serialize to properly escape special characters (backslashes, quotes).
            let jv = JsonDocument.Parse(JsonSerializer.Serialize(v)).RootElement
            k, jv)
        |> Map.ofList
    { Id = ToolCallId "c1"; Tool = ToolName name; Arguments = jargs; ProviderMeta = None }

let private hint calls = formatToolHints calls

// ─── abbreviatePath tests ─────────────────────────────────────────────────────

[<Fact>]
let ``abbreviatePath returns path as-is when short enough`` () =
    Assert.Equal("foo.txt", abbreviatePath "foo.txt" 40)

[<Fact>]
let ``abbreviatePath replaces home dir with tilde`` () =
    let home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
    let result = abbreviatePath (home + "/Projects/nanobot/agent.fs") 80
    Assert.StartsWith("~", result)

[<Fact>]
let ``abbreviatePath abbreviates long path keeping basename`` () =
    let longPath = "/home/user/projects/nanobot/very/deep/nested/directory/structure/agent.fs"
    let result = abbreviatePath longPath 30
    Assert.True(result.Length <= 30, $"Expected ≤ 30 chars but got: {result}")
    Assert.Contains("agent.fs", result)

[<Fact>]
let ``abbreviatePath preserves short path unchanged`` () =
    let result = abbreviatePath "src/app.py" 40
    Assert.Equal("src/app.py", result)

[<Fact>]
let ``abbreviatePath uses ellipsis prefix for long paths`` () =
    let longPath = "/home/user/projects/nanobot/very/deep/nested/agent.fs"
    let result = abbreviatePath longPath 30
    Assert.StartsWith("…", result)

[<Fact>]
let ``abbreviatePath handles URL with domain and filename`` () =
    let url = "https://github.com/user/repo/blob/main/src/nanobot/agent.py"
    let result = abbreviatePath url 40
    Assert.True(result.Length <= 40 || result.Contains("github.com"), $"Unexpected result: {result}")

[<Fact>]
let ``abbreviatePath URL with no path returns unchanged if short`` () =
    let url = "https://example.com"
    Assert.Equal(url, abbreviatePath url 80)

[<Fact>]
let ``abbreviatePath URL keeps domain and last segment for long URLs`` () =
    let url = "https://example.com/api/v2/endpoint?key=value&other=123"
    let result = abbreviatePath url 40
    Assert.Contains("example.com", result)
    Assert.Contains("\u2026", result)   // ellipsis character

[<Fact>]
let ``abbreviatePath URL with very long basename still includes domain`` () =
    let url = "https://example.com/path/very_long_resource_name_file.json"
    let result = abbreviatePath url 35
    Assert.Contains("example.com", result)

[<Fact>]
let ``abbreviatePath short URL with query is unchanged`` () =
    let url = "https://example.com/api"
    Assert.Equal(url, abbreviatePath url 80)

// ─── abbreviatePath — Python parity edge cases ───────────────────────────────

[<Fact>]
let ``abbreviatePath returns empty string for empty input`` () =
    // Python parity: test_empty_string
    Assert.Equal("", abbreviatePath "" 40)

[<Fact>]
let ``abbreviatePath returns path unchanged when length equals max_len`` () =
    // Python parity: test_exact_max_len_unchanged
    Assert.Equal("/a/b/c", abbreviatePath "/a/b/c" 6)

[<Fact>]
let ``abbreviatePath returns basename-only input unchanged when short`` () =
    // Python parity: test_basename_only
    Assert.Equal("file.py", abbreviatePath "file.py" 40)

[<Fact>]
let ``abbreviatePath long path keeps parent dir and basename`` () =
    // Python parity: test_long_path_keeps_parent_dir
    // /a/b/c/d/e/f/g/h/src/loop.py with max_len=30 → parent "src" and file "loop.py" visible
    let path = "/a/b/c/d/e/f/g/h/src/loop.py"
    let result = abbreviatePath path 30
    Assert.Contains("loop.py", result)
    Assert.Contains("src", result)

[<Fact>]
let ``abbreviatePath very long path with tight limit shows only basename`` () =
    // Python parity: test_very_long_path_just_basename
    let path = "/a/b/c/d/e/f/g/h/i/j/k/l/m/n/o/p/q/r/s/t/u/v/w/x/y/z/file.py"
    let result = abbreviatePath path 20
    Assert.Contains("file.py", result)
    Assert.True(result.Length <= 20, $"Expected ≤ 20 chars but got {result.Length}: {result}")

[<Fact>]
let ``abbreviatePath Windows drive path normalizes backslashes and preserves basename and parent`` () =
    // Python parity: test_windows_drive_path
    // Backslashes are normalized to forward slashes before abbreviation.
    let path = @"D:\Documents\GitHub\nanobot\src\utils\helpers.py"
    let result = abbreviatePath path 40
    Assert.True(result.EndsWith("helpers.py"), $"Expected basename preserved: {result}")
    Assert.Contains("nanobot", result)

[<Fact>]
let ``abbreviatePath URL with very tight budget still produces domain-ellipsis-basename`` () =
    // Python parity: test_url_negative_budget_consistent_format
    // When maxLen is smaller than domain + "…/" + basename, the function still
    // emits "domain/…/basename" rather than truncating mid-path.
    let url = "https://a.co/very/deep/path/with/lots/of/segments/and/a/long/basename.txt"
    let result = abbreviatePath url 20
    Assert.Contains("a.co", result)
    Assert.Contains("/…/", result)

// ─── formatToolHints — empty ─────────────────────────────────────────────────

[<Fact>]
let ``formatToolHints returns empty string for empty call list`` () =
    Assert.Equal("", hint [])

// ─── formatToolHints — known tools ──────────────────────────────────────────

[<Fact>]
let ``read_file short path formats as 'read foo.txt'`` () =
    Assert.Equal("read foo.txt", hint [mkCall "read_file" ["path", "foo.txt"]])

[<Fact>]
let ``write_file short path formats as 'write src/app.py'`` () =
    Assert.Equal("write src/app.py", hint [mkCall "write_file" ["path", "src/app.py"]])

[<Fact>]
let ``edit_file formats using file_path key`` () =
    let result = hint [mkCall "edit_file" ["file_path", "src/main.py"]]
    Assert.Contains("main.py", result)
    Assert.StartsWith("edit", result)

[<Fact>]
let ``glob formats as 'glob "**/*.py"'`` () =
    Assert.Equal("glob \"**/*.py\"", hint [mkCall "glob" ["pattern", "**/*.py"]])

[<Fact>]
let ``grep formats as 'grep "TODO|FIXME"'`` () =
    Assert.Equal("grep \"TODO|FIXME\"", hint [mkCall "grep" ["pattern", "TODO|FIXME"]])

[<Fact>]
let ``exec formats as '$ <command>'`` () =
    Assert.Equal("$ npm install", hint [mkCall "exec" ["command", "npm install"]])

[<Fact>]
let ``exec truncates long command with ellipsis`` () =
    let longCmd = "cd /very/long/path && cat file && echo done && sleep 1 && ls -la"
    let result = hint [mkCall "exec" ["command", longCmd]]
    Assert.StartsWith("$ ", result)
    Assert.True(result.Length <= 50, $"Expected ≤ 50 chars but got {result.Length}: {result}")

[<Fact>]
let ``web_search formats as 'search "query"'`` () =
    Assert.Equal("search \"async F#\"", hint [mkCall "web_search" ["query", "async F#"]])

[<Fact>]
let ``web_fetch formats as 'fetch <url>'`` () =
    let result = hint [mkCall "web_fetch" ["url", "https://example.com/api"]]
    Assert.StartsWith("fetch", result)

[<Fact>]
let ``list_dir formats as 'ls <path>'`` () =
    Assert.Equal("ls src", hint [mkCall "list_dir" ["path", "src"]])

// ─── formatToolHints — MCP tools ────────────────────────────────────────────

[<Fact>]
let ``mcp tool with double underscore formats as server::tool`` () =
    let result = hint [mkCall "mcp_github__get_issue" ["issue", "123"]]
    Assert.Contains("github", result)
    Assert.Contains("get_issue", result)

[<Fact>]
let ``mcp tool without args formats as server::tool`` () =
    let result = hint [mkCall "mcp_github__list_repos" []]
    Assert.Contains("github", result)
    Assert.Contains("list_repos", result)

// ─── formatToolHints — fallback tool ─────────────────────────────────────────

[<Fact>]
let ``unknown tool with no args formats as tool name only`` () =
    Assert.Equal("my_custom_tool", hint [mkCall "my_custom_tool" []])

[<Fact>]
let ``unknown tool with short string arg shows arg in quotes`` () =
    let result = hint [mkCall "my_tool" ["input", "hello world"]]
    Assert.Contains("my_tool", result)
    Assert.Contains("hello world", result)

// ─── formatToolHints — deduplication ─────────────────────────────────────────

[<Fact>]
let ``identical consecutive hints are collapsed with x count`` () =
    let calls = [
        mkCall "read_file" ["path", "a.txt"]
        mkCall "read_file" ["path", "a.txt"]
        mkCall "read_file" ["path", "a.txt"]
    ]
    let result = hint calls
    Assert.Contains("×", result)
    Assert.Contains("3", result)

[<Fact>]
let ``different consecutive hints are separated by comma`` () =
    let calls = [
        mkCall "read_file"  ["path", "a.txt"]
        mkCall "write_file" ["path", "b.txt"]
    ]
    let result = hint calls
    Assert.Contains(",", result)
    Assert.Contains("read", result)
    Assert.Contains("write", result)

[<Fact>]
let ``non-consecutive identical hints are not deduplicated`` () =
    let calls = [
        mkCall "read_file"  ["path", "a.txt"]
        mkCall "write_file" ["path", "b.txt"]
        mkCall "read_file"  ["path", "a.txt"]
    ]
    let result = hint calls
    // Should not have × because they are not consecutive
    Assert.False(result.Contains("×"), $"Should not deduplicate non-consecutive hints: {result}")

[<Fact>]
let ``single call does not produce x count`` () =
    let result = hint [mkCall "read_file" ["path", "foo.txt"]]
    Assert.False(result.Contains("×"), $"Single call should not show × count: {result}")

[<Fact>]
let ``same tool consecutive with different args is not folded`` () =
    // Two reads with DIFFERENT paths → distinct hints → no ×
    let calls = [
        mkCall "read_file" ["path", "a.txt"]
        mkCall "read_file" ["path", "b.txt"]
    ]
    let result = hint calls
    Assert.False(result.Contains("×"), $"Different-arg consecutive calls should not fold: {result}")

[<Fact>]
let ``three consecutive same tool different args all listed`` () =
    let calls = [
        mkCall "read_file" ["path", "a.py"]
        mkCall "read_file" ["path", "b.py"]
        mkCall "read_file" ["path", "c.py"]
    ]
    let result = hint calls
    Assert.False(result.Contains("×"), $"Three different-path reads should not fold: {result}")
    // All three hints should appear in the output
    let parts = result.Split(',')
    Assert.Equal(3, parts.Length)

// ─── formatToolHints — exec path abbreviation ────────────────────────────────

[<Fact>]
let ``exec abbreviates Windows path in command`` () =
    let cmd = @"cd D:\Documents\GitHub\nanobot\.worktree\tomain\nanobot && git diff --name-only"
    let result = hint [mkCall "exec" ["command", cmd]]
    Assert.StartsWith("$ ", result)
    Assert.Contains("…/", result)
    Assert.DoesNotContain("Documents", result)

[<Fact>]
let ``exec abbreviates Unix path in command`` () =
    let cmd = "cd /home/user/projects/nanobot/.worktree/tomain && make build"
    let result = hint [mkCall "exec" ["command", cmd]]
    Assert.StartsWith("$ ", result)
    Assert.Contains("…/", result)
    Assert.DoesNotContain("projects", result)

// ─── formatToolHints — exec quoted and home path abbreviation (Python parity) ─

[<Fact>]
let ``exec abbreviates home path in command`` () =
    // Python parity: test_exec_abbreviates_home_paths
    let cmd = "cd ~/projects/nanobot/workspace && pytest tests/"
    let result = hint [mkCall "exec" ["command", cmd]]
    Assert.StartsWith("$ ", result)
    Assert.Contains("…/", result)

[<Fact>]
let ``exec abbreviates quoted Unix path with spaces`` () =
    // Python parity: test_exec_abbreviates_quoted_linux_paths_with_spaces
    let cmd = """cd "/home/user/My Documents/project" && pytest tests/"""
    let result = hint [mkCall "exec" ["command", cmd]]
    Assert.StartsWith("$ ", result)
    Assert.Contains("…/", result)
    Assert.DoesNotContain("/home/user/My Documents/project", result)
    Assert.Contains("\"", result)  // surrounding quotes preserved

[<Fact>]
let ``exec abbreviates quoted Windows path with spaces`` () =
    // Python parity: test_exec_abbreviates_quoted_windows_paths_with_spaces
    let cmd = """cd "C:/Program Files/Git/project" && git status"""
    let result = hint [mkCall "exec" ["command", cmd]]
    Assert.StartsWith("$ ", result)
    Assert.Contains("…/", result)
    Assert.DoesNotContain("C:/Program Files/Git/project", result)
    Assert.Contains("\"", result)  // surrounding quotes preserved

// ─── formatToolHints — write_file with content key ──────────────────────────

[<Fact>]
let ``write_file with both path and content shows only path`` () =
    // Python parity: test_write_file_shows_path_not_content
    // When both path and content are in arguments, only the path is formatted.
    let result = hint [mkCall "write_file" ["path", "docs/api.md"; "content", "# API Reference\n\nLong content..."]]
    Assert.Equal("write docs/api.md", result)

// ─── formatToolHints — exec chained commands ─────────────────────────────────

[<Fact>]
let ``exec chained Windows path command folds path and keeps npm visible`` () =
    // Python parity: test_exec_chained_commands_truncated_not_mid_path
    // Long chained commands should abbreviate the path but leave the command part visible.
    let cmd = @"cd D:\Documents\GitHub\project && npm run build && npm test"
    let result = hint [mkCall "exec" ["command", cmd]]
    Assert.StartsWith("$ ", result)
    Assert.Contains("…/", result)   // path was folded
    Assert.Contains("npm", result)  // chained npm command still visible

// ─── formatToolHints — read long path ────────────────────────────────────────

[<Fact>]
let ``read_file long path preserves basename in output`` () =
    // Python parity: test_read_file_long_path — basename must survive abbreviation
    let longPath = "/home/user/.local/share/uv/tools/nanobot/agent/loop.py"
    let result = hint [mkCall "read_file" ["path", longPath]]
    Assert.Contains("loop.py", result)
    Assert.StartsWith("read ", result)

// ─── formatToolHints — fallback with long arg ────────────────────────────────

[<Fact>]
let ``unknown tool with long arg truncates with ellipsis`` () =
    let longArg = String.replicate 60 "a"
    let result = hint [mkCall "custom_tool" ["data", longArg]]
    Assert.Contains("custom_tool", result)
    Assert.Contains("…", result)
    Assert.True(result.Length < 80, $"Result too long: {result}")

// ─── formatToolHints — numeric (non-string) arg ──────────────────────────────

[<Fact>]
let ``unknown tool with numeric JSON arg shows only tool name`` () =
    // Python parity: test_unknown_tool_no_string_arg
    // Only string-typed JsonElement args are included; numeric args are excluded.
    // So a call with {"count": 42} (Number) shows just "custom_tool", not "custom_tool(\"42\")".
    let call = {
        Id           = ToolCallId "c1"
        Tool         = ToolName "custom_tool"
        Arguments    = Map.ofList [ "count", System.Text.Json.JsonDocument.Parse("42").RootElement ]
        ProviderMeta = None
    }
    let result = hint [call]
    Assert.Equal("custom_tool", result)

// ─── formatToolHints — MCP tool with underscore in server name ───────────────

[<Fact>]
let ``mcp tool with underscore in server portion formats correctly`` () =
    // Python parity: test_mcp_standard_format
    // mcp_4_5v_mcp__analyze_image → server="4_5v_mcp", tool="analyze_image"
    // The server portion may contain underscores; split happens at the first "__".
    let result = hint [mkCall "mcp_4_5v_mcp__analyze_image" ["imageSource", "https://img.jpg"]]
    Assert.Contains("4_5v", result)
    Assert.Contains("analyze_image", result)

// ─── formatToolHints — five interleaved different-arg calls ──────────────────

[<Fact>]
let ``five interleaved different-arg calls all listed without deduplication`` () =
    // Python parity: test_read_read_grep_grep_read
    // All 5 calls have distinct args → no × folding → 5 comma-separated hints.
    let calls = [
        mkCall "read_file"  ["path",    "a.py"]
        mkCall "read_file"  ["path",    "b.py"]
        mkCall "grep"       ["pattern", "x"]
        mkCall "grep"       ["pattern", "y"]
        mkCall "read_file"  ["path",    "c.py"]
    ]
    let result = hint calls
    Assert.False(result.Contains("×"), $"No deduplication expected for 5 unique calls: {result}")
    let parts = result.Split(", ")
    Assert.Equal(5, parts.Length)
