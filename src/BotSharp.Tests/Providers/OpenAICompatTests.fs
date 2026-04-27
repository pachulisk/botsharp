module BotSharp.Tests.Providers.OpenAICompatTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Providers.OpenAICompatAdapter
open BotSharp.Infrastructure.Providers.ProviderRegistry

// ═══════════════════════════════════════════════════════════════════════════
// HTTP mock helpers
// ═══════════════════════════════════════════════════════════════════════════

/// A fake HttpMessageHandler that returns a fixed response
type StubHandler(statusCode: HttpStatusCode, body: string, ?contentType: string) =
    inherit HttpMessageHandler()
    let ct = defaultArg contentType "application/json"
    override _.SendAsync(_, _) =
        let resp = new HttpResponseMessage(statusCode)
        resp.Content <- new StringContent(body, Encoding.UTF8, ct)
        System.Threading.Tasks.Task.FromResult(resp)

/// A fake handler that also sets a Retry-After response header (for 429 tests).
type StubHandlerWithRetryAfter(statusCode: HttpStatusCode, body: string, retryAfterSeconds: float) =
    inherit HttpMessageHandler()
    override _.SendAsync(_, _) =
        let resp = new HttpResponseMessage(statusCode)
        resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        resp.Headers.RetryAfter <- Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds))
        System.Threading.Tasks.Task.FromResult(resp)

/// An SSE handler that returns multiple lines one at a time
type SseHandler(lines: string list) =
    inherit HttpMessageHandler()
    override _.SendAsync(_, _) =
        let body = lines |> String.concat "\n"
        let resp = new HttpResponseMessage(HttpStatusCode.OK)
        resp.Content <- new StringContent(body, Encoding.UTF8, "text/event-stream")
        System.Threading.Tasks.Task.FromResult(resp)

let private makeClient (handler: HttpMessageHandler) = new HttpClient(handler)

let private dummyKey =
    match ApiKey.create "sk-test" with
    | Ok k -> k
    | Error e -> failwith e
let private baseUrl   = "https://api.example.com/v1"
let private model     = "gpt-4o-mini"
let private settings  = GenerationSettings.defaults
let private messages  = [ UserMessage ("hello", []) ]

// ═══════════════════════════════════════════════════════════════════════════
// Request body construction (pure, no HTTP)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequestBody includes model and messages`` () =
    let json = buildRequestBody model settings messages [] false false
    use doc  = JsonDocument.Parse(json)
    let root = doc.RootElement
    Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString())
    Assert.Equal(JsonValueKind.Array, root.GetProperty("messages").ValueKind)
    Assert.Equal(1, root.GetProperty("messages").GetArrayLength())

[<Fact>]
let ``buildRequestBody sets stream false for non-streaming`` () =
    let json = buildRequestBody model settings messages [] false false
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.GetProperty("stream").GetBoolean())

[<Fact>]
let ``buildRequestBody sets stream true for streaming`` () =
    let json = buildRequestBody model settings messages [] true false
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.GetProperty("stream").GetBoolean())

[<Fact>]
let ``buildRequestBody includes tool definitions when tools provided`` () =
    let spec = { Name = ToolName "read_file"
                 Description = "Read a file"
                 Parameters = Map.ofList [
                     "path", { Type = JsString; Description = "File path"; Required = true }
                 ]
                 ConcurrencySafe = false }
    let json = buildRequestBody model settings messages [spec] false false
    use doc  = JsonDocument.Parse(json)
    let tools = doc.RootElement.GetProperty("tools")
    Assert.Equal(1, tools.GetArrayLength())
    let tool = tools[0]
    Assert.Equal("function", tool.GetProperty("type").GetString())
    Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString())

[<Fact>]
let ``buildRequestBody omits tools array when no tools`` () =
    let json = buildRequestBody model settings messages [] false false
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("tools") |> fst)

