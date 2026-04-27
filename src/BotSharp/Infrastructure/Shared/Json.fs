module BotSharp.Infrastructure.Shared.Json

open System.Text.Json
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Result computation expression  (must be defined before use in this file)
// ═══════════════════════════════════════════════════════════════════════════

type ResultBuilder() =
    member _.Return x     = Ok x
    member _.ReturnFrom m = m
    member _.Zero ()      = Ok ()
    member _.Bind(m, f)   =
        match m with
        | Ok x    -> f x
        | Error e -> Error e
    member _.Combine(a, b) =
        match a with
        | Ok ()   -> b
        | Error e -> Error e
    member _.Delay f = f ()

let result = ResultBuilder()

// ═══════════════════════════════════════════════════════════════════════════
// JsonElement helper functions — composable Result-returning decoders
// ═══════════════════════════════════════════════════════════════════════════

/// Try to get a property by name; None if missing
let tryGetProp (name: string) (el: JsonElement) : JsonElement option =
    match el.TryGetProperty(name) with
    | true, v -> Some v
    | _       -> None

/// Require a property; ParseError if missing
let requireProp (name: string) (el: JsonElement) : Result<JsonElement, ParseError> =
    match el.TryGetProperty(name) with
    | true, v -> Ok v
    | _       -> Error (MissingField name)

/// Require a string property
let requireString (name: string) (el: JsonElement) : Result<string, ParseError> =
    result {
        let! prop = requireProp name el
        if prop.ValueKind = JsonValueKind.String then
            match prop.GetString() with
            | null -> return! Error (SchemaError (name, "string value was null"))
            | s    -> return s
        else
            return! Error (SchemaError (name, $"expected string, got {prop.ValueKind}"))
    }

/// Try to get a string property; None if missing or null
let tryGetString (name: string) (el: JsonElement) : string option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String ->
        match v.GetString() with
        | null -> None
        | s    -> Some s
    | _ -> None

/// Require an integer property
let requireInt (name: string) (el: JsonElement) : Result<int, ParseError> =
    result {
        let! prop = requireProp name el
        if prop.ValueKind = JsonValueKind.Number then
            return prop.GetInt32()
        else
            return! Error (SchemaError (name, $"expected number, got {prop.ValueKind}"))
    }

/// Try to get an integer property
let tryGetInt (name: string) (el: JsonElement) : int option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetInt32())
    | _ -> None

/// Require a float property
let requireFloat (name: string) (el: JsonElement) : Result<float, ParseError> =
    result {
        let! prop = requireProp name el
        if prop.ValueKind = JsonValueKind.Number then
            return prop.GetDouble()
        else
            return! Error (SchemaError (name, $"expected number, got {prop.ValueKind}"))
    }

/// Require a boolean property
let requireBool (name: string) (el: JsonElement) : Result<bool, ParseError> =
    result {
        let! prop = requireProp name el
        match prop.ValueKind with
        | JsonValueKind.True  -> return true
        | JsonValueKind.False -> return false
        | kind -> return! Error (SchemaError (name, $"expected bool, got {kind}"))
    }

/// Try to get a boolean property
let tryGetBool (name: string) (el: JsonElement) : bool option =
    match el.TryGetProperty(name) with
    | true, v ->
        match v.ValueKind with
        | JsonValueKind.True  -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None
    | _ -> None

/// Require an array property, returning a list of JsonElement
let requireArray (name: string) (el: JsonElement) : Result<JsonElement list, ParseError> =
    result {
        let! prop = requireProp name el
        if prop.ValueKind = JsonValueKind.Array then
            return prop.EnumerateArray() |> Seq.cast<JsonElement> |> Seq.toList
        else
            return! Error (SchemaError (name, $"expected array, got {prop.ValueKind}"))
    }

/// Try to get an array property
let tryGetArray (name: string) (el: JsonElement) : JsonElement list option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Array ->
        Some (v.EnumerateArray() |> Seq.cast<JsonElement> |> Seq.toList)
    | _ -> None

/// Require an object property
let requireObject (name: string) (el: JsonElement) : Result<JsonElement, ParseError> =
    result {
        let! prop = requireProp name el
        if prop.ValueKind = JsonValueKind.Object then
            return prop
        else
            return! Error (SchemaError (name, $"expected object, got {prop.ValueKind}"))
    }

/// Try to get an object property
let tryGetObject (name: string) (el: JsonElement) : JsonElement option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Object -> Some v
    | _ -> None

/// Navigate a nested path, e.g. ["foo"; "bar"]
let rec getPath (path: string list) (el: JsonElement) : JsonElement option =
    match path with
    | []         -> Some el
    | key :: rest ->
        match el.TryGetProperty(key) with
        | true, child -> getPath rest child
        | _           -> None

/// Navigate a nested path, requiring each level to exist
let requirePath (path: string list) (el: JsonElement) : Result<JsonElement, ParseError> =
    match getPath path el with
    | Some v -> Ok v
    | None   -> Error (MissingField (String.concat "." path))

/// Parse an object's properties as Map<string, JsonElement>
let toMap (el: JsonElement) : Map<string, JsonElement> =
    if el.ValueKind <> JsonValueKind.Object then Map.empty
    else
        el.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value)
        |> Map.ofSeq

/// Traverse a sequence with a Result-returning function; fail-fast on first Error
let traverseResult (f: 'a -> Result<'b, 'e>) (xs: 'a seq) : Result<'b list, 'e> =
    let mutable error : 'e option = None
    let mutable acc   : 'b list   = []
    for x in xs do
        if error.IsNone then
            match f x with
            | Ok v    -> acc <- acc @ [v]
            | Error e -> error <- Some e
    match error with
    | Some e -> Error e
    | None   -> Ok acc
