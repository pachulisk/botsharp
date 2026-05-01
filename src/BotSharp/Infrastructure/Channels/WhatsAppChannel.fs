module BotSharp.Infrastructure.Channels.WhatsAppChannel

#nowarn "3261" // Nullness interop — C# libs return 'string | null' consumed as 'string'

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Collections.Generic
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// WhatsApp channel using Meta Cloud API (webhook + REST)
//
// No external SDK or Node.js bridge — uses standard .NET HttpClient.
// Receives messages via webhook callback (requires public URL or tunnel).
// Sends replies via WhatsApp Cloud API.
//
// API:
//   POST https://graph.facebook.com/v21.0/{phone_number_id}/messages
//
// Config:
//   "whatsapp": {
//     "phone_number_id": "xxx",
//     "access_token": "EAA...",
//     "verify_token": "my-verify-token",
//     "webhook_port": 19802,
//     "allow_from": ["*"]
//   }
// ═══════════════════════════════════════════════════════════════════════════

let private graphApiBase = "https://graph.facebook.com/v21.0"

type WhatsAppConfig = {
    PhoneNumberId : string
    AccessToken   : string
    VerifyToken   : string
    WebhookPort   : int
    AllowFrom     : AllowList
}

type WhatsAppServer(coordinator: AgentCoordinator, config: WhatsAppConfig, httpClient: HttpClient) =
    let listener = new HttpListener()
    let processedIds = HashSet<string>()

    let sendMessage (recipientPhone: string) (text: string) : Async<unit> =
        async {
            let escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            let body = $"""{{"messaging_product":"whatsapp","to":"{recipientPhone}","type":"text","text":{{"body":"{escapedText}"}}}}"""
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            let req = new HttpRequestMessage(HttpMethod.Post, $"{graphApiBase}/{config.PhoneNumberId}/messages")
            req.Headers.Add("Authorization", $"Bearer {config.AccessToken}")
            req.Content <- content
            try
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                if not resp.IsSuccessStatusCode then
                    let! err = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    eprintfn "[WhatsApp] Send failed (%d): %s" (int resp.StatusCode) (if err.Length > 200 then err.[..199] else err)
            with ex ->
                eprintfn "[WhatsApp] Send error: %s" ex.Message
        }

    let handleWebhook (body: string) : Async<string> =
        async {
            try
                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement

                // Navigate: entry[0].changes[0].value.messages[0]
                match root.TryGetProperty("entry") with
                | false, _ -> return """{"status":"ok"}"""
                | true, entries when entries.GetArrayLength() = 0 -> return """{"status":"ok"}"""
                | true, entries ->
                    let entry = entries.[0]
                    match entry.TryGetProperty("changes") with
                    | false, _ -> return """{"status":"ok"}"""
                    | true, changes when changes.GetArrayLength() = 0 -> return """{"status":"ok"}"""
                    | true, changes ->
                        let change = changes.[0]
                        match change.TryGetProperty("value") with
                        | false, _ -> return """{"status":"ok"}"""
                        | true, value ->
                            match value.TryGetProperty("messages") with
                            | false, _ -> return """{"status":"ok"}"""
                            | true, messages when messages.GetArrayLength() = 0 -> return """{"status":"ok"}"""
                            | true, messages ->
                                let msg = messages.[0]
                                let msgId = match msg.TryGetProperty("id") with true, id -> id.GetString() | _ -> ""
                                if msgId <> "" && not (processedIds.Add(msgId)) then
                                    return """{"status":"ok"}"""   // dedup
                                else
                                if processedIds.Count > 1000 then processedIds.Clear()

                                let fromPhone = match msg.TryGetProperty("from") with true, f -> f.GetString() | _ -> ""
                                let msgType = match msg.TryGetProperty("type") with true, t -> t.GetString() | _ -> ""
                                let text =
                                    if msgType = "text" then
                                        match msg.TryGetProperty("text") with
                                        | true, textObj -> match textObj.TryGetProperty("body") with true, b -> b.GetString() | _ -> ""
                                        | _ -> ""
                                    else $"[{msgType}]"

                                if fromPhone = "" || String.IsNullOrWhiteSpace text then
                                    return """{"status":"ok"}"""
                                elif not (AllowList.permits (UserId fromPhone) config.AllowFrom) then
                                    return """{"status":"ok"}"""
                                else

                                Async.Start(async {
                                    let inbound : InboundMessage = {
                                        Channel            = ChannelId "whatsapp"
                                        Sender             = UserId fromPhone
                                        Chat               = ChatId fromPhone
                                        Input              = BotSharp.Infrastructure.Channels.ChannelBase.parseInput (text.Trim())
                                        Metadata           = Map.ofList [ "message_id", msgId; "msg_type", msgType ]
                                        SessionKeyOverride = None
                                    }
                                    let! result = coordinator.Route inbound
                                    match result with
                                    | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                                        do! sendMessage fromPhone t
                                    | Result.Error e ->
                                        do! sendMessage fromPhone $"Error: {e}"
                                    | _ -> ()
                                })
                                return """{"status":"ok"}"""
            with ex ->
                eprintfn "[WhatsApp] Webhook error: %s" ex.Message
                return """{"error":"internal"}"""
        }

    let handleRequest (ctx: HttpListenerContext) : Async<unit> =
        async {
            let path = ctx.Request.Url |> Option.ofObj |> Option.map (fun u -> u.AbsolutePath) |> Option.defaultValue ""
            let method = ctx.Request.HttpMethod.ToUpperInvariant()
            try
                match method, path with
                | "GET", "/whatsapp/webhook" ->
                    // Webhook verification (Meta sends hub.mode, hub.verify_token, hub.challenge)
                    let query = ctx.Request.QueryString
                    let mode = query.["hub.mode"] |> Option.ofObj |> Option.defaultValue ""
                    let token = query.["hub.verify_token"] |> Option.ofObj |> Option.defaultValue ""
                    let challenge = query.["hub.challenge"] |> Option.ofObj |> Option.defaultValue ""
                    if mode = "subscribe" && token = config.VerifyToken then
                        let bytes = Encoding.UTF8.GetBytes(challenge)
                        ctx.Response.ContentType <- "text/plain"
                        ctx.Response.ContentLength64 <- int64 bytes.Length
                        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    else
                        ctx.Response.StatusCode <- 403
                    ctx.Response.Close()
                | "POST", "/whatsapp/webhook" ->
                    use reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)
                    let! body = reader.ReadToEndAsync() |> Async.AwaitTask
                    let! response = handleWebhook body
                    let bytes = Encoding.UTF8.GetBytes(response)
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    ctx.Response.Close()
                | "GET", "/whatsapp/health" ->
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
                eprintfn "[WhatsApp] Request error: %s" ex.Message
                try ctx.Response.Close() with _ -> ()
        }

    member _.Start() : Async<unit> =
        async {
            let prefix = $"http://localhost:{config.WebhookPort}/"
            listener.Prefixes.Add(prefix)
            listener.Start()
            printfn "[WhatsApp] Webhook server listening on http://localhost:%d" config.WebhookPort
            printfn "[WhatsApp]   GET/POST /whatsapp/webhook    Meta webhook"
            printfn "[WhatsApp]   GET      /whatsapp/health     Health check"

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
