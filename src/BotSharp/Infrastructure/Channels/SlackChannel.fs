module BotSharp.Infrastructure.Channels.SlackChannel

#nowarn "3261" // Nullness interop — C# libs return 'string | null' consumed as 'string'

open System
open System.IO
open System.Net.Http
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Slack channel using Socket Mode (WebSocket) + Web API (HTTP)
//
// No external SDK — uses standard .NET HttpClient + ClientWebSocket.
// Socket Mode: receives events via WebSocket (no public endpoint needed).
// Web API: sends messages via chat.postMessage.
//
// Config in config.json:
//   "slack": {
//     "bot_token": "xoxb-...",
//     "app_token": "xapp-...",
//     "allow_from": ["*"],
//     "reply_in_thread": true
//   }
// ═══════════════════════════════════════════════════════════════════════════

let private slackApiBase = "https://slack.com/api"

// ── Configuration ────────────────────────────────────────────────────────

type SlackConfig = {
    BotToken      : string
    AppToken      : string
    AllowFrom     : AllowList
    ReplyInThread : bool
}

// ── Markdown → Slack mrkdwn ──────────────────────────────────────────────

let private toMrkdwn (text: string) : string =
    if String.IsNullOrEmpty text then ""
    else
        text
        // **bold** → *bold*
        |> fun s -> Regex.Replace(s, @"\*\*(.+?)\*\*", "*$1*")
        // ## headers → *header*
        |> fun s -> Regex.Replace(s, @"^#{1,6}\s+(.+)$", "*$1*", RegexOptions.Multiline)

// ── Session ID ───────────────────────────────────────────────────────────

let private sessionIdForSlack (channelId: string) (threadTs: string option) : string option =
    match threadTs with
    | Some ts -> Some $"slack:{channelId}:{ts}"
    | None    -> None

// ── Server ───────────────────────────────────────────────────────────────

