module BotSharp.Tests.Infrastructure.McpToolTests

open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.McpTool

// ═══════════════════════════════════════════════════════════════════════════
// extractContent
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``extractContent returns raw JSON when no content field`` () =
    use doc = JsonDocument.Parse("""{"answer":42}""")
    let text = extractContent (doc.RootElement.Clone())
    Assert.Equal("""{"answer":42}""", text)

[<Fact>]
let ``extractContent extracts single text block`` () =
    use doc = JsonDocument.Parse("""{"content":[{"type":"text","text":"hello"}]}""")
    let text = extractContent (doc.RootElement.Clone())
    Assert.Equal("hello", text)

[<Fact>]
let ``extractContent concatenates multiple text blocks with newlines`` () =
    use doc = JsonDocument.Parse("""{"content":[{"type":"text","text":"line1"},{"type":"text","text":"line2"}]}""")
    let text = extractContent (doc.RootElement.Clone())
    Assert.Equal("line1\nline2", text)

[<Fact>]
let ``extractContent returns (no output) for empty content array`` () =
    use doc = JsonDocument.Parse("""{"content":[]}""")
    let text = extractContent (doc.RootElement.Clone())
    Assert.Equal("(no output)", text)

[<Fact>]
let ``extractContent returns raw block JSON for non-text block types`` () =
    use doc = JsonDocument.Parse("""{"content":[{"type":"image","data":"abc"}]}""")
    let text = extractContent (doc.RootElement.Clone())
    // non-text block is included as raw JSON
    Assert.Contains("image", text)

// ═══════════════════════════════════════════════════════════════════════════
// parseInputSchema
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseInputSchema returns empty map when no inputSchema`` () =
    use doc = JsonDocument.Parse("""{"name":"tool"}""")
    let props = parseInputSchema (doc.RootElement.Clone())
    Assert.Empty(props)

