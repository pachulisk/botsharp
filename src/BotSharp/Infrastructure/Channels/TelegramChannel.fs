module BotSharp.Infrastructure.Channels.TelegramChannel

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Application.SessionActor
open BotSharp.Infrastructure.Input.InputParser
open BotSharp.Infrastructure.Channels.MarkdownParser
open BotSharp.Infrastructure.Storage.DreamStore

// ── Aliases to avoid name collisions with BotSharp.Domain.Types ────────────
// Domain defines: Message (chat history), ChatId (newtype), SessionId, etc.
// Telegram.Bot.Types also defines: Message, ChatId, File, etc.
// We alias the Telegram types so both can coexist in this module.
type private TgMessage = Telegram.Bot.Types.Message
type private TgChatId  = Telegram.Bot.Types.ChatId
type private TgFile    = Telegram.Bot.Types.File

// ═══════════════════════════════════════════════════════════════════════════
// § 1  Per-chat streaming state
//
// NotStarted: no message sent yet for this turn.
// Streaming: a message was sent; accumulate text and rate-limit edits.
// ═══════════════════════════════════════════════════════════════════════════

[<Struct>]
type StreamState =
    | NotStarted
    | Streaming of MsgId: int * Accumulated: string * LastEdit: DateTimeOffset

// ═══════════════════════════════════════════════════════════════════════════
// § 2  Small pure helpers
// ═══════════════════════════════════════════════════════════════════════════

/// Build a domain SessionId from a Telegram chat ID.
let private sessionIdForTelegram (chatId: int64) : SessionId =
    SessionId (sprintf "telegram:%d" chatId)

/// Build a domain UserId: "<userId>|<username>" (mirrors Python nanobot).
let private makeSenderId (user: User) : UserId =
    let username =
        match user.Username with
        | null -> "anon"
        | u when String.IsNullOrEmpty(u) -> "anon"
        | u -> u
    UserId (sprintf "%d|%s" user.Id username)

/// True when the chat is a group / supergroup / channel.
let private isGroupChat (chatType: ChatType) : bool =
    chatType = ChatType.Group || chatType = ChatType.Supergroup || chatType = ChatType.Channel

/// Check whether this message should be processed given the GroupPolicy.
/// DMs always pass.  Groups: OpenPolicy → always; MentionPolicy → @mentioned or reply-to-bot.
let private checkGroupPolicy (tgCfg: TelegramConfig) (msg: TgMessage) (botUsername: string) : bool =
    if not (isGroupChat msg.Chat.Type) then true
    else
        match tgCfg.GroupPolicy with
        | OpenPolicy -> true
        | MentionPolicy ->
            let txt =
                match msg.Text with
                | null ->
                    match msg.Caption with
                    | null -> ""
                    | cap  -> cap
                | t -> t
            let mentioned   = txt.Contains("@" + botUsername, StringComparison.OrdinalIgnoreCase)
            let replyToBot  =
                match msg.ReplyToMessage with
                | null -> false
                | reply ->
                    match reply.From with
                    | null -> false
                    | from ->
                        match from.Username with
                        | null -> false
                        | uname -> uname = botUsername
            mentioned || replyToBot

/// Extract the text of the message being replied to (for context injection).
let private extractReplyContext (msg: TgMessage) : string option =
    match msg.ReplyToMessage with
    | null -> None
    | reply ->
        let text =
            match reply.Text with
            | null ->
                match reply.Caption with
                | null -> ""
                | cap  -> cap
            | t -> t
        if String.IsNullOrWhiteSpace(text) then None
        else Some (sprintf "[Reply to: %s]" text)

// ═══════════════════════════════════════════════════════════════════════════
// § 4  Typing indicator loop
//
// Sends ChatAction.Typing every 4 s until the CancellationToken fires.
// Pure F# async — no Thread.Sleep, no Task.Run.
// ═══════════════════════════════════════════════════════════════════════════

let private startTypingLoop (bot: ITelegramBotClient) (chatId: int64) (ct: CancellationToken) : unit =
    Async.Start(async {
        while not ct.IsCancellationRequested do
            try
                do! bot.SendChatAction(TgChatId(chatId), ChatAction.Typing, cancellationToken = ct)
                    |> Async.AwaitTask |> Async.Ignore
            with _ -> ()
            do! Async.Sleep 4000
    }, ct)