type SlackServer(coordinator: AgentCoordinator, config: SlackConfig, httpClient: HttpClient) =
    let mutable botUserId = ""
    let mutable running = true

    let postSlackApi (method: string) (body: string) : Async<Result<JsonElement, string>> =
        async {
            try
                let content = new StringContent(body, Encoding.UTF8, "application/json")
                let req = new HttpRequestMessage(HttpMethod.Post, $"{slackApiBase}/{method}")
                req.Headers.Add("Authorization", $"Bearer {config.BotToken}")
                req.Content <- content
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                use doc = JsonDocument.Parse(respBody)
                let root = doc.RootElement.Clone()
                match root.TryGetProperty("ok") with
                | true, ok when ok.GetBoolean() -> return Ok root
                | _ ->
                    let err = match root.TryGetProperty("error") with true, e -> e.GetString() | _ -> "unknown"
                    return Error $"Slack API {method}: {err}"
            with ex ->
                return Error $"Slack API {method}: {ex.Message}"
        }

    let sendMessage (channelId: string) (text: string) (threadTs: string option) : Async<unit> =
        async {
            let mrkdwn = toMrkdwn text
            let threadField = match threadTs with Some ts -> $""","thread_ts":"{ts}" """ | None -> ""
            let body = $"""{{"channel":"{channelId}","text":"{mrkdwn |> fun s -> s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n")}"{threadField}}}"""
            match! postSlackApi "chat.postMessage" body with
            | Ok _ -> ()
            | Error msg -> eprintfn "[Slack] %s" msg
        }

    let getWsUrl () : Async<Result<string, string>> =
        async {
            try
                let req = new HttpRequestMessage(HttpMethod.Post, $"{slackApiBase}/apps.connections.open")
                req.Headers.Add("Authorization", $"Bearer {config.AppToken}")
                req.Content <- new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded")
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement
                match root.TryGetProperty("ok") with
                | true, ok when ok.GetBoolean() ->
                    match root.TryGetProperty("url") with
                    | true, url -> return Ok (url.GetString())
                    | _ -> return Error "No url in apps.connections.open response"
                | _ ->
                    let err = match root.TryGetProperty("error") with true, e -> e.GetString() | _ -> "unknown"
                    return Error $"apps.connections.open: {err}"
            with ex ->
                return Error $"apps.connections.open: {ex.Message}"
        }

    let handleEvent (envelopeId: string) (event: JsonElement) (ws: ClientWebSocket) : Async<unit> =
        async {
            // Acknowledge immediately
            let ack = $"""{{"envelope_id":"{envelopeId}"}}"""
            let ackBytes = Encoding.UTF8.GetBytes(ack)
            do! ws.SendAsync(ArraySegment(ackBytes), WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask

            let eventType =
                match event.TryGetProperty("type") with true, t -> t.GetString() | _ -> ""

            if eventType <> "message" && eventType <> "app_mention" then ()
            else

            // Ignore bot messages and subtypes
            let hasSubtype = match event.TryGetProperty("subtype") with true, _ -> true | _ -> false
            if hasSubtype then ()
            else

            let senderId =
                match event.TryGetProperty("user") with true, u -> u.GetString() | _ -> ""
            let channelId =
                match event.TryGetProperty("channel") with true, c -> c.GetString() | _ -> ""
            let text =
                match event.TryGetProperty("text") with true, t -> t.GetString() | _ -> ""

            if String.IsNullOrEmpty senderId || String.IsNullOrEmpty channelId then ()
            elif senderId = botUserId then ()   // ignore own messages
            elif not (AllowList.permits (UserId senderId) config.AllowFrom) then ()
            else

            // Strip bot mention from text
            let cleanText =
                if String.IsNullOrEmpty botUserId then text
                else Regex.Replace(text, $"<@{botUserId}>\\s*", "").Trim()

            let threadTs =
                match event.TryGetProperty("thread_ts") with
                | true, ts -> Some (ts.GetString())
                | _ ->
                    if config.ReplyInThread then
                        match event.TryGetProperty("ts") with true, ts -> Some (ts.GetString()) | _ -> None
                    else None

            let sessionKey = sessionIdForSlack channelId threadTs

            let inbound : InboundMessage = {
                Channel            = ChannelId "slack"
                Sender             = UserId senderId
                Chat               = ChatId channelId
                Input              = BotSharp.Infrastructure.Channels.ChannelBase.parseInput cleanText
                Metadata           = Map.ofList [ "thread_ts", (threadTs |> Option.defaultValue "") ]
                SessionKeyOverride = sessionKey |> Option.map SessionId
            }

            let! result = coordinator.Route inbound
            match result with
            | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) when not (String.IsNullOrWhiteSpace text) ->
                do! sendMessage channelId text threadTs
            | Result.Error e ->
                do! sendMessage channelId $"Error: {e}" threadTs
            | _ -> ()
        }

    let socketLoop () : Async<unit> =
        async {
            while running do
                match! getWsUrl () with
                | Error msg ->
                    eprintfn "[Slack] %s" msg
                    do! Async.Sleep 5000
                | Ok wsUrl ->
                    use ws = new ClientWebSocket()
                    try
                        do! ws.ConnectAsync(Uri(wsUrl), CancellationToken.None) |> Async.AwaitTask
                        printfn "[Slack] Socket Mode connected"

                        let buffer = Array.zeroCreate<byte> (64 * 1024)
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
                                    use doc = JsonDocument.Parse(json)
                                    let root = doc.RootElement
                                    let envelopeId =
                                        match root.TryGetProperty("envelope_id") with true, e -> e.GetString() | _ -> ""
                                    let msgType =
                                        match root.TryGetProperty("type") with true, t -> t.GetString() | _ -> ""

                                    if msgType = "events_api" && envelopeId <> "" then
                                        match root.TryGetProperty("payload") with
                                        | true, payload ->
                                            match payload.TryGetProperty("event") with
                                            | true, event ->
                                                Async.Start(handleEvent envelopeId event ws)
                                            | _ -> ()
                                        | _ -> ()
                                    elif msgType = "disconnect" then
                                        eprintfn "[Slack] Received disconnect, reconnecting..."
                                    else
                                        // Acknowledge other envelope types (hello, etc.)
                                        if envelopeId <> "" then
                                            let ack = $"""{{"envelope_id":"{envelopeId}"}}"""
                                            let ackBytes = Encoding.UTF8.GetBytes(ack)
                                            do! ws.SendAsync(ArraySegment(ackBytes), WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                                with _ -> ()
                    with ex ->
                        eprintfn "[Slack] WebSocket error: %s" ex.Message
                        if running then
                            eprintfn "[Slack] Reconnecting in 5s..."
                            do! Async.Sleep 5000
        }

    member _.Start() : Async<unit> =
        async {
            // Resolve bot user ID
            match! postSlackApi "auth.test" "{}" with
            | Ok root ->
                match root.TryGetProperty("user_id") with
                | true, uid -> botUserId <- uid.GetString()
                | _ -> ()
                printfn "[Slack] Bot connected (user_id: %s)" botUserId
            | Error msg ->
                eprintfn "[Slack] auth.test failed: %s" msg

            do! socketLoop ()
        }

    member _.Stop() =
        running <- false
