module BotSharp.Tests.Infrastructure.ToolParserTests

open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private jsonInt (n: int) =
    JsonDocument.Parse($"{n}").RootElement.Clone()

let private jsonObj (pairs: (string * string) list) =
    let body =
        pairs
        |> List.map (fun (k, v) -> $"\"{k}\":\"{v}\"")
        |> String.concat ","
    JsonDocument.Parse($"{{{body}}}").RootElement.Clone()

let private jsonBool (b: bool) =
    JsonDocument.Parse(if b then "true" else "false").RootElement.Clone()

let private jsonArray () =
    JsonDocument.Parse("[1,2,3]").RootElement.Clone()

/// Build a minimal ToolSpec with the given parameter map.
let private makeSpec (parameters: Map<string, JsonSchemaProperty>) =
    { Name        = ToolName "test_tool"
      Description = "A tool for testing"
      Parameters  = parameters
      ConcurrencySafe = false }

/// Convenience: a required string property.
let private requiredProp =
    { Type = JsString; Description = ""; Required = true }

/// Convenience: an optional string property.
let private optionalProp =
    { Type = JsString; Description = ""; Required = false }

// ═══════════════════════════════════════════════════════════════════════════
// parseArguments
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseArguments: JSON object with string values returns Ok map`` () =
    let el = jsonObj [ "path", "./foo.txt"; "mode", "read" ]
    match parseArguments el with
    | Ok args ->
        Assert.True(args.ContainsKey("path"))
        Assert.True(args.ContainsKey("mode"))
        Assert.Equal(JsonValueKind.String, args["path"].ValueKind)
        Assert.Equal("./foo.txt", args["path"].GetString())
        Assert.Equal("read", args["mode"].GetString())
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact>]
let ``parseArguments: empty JSON object returns Ok empty map`` () =
    let el = JsonDocument.Parse("{}").RootElement.Clone()
    match parseArguments el with
    | Ok args -> Assert.Empty(args)
    | Error e -> Assert.Fail($"Expected Ok empty map, got Error {e}")

[<Fact>]
let ``parseArguments: JSON array returns SchemaError`` () =
    let el = jsonArray ()
    match parseArguments el with
    | Error (SchemaError ("arguments", msg)) ->
        Assert.Contains("Array", msg)
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