// ═══════════════════════════════════════════════════════════════════════════
// § 5  Streaming output — delta handler and stream-end handler
// ═══════════════════════════════════════════════════════════════════════════

let private onStreamDelta
    (bot        : ITelegramBotClient)
    (tgCfg      : TelegramConfig)
    (chatId     : int64)
    (stateRef   : StreamState ref)
    (replyToRef : int option ref)
    (text       : string)
    : Async<unit> =
    async {
        match !stateRef with
        | NotStarted ->
            try
                let replyParams : ReplyParameters =
                    match !replyToRef with
                    | Some rid when tgCfg.ReplyToMessage -> ReplyParameters(MessageId = rid)
                    | _                                  -> Unchecked.defaultof<ReplyParameters>
                let! msg =
                    bot.SendMessage(TgChatId(chatId), text + "▌", replyParameters = replyParams)
                    |> Async.AwaitTask
                stateRef := Streaming (msg.MessageId, text, DateTimeOffset.UtcNow)
            with ex ->
                eprintfn "[Telegram] SendMessage (streaming) error: %s" ex.Message

        | Streaming (msgId, accumulated, lastEdit) ->
            let newAccumulated = accumulated + text
            let now     = DateTimeOffset.UtcNow
            let elapsed = now - lastEdit
            if elapsed >= tgCfg.StreamEditInterval then
                stateRef := Streaming (msgId, newAccumulated, now)
                try
                    do! bot.EditMessageText(TgChatId(chatId), msgId, newAccumulated + "▌")
                        |> Async.AwaitTask |> Async.Ignore
                with _ -> ()
            else
                stateRef := Streaming (msgId, newAccumulated, lastEdit)
    }

let private onStreamEnd
    (bot       : ITelegramBotClient)
    (tgCfg     : TelegramConfig)
    (chatId    : int64)
    (stateRef  : StreamState ref)
    (_hasTools : bool)
    : Async<unit> =
    async {
        match !stateRef with
        | NotStarted -> ()
        | Streaming (msgId, accumulated, _) ->
            let finalHtml = markdownToHtml accumulated
            let! success =
                async {
                    try
                        do! bot.EditMessageText(TgChatId(chatId), msgId, finalHtml,
                                parseMode = ParseMode.Html)
                            |> Async.AwaitTask |> Async.Ignore
                        return true
                    with _ -> return false
                }
            if not success then
                try
                    do! bot.EditMessageText(TgChatId(chatId), msgId, accumulated)
                        |> Async.AwaitTask |> Async.Ignore
                with _ -> ()

            match tgCfg.ReactEmoji with
            | Some emoji ->
                try
                    let reaction = ReactionTypeEmoji()
                    reaction.Emoji <- emoji
                    do! bot.SetMessageReaction(TgChatId(chatId), msgId,
                            [| reaction :> ReactionType |])
                        |> Async.AwaitTask |> Async.Ignore
                with _ -> ()
            | None -> ()

            stateRef := NotStarted
    }

// ═══════════════════════════════════════════════════════════════════════════
// § 6  File download helper
// ═══════════════════════════════════════════════════════════════════════════

let private downloadTelegramFile
    (bot        : ITelegramBotClient)
    (httpClient : HttpClient)
    (tokenStr   : string)
    (fileId     : string)
    : Async<LocalFilePath option> =
    async {
        try
            let! file = bot.GetFile(fileId) |> Async.AwaitTask
            let filePath = file.FilePath
            if String.IsNullOrEmpty(filePath) then return None
            else
                let url    = sprintf "https://api.telegram.org/file/bot%s/%s" tokenStr filePath
                let! bytes = httpClient.GetByteArrayAsync(url) |> Async.AwaitTask
                let ext    = IO.Path.GetExtension(filePath)
                let tmp    = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext)
                do! IO.File.WriteAllBytesAsync(tmp, bytes) |> Async.AwaitTask
                return Some (LocalFilePath.ofAbsolute tmp)   // temp path is absolute by construction
        with ex ->
            eprintfn "[Telegram] File download error (%s): %s" fileId ex.Message
            return None
    }

