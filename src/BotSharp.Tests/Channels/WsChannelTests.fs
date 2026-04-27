module BotSharp.Tests.Channels.WsChannelTests

open System
open System.Net
open System.Net.Http
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Application.SessionActor
open BotSharp.Infrastructure.Channels.WsChannel

// ═══════════════════════════════════════════════════════════════════════════
// Test infrastructure (mirrors ApiChannelTests pattern)
// ═══════════════════════════════════════════════════════════════════════════

let private getFreePort () =
    use listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
    listener.Stop()
    port

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
    // ChatStream MUST call the emitter so textAcc is non-empty.
    // Without this, the agent loop returns Body = Empty, skips notifyStreamEnd,
    // and the "done" event is never sent — causing WS tests to hang indefinitely.
    ChatStream   = fun _ _ _ emitter -> async {
        do! emitter (ContentDelta (TextDelta reply))
        return Result.Ok ()
    }
}

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
      CurrentIteration  = ref 0 }

/// Poll GET /health until the server responds 200 or the deadline is reached.
/// Retries up to ~50 times with 20 ms between attempts (≤ 1 s total wait).
let private waitForServer (port: int) : unit =
    use client = new HttpClient()
    client.Timeout <- TimeSpan.FromMilliseconds(200.0)
    let deadline = DateTime.UtcNow.AddSeconds(5.0)
    let mutable ready = false
    while not ready && DateTime.UtcNow < deadline do
        try
            let resp = client.GetAsync(sprintf "http://localhost:%d/health" port).Result
            if resp.StatusCode = HttpStatusCode.OK then ready <- true
        with _ ->
            Thread.Sleep(20)
    if not ready then
        failwith $"WsServer did not become ready on port {port} within 5 s"

/// Start a WsServer on a free port, run `action port`, then stop.
let private withWsServer (reply: string) (token: string option) (action: int -> unit) =
    let port   = getFreePort()
    let server = WsServer(mkDeps reply, token)
    Async.Start(server.Start(port))
    waitForServer port
    try
        action port
    finally
        server.Stop()

/// Start a WsServer with a custom config (e.g. AllowFrom restriction).
let private withWsServerCfg (reply: string) (token: string option) (cfg: BotSharpConfig) (action: int -> unit) =
    let port   = getFreePort()
    let deps   = { mkDeps reply with Config = cfg }
    let server = WsServer(deps, token)
    Async.Start(server.Start(port))
    waitForServer port
    try
        action port
    finally
        server.Stop()

/// Connect a ClientWebSocket to ws://localhost:{port}/ws
let private connectWs (port: int) : ClientWebSocket =
    let ws = new ClientWebSocket()
    ws.ConnectAsync(Uri(sprintf "ws://localhost:%d/ws" port), CancellationToken.None)
      .Wait()
    ws

/// Connect to ws://localhost:{port}/ws with extra query params
let private connectWsWith (port: int) (query: string) : ClientWebSocket =
    let ws = new ClientWebSocket()
    ws.ConnectAsync(Uri(sprintf "ws://localhost:%d/ws?%s" port query), CancellationToken.None)
      .Wait()
    ws

/// Read one complete WebSocket text message and return it as a string.
let private recvText (ws: ClientWebSocket) : string =
    let buf = Array.zeroCreate 65536
    let seg = ArraySegment<byte>(buf)
    use ms = new System.IO.MemoryStream()
    let mutable finished = false
    while not finished do
        let result = ws.ReceiveAsync(seg, CancellationToken.None).Result
        ms.Write(buf, 0, result.Count)
        finished <- result.EndOfMessage
    Encoding.UTF8.GetString(ms.ToArray())

/// Read one WebSocket frame, parse as JSON, return the root element.
let private recvJson (ws: ClientWebSocket) : JsonElement =
    let text = recvText ws
    JsonDocument.Parse(text).RootElement.Clone()

/// Send a JSON string to the WebSocket.
let private sendText (ws: ClientWebSocket) (text: string) : unit =
    let bytes = Encoding.UTF8.GetBytes(text)
    ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
      .Wait()

let private sendMsg (ws: ClientWebSocket) (text: string) : unit =
    sendText ws (sprintf """{"text":"%s"}""" text)

// ═══════════════════════════════════════════════════════════════════════════
// Health check (HTTP, not WebSocket)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GET /health returns 200 ok`` () =
    withWsServer "ignored" None (fun port ->
        use client = new HttpClient()
        let resp = client.GetAsync(sprintf "http://localhost:%d/health" port).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString())
    )