[<Fact>]
let ``parseArguments: JSON string returns SchemaError`` () =
    let el = jsonStr "not an object"
    match parseArguments el with
    | Error (SchemaError ("arguments", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

[<Fact>]
let ``parseArguments: JSON number returns SchemaError`` () =
    let el = jsonInt 42
    match parseArguments el with
    | Error (SchemaError ("arguments", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// validateAgainstSpec
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``validateAgainstSpec: all required params present returns Ok ()`` () =
    let spec = makeSpec (Map.ofList [ "path", requiredProp; "mode", requiredProp ])
    let args = Map.ofList [ "path", jsonStr "./foo.txt"; "mode", jsonStr "read" ]
    match validateAgainstSpec spec args with
    | Ok () -> ()
    | Error e -> Assert.Fail($"Expected Ok (), got Error {e}")

[<Fact>]
let ``validateAgainstSpec: missing required param returns ParameterMissing`` () =
    let spec = makeSpec (Map.ofList [ "path", requiredProp ])
    let args = Map.empty
    match validateAgainstSpec spec args with
    | Error (ParameterMissing "path") -> ()
    | other -> Assert.Fail($"Expected ParameterMissing \"path\", got {other}")

[<Fact>]
let ``validateAgainstSpec: optional param absent returns Ok ()`` () =
    let spec = makeSpec (Map.ofList [ "filter", optionalProp ])
    let args = Map.empty
    match validateAgainstSpec spec args with
    | Ok () -> ()
    | Error e -> Assert.Fail($"Expected Ok (), got Error {e}")

[<Fact>]
let ``validateAgainstSpec: no parameters returns Ok ()`` () =
    let spec = makeSpec Map.empty
    let args = Map.empty
    match validateAgainstSpec spec args with
    | Ok () -> ()
    | Error e -> Assert.Fail($"Expected Ok () for empty spec, got Error {e}")

[<Fact>]
let ``validateAgainstSpec: extra args beyond spec are ignored`` () =
    let spec = makeSpec (Map.ofList [ "path", requiredProp ])
    let args = Map.ofList [ "path", jsonStr "x"; "extra", jsonStr "y" ]
    match validateAgainstSpec spec args with
    | Ok () -> ()
    | Error e -> Assert.Fail($"Expected Ok () ignoring extra args, got Error {e}")

[<Fact>]
let ``validateAgainstSpec: first missing required param name is reported`` () =
    // Only one required param missing; check the field name is propagated.
    let spec = makeSpec (Map.ofList [ "query", requiredProp ])
    let args = Map.empty
    match validateAgainstSpec spec args with
    | Error (ParameterMissing field) -> Assert.Equal("query", field)
    | other -> Assert.Fail($"Expected ParameterMissing \"query\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// requireStringArg
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireStringArg: present string field returns Ok value`` () =
    let args = Map.ofList [ "name", jsonStr "alice" ]
    match requireStringArg "name" args with
    | Ok "alice" -> ()
    | other -> Assert.Fail($"Expected Ok \"alice\", got {other}")

[<Fact>]
let ``requireStringArg: missing field returns ParameterMissing`` () =
    let args = Map.empty
    match requireStringArg "name" args with
    | Error (ParameterMissing "name") -> ()
    | other -> Assert.Fail($"Expected ParameterMissing \"name\", got {other}")

[<Fact>]
let ``requireStringArg: number field returns ParameterInvalid`` () =
    let args = Map.ofList [ "count", jsonInt 7 ]
    match requireStringArg "count" args with
    | Error (ParameterInvalid ("count", reason)) ->
        Assert.Contains("Number", reason)
    | other -> Assert.Fail($"Expected ParameterInvalid (\"count\", _), got {other}")

[<Fact>]
let ``requireStringArg: boolean field returns ParameterInvalid`` () =
    let args = Map.ofList [ "flag", jsonBool true ]
    match requireStringArg "flag" args with
    | Error (ParameterInvalid ("flag", _)) -> ()
    | other -> Assert.Fail($"Expected ParameterInvalid, got {other}")

[<Fact>]
let ``requireStringArg: field name is preserved in ParameterMissing`` () =
    let args = Map.empty
    match requireStringArg "workspace" args with
    | Error (ParameterMissing field) -> Assert.Equal("workspace", field)
    | other -> Assert.Fail($"Expected ParameterMissing \"workspace\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryStringArg
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryStringArg: present string field returns Some value`` () =
    let args = Map.ofList [ "tag", jsonStr "v1.0" ]
    Assert.Equal(Some "v1.0", tryStringArg "tag" args)

[<Fact>]
let ``tryStringArg: missing field returns None`` () =
    let args = Map.empty
    Assert.Equal(None, tryStringArg "tag" args)

[<Fact>]
let ``tryStringArg: number field returns None`` () =
    let args = Map.ofList [ "count", jsonInt 3 ]
    Assert.Equal(None, tryStringArg "count" args)

[<Fact>]
let ``tryStringArg: boolean field returns None`` () =
    let args = Map.ofList [ "enabled", jsonBool false ]
    Assert.Equal(None, tryStringArg "enabled" args)

[<Fact>]
let ``tryStringArg: array field returns None`` () =
    let args = Map.ofList [ "items", jsonArray () ]
    Assert.Equal(None, tryStringArg "items" args)

// ═══════════════════════════════════════════════════════════════════════════
// requireIntArg
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireIntArg: present number field returns Ok value`` () =
    let args = Map.ofList [ "limit", jsonInt 100 ]
    match requireIntArg "limit" args with
    | Ok 100 -> ()
    | other -> Assert.Fail($"Expected Ok 100, got {other}")

[<Fact>]
let ``requireIntArg: zero is a valid int`` () =
    let args = Map.ofList [ "offset", jsonInt 0 ]
    match requireIntArg "offset" args with
    | Ok 0 -> ()
    | other -> Assert.Fail($"Expected Ok 0, got {other}")

[<Fact>]
let ``requireIntArg: missing field returns ParameterMissing`` () =
    let args = Map.empty
    match requireIntArg "limit" args with
    | Error (ParameterMissing "limit") -> ()
    | other -> Assert.Fail($"Expected ParameterMissing \"limit\", got {other}")

[<Fact>]
let ``requireIntArg: string field returns ParameterInvalid`` () =
    let args = Map.ofList [ "limit", jsonStr "fifty" ]
    match requireIntArg "limit" args with
    | Error (ParameterInvalid ("limit", reason)) ->
        Assert.Contains("String", reason)
    | other -> Assert.Fail($"Expected ParameterInvalid (\"limit\", _), got {other}")

[<Fact>]
let ``requireIntArg: boolean field returns ParameterInvalid`` () =
    let args = Map.ofList [ "limit", jsonBool true ]
    match requireIntArg "limit" args with
    | Error (ParameterInvalid ("limit", _)) -> ()
    | other -> Assert.Fail($"Expected ParameterInvalid, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryIntArg
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryIntArg: present number field returns Some value`` () =
    let args = Map.ofList [ "page", jsonInt 3 ]
    Assert.Equal(Some 3, tryIntArg "page" args)

[<Fact>]
let ``tryIntArg: missing field returns None`` () =
    let args = Map.empty
    Assert.Equal(None, tryIntArg "page" args)

[<Fact>]
let ``tryIntArg: string field returns None`` () =
    let args = Map.ofList [ "page", jsonStr "3" ]
    Assert.Equal(None, tryIntArg "page" args)

[<Fact>]
let ``tryIntArg: boolean field returns None`` () =
    let args = Map.ofList [ "page", jsonBool true ]
    Assert.Equal(None, tryIntArg "page" args)

// ═══════════════════════════════════════════════════════════════════════════
// tryStringArrayArg
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStringArray (items: string list) =
    let body = items |> List.map (fun s -> $"\"{s}\"") |> String.concat ","
    JsonDocument.Parse($"[{body}]").RootElement.Clone()

[<Fact>]
let ``tryStringArrayArg: string array field returns Some list`` () =
    let args = Map.ofList [ "files", jsonStringArray ["a.txt"; "b.txt"] ]
    match tryStringArrayArg "files" args with
    | Some ["a.txt"; "b.txt"] -> ()
    | other -> Assert.Fail($"Expected Some [\"a.txt\"; \"b.txt\"], got {other}")

[<Fact>]
let ``tryStringArrayArg: empty array returns Some empty list`` () =
    let args = Map.ofList [ "files", jsonStringArray [] ]
    match tryStringArrayArg "files" args with
    | Some [] -> ()
    | other -> Assert.Fail($"Expected Some [], got {other}")

[<Fact>]
let ``tryStringArrayArg: missing field returns None`` () =
    let args = Map.empty
    Assert.Equal(None, tryStringArrayArg "files" args)

[<Fact>]
let ``tryStringArrayArg: string field (not array) returns None`` () =
    let args = Map.ofList [ "files", jsonStr "single.txt" ]
    Assert.Equal(None, tryStringArrayArg "files" args)

[<Fact>]
let ``tryStringArrayArg: mixed array skips non-string elements`` () =
    let arr = JsonDocument.Parse("[\"a.txt\", 42, \"b.txt\", true]").RootElement.Clone()
    let args = Map.ofList [ "files", arr ]
    match tryStringArrayArg "files" args with
    | Some ["a.txt"; "b.txt"] -> ()
    | other -> Assert.Fail($"Expected non-string elements skipped, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryBoolArg
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryBoolArg: true JSON boolean returns Some true`` () =
    let v = JsonDocument.Parse("true").RootElement.Clone()
    let args = Map.ofList [ "flag", v ]
    Assert.Equal(Some true, tryBoolArg "flag" args)

[<Fact>]
let ``tryBoolArg: false JSON boolean returns Some false`` () =
    let v = JsonDocument.Parse("false").RootElement.Clone()
    let args = Map.ofList [ "flag", v ]
    Assert.Equal(Some false, tryBoolArg "flag" args)

[<Fact>]
let ``tryBoolArg: missing field returns None`` () =
    Assert.Equal(None, tryBoolArg "flag" Map.empty)

[<Fact>]
let ``tryBoolArg: string field returns None`` () =
    let v = JsonDocument.Parse("\"true\"").RootElement.Clone()
    let args = Map.ofList [ "flag", v ]
    Assert.Equal(None, tryBoolArg "flag" args)

[<Fact>]
let ``tryBoolArg: number field returns None`` () =
    let v = JsonDocument.Parse("1").RootElement.Clone()
    let args = Map.ofList [ "flag", v ]
    Assert.Equal(None, tryBoolArg "flag" args)