/// Download all media attachments from a Telegram message.
let private extractMedia
    (bot        : ITelegramBotClient)
    (httpClient : HttpClient)
    (tokenStr   : string)
    (msg        : TgMessage)
    : Async<MediaContent list> =
    async {
        let photoTask =
            match msg.Photo with
            | null -> None
            | photo when photo.Length = 0 -> None
            | photo ->
                let best = photo |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0L)
                Some (async {
                    let! path = downloadTelegramFile bot httpClient tokenStr (Unchecked.nonNull best.FileId)
                    return path |> Option.map ImageFile
                })

        let docTask =
            match msg.Document with
            | null -> None
            | doc ->
                let mime =
                    match doc.MimeType with
                    | null -> ""
                    | mt   -> mt
                let mapper : LocalFilePath -> MediaContent =
                    if   mime.StartsWith("image/") then ImageFile
                    elif mime.StartsWith("audio/") then AudioFile
                    elif mime.StartsWith("video/") then VideoFile
                    else DocumentFile
                Some (async {
                    let! path = downloadTelegramFile bot httpClient tokenStr (Unchecked.nonNull doc.FileId)
                    return path |> Option.map mapper
                })

        let audioTask =
            match msg.Audio with
            | null  -> None
            | audio ->
                Some (async {
                    let! path = downloadTelegramFile bot httpClient tokenStr (Unchecked.nonNull audio.FileId)
                    return path |> Option.map AudioFile
                })

        let videoTask =
            match msg.Video with
            | null  -> None
            | video ->
                Some (async {
                    let! path = downloadTelegramFile bot httpClient tokenStr (Unchecked.nonNull video.FileId)
                    return path |> Option.map VideoFile
                })

        let voiceTask =
            match msg.Voice with
            | null  -> None
            | voice ->
                Some (async {
                    let! path = downloadTelegramFile bot httpClient tokenStr (Unchecked.nonNull voice.FileId)
                    return path |> Option.map AudioFile
                })

        let tasks =
            [| photoTask; docTask; audioTask; videoTask; voiceTask |]
            |> Array.choose id

        let! results = tasks |> Async.Parallel
        return results |> Array.choose id |> Array.toList
    }

// ═══════════════════════════════════════════════════════════════════════════
// § 6b  Outbound media — send files/images/audio/video to Telegram
//
// Used by the MessageTool send callback when OutboundMessage has Attachments.
// InputFileStream(stream, fileName) passes the filename to Telegram, which
// auto-detects MIME type from the extension — avoids the application/octet-stream bug.
// ═══════════════════════════════════════════════════════════════════════════

/// Send a single MediaContent item to a Telegram chat.
let sendMediaContent
    (bot    : ITelegramBotClient)
    (chatId : int64)
    (media  : MediaContent)
    : Async<Result<unit, string>> =
    async {
        let path = match media with ImageFile p | AudioFile p | DocumentFile p | VideoFile p -> LocalFilePath.value p
        if not (IO.File.Exists path) then
            return Error $"File not found: {path}"
        else
            let fi = IO.FileInfo(path)
            // Telegram limits: 10 MB photos, 50 MB documents/audio/video
            let limitMb = match media with ImageFile _ -> 10L | _ -> 50L
            if fi.Length > limitMb * 1024L * 1024L then
                return Error $"File too large ({fi.Length / 1024L / 1024L} MB, limit {limitMb} MB): {path}"
            else
                try
                    use stream = IO.File.OpenRead(path)
                    let fileName = IO.Path.GetFileName(path)
                    let inputFile = Telegram.Bot.Types.InputFileStream(stream, fileName)
                    let tgChat = TgChatId(chatId)
                    match media with
                    | ImageFile _ ->
                        do! bot.SendPhoto(tgChat, inputFile) |> Async.AwaitTask |> Async.Ignore
                    | AudioFile _ ->
                        let ext = (IO.Path.GetExtension(path) |> Unchecked.nonNull).ToLowerInvariant()
                        if ext = ".ogg" then
                            do! bot.SendVoice(tgChat, inputFile) |> Async.AwaitTask |> Async.Ignore
                        else
                            do! bot.SendAudio(tgChat, inputFile) |> Async.AwaitTask |> Async.Ignore
                    | VideoFile _ ->
                        do! bot.SendVideo(tgChat, inputFile) |> Async.AwaitTask |> Async.Ignore
                    | DocumentFile _ ->
                        do! bot.SendDocument(tgChat, inputFile) |> Async.AwaitTask |> Async.Ignore
                    return Ok ()
                with ex ->
                    return Error $"Telegram send error: {ex.Message}"
    }

