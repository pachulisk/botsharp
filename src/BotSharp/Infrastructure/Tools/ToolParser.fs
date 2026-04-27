module BotSharp.Infrastructure.Tools.ToolParser

open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.Json

// ═══════════════════════════════════════════════════════════════════════════
// Tool argument parser + spec validator
//
// The LLM sends tool arguments as a JSON object.  These functions convert
// the raw JsonElement map into typed arguments and validate them against the
// ToolSpec's parameter schema.
// ═══════════════════════════════════════════════════════════════════════════

/// Extract the top-level keys from a JSON object element.
/// Already done by LlmResponseParser.parseToolCall; exposed here for testing.
let parseArguments (el: JsonElement) : Result<Map<string, JsonElement>, ParseError> =
    if el.ValueKind <> JsonValueKind.Object then
        Error (SchemaError ("arguments", $"expected JSON object, got {el.ValueKind}"))
    else
        el.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
        |> Result.Ok

/// Validate a parsed argument map against a ToolSpec's parameter schema.
/// Returns Ok() when all required parameters are present; ToolError otherwise.
let validateAgainstSpec
    (spec : ToolSpec)
    (args : Map<string, JsonElement>)
    : Result<unit, ToolError> =
    let missingRequired =
        spec.Parameters
        |> Map.filter (fun _ prop -> prop.Required)
        |> Map.filter (fun key _ -> not (args.ContainsKey key))
        |> Map.toList
        |> List.map fst

    match missingRequired with
    | []      -> Result.Ok ()
    | key :: _ -> Result.Error (ParameterMissing key)

/// Look up a string argument, returning ParameterMissing/ParameterInvalid on error.
let requireStringArg (field: string) (args: Map<string, JsonElement>) : Result<string, ToolError> =
    match args.TryFind field with
    | None   -> Result.Error (ParameterMissing field)
    | Some v ->
        if v.ValueKind = JsonValueKind.String then
            match v.GetString() with
            | null -> Result.Error (ParameterInvalid (field, "null string"))
            | s    -> Result.Ok s
        else
            Result.Error (ParameterInvalid (field, $"expected string, got {v.ValueKind}"))

/// Look up an optional string argument.
let tryStringArg (field: string) (args: Map<string, JsonElement>) : string option =
    args.TryFind field
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.String then
            match v.GetString() with
            | null -> None
            | s    -> Some s
        else None)

/// Look up an int argument.
let requireIntArg (field: string) (args: Map<string, JsonElement>) : Result<int, ToolError> =
    match args.TryFind field with
    | None   -> Result.Error (ParameterMissing field)
    | Some v ->
        if v.ValueKind = JsonValueKind.Number then
            Result.Ok (v.GetInt32())
        else
            Result.Error (ParameterInvalid (field, $"expected number, got {v.ValueKind}"))

/// Look up an optional int argument.
let tryIntArg (field: string) (args: Map<string, JsonElement>) : int option =
    args.TryFind field
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.Number then Some (v.GetInt32())
        else None)

/// Look up an optional bool argument.
let tryBoolArg (field: string) (args: Map<string, JsonElement>) : bool option =
    args.TryFind field
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.True  -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None)

/// Look up an optional string-array argument. Returns None if absent or not an array.
/// Silently skips non-string elements (same lenient approach as Python).
let tryStringArrayArg (field: string) (args: Map<string, JsonElement>) : string list option =
    args.TryFind field
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.Array then
            let items =
                v.EnumerateArray()
                |> Seq.choose (fun el ->
                    if el.ValueKind = JsonValueKind.String then el.GetString() |> Option.ofObj
                    else None)
                |> Seq.toList
            Some items
        else None)
