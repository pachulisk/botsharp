module BotSharp.Infrastructure.Channels.FeishuChannel

#nowarn "3261" // Nullness interop — C# libs return 'string | null' consumed as 'string'

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Feishu/Lark channel using REST API + Long Polling
//
// No external SDK — uses standard .NET HttpClient.
// Uses Feishu's event subscription via HTTP callback (webhook mode).
// For WebSocket mode, start with gateway and configure a webhook URL.
//
// API:
//   POST /open-apis/auth/v3/tenant_access_token/internal → get token
//   POST /open-apis/im/v1/messages?receive_id_type=chat_id → send message
//
// Config:
//   "feishu": {
//     "app_id": "cli_xxx",
//     "app_secret": "xxx",
//     "verification_token": "xxx",
//     "allow_from": ["*"],
//     "webhook_port": 19800
//   }
// ═══════════════════════════════════════════════════════════════════════════

let private feishuApiBase = "https://open.feishu.cn/open-apis"

// ── Configuration ────────────────────────────────────────────────────────

type FeishuConfig = {
    AppId              : string
    AppSecret          : string
    VerificationToken  : string
    AllowFrom          : AllowList
    WebhookPort        : int
}

// ── Token management ─────────────────────────────────────────────────────

type private TokenState = {
    mutable Token     : string
    mutable ExpiresAt : DateTimeOffset
}

let private refreshToken (httpClient: HttpClient) (appId: string) (appSecret: string) (state: TokenState) : Async<string> =
    async {
        if state.Token <> "" && DateTimeOffset.UtcNow < state.ExpiresAt then
            return state.Token
        else
            let body = $"""{{"app_id":"{appId}","app_secret":"{appSecret}"}}"""
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            let! resp = httpClient.PostAsync($"{feishuApiBase}/auth/v3/tenant_access_token/internal", content) |> Async.AwaitTask
            let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            use doc = JsonDocument.Parse(respBody)
            let root = doc.RootElement
            match root.TryGetProperty("tenant_access_token") with
            | true, token ->
                let expire = match root.TryGetProperty("expire") with true, e -> e.GetInt32() | _ -> 7200
                state.Token <- token.GetString()
                state.ExpiresAt <- DateTimeOffset.UtcNow.AddSeconds(float (expire - 300))  // refresh 5 min early
                eprintfn "[Feishu] Token refreshed (expires in %ds)" expire
                return state.Token
            | _ ->
                let msg = match root.TryGetProperty("msg") with true, m -> m.GetString() | _ -> "unknown error"
                eprintfn "[Feishu] Token refresh failed: %s" msg
                return state.Token
    }

// ── Message deduplication ────────────────────────────────────────────────

let private maxDedup = 1000

// ── Server ───────────────────────────────────────────────────────────────