/// Send an OutboundMessage to Telegram — text content + file attachments.
let sendOutboundMessage
    (bot   : ITelegramBotClient)
    (tgCfg : TelegramConfig)
    (msg   : OutboundMessage)
    : Async<unit> =
    async {
        let (ChatId chatStr) = msg.Chat
        match Int64.TryParse(chatStr) with
        | false, _ ->
            eprintfn "[Telegram] Cannot parse chat ID '%s' as int64, skipping send" chatStr
        | true, chatId ->
            // Send text content
            if not (String.IsNullOrWhiteSpace msg.Content) then
                let html = markdownToHtml msg.Content
                try
                    do! bot.SendMessage(TgChatId(chatId), html, parseMode = ParseMode.Html)
                        |> Async.AwaitTask |> Async.Ignore
                with _ ->
                    try
                        do! bot.SendMessage(TgChatId(chatId), msg.Content)
                            |> Async.AwaitTask |> Async.Ignore
                    with ex -> eprintfn "[Telegram] SendMessage error: %s" ex.Message
            // Send attachments
            for media in msg.Attachments do
                match! sendMediaContent bot chatId media with
                | Ok ()      -> ()
                | Error desc -> eprintfn "[Telegram] %s" desc
    }

// ═══════════════════════════════════════════════════════════════════════════
// § 7  TelegramCoordinator
//
// Creates per-chat AgentDependencies with a chat-specific StreamingHook so
// that each chat gets its own StreamBuf and reply-to reference.
// ═══════════════════════════════════════════════════════════════════════════

type TelegramCoordinator(baseDeps: AgentDependencies, bot: ITelegramBotClient, tgCfg: TelegramConfig) =
    let actors       = ConcurrentDictionary<SessionId, MailboxProcessor<SessionActorMsg>>()
    let streamStates = ConcurrentDictionary<int64, StreamState ref>()
    let replyToRefs  = ConcurrentDictionary<int64, int option ref>()

    let makeStreamHook (chatId: int64) : AgentStreamHook =
        if not tgCfg.Streaming then NoStreaming
        else
            let stateRef   = streamStates.GetOrAdd(chatId,  fun _ -> ref NotStarted)
            let replyToRef = replyToRefs.GetOrAdd(chatId, fun _ -> ref None)
            StreamingHook(
                onDelta     = (fun text     -> onStreamDelta bot tgCfg chatId stateRef replyToRef text),
                onStreamEnd = (fun hasTools -> onStreamEnd   bot tgCfg chatId stateRef hasTools))

    member private _.GetOrCreate(sid: SessionId, chatId: int64) =
        actors.GetOrAdd(sid, fun _ ->
            let deps = { baseDeps with StreamHook = makeStreamHook chatId }
            createSessionActor sid deps)

    /// Set the Telegram message ID to reply to before routing the next message.
    member _.SetReplyTo(chatId: int64, replyTo: int option) : unit =
        let r = replyToRefs.GetOrAdd(chatId, fun _ -> ref None)
        r := replyTo

    /// Route one inbound domain message to its session actor.
    member this.Route(inbound: InboundMessage, chatId: int64) : Async<Result<AgentResult, AgentError>> =
        async {
            let sid   = sessionIdForTelegram chatId
            let actor = this.GetOrCreate(sid, chatId)
            let! result = actor.PostAndAsyncReply(fun ch -> ProcessInput(inbound, ch))
            match result with
            | Result.Ok (text, _) ->
                let agentResult =
                    if tgCfg.Streaming then StreamedResponse text
                    else PlainResponse text
                return Result.Ok agentResult
            | Result.Error e -> return Result.Error e
        }

    /// Expose the base config so that processMessage can render /status output.
    member _.Config = baseDeps.Config

    /// Force a memory consolidation for the session bound to a Telegram chat.
    member _.Consolidate(chatId: int64) : Async<Result<ConsolidationResult, AgentError>> =
        async {
            let sid = sessionIdForTelegram chatId
            match actors.TryGetValue(sid) with
            | false, _ ->
                return Result.Ok ConsolidationSkipped
            | true, actor ->
                return! actor.PostAndAsyncReply(fun ch -> RequestConsolidate ch)
        }

    member _.ShutdownAll() : unit =
        for kv in actors do kv.Value.Post Shutdown
        actors.Clear()