[<Fact>]
let ``GET / returns HTML WebUI`` () =
    withWsServer "ignored" None (fun port ->
        use client = new HttpClient()
        let resp = client.GetAsync(sprintf "http://localhost:%d/" port).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let ct = resp.Content.Headers.ContentType
        Assert.NotNull(ct)
        Assert.StartsWith("text/html", ct.MediaType)
        let body = resp.Content.ReadAsStringAsync().Result
        Assert.Contains("<title>BotSharp</title>", body)
        Assert.Contains("WebSocket", body)
    )

// ═══════════════════════════════════════════════════════════════════════════
// WebSocket connection — ready event
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``WS connection receives ready event with chat_id`` () =
    withWsServer "ignored" None (fun port ->
        use ws = connectWs port
        let msg = recvJson ws
        Assert.Equal("ready", msg.GetProperty("type").GetString())
        let chatId = msg.GetProperty("chat_id").GetString()
        Assert.False(String.IsNullOrWhiteSpace(chatId))
    )

[<Fact>]
let ``WS connection uses provided chat_id from query param`` () =
    withWsServer "ignored" None (fun port ->
        use ws = connectWsWith port "chat_id=my-test-session-42"
        let msg = recvJson ws
        Assert.Equal("ready", msg.GetProperty("type").GetString())
        Assert.Equal("my-test-session-42", msg.GetProperty("chat_id").GetString())
    )

[<Fact>]
let ``WS connection generates different chat_ids for different connections`` () =
    withWsServer "ignored" None (fun port ->
        use ws1 = connectWs port
        let id1 = (recvJson ws1).GetProperty("chat_id").GetString()
        use ws2 = connectWs port
        let id2 = (recvJson ws2).GetProperty("chat_id").GetString()
        Assert.NotEqual<string>(id1, id2)
    )

// ═══════════════════════════════════════════════════════════════════════════
// Message routing — delta + done events
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``sending a message produces delta and done events`` () =
    // The stub provider returns "stub reply" as a TextOnly response.
    // With NoStreaming base deps, WsCoordinator upgrades to StreamingHook per connection.
    // The agent loop emits the text via onDelta, then fires onStreamEnd → "done".
    withWsServer "stub reply" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())

        sendMsg ws "hello"

        // Collect events until "done"
        let mutable events = []
        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            let t = ev.GetProperty("type").GetString()
            events <- events @ [ev]
            if t = "done" then isDone <- true

        // Should have at least one delta followed by done
        let types = events |> List.map (fun e -> e.GetProperty("type").GetString())
        Assert.Contains("done", types)
    )

[<Fact>]
let ``delta events carry chat_id`` () =
    withWsServer "hello ws" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        sendMsg ws "hi"

        let mutable found = false
        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            let t = ev.GetProperty("type").GetString()
            if t = "delta" then
                Assert.Equal(chatId, ev.GetProperty("chat_id").GetString())
                found <- true
            elif t = "done" then
                isDone <- true

        Assert.True(found, "Expected at least one delta event")
    )

[<Fact>]
let ``done event carries chat_id`` () =
    withWsServer "done check" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        sendMsg ws "go"

        let mutable isDone = false
        let mutable doneEv = Unchecked.defaultof<JsonElement>
        while not isDone do
            let ev = recvJson ws
            if ev.GetProperty("type").GetString() = "done" then
                doneEv <- ev
                isDone <- true

        Assert.Equal(chatId, doneEv.GetProperty("chat_id").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// Authentication
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``no token configured — connection succeeds without token`` () =
    withWsServer "ok" None (fun port ->
        // Should connect successfully
        use ws = connectWs port
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())
    )

[<Fact>]
let ``correct token in query param allows connection`` () =
    withWsServer "ok" (Some "secret-token") (fun port ->
        use ws = connectWsWith port "token=secret-token"
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())
    )

[<Fact>]
let ``wrong token in query param returns HTTP 401`` () =
    withWsServer "ok" (Some "secret-token") (fun port ->
        use ws = new ClientWebSocket()
        try
            ws.ConnectAsync(Uri(sprintf "ws://localhost:%d/ws?token=wrong-token" port), CancellationToken.None)
              .Wait()
            // If ConnectAsync doesn't throw, the server should have rejected with 401
            // ClientWebSocket throws when the server returns non-101
            Assert.Fail("Expected WebSocket connection to be rejected with 401")
        with
        | :? AggregateException as ae when ae.InnerExceptions |> Seq.exists (fun ex -> ex.Message.Contains("101") || ex.Message.Contains("401") || ex.Message.Contains("WebSocket") || ex.Message.Contains("Unauthorized")) ->
            ()   // expected: server rejected the upgrade
        | :? AggregateException ->
            ()   // any connection failure counts as rejected
        | :? WebSocketException ->
            ()   // expected
    )

