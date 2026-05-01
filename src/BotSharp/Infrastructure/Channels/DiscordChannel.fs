module BotSharp.Infrastructure.Channels.DiscordChannel

open System
open System.Threading
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Discord channel using Discord.Net SDK
//
// Uses the Discord Gateway WebSocket for receiving messages and the REST
// API for sending replies. Follows the same pattern as TelegramChannel:
//   - Per-channel session routing via AgentCoordinator
//   - Streaming support (StreamedResponse / PlainResponse detection)
//   - Message splitting for Discord's 2000-char limit
//
// Config in config.json:
//   "discord": { "token": "BOT_TOKEN", "allow_from": ["*"] }
// ═══════════════════════════════════════════════════════════════════════════

let private maxMessageLen = 2000

// ── Configuration ────────────────────────────────────────────────────────

type DiscordConfig = {
    Token     : string
    AllowFrom : AllowList
}

// ── Message splitting ────────────────────────────────────────────────────

let private splitMessage (content: string) : string list =
    if String.IsNullOrEmpty content then []
    elif content.Length <= maxMessageLen then [ content ]
    else
        let rec split (remaining: string) acc =
            if remaining.Length <= maxMessageLen then
                List.rev (remaining :: acc)
            else
                let chunk = remaining.[..maxMessageLen-1]
                let pos =
                    let nl = chunk.LastIndexOf('\n')
                    if nl > 0 then nl
                    else
                        let sp = chunk.LastIndexOf(' ')
                        if sp > 0 then sp
                        else maxMessageLen
                let piece = remaining.[..pos-1]
                let rest  = remaining.[pos..].TrimStart()
                split rest (piece :: acc)
        split content []

// ── Session ID ───────────────────────────────────────────────────────────

let private sessionIdForDiscord (channelId: uint64) : SessionId =
    SessionId (sprintf "discord:%d" channelId)

// ── Server ───────────────────────────────────────────────────────────────

type DiscordServer(coordinator: AgentCoordinator, config: DiscordConfig) =
    let client = new DiscordSocketClient(
        DiscordSocketConfig(
            GatewayIntents = (GatewayIntents.Guilds ||| GatewayIntents.GuildMessages ||| GatewayIntents.DirectMessages ||| GatewayIntents.MessageContent)))

    let handleMessage (msg: SocketMessage) : Task =
        Task.Run(fun () ->
            async {
                // Ignore bot messages
                if msg.Author.IsBot then ()
                else

                let senderId = string msg.Author.Id
                let channelId = msg.Channel.Id
                let content = if String.IsNullOrEmpty msg.Content then "[empty message]" else msg.Content

                // Allow list check
                if not (AllowList.permits (UserId senderId) config.AllowFrom) then ()
                else

                // Typing indicator
                let typingState = msg.Channel.EnterTypingState()

                let inbound : InboundMessage = {
                    Channel            = ChannelId "discord"
                    Sender             = UserId senderId
                    Chat               = ChatId (string channelId)
                    Input              = BotSharp.Infrastructure.Channels.ChannelBase.parseInput content
                    Metadata           = Map.ofList [ "message_id", string msg.Id ]
                    SessionKeyOverride = None
                }

                let! result = coordinator.Route inbound
                typingState.Dispose()

                match result with
                | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) when not (String.IsNullOrWhiteSpace text) ->
                    let chunks = splitMessage text
                    for chunk in chunks do
                        try
                            msg.Channel.SendMessageAsync(chunk)
                            |> Async.AwaitTask |> Async.Ignore
                            |> Async.RunSynchronously
                        with ex ->
                            eprintfn "[Discord] SendMessage error: %s" ex.Message
                | Result.Error e ->
                    try
                        msg.Channel.SendMessageAsync(sprintf "Error: %A" e)
                        |> Async.AwaitTask |> Async.Ignore
                        |> Async.RunSynchronously
                    with _ -> ()
                | _ -> ()
            } |> Async.StartAsTask :> Task)

    member _.Start() : Async<unit> =
        async {
            client.add_MessageReceived(fun msg -> handleMessage msg)
            client.add_Ready(fun () ->
                printfn "[Discord] Bot is ready: %s#%s" client.CurrentUser.Username client.CurrentUser.Discriminator
                Task.CompletedTask)
            client.add_Log(fun logMsg ->
                if logMsg.Severity <= LogSeverity.Warning then
                    eprintfn "[Discord] %s" logMsg.Message
                Task.CompletedTask)

            do! client.LoginAsync(TokenType.Bot, config.Token) |> Async.AwaitTask
            do! client.StartAsync() |> Async.AwaitTask
            printfn "[Discord] Connecting to gateway..."

            // Block until cancelled
            let! ct = Async.CancellationToken
            try
                do! Task.Delay(Timeout.Infinite, ct) |> Async.AwaitTask
            with :? TaskCanceledException -> ()
        }

    member _.Stop() =
        client.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
        client.Dispose()
