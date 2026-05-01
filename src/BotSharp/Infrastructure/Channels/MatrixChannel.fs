module BotSharp.Infrastructure.Channels.MatrixChannel

#nowarn "3261" // Nullness interop — C# libs return 'string | null' consumed as 'string'

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Matrix (Element) channel using Client-Server API
//
// No external SDK — uses standard .NET HttpClient.
// Inbound: long-poll /sync for new messages.
// Outbound: PUT /rooms/{roomId}/send/m.room.message/{txnId}
//
// Config:
//   "matrix": {
//     "homeserver": "https://matrix.org",
//     "user_id": "@bot:matrix.org",
//     "access_token": "syt_xxx",
//     "allow_from": ["*"]
//   }
// ═══════════════════════════════════════════════════════════════════════════

type MatrixConfig = {
    Homeserver  : string   // e.g. https://matrix.org
    UserId      : string   // e.g. @bot:matrix.org
    AccessToken : string
    AllowFrom   : AllowList
}

type MatrixServer(coordinator: AgentCoordinator, config: MatrixConfig, httpClient: HttpClient) =
    let mutable running = true
    let mutable nextBatch = ""
    let mutable txnCounter = 0L

    let apiUrl (path: string) =
        let baseUrl = config.Homeserver.TrimEnd('/')
        $"{baseUrl}/_matrix/client/v3{path}"

    let authHeaders () =
        [| "Authorization", $"Bearer {config.AccessToken}" |]

    let sendRequest (method: HttpMethod) (url: string) (body: string option) : Async<Result<JsonElement, string>> =
        async {
            try
                let req = new HttpRequestMessage(method, url)
                for (k, v) in authHeaders () do
                    req.Headers.TryAddWithoutValidation(k, v) |> ignore
                match body with
                | Some b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/json")
                | None -> ()
                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                if resp.IsSuccessStatusCode then
                    use doc = JsonDocument.Parse(respBody)
                    return Ok (doc.RootElement.Clone())
                else
                    return Error $"Matrix API {resp.StatusCode}: {respBody.[..min 200 (respBody.Length - 1)]}"
            with ex ->
                return Error $"Matrix API error: {ex.Message}"
        }

    let sendMessage (roomId: string) (text: string) : Async<unit> =
        async {
            txnCounter <- txnCounter + 1L
            let txnId = $"botsharp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{txnCounter}"
            let escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            let body = $"""{{"msgtype":"m.text","body":"{escapedText}"}}"""
            let url = apiUrl $"/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{txnId}"
            match! sendRequest HttpMethod.Put url (Some body) with
            | Ok _ -> ()
            | Error msg -> eprintfn "[Matrix] %s" msg
        }

    let processTimeline (roomId: string) (events: JsonElement) : Async<unit> =
        async {
            if events.ValueKind <> JsonValueKind.Array then ()
            else
            for event in events.EnumerateArray() do
                let eventType =
                    match event.TryGetProperty("type") with true, t -> t.GetString() | _ -> ""
                if eventType <> "m.room.message" then ()
                else
                let sender =
                    match event.TryGetProperty("sender") with true, s -> s.GetString() | _ -> ""
                if sender = "" || sender = config.UserId then ()   // skip own messages
                else
                if not (AllowList.permits (UserId sender) config.AllowFrom) then ()
                else
                let content =
                    match event.TryGetProperty("content") with true, c -> c | _ -> JsonElement()
                let msgType =
                    match content.TryGetProperty("msgtype") with true, t -> t.GetString() | _ -> ""
                let body =
                    match content.TryGetProperty("body") with true, b -> b.GetString() | _ -> ""

                if msgType <> "m.text" || String.IsNullOrWhiteSpace body then ()
                else

                Async.Start(async {
                    let inbound : InboundMessage = {
                        Channel            = ChannelId "matrix"
                        Sender             = UserId sender
                        Chat               = ChatId roomId
                        Input              = ChatMessage (body, [])
                        Metadata           = Map.ofList [ "msg_type", msgType ]
                        SessionKeyOverride = None
                    }
                    let! result = coordinator.Route inbound
                    match result with
                    | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                        do! sendMessage roomId t
                    | Result.Error e ->
                        do! sendMessage roomId $"Error: {e}"
                    | _ -> ()
                })
        }

    let sync () : Async<unit> =
        async {
            let timeout = 30000
            let filterStr = """{"room":{"timeline":{"limit":10},"state":{"lazy_load_members":true}}}"""
            let url =
                if nextBatch = "" then
                    apiUrl $"/sync?timeout={timeout}&filter={Uri.EscapeDataString(filterStr)}"
                else
                    apiUrl $"/sync?timeout={timeout}&since={nextBatch}&filter={Uri.EscapeDataString(filterStr)}"

            match! sendRequest HttpMethod.Get url None with
            | Error msg ->
                eprintfn "[Matrix] Sync error: %s" msg
                do! Async.Sleep 5000
            | Ok root ->
                match root.TryGetProperty("next_batch") with
                | true, nb -> nextBatch <- nb.GetString()
                | _ -> ()

                // Process room events
                match root.TryGetProperty("rooms") with
                | true, rooms ->
                    match rooms.TryGetProperty("join") with
                    | true, join when join.ValueKind = JsonValueKind.Object ->
                        for room in join.EnumerateObject() do
                            let roomId = room.Name
                            match room.Value.TryGetProperty("timeline") with
                            | true, timeline ->
                                match timeline.TryGetProperty("events") with
                                | true, events -> do! processTimeline roomId events
                                | _ -> ()
                            | _ -> ()
                    | _ -> ()
                | _ -> ()
        }

    member _.Start() : Async<unit> =
        async {
            printfn "[Matrix] Connecting to %s as %s" config.Homeserver config.UserId
            // Initial sync to get next_batch (skip old messages)
            let initUrl = apiUrl "/sync?timeout=0&filter={\"room\":{\"timeline\":{\"limit\":0}}}"
            match! sendRequest HttpMethod.Get initUrl None with
            | Ok root ->
                match root.TryGetProperty("next_batch") with
                | true, nb ->
                    nextBatch <- nb.GetString()
                    printfn "[Matrix] Initial sync complete, listening for messages..."
                | _ -> ()
            | Error msg ->
                eprintfn "[Matrix] Initial sync failed: %s" msg

            while running do
                do! sync ()
        }

    member _.Stop() =
        running <- false
