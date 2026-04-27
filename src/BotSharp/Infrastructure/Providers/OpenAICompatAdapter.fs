module BotSharp.Infrastructure.Providers.OpenAICompatAdapter

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.AsyncResult
open BotSharp.Infrastructure.Providers.SseParser
open BotSharp.Infrastructure.Providers.LlmResponseParser

// ═══════════════════════════════════════════════════════════════════════════
// OpenAI-compat HTTP adapter
//
// Supports both non-streaming (POST → read full JSON body) and streaming
// (POST with "stream":true → read SSE lines, emit StreamEvents).
//
// Design:
//   • Request building is pure (no IO)
//   • IO uses HttpClient injected by the caller (reuse for connection pooling)
//   • Errors are classified into LlmErrorKind based on HTTP status
//   • No retry logic here — the Application layer wraps with RetryPolicy
// ═══════════════════════════════════════════════════════════════════════════

// ── Request body construction (pure) ─────────────────────────────────────

// ── JSON Schema serializer ────────────────────────────────────────────────
//
// Design: `go` always returns a COMPLETE JSON Schema object, never a bare type
// name.  `JsString` → `{"type":"string"}`, not `"string"`.  This ensures that
// when a schema is used as the value of `"items"` or `"properties"/<key>`, the
// result is valid JSON Schema and not rejected by strict validators (e.g. DeepSeek).
//
// Previous design returned bare strings for primitives and set them as
// `propObj["type"] = "string"` — correct for top-level but wrong for nested use
// like `{"items":"string"}` which must be `{"items":{"type":"string"}}`.

let private schemaTypeToJson (t: JsonSchemaType) : JsonObject =
    let rec go (t: JsonSchemaType) : JsonObject =
        match t with
        | JsString  -> let o = JsonObject() in o["type"] <- JsonValue.Create("string");  o
        | JsNumber  -> let o = JsonObject() in o["type"] <- JsonValue.Create("number");  o
        | JsBoolean -> let o = JsonObject() in o["type"] <- JsonValue.Create("boolean"); o
        | JsAny     -> let o = JsonObject() in o["type"] <- JsonValue.Create("object");  o
        | JsArray items ->
            let obj = JsonObject()
            obj["type"]  <- JsonValue.Create("array")
            obj["items"] <- go items     // nested schema, not a bare string
            obj
        | JsEnum values ->
            let obj = JsonObject()
            obj["type"] <- JsonValue.Create("string")
            let arr = JsonArray()
            for v in values do arr.Add(JsonValue.Create(v))
            obj["enum"] <- arr
            obj
        | JsObject props ->
            let obj = JsonObject()
            obj["type"] <- JsonValue.Create("object")
            let propsObj = JsonObject()
            let required = JsonArray()
            for kv in props do
                let propSchema = go kv.Value.Type
                propSchema["description"] <- JsonValue.Create(kv.Value.Description)
                propsObj[kv.Key]          <- propSchema
                if kv.Value.Required then required.Add(JsonValue.Create(kv.Key))
            obj["properties"] <- propsObj
            obj["required"]   <- required
            obj
    go t

let private convertTool (spec: ToolSpec) : JsonNode =
    let (ToolName name) = spec.Name
    let fn = JsonObject()
    fn["name"]        <- JsonValue.Create(name)
    fn["description"] <- JsonValue.Create(spec.Description)
    let parameters = JsonObject()
    parameters["type"] <- JsonValue.Create("object")
    let props = JsonObject()
    let required = JsonArray()
    for kv in spec.Parameters do
        // schemaTypeToJson returns a complete JsonObject; add description in-place.
        let propSchema = schemaTypeToJson kv.Value.Type
        propSchema["description"] <- JsonValue.Create(kv.Value.Description)
        props[kv.Key]             <- propSchema
        if kv.Value.Required then required.Add(JsonValue.Create(kv.Key))
    parameters["properties"] <- props
    parameters["required"]   <- required
    fn["parameters"]          <- parameters
    let tool = JsonObject()
    tool["type"]     <- JsonValue.Create("function")
    tool["function"] <- fn
    tool

/// Detect MIME type of image bytes from magic bytes (first 16 bytes).
let private detectImageMime (header: byte[]) : string option =
    if header.Length >= 3 && header.[0] = 0xFFuy && header.[1] = 0xD8uy && header.[2] = 0xFFuy then
        Some "image/jpeg"
    elif header.Length >= 8 &&
         header.[0] = 0x89uy && header.[1] = 0x50uy && header.[2] = 0x4Euy && header.[3] = 0x47uy &&
         header.[4] = 0x0Duy && header.[5] = 0x0Auy && header.[6] = 0x1Auy && header.[7] = 0x0Auy then
        Some "image/png"
    elif header.Length >= 6 &&
         ((header.[0] = 0x47uy && header.[1] = 0x49uy && header.[2] = 0x46uy &&
           header.[3] = 0x38uy && (header.[4] = 0x37uy || header.[4] = 0x39uy) && header.[5] = 0x61uy)) then
        Some "image/gif"
    elif header.Length >= 4 &&
         header.[0] = 0x52uy && header.[1] = 0x49uy && header.[2] = 0x46uy && header.[3] = 0x46uy then
        Some "image/webp"
    else
        None