// ═══════════════════════════════════════════════════════════════════════════
// § 8  Media group buffering
// ═══════════════════════════════════════════════════════════════════════════

type private MediaGroupState = {
    mutable Media    : MediaContent list
    mutable Caption  : string
    mutable LastSeen : DateTimeOffset
    mutable Sender   : UserId
    mutable ChatId   : int64
}

// ═══════════════════════════════════════════════════════════════════════════
// § 9  Single-message processor
// ═══════════════════════════════════════════════════════════════════════════

let private processMessage
    (bot         : ITelegramBotClient)
    (httpClient  : HttpClient)
    (tokenStr    : string)
    (tgCfg       : TelegramConfig)
    (coordinator : TelegramCoordinator)
    (mediaGroups : ConcurrentDictionary<string, MediaGroupState>)
    (botUsername : string)
    (ct          : CancellationToken)
    (msg         : TgMessage)
    : Async<unit> =
    async {
        // Ignore non-user messages (channel posts, service messages)
        match msg.From with
        | null -> ()
        | from ->

        let sender = makeSenderId from
        let chatId = msg.Chat.Id

        // Allow-list check
        if not (AllowList.permits sender tgCfg.AllowFrom) then ()
        else

        // Group policy check
        if not (checkGroupPolicy tgCfg msg botUsername) then ()
        else

        // Parse message text
        let rawText =
            match msg.Text with
            | null ->
                match msg.Caption with
                | null -> ""
                | cap  -> cap
            | t -> t

        // Media group buffering — accumulate and let the flusher route them together
        let! continueProcessing =
            async {
                match msg.MediaGroupId with
                | null -> return true
                | mediaGroupId ->
                    let state =
                        mediaGroups.GetOrAdd(mediaGroupId, fun _ -> {
                            Media    = []
                            Caption  = rawText
                            LastSeen = DateTimeOffset.UtcNow
                            Sender   = sender
                            ChatId   = chatId
                        })
                    let! media = extractMedia bot httpClient tokenStr msg
                    lock state (fun () ->
                        state.Media    <- state.Media @ media
                        state.LastSeen <- DateTimeOffset.UtcNow
                        if String.IsNullOrEmpty(state.Caption) && not (String.IsNullOrEmpty(rawText)) then
                            state.Caption <- rawText)
                    return false
            }

        if not continueProcessing then ()
        else

        // Parse slash commands or chat message
        let input =
            match parseUserInput rawText with
            | Result.Ok v  -> v
            | Result.Error _ -> ChatMessage (rawText, [])

        match input with
        | Command StopProcessing ->
            do! bot.SendMessage(TgChatId(chatId), "Bye!") |> Async.AwaitTask |> Async.Ignore

        | Command Restart ->
            // Restarting the process from Telegram would affect the CLI session too —
            // surface a message so the operator can restart from the CLI instead.
            do! bot.SendMessage(TgChatId(chatId), "Use /restart from the CLI to restart the bot.")
                |> Async.AwaitTask |> Async.Ignore

        | Command ShowHelp ->
            let helpText =
                "Commands:\n/new              — Start a new conversation\n" +
                "/stop             — Exit\n/status           — Show configuration\n" +
                "/dream            — Consolidate memory and save a dream entry\n" +
                "/dream-log        — List all dream entries\n" +
                "/dream-log <sha>  — Show a specific dream entry\n" +
                "/dream-restore    — Restore context from latest dream entry\n" +
                "/dream-restore <sha> — Restore from a specific dream entry\n" +
                "/help             — Show this message"
            do! bot.SendMessage(TgChatId(chatId), helpText) |> Async.AwaitTask |> Async.Ignore

        | Command ShowStatus ->
            let c = coordinator.Config
            let ctxStr =
                if c.ContextWindowTokens > 0 then sprintf "%dk" (c.ContextWindowTokens / 1000)
                else "n/a"
            let text =
                sprintf "Model: %s\nProvider: %s\nTemperature: %.1f\nMax tokens: %d\nContext window: %s"
                    c.DefaultModel c.DefaultProvider c.Temperature c.MaxTokens ctxStr
            do! bot.SendMessage(TgChatId(chatId), text) |> Async.AwaitTask |> Async.Ignore

        | Command Dream ->
            let! result = coordinator.Consolidate(chatId)
            match result with
            | Result.Ok (Consolidated (summary, _, _)) ->
                let preview = if summary.Length > 300 then summary.[..299] + "…" else summary
                do! bot.SendMessage(TgChatId(chatId), sprintf "Memory consolidated:\n%s" preview)
                    |> Async.AwaitTask |> Async.Ignore
            | Result.Ok ConsolidationSkipped ->
                do! bot.SendMessage(TgChatId(chatId), "Not enough messages to consolidate yet.")
                    |> Async.AwaitTask |> Async.Ignore
            | Result.Error e ->
                do! bot.SendMessage(TgChatId(chatId), sprintf "⚠️ %A" e)
                    |> Async.AwaitTask |> Async.Ignore

        | Command (DreamLog shaOpt) ->
            let! logResult = loadDreamLog coordinator.Config.WorkspacePath
            match logResult with
            | Result.Error e ->
                do! bot.SendMessage(TgChatId(chatId), sprintf "[dream-log error] %s" e)
                    |> Async.AwaitTask |> Async.Ignore
            | Result.Ok entries ->
                let text =
                    match shaOpt with
                    | None ->
                        if entries.IsEmpty then "No dream entries yet."
                        else
                            let lines =
                                entries
                                |> List.map (fun e ->
                                    let dateStr  = e.OccurredAt.ToString("yyyy-MM-dd HH:mm")
                                    let preview  = if e.Summary.Length > 60 then e.Summary.[..59] + "…" else e.Summary
                                    sprintf "[%s] %s (%d msgs)\n  %s" e.Sha dateStr e.MessageCount preview)
                            sprintf "Dream log (%d entries):\n\n%s" entries.Length (String.concat "\n\n" lines)
                    | Some sha ->
                        match entries |> List.tryFind (fun e -> e.Sha.StartsWith(sha)) with
                        | None   -> sprintf "No dream entry matching '%s'." sha
                        | Some e ->
                            let dateStr = e.OccurredAt.ToString("o")
                            sprintf "[%s] %s (%d messages)\n\n%s" e.Sha dateStr e.MessageCount e.Summary
                // Telegram message limit is 4096 chars
                let capped = if text.Length > 4000 then text.[..3999] + "…" else text
                do! bot.SendMessage(TgChatId(chatId), capped) |> Async.AwaitTask |> Async.Ignore

        | Command (DreamRestore shaOpt) ->
            let! logResult = loadDreamLog coordinator.Config.WorkspacePath
            match logResult with
            | Result.Error e ->
                do! bot.SendMessage(TgChatId(chatId), sprintf "[dream-restore error] %s" e)
                    |> Async.AwaitTask |> Async.Ignore
            | Result.Ok entries ->
                let entryOpt =
                    match shaOpt with
                    | None     -> List.tryLast entries
                    | Some sha -> entries |> List.tryFind (fun e -> e.Sha.StartsWith(sha))
                match entryOpt with
                | None ->
                    let suffix = shaOpt |> Option.map (fun s -> sprintf " matching '%s'" s) |> Option.defaultValue ""
                    do! bot.SendMessage(TgChatId(chatId), sprintf "No dream entry found%s." suffix)
                        |> Async.AwaitTask |> Async.Ignore
                | Some entry ->
                    // Clear session then seed with dream summary
                    let makeInbound userInput =
                        { Channel            = ChannelId "telegram"
                          Sender             = sender
                          Chat               = BotSharp.Domain.Types.ChatId (sprintf "%d" chatId)
                          Input              = userInput
                          Metadata           = Map.empty
                          SessionKeyOverride = None }
                    let! _ = coordinator.Route(makeInbound (Command NewSession), chatId)
                    let dateStr  = entry.OccurredAt.ToString("yyyy-MM-dd")
                    let seedText = sprintf "[Restoring context from dream entry %s recorded on %s]\n\n%s" entry.Sha dateStr entry.Summary
                    coordinator.SetReplyTo(chatId, if tgCfg.ReplyToMessage then Some msg.MessageId else None)
                    let! seedResult = coordinator.Route(makeInbound (ChatMessage (seedText, [])), chatId)
                    match seedResult with
                    | Result.Ok (PlainResponse text) ->
                        let reply = sprintf "Restored from dream [%s].\n\n%s" entry.Sha text
                        do! bot.SendMessage(TgChatId(chatId), reply) |> Async.AwaitTask |> Async.Ignore
                    | Result.Ok (StreamedResponse _) ->
                        ()   // streaming already sent the response
                    | Result.Error e ->
                        do! bot.SendMessage(TgChatId(chatId), sprintf "[dream-restore error] %A" e)
                            |> Async.AwaitTask |> Async.Ignore

        | Command NewSession
        | ChatMessage _ ->
            let! media = extractMedia bot httpClient tokenStr msg

            // Inject reply context into the text
            let replyCtx = extractReplyContext msg
            let fullText =
                match replyCtx with
                | Some ctx -> ctx + "\n" + rawText
                | None     -> rawText

            let finalInput =
                match input with
                | Command NewSession -> Command NewSession
                | _                  -> ChatMessage (fullText, media)

            let inbound : InboundMessage = {
                Channel            = ChannelId "telegram"
                Sender             = sender
                Chat               = BotSharp.Domain.Types.ChatId (sprintf "%d" chatId)
                Input              = finalInput
                Metadata           = Map.empty
                SessionKeyOverride = None
            }

            // Set reply-to before routing (streaming hook reads it on first delta)
            let replyTo = if tgCfg.ReplyToMessage then Some msg.MessageId else None
            coordinator.SetReplyTo(chatId, replyTo)

            // Typing indicator
            use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            startTypingLoop bot chatId cts.Token

            // Route
            let! result = coordinator.Route(inbound, chatId)

            cts.Cancel()

            // Send reply for non-streaming path
            match result with
            | Result.Ok (PlainResponse text) when not (String.IsNullOrWhiteSpace(text)) ->
                let html = markdownToHtml text
                let replyParams : ReplyParameters =
                    match replyTo with
                    | Some rid -> ReplyParameters(MessageId = rid)
                    | None     -> Unchecked.defaultof<ReplyParameters>
                let! success =
                    async {
                        try
                            do! bot.SendMessage(TgChatId(chatId), html,
                                    parseMode       = ParseMode.Html,
                                    replyParameters = replyParams)
                                |> Async.AwaitTask |> Async.Ignore
                            return true
                        with _ -> return false
                    }
                if not success then
                    try
                        do! bot.SendMessage(TgChatId(chatId), text, replyParameters = replyParams)
                            |> Async.AwaitTask |> Async.Ignore
                    with ex ->
                        eprintfn "[Telegram] SendMessage error: %s" ex.Message

            | Result.Ok (PlainResponse _)    -> ()   // empty — nothing to send
            | Result.Ok (StreamedResponse _) -> ()   // already displayed via streaming hook

            | Result.Error e ->
                try
                    do! bot.SendMessage(TgChatId(chatId), sprintf "⚠️ Error: %A" e)
                        |> Async.AwaitTask |> Async.Ignore
                with _ -> ()
    }