[<Fact>]
let ``buildRequestBody converts SystemMessage to role:system`` () =
    let msgs = [ SystemMessage "You are a helpful assistant."; UserMessage ("hi", []) ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let arr  = doc.RootElement.GetProperty("messages")
    Assert.Equal(2, arr.GetArrayLength())
    // First message must have role:system
    Assert.Equal("system", arr[0].GetProperty("role").GetString())
    Assert.Equal("You are a helpful assistant.", arr[0].GetProperty("content").GetString())
    // Second message has role:user
    Assert.Equal("user", arr[1].GetProperty("role").GetString())

[<Fact>]
let ``buildRequestBody omits reasoning_effort when None`` () =
    let s = { settings with ReasoningEffort = None }
    let json = buildRequestBody model s messages [] false false
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("reasoning_effort") |> fst)

[<Fact>]
let ``buildRequestBody includes reasoning_effort medium when Some Medium`` () =
    let s = { settings with ReasoningEffort = Some Medium }
    let json = buildRequestBody model s messages [] false false
    use doc  = JsonDocument.Parse(json)
    match doc.RootElement.TryGetProperty("reasoning_effort") with
    | true, v -> Assert.Equal("medium", v.GetString())
    | false, _ -> Assert.Fail("Expected reasoning_effort key")

[<Fact>]
let ``buildRequestBody includes reasoning_effort low when Some Low`` () =
    let s = { settings with ReasoningEffort = Some Low }
    let json = buildRequestBody model s messages [] false false
    use doc  = JsonDocument.Parse(json)
    match doc.RootElement.TryGetProperty("reasoning_effort") with
    | true, v -> Assert.Equal("low", v.GetString())
    | false, _ -> Assert.Fail("Expected reasoning_effort key for Low")

[<Fact>]
let ``buildRequestBody includes reasoning_effort high when Some High`` () =
    let s = { settings with ReasoningEffort = Some High }
    let json = buildRequestBody model s messages [] false false
    use doc  = JsonDocument.Parse(json)
    match doc.RootElement.TryGetProperty("reasoning_effort") with
    | true, v -> Assert.Equal("high", v.GetString())
    | false, _ -> Assert.Fail("Expected reasoning_effort key for High")

[<Fact>]
let ``buildRequestBody maps Adaptive to auto for OpenAI compat`` () =
    // Critical: OpenAI calls "adaptive" mode "auto"; our domain uses Adaptive.
    let s = { settings with ReasoningEffort = Some Adaptive }
    let json = buildRequestBody model s messages [] false false
    use doc  = JsonDocument.Parse(json)
    match doc.RootElement.TryGetProperty("reasoning_effort") with
    | true, v -> Assert.Equal("auto", v.GetString())
    | false, _ -> Assert.Fail("Expected reasoning_effort key for Adaptive")

// ═══════════════════════════════════════════════════════════════════════════
// Non-streaming HTTP: chat
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chat returns TextOnly on 200 response`` () =
    let responseJson = """
    { "choices": [{ "message": { "role": "assistant", "content": "Hello!" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 5, "completion_tokens": 3, "cached_tokens": 0 } }"""
    use client = makeClient (new StubHandler(HttpStatusCode.OK, responseJson))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Ok { Body = TextOnly "Hello!" } -> ()
    | other -> Assert.Fail($"Expected TextOnly \"Hello!\", got {other}")