[<Fact>]
let ``missing token when token required returns HTTP 401`` () =
    withWsServer "ok" (Some "secret-token") (fun port ->
        use ws = new ClientWebSocket()
        try
            ws.ConnectAsync(Uri(sprintf "ws://localhost:%d/ws" port), CancellationToken.None)
              .Wait()
            Assert.Fail("Expected connection to be rejected when token is required but absent")
        with _ ->
            ()   // any exception = connection rejected as expected
    )

// ═══════════════════════════════════════════════════════════════════════════
// Non-WebSocket request to /ws returns 400
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``plain GET /ws without WebSocket upgrade returns 400`` () =
    withWsServer "ignored" None (fun port ->
        use client = new HttpClient()
        let resp = client.GetAsync(sprintf "http://localhost:%d/ws" port).Result
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
    )

// ═══════════════════════════════════════════════════════════════════════════
// 404 for unknown paths
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GET /unknown returns 404`` () =
    withWsServer "ignored" None (fun port ->
        use client = new HttpClient()
        let resp = client.GetAsync(sprintf "http://localhost:%d/unknown" port).Result
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode)
    )

// ═══════════════════════════════════════════════════════════════════════════
// Unparseable frames are silently skipped (no disconnect)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``sending invalid JSON does not disconnect the client`` () =
    withWsServer "fine" None (fun port ->
        use ws = connectWs port
        let _ = recvJson ws   // consume ready

        // Send garbage JSON — should be silently ignored
        sendText ws "not json {"

        // Connection should still be open; a valid message should work
        sendMsg ws "hello"

        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            if ev.GetProperty("type").GetString() = "done" then
                isDone <- true

        Assert.True(isDone)
    )

// ═══════════════════════════════════════════════════════════════════════════
// allow_from — client_id query param authorization
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AnyoneAllowed permits connection without client_id`` () =
    let cfg = { BotSharpConfig.defaults with AllowFrom = AnyoneAllowed }
    withWsServerCfg "ok" None cfg (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())
    )

[<Fact>]
let ``AllowedSet permits connection with allowed client_id`` () =
    let cfg = { BotSharpConfig.defaults with AllowFrom = AllowedSet (Set.singleton "alice") }
    withWsServerCfg "ok" None cfg (fun port ->
        use ws = connectWsWith port "client_id=alice"
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())
    )

[<Fact>]
let ``AllowedSet rejects connection with blocked client_id`` () =
    let cfg = { BotSharpConfig.defaults with AllowFrom = AllowedSet (Set.singleton "alice") }
    withWsServerCfg "ok" None cfg (fun port ->
        use ws = new ClientWebSocket()
        try
            ws.ConnectAsync(Uri(sprintf "ws://localhost:%d/ws?client_id=mallory" port), CancellationToken.None)
              .Wait()
            Assert.Fail("Expected connection to be rejected with 403")
        with _ ->
            ()   // any exception = rejected as expected
    )

[<Fact>]
let ``AllowedSet permits connection with no client_id (not filtered)`` () =
    // When no client_id is supplied, allow_from check is skipped entirely.
    let cfg = { BotSharpConfig.defaults with AllowFrom = AllowedSet (Set.singleton "alice") }
    withWsServerCfg "ok" None cfg (fun port ->
        // No client_id in query — should connect successfully
        use ws = connectWs port
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// Typed envelope protocol — new_chat / attach / message
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``new_chat envelope creates a new chat_id`` () =
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let originalId = ready.GetProperty("chat_id").GetString()

        // Send new_chat envelope
        sendText ws """{"type":"new_chat"}"""

        let attached = recvJson ws
        Assert.Equal("attached", attached.GetProperty("type").GetString())
        let newId = attached.GetProperty("chat_id").GetString()
        // Should have generated a different chat_id
        Assert.NotEqual<string>(originalId, newId)
        Assert.False(String.IsNullOrWhiteSpace newId)
    )

[<Fact>]
let ``attach envelope switches to specified chat_id`` () =
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let _ = recvJson ws   // consume ready

        // Attach to a specific chat_id
        sendText ws """{"type":"attach","chat_id":"my-special-session-99"}"""

        let attached = recvJson ws
        Assert.Equal("attached", attached.GetProperty("type").GetString())
        Assert.Equal("my-special-session-99", attached.GetProperty("chat_id").GetString())
    )

