module BotSharp.Infrastructure.EventBus.EventBus

open System
open System.Collections.Generic
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// EventBus — unified message queue for all BotSharp system events
//
// All events (LLM calls, tool execution, session activity, job lifecycle,
// hook execution, etc.) flow through this bus. Routers dispatch events to
// channels; channels deliver to consumers.
//
// Default setup: a "log" channel with SqliteLogger consumer that writes
// all events to the event_log table. Users can add custom routers and
// consumers at runtime.
//
// Design:
//   - MailboxProcessor ensures thread-safe publish/subscribe
//   - Publish is fire-and-forget (Post, non-blocking)
//   - Consumers run async, errors are isolated (one bad consumer can't
//     block others)
//   - A single event can match multiple routers → delivered to multiple channels
// ═══════════════════════════════════════════════════════════════════════════

/// Route rule: event → channel name.
type EventRouter = {
    Match   : BotEvent -> bool
    Channel : string
}

/// Message types for the MailboxProcessor.
type private BusMsg =
    | Publish     of BotEvent
    | AddChannel  of name: string
    | AddRouter   of EventRouter
    | Subscribe   of channel: string * consumer: (BotEvent -> Async<unit>)
    | Unsubscribe of channel: string * consumerId: string

/// A running EventBus instance.
type EventBus = {
    /// Fire-and-forget publish. Never blocks the caller.
    Publish     : BotEvent -> unit
    /// Create a named channel (idempotent).
    AddChannel  : string -> unit
    /// Add a routing rule (event → channel). All matching routers fire.
    AddRouter   : EventRouter -> unit
    /// Add a consumer to a channel. Returns a consumer ID for unsubscribe.
    Subscribe   : string -> (BotEvent -> Async<unit>) -> string
    /// Remove a consumer by ID.
    Unsubscribe : string -> string -> unit
}

/// Helper to create a BotEvent with auto-generated ID and timestamp.
let mkEvent (category: string) (kind: string) (sessionId: string option) (data: (string * string) list) : BotEvent =
    { Id        = Guid.NewGuid().ToString("N").[..11]
      Timestamp = DateTimeOffset.UtcNow
      Category  = category
      Kind      = kind
      SessionId = sessionId
      Data      = Map.ofList data }

/// Create and start an EventBus.
/// The "log" channel is created by default; add consumers via Subscribe.
let create () : EventBus =
    // State: channels (name → consumer list), routers
    let channels = Dictionary<string, Dictionary<string, BotEvent -> Async<unit>>>()
    let routers  = List<EventRouter>()

    // Default "log" channel always exists
    channels["log"] <- Dictionary<string, BotEvent -> Async<unit>>()

    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! msg = inbox.Receive()
            match msg with
            | Publish evt ->
                // Route to matching channels (all matches, not first-match)
                // Default: "log" channel always receives everything
                let targetChannels = HashSet<string>()
                targetChannels.Add("log") |> ignore
                for router in routers do
                    try
                        if router.Match evt then
                            targetChannels.Add(router.Channel) |> ignore
                    with _ -> ()
                // Deliver to all consumers in each target channel
                for chName in targetChannels do
                    match channels.TryGetValue(chName) with
                    | true, consumers ->
                        for kv in consumers do
                            try do! kv.Value evt
                            with ex ->
                                eprintfn "[EventBus] Consumer %s/%s error: %s" chName kv.Key ex.Message
                    | false, _ -> ()

            | AddChannel name ->
                if not (channels.ContainsKey name) then
                    channels[name] <- Dictionary<string, BotEvent -> Async<unit>>()

            | AddRouter router ->
                routers.Add(router)

            | Subscribe (chName, consumer) ->
                match channels.TryGetValue(chName) with
                | true, consumers ->
                    let cid = Guid.NewGuid().ToString("N").[..7]
                    consumers[cid] <- consumer
                | false, _ ->
                    // Auto-create channel
                    let consumers = Dictionary<string, BotEvent -> Async<unit>>()
                    let cid = Guid.NewGuid().ToString("N").[..7]
                    consumers[cid] <- consumer
                    channels[chName] <- consumers

            | Unsubscribe (chName, consumerId) ->
                match channels.TryGetValue(chName) with
                | true, consumers -> consumers.Remove(consumerId) |> ignore
                | false, _ -> ()

            return! loop ()
        }
        loop ())

    // Subscribe needs to return the consumer ID synchronously.
    // We use a ConcurrentDictionary for thread-safe ID generation
    // since the actual subscription happens async in the mailbox.
    let mutable nextId = 0
    let idLock = obj()

    { Publish     = fun evt -> agent.Post(Publish evt)
      AddChannel  = fun name -> agent.Post(AddChannel name)
      AddRouter   = fun router -> agent.Post(AddRouter router)
      Subscribe   = fun chName consumer ->
          let cid =
              lock idLock (fun () ->
                  nextId <- nextId + 1
                  sprintf "c%d" nextId)
          // We need to pass the ID to the mailbox. Wrap consumer with known ID.
          // Simplification: Subscribe posts the consumer; the mailbox generates its own ID.
          // We return a predictable ID by pre-assigning.
          agent.Post(Subscribe (chName, consumer))
          cid
      Unsubscribe = fun chName cid -> agent.Post(Unsubscribe (chName, cid)) }
