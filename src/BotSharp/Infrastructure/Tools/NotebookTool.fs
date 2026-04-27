module BotSharp.Infrastructure.Tools.NotebookTool

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// NotebookTool — edit Jupyter .ipynb notebook cells
//
// Behavioural parity with Python's NotebookEditTool:
//   • Three edit modes (replace/insert/delete) via CellEditMode DU — no
//     string discrimination; all match points are exhaustive.
//   • Two cell types (code/markdown) via CellType DU.
//   • Creating a new notebook on insert when the file doesn't exist.
//   • cell_index is 0-based.
//   • Auto-generates cell IDs for notebooks with nbformat_minor ≥ 5.
// ═══════════════════════════════════════════════════════════════════════════

// ── Type-driven: illegal modes / cell types are unrepresentable ───────────

/// Cell edit mode: structurally distinct from any raw string.
type private CellEditMode = Replace | Insert | Delete

/// Cell type: structurally distinct from any raw string.
type private CellType = Code | Markdown

/// Parser that converts string arg → typed DU at the argument boundary.
/// After this call, callers see CellEditMode — not a string.
let private parseCellEditMode (raw: string) : Result<CellEditMode, ToolError> =
    match raw.Trim().ToLowerInvariant() with
    | "replace" -> Ok Replace
    | "insert"  -> Ok Insert
    | "delete"  -> Ok Delete
    | other     -> Error (ParameterInvalid ("edit_mode",
                            $"must be 'replace', 'insert', or 'delete'; got '{other}'"))

let private parseCellType (raw: string) : Result<CellType, ToolError> =
    match raw.Trim().ToLowerInvariant() with
    | "code"     -> Ok Code
    | "markdown" -> Ok Markdown
    | other      -> Error (ParameterInvalid ("cell_type",
                            $"must be 'code' or 'markdown'; got '{other}'"))

// ── Notebook helpers ──────────────────────────────────────────────────────

let private newCellId () = Guid.NewGuid().ToString("N").[..7]

/// Build a new cell JsonObject (matches Python's _new_cell).
let private newCell (source: string) (cellType: CellType) (generateId: bool) : JsonObject =
    let cell = JsonObject()
    cell["cell_type"] <- JsonValue.Create(match cellType with Code -> "code" | Markdown -> "markdown")
    cell["source"]    <- JsonValue.Create(source)
    cell["metadata"]  <- JsonObject()
    match cellType with
    | Code ->
        cell["outputs"]         <- JsonArray()
        cell["execution_count"] <- JsonValue.Create<int Nullable>(Nullable())
    | Markdown -> ()
    if generateId then cell["id"] <- JsonValue.Create(newCellId ())
    cell

/// Build an empty notebook (matches Python's _make_empty_notebook).
let private makeEmptyNotebook () : JsonObject =
    let meta    = JsonObject()
    let kernel  = JsonObject()
    kernel["display_name"] <- JsonValue.Create("Python 3")
    kernel["language"]     <- JsonValue.Create("python")
    kernel["name"]         <- JsonValue.Create("python3")
    let lang    = JsonObject()
    lang["name"] <- JsonValue.Create("python")
    meta["kernelspec"]      <- kernel
    meta["language_info"]   <- lang
    let nb = JsonObject()
    nb["nbformat"]       <- JsonValue.Create(4)
    nb["nbformat_minor"] <- JsonValue.Create(5)
    nb["metadata"]       <- meta
    nb["cells"]          <- JsonArray()
    nb

// ── Workspace guard ───────────────────────────────────────────────────────

let private resolvePath (workspacePath: string) (path: string) : Result<string, ToolError> =
    let full =
        if Path.IsPathRooted(path) then path
        else Path.GetFullPath(Path.Combine(workspacePath, path))
    let workspace = Path.GetFullPath(workspacePath)
    if full.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) then Ok full
    else Error (WorkspaceViolation full)

// ── Tool implementation ───────────────────────────────────────────────────