[<Fact>]
let ``chat returns RateLimited error on 429`` () =
    use client = makeClient (new StubHandler(HttpStatusCode.TooManyRequests, "Rate limit exceeded"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = RateLimited _ } -> ()
    | other -> Assert.Fail($"Expected RateLimited, got {other}")

[<Fact>]
let ``chat returns ServerError on 500`` () =
    use client = makeClient (new StubHandler(HttpStatusCode.InternalServerError, "Internal error"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ServerError 500 } -> ()
    | other -> Assert.Fail($"Expected ServerError 500, got {other}")

[<Fact>]
let ``chat returns ContextTooLong on 413`` () =
    use client = makeClient (new StubHandler(HttpStatusCode.RequestEntityTooLarge, "Too large"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ContextTooLong } -> ()
    | other -> Assert.Fail($"Expected ContextTooLong, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Streaming: chatStream
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chatStream emits TextDelta events`` () =
    let chunk1 = """data: {"choices":[{"delta":{"content":"Hello"},"finish_reason":null}]}"""
    let chunk2 = """data: {"choices":[{"delta":{"content":" world"},"finish_reason":null}]}"""
    let done_  = "data: [DONE]"
    use client = makeClient (new SseHandler([chunk1; chunk2; done_]))
    let events = System.Collections.Generic.List<StreamEvent>()
    let emitter evt = async { events.Add(evt) }
    let result =
        chatStream client baseUrl dummyKey model Map.empty settings messages [] false emitter
        |> Async.RunSynchronously
    Assert.True(Result.isOk result, $"Expected Ok, got {result}")
    Assert.Equal(2, events.Count)
    match events[0] with
    | ContentDelta (TextDelta "Hello") -> ()
    | other -> Assert.Fail($"Expected TextDelta \"Hello\", got {other}")
    match events[1] with
    | ContentDelta (TextDelta " world") -> ()
    | other -> Assert.Fail($"Expected TextDelta \" world\", got {other}")

[<Fact>]
let ``chatStream returns RateLimited on 429`` () =
    use client = makeClient (new StubHandler(HttpStatusCode.TooManyRequests, "limit"))
    let result =
        chatStream client baseUrl dummyKey model Map.empty settings messages [] false (fun _ -> async { () })
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = RateLimited _ } -> ()
    | other -> Assert.Fail($"Expected RateLimited, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Vision / media support (image_url content blocks)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``UserMessage without media sends plain string content`` () =
    let msgs = [ UserMessage ("hello vision", []) ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let msgArr = doc.RootElement.GetProperty("messages")
    let userMsg = msgArr.[0]
    // Content should be a plain string, not an array
    Assert.Equal(JsonValueKind.String, userMsg.GetProperty("content").ValueKind)
    Assert.Equal("hello vision", userMsg.GetProperty("content").GetString())

[<Fact>]
let ``UserMessage with ImageFile sends content array with image_url block`` () =
    // Write a minimal JPEG magic bytes to a temp file
    let tmp = System.IO.Path.GetTempFileName()
    try
        // JPEG magic: FF D8 FF
        let jpegHeader = [| 0xFFuy; 0xD8uy; 0xFFuy; 0xE0uy; 0x00uy; 0x10uy |]
        System.IO.File.WriteAllBytes(tmp, jpegHeader)
        let path = LocalFilePath.ofAbsolute tmp
        let msgs = [ UserMessage ("describe this", [ ImageFile path ]) ]
        let json = buildRequestBody model settings msgs [] false false
        use doc  = JsonDocument.Parse(json)
        let msgArr = doc.RootElement.GetProperty("messages")
        let userMsg = msgArr.[0]
        let content = userMsg.GetProperty("content")
        // Content should be an array (image_url blocks + text block)
        Assert.Equal(JsonValueKind.Array, content.ValueKind)
        Assert.Equal(2, content.GetArrayLength())
        // First block: image_url
        let imgBlock = content.[0]
        Assert.Equal("image_url", imgBlock.GetProperty("type").GetString())
        let url = imgBlock.GetProperty("image_url").GetProperty("url").GetString() |> Option.ofObj |> Option.defaultValue ""
        Assert.True(url.StartsWith("data:image/jpeg;base64,"), $"Expected JPEG data URL, got: {url.[..40]}")
        // Last block: text
        let textBlock = content.[content.GetArrayLength() - 1]
        Assert.Equal("text", textBlock.GetProperty("type").GetString())
        Assert.Equal("describe this", textBlock.GetProperty("text").GetString())
    finally
        if System.IO.File.Exists(tmp) then System.IO.File.Delete(tmp)

[<Fact>]
let ``UserMessage with non-existent ImageFile falls back to plain string`` () =
    let path = LocalFilePath.ofAbsolute "/nonexistent/image.jpg"
    let msgs = [ UserMessage ("text only", [ ImageFile path ]) ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let msgArr = doc.RootElement.GetProperty("messages")
    let userMsg = msgArr.[0]
    // File doesn't exist → fall back to plain string content
    Assert.Equal(JsonValueKind.String, userMsg.GetProperty("content").ValueKind)
    Assert.Equal("text only", userMsg.GetProperty("content").GetString())

// ═══════════════════════════════════════════════════════════════════════════
// Provider registry
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``detectProvider matches gpt-4o to openai`` () =
    match detectProvider "gpt-4o-mini" with
    | Some { Id = "openai" } -> ()
    | other -> Assert.Fail($"Expected openai spec, got {other}")

[<Fact>]
let ``detectProvider matches deepseek-r1 to deepseek`` () =
    match detectProvider "deepseek-r1" with
    | Some { Id = "deepseek" } -> ()
    | other -> Assert.Fail($"Expected deepseek spec, got {other}")

[<Fact>]
let ``detectProvider returns None for unknown model`` () =
    Assert.Equal(None, detectProvider "unknown-model-xyz")

[<Fact>]
let ``resolve returns None when no API key is set`` () =
    // Config has no API keys and env var is not set
    let config = { BotSharpConfig.defaults with ApiKeys = Map.empty; DefaultProvider = "openai" }
    use client = new HttpClient()
    // If env var OPENAI_API_KEY is set in CI, skip; otherwise verify None
    let key = ApiKey.tryFromEnv "OPENAI_API_KEY"
    if key.IsNone then
        Assert.Equal(None, resolve client "gpt-4o-mini" config)

[<Fact>]
let ``chat returns ConnectionFailed on 401 Unauthorized`` () =
    use client = makeClient (new StubHandler(HttpStatusCode.Unauthorized, "Invalid API key"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ConnectionFailed msg } ->
        Assert.Contains("401", msg)
    | other -> Assert.Fail($"Expected ConnectionFailed for 401, got {other}")

[<Fact>]
let ``chat returns ModelNotFound on 404`` () =
    use client = makeClient (new StubHandler(HttpStatusCode.NotFound, "Model not found"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ModelNotFound _ } -> ()
    | other -> Assert.Fail($"Expected ModelNotFound for 404, got {other}")

[<Fact>]
let ``chat returns MalformedResponse when JSON schema is wrong`` () =
    // 200 OK with valid JSON that lacks the required 'choices' field → schema error → MalformedResponse
    use client = makeClient (new StubHandler(HttpStatusCode.OK, """{"error":"unexpected"}"""))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = MalformedResponse _ } -> ()
    | other -> Assert.Fail($"Expected MalformedResponse for schema mismatch, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// HTTP error classification — additional status codes
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chat returns MalformedResponse on 400 Bad Request`` () =
    // classifyHttpError 400 → MalformedResponse (SchemaError ("request", body))
    use client = makeClient (new StubHandler(HttpStatusCode.BadRequest, "bad request body"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = MalformedResponse _ } -> ()
    | other -> Assert.Fail($"Expected MalformedResponse for 400, got {other}")

[<Fact>]
let ``chat returns ConnectionFailed on 403 Forbidden`` () =
    // classifyHttpError 403 → ConnectionFailed "Unauthorized (HTTP 403)"
    use client = makeClient (new StubHandler(HttpStatusCode.Forbidden, "forbidden"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ConnectionFailed msg } ->
        Assert.Contains("403", msg)
    | other -> Assert.Fail($"Expected ConnectionFailed for 403, got {other}")

[<Fact>]
let ``chat returns ServerError 503 on 503 response`` () =
    // classifyHttpError for s >= 500 → ServerError s (tests the wildcard branch)
    use client = makeClient (new StubHandler(HttpStatusCode.ServiceUnavailable, "service unavailable"))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = ServerError 503 } -> ()
    | other -> Assert.Fail($"Expected ServerError 503, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// convertMessage — ToolCallMessage and ToolResultMessage
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequestBody serializes ToolCallMessage as role:assistant with tool_calls`` () =
    let args =
        let doc = JsonDocument.Parse("""{"path":"/tmp"}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = { Id = ToolCallId "call_x"; Tool = ToolName "read_file"; Arguments = args; ProviderMeta = None }
    let msgs = [ ToolCallMessage (NonEmptyList.singleton call, None) ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let arr  = doc.RootElement.GetProperty("messages")
    Assert.Equal(1, arr.GetArrayLength())
    let msg = arr.[0]
    Assert.Equal("assistant", msg.GetProperty("role").GetString())
    // tool_calls array must be present
    let toolCalls = msg.GetProperty("tool_calls")
    Assert.Equal(JsonValueKind.Array, toolCalls.ValueKind)
    Assert.Equal(1, toolCalls.GetArrayLength())
    let tc = toolCalls.[0]
    Assert.Equal("call_x", tc.GetProperty("id").GetString())
    Assert.Equal("function", tc.GetProperty("type").GetString())
    Assert.Equal("read_file", tc.GetProperty("function").GetProperty("name").GetString())

[<Fact>]
let ``buildRequestBody serializes ToolResultMessage as role:tool with tool_call_id`` () =
    let msgs = [ ToolResultMessage (ToolCallId "call_y", ToolName "read_file", "file contents") ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let arr  = doc.RootElement.GetProperty("messages")
    Assert.Equal(1, arr.GetArrayLength())
    let msg = arr.[0]
    Assert.Equal("tool", msg.GetProperty("role").GetString())
    Assert.Equal("call_y", msg.GetProperty("tool_call_id").GetString())
    Assert.Equal("file contents", msg.GetProperty("content").GetString())

[<Fact>]
let ``buildRequestBody serializes AssistantMessage with reasoning_content when Some`` () =
    let msgs = [ AssistantMessage ("Final answer.", Some "I thought step by step.") ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let msg  = doc.RootElement.GetProperty("messages").[0]
    Assert.Equal("assistant",            msg.GetProperty("role").GetString())
    Assert.Equal("Final answer.",        msg.GetProperty("content").GetString())
    Assert.Equal("I thought step by step.", msg.GetProperty("reasoning_content").GetString())

[<Fact>]
let ``buildRequestBody omits reasoning_content from AssistantMessage when None`` () =
    let msgs = [ AssistantMessage ("Plain reply.", None) ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let msg  = doc.RootElement.GetProperty("messages").[0]
    Assert.Equal("assistant",   msg.GetProperty("role").GetString())
    Assert.Equal("Plain reply.", msg.GetProperty("content").GetString())
    Assert.False(msg.TryGetProperty("reasoning_content") |> fst,
                 "reasoning_content must be absent when None")

[<Fact>]
let ``buildRequestBody serializes ToolCallMessage with reasoning_content when Some`` () =
    let call = { Id = ToolCallId "call_r"; Tool = ToolName "think_tool"; Arguments = Map.empty; ProviderMeta = None }
    let msgs = [ ToolCallMessage (NonEmptyList.singleton call, Some "I thought about tool use.") ]
    let json = buildRequestBody model settings msgs [] false false
    use doc  = JsonDocument.Parse(json)
    let msg  = doc.RootElement.GetProperty("messages").[0]
    Assert.Equal("assistant", msg.GetProperty("role").GetString())
    Assert.Equal("I thought about tool use.", msg.GetProperty("reasoning_content").GetString())

// ═══════════════════════════════════════════════════════════════════════════
// schemaTypeToJson — JsBoolean, JsNumber, JsAny, JsArray, JsEnum
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequestBody serializes JsBoolean tool parameter type`` () =
    let spec = { Name = ToolName "toggle"
                 Description = "Toggle something"
                 Parameters = Map.ofList [
                     "enabled", { Type = JsBoolean; Description = "Whether to enable"; Required = true }
                 ]
                 ConcurrencySafe = false }
    let json = buildRequestBody model settings messages [spec] false false
    use doc  = JsonDocument.Parse(json)
    let toolFn = doc.RootElement.GetProperty("tools").[0].GetProperty("function")
    let propType = toolFn.GetProperty("parameters").GetProperty("properties").GetProperty("enabled").GetProperty("type").GetString()
    Assert.Equal("boolean", propType)

[<Fact>]
let ``buildRequestBody serializes JsNumber tool parameter type`` () =
    let spec = { Name = ToolName "counter"
                 Description = "A counter"
                 Parameters = Map.ofList [
                     "count", { Type = JsNumber; Description = "Count"; Required = false }
                 ]
                 ConcurrencySafe = false }
    let json = buildRequestBody model settings messages [spec] false false
    use doc  = JsonDocument.Parse(json)
    let toolFn = doc.RootElement.GetProperty("tools").[0].GetProperty("function")
    let propType = toolFn.GetProperty("parameters").GetProperty("properties").GetProperty("count").GetProperty("type").GetString()
    Assert.Equal("number", propType)

[<Fact>]
let ``buildRequestBody serializes JsAny tool parameter type as object`` () =
    // JsAny → "object" in schemaTypeToJson
    let spec = { Name = ToolName "flexible"
                 Description = "Flexible tool"
                 Parameters = Map.ofList [
                     "data", { Type = JsAny; Description = "Any data"; Required = false }
                 ]
                 ConcurrencySafe = false }
    let json = buildRequestBody model settings messages [spec] false false
    use doc  = JsonDocument.Parse(json)
    let toolFn = doc.RootElement.GetProperty("tools").[0].GetProperty("function")
    let propType = toolFn.GetProperty("parameters").GetProperty("properties").GetProperty("data").GetProperty("type").GetString()
    Assert.Equal("object", propType)

[<Fact>]
let ``buildRequestBody serializes JsArray tool parameter type`` () =
    // JsArray JsString → prop = {"type":"array","items":{"type":"string"},"description":"..."}
    // Previously the items value was the bare string "string" (invalid per strict JSON Schema).
    // Fix: schemaTypeToJson always returns a full schema object so items is {"type":"string"}.
    let spec = { Name = ToolName "list_tool"
                 Description = "Takes a list"
                 Parameters = Map.ofList [
                     "items", { Type = JsArray JsString; Description = "List of strings"; Required = true }
                 ]
                 ConcurrencySafe = false }
    let json = buildRequestBody model settings messages [spec] false false
    use doc  = JsonDocument.Parse(json)
    let toolFn = doc.RootElement.GetProperty("tools").[0].GetProperty("function")
    let prop = toolFn.GetProperty("parameters").GetProperty("properties").GetProperty("items")
    Assert.Equal("array", prop.GetProperty("type").GetString())
    let itemsSchema = prop.GetProperty("items")   // must be {"type":"string"}, not bare "string"
    Assert.Equal("string", itemsSchema.GetProperty("type").GetString())

[<Fact>]
let ``buildRequestBody serializes JsEnum tool parameter type`` () =
    // JsEnum values → prop = {"type":"string","enum":[...],"description":"..."}
    // Previously enum and type were nested under prop["type"]; now they are top-level on prop.
    let spec = { Name = ToolName "mode_tool"
                 Description = "Mode selector"
                 Parameters = Map.ofList [
                     "mode", { Type = JsEnum ["read"; "write"; "append"]; Description = "Mode"; Required = true }
                 ]
                 ConcurrencySafe = false }
    let json = buildRequestBody model settings messages [spec] false false
    use doc  = JsonDocument.Parse(json)
    let toolFn = doc.RootElement.GetProperty("tools").[0].GetProperty("function")
    let prop = toolFn.GetProperty("parameters").GetProperty("properties").GetProperty("mode")
    Assert.Equal("string", prop.GetProperty("type").GetString())
    let enumArr = prop.GetProperty("enum")   // top-level on prop, not nested under prop["type"]
    Assert.Equal(JsonValueKind.Array, enumArr.ValueKind)
    Assert.Equal(3, enumArr.GetArrayLength())

// ═══════════════════════════════════════════════════════════════════════════
// detectImageMime — PNG and WebP magic bytes
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``UserMessage with PNG ImageFile sends image_url block with image/png MIME type`` () =
    let tmp = System.IO.Path.GetTempFileName()
    try
        // PNG magic: 89 50 4E 47 0D 0A 1A 0A
        let pngHeader = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
        System.IO.File.WriteAllBytes(tmp, pngHeader)
        let path = LocalFilePath.ofAbsolute tmp
        let msgs = [ UserMessage ("describe png", [ ImageFile path ]) ]
        let json = buildRequestBody model settings msgs [] false false
        use doc  = JsonDocument.Parse(json)
        let content = doc.RootElement.GetProperty("messages").[0].GetProperty("content")
        Assert.Equal(JsonValueKind.Array, content.ValueKind)
        let imgBlock = content.[0]
        let url = imgBlock.GetProperty("image_url").GetProperty("url").GetString() |> Option.ofObj |> Option.defaultValue ""
        Assert.True(url.StartsWith("data:image/png;base64,"), $"Expected PNG data URL, got: {url.[..40]}")
    finally
        if System.IO.File.Exists(tmp) then System.IO.File.Delete(tmp)

[<Fact>]
let ``UserMessage with file having unrecognized magic bytes falls back to plain string`` () =
    let tmp = System.IO.Path.GetTempFileName()
    try
        // Unknown magic bytes — detectImageMime returns None → no image block
        let unknownBytes = [| 0x00uy; 0x01uy; 0x02uy; 0x03uy |]
        System.IO.File.WriteAllBytes(tmp, unknownBytes)
        let path = LocalFilePath.ofAbsolute tmp
        let msgs = [ UserMessage ("text", [ ImageFile path ]) ]
        let json = buildRequestBody model settings msgs [] false false
        use doc  = JsonDocument.Parse(json)
        let content = doc.RootElement.GetProperty("messages").[0].GetProperty("content")
        // No recognized image → falls back to plain string
        Assert.Equal(JsonValueKind.String, content.ValueKind)
        Assert.Equal("text", content.GetString())
    finally
        if System.IO.File.Exists(tmp) then System.IO.File.Delete(tmp)

// ═══════════════════════════════════════════════════════════════════════════
// buildRequestBody — stream_options included when streaming
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequestBody includes stream_options with include_usage when streaming`` () =
    // When stream=true AND includeStreamUsage=true, "stream_options":{"include_usage":true} must be present
    let json = buildRequestBody model settings messages [] true true
    use doc  = JsonDocument.Parse(json)
    match doc.RootElement.TryGetProperty("stream_options") with
    | true, opts ->
        match opts.TryGetProperty("include_usage") with
        | true, v -> Assert.True(v.GetBoolean(), "Expected include_usage=true")
        | false, _ -> Assert.Fail("Expected include_usage property in stream_options")
    | false, _ -> Assert.Fail("Expected stream_options when streaming")

[<Fact>]
let ``buildRequestBody omits stream_options when not streaming`` () =
    let json = buildRequestBody model settings messages [] false false
    use doc  = JsonDocument.Parse(json)
    Assert.False(doc.RootElement.TryGetProperty("stream_options") |> fst,
                 "stream_options should be absent when stream=false")

[<Fact>]
let ``buildRequestBody omits stream_options when streaming but includeStreamUsage is false`` () =
    // iFlytek MaaS and similar providers don't support stream_options.include_usage.
    // When StreamUsageTracking capability is absent, includeStreamUsage=false and
    // stream_options must NOT be sent even though stream=true.
    let json = buildRequestBody model settings messages [] true false
    use doc  = JsonDocument.Parse(json)
    Assert.True(doc.RootElement.GetProperty("stream").GetBoolean())
    Assert.False(doc.RootElement.TryGetProperty("stream_options") |> fst,
                 "stream_options should be absent when includeStreamUsage=false")

// ═══════════════════════════════════════════════════════════════════════════
// chatStream — DataLine producing no StreamEvent (Result.Ok None path)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chatStream with DataLine producing no event continues without emitting`` () =
    // A chunk with null delta content → parseStreamChunk returns Ok None → loop continues silently
    let chunk = """data: {"choices":[{"delta":{},"finish_reason":null}]}"""
    let done_ = "data: [DONE]"
    use client = makeClient (new SseHandler([chunk; done_]))
    let events = System.Collections.Generic.List<StreamEvent>()
    let emitter evt = async { events.Add(evt) }
    let result =
        chatStream client baseUrl dummyKey model Map.empty settings messages [] false emitter
        |> Async.RunSynchronously
    Assert.True(Result.isOk result, $"Expected Ok, got {result}")
    // No ContentDelta event emitted for empty delta
    Assert.Equal(0, events.Count)

// ═══════════════════════════════════════════════════════════════════════════
// reasoning_content extraction (Python parity: test_reasoning_content.py)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chat extracts reasoning_content from message object`` () =
    // Python parity: test_parse_dict_extracts_reasoning_content
    // When the response JSON includes reasoning_content in the message,
    // LLMResponse.ReasoningContent is Some with that value.
    let responseJson = """
    { "choices": [{ "message": { "role": "assistant", "content": "42",
                                 "reasoning_content": "Let me think step by step\u2026" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 5, "completion_tokens": 10, "cached_tokens": 0 } }"""
    use client = makeClient (new StubHandler(HttpStatusCode.OK, responseJson))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Ok { Body = TextOnly "42"; ReasoningContent = Some rc } ->
        Assert.Contains("think step by step", rc)
    | Result.Ok { ReasoningContent = None } ->
        Assert.Fail("Expected ReasoningContent to be Some, got None")
    | other -> Assert.Fail($"Expected Ok with reasoning_content, got {other}")

[<Fact>]
let ``chat sets ReasoningContent to None when absent from response`` () =
    // Python parity: test_parse_dict_reasoning_content_none_when_absent
    let responseJson = """
    { "choices": [{ "message": { "role": "assistant", "content": "hello" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 5, "completion_tokens": 3, "cached_tokens": 0 } }"""
    use client = makeClient (new StubHandler(HttpStatusCode.OK, responseJson))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Ok { ReasoningContent = None } -> ()
    | Result.Ok { ReasoningContent = Some rc } ->
        Assert.Fail($"Expected ReasoningContent to be None, got Some \"{rc}\"")
    | other -> Assert.Fail($"Expected Ok, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Retry-After header extraction (Python parity: test_provider_retry_after_hints.py)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chat HTTP 429 with Retry-After header produces RateLimited with Some TimeSpan`` () =
    // Python parity: test_openai_compat_error_captures_retry_after_from_headers
    // When the server returns 429 with a Retry-After: 20 header, the error kind
    // must be RateLimited (Some 20s) so the retry policy can honour the hint.
    use client =
        makeClient (new StubHandlerWithRetryAfter(HttpStatusCode.TooManyRequests,
                                              """{"error":{"message":"Rate limit exceeded"}}""",
                                              20.0))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = RateLimited (Some after) } ->
        Assert.True(after.TotalSeconds >= 19.9 && after.TotalSeconds <= 20.1,
                    $"Expected ~20s Retry-After, got {after.TotalSeconds}s")
    | Result.Error { Kind = RateLimited None } ->
        Assert.Fail("Retry-After header was present but retryAfter is None")
    | other -> Assert.Fail($"Expected RateLimited, got {other}")

[<Fact>]
let ``chat HTTP 429 without Retry-After header produces RateLimited with None`` () =
    // No Retry-After header → retryAfter = None (exponential backoff used instead)
    use client =
        makeClient (new StubHandler(HttpStatusCode.TooManyRequests,
                                """{"error":{"message":"Rate limit exceeded"}}"""))
    let result =
        chat client baseUrl dummyKey model Map.empty settings messages []
        |> Async.RunSynchronously
    match result with
    | Result.Error { Kind = RateLimited None } -> ()
    | Result.Error { Kind = RateLimited (Some after) } ->
        Assert.Fail($"Expected None Retry-After when header absent, got Some {after.TotalSeconds}s")
    | other -> Assert.Fail($"Expected RateLimited, got {other}")
