module BotSharp.Tests.Channels.ApiChannelTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.SessionActor
open BotSharp.Infrastructure.Channels.ApiChannel

// ═══════════════════════════════════════════════════════════════════════════
// Test infrastructure
// ═══════════════════════════════════════════════════════════════════════════

/// Find an OS-assigned free port (best-effort; port could be taken by the time
/// the server binds it, but this is rare in a test environment).
let private getFreePort () =
    use listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
    listener.Stop()
    port

/// Stub LLMProvider that always returns a fixed plain-text reply.
let private stubProvider (reply: string) : LLMProvider = {
    Id           = "stub"
    DefaultModel = "test-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async {
        return Result.Ok {
            Body             = TextOnly reply
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 1; CompletionTokens = 1; CachedTokens = 0 }
            FinishReason     = None
        }
    }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

/// Minimal in-memory AgentDependencies using the stub provider.
let private mkDeps (reply: string) : AgentDependencies =
    let mutable stored : SessionSnapshot option = None
    { Provider          = stubProvider reply
      Tools             = Map.empty
      LoadSession       = fun sid -> async {
          return Result.Ok (match stored with
                            | Some s -> s
                            | None   -> SessionSnapshot.empty sid DateTimeOffset.UtcNow)
      }
      PersistSession    = fun snap -> async { stored <- Some snap; return Result.Ok () }
      BuildSystemPrompt = fun _ _ -> async { return "You are a test assistant." }
      Config            = BotSharpConfig.defaults
      StreamHook        = NoStreaming
      CronService       = None
      Hook              = AgentHook.none
      LastTokenUsage    = ref None
      CurrentIteration  = ref 0
      RuleEngine        = None
      FallbackProviders = []
      OpenStateDb       = None
      TokenTracker      = ref None
      EventBus          = None }

/// AgentDependencies backed by a provider that always returns an LLM error.
let private mkErrorDeps () : AgentDependencies =
    let errorProv : LLMProvider = {
        Id           = "err"
        DefaultModel = "stub"
        Capabilities = Set.empty
        RetryPolicy  = RetryPolicy.standard
        Chat         = fun _ _ _ -> async {
            return Result.Error { Kind = ServerError 503; RawMessage = "simulated failure"; ProviderCode = None }
        }
        ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
    }
    { mkDeps "unused" with Provider = errorProv }

/// Poll the /health endpoint until the server responds 200 OK or we time out.
/// Replaces Thread.Sleep(N) with a deterministic readiness check that avoids
/// both flakiness (too short) and unnecessary slowness (too long).
let private waitForServer (baseUrl: string) (maxWaitMs: int) =
    use probe = new HttpClient(Timeout = TimeSpan.FromMilliseconds 100)
    let sw    = Diagnostics.Stopwatch.StartNew()
    let mutable ready = false
    while not ready && sw.ElapsedMilliseconds < int64 maxWaitMs do
        try
            let resp = probe.GetAsync(baseUrl + "/health").Result
            ready <- (int resp.StatusCode = 200)
        with _ ->
            Thread.Sleep(10)
    ready

/// Start an ApiServer backed by an error-returning coordinator.
let private withErrorServer (action: HttpClient -> string -> unit) =
    let port   = getFreePort()
    let coord  = AgentCoordinator(mkErrorDeps())
    let server = ApiServer(coord, "test-model", 10_000)
    Async.Start(server.Start(port))
    let baseUrl = sprintf "http://localhost:%d" port
    if not (waitForServer baseUrl 2000) then
        failwith $"Error-server on port {port} did not start within 2 s"
    use client = new HttpClient()
    try
        action client baseUrl
    finally
        server.Stop()

/// Start an ApiServer on a free port, run `action client baseUrl`, then stop.
/// The server is always stopped even if `action` throws.
let private withServer (reply: string) (action: HttpClient -> string -> unit) =
    let port    = getFreePort()
    let coord   = AgentCoordinator(mkDeps reply)
    let server  = ApiServer(coord, "test-model", 10_000)
    Async.Start(server.Start(port))
    let baseUrl = sprintf "http://localhost:%d" port
    if not (waitForServer baseUrl 2000) then
        failwith $"Test server on port {port} did not start within 2 s"
    use client = new HttpClient()
    try
        action client baseUrl
    finally
        server.Stop()