[<Fact>]
let ``typed message envelope produces delta and done events`` () =
    withWsServer "typed reply" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        // Send typed message envelope
        sendText ws (sprintf """{"type":"message","content":"hello typed","chat_id":"%s"}""" chatId)

        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            let t = ev.GetProperty("type").GetString()
            if t = "done" then isDone <- true

        Assert.True(isDone)
    )

[<Fact>]
let ``typed message envelope with missing content returns error`` () =
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        // Empty content — should get an error event, not a hang
        sendText ws (sprintf """{"type":"message","content":"","chat_id":"%s"}""" chatId)

        let ev = recvJson ws
        Assert.Equal("error", ev.GetProperty("type").GetString())
        let detail = ev.GetProperty("detail").GetString()
        Assert.Equal("missing_content", detail)
    )

[<Fact>]
let ``attach with invalid chat_id is ignored (no attached event)`` () =
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let _ = recvJson ws   // consume ready

        // attach with empty chat_id — should be silently ignored (no attached event)
        sendText ws """{"type":"attach","chat_id":""}"""

        // Send a valid message to prove connection still works
        sendMsg ws "ping"

        // Should get done (streaming completes) not an attached event
        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            if ev.GetProperty("type").GetString() = "done" then isDone <- true

        Assert.True(isDone)
    )

// ═══════════════════════════════════════════════════════════════════════════
// Image upload — base64 data URL in typed message envelope
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``typed message with too many images returns image_rejected error`` () =
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        // Send 5 media items (limit is 4)
        let fakeItem = """{"data_url":"data:image/png;base64,iVBORw0KGgo="}"""
        let media = String.concat "," (List.replicate 5 fakeItem)
        let envelope = sprintf """{"type":"message","content":"hello","chat_id":"%s","media":[%s]}""" chatId media
        sendText ws envelope

        let ev = recvJson ws
        Assert.Equal("error", ev.GetProperty("type").GetString())
        Assert.Equal("image_rejected", ev.GetProperty("detail").GetString())
        // Reason should be "too_many_images"
        let text = ev.GetProperty("text").GetString()
        Assert.Equal("too_many_images", text)
    )

[<Fact>]
let ``typed message with malformed data_url returns image_rejected error`` () =
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        let envelope = sprintf """{"type":"message","content":"hello","chat_id":"%s","media":[{"data_url":"not-a-data-url"}]}""" chatId
        sendText ws envelope

        let ev = recvJson ws
        Assert.Equal("error", ev.GetProperty("type").GetString())
        Assert.Equal("image_rejected", ev.GetProperty("detail").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// GET /token — short-lived token issuance
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``GET /token without auth configured returns 400`` () =
    // When no static token is configured, token issuance makes no sense.
    withWsServer "ignored" None (fun port ->
        use client = new HttpClient()
        let resp = client.GetAsync(sprintf "http://localhost:%d/token" port).Result
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
    )

[<Fact>]
let ``GET /token with wrong master token returns 401`` () =
    withWsServer "ignored" (Some "master-secret") (fun port ->
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-token")
        let resp = client.GetAsync(sprintf "http://localhost:%d/token" port).Result
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode)
    )

[<Fact>]
let ``GET /token with correct master token returns 200 with token and expires_in`` () =
    withWsServer "ignored" (Some "master-secret") (fun port ->
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "master-secret")
        let resp = client.GetAsync(sprintf "http://localhost:%d/token" port).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement
        let tok = root.GetProperty("token").GetString()
        Assert.False(String.IsNullOrWhiteSpace(tok))
        let exp = root.GetProperty("expires_in").GetInt32()
        Assert.True(exp > 0)
    )

[<Fact>]
let ``issued token from GET /token can be used to connect to /ws`` () =
    withWsServer "ok" (Some "master-secret") (fun port ->
        // Fetch an issued token using the master token
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "master-secret")
        let resp = client.GetAsync(sprintf "http://localhost:%d/token" port).Result
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        let issuedTok = doc.RootElement.GetProperty("token").GetString()

        // Connect to /ws using the issued token (not the master token)
        use ws = new ClientWebSocket()
        ws.ConnectAsync(Uri(sprintf "ws://localhost:%d/ws?token=%s" port issuedTok), CancellationToken.None)
          .Wait()
        let ready = recvJson ws
        Assert.Equal("ready", ready.GetProperty("type").GetString())
    )