/// Try to read an image file and build an image_url block (base64 data URL).
/// Returns None if file doesn't exist, isn't an image, or can't be read.
let private tryBuildImageBlock (path: string) : JsonNode option =
    try
        let bytes = File.ReadAllBytes(path)
        let header = bytes.[..min 15 (bytes.Length - 1)]
        match detectImageMime header with
        | None -> None
        | Some mime ->
            let b64  = Convert.ToBase64String(bytes)
            let dataUrl = $"data:{mime};base64,{b64}"
            let block = JsonObject()
            block["type"] <- JsonValue.Create("image_url")
            let inner = JsonObject()
            inner["url"] <- JsonValue.Create(dataUrl)
            block["image_url"] <- inner
            Some (block :> JsonNode)
    with _ -> None

let private convertMessage (msg: Message) : JsonNode =
    let obj = JsonObject()
    match msg with
    | SystemMessage content ->
        obj["role"]    <- JsonValue.Create("system")
        obj["content"] <- JsonValue.Create(content)
    | UserMessage (content, media) ->
        obj["role"] <- JsonValue.Create("user")
        // Build image blocks for any ImageFile media items
        let imageBlocks =
            media |> List.choose (fun m ->
                match m with
                | ImageFile path -> tryBuildImageBlock (LocalFilePath.value path)
                | _ -> None)
        if imageBlocks.IsEmpty then
            obj["content"] <- JsonValue.Create(content)
        else
            // Vision format: [image_url blocks...] + text block
            let contentArr = JsonArray()
            for block in imageBlocks do contentArr.Add(block)
            let textBlock = JsonObject()
            textBlock["type"] <- JsonValue.Create("text")
            textBlock["text"] <- JsonValue.Create(content)
            contentArr.Add(textBlock)
            obj["content"] <- contentArr
    | AssistantMessage (content, rcOpt) ->
        obj["role"]    <- JsonValue.Create("assistant")
        obj["content"] <- JsonValue.Create(content)
        match rcOpt with
        | Some rc -> obj["reasoning_content"] <- JsonValue.Create(rc)
        | None    -> ()
    | ToolCallMessage (nel, rcOpt) ->
        obj["role"]    <- JsonValue.Create("assistant")
        obj["content"] <- JsonValue.Create(Unchecked.defaultof<string>)
        match rcOpt with
        | Some rc -> obj["reasoning_content"] <- JsonValue.Create(rc)
        | None    -> ()
        let toolCalls = JsonArray()
        for call in NonEmptyList.toList nel do
            let (ToolCallId id)   = call.Id
            let (ToolName   name) = call.Tool
            let tc = JsonObject()
            tc["id"]   <- JsonValue.Create(id)
            tc["type"] <- JsonValue.Create("function")
            let fn = JsonObject()
            fn["name"] <- JsonValue.Create(name)
            let argsObj = JsonObject()
            for kv in call.Arguments do
                argsObj[kv.Key] <- JsonNode.Parse(kv.Value.GetRawText())
            fn["arguments"] <- JsonValue.Create(argsObj.ToJsonString())
            tc["function"]  <- fn
            toolCalls.Add(tc)
        obj["tool_calls"] <- toolCalls
    | ToolResultMessage (id, _name, content) ->
        let (ToolCallId idStr) = id
        obj["role"]         <- JsonValue.Create("tool")
        obj["tool_call_id"] <- JsonValue.Create(idStr)
        obj["content"]      <- JsonValue.Create(content)
    obj

