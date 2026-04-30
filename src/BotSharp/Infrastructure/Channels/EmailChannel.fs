module BotSharp.Infrastructure.Channels.EmailChannel

open System
open System.Collections.Generic
open System.Threading
open MailKit
open MailKit.Net.Imap
open MailKit.Net.Smtp
open MailKit.Security
open MimeKit
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Email channel using MailKit (IMAP polling + SMTP replies)
//
// Inbound: polls IMAP mailbox for unread messages on a timer.
// Outbound: sends replies via SMTP back to the sender address.
//
// Uses MailKit NuGet package — the standard .NET mail library.
//
// Config:
//   "email": {
//     "imap_host": "imap.gmail.com", "imap_port": 993, "imap_use_ssl": true,
//     "smtp_host": "smtp.gmail.com", "smtp_port": 587, "smtp_use_tls": true,
//     "username": "bot@example.com", "password": "app-password",
//     "poll_interval_seconds": 30,
//     "allow_from": ["*"]
//   }
// ═══════════════════════════════════════════════════════════════════════════

// ── Configuration ────────────────────────────────────────────────────────

type EmailConfig = {
    ImapHost    : string
    ImapPort    : int
    ImapUseSsl  : bool
    SmtpHost    : string
    SmtpPort    : int
    SmtpUseTls  : bool
    Username    : string
    Password    : string
    PollSeconds : int
    AllowFrom   : AllowList
}

// ── Server ───────────────────────────────────────────────────────────────

type EmailServer(coordinator: AgentCoordinator, config: EmailConfig) =
    let mutable running = true
    let processedUids = HashSet<uint32>()
    let lastSubjects = Dictionary<string, string>()

    let sendReply (toAddr: string) (text: string) : unit =
        try
            let subject =
                match lastSubjects.TryGetValue(toAddr) with
                | true, s -> if s.StartsWith("Re:") then s else $"Re: {s}"
                | _ -> "BotSharp Reply"
            let msg = new MimeMessage()
            msg.From.Add(MailboxAddress(null, config.Username))
            msg.To.Add(MailboxAddress(null, toAddr))
            msg.Subject <- subject
            msg.Body <- new TextPart("plain", Text = text)

            use smtp = new SmtpClient()
            let sslOpt = if config.SmtpUseTls then SecureSocketOptions.StartTls else SecureSocketOptions.Auto
            smtp.Connect(config.SmtpHost, config.SmtpPort, sslOpt)
            smtp.Authenticate(config.Username, config.Password)
            smtp.Send(msg)
            smtp.Disconnect(true)
        with ex ->
            eprintfn "[Email] SMTP send error to %s: %s" toAddr ex.Message

    let pollAndProcess () : Async<unit> =
        async {
            try
                use imap = new ImapClient()
                let sslOpt = if config.ImapUseSsl then SecureSocketOptions.SslOnConnect else SecureSocketOptions.Auto
                imap.Connect(config.ImapHost, config.ImapPort, sslOpt)
                imap.Authenticate(config.Username, config.Password)
                imap.Inbox.Open(FolderAccess.ReadWrite) |> ignore

                let uids = imap.Inbox.Search(MailKit.Search.SearchQuery.NotSeen)
                for uid in uids do
                    if processedUids.Contains(uid.Id) then ()
                    else
                        processedUids.Add(uid.Id) |> ignore
                        // Cap dedup set
                        if processedUids.Count > 10000 then
                            processedUids.Clear()

                        let msg = imap.Inbox.GetMessage(uid)
                        let fromAddr =
                            match msg.From |> Seq.tryHead with
                            | Some (:? MailboxAddress as mb) -> mb.Address
                            | _ -> ""
                        let subject = msg.Subject |> Option.ofObj |> Option.defaultValue ""
                        let body = msg.TextBody |> Option.ofObj |> Option.defaultValue ""

                        if fromAddr = "" || String.IsNullOrWhiteSpace body then ()
                        elif not (AllowList.permits (UserId fromAddr) config.AllowFrom) then ()
                        else

                        if subject <> "" then lastSubjects.[fromAddr] <- subject

                        // Mark as seen
                        imap.Inbox.AddFlags(uid, MessageFlags.Seen, true)

                        // Route to agent
                        Async.Start(async {
                            let inbound : InboundMessage = {
                                Channel            = ChannelId "email"
                                Sender             = UserId fromAddr
                                Chat               = ChatId fromAddr
                                Input              = ChatMessage (body.Trim(), [])
                                Metadata           = Map.ofList [ "subject", subject ]
                                SessionKeyOverride = None
                            }
                            let! result = coordinator.Route inbound
                            match result with
                            | Result.Ok (PlainResponse t) | Result.Ok (StreamedResponse t) when not (String.IsNullOrWhiteSpace t) ->
                                sendReply fromAddr t
                            | Result.Error e ->
                                sendReply fromAddr $"Error: {e}"
                            | _ -> ()
                        })

                imap.Disconnect(true)
            with ex ->
                eprintfn "[Email] IMAP poll error: %s" ex.Message
        }

    member _.Start() : Async<unit> =
        async {
            printfn "[Email] Starting (IMAP: %s:%d, SMTP: %s:%d, poll every %ds)"
                config.ImapHost config.ImapPort config.SmtpHost config.SmtpPort config.PollSeconds

            while running do
                do! pollAndProcess ()
                do! Async.Sleep (config.PollSeconds * 1000)
        }

    member _.Stop() =
        running <- false