// ═══════════════════════════════════════════════════════════════════════════
// § 10  Media-group flush loop
//
// Every 200 ms: flush groups idle > 600 ms by routing them as a ChatMessage
// with all accumulated media files.
// ═══════════════════════════════════════════════════════════════════════════

let private mediaGroupFlusher
    (coordinator : TelegramCoordinator)
    (mediaGroups : ConcurrentDictionary<string, MediaGroupState>)
    (ct          : CancellationToken)
    : Async<unit> =
    let rec loop () = async {
        if ct.IsCancellationRequested then ()
        else
            let now   = DateTimeOffset.UtcNow
            let stale =
                mediaGroups
                |> Seq.filter (fun kv -> (now - kv.Value.LastSeen).TotalSeconds >= 0.6)
                |> Seq.toList

            for kv in stale do
                let mutable removed = Unchecked.defaultof<MediaGroupState>
                if mediaGroups.TryRemove(kv.Key, &removed) then
                    let state  = removed
                    let chatId = state.ChatId

                    let inbound : InboundMessage = {
                        Channel            = ChannelId "telegram"
                        Sender             = state.Sender
                        Chat               = BotSharp.Domain.Types.ChatId (sprintf "%d" chatId)
                        Input              = ChatMessage (state.Caption, state.Media)
                        Metadata           = Map.empty
                        SessionKeyOverride = None
                    }

                    coordinator.SetReplyTo(chatId, None)

                    Async.Start(async {
                        let! _ = coordinator.Route(inbound, chatId)
                        return ()
                    }, ct)

            do! Async.Sleep 200
            return! loop ()
    }
    loop ()