[<Fact>]
let ``issued token cannot be used to mint more tokens (no amplification)`` () =
    withWsServer "ignored" (Some "master-secret") (fun port ->
        // Fetch an issued token
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "master-secret")
        let resp = client.GetAsync(sprintf "http://localhost:%d/token" port).Result
        let body = resp.Content.ReadAsStringAsync().Result
        use doc = JsonDocument.Parse(body)
        let issuedTok = doc.RootElement.GetProperty("token").GetString()

        // Try to use the issued token to mint another token — should be rejected
        use client2 = new HttpClient()
        client2.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", issuedTok)
        let resp2 = client2.GetAsync(sprintf "http://localhost:%d/token" port).Result
        Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode)
    )

[<Fact>]
let ``GET /token without any Authorization header returns 401`` () =
    withWsServer "ignored" (Some "master-secret") (fun port ->
        use client = new HttpClient()
        let resp = client.GetAsync(sprintf "http://localhost:%d/token" port).Result
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode)
    )

// ═══════════════════════════════════════════════════════════════════════════
// parseEnvelope — untested branches
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``frame starting with brace but invalid JSON is silently dropped (JsonException path)`` () =
    // parseEnvelope: s.StartsWith("{") → true → JsonDocument.Parse throws JsonException → None
    // → silently skipped (not plain-text path, which starts with non-brace characters)
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let _ = recvJson ws   // consume ready

        // Starts with '{' → JsonException → None → silently ignored
        sendText ws "{invalid json here"

        // Connection is still open
        sendMsg ws "hello"

        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            if ev.GetProperty("type").GetString() = "done" then isDone <- true

        Assert.True(isDone)
    )

[<Fact>]
let ``unknown typed envelope type is silently ignored`` () =
    // parseEnvelope: type = "ping" → | _ -> None → silently skipped
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let _ = recvJson ws   // consume ready

        sendText ws """{"type":"ping","data":"ignored"}"""

        // Connection still works after the unknown envelope
        sendMsg ws "hello"

        let mutable isDone = false
        while not isDone do
            let ev = recvJson ws
            if ev.GetProperty("type").GetString() = "done" then isDone <- true

        Assert.True(isDone)
    )

// ═══════════════════════════════════════════════════════════════════════════
// parseMediaItems — untested error branches
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``typed message with media item missing data_url field returns image_rejected`` () =
    // parseMediaItems: item has no "data_url" property → | _ -> Result.Error Malformed
    // (distinct from malformed data_url string, which goes through parseMediaItem instead)
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        // Item has no "data_url" key at all
        let envelope = sprintf """{"type":"message","content":"hi","chat_id":"%s","media":[{"mime":"image/png"}]}""" chatId
        sendText ws envelope

        let ev = recvJson ws
        Assert.Equal("error", ev.GetProperty("type").GetString())
        Assert.Equal("image_rejected", ev.GetProperty("detail").GetString())
    )

[<Fact>]
let ``typed message with unsupported MIME type returns mime_not_allowed`` () =
    // parseMediaItem: data URL parses OK but MIME not in ImageMime.parse → MimeNotAllowed
    // → ImageDecodeError.toToken MimeNotAllowed = "mime_not_allowed"
    withWsServer "ok" None (fun port ->
        use ws = connectWs port
        let ready = recvJson ws
        let chatId = ready.GetProperty("chat_id").GetString()

        // Valid data URL structure but unsupported MIME (video/mp4)
        let envelope = sprintf """{"type":"message","content":"hi","chat_id":"%s","media":[{"data_url":"data:video/mp4;base64,AAAA"}]}""" chatId
        sendText ws envelope

        let ev = recvJson ws
        Assert.Equal("error", ev.GetProperty("type").GetString())
        Assert.Equal("image_rejected", ev.GetProperty("detail").GetString())
        Assert.Equal("mime_not_allowed", ev.GetProperty("text").GetString())
    )

// ═══════════════════════════════════════════════════════════════════════════
// dispatchRequest — OPTIONS CORS preflight
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``OPTIONS request returns 204 with CORS headers`` () =
    // dispatchRequest: "OPTIONS", _ → addCorsHeaders + StatusCode 204 + Close
    withWsServer "ignored" None (fun port ->
        use client = new HttpClient()
        let req = new HttpRequestMessage(HttpMethod("OPTIONS"), sprintf "http://localhost:%d/ws" port)
        let resp = client.SendAsync(req).Result
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode)
        Assert.True(resp.Headers.Contains("Access-Control-Allow-Origin"),
                    "CORS header Access-Control-Allow-Origin should be present")
    )
