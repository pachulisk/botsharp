module BotSharp.Tests.Infrastructure.JsonTests

open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.Json

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

/// Parse a JSON string and return the root element (cloned for safety).
let private el (json: string) =
    JsonDocument.Parse(json).RootElement.Clone()

// ═══════════════════════════════════════════════════════════════════════════
// tryGetProp
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryGetProp: present key returns Some element`` () =
    let root = el """{"name":"alice"}"""
    match tryGetProp "name" root with
    | Some v -> Assert.Equal("alice", v.GetString())
    | None   -> Assert.Fail("Expected Some, got None")

[<Fact>]
let ``tryGetProp: missing key returns None`` () =
    let root = el """{"name":"alice"}"""
    Assert.Equal(None, tryGetProp "missing" root)

// ═══════════════════════════════════════════════════════════════════════════
// requireProp
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireProp: present key returns Ok element`` () =
    let root = el """{"x":42}"""
    match requireProp "x" root with
    | Ok v  -> Assert.Equal(42, v.GetInt32())
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact>]
let ``requireProp: missing key returns Error MissingField`` () =
    let root = el """{"x":42}"""
    match requireProp "y" root with
    | Error (MissingField "y") -> ()
    | other -> Assert.Fail($"Expected MissingField \"y\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// requireString
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireString: present string property returns Ok value`` () =
    let root = el """{"greeting":"hello"}"""
    match requireString "greeting" root with
    | Ok "hello" -> ()
    | other -> Assert.Fail($"Expected Ok \"hello\", got {other}")

[<Fact>]
let ``requireString: missing property returns Error MissingField`` () =
    let root = el """{}"""
    match requireString "greeting" root with
    | Error (MissingField "greeting") -> ()
    | other -> Assert.Fail($"Expected MissingField, got {other}")

[<Fact>]
let ``requireString: number property returns Error SchemaError`` () =
    let root = el """{"count":5}"""
    match requireString "count" root with
    | Error (SchemaError ("count", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryGetString
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryGetString: present string property returns Some value`` () =
    let root = el """{"label":"foo"}"""
    Assert.Equal(Some "foo", tryGetString "label" root)

[<Fact>]
let ``tryGetString: missing property returns None`` () =
    let root = el """{}"""
    Assert.Equal(None, tryGetString "label" root)

[<Fact>]
let ``tryGetString: number property returns None`` () =
    let root = el """{"n":99}"""
    Assert.Equal(None, tryGetString "n" root)

// ═══════════════════════════════════════════════════════════════════════════
// requireInt
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireInt: present integer property returns Ok value`` () =
    let root = el """{"count":7}"""
    match requireInt "count" root with
    | Ok 7  -> ()
    | other -> Assert.Fail($"Expected Ok 7, got {other}")

[<Fact>]
let ``requireInt: missing property returns Error MissingField`` () =
    let root = el """{}"""
    match requireInt "count" root with
    | Error (MissingField "count") -> ()
    | other -> Assert.Fail($"Expected MissingField, got {other}")

[<Fact>]
let ``requireInt: string property returns Error SchemaError`` () =
    let root = el """{"count":"seven"}"""
    match requireInt "count" root with
    | Error (SchemaError ("count", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryGetInt
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryGetInt: present integer property returns Some value`` () =
    let root = el """{"page":3}"""
    Assert.Equal(Some 3, tryGetInt "page" root)

[<Fact>]
let ``tryGetInt: missing property returns None`` () =
    let root = el """{}"""
    Assert.Equal(None, tryGetInt "page" root)

[<Fact>]
let ``tryGetInt: string property returns None`` () =
    let root = el """{"page":"three"}"""
    Assert.Equal(None, tryGetInt "page" root)

// ═══════════════════════════════════════════════════════════════════════════
// requireFloat
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireFloat: present float property returns Ok value`` () =
    let root = el """{"temp":3.14}"""
    match requireFloat "temp" root with
    | Ok f  -> Assert.Equal(3.14, f, 6)
    | other -> Assert.Fail($"Expected Ok float, got {other}")

[<Fact>]
let ``requireFloat: missing property returns Error MissingField`` () =
    let root = el """{}"""
    match requireFloat "temp" root with
    | Error (MissingField "temp") -> ()
    | other -> Assert.Fail($"Expected MissingField, got {other}")

[<Fact>]
let ``requireFloat: boolean property returns Error SchemaError`` () =
    let root = el """{"temp":true}"""
    match requireFloat "temp" root with
    | Error (SchemaError ("temp", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// requireBool
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireBool: true property returns Ok true`` () =
    let root = el """{"flag":true}"""
    match requireBool "flag" root with
    | Ok true -> ()
    | other   -> Assert.Fail($"Expected Ok true, got {other}")

[<Fact>]
let ``requireBool: false property returns Ok false`` () =
    let root = el """{"flag":false}"""
    match requireBool "flag" root with
    | Ok false -> ()
    | other    -> Assert.Fail($"Expected Ok false, got {other}")

[<Fact>]
let ``requireBool: missing property returns Error MissingField`` () =
    let root = el """{}"""
    match requireBool "flag" root with
    | Error (MissingField "flag") -> ()
    | other -> Assert.Fail($"Expected MissingField, got {other}")

[<Fact>]
let ``requireBool: string property returns Error SchemaError`` () =
    let root = el """{"flag":"yes"}"""
    match requireBool "flag" root with
    | Error (SchemaError ("flag", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryGetBool
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryGetBool: true property returns Some true`` () =
    let root = el """{"active":true}"""
    Assert.Equal(Some true, tryGetBool "active" root)

[<Fact>]
let ``tryGetBool: false property returns Some false`` () =
    let root = el """{"active":false}"""
    Assert.Equal(Some false, tryGetBool "active" root)

[<Fact>]
let ``tryGetBool: missing property returns None`` () =
    let root = el """{}"""
    Assert.Equal(None, tryGetBool "active" root)

[<Fact>]
let ``tryGetBool: string property returns None`` () =
    let root = el """{"active":"yes"}"""
    Assert.Equal(None, tryGetBool "active" root)

// ═══════════════════════════════════════════════════════════════════════════
// requireArray
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireArray: array property returns Ok list of elements`` () =
    let root = el """{"items":[1,2,3]}"""
    match requireArray "items" root with
    | Ok lst ->
        Assert.Equal(3, lst.Length)
        Assert.Equal(1, lst.[0].GetInt32())
        Assert.Equal(2, lst.[1].GetInt32())
        Assert.Equal(3, lst.[2].GetInt32())
    | other -> Assert.Fail($"Expected Ok list, got {other}")

[<Fact>]
let ``requireArray: empty array property returns Ok empty list`` () =
    let root = el """{"items":[]}"""
    match requireArray "items" root with
    | Ok [] -> ()
    | other -> Assert.Fail($"Expected Ok [], got {other}")

[<Fact>]
let ``requireArray: missing property returns Error MissingField`` () =
    let root = el """{}"""
    match requireArray "items" root with
    | Error (MissingField "items") -> ()
    | other -> Assert.Fail($"Expected MissingField, got {other}")

[<Fact>]
let ``requireArray: string property returns Error SchemaError`` () =
    let root = el """{"items":"not-an-array"}"""
    match requireArray "items" root with
    | Error (SchemaError ("items", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryGetArray
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryGetArray: array property returns Some list`` () =
    let root = el """{"tags":["a","b"]}"""
    match tryGetArray "tags" root with
    | Some lst ->
        Assert.Equal(2, lst.Length)
        Assert.Equal("a", lst.[0].GetString())
    | None -> Assert.Fail("Expected Some, got None")

[<Fact>]
let ``tryGetArray: missing property returns None`` () =
    let root = el """{}"""
    Assert.Equal(None, tryGetArray "tags" root)

[<Fact>]
let ``tryGetArray: string property returns None`` () =
    let root = el """{"tags":"a,b"}"""
    Assert.Equal(None, tryGetArray "tags" root)

// ═══════════════════════════════════════════════════════════════════════════
// requireObject
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requireObject: object property returns Ok element`` () =
    let root = el """{"meta":{"k":"v"}}"""
    match requireObject "meta" root with
    | Ok obj -> Assert.Equal(JsonValueKind.Object, obj.ValueKind)
    | other  -> Assert.Fail($"Expected Ok object, got {other}")

[<Fact>]
let ``requireObject: missing property returns Error MissingField`` () =
    let root = el """{}"""
    match requireObject "meta" root with
    | Error (MissingField "meta") -> ()
    | other -> Assert.Fail($"Expected MissingField, got {other}")

[<Fact>]
let ``requireObject: array property returns Error SchemaError`` () =
    let root = el """{"meta":[1,2]}"""
    match requireObject "meta" root with
    | Error (SchemaError ("meta", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryGetObject
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tryGetObject: object property returns Some element`` () =
    let root = el """{"cfg":{"debug":true}}"""
    match tryGetObject "cfg" root with
    | Some obj -> Assert.Equal(JsonValueKind.Object, obj.ValueKind)
    | None     -> Assert.Fail("Expected Some, got None")

[<Fact>]
let ``tryGetObject: missing property returns None`` () =
    let root = el """{}"""
    Assert.Equal(None, tryGetObject "cfg" root)

[<Fact>]
let ``tryGetObject: string property returns None`` () =
    let root = el """{"cfg":"not-an-object"}"""
    Assert.Equal(None, tryGetObject "cfg" root)

// ═══════════════════════════════════════════════════════════════════════════
// getPath
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``getPath: two-level path returns Some element`` () =
    let root = el """{"a":{"b":42}}"""
    match getPath ["a"; "b"] root with
    | Some v -> Assert.Equal(42, v.GetInt32())
    | None   -> Assert.Fail("Expected Some, got None")

[<Fact>]
let ``getPath: missing intermediate key returns None`` () =
    let root = el """{"a":{"c":1}}"""
    Assert.Equal(None, getPath ["a"; "b"] root)

[<Fact>]
let ``getPath: missing top-level key returns None`` () =
    let root = el """{"x":1}"""
    Assert.Equal(None, getPath ["a"; "b"] root)

[<Fact>]
let ``getPath: empty path returns Some root`` () =
    let root = el """{"x":1}"""
    match getPath [] root with
    | Some v -> Assert.Equal(JsonValueKind.Object, v.ValueKind)
    | None   -> Assert.Fail("Expected Some root for empty path, got None")

[<Fact>]
let ``getPath: single-key path returns Some element`` () =
    let root = el """{"z":99}"""
    match getPath ["z"] root with
    | Some v -> Assert.Equal(99, v.GetInt32())
    | None   -> Assert.Fail("Expected Some, got None")

// ═══════════════════════════════════════════════════════════════════════════
// requirePath
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``requirePath: existing nested path returns Ok element`` () =
    let root = el """{"a":{"b":{"c":"deep"}}}"""
    match requirePath ["a"; "b"; "c"] root with
    | Ok v  -> Assert.Equal("deep", v.GetString())
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``requirePath: missing path returns Error MissingField with dot-joined key`` () =
    let root = el """{"a":{}}"""
    match requirePath ["a"; "b"] root with
    | Error (MissingField "a.b") -> ()
    | other -> Assert.Fail($"Expected MissingField \"a.b\", got {other}")

[<Fact>]
let ``requirePath: missing top-level key returns Error MissingField`` () =
    let root = el """{}"""
    match requirePath ["x"; "y"] root with
    | Error (MissingField "x.y") -> ()
    | other -> Assert.Fail($"Expected MissingField \"x.y\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// toMap
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``toMap: object with two keys returns Map with both entries`` () =
    let root = el """{"x":1,"y":2}"""
    let m = toMap root
    Assert.Equal(2, m.Count)
    Assert.True(m.ContainsKey("x"))
    Assert.True(m.ContainsKey("y"))
    Assert.Equal(1, m["x"].GetInt32())
    Assert.Equal(2, m["y"].GetInt32())

[<Fact>]
let ``toMap: empty object returns empty Map`` () =
    let root = el """{}"""
    let m = toMap root
    Assert.Empty(m)

[<Fact>]
let ``toMap: non-object element returns empty Map`` () =
    let root = el """[1,2,3]"""
    let m = toMap root
    Assert.Empty(m)

[<Fact>]
let ``toMap: object preserves string values`` () =
    let root = el """{"key":"value"}"""
    let m = toMap root
    Assert.Equal("value", m["key"].GetString())

// ═══════════════════════════════════════════════════════════════════════════
// traverseResult
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``traverseResult: all Ok returns Ok list in order`` () =
    let items = [1; 2; 3]
    let f x = Ok (x * 10)
    match traverseResult f items with
    | Ok lst -> Assert.Equal<int list>([10; 20; 30], lst)
    | other  -> Assert.Fail($"Expected Ok [10;20;30], got {other}")

[<Fact>]
let ``traverseResult: first Error short-circuits and returns that Error`` () =
    let items = [1; 2; 3]
    let f x =
        if x = 2 then Error (MissingField "two")
        else Ok x
    match traverseResult f items with
    | Error (MissingField "two") -> ()
    | other -> Assert.Fail($"Expected MissingField \"two\", got {other}")

[<Fact>]
let ``traverseResult: empty sequence returns Ok empty list`` () =
    let items : int list = []
    let f x = Ok x
    match traverseResult f items with
    | Ok [] -> ()
    | other -> Assert.Fail($"Expected Ok [], got {other}")

[<Fact>]
let ``traverseResult: only first error is reported even if multiple items fail`` () =
    let items = [1; 2; 3]
    let f x =
        if x >= 2 then Error (MissingField (string x))
        else Ok x
    match traverseResult f items with
    | Error (MissingField "2") -> ()
    | other -> Assert.Fail($"Expected MissingField \"2\" (first failure), got {other}")