let buildRequestBody
    (model              : string)
    (settings           : GenerationSettings)
    (messages           : Message list)
    (tools              : ToolSpec list)
    (stream             : bool)
    (includeStreamUsage : bool)
    : string =
    let root = JsonObject()
    root["model"]             <- JsonValue.Create(model)
    root["temperature"]       <- JsonValue.Create(settings.Temperature)
    root["max_tokens"]        <- JsonValue.Create(settings.MaxTokens)
    root["stream"]            <- JsonValue.Create(stream)
    // stream_options.include_usage=true is an OpenAI extension; only send it when
    // the provider has declared StreamUsageTracking capability.  Sending it to
    // providers that don't support it (e.g. iFlytek MaaS) causes a format mismatch
    // that silently empties the response stream.
    if stream && includeStreamUsage then
        let opts = JsonObject()
        opts["include_usage"] <- JsonValue.Create(true)
        root["stream_options"] <- opts
    // reasoning_effort is an OpenAI o-series / thinking-mode parameter — omit when None
    match settings.ReasoningEffort with
    | Some Low      -> root["reasoning_effort"] <- JsonValue.Create("low")
    | Some Medium   -> root["reasoning_effort"] <- JsonValue.Create("medium")
    | Some High     -> root["reasoning_effort"] <- JsonValue.Create("high")
    | Some Adaptive -> root["reasoning_effort"] <- JsonValue.Create("auto")  // OpenAI calls "adaptive" → "auto"
    | None          -> ()
    let msgs = JsonArray()
    for m in messages do msgs.Add(convertMessage m)
    root["messages"] <- msgs
    if not tools.IsEmpty then
        let ts = JsonArray()
        for t in tools do ts.Add(convertTool t)
        root["tools"] <- ts
    root.ToJsonString()

// ── Error classification ─────────────────────────────────────────────────

/// Extract the Retry-After value from an HTTP response as a TimeSpan.
/// Supports both the delta (seconds) form and the HTTP-date form via .NET's
/// RetryConditionHeaderValue; falls back to plain integer-seconds header parsing.
/// Python parity: OpenAICompatProvider._handle_error reads response.headers["Retry-After"].
let private parseRetryAfter (resp: HttpResponseMessage) : TimeSpan option =
    match resp.Headers.RetryAfter with
    | null -> None
    | rv when rv.Delta.HasValue -> Some rv.Delta.Value
    | rv when rv.Date.HasValue ->
        let delay = rv.Date.Value - DateTimeOffset.UtcNow
        if delay > TimeSpan.Zero then Some delay else None
    | _ ->
        // Fallback: parse the raw header value as an integer (seconds)
        match resp.Headers.TryGetValues("Retry-After") with
        | true, values ->
            match Double.TryParse(Seq.head values) with
            | true, v when v > 0.0 -> Some (TimeSpan.FromSeconds v)
            | _ -> None
        | _ -> None

let private classifyHttpError (statusCode: int) (body: string) (retryAfter: TimeSpan option) : LlmError =
    let kind =
        match statusCode with
        | 429 -> RateLimited retryAfter   // Python parity: honours Retry-After header
        | 400 -> MalformedResponse (SchemaError ("request", body.[..200]))
        | 401 | 403 -> ConnectionFailed $"Unauthorized (HTTP {statusCode})"
        | 404 -> ModelNotFound body.[..100]
        | 413 -> ContextTooLong
        | s when s >= 500 -> ServerError s
        | s -> ConnectionFailed $"Unexpected HTTP status {s}"
    { Kind         = kind
      RawMessage   = body.[..500]
      ProviderCode = None }

// ── Non-streaming request ─────────────────────────────────────────────────

/// Send a non-streaming POST request and parse the complete response.
let chat
    (client       : HttpClient)
    (baseUrl      : string)
    (apiKey       : ApiKey)
    (model        : string)
    (extraHeaders : Map<string, string>)
    (settings     : GenerationSettings)
    (messages     : Message list)
    (tools        : ToolSpec list)
    : AsyncResult<LLMResponse, LlmError> =
    asyncResult {
        let body  = buildRequestBody model settings messages tools false false
        let url   = baseUrl.TrimEnd('/') + "/chat/completions"
        let keyStr = ApiKey.value apiKey
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Headers.Authorization <-
            Net.Http.Headers.AuthenticationHeaderValue("Bearer", keyStr)
        extraHeaders |> Map.iter (fun k v -> req.Headers.TryAddWithoutValidation(k, v) |> ignore)
        req.Content <- content

        let! resp =
            AsyncResult.catch
                (fun ex -> { Kind = ConnectionFailed ex.Message
                             RawMessage = ex.Message; ProviderCode = None})
                (client.SendAsync(req) |> Async.AwaitTask)

        let code = int resp.StatusCode
        let! bodyText =
            AsyncResult.catch
                (fun ex -> { Kind = ConnectionFailed ex.Message
                             RawMessage = ex.Message; ProviderCode = None})
                (resp.Content.ReadAsStringAsync() |> Async.AwaitTask)

        if code <> 200 then
            let retryAfter = parseRetryAfter resp
            return! AsyncResult.ofResult (Error (classifyHttpError code bodyText retryAfter))
        else
            use doc = JsonDocument.Parse(bodyText)
            return!
                parseLlmResponse doc.RootElement
                |> Result.mapError (fun pe ->
                    { Kind = MalformedResponse pe; RawMessage = bodyText.[..500]
                      ProviderCode = None})
                |> AsyncResult.ofResult
    }

