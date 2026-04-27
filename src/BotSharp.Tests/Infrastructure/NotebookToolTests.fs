module BotSharp.Tests.Infrastructure.NotebookToolTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.NotebookTool

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private jsonInt (n: int) =
    JsonDocument.Parse($"{n}").RootElement.Clone()

let private makeArgs (pairs: (string * JsonElement) list) : Map<string, JsonElement> =
    pairs |> Map.ofList

/// Create a temp dir and return its path.
let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "nb-test-" + Guid.NewGuid().ToString("N").[..7])
    Directory.CreateDirectory(dir) |> ignore
    dir

/// Minimal valid notebook JSON as a string.
let private minimalNotebook cells =
    let cellsJson = cells |> String.concat ","
    $"""{{
  "nbformat": 4,
  "nbformat_minor": 5,
  "metadata": {{}},
  "cells": [{cellsJson}]
}}"""

let private codeCell src =
    $"""{{
  "cell_type": "code",
  "source": "{src}",
  "metadata": {{}},
  "outputs": [],
  "execution_count": null
}}"""

// ═══════════════════════════════════════════════════════════════════════════
// Missing required arguments
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit returns ToolFailure when path arg is absent`` () =
    let dir = tempDir ()
    let result = execNotebookEdit dir Map.empty |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for missing path arg, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Blocking non-.ipynb files
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit rejects non-ipynb files`` () =
    let dir = tempDir ()
    let args = makeArgs [ "path", jsonStr "file.py" ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("notebook_edit only works on .ipynb", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Insert mode: create new notebook
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit insert creates notebook when file does not exist`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "new.ipynb")
    let args = makeArgs [
        "path",       jsonStr nbPath
        "new_source", jsonStr "print('hello')"
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("created", msg)
        Assert.True(File.Exists nbPath, "notebook file should exist")
        let json = File.ReadAllText nbPath
        // JsonNode unicode-escapes single quotes → \u0027
        Assert.True(json.Contains("print(") || json.Contains("print(\\u0027hello\\u0027)"),
            "notebook should contain the source")
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``notebook_edit replace returns error when file does not exist`` () =
    let dir = tempDir ()
    let args = makeArgs [
        "path",      jsonStr (Path.Combine(dir, "missing.ipynb"))
        "edit_mode", jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("not found", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Delete mode on missing file
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit delete returns error when file does not exist`` () =
    let dir = tempDir ()
    let args = makeArgs [
        "path",      jsonStr (Path.Combine(dir, "missing.ipynb"))
        "edit_mode", jsonStr "delete"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("not found", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Replace mode
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit replace updates cell source`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "test.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "old code"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "new_source", jsonStr "new code"
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("edited cell 0", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("new code", json)
        Assert.DoesNotContain("old code", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``notebook_edit replace returns error for out-of-range index`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "test.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "cell0"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 5
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("out of range", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Insert mode: existing notebook
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit insert adds cell after target index`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "test.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "cell0"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "new_source", jsonStr "inserted cell"
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("inserted cell at index 1", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("inserted cell", json)
        Assert.Contains("cell0", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Insert with out-of-bounds cellIndex (clamped to end)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit insert with out-of-bounds index appends to end`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "test.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "only-cell"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 99   // way past the end — should clamp to cells.Count
        "new_source", jsonStr "appended"
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.True(msg.Contains("inserted cell"), $"Expected insert confirmation, got: {msg}")
        let json = File.ReadAllText nbPath
        Assert.Contains("appended", json)
        Assert.Contains("only-cell", json)   // original cell preserved
    | other -> Assert.Fail($"Expected ToolSuccess for clamped insert, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Delete mode
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit delete removes cell at index`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "test.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "cell0"; codeCell "cell1"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "edit_mode",  jsonStr "delete"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("deleted cell 0", msg)
        let json = File.ReadAllText nbPath
        Assert.DoesNotContain("cell0", json)
        Assert.Contains("cell1", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``notebook_edit delete returns error for out-of-range index`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "test.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "cell0"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 99
        "edit_mode",  jsonStr "delete"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("out of range", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with error message, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Invalid parameters (parser boundary)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit returns ToolFailure for invalid edit_mode`` () =
    let dir = tempDir ()
    let args = makeArgs [
        "path",      jsonStr (Path.Combine(dir, "x.ipynb"))
        "edit_mode", jsonStr "invalid-mode"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for invalid edit_mode, got {other}")

[<Fact>]
let ``notebook_edit returns ToolFailure for invalid cell_type`` () =
    let dir = tempDir ()
    let args = makeArgs [
        "path",      jsonStr (Path.Combine(dir, "x.ipynb"))
        "cell_type", jsonStr "html"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for invalid cell_type, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Markdown cell
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit insert creates markdown cell`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "md.ipynb")
    let args = makeArgs [
        "path",       jsonStr nbPath
        "new_source", jsonStr "# Header"
        "cell_type",  jsonStr "markdown"
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("created", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("markdown", json)
        Assert.Contains("Header", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``notebook_edit replace changes code cell to markdown`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "change.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "x = 1"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "new_source", jsonStr "# Now markdown"
        "cell_type",  jsonStr "markdown"
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("edited cell 0", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("markdown", json)
        Assert.Contains("Now markdown", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Workspace violation
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit returns ToolFailure for path outside workspace`` () =
    let dir = tempDir ()
    // Use an absolute path outside the workspace
    let outsidePath = "/tmp/outside-workspace/evil.ipynb"
    let args = makeArgs [
        "path",      jsonStr outsidePath
        "edit_mode", jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolFailure (WorkspaceViolation _) -> ()
    | other -> Assert.Fail($"Expected ToolFailure(WorkspaceViolation), got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Corrupt / invalid notebook JSON
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit replace returns error for unparsable notebook`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "corrupt.ipynb")
    File.WriteAllText(nbPath, "this is not valid json {{{{")
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("Error", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with Error message, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Notebook without "cells" key — auto-creates empty cells array
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit insert into notebook without cells key creates the array`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "nocells.ipynb")
    // Valid JSON but no "cells" property — the tool must synthesize an empty array
    File.WriteAllText(nbPath, """{"nbformat":4,"nbformat_minor":5,"metadata":{}}""")
    let args = makeArgs [
        "path",       jsonStr nbPath
        "new_source", jsonStr "x = 1"
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("inserted cell", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("x = 1", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Replace: markdown → code type change (adds outputs and execution_count)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit replace converts markdown cell to code cell`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "md2code.ipynb")
    // Start with a markdown cell (no outputs/execution_count keys)
    let mdCell = """{"cell_type":"markdown","source":"# Title","metadata":{}}"""
    File.WriteAllText(nbPath, minimalNotebook [mdCell])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "new_source", jsonStr "x = 42"
        "cell_type",  jsonStr "code"     // converting to code → outputs/execution_count added
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("edited cell 0", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("code", json)
        Assert.Contains("outputs", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// generateId = false: notebook with nbformat_minor < 5 — no "id" field
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit insert into nbformat_minor 4 notebook does not add id field`` () =
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "old.ipynb")
    // nbformat_minor = 4 → generateId = false → no "id" field on inserted cell
    let oldFmt = """{"nbformat":4,"nbformat_minor":4,"metadata":{},"cells":[]}"""
    File.WriteAllText(nbPath, oldFmt)
    let args = makeArgs [
        "path",       jsonStr nbPath
        "new_source", jsonStr "y = 2"
        "cell_index", jsonInt 0
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("inserted cell", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("y = 2", json)
        // No cell "id" field should be present in old-format notebook
        Assert.DoesNotContain("\"id\":", json)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// Python parity: test_preserves_metadata_and_outputs
[<Fact>]
let ``notebook_edit replace preserves notebook-level metadata`` () =
    // Python parity: test_preserves_metadata_and_outputs
    // Replacing a cell's source must not destroy the notebook's metadata block.
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "meta.ipynb")
    let nb = """{"nbformat":4,"nbformat_minor":5,"metadata":{"kernelspec":{"display_name":"Python 3","language":"python","name":"python3"}},"cells":[{"cell_type":"code","source":"old","outputs":[],"execution_count":null,"metadata":{}}]}"""
    File.WriteAllText(nbPath, nb)
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "edit_mode",  jsonStr "replace"
        "new_source", jsonStr "new"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess _ ->
        let saved = File.ReadAllText nbPath
        Assert.Contains("\"language\"", saved)
        Assert.Contains("\"python\"", saved)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// Python parity: test_nbformat_45_generates_cell_id
[<Fact>]
let ``notebook_edit insert into nbformat_minor 5 notebook generates cell id`` () =
    // Python parity: test_nbformat_45_generates_cell_id
    // nbformat_minor = 5 → generateId = true → inserted cell must have an "id" field
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "v5.ipynb")
    let v5Fmt = """{"nbformat":4,"nbformat_minor":5,"metadata":{},"cells":[]}"""
    File.WriteAllText(nbPath, v5Fmt)
    let args = makeArgs [
        "path",       jsonStr nbPath
        "new_source", jsonStr "x = 1"
        "cell_index", jsonInt 0
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess _ ->
        let json = File.ReadAllText nbPath
        Assert.Contains("\"id\"", json)
    | other -> Assert.Fail($"Expected ToolSuccess with id field, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Negative cell_index
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``notebook_edit replace with negative cell_index returns out-of-range error`` () =
    // cellIndex < 0 → outOfRange = true → "out of range" message
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "neg.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "cell0"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt -1
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("out of range", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with out-of-range message, got {other}")

[<Fact>]
let ``notebook_edit delete with negative cell_index returns out-of-range error`` () =
    // cellIndex < 0 → outOfRange = true → "out of range" message
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "neg.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "cell0"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt -1
        "edit_mode",  jsonStr "delete"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("out of range", msg)
    | other -> Assert.Fail($"Expected ToolSuccess with out-of-range message, got {other}")

[<Fact>]
let ``notebook_edit insert with negative cell_index inserts at the beginning`` () =
    // insertAt = min ((-1) + 1) cells.Count = min 0 n = 0 → prepend
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "neg.ipynb")
    File.WriteAllText(nbPath, minimalNotebook [codeCell "existing"])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt -1
        "new_source", jsonStr "prepended"
        "edit_mode",  jsonStr "insert"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("inserted cell at index 0", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("prepended", json)
        Assert.Contains("existing", json)
    | other -> Assert.Fail($"Expected ToolSuccess for prepend-insert, got {other}")

[<Fact>]
let ``notebook_edit replace cell with no cell_type property treats it as code`` () =
    // | _ -> "code" fallback in existingType when cell lacks cell_type property.
    // Requesting cell_type=code → existingType = newTypeStr → no type change.
    let dir = tempDir ()
    let nbPath = Path.Combine(dir, "notype.ipynb")
    // Cell has no "cell_type" key — unusual but valid JSON for this test
    let noTypeCell = """{"source":"x=1","metadata":{}}"""
    File.WriteAllText(nbPath, minimalNotebook [noTypeCell])
    let args = makeArgs [
        "path",       jsonStr nbPath
        "cell_index", jsonInt 0
        "new_source", jsonStr "x=2"
        "cell_type",  jsonStr "code"
        "edit_mode",  jsonStr "replace"
    ]
    let result = execNotebookEdit dir args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("edited cell 0", msg)
        let json = File.ReadAllText nbPath
        Assert.Contains("x=2", json)
    | other -> Assert.Fail($"Expected ToolSuccess for no-cell_type replace, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// allTools registration
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allTools returns 1 tool named notebook_edit`` () =
    let tools = allTools "/tmp"
    Assert.Equal(1, List.length tools)
    let (spec, _) = List.head tools
    let (ToolName n) = spec.Name
    Assert.Equal("notebook_edit", n)