type FeishuServer(coordinator: AgentCoordinator, config: FeishuConfig, httpClient: HttpClient) =
    let listener = new System.Net.HttpListener()
    let tokenState = { Token = ""; ExpiresAt = DateTimeOffset.MinValue }
    let processedIds = ConcurrentDictionary<string, byte>()
    let mutable dedupCount = 0

    let getToken () = refreshToken httpClient config.AppId config.AppSecret tokenState

    let sendFeishuMessage (chatId: string) (text: string) : Async<unit> =
        async {
            let! token = getToken ()
            let receiveIdType = if chatId.StartsWith("oc_") then "chat_id" else "open_id"
            let escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            let body = $"""{{"receive_id":"{chatId}","msg_type":"text","content":"{{\\"text\\":\\"{escapedText}\\"}}"}}"""
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            let req = new HttpRequestMessage(HttpMethod.Post, $"{feishuApiBase}/im/v1/messages?receive_id_type={receiveIdType}")
            req.Headers.Add("Authorization", $"Bearer {token}")
            req.Content <- content
            try
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                use doc = JsonDocument.Parse(respBody)
                let code = match doc.RootElement.TryGetProperty("code") with true, c -> c.GetInt32() | _ -> -1
                if code <> 0 then
                    let msg = match doc.RootElement.TryGetProperty("msg") with true, m -> m.GetString() | _ -> ""
                    eprintfn "[Feishu] Send failed (code %d): %s" code msg
            with ex ->
                eprintfn "[Feishu] Send error: %s" ex.Message
        }

    let handleEvent (body: string) : Async<string> =
        async {
            try
                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement

                // URL verification challenge
                match root.TryGetProperty("challenge") with
                | true, challenge ->
                    return $"""{{"challenge":"{challenge.GetString()}"}}"""
                | _ ->

                // Verification token check
                match root.TryGetProperty("token") with
                | true, token when token.GetString() <> config.VerificationToken ->
                    return """{"error":"invalid token"}"""
                | _ ->

                // Event handling
                match root.TryGetProperty("header") with
                | true, header ->
                    let eventType = match header.TryGetProperty("event_type") with true, t -> t.GetString() | _ -> ""
                    let eventId = match header.TryGetProperty("event_id") with true, e -> e.GetString() | _ -> ""

                    // Deduplication
                    if eventId <> "" && not (processedIds.TryAdd(eventId, 0uy)) then
                        return """{"ok":true}"""
                    else
                    dedupCount <- dedupCount + 1
                    if dedupCount > maxDedup then
                        let oldest = processedIds.Keys |> Seq.tryHead
                        oldest |> Option.iter (fun k -> processedIds.TryRemove(k) |> ignore)

                    if eventType <> "im.message.receive_v1" then
                        return """{"ok":true}"""
                    else

                    match root.TryGetProperty("event") with
                    | true, event ->
                        let sender =
                            match event.TryGetProperty("sender") with
                            | true, s ->
                                let senderType = match s.TryGetProperty("sender_type") with true, t -> t.GetString() | _ -> ""
                                let senderId =
                                    match s.TryGetProperty("sender_id") with
                                    | true, sid -> match sid.TryGetProperty("open_id") with true, oid -> oid.GetString() | _ -> ""
                                    | _ -> ""
                                (senderType, senderId)
                            | _ -> ("", "")

                        let senderType, senderId = sender
                        if senderType = "bot" || senderId = "" then
                            return """{"ok":true}"""
                        else

                        let message =
                            match event.TryGetProperty("message") with true, m -> m | _ -> JsonElement()

                        let chatId = match message.TryGetProperty("chat_id") with true, c -> c.GetString() | _ -> ""
                        let chatType = match message.TryGetProperty("chat_type") with true, c -> c.GetString() | _ -> ""
                        let msgType = match message.TryGetProperty("message_type") with true, t -> t.GetString() | _ -> ""
                        let contentStr = match message.TryGetProperty("content") with true, c -> c.GetString() | _ -> "{}"

                        let text =
                            if msgType = "text" then
                                try
                                    use contentDoc = JsonDocument.Parse(contentStr)
                                    match contentDoc.RootElement.TryGetProperty("text") with true, t -> t.GetString() | _ -> ""
                                with _ -> contentStr
                            else $"[{msgType}]"

                        if String.IsNullOrWhiteSpace text then
                            return """{"ok":true}"""
                        else

                        if not (AllowList.permits (UserId senderId) config.AllowFrom) then
                            return """{"ok":true}"""
                        else

                        let replyTo = if chatType = "group" then chatId else senderId

                        // Route to agent
                        Async.Start(async {
                            let inbound : InboundMessage = {
                                Channel            = ChannelId "feishu"
                                Sender             = UserId senderId
                                Chat               = ChatId replyTo
                                Input              = ChatMessage (text, [])
                                Metadata           = Map.ofList [ "chat_type", chatType; "msg_type", msgType ]
                                SessionKeyOverride = None
                            }
                            let! result = coordinator.Route inbound
                            match result with
                            | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                                do! sendFeishuMessage replyTo t
                            | Result.Error e ->
                                do! sendFeishuMessage replyTo $"Error: {e}"
                            | _ -> ()
                        })

                        return """{"ok":true}"""
                    | _ -> return """{"ok":true}"""
                | _ -> return """{"ok":true}"""
            with ex ->
                eprintfn "[Feishu] Event handler error: %s" ex.Message
                return """{"error":"internal"}"""
        }

    let handleRequest (ctx: System.Net.HttpListenerContext) : Async<unit> =
        async {
            let path = ctx.Request.Url |> Option.ofObj |> Option.map (fun u -> u.AbsolutePath) |> Option.defaultValue ""
            let method = ctx.Request.HttpMethod.ToUpperInvariant()
            try
                match method, path with
                | "POST", "/feishu/event" | "POST", "/feishu/webhook" ->
                    use reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)
                    let! body = reader.ReadToEndAsync() |> Async.AwaitTask
                    let! response = handleEvent body
                    let bytes = Encoding.UTF8.GetBytes(response)
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    ctx.Response.Close()
                | "GET", "/feishu/health" ->
                    let json = """{"status":"ok"}"""
                    let bytes = Encoding.UTF8.GetBytes(json)
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    ctx.Response.Close()
                | _ ->
                    ctx.Response.StatusCode <- 404
                    ctx.Response.Close()
            with ex ->
                eprintfn "[Feishu] Request handler error: %s" ex.Message
                try ctx.Response.Close() with _ -> ()
        }

    member _.Start() : Async<unit> =
        async {
            // Verify credentials by getting initial token
            let! _ = getToken ()

            let prefix = $"http://localhost:{config.WebhookPort}/"
            listener.Prefixes.Add(prefix)
            listener.Start()
            printfn "[Feishu] Webhook server listening on http://localhost:%d" config.WebhookPort
            printfn "[Feishu]   POST /feishu/event     Event callback"
            printfn "[Feishu]   GET  /feishu/health     Health check"
            printfn "[Feishu] Configure webhook URL in Feishu console: http://<your-host>:%d/feishu/event" config.WebhookPort

            try
                while listener.IsListening do
                    let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                    Async.Start(handleRequest ctx)
            with
            | :? ObjectDisposedException -> ()
            | :? System.Net.HttpListenerException -> ()
        }

    member _.Stop() =
        if listener.IsListening then
            listener.Stop()
            listener.Close()
