module BotSharp.Infrastructure.Channels.DingTalkChannel

#nowarn "3261" // Nullness interop — C# libs return 'string | null' consumed as 'string'

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// DingTalk/DingDing channel using webhook + REST API
//
// No external SDK — uses standard .NET HttpClient + HttpListener.
// Receives messages via HTTP webhook callback.
// Sends replies via DingTalk REST API.
//
// API:
//   POST /v1.0/oauth2/accessToken → get token
//   POST /v1.0/robot/oToMessages/batchSend → send message
//
// Config:
//   "dingtalk": {
//     "client_id": "xxx",
//     "client_secret": "xxx",
//     "allow_from": ["*"],
//     "webhook_port": 19801
//   }
// ═══════════════════════════════════════════════════════════════════════════

let private dingtalkApiBase = "https://api.dingtalk.com"

// ── Configuration ────────────────────────────────────────────────────────

type DingTalkConfig = {
    ClientId     : string
    ClientSecret : string
    AllowFrom    : AllowList
    WebhookPort  : int
}

// ── Token management ─────────────────────────────────────────────────────

type private TokenState = {
    mutable Token     : string
    mutable ExpiresAt : DateTimeOffset
}

let private refreshToken (httpClient: HttpClient) (clientId: string) (clientSecret: string) (state: TokenState) : Async<string> =
    async {
        if state.Token <> "" && DateTimeOffset.UtcNow < state.ExpiresAt then
            return state.Token
        else
            let body = $"""{{"appKey":"{clientId}","appSecret":"{clientSecret}"}}"""
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            try
                let! resp = httpClient.PostAsync($"{dingtalkApiBase}/v1.0/oauth2/accessToken", content) |> Async.AwaitTask
                let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                use doc = JsonDocument.Parse(respBody)
                let root = doc.RootElement
                match root.TryGetProperty("accessToken") with
                | true, token ->
                    let expire = match root.TryGetProperty("expireIn") with true, e -> e.GetInt32() | _ -> 7200
                    state.Token <- token.GetString()
                    state.ExpiresAt <- DateTimeOffset.UtcNow.AddSeconds(float (expire - 60))
                    eprintfn "[DingTalk] Token refreshed (expires in %ds)" expire
                    return state.Token
                | _ ->
                    eprintfn "[DingTalk] Token refresh failed"
                    return state.Token
            with ex ->
                eprintfn "[DingTalk] Token refresh error: %s" ex.Message
                return state.Token
    }

// ── Server ───────────────────────────────────────────────────────────────

type DingTalkServer(coordinator: AgentCoordinator, config: DingTalkConfig, httpClient: HttpClient) =
    let listener = new HttpListener()
    let tokenState = { Token = ""; ExpiresAt = DateTimeOffset.MinValue }

    let getToken () = refreshToken httpClient config.ClientId config.ClientSecret tokenState

    let sendMessage (userId: string) (text: string) : Async<unit> =
        async {
            let! token = getToken ()
            let escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            let body = $"""{{"robotCode":"{config.ClientId}","userIds":["{userId}"],"msgKey":"sampleText","msgParam":"{{\\"content\\":\\"{escapedText}\\"}}"}}"""
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            let req = new HttpRequestMessage(HttpMethod.Post, $"{dingtalkApiBase}/v1.0/robot/oToMessages/batchSend")
            req.Headers.Add("x-acs-dingtalk-access-token", token)
            req.Content <- content
            try
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                if not resp.IsSuccessStatusCode then
                    let! errBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    eprintfn "[DingTalk] Send failed (%d): %s" (int resp.StatusCode) (if errBody.Length > 200 then errBody.[..199] else errBody)
            with ex ->
                eprintfn "[DingTalk] Send error: %s" ex.Message
        }

    let handleWebhook (body: string) : Async<string> =
        async {
            try
                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement

                let getString (name: string) (el: JsonElement) =
                    match el.TryGetProperty(name) with
                    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
                    | _ -> None

                let msgType = getString "msgtype" root |> Option.defaultValue ""
                let senderId = getString "senderStaffId" root |> Option.defaultValue (getString "senderId" root |> Option.defaultValue "")
                let conversationId = getString "conversationId" root |> Option.defaultValue ""
                let isGroup = match root.TryGetProperty("conversationType") with true, v -> v.GetString() = "2" | _ -> false

                let text =
                    if msgType = "text" then
                        match root.TryGetProperty("text") with
                        | true, textObj -> getString "content" textObj |> Option.defaultValue ""
                        | _ -> ""
                    else $"[{msgType}]"

                if String.IsNullOrWhiteSpace text || senderId = "" then
                    return """{"status":"ok"}"""
                else

                if not (AllowList.permits (UserId senderId) config.AllowFrom) then
                    return """{"status":"ok"}"""
                else

                let chatId = if isGroup then conversationId else senderId

                Async.Start(async {
                    let inbound : InboundMessage = {
                        Channel            = ChannelId "dingtalk"
                        Sender             = UserId senderId
                        Chat               = ChatId chatId
                        Input              = BotSharp.Infrastructure.Channels.ChannelBase.parseInput (text.Trim())
                        Metadata           = Map.ofList [ "msg_type", msgType; "is_group", string isGroup ]
                        SessionKeyOverride = None
                    }
                    let! result = coordinator.Route inbound
                    match result with
                    | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                        do! sendMessage senderId t
                    | Result.Error e ->
                        do! sendMessage senderId $"Error: {e}"
                    | _ -> ()
                })

                return """{"status":"ok"}"""
            with ex ->
                eprintfn "[DingTalk] Webhook error: %s" ex.Message
                return """{"error":"internal"}"""
        }

    let handleRequest (ctx: HttpListenerContext) : Async<unit> =
        async {
            let path = ctx.Request.Url |> Option.ofObj |> Option.map (fun u -> u.AbsolutePath) |> Option.defaultValue ""
            let method = ctx.Request.HttpMethod.ToUpperInvariant()
            try
                match method, path with
                | "POST", "/dingtalk/webhook" | "POST", "/dingtalk/event" ->
                    use reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)
                    let! body = reader.ReadToEndAsync() |> Async.AwaitTask
                    let! response = handleWebhook body
                    let bytes = Encoding.UTF8.GetBytes(response)
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    ctx.Response.Close()
                | "GET", "/dingtalk/health" ->
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
                eprintfn "[DingTalk] Request error: %s" ex.Message
                try ctx.Response.Close() with _ -> ()
        }

    member _.Start() : Async<unit> =
        async {
            let! _ = getToken ()

            let prefix = $"http://localhost:{config.WebhookPort}/"
            listener.Prefixes.Add(prefix)
            listener.Start()
            printfn "[DingTalk] Webhook server listening on http://localhost:%d" config.WebhookPort
            printfn "[DingTalk]   POST /dingtalk/webhook    Message callback"
            printfn "[DingTalk]   GET  /dingtalk/health     Health check"

            try
                while listener.IsListening do
                    let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                    Async.Start(handleRequest ctx)
            with
            | :? ObjectDisposedException -> ()
            | :? HttpListenerException -> ()
        }

    member _.Stop() =
        if listener.IsListening then
            listener.Stop()
            listener.Close()
