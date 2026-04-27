module BotSharp.Infrastructure.Channels.ApiChannel

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// OpenAI-compatible HTTP API channel
//
// Endpoints:
//   POST /v1/chat/completions  — OpenAI-compatible chat (stream=true supported)
//   GET  /v1/models            — list available models
//   GET  /health               — health check
//   OPTIONS *                  — CORS preflight
//
// Design notes:
//   • Uses System.Net.HttpListener (no SDK change required).
//   • Listens on localhost:{port} — no elevated privileges needed on macOS.
//     To bind all interfaces, change prefix to "http://+:{port}/" and run
//     as administrator/root (or grant ACL with `netsh http add urlacl`).
//   • The API coordinator uses NoStreaming; SSE "streaming" is implemented by
//     emitting the buffered full response as a single content chunk followed
//     by [DONE]. This means `stream=true` responses are latency-equivalent to
//     non-streaming — they satisfy protocol-level streaming expectations without
//     requiring per-request StreamingHook infrastructure.
//   • Per-session SemaphoreSlim ensures one active request per session at a time.
//   • Request bodies are capped at 2 MB to prevent OOM on malformed input.
// ═══════════════════════════════════════════════════════════════════════════

let private apiChannel = ChannelId "api"
let private maxBodyBytes = 2 * 1024 * 1024   // 2 MB guard

// ── CORS ──────────────────────────────────────────────────────────────────────

let private addCorsHeaders (resp: HttpListenerResponse) =
    resp.Headers.["Access-Control-Allow-Origin"]  <- "*"
    resp.Headers.["Access-Control-Allow-Methods"] <- "GET, POST, OPTIONS"
    resp.Headers.["Access-Control-Allow-Headers"] <- "Content-Type, Authorization"

// ── JSON response helpers ─────────────────────────────────────────────────────

let private writeBytes (bytes: byte[]) (statusCode: int) (contentType: string) (ctx: HttpListenerContext) : Async<unit> =
    async {
        addCorsHeaders ctx.Response
        ctx.Response.StatusCode      <- statusCode
        ctx.Response.ContentType     <- contentType
        ctx.Response.ContentLength64 <- int64 bytes.Length
        try
            do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
        with _ -> ()   // client may have disconnected
        ctx.Response.Close()
    }

let private writeJson (body: string) (statusCode: int) (ctx: HttpListenerContext) : Async<unit> =
    writeBytes (Encoding.UTF8.GetBytes body) statusCode "application/json; charset=utf-8" ctx

// ── ID / timestamp helpers ────────────────────────────────────────────────────

let private makeId () =
    sprintf "chatcmpl-%s" (Guid.NewGuid().ToString("N").[..11])

let private nowTs () = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

// ── Error JSON ────────────────────────────────────────────────────────────────

let private errorJson (message: string) (errType: string) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteStartObject("error")
    w.WriteString("message", message)
    w.WriteString("type",    errType)
    w.WriteEndObject()
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

// ── Non-streaming completion response ────────────────────────────────────────

let private completionJson (content: string) (modelName: string) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms, JsonWriterOptions(Indented = false))
    w.WriteStartObject()
    w.WriteString("id",      makeId())
    w.WriteString("object",  "chat.completion")
    w.WriteNumber("created", nowTs())
    w.WriteString("model",   modelName)
    w.WriteStartArray("choices")
    w.WriteStartObject()
    w.WriteNumber("index", 0)
    w.WriteStartObject("message")
    w.WriteString("role",    "assistant")
    w.WriteString("content", content)
    w.WriteEndObject()
    w.WriteString("finish_reason", "stop")
    w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartObject("usage")
    w.WriteNumber("prompt_tokens",     0)
    w.WriteNumber("completion_tokens", 0)
    w.WriteNumber("total_tokens",      0)
    w.WriteEndObject()
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

// ── SSE chunk ─────────────────────────────────────────────────────────────────

let private sseChunk (content: string) (modelName: string) (chunkId: string) (finishReason: string option) : byte[] =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("id",      chunkId)
    w.WriteString("object",  "chat.completion.chunk")
    w.WriteNumber("created", nowTs())
    w.WriteString("model",   modelName)
    w.WriteStartArray("choices")
    w.WriteStartObject()
    w.WriteNumber("index", 0)
    w.WriteStartObject("delta")
    if content <> "" then w.WriteString("content", content)
    w.WriteEndObject()
    match finishReason with
    | Some r -> w.WriteString("finish_reason", r)
    | None   -> w.WriteNull("finish_reason")
    w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
    w.Flush()
    let json = Encoding.UTF8.GetString(ms.ToArray())
    Encoding.UTF8.GetBytes(sprintf "data: %s\n\n" json)

