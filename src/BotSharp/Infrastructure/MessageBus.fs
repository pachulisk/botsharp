module BotSharp.Infrastructure.MessageBus

open System
open System.Threading.Channels
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Channels.ChannelBase
open BotSharp.Infrastructure.Storage.DreamStore
open BotSharp.Application.AgentLoop
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Message bus
//
// Provides two unbounded in-process queues:
//   inbound  — InboundMessages enqueued by channel adapters
//   outbound — OutboundMessages produced by the agent and dispatched back
//
// StartProcessing: dequeues InboundMessages, routes via AgentCoordinator,
//                  enqueues the reply as an OutboundMessage.
// StartDispatching: dequeues OutboundMessages, sends via ChannelPort.Send.
//
// Both loops run until Close() is called and the queues drain.
//
// Note: The bus is designed for multi-channel scenarios (webhooks, WebSocket).
// For the CLI, use startCli directly — it runs a sequential request/response
// loop that structurally prevents "you>" from appearing before the response.
// ═══════════════════════════════════════════════════════════════════════════

type MessageBus(coordinator: AgentCoordinator, port: ChannelPort) =
    let inbound  : Channel<InboundMessage>  = Channel.CreateUnbounded()
    let outbound : Channel<OutboundMessage> = Channel.CreateUnbounded()

    /// Enqueue an inbound message for processing (non-blocking).
    member _.Enqueue(msg: InboundMessage) : unit =
        inbound.Writer.TryWrite(msg) |> ignore

    /// Start the processing loop: route inbound → agent → outbound.
    /// AgentResult is parsed at the coordinator boundary:
    ///   PlainResponse    → enqueue for display via port.Send
    ///   StreamedResponse → already shown; enqueue nothing
    member _.StartProcessing() : Async<unit> =
        let rec loop () = async {
            let! canRead =
                inbound.Reader.WaitToReadAsync().AsTask() |> Async.AwaitTask
            if canRead then
                let ok, msg = inbound.Reader.TryRead()
                if ok then
                    let! result = coordinator.Route msg
                    match result with
                    | Result.Ok (PlainResponse text) ->
                        let reply : OutboundMessage = {
                            Channel     = msg.Channel
                            Chat        = msg.Chat
                            Content     = text
                            ReplyTo     = None
                            Attachments = []
                            Buttons     = []
                        }
                        outbound.Writer.TryWrite(reply) |> ignore
                    | Result.Ok (StreamedResponse _) ->
                        ()   // text was already printed via OnDelta; nothing to dispatch
                    | Result.Error e ->
                        let reply : OutboundMessage = {
                            Channel     = msg.Channel
                            Chat        = msg.Chat
                            Content     = sprintf "[error] %A" e
                            ReplyTo     = None
                            Attachments = []
                            Buttons     = []
                        }
                        outbound.Writer.TryWrite(reply) |> ignore
                return! loop ()
        }
        loop ()

    /// Start the dispatching loop: send outbound messages via the channel port.
    member _.StartDispatching() : Async<unit> =
        let rec loop () = async {
            let! canRead =
                outbound.Reader.WaitToReadAsync().AsTask() |> Async.AwaitTask
            if canRead then
                let ok, msg = outbound.Reader.TryRead()
                if ok then
                    do! port.Send msg
                return! loop ()
        }
        loop ()

    /// Complete both channel writers (graceful shutdown).
    /// Existing messages in the queues are still processed before the loops exit.
    member _.Close() : unit =
        inbound.Writer.TryComplete()  |> ignore
        outbound.Writer.TryComplete() |> ignore

// ═══════════════════════════════════════════════════════════════════════════
// CLI sequential loop
//
// Parse pipeline:  stdin → ReceiveResult → InboundMessage → AgentResult → display → repeat
//
// Sequential by construction: port.Receive (which prints "you>") is only
// called after the previous AgentResult has been fully handled.  It is
// structurally impossible for "you>" to appear before the response.
//
// ReceiveResult cases:
//   ChannelClosed — EOF (Ctrl-D); loop stops.
//   NoMessage     — transient empty poll; loop continues (CLI never produces this).
//   Message msg   — a new user turn; route through coordinator.
//
// AgentResult cases:
//   PlainResponse text  → print with "assistant>" prefix
//   StreamedResponse _  → text was already emitted via OnDelta; do nothing
// ═══════════════════════════════════════════════════════════════════════════