let execNotebookEdit
    (workspacePath : string)
    (args          : Map<string, JsonElement>)
    : Async<ToolResult> =
    async {
        // ── Parse args at the boundary ────────────────────────────────
        match requireStringArg "path" args with
        | Error e -> return ToolFailure e
        | Ok rawPath ->

        if not (rawPath.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase)) then
            return ToolSuccess
                "Error: notebook_edit only works on .ipynb files. Use edit_file for other files."
        else

        let cellIndex = tryIntArg "cell_index" args |> Option.defaultValue 0

        let newSource = tryStringArg "new_source" args |> Option.defaultValue ""

        let cellTypeRaw = tryStringArg "cell_type" args |> Option.defaultValue "code"
        match parseCellType cellTypeRaw with
        | Error e -> return ToolFailure e
        | Ok cellType ->

        let editModeRaw = tryStringArg "edit_mode" args |> Option.defaultValue "replace"
        match parseCellEditMode editModeRaw with
        | Error e -> return ToolFailure e
        | Ok editMode ->

        match resolvePath workspacePath rawPath with
        | Error e -> return ToolFailure e
        | Ok fullPath ->

        try
            // ── Handle missing file ───────────────────────────────────
            if not (File.Exists fullPath) then
                match editMode with
                | Insert ->
                    let nb   = makeEmptyNotebook ()
                    let cell = newCell newSource cellType true
                    (Unchecked.nonNull nb["cells"] :?> JsonArray).Add(cell)
                    Directory.CreateDirectory(Unchecked.nonNull (Path.GetDirectoryName(fullPath))) |> ignore
                    let json = nb.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                    do! File.WriteAllTextAsync(fullPath, json) |> Async.AwaitTask
                    return ToolSuccess $"Successfully created {fullPath} with 1 cell"
                | Replace | Delete ->
                    return ToolSuccess $"Error: File not found: {rawPath}"
            else

            // ── Load existing notebook ────────────────────────────────
            let! text = File.ReadAllTextAsync(fullPath) |> Async.AwaitTask
            let parsed =
                try Some (Unchecked.nonNull (JsonNode.Parse(text)) :?> JsonObject)
                with _ -> None
            match parsed with
            | None -> return ToolSuccess $"Error: Failed to parse notebook: {rawPath}"
            | Some nb ->

            let cells =
                match nb.TryGetPropertyValue("cells") with
                | true, arr -> Unchecked.nonNull arr :?> JsonArray
                | _         ->
                    let arr = JsonArray()
                    nb["cells"] <- arr
                    arr

            let nbformat      = match nb.TryGetPropertyValue("nbformat") with | true, v -> (Unchecked.nonNull v).GetValue<int>() | _ -> 0
            let nbformatMinor = match nb.TryGetPropertyValue("nbformat_minor") with | true, v -> (Unchecked.nonNull v).GetValue<int>() | _ -> 0
            let generateId    = nbformat >= 4 && nbformatMinor >= 5

            // ── Apply edit mode (exhaustive DU match) ─────────────────
            match editMode with
            | Delete ->
                let outOfRange = cellIndex < 0 || cellIndex >= cells.Count
                if outOfRange then
                    return ToolSuccess
                        $"Error: cell_index {cellIndex} out of range (notebook has {cells.Count} cells)"
                else
                    cells.RemoveAt(cellIndex)
                    nb["cells"] <- cells
                    let json = nb.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                    do! File.WriteAllTextAsync(fullPath, json) |> Async.AwaitTask
                    return ToolSuccess $"Successfully deleted cell {cellIndex} from {fullPath}"

            | Insert ->
                let insertAt = min (cellIndex + 1) cells.Count
                let cell = newCell newSource cellType generateId
                cells.Insert(insertAt, cell)
                nb["cells"] <- cells
                let json = nb.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                do! File.WriteAllTextAsync(fullPath, json) |> Async.AwaitTask
                return ToolSuccess $"Successfully inserted cell at index {insertAt} in {fullPath}"

            | Replace ->
                let outOfRange = cellIndex < 0 || cellIndex >= cells.Count
                if outOfRange then
                    return ToolSuccess
                        $"Error: cell_index {cellIndex} out of range (notebook has {cells.Count} cells)"
                else
                    let cell = Unchecked.nonNull cells.[cellIndex] :?> JsonObject
                    cell["source"] <- JsonValue.Create(newSource)
                    // Update cell type if changed
                    let existingType =
                        match cell.TryGetPropertyValue("cell_type") with
                        | true, v -> (Unchecked.nonNull v).GetValue<string>()
                        | _       -> "code"
                    let newTypeStr = match cellType with Code -> "code" | Markdown -> "markdown"
                    if existingType <> newTypeStr then
                        cell["cell_type"] <- JsonValue.Create(newTypeStr)
                        match cellType with
                        | Code ->
                            if not (cell.ContainsKey("outputs"))
                            then cell["outputs"] <- JsonArray()
                            if not (cell.ContainsKey("execution_count"))
                            then cell["execution_count"] <- JsonValue.Create<int Nullable>(Nullable())
                        | Markdown ->
                            cell.Remove("outputs")         |> ignore
                            cell.Remove("execution_count") |> ignore
                    nb["cells"] <- cells
                    let json = nb.ToJsonString(JsonSerializerOptions(WriteIndented = true))
                    do! File.WriteAllTextAsync(fullPath, json) |> Async.AwaitTask
                    return ToolSuccess $"Successfully edited cell {cellIndex} in {fullPath}"

        with ex ->
            return ToolSuccess $"Error editing notebook: {ex.Message}"
    }

// ── Tool spec ──────────────────────────────────────────────────────────────

let notebookEditSpec : ToolSpec = {
    Name            = ToolName "notebook_edit"
    Description     =
        "Edit a Jupyter notebook (.ipynb) cell. " +
        "Modes: replace (default) replaces cell content, " +
        "insert adds a new cell after the target index, " +
        "delete removes the cell at the index. " +
        "cell_index is 0-based. Creates the notebook if it doesn't exist and mode is insert."
    Parameters      = Map.ofList [
        "path",       { Type = JsString; Description = "Path to the .ipynb notebook file"; Required = true }
        "cell_index", { Type = JsNumber; Description = "0-based cell index (default 0)"; Required = false }
        "new_source", { Type = JsString; Description = "New source content for the cell"; Required = false }
        "cell_type",  { Type = JsEnum ["code"; "markdown"]
                        Description = "Cell type: 'code' or 'markdown' (default: code)"; Required = false }
        "edit_mode",  { Type = JsEnum ["replace"; "insert"; "delete"]
                        Description = "Edit mode: 'replace' (default), 'insert', or 'delete'"; Required = false }
    ]
    ConcurrencySafe = false  // mutates notebook files
}

let allTools (workspacePath: string)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ notebookEditSpec, execNotebookEdit workspacePath ]