// ── Streaming request ─────────────────────────────────────────────────────

/// Send a streaming POST request; call emitter for each StreamEvent.
/// Returns Ok() when the stream completes normally, or Error on failure.
let chatStream
    (client             : HttpClient)
    (baseUrl            : string)
    (apiKey             : ApiKey)
    (model              : string)
    (extraHeaders       : Map<string, string>)
    (settings           : GenerationSettings)
    (messages           : Message list)
    (tools              : ToolSpec list)
    (includeStreamUsage : bool)
    (emitter            : StreamEvent -> Async<unit>)
    : AsyncResult<unit, LlmError> =
    asyncResult {
        let body   = buildRequestBody model settings messages tools true includeStreamUsage
        let url    = baseUrl.TrimEnd('/') + "/chat/completions"
        let keyStr = ApiKey.value apiKey
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Headers.Authorization <-
            Net.Http.Headers.AuthenticationHeaderValue("Bearer", keyStr)
        extraHeaders |> Map.iter (fun k v -> req.Headers.TryAddWithoutValidation(k, v) |> ignore)
        req.Content <- content

        let! resp =
            AsyncResult.catch
                (fun ex -> { Kind = ConnectionFailed ex.Message
                             RawMessage = ex.Message; ProviderCode = None})
                (client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask)

        let code = int resp.StatusCode
        if code <> 200 then
            let! bodyText =
                AsyncResult.catch
                    (fun ex -> { Kind = ConnectionFailed ex.Message
                                 RawMessage = ex.Message; ProviderCode = None})
                    (resp.Content.ReadAsStringAsync() |> Async.AwaitTask)
            let retryAfter = parseRetryAfter resp
            return! AsyncResult.ofResult (Error (classifyHttpError code bodyText retryAfter))
        else
            // Read SSE stream line by line
            let! stream =
                AsyncResult.catch
                    (fun ex -> { Kind = ConnectionFailed ex.Message
                                 RawMessage = ex.Message; ProviderCode = None})
                    (resp.Content.ReadAsStreamAsync() |> Async.AwaitTask)

            use reader = new StreamReader(stream)

            // Tail-recursive SSE line processor (AsyncResultBuilder has no While)
            let rec readLoop () : AsyncResult<unit, LlmError> =
                asyncResult {
                    let! line = reader.ReadLineAsync() |> Async.AwaitTask |> AsyncResult.ofAsync
                    match line with
                    | null -> return ()
                    | line ->
                        match parseSseLine line with
                        | Result.Ok DoneLine    -> return ()
                        | Result.Ok CommentLine -> return! readLoop ()
                        | Result.Ok (DataLine json) ->
                            try
                                use doc = JsonDocument.Parse(json)
                                match parseStreamChunk doc.RootElement with
                                | Result.Ok (Some evt) ->
                                    do! emitter evt |> AsyncResult.ofAsync
                                    return! readLoop ()
                                | Result.Ok None ->
                                    return! readLoop ()
                                | Result.Error pe ->
                                    let err = { Kind = MalformedResponse pe
                                                RawMessage = json.[..min 200 (json.Length-1)]
                                                ProviderCode = None }
                                    do! emitter (StreamError err) |> AsyncResult.ofAsync
                                    return! readLoop ()
                            with ex ->
                                let pe = JsonParseError (ex.Message, 0)
                                let err = { Kind = MalformedResponse pe
                                            RawMessage = line.[..min 200 (line.Length-1)]
                                            ProviderCode = None }
                                do! emitter (StreamError err) |> AsyncResult.ofAsync
                                return! readLoop ()
                        | Result.Error _ ->
                            return! readLoop ()  // ignore malformed SSE lines
                }

            return! readLoop ()
    }

// ── LLMProvider factory ───────────────────────────────────────────────────

/// Create an LLMProvider record-of-functions for a given endpoint and API key.
/// The HttpClient is shared across all requests (caller owns its lifetime).
let createProvider
    (client          : HttpClient)
    (providerId      : string)
    (baseUrl         : string)
    (apiKey          : ApiKey)
    (model           : string)
    (caps            : Set<ProviderCapability>)
    (retryMode       : string)
    (extraHeaders    : Map<string, string>)
    : LLMProvider =
    let retryPolicy =
        match retryMode with
        | "persistent" -> RetryPolicy.persistent
        | _            -> RetryPolicy.standard
    { Id           = providerId
      DefaultModel = model
      Capabilities = caps
      RetryPolicy  = retryPolicy

      Chat = fun settings messages tools ->
          chat client baseUrl apiKey model extraHeaders settings messages tools

      ChatStream = fun settings messages tools emitter ->
          let includeUsage = caps.Contains StreamUsageTracking
          chatStream client baseUrl apiKey model extraHeaders settings messages tools includeUsage emitter }
