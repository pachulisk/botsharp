module BotSharp.Tests.Parsers.SessionParserTests

open System.Text.Json
open Xunit
open FsCheck.Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.SessionParser

// ═══════════════════════════════════════════════════════════════════════════
// Roundtrip helpers
// ═══════════════════════════════════════════════════════════════════════════

let private roundtrip (msg: Message) : Result<Message, ParseError> =
    let json = serializeMessage msg
    use doc  = JsonDocument.Parse(json)
    parseMessageLine doc.RootElement

// ═══════════════════════════════════════════════════════════════════════════
// Serialization roundtrip — each Message variant
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``SystemMessage roundtrips correctly`` () =
    let msg = SystemMessage "You are a helpful assistant."
    Assert.Equal(Ok msg, roundtrip msg)

[<Fact>]
let ``SystemMessage serializes to role:system`` () =
    let msg  = SystemMessage "prompt text"
    let json = serializeMessage msg
    use doc  = JsonDocument.Parse(json)
    Assert.Equal("system", doc.RootElement.GetProperty("role").GetString())
    Assert.Equal("prompt text", doc.RootElement.GetProperty("content").GetString())

[<Fact>]
let ``UserMessage roundtrips correctly`` () =
    let msg = UserMessage ("hello world", [])
    Assert.Equal(Ok msg, roundtrip msg)

[<Fact>]
let ``UserMessage with media roundtrips correctly`` () =
    let msg = UserMessage ("caption", [ImageFile (LocalFilePath.ofAbsolute "/tmp/a.png"); AudioFile (LocalFilePath.ofAbsolute "/tmp/b.mp3")])
    Assert.Equal(Ok msg, roundtrip msg)

[<Fact>]
let ``AssistantMessage roundtrips correctly`` () =
    let msg = AssistantMessage ("I can help with that.", None)
    Assert.Equal(Ok msg, roundtrip msg)

[<Fact>]
let ``AssistantMessage with reasoning_content roundtrips correctly`` () =
    let msg = AssistantMessage ("Final answer.", Some "I thought about it carefully.")
    Assert.Equal(Ok msg, roundtrip msg)

