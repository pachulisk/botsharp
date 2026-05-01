module BotSharp.Infrastructure.Channels.MoChatChannel

#nowarn "3261" // Nullness interop — C# libs return 'string | null' consumed as 'string'

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// MoChat channel — WeChat private domain proxy
//
// Connects to a MoChat/Claw server via HTTP polling + REST API.
// MoChat proxies WeChat messages through its own API server.
//
// No Socket.IO SDK — uses HTTP polling for receiving and REST for sending.
//
// Config:
//   "mochat": {
//     "base_url": "https://your-mochat-server.com",
//     "claw_token": "xxx",
//     "poll_interval_seconds": 5,
//     "allow_from": ["*"]
//   }
// ═══════════════════════════════════════════════════════════════════════════

type MoChatConfig = {
    BaseUrl      : string
    ClawToken    : string
    PollSeconds  : int
    AllowFrom    : AllowList
}

type MoChatServer(coordinator: AgentCoordinator, config: MoChatConfig, httpClient: HttpClient) =
    let mutable running = true
    let processedIds = HashSet<string>()
    let baseUrl = config.BaseUrl.TrimEnd('/')

    let apiRequest (method: HttpMethod) (path: string) (body: string option) : Async<Result<JsonElement, string>> =
        async {
            try
                let req = new HttpRequestMessage(method, $"{baseUrl}{path}")
                req.Headers.Add("Authorization", $"Bearer {config.ClawToken}")
                match body with
                | Some b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/json")
                | None -> ()
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                if resp.IsSuccessStatusCode then
                    use doc = JsonDocument.Parse(respBody)
                    return Ok (doc.RootElement.Clone())
                else
                    return Error $"MoChat API {resp.StatusCode}: {respBody.[..min 200 (respBody.Length - 1)]}"
            with ex ->
                return Error $"MoChat API error: {ex.Message}"
        }

    let sendMessage (sessionId: string) (text: string) : Async<unit> =
        async {
            let escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            let body = $"""{{"session_id":"{sessionId}","content":"{escapedText}","type":"text"}}"""
            match! apiRequest HttpMethod.Post "/api/v1/messages/send" (Some body) with
            | Ok _ -> ()
            | Error msg -> eprintfn "[MoChat] Send error: %s" msg
        }

    let pollMessages () : Async<unit> =
        async {
            match! apiRequest HttpMethod.Get "/api/v1/messages/pending" None with
            | Error msg ->
                eprintfn "[MoChat] Poll error: %s" msg
            | Ok root ->
                let messages =
                    match root.TryGetProperty("data") with
                    | true, data when data.ValueKind = JsonValueKind.Array -> data.EnumerateArray() |> Seq.toList
                    | _ ->
                        match root.TryGetProperty("messages") with
                        | true, msgs when msgs.ValueKind = JsonValueKind.Array -> msgs.EnumerateArray() |> Seq.toList
                        | _ -> []

                for msg in messages do
                    let msgId = match msg.TryGetProperty("id") with true, id -> id.GetString() | _ -> ""
                    if msgId <> "" && not (processedIds.Add(msgId)) then ()   // dedup
                    else
                    if processedIds.Count > 2000 then processedIds.Clear()

                    let senderId = match msg.TryGetProperty("sender_id") with true, s -> s.GetString() | _ -> ""
                    let sessionId = match msg.TryGetProperty("session_id") with true, s -> s.GetString() | _ -> senderId
                    let content = match msg.TryGetProperty("content") with true, c -> c.GetString() | _ -> ""
                    let msgType = match msg.TryGetProperty("type") with true, t -> t.GetString() | _ -> "text"

                    let text = if msgType = "text" then content else $"[{msgType}]"

                    if senderId = "" || String.IsNullOrWhiteSpace text then ()
                    elif not (AllowList.permits (UserId senderId) config.AllowFrom) then ()
                    else

                    Async.Start(async {
                        let inbound : InboundMessage = {
                            Channel            = ChannelId "mochat"
                            Sender             = UserId senderId
                            Chat               = ChatId sessionId
                            Input              = BotSharp.Infrastructure.Channels.ChannelBase.parseInput (text.Trim())
                            Metadata           = Map.ofList [ "message_id", msgId; "msg_type", msgType ]
                            SessionKeyOverride = None
                        }
                        let! result = coordinator.Route inbound
                        match result with
                        | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                            do! sendMessage sessionId t
                        | Result.Error e ->
                            do! sendMessage sessionId $"Error: {e}"
                        | _ -> ()
                    })
        }

    member _.Start() : Async<unit> =
        async {
            printfn "[MoChat] Connecting to %s (polling every %ds)" baseUrl config.PollSeconds
            while running do
                do! pollMessages ()
                do! Async.Sleep (config.PollSeconds * 1000)
        }

    member _.Stop() =
        running <- false