[<Fact>]
let ``parseInputSchema maps string property correctly`` () =
    let json = """{"inputSchema":{"type":"object","properties":{"path":{"type":"string","description":"File path"}},"required":["path"]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    Assert.Equal(1, props.Count)
    let p = props.["path"]
    Assert.Equal(JsString, p.Type)
    Assert.Equal("File path", p.Description)
    Assert.True(p.Required)

[<Fact>]
let ``parseInputSchema maps number and boolean properties correctly`` () =
    let json = """{"inputSchema":{"type":"object","properties":{"count":{"type":"integer"},"flag":{"type":"boolean"}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    Assert.Equal(JsNumber,  props.["count"].Type)
    Assert.Equal(JsBoolean, props.["flag"].Type)
    Assert.False(props.["count"].Required)

[<Fact>]
let ``parseInputSchema maps array property correctly`` () =
    let json = """{"inputSchema":{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"}}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    Assert.Equal(JsArray JsString, props.["items"].Type)

[<Fact>]
let ``parseInputSchema maps enum property correctly`` () =
    let json = """{"inputSchema":{"type":"object","properties":{"mode":{"enum":["read","write","append"]}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    match props.["mode"].Type with
    | JsEnum values -> Assert.Equal<string list>(["read";"write";"append"], values)
    | other -> Assert.Fail($"Expected JsEnum, got %A{other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseMcpToolEntry
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseMcpToolEntry returns None when name is missing`` () =
    use doc = JsonDocument.Parse("""{"description":"no name here"}""")
    let result = parseMcpToolEntry "srv" (doc.RootElement.Clone())
    Assert.True(result.IsNone)

[<Fact>]
let ``parseMcpToolEntry returns prefixed tool name`` () =
    let json = """{"name":"read_file","description":"Read a file","inputSchema":{"type":"object","properties":{}}}"""
    use doc = JsonDocument.Parse(json)
    match parseMcpToolEntry "myserver" (doc.RootElement.Clone()) with
    | None -> Assert.Fail("Expected Some")
    | Some (origName, spec) ->
        Assert.Equal("read_file", origName)
        let (ToolName n) = spec.Name
        Assert.Equal("mcp_myserver_read_file", n)

[<Fact>]
let ``parseMcpToolEntry uses origName as description when description is absent`` () =
    let json = """{"name":"do_thing","inputSchema":{"type":"object","properties":{}}}"""
    use doc = JsonDocument.Parse(json)
    match parseMcpToolEntry "srv" (doc.RootElement.Clone()) with
    | None -> Assert.Fail("Expected Some")
    | Some (_, spec) -> Assert.Equal("do_thing", spec.Description)

[<Fact>]
let ``parseMcpToolEntry populates parameters from inputSchema`` () =
    let json = """{"name":"search","description":"Search","inputSchema":{"type":"object","properties":{"query":{"type":"string","description":"Query string"}},"required":["query"]}}"""
    use doc = JsonDocument.Parse(json)
    match parseMcpToolEntry "web" (doc.RootElement.Clone()) with
    | None -> Assert.Fail("Expected Some")
    | Some (_, spec) ->
        Assert.Equal(1, spec.Parameters.Count)
        let p = spec.Parameters.["query"]
        Assert.Equal(JsString, p.Type)
        Assert.True(p.Required)

// ═══════════════════════════════════════════════════════════════════════════
// readNextResponse — unit tested with StringReader
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readNextResponse returns first line with id field`` () =
    let lines = """{"jsonrpc":"2.0","id":"1","result":{}}"""
    use reader = new System.IO.StringReader(lines)
    let result = readNextResponse reader |> Async.RunSynchronously
    match result with
    | Ok line -> Assert.Contains("\"id\"", line)
    | Error e -> Assert.Fail(e)

[<Fact>]
let ``readNextResponse skips notifications (no id) and returns next response`` () =
    // First line is a notification (no id), second is a response
    let lines = "{\"jsonrpc\":\"2.0\",\"method\":\"progress\"}\n{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"result\":{}}"
    use reader = new System.IO.StringReader(lines)
    let result = readNextResponse reader |> Async.RunSynchronously
    match result with
    | Ok line -> Assert.Contains("\"id\"", line)
    | Error e -> Assert.Fail(e)

[<Fact>]
let ``readNextResponse returns error on EOF`` () =
    use reader = new System.IO.StringReader("")
    let result = readNextResponse reader |> Async.RunSynchronously
    match result with
    | Error msg -> Assert.Contains("closed", msg)
    | Ok _ -> Assert.Fail("Expected error on EOF")

// ═══════════════════════════════════════════════════════════════════════════
// readSseResponse
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readSseResponse returns data line with id field`` () =
    let sse = "data: {\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}\n"
    use reader = new StringReader(sse)
    let result = readSseResponse reader |> Async.RunSynchronously
    match result with
    | Ok json -> Assert.Contains("\"id\"", json)
    | Error e -> Assert.Fail($"Expected Ok, got Error: {e}")

[<Fact>]
let ``readSseResponse skips notifications and returns response`` () =
    // First line is a notification (no id), second is the response
    let sse = "data: {\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"2\",\"result\":{}}\n"
    use reader = new StringReader(sse)
    let result = readSseResponse reader |> Async.RunSynchronously
    match result with
    | Ok json -> Assert.Contains("\"id\"", json)
    | Error e -> Assert.Fail($"Expected Ok skipping notification, got Error: {e}")

[<Fact>]
let ``readSseResponse returns error on empty stream`` () =
    use reader = new StringReader("")
    let result = readSseResponse reader |> Async.RunSynchronously
    match result with
    | Error msg -> Assert.False(System.String.IsNullOrEmpty(msg))
    | Ok _ -> Assert.Fail("Expected error on empty stream")

[<Fact>]
let ``readSseResponse skips comment and blank lines`` () =
    // Comments (: ...) and blank lines should be skipped
    let sse = ": comment\n\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"3\",\"result\":{}}\n"
    use reader = new StringReader(sse)
    let result = readSseResponse reader |> Async.RunSynchronously
    match result with
    | Ok json -> Assert.Contains("\"id\"", json)
    | Error e -> Assert.Fail($"Expected Ok after skipping comments, got Error: {e}")

// ═══════════════════════════════════════════════════════════════════════════
// extractContent — content field present but not an array
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``extractContent returns (no output) when content is a string not an array`` () =
    // content: "hello" — ValueKind is String, not Array → last match arm → "(no output)"
    use doc = JsonDocument.Parse("""{"content":"just a string"}""")
    let text = extractContent (doc.RootElement.Clone())
    Assert.Equal("(no output)", text)

// ═══════════════════════════════════════════════════════════════════════════
// parseMcpToolEntry — null name field
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseMcpToolEntry returns None when name is JSON null`` () =
    use doc = JsonDocument.Parse("""{"name":null,"description":"bad entry"}""")
    let result = parseMcpToolEntry "srv" (doc.RootElement.Clone())
    Assert.True(result.IsNone, "Expected None when name is null")

// ═══════════════════════════════════════════════════════════════════════════
// parseInputSchema — array property with no items field → JsArray JsAny
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseInputSchema maps array property with no items to JsArray JsAny`` () =
    let json = """{"inputSchema":{"type":"object","properties":{"tags":{"type":"array"}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    match props.["tags"].Type with
    | JsArray JsAny -> ()   // correct fallback
    | other -> Assert.Fail($"Expected JsArray JsAny, got %A{other}")

[<Fact>]
let ``parseInputSchema returns empty map when inputSchema has no properties field`` () =
    // | false, _ -> Map.empty branch at line 214 of McpTool.fs
    let json = """{"inputSchema":{"type":"object"}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    Assert.Empty(props)

[<Fact>]
let ``parseInputSchema maps unknown type string to JsAny`` () =
    // "object" and custom types → the | _ -> JsAny arm in typeOf
    let json = """{"inputSchema":{"type":"object","properties":{"obj":{"type":"object","description":"nested"}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    match props.["obj"].Type with
    | JsAny -> ()
    | other -> Assert.Fail($"Expected JsAny for 'object' type, got %A{other}")

[<Fact>]
let ``extractContent returns no output when text block has no text field`` () =
    // block type = "text" but no "text" property → None → filtered → "(no output)"
    use doc = JsonDocument.Parse("""{"content":[{"type":"text"}]}""")
    let result = extractContent (doc.RootElement.Clone())
    Assert.Equal("(no output)", result)

[<Fact>]
let ``readSseResponse returns error when stream ends with DoneLine`` () =
    // `data: [DONE]` → DoneLine → `return Error "MCP SSE stream ended without response"`
    let sse = "data: [DONE]\n"
    use reader = new System.IO.StringReader(sse)
    let result = readSseResponse reader |> Async.RunSynchronously
    match result with
    | Error msg -> Assert.Contains("ended", msg)
    | Ok _ -> Assert.Fail("Expected Error when stream ends with DoneLine")

[<Fact>]
let ``readNextResponse skips malformed JSON lines and returns error on EOF`` () =
    // Lines that are not valid JSON are caught by `with _ -> return! loop()`
    // After all lines are consumed, EOF returns Error "closed"
    let lines = "not json\nalso bad\n"
    use reader = new System.IO.StringReader(lines)
    let result = readNextResponse reader |> Async.RunSynchronously
    match result with
    | Error msg -> Assert.Contains("closed", msg)
    | Ok _ -> Assert.Fail("Expected Error after only malformed lines")

// ═══════════════════════════════════════════════════════════════════════════
// parseInputSchema — required field is not an array (Set.empty fallback)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseInputSchema treats all fields as optional when required is not an array`` () =
    // required is a string, not an array → | _ -> Set.empty → all Required = false
    let json = """{"inputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":"oops"}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    Assert.False(props.["path"].Required, "Required should be false when 'required' field is not an array")

// ═══════════════════════════════════════════════════════════════════════════
// extractContent — block with no type property
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``extractContent returns (no output) when content block has no type field`` () =
    // block has no "type" property → | _ -> None in Seq.choose → filtered out → "(no output)"
    use doc = JsonDocument.Parse("""{"content":[{"data":"some data"}]}""")
    let text = extractContent (doc.RootElement.Clone())
    Assert.Equal("(no output)", text)

// ═══════════════════════════════════════════════════════════════════════════
// readSseResponse — DataLine with valid JSON but no id field (notification) then EOF
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``readSseResponse returns error when all data lines are notifications (no id) and stream ends`` () =
    // DataLine with valid JSON but no "id" → notification, skip → EOF → Error "closed"
    let sse = "data: {\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\n"
    use reader = new System.IO.StringReader(sse)
    let result = readSseResponse reader |> Async.RunSynchronously
    match result with
    | Error msg -> Assert.False(System.String.IsNullOrEmpty(msg))
    | Ok _ -> Assert.Fail("Expected Error when all data lines are notifications")

// ═══════════════════════════════════════════════════════════════════════════
// parseInputSchema — enum property value is not an array
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseInputSchema maps property with no type and non-array enum to JsAny`` () =
    // no "type" field, "enum" present but not an array → | _ -> JsAny
    let json = """{"inputSchema":{"type":"object","properties":{"mode":{"enum":"not-array"}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    match props.["mode"].Type with
    | JsAny -> ()
    | other -> Assert.Fail($"Expected JsAny for non-array enum, got %A{other}")

[<Fact>]
let ``parseInputSchema maps nullable union type array to JsAny`` () =
    // Python parity: test_wrapper_normalizes_nullable_property_type_union
    // Python's MCPToolWrapper collapses ["string", "null"] → {type:"string", nullable:true}.
    // F# typeOf: "type" property is an Array (not String) → falls to | _ -> JsAny.
    // JsAny means "any value accepted", which is correct for an optional string.
    let json = """{"inputSchema":{"type":"object","properties":{"name":{"type":["string","null"],"description":"optional"}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    match props.["name"].Type with
    | JsAny -> ()   // array-type union → JsAny (nullable-string treated as any)
    | other -> Assert.Fail($"Expected JsAny for nullable union type, got %A{other}")

[<Fact>]
let ``parseInputSchema maps anyOf schema to JsAny`` () =
    // Python parity: test_wrapper_normalizes_nullable_property_anyof
    // Python normalises anyOf:[{type:string},{type:null}] → {type:string,nullable:true}.
    // F# typeOf: no "type" field, no "enum" array → | _ -> JsAny fallback.
    let json = """{"inputSchema":{"type":"object","properties":{"name":{"anyOf":[{"type":"string"},{"type":"null"}],"description":"opt"}},"required":[]}}"""
    use doc = JsonDocument.Parse(json)
    let props = parseInputSchema (doc.RootElement.Clone())
    match props.["name"].Type with
    | JsAny -> ()   // anyOf without enum top-level → JsAny
    | other -> Assert.Fail($"Expected JsAny for anyOf schema, got %A{other}")