[<Fact>]
let ``ToolCallMessage roundtrips correctly`` () =
    let args =
        let doc = JsonDocument.Parse("""{"path":"./src","depth":2}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = { Id = ToolCallId "call_1"; Tool = ToolName "list_dir"; Arguments = args; ProviderMeta = None }
    let msg  = ToolCallMessage (NonEmptyList.singleton call, None)
    match roundtrip msg with
    | Ok (ToolCallMessage (nel, _)) when NonEmptyList.length nel = 1 ->
        let result = nel.Head
        Assert.Equal(call.Id, result.Id)
        Assert.Equal(call.Tool, result.Tool)
        Assert.True(result.Arguments.ContainsKey("path"))
        Assert.True(result.Arguments.ContainsKey("depth"))
    | other -> Assert.Fail($"Expected matching ToolCallMessage, got {other}")

[<Fact>]
let ``ToolCallMessage with reasoning_content roundtrips correctly`` () =
    let call = { Id = ToolCallId "call_2"; Tool = ToolName "search"; Arguments = Map.empty; ProviderMeta = None }
    let msg  = ToolCallMessage (NonEmptyList.singleton call, Some "I decided to call the search tool.")
    match roundtrip msg with
    | Ok (ToolCallMessage (nel, Some rc)) when NonEmptyList.length nel = 1 ->
        Assert.Equal("call_2", let (ToolCallId id) = nel.Head.Id in id)
        Assert.Equal("I decided to call the search tool.", rc)
    | other -> Assert.Fail($"Expected ToolCallMessage with reasoning_content, got {other}")

[<Fact>]
let ``ToolResultMessage roundtrips correctly`` () =
    let msg = ToolResultMessage (ToolCallId "call_1", ToolName "list_dir", "file1.txt\nfile2.txt")
    Assert.Equal(Ok msg, roundtrip msg)

// ═══════════════════════════════════════════════════════════════════════════
// parseMessageLine error cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``unknown role returns SchemaError`` () =
    let json = """{"role":"unknown_role","content":"x"}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (SchemaError ("role", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError on role, got {other}")

[<Fact>]
let ``missing content on user message returns MissingField`` () =
    let json = """{"role":"user"}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (MissingField "content") -> ()
    | other -> Assert.Fail($"Expected MissingField \"content\", got {other}")

[<Fact>]
let ``missing content on assistant message returns MissingField`` () =
    let json = """{"role":"assistant"}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (MissingField "content") -> ()
    | other -> Assert.Fail($"Expected MissingField \"content\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseSessionFile
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseSessionFile with valid lines produces correct snapshot`` () =
    let sid  = SessionId "cli:test"
    let line1 = serializeMessage (UserMessage ("hi", []))
    let line2 = serializeMessage (AssistantMessage ("hello back", None))
    match parseSessionFile sid [line1; line2] with
    | Ok snap ->
        Assert.Equal(2, SessionSnapshot.messageCount snap)
        Assert.Equal(sid, SessionSnapshot.id snap)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``parseSessionFile skips blank lines`` () =
    let sid  = SessionId "cli:test"
    let line = serializeMessage (AssistantMessage ("hello", None))
    match parseSessionFile sid [""; "   "; line; ""] with
    | Ok snap -> Assert.Equal(1, SessionSnapshot.messageCount snap)
    | Error errs -> Assert.Fail($"Expected Ok, got errors: {errs}")

[<Fact>]
let ``parseSessionFile returns errors for invalid JSON lines`` () =
    let sid  = SessionId "cli:test"
    match parseSessionFile sid ["not json at all"] with
    | Error _ -> ()
    | Ok snap -> Assert.Fail($"Expected errors, got Ok with {SessionSnapshot.messageCount snap} messages")

[<Fact>]
let ``parseSessionFile returns errors for valid JSON but wrong schema`` () =
    let sid = SessionId "cli:test"
    match parseSessionFile sid ["""{"role":"unknown"}"""] with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Expected errors for unknown role")

[<Fact>]
let ``empty sequence produces empty snapshot`` () =
    let sid = SessionId "cli:test"
    match parseSessionFile sid [] with
    | Ok snap -> Assert.Equal(0, SessionSnapshot.messageCount snap)
    | Error errs -> Assert.Fail($"Expected Ok, got {errs}")

// ═══════════════════════════════════════════════════════════════════════════
// Media variants: DocumentFile and VideoFile roundtrip
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``UserMessage with DocumentFile media roundtrips correctly`` () =
    let msg = UserMessage ("doc", [ DocumentFile (LocalFilePath.ofAbsolute "/tmp/report.pdf") ])
    Assert.Equal(Ok msg, roundtrip msg)

[<Fact>]
let ``UserMessage with VideoFile media roundtrips correctly`` () =
    let msg = UserMessage ("vid", [ VideoFile (LocalFilePath.ofAbsolute "/tmp/demo.mp4") ])
    Assert.Equal(Ok msg, roundtrip msg)

// ═══════════════════════════════════════════════════════════════════════════
// ToolCallMessage — empty calls list → SchemaError
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool_calls with empty calls array returns SchemaError`` () =
    let json = """{"role":"tool_calls","calls":[]}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (SchemaError ("calls", _)) -> ()
    | other -> Assert.Fail($"Expected SchemaError for empty calls, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tool_result — missing required fields
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool_result missing id returns MissingField`` () =
    let json = """{"role":"tool_result","name":"tool","content":"ok"}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (MissingField "id") -> ()
    | other -> Assert.Fail($"Expected MissingField \"id\", got {other}")

[<Fact>]
let ``tool_result missing name returns MissingField`` () =
    let json = """{"role":"tool_result","id":"call_1","content":"ok"}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (MissingField "name") -> ()
    | other -> Assert.Fail($"Expected MissingField \"name\", got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// SystemMessage roundtrip (role added)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``SystemMessage with special chars roundtrips correctly`` () =
    let msg = SystemMessage "You are a helpful assistant.\n\nBe concise."
    Assert.Equal(Ok msg, roundtrip msg)

// ═══════════════════════════════════════════════════════════════════════════
// serializeMessage produces valid JSON
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``serializeMessage produces parseable JSON`` () =
    let messages = [
        UserMessage ("test", [])
        AssistantMessage ("response", None)
        ToolResultMessage (ToolCallId "x", ToolName "y", "result")
    ]
    for msg in messages do
        let json = serializeMessage msg
        // Must parse without exception
        use doc = JsonDocument.Parse(json)
        Assert.NotNull(doc)

// ═══════════════════════════════════════════════════════════════════════════
// parseToolCallRecord — missing id / tool fields
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool_calls with call missing id returns error`` () =
    // parseToolCallRecord returns MissingField "id" which traverseResult propagates
    let json = """{"role":"tool_calls","calls":[{"tool":"read_file","arguments":{}}]}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error _ -> ()
    | Ok other -> Assert.Fail($"Expected Error for call missing id, got {other}")

[<Fact>]
let ``tool_calls with call missing arguments is parsed with empty map`` () =
    // tryGetObject "arguments" returns None → Map.empty (the | None -> Map.empty branch)
    let json = """{"role":"tool_calls","calls":[{"id":"c1","tool":"ping"}]}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Ok (ToolCallMessage (nel, _)) ->
        let call = nel.Head
        Assert.Equal(ToolCallId "c1", call.Id)
        Assert.Empty(call.Arguments)
    | other -> Assert.Fail($"Expected ToolCallMessage with empty args, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseMediaItem — unknown type silently filtered out
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``UserMessage with unknown media type is deserialized with that entry filtered out`` () =
    // parseMediaItem | _ -> None: unknown type is dropped silently
    let json = """{"role":"user","content":"hi","media":[{"type":"unknown","path":"/tmp/x"}]}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Ok (UserMessage (text, media)) ->
        Assert.Equal("hi", text)
        Assert.Empty(media)   // unknown type → None → filtered
    | other -> Assert.Fail($"Expected UserMessage with empty media, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseSessionFile — mixed valid and invalid lines returns Error
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseSessionFile with one valid and one invalid line returns Error`` () =
    let sid    = SessionId "cli:mixed"
    let valid  = serializeMessage (AssistantMessage ("ok", None))
    let invalid = "not json {"
    match parseSessionFile sid [valid; invalid] with
    | Error errs -> Assert.NotEmpty(errs)
    | Ok snap    -> Assert.Fail($"Expected Error, got Ok with {SessionSnapshot.messageCount snap} messages")

// ═══════════════════════════════════════════════════════════════════════════
// tool_result — missing content field
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool_result missing content returns MissingField`` () =
    // requireString "content" is the third field checked in the tool_result case;
    // triggered only when "id" and "name" are both present.
    let json = """{"role":"tool_result","id":"call_1","name":"read_file"}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (MissingField "content") -> ()
    | other -> Assert.Fail($"Expected MissingField \"content\" for tool_result, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseToolCallRecord — missing tool field
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``tool_calls with call missing tool field returns error`` () =
    // requireString "tool" in parseToolCallRecord is reached only when "id" is present.
    let json = """{"role":"tool_calls","calls":[{"id":"c1","arguments":{}}]}"""
    use doc  = JsonDocument.Parse(json)
    match parseMessageLine doc.RootElement with
    | Error (MissingField "tool") -> ()
    | Error _ -> ()   // any error is acceptable — traverseResult propagates any parse error
    | Ok other -> Assert.Fail($"Expected Error for call missing tool, got {other}")