/// Sequential CLI request/response loop.
/// The `deps` parameter gives access to Config (for /status) and the
/// workspace path (for /dream, /dream-log).
let startCli (coordinator: AgentCoordinator) (port: ChannelPort) (deps: AgentDependencies) : Async<unit> =
    let rec loop () = async {
        let! received = port.Receive
        match received with
        | ChannelClosed ->
            ()   // EOF — stop

        | NoMessage ->
            return! loop ()   // transient empty poll — continue (CLI never produces this)

        | Message msg ->
            match msg.Input with

            | Command StopProcessing ->
                printfn "Bye!"

            | Command Restart ->
                printfn "Restarting..."
                let exe  = Environment.GetCommandLineArgs().[0]
                let args = Environment.GetCommandLineArgs().[1..] |> String.concat " "
                Diagnostics.Process.Start(exe, args) |> ignore
                Environment.Exit(0)

            | Command ShowHelp ->
                printfn """
Commands:
  /new              — Start a new conversation (archives history)
  /clear            — Clear history without archiving
  /history [n]      — Show last n messages (default 10)
  /stop             — Exit
  /restart          — Restart the process
  /status           — Show current configuration
  /dream            — Consolidate memory and save a dream entry
  /dream-log        — List all dream entries
  /dream-log <sha>  — Show a specific dream entry
  /dream-restore    — Restore session context from a dream snapshot (latest)
  /dream-restore <sha> — Restore from a specific dream entry
  /help             — Show this message"""
                return! loop ()

            | Command ShowStatus ->
                let c = deps.Config
                let proc    = Diagnostics.Process.GetCurrentProcess()
                let uptimeS = int (DateTimeOffset.UtcNow - DateTimeOffset.Parse(proc.StartTime.ToUniversalTime().ToString("o"))).TotalSeconds
                let uptimeStr =
                    if uptimeS >= 3600 then sprintf "%dh %dm" (uptimeS / 3600) ((uptimeS % 3600) / 60)
                    else sprintf "%dm %ds" (uptimeS / 60) (uptimeS % 60)
                let sid = sessionId msg
                let! snapOpt  = coordinator.GetSnapshot(sid)
                let! usageOpt = coordinator.GetLastUsage(sid)
                let msgCount = snapOpt |> Option.map SessionSnapshot.messageCount |> Option.defaultValue 0
                let ctxStr =
                    if c.ContextWindowTokens > 0 then sprintf "%dk" (c.ContextWindowTokens / 1000)
                    else "auto"
                printfn "\nStatus:"
                printfn "  Model:          %s"  c.DefaultModel
                printfn "  Provider:       %s"  c.DefaultProvider
                printfn "  Workspace:      %s"  c.WorkspacePath
                printfn "  Temperature:    %.1f" c.Temperature
                printfn "  Max tokens:     %d"  c.MaxTokens
                printfn "  Context window: %s tokens" ctxStr
                printfn "  Memory window:  %d messages" c.MemoryWindowSize
                printfn "  Max iterations: %d"  c.MaxIterations
                printfn "  Session:        %d messages" msgCount
                printfn "  Uptime:         %s" uptimeStr
                match usageOpt with
                | Some u -> printfn "  Last call:      %s" (TokenUsage.formatUsage u)
                | None   -> ()
                return! loop ()

            // /clear and /history are routed to the coordinator (handled in SessionActor)
            | Command ClearHistory | Command (ShowHistory _) ->
                let! result = coordinator.Route msg
                match result with
                | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) ->
                    if text <> "" then printfn "\n%s" text
                | Result.Error e ->
                    printfn "\nError: %A" e
                return! loop ()

            | Command Dream ->
                let sid = sessionId msg
                let! snapOpt = coordinator.GetSnapshot(sid)
                match snapOpt with
                | None ->
                    printfn "\nNo active session to consolidate."
                | Some _ ->
                    let! result = coordinator.Consolidate(sid)
                    match result with
                    | Result.Ok (Consolidated (summary, _, newIdx)) ->
                        let sha   = makeSha (DateTimeOffset.UtcNow.ToString("o") + summary)
                        let entry = {
                            Sha          = sha
                            OccurredAt   = DateTimeOffset.UtcNow
                            Summary      = summary
                            MessageCount = newIdx
                        }
                        let! saved = appendDreamEntry deps.Config.WorkspacePath entry
                        match saved with
                        | Result.Ok () ->
                            let preview = if summary.Length > 200 then summary.[..199] + "…" else summary
                            printfn "\nDream saved [%s]\n%s" sha preview
                        | Result.Error e ->
                            printfn "\nConsolidated but could not save dream: %s" e
                    | Result.Ok ConsolidationSkipped ->
                        printfn "\nNot enough messages to consolidate (need ≥ %d unconsolidated)."
                            deps.Config.MemoryWindowSize
                    | Result.Error e ->
                        printfn "\n[dream error] %A" e
                return! loop ()

            | Command (DreamLog shaOpt) ->
                let! result = loadDreamLog deps.Config.WorkspacePath
                match result with
                | Result.Error e -> printfn "\n[dream-log error] %s" e
                | Result.Ok entries ->
                    match shaOpt with
                    | None ->
                        if entries.IsEmpty then printfn "\nNo dream entries yet."
                        else
                            printfn "\nDream log (%d entries):" entries.Length
                            for e in entries do
                                printfn "  [%s] %s  (%d msgs)"
                                    e.Sha
                                    (e.OccurredAt.ToString("yyyy-MM-dd HH:mm"))
                                    e.MessageCount
                                let preview = if e.Summary.Length > 80 then e.Summary.[..79] + "…" else e.Summary
                                printfn "    %s" preview
                    | Some sha ->
                        match entries |> List.tryFind (fun e -> e.Sha.StartsWith(sha)) with
                        | None   -> printfn "\nNo dream entry matching '%s'." sha
                        | Some e ->
                            printfn "\n[%s] %s (%d messages)\n%s"
                                e.Sha (e.OccurredAt.ToString("o")) e.MessageCount e.Summary
                return! loop ()

            | Command (DreamRestore shaOpt) ->
                let! logResult = loadDreamLog deps.Config.WorkspacePath
                match logResult with
                | Result.Error e ->
                    printfn "\n[dream-restore error] %s" e
                | Result.Ok entries ->
                    let entryOpt =
                        match shaOpt with
                        | None     -> List.tryLast entries
                        | Some sha -> entries |> List.tryFind (fun e -> e.Sha.StartsWith(sha))
                    match entryOpt with
                    | None ->
                        let target = shaOpt |> Option.map (fun s -> $" matching '{s}'") |> Option.defaultValue ""
                        printfn "\nNo dream entry found%s." target
                    | Some entry ->
                        // Clear the current session, then seed the new one with the dream summary.
                        let! _ = coordinator.Route { msg with Input = Command NewSession }
                        let dateStr  = entry.OccurredAt.ToString("yyyy-MM-dd")
                        let seedText = sprintf "[Restoring context from dream entry %s recorded on %s]\n\n%s" entry.Sha dateStr entry.Summary
                        let! seedResult = coordinator.Route { msg with Input = ChatMessage (seedText, []) }
                        match seedResult with
                        | Result.Ok (PlainResponse text) ->
                            printfn "\nRestored from dream [%s].\nassistant> %s" entry.Sha text
                        | Result.Ok (StreamedResponse _) ->
                            printfn "\nRestored from dream [%s]." entry.Sha
                        | Result.Error e ->
                            printfn "\n[dream-restore error] %A" e
                return! loop ()

            | Command NewSession
            | ChatMessage _ ->
                let! result = coordinator.Route msg
                match result with
                | Result.Ok (PlainResponse text) ->
                    printfn "\nassistant> %s" text
                | Result.Ok (StreamedResponse _) ->
                    ()   // streaming already showed the text via OnDelta
                | Result.Error e ->
                    printfn "\n[error] %A" e
                return! loop ()
    }
    loop ()
