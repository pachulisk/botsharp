module BotSharp.Infrastructure.Channels.QQChannel

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.WebSockets
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// QQ channel using QQ Bot Open API (HTTP + WebSocket)
//
// No external SDK — uses standard .NET HttpClient + ClientWebSocket.
// Uses QQ Bot's WebSocket Gateway for receiving events and REST API
// for sending C2C (private) messages.
//
// API:
//   POST /app/getAppAccessToken → get access token
//   GET  /gateway → get WebSocket URL
//   POST /v2/users/{openid}/messages → send C2C message
//
// Config:
//   "qq": {
//     "app_id": "xxx",
//     "secret": "xxx",
//     "allow_from": ["*"]
//   }
// ═══════════════════════════════════════════════════════════════════════════

let private qqApiBase = "https://api.sgroup.qq.com"
let private qqSandboxApiBase = "https://sandbox.api.sgroup.qq.com"

type QQConfig = {
    AppId     : string
    Secret    : string
    AllowFrom : AllowList
    Sandbox   : bool
}

type QQServer(coordinator: AgentCoordinator, config: QQConfig, httpClient: HttpClient) =
    let mutable running = true
    let mutable accessToken = ""
    let mutable tokenExpiresAt = DateTimeOffset.MinValue
    let processedIds = HashSet<string>()
    let apiBase = if config.Sandbox then qqSandboxApiBase else qqApiBase

    let refreshToken () : Async<string> =
        async {
            if accessToken <> "" && DateTimeOffset.UtcNow < tokenExpiresAt then
                return accessToken
            else
                let body = $"""{{"appId":"{config.AppId}","clientSecret":"{config.Secret}"}}"""
                let content = new StringContent(body, Encoding.UTF8, "application/json")
                try
                    let! resp = httpClient.PostAsync("https://bots.qq.com/app/getAppAccessToken", content) |> Async.AwaitTask
                    let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    use doc = JsonDocument.Parse(respBody)
                    let root = doc.RootElement
                    match root.TryGetProperty("access_token") with
                    | true, token ->
                        let expire = match root.TryGetProperty("expires_in") with true, e -> e.GetString() |> int | _ -> 7200
                        accessToken <- token.GetString()
                        tokenExpiresAt <- DateTimeOffset.UtcNow.AddSeconds(float (expire - 60))
                        eprintfn "[QQ] Token refreshed"
                        return accessToken
                    | _ ->
                        eprintfn "[QQ] Token refresh failed"
                        return accessToken
                with ex ->
                    eprintfn "[QQ] Token error: %s" ex.Message
                    return accessToken
        }

    let sendC2CMessage (openId: string) (text: string) (msgId: string) : Async<unit> =
        async {
            let! token = refreshToken ()
            let escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            let body = $"""{{"content":"{escapedText}","msg_type":0,"msg_id":"{msgId}"}}"""
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            let req = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/v2/users/{openId}/messages")
            req.Headers.Add("Authorization", $"QQBot {token}")
            req.Content <- content
            try
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                if not resp.IsSuccessStatusCode then
                    let! err = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    eprintfn "[QQ] Send failed (%d): %s" (int resp.StatusCode) (if err.Length > 200 then err.[..199] else err)
            with ex ->
                eprintfn "[QQ] Send error: %s" ex.Message
        }

    let handleEvent (payload: JsonElement) : Async<unit> =
        async {
            let eventType = match payload.TryGetProperty("t") with true, t -> t.GetString() | _ -> ""

            if eventType <> "C2C_MESSAGE_CREATE" && eventType <> "DIRECT_MESSAGE_CREATE" then ()
            else

            match payload.TryGetProperty("d") with
            | false, _ -> ()
            | true, data ->
                let msgId = match data.TryGetProperty("id") with true, id -> id.GetString() | _ -> ""
                if msgId <> "" && not (processedIds.Add(msgId)) then ()   // dedup
                else
                if processedIds.Count > 1000 then processedIds.Clear()

                let userId =
                    match data.TryGetProperty("author") with
                    | true, author ->
                        match author.TryGetProperty("user_openid") with
                        | true, oid -> oid.GetString()
                        | _ -> match author.TryGetProperty("id") with true, id -> id.GetString() | _ -> ""
                    | _ -> ""

                let content = match data.TryGetProperty("content") with true, c -> c.GetString().Trim() | _ -> ""

                if userId = "" || content = "" then ()
                elif not (AllowList.permits (UserId userId) config.AllowFrom) then ()
                else

                Async.Start(async {
                    let inbound : InboundMessage = {
                        Channel            = ChannelId "qq"
                        Sender             = UserId userId
                        Chat               = ChatId userId
                        Input              = ChatMessage (content, [])
                        Metadata           = Map.ofList [ "message_id", msgId ]
                        SessionKeyOverride = None
                    }
                    let! result = coordinator.Route inbound
                    match result with
                    | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                        do! sendC2CMessage userId t msgId
                    | Result.Error e ->
                        do! sendC2CMessage userId ($"Error: {e}") msgId
                    | _ -> ()
                })
        }

    let gatewayLoop () : Async<unit> =
        async {
            while running do
                try
                    let! token = refreshToken ()
                    let req = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/gateway")
                    req.Headers.Add("Authorization", $"QQBot {token}")
                    let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    use doc = JsonDocument.Parse(body)
                    let wsUrl = match doc.RootElement.TryGetProperty("url") with true, u -> u.GetString() | _ -> ""

                    if wsUrl = "" then
                        eprintfn "[QQ] No gateway URL"
                        do! Async.Sleep 5000
                    else

                    use ws = new ClientWebSocket()
                    do! ws.ConnectAsync(Uri(wsUrl), CancellationToken.None) |> Async.AwaitTask
                    printfn "[QQ] Gateway connected"

                    let buffer = Array.zeroCreate<byte> (64 * 1024)
                    let mutable seq = 0

                    while running && ws.State = WebSocketState.Open do
                        let ms = new MemoryStream()
                        let mutable complete = false
                        while not complete do
                            let! result = ws.ReceiveAsync(ArraySegment(buffer), CancellationToken.None) |> Async.AwaitTask
                            ms.Write(buffer, 0, result.Count)
                            complete <- result.EndOfMessage
                        let json = Encoding.UTF8.GetString(ms.ToArray())
                        if json.Length > 0 then
                            try
                                use eventDoc = JsonDocument.Parse(json)
                                let root = eventDoc.RootElement
                                let op = match root.TryGetProperty("op") with true, o -> o.GetInt32() | _ -> -1

                                match root.TryGetProperty("s") with
                                | true, s when s.ValueKind = JsonValueKind.Number -> seq <- s.GetInt32()
                                | _ -> ()

                                match op with
                                | 10 -> // Hello: send Identify
                                    let identify = $"""{{"op":2,"d":{{"token":"QQBot {token}","intents":33554432}}}}"""
                                    let identifyBytes = Encoding.UTF8.GetBytes(identify)
                                    do! ws.SendAsync(ArraySegment(identifyBytes), WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                                | 0 -> // Dispatch: handle event
                                    do! handleEvent root
                                | 1 -> // Heartbeat request
                                    let hb = $"""{{"op":1,"d":{seq}}}"""
                                    let hbBytes = Encoding.UTF8.GetBytes(hb)
                                    do! ws.SendAsync(ArraySegment(hbBytes), WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                                | 11 -> () // Heartbeat ACK
                                | 7 | 9 -> // Reconnect / Invalid Session
                                    eprintfn "[QQ] Gateway reconnect requested"
                                | _ -> ()
                            with _ -> ()
                with ex ->
                    eprintfn "[QQ] Gateway error: %s" ex.Message
                    if running then
                        do! Async.Sleep 5000
        }

    member _.Start() : Async<unit> =
        async {
            printfn "[QQ] Connecting (app_id: %s)" config.AppId
            do! gatewayLoop ()
        }

    member _.Stop() =
        running <- false