// ═══════════════════════════════════════════════════════════════════════════
// § 11  Long-poll loop
//
// GetUpdates with 30-second server-side timeout; dispatches each message
// concurrently via Async.Start + error guard.
// ═══════════════════════════════════════════════════════════════════════════

let private pollLoop
    (bot         : ITelegramBotClient)
    (httpClient  : HttpClient)
    (tokenStr    : string)
    (tgCfg       : TelegramConfig)
    (coordinator : TelegramCoordinator)
    (mediaGroups : ConcurrentDictionary<string, MediaGroupState>)
    (botUsername : string)
    (ct          : CancellationToken)
    : Async<unit> =
    let rec loop (offset: int) = async {
        if ct.IsCancellationRequested then ()
        else
            let! updates =
                async {
                    try
                        let! arr =
                            bot.GetUpdates(offset = offset, limit = 100, timeout = 30,
                                           cancellationToken = ct)
                            |> Async.AwaitTask
                        return Array.toList arr
                    with ex ->
                        if not ct.IsCancellationRequested then
                            eprintfn "[Telegram] GetUpdates error: %s" ex.Message
                            do! Async.Sleep 5000
                        return []
                }

            let nextOffset =
                if updates.IsEmpty then offset
                else (updates |> List.map (fun u -> u.Id) |> List.max) + 1

            for update in updates do
                match update.Message with
                | null -> ()
                | msg ->
                    Async.Start(async {
                        try
                            do! processMessage bot httpClient tokenStr tgCfg coordinator
                                    mediaGroups botUsername ct msg
                        with ex ->
                            eprintfn "[Telegram] Unhandled error: %s" ex.Message
                    }, ct)

            return! loop nextOffset
    }
    loop 0