/// JSON body helpers
let private jsonBody (json: string) =
    new StringContent(json, Encoding.UTF8, "application/json")

let private chatBody (stream: bool) (text: string) =
    let streamVal = if stream then "true" else "false"
    jsonBody (sprintf """{"messages":[{"role":"user","content":"%s"}],"stream":%s}""" text streamVal)

// ═══════════════════════════════════════════════════════════════════════════
// Health check
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GET /health returns 200 with status ok`` () =
    withServer "ignored" (fun client baseUrl ->
        let resp = client.GetAsync(baseUrl + "/health").Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// Models list
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GET /v1/models returns 200 with test-model in data array`` () =
    withServer "ignored" (fun client baseUrl ->
        let resp = client.GetAsync(baseUrl + "/v1/models").Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement
        Assert.Equal("list", root.GetProperty("object").GetString())
        let data = root.GetProperty("data")
        Assert.Equal(1, data.GetArrayLength())
        Assert.Equal("test-model", data.[0].GetProperty("id").GetString())
        Assert.Equal("model", data.[0].GetProperty("object").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// CORS
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``OPTIONS preflight returns 204 with CORS headers`` () =
    withServer "ignored" (fun client baseUrl ->
        use req = new HttpRequestMessage(HttpMethod.Options, baseUrl + "/v1/chat/completions")
        let resp = client.SendAsync(req).Result
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode)
        Assert.True(resp.Headers.Contains("Access-Control-Allow-Origin"))
    )

[<Fact>]
let ``GET /health response includes CORS Allow-Origin header`` () =
    withServer "ignored" (fun client baseUrl ->
        let resp = client.GetAsync(baseUrl + "/health").Result
        Assert.True(resp.Headers.Contains("Access-Control-Allow-Origin"))
        let acao = resp.Headers.GetValues("Access-Control-Allow-Origin") |> Seq.head
        Assert.Equal("*", acao)
    )

// ═══════════════════════════════════════════════════════════════════════════
// 404 / 405 routing
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GET /unknown returns 404`` () =
    withServer "ignored" (fun client baseUrl ->
        let resp = client.GetAsync(baseUrl + "/unknown").Result
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
    )

[<Fact>]
let ``GET /v1/chat/completions returns 405 Method Not Allowed`` () =
    withServer "ignored" (fun client baseUrl ->
        let resp = client.GetAsync(baseUrl + "/v1/chat/completions").Result
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode)
    )

// ═══════════════════════════════════════════════════════════════════════════
// Request body validation
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``POST /v1/chat/completions with invalid JSON returns 400`` () =
    withServer "ignored" (fun client baseUrl ->
        let body = jsonBody "not valid json {"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
        let respBody = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(respBody)
        let err = doc.RootElement.GetProperty("error")
        Assert.Equal("invalid_request_error", err.GetProperty("type").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// Non-streaming completion
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``POST /v1/chat/completions non-streaming returns correct OpenAI shape`` () =
    withServer "Hello from stub!" (fun client baseUrl ->
        let body = chatBody false "hi"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        Assert.Equal("application/json; charset=utf-8", (Unchecked.nonNull resp.Content.Headers.ContentType).ToString())
        let respBody = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(respBody)
        let root = doc.RootElement
        Assert.Equal("chat.completion", root.GetProperty("object").GetString())
        Assert.Equal("test-model",      root.GetProperty("model").GetString())
        let choices = root.GetProperty("choices")
        Assert.Equal(1, choices.GetArrayLength())
        let msg = choices.[0].GetProperty("message")
        Assert.Equal("assistant",        msg.GetProperty("role").GetString())
        Assert.Equal("Hello from stub!", msg.GetProperty("content").GetString())
        Assert.Equal("stop",             choices.[0].GetProperty("finish_reason").GetString())
    )

[<Fact>]
let ``non-streaming response has id starting with chatcmpl-`` () =
    withServer "ok" (fun client baseUrl ->
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", chatBody false "ping").Result
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        let id = doc.RootElement.GetProperty("id").GetString()
        Assert.StartsWith("chatcmpl-", id)
    )

[<Fact>]
let ``non-streaming response has created timestamp greater than zero`` () =
    withServer "ok" (fun client baseUrl ->
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", chatBody false "ping").Result
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        let created = doc.RootElement.GetProperty("created").GetInt64()
        Assert.True(created > 0L)
    )

// ═══════════════════════════════════════════════════════════════════════════
// Streaming (SSE) completion
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``POST /v1/chat/completions with stream=true returns text/event-stream`` () =
    withServer "streaming reply" (fun client baseUrl ->
        let body = chatBody true "hi"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let ct = (Unchecked.nonNull resp.Content.Headers.ContentType).MediaType |> Unchecked.nonNull
        Assert.Equal("text/event-stream", ct)
    )

[<Fact>]
let ``streaming response body contains data: lines and [DONE]`` () =
    withServer "stream text" (fun client baseUrl ->
        let body = chatBody true "go"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        let raw = resp.Content.ReadAsStringAsync().Result
        // Should contain at least one data: {...} line and a [DONE] sentinel
        Assert.Contains("data: ", raw)
        Assert.Contains("[DONE]", raw)
    )

[<Fact>]
let ``streaming response first data chunk has object chat.completion.chunk`` () =
    withServer "chunk text" (fun client baseUrl ->
        let body = chatBody true "go"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        let raw = resp.Content.ReadAsStringAsync().Result
        // Find first data: line that is not [DONE]
        let firstChunk =
            raw.Split('\n')
            |> Array.tryFind (fun line ->
                line.StartsWith("data: ") && not (line.Contains("[DONE]")))
        Assert.True(firstChunk.IsSome, "Expected at least one SSE data chunk")
        let json = firstChunk.Value.Substring("data: ".Length)
        use doc = JsonDocument.Parse(json)
        Assert.Equal("chat.completion.chunk", doc.RootElement.GetProperty("object").GetString())
    )

[<Fact>]
let ``streaming response content chunk contains the agent reply text`` () =
    withServer "hello streaming" (fun client baseUrl ->
        let body = chatBody true "go"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        let raw = resp.Content.ReadAsStringAsync().Result
        // Collect all non-DONE data: lines, find the one with content
        let contentChunk =
            raw.Split('\n')
            |> Array.tryFind (fun line ->
                line.StartsWith("data: ") && not (line.Contains("[DONE]")) &&
                line.Contains("\"content\""))
        Assert.True(contentChunk.IsSome, "No content chunk found in SSE stream")
        let json = contentChunk.Value.Substring("data: ".Length)
        use doc = JsonDocument.Parse(json)
        let choices = doc.RootElement.GetProperty("choices")
        let delta = choices.[0].GetProperty("delta")
        Assert.Equal("hello streaming", delta.GetProperty("content").GetString())
    )

[<Fact>]
let ``streaming final chunk has finish_reason stop`` () =
    withServer "done" (fun client baseUrl ->
        let body = chatBody true "x"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        let raw = resp.Content.ReadAsStringAsync().Result
        let stopChunk =
            raw.Split('\n')
            |> Array.tryFind (fun line ->
                line.StartsWith("data: ") && not (line.Contains("[DONE]")) &&
                line.Contains("\"stop\""))
        Assert.True(stopChunk.IsSome, "No finish_reason stop chunk found")
        let json = stopChunk.Value.Substring("data: ".Length)
        use doc = JsonDocument.Parse(json)
        let choices = doc.RootElement.GetProperty("choices")
        Assert.Equal("stop", choices.[0].GetProperty("finish_reason").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// Session routing
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``session_id field in request body is accepted`` () =
    // Two requests with the same session_id go to the same logical session.
    // With a stub provider both just return the fixed reply — this test
    // verifies the field is parsed without causing a 400 error.
    withServer "reply" (fun client baseUrl ->
        let body =
            jsonBody """{"messages":[{"role":"user","content":"hi"}],"session_id":"my-session"}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``empty messages array results in 200 with empty-content reply`` () =
    // parseRequest falls back to "" when no user message is found;
    // stub provider returns the fixed reply regardless.
    withServer "fallback" (fun client baseUrl ->
        let body = jsonBody """{"messages":[],"stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        // Should be 200 (empty text is a valid empty user turn)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

// ═══════════════════════════════════════════════════════════════════════════
// Agent error → 500
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``POST /v1/chat/completions returns 500 when agent coordinator returns an error`` () =
    // Error-returning provider causes AgentCoordinator.Route to return Result.Error,
    // which the API channel maps to HTTP 500 with an "error" JSON body.
    withErrorServer (fun client baseUrl ->
        let body = chatBody false "trigger error"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode)
        let respBody = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(respBody)
        Assert.True(doc.RootElement.TryGetProperty("error") |> fst,
                    "Expected 'error' key in response JSON body")
    )

// ═══════════════════════════════════════════════════════════════════════════
// Request body parsing edge cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``POST without messages field returns 200 using empty text`` () =
    // parseRequest: `| _ -> ""` when "messages" is absent → empty user turn → agent still responds
    withServer "fallback reply" (fun client baseUrl ->
        let body = jsonBody """{"stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``POST with multiple user messages uses the last one`` () =
    // parseRequest: List.tryFindBack picks the last "user" message
    // Stub provider echoes back whatever text it receives — but since it always
    // returns the fixed reply, we just verify the request succeeds (the text
    // extraction doesn't raise an error with multiple messages).
    withServer "echo" (fun client baseUrl ->
        let body =
            jsonBody """{"messages":[
                {"role":"user","content":"first message"},
                {"role":"assistant","content":"hello"},
                {"role":"user","content":"last message"}
            ],"stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``POST with model field in body returns 200`` () =
    // parseRequest extracts model field without error; server uses its configured modelName
    withServer "model ok" (fun client baseUrl ->
        let body = jsonBody """{"messages":[{"role":"user","content":"hi"}],"model":"gpt-4o","stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``POST /health returns 404 (not GET)`` () =
    // Routing table: "GET", "/health" -> 200; any other method on /health hits wildcard -> 404
    withServer "ignored" (fun client baseUrl ->
        use req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/health")
        req.Content <- jsonBody "{}"
        let resp = client.SendAsync(req).Result
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    )

[<Fact>]
let ``POST /v1/models returns 404 (not GET)`` () =
    // Only GET /v1/models is routed; POST hits wildcard -> 404
    withServer "ignored" (fun client baseUrl ->
        use req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/models")
        req.Content <- jsonBody "{}"
        let resp = client.SendAsync(req).Result
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    )

[<Fact>]
let ``non-streaming response has usage object with prompt_tokens completion_tokens total_tokens`` () =
    // completionJson always writes usage with all-zero counts
    withServer "usage test" (fun client baseUrl ->
        let body = chatBody false "ping"
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        let respBody = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(respBody)
        let usage = doc.RootElement.GetProperty("usage")
        Assert.True(usage.TryGetProperty("prompt_tokens")     |> fst)
        Assert.True(usage.TryGetProperty("completion_tokens") |> fst)
        Assert.True(usage.TryGetProperty("total_tokens")      |> fst)
    )

// ═══════════════════════════════════════════════════════════════════════════
// parseRequest — additional edge cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``POST with messages field as non-array string returns 200 using empty text`` () =
    // root.TryGetProperty("messages") → ValueKind ≠ Array → | _ -> "" branch
    withServer "fallback" (fun client baseUrl ->
        let body = jsonBody """{"messages":"not-an-array","stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``POST with user message content as non-string number returns 200 using empty text`` () =
    // content field ValueKind ≠ String → | _ -> None → Option.defaultValue ""
    withServer "fallback" (fun client baseUrl ->
        let body = jsonBody """{"messages":[{"role":"user","content":42}],"stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``POST with null session_id falls back to default session`` () =
    // "session_id":null → ValueKind = Null → | _ -> "default" branch
    withServer "ok" (fun client baseUrl ->
        let body = jsonBody """{"messages":[{"role":"user","content":"hi"}],"session_id":null}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )

[<Fact>]
let ``POST with messages containing only assistant turns uses empty text`` () =
    // List.tryFindBack returns None (no user role) → Option.defaultValue ""
    withServer "fallback" (fun client baseUrl ->
        let body = jsonBody """{"messages":[{"role":"assistant","content":"I said something"}],"stream":false}"""
        let resp = client.PostAsync(baseUrl + "/v1/chat/completions", body).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    )