let private sseDone = Encoding.UTF8.GetBytes("data: [DONE]\n\n")

// ── Models response ───────────────────────────────────────────────────────────

let private modelsJson (modelName: string) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("object", "list")
    w.WriteStartArray("data")
    w.WriteStartObject()
    w.WriteString("id",       modelName)
    w.WriteString("object",   "model")
    w.WriteNumber("created",  0)
    w.WriteString("owned_by", "botsharp")
    w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

// ── Request body parsing ──────────────────────────────────────────────────────

[<Struct>]
type private ParsedRequest = {
    Text      : string
    Stream    : bool
    SessionId : string
    Model     : string option
}

let private parseRequest (bodyBytes: byte[]) : Result<ParsedRequest, string> =
    try
        use doc  = JsonDocument.Parse(bodyBytes)
        let root = doc.RootElement

        // Last user message
        let userText =
            match root.TryGetProperty("messages") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray()
                |> Seq.toList
                |> List.tryFindBack (fun el ->
                    match el.TryGetProperty("role") with
                    | true, v -> v.GetString() = "user"
                    | _       -> false)
                |> Option.bind (fun el ->
                    match el.TryGetProperty("content") with
                    | true, v when v.ValueKind = JsonValueKind.String ->
                        v.GetString() |> Option.ofObj
                    | _ -> None)
                |> Option.defaultValue ""
            | _ -> ""

        let stream =
            match root.TryGetProperty("stream") with
            | true, v -> v.ValueKind = JsonValueKind.True
            | _       -> false

        let sessionId =
            match root.TryGetProperty("session_id") with
            | true, v when v.ValueKind = JsonValueKind.String ->
                v.GetString() |> Option.ofObj |> Option.defaultValue "default"
            | _ -> "default"

        let model =
            match root.TryGetProperty("model") with
            | true, v when v.ValueKind = JsonValueKind.String ->
                v.GetString() |> Option.ofObj
            | _ -> None

        Result.Ok { Text = userText; Stream = stream; SessionId = sessionId; Model = model }
    with ex ->
        Result.Error (sprintf "Invalid JSON: %s" ex.Message)

// ── API server ────────────────────────────────────────────────────────────────

