module BotSharp.Infrastructure.Channels.TelnetChannel

#nowarn "3261" // Nullness interop — C# libs return nullable types consumed as non-null

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Telnet channel — simple TCP text server
//
// Each TCP connection gets its own session. The user types a line,
// the agent processes it, and the response is sent back.
// Useful for debugging or integration with legacy systems.
//
// Config:
//   "telnet": { "port": 2323, "allow_from": ["*"] }
// ═══════════════════════════════════════════════════════════════════════════

type TelnetConfig = {
    Port      : int
    AllowFrom : AllowList
}

type TelnetServer(coordinator: AgentCoordinator, config: TelnetConfig) =
    let listener = new TcpListener(IPAddress.Loopback, config.Port)
    let mutable running = true

    let handleClient (client: TcpClient) : Async<unit> =
        async {
            let endpoint = client.Client.RemoteEndPoint.ToString()
            let sessionId = "telnet:" + endpoint.Replace(":", "_")
            eprintfn "[Telnet] Client connected: %s" endpoint
            use stream = client.GetStream()
            use reader = new StreamReader(stream, Encoding.UTF8)
            use writer = new StreamWriter(stream, Encoding.UTF8, AutoFlush = true)

            try
                do! writer.WriteLineAsync("BotSharp Telnet. Type /help for commands, /quit to disconnect.") |> Async.AwaitTask
                do! writer.WriteAsync("you> ") |> Async.AwaitTask

                let mutable connected = true
                while connected && running do
                    let! line = reader.ReadLineAsync() |> Async.AwaitTask
                    if line = null then
                        connected <- false
                    elif line.Trim().ToLowerInvariant() = "/quit" then
                        do! writer.WriteLineAsync("Bye!") |> Async.AwaitTask
                        connected <- false
                    elif line.Trim() = "" then
                        do! writer.WriteAsync("you> ") |> Async.AwaitTask
                    else
                        let inbound : InboundMessage = {
                            Channel            = ChannelId "telnet"
                            Sender             = UserId endpoint
                            Chat               = ChatId sessionId
                            Input              = ChatMessage (line.Trim(), [])
                            Metadata           = Map.empty
                            SessionKeyOverride = Some (SessionId sessionId)
                        }
                        let! result = coordinator.Route inbound
                        match result with
                        | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) ->
                            if not (String.IsNullOrWhiteSpace text) then
                                do! writer.WriteLineAsync(text) |> Async.AwaitTask
                        | Result.Error e ->
                            do! writer.WriteLineAsync($"Error: {e}") |> Async.AwaitTask
                        do! writer.WriteAsync("\nyou> ") |> Async.AwaitTask
            with
            | :? IOException -> ()
            | ex -> eprintfn "[Telnet] Client error: %s" ex.Message

            eprintfn "[Telnet] Client disconnected: %s" endpoint
            client.Close()
        }

    member _.Start() : Async<unit> =
        async {
            listener.Start()
            printfn "[Telnet] Listening on localhost:%d" config.Port

            try
                while running do
                    let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
                    Async.Start(handleClient client)
            with
            | :? ObjectDisposedException -> ()
            | :? SocketException -> ()
        }

    member _.Stop() =
        running <- false
        listener.Stop()