// ═══════════════════════════════════════════════════════════════════════════
// § 12  Public entry point
//
// Creates the TelegramBotClient, resolves the bot username, starts the
// media-group flusher and the poll loop concurrently.
// ═══════════════════════════════════════════════════════════════════════════

let startTelegram
    (tgCfg      : TelegramConfig)
    (baseDeps   : AgentDependencies)
    (httpClient : HttpClient)
    (ct         : CancellationToken)
    (onBotReady : ITelegramBotClient -> TelegramConfig -> unit)
    : Async<unit> =
    async {
        let tokenStr = TelegramBotToken.value tgCfg.Token

        let bot : ITelegramBotClient =
            match tgCfg.Proxy with
            | None ->
                TelegramBotClient(tokenStr, httpClient) :> ITelegramBotClient
            | Some proxy ->
                let handler    = new HttpClientHandler(Proxy = Net.WebProxy(proxy))
                let proxyClient = new HttpClient(handler)
                TelegramBotClient(tokenStr, proxyClient) :> ITelegramBotClient

        let! me = bot.GetMe() |> Async.AwaitTask
        let botUsername =
            match me.Username with
            | null -> ""
            | u    -> u
        printfn "[Telegram] Bot @%s is running." botUsername
        onBotReady bot tgCfg

        let coordinator = TelegramCoordinator(baseDeps, bot, tgCfg)
        let mediaGroups = ConcurrentDictionary<string, MediaGroupState>()

        do! Async.Parallel [|
                mediaGroupFlusher coordinator mediaGroups ct
                pollLoop bot httpClient tokenStr tgCfg coordinator mediaGroups botUsername ct
            |] |> Async.Ignore

        coordinator.ShutdownAll()
    }