type ApiServer(coordinator: AgentCoordinator, modelName: string, timeoutMs: int) =
    let sessionLocks = ConcurrentDictionary<string, SemaphoreSlim>()
    let listener     = new HttpListener()

    let getLock key =
        sessionLocks.GetOrAdd(key, fun _ -> new SemaphoreSlim(1, 1))

    /// Route a plain text message to the coordinator and return the reply text.
    let routeToAgent (sessionKey: string) (text: string) : Async<Result<string, string>> =
        async {
            let msg : InboundMessage = {
                Channel            = apiChannel
                Sender             = UserId "api"
                Chat               = ChatId sessionKey
                Input              = ChatMessage (text, [])
                Metadata           = Map.ofList [ "source", "api" ]
                SessionKeyOverride = None
            }
            let! result = coordinator.Route msg
            return
                match result with
                | Result.Ok (PlainResponse t)    -> Result.Ok t
                | Result.Ok (StreamedResponse t) -> Result.Ok t   // NoStreaming coordinator won't hit this
                | Result.Error e                 -> Result.Error (sprintf "%A" e)
        }

    let handleChatCompletions (ctx: HttpListenerContext) : Async<unit> =
        async {
            if ctx.Request.HttpMethod <> "POST" then
                do! writeJson (errorJson "Method not allowed" "invalid_request_error") 405 ctx
            else

            // Body size guard
            let contentLen = ctx.Request.ContentLength64
            if contentLen > int64 maxBodyBytes then
                do! writeJson (errorJson (sprintf "Request body too large (max %d MB)" (maxBodyBytes / 1024 / 1024)) "invalid_request_error") 413 ctx
            else

            try
                // Read body
                use ms = new MemoryStream()
                do! ctx.Request.InputStream.CopyToAsync(ms) |> Async.AwaitTask
                let bodyBytes = ms.ToArray()
                if bodyBytes.Length > maxBodyBytes then
                    do! writeJson (errorJson "Request body too large" "invalid_request_error") 413 ctx
                else

                match parseRequest bodyBytes with
                | Result.Error msg ->
                    do! writeJson (errorJson msg "invalid_request_error") 400 ctx
                | Result.Ok req ->
                    let lock = getLock req.SessionId
                    let! acquired = lock.WaitAsync(timeoutMs) |> Async.AwaitTask
                    if not acquired then
                        let secs = timeoutMs / 1000
                        do! writeJson (errorJson (sprintf "Request timed out after %ds" secs) "server_error") 504 ctx
                    else
                    try
                        let! agentResult = routeToAgent req.SessionId req.Text
                        match agentResult with
                        | Result.Error e ->
                            do! writeJson (errorJson e "server_error") 500 ctx
                        | Result.Ok content ->
                            if req.Stream then
                                // SSE streaming path — single content chunk then [DONE].
                                // The response is buffered internally (NoStreaming coordinator),
                                // so latency matches the non-streaming path. This satisfies
                                // clients that require stream=true without real token streaming.
                                addCorsHeaders ctx.Response
                                ctx.Response.StatusCode  <- 200
                                ctx.Response.ContentType <- "text/event-stream"
                                ctx.Response.Headers.["Cache-Control"] <- "no-cache"
                                ctx.Response.Headers.["Connection"]    <- "keep-alive"
                                let chunkId = makeId()
                                let chunk   = sseChunk content modelName chunkId None
                                let fin     = sseChunk "" modelName chunkId (Some "stop")
                                try
                                    do! ctx.Response.OutputStream.WriteAsync(chunk, 0, chunk.Length) |> Async.AwaitTask
                                    do! ctx.Response.OutputStream.WriteAsync(fin,   0, fin.Length)   |> Async.AwaitTask
                                    do! ctx.Response.OutputStream.WriteAsync(sseDone, 0, sseDone.Length) |> Async.AwaitTask
                                with _ -> ()
                                ctx.Response.Close()
                            else
                                do! writeJson (completionJson content modelName) 200 ctx
                    finally
                        lock.Release() |> ignore
            with ex ->
                try do! writeJson (errorJson ex.Message "server_error") 500 ctx
                with _ -> ctx.Response.Abort()
        }

    let handleRequest (ctx: HttpListenerContext) : Async<unit> =
        async {
            let path = (Unchecked.nonNull ctx.Request.Url).AbsolutePath.TrimEnd('/')
            try
                match ctx.Request.HttpMethod, path with
                | "OPTIONS", _ ->
                    // CORS preflight
                    addCorsHeaders ctx.Response
                    ctx.Response.StatusCode <- 204
                    ctx.Response.Close()

                | _, "/v1/chat/completions" ->
                    do! handleChatCompletions ctx

                | "GET", "/v1/models" ->
                    do! writeJson (modelsJson modelName) 200 ctx

                | "GET", "/health" ->
                    do! writeJson """{"status":"ok"}""" 200 ctx

                | _ ->
                    do! writeJson (errorJson "Not found" "invalid_request_error") 404 ctx
            with ex ->
                try do! writeJson (errorJson ex.Message "server_error") 500 ctx
                with _ -> ctx.Response.Abort()
        }

    /// Start listening on the given host and port. Blocks until the listener is stopped.
    /// Pass host as "*" or "+" to bind all interfaces.
    member _.Start(port: int, ?host: string) : Async<unit> =
        async {
            let h = defaultArg host "localhost"
            let prefix = sprintf "http://%s:%d/" h port
            listener.Prefixes.Add(prefix)
            listener.Start()
            eprintfn "[API] Listening on http://%s:%d" h port
            eprintfn "[API]   POST /v1/chat/completions   OpenAI-compatible chat endpoint"
            eprintfn "[API]   GET  /v1/models             List available models"
            eprintfn "[API]   GET  /health                Health check"
            let rec loop () = async {
                try
                    let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                    // Fire-and-forget: each request runs concurrently.
                    Async.Start(handleRequest ctx)
                    return! loop ()
                with :? HttpListenerException ->
                    ()   // listener stopped — exit loop
            }
            return! loop ()
        }

    /// Stop the HTTP listener (releases the blocking Start call).
    member _.Stop() =
        try listener.Stop() with _ -> ()
        try listener.Close() with _ -> ()
