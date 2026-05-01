module BotSharp.Infrastructure.Channels.PocketChannel

open System
open System.IO
open System.Net.Sockets
open System.Text
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Channels.ChannelBase
open BotSharp.Infrastructure.Input.InputParser
open BotSharp.Infrastructure.Tools.ToolHints
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// PocketChannel — connects BotSharp to the botx-pocket HostBridge
//
// Replaces botx as the primary agent in the pocket mobile container.
// Communicates via Unix domain socket with line-delimited JSON-RPC 2.0.
//
// Speaks the same protocol as botx's PocketChannel (channels.hpp):
//   • chat.register  — register identity with HostBridge
//   • chat.poll      — pull user messages from the UI
//   • chat.send      — push replies (complete or streaming deltas)
//   • chat.activity  — report agent phase (thinking/tool_start/tool_end)
//   • chat.participants — query registered agents
//
// Transport: Unix domain socket (abstract namespace on Android via @ prefix)
// ═══════════════════════════════════════════════════════════════════════════

// ── Configuration ────────────────────────────────────────────────────────────

type PocketChannelConfig = {
    SocketName      : string
    AgentId         : string
    DisplayName     : string
    PollTimeoutMs   : int
    Capabilities    : string list
}

module PocketChannelConfig =
    let defaults = {
        SocketName      = ""
        AgentId         = "botsharp"
        DisplayName     = "BotSharp"
        PollTimeoutMs   = 5000
        Capabilities    = ["chat"; "evaluate"; "delegate"]
    }

// ── JSON-RPC client ──────────────────────────────────────────────────────────

type PocketRpcClient(socketName: string) =
    let mutable idCounter = 0
    let sem = new Threading.SemaphoreSlim(1, 1)

    // Socket + reader/writer — lazy-connected
    let mutable socket : Socket option = None
    let mutable reader : StreamReader option = None
    let mutable writer : StreamWriter option = None

    let nextId () =
        let id = Threading.Interlocked.Increment(&idCounter)
        string id

    let ensureConnected () =
        match socket with
        | Some s when s.Connected -> ()
        | _ ->
            // Clean up old connection
            writer |> Option.iter (fun w -> try w.Dispose() with _ -> ())
            reader |> Option.iter (fun r -> try r.Dispose() with _ -> ())
            socket |> Option.iter (fun s -> try s.Dispose() with _ -> ())

            let s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            // Abstract namespace: prefix with \0 for Android/Linux
            let effectivePath =
                if socketName.StartsWith("@") then "\x00" + socketName.[1..]
                else socketName
            let endpoint = UnixDomainSocketEndPoint(effectivePath)
            s.Connect(endpoint)
            let stream = new NetworkStream(s, ownsSocket = false)
            let utf8NoBom = new UTF8Encoding(false)  // no BOM — critical for JSON-RPC line protocol
            let r = new StreamReader(stream, utf8NoBom)
            let w = new StreamWriter(stream, utf8NoBom)
            w.NewLine <- "\n"
            socket <- Some s
            reader <- Some r
            writer <- Some w

    /// Send a JSON-RPC request and read the response line.
    member _.Request(method: string, paramsJson: string) : Async<JsonElement> =
        async {
            do! sem.WaitAsync() |> Async.AwaitTask
            try
                ensureConnected ()
                let id = nextId ()
                let line =
                    if String.IsNullOrEmpty paramsJson || paramsJson = "{}" then
                        sprintf """{"jsonrpc":"2.0","method":"%s","id":"%s"}""" method id
                    else
                        sprintf """{"jsonrpc":"2.0","method":"%s","id":"%s","params":%s}""" method id paramsJson
                let w = writer.Value
                do! w.WriteLineAsync(line) |> Async.AwaitTask
                do! w.FlushAsync() |> Async.AwaitTask

                // Read lines until we get a response (skip notifications)
                let rec readResponse () = async {
                    let! responseLine = reader.Value.ReadLineAsync() |> Async.AwaitTask
                    match responseLine with
                    | null -> return failwith "HostBridge closed connection"
                    | line ->
                        use doc = JsonDocument.Parse(line)
                        let root = doc.RootElement.Clone()
                        match root.TryGetProperty("id") with
                        | true, _ ->
                            // Check for error
                            match root.TryGetProperty("error") with
                            | true, err ->
                                let msg =
                                    match err.TryGetProperty("message") with
                                    | true, m -> m.GetString() |> Option.ofObj |> Option.defaultValue "unknown"
                                    | _ -> "unknown"
                                return failwith $"HostBridge RPC error: {msg}"
                            | _ ->
                                match root.TryGetProperty("result") with
                                | true, result -> return result
                                | _ -> return JsonDocument.Parse("{}").RootElement.Clone()
                        | false, _ ->
                            // Notification (no id) — skip
                            return! readResponse ()
                }
                return! readResponse ()
            finally
                sem.Release() |> ignore
        }

    // ── High-level chat methods ──────────────────────────────────────────────

    member this.ChatRegister(agentId: string, displayName: string, capabilities: string list) =
        async {
            let caps = capabilities |> List.map (sprintf "\"%s\"") |> String.concat ","
            let paramsJson = sprintf """{"agent_id":"%s","display_name":"%s","capabilities":[%s]}"""
                                agentId displayName caps
            let! _ = this.Request("chat.register", paramsJson)
            ()
        }

    member this.ChatPoll(agentId: string, timeoutMs: int) =
        async {
            let paramsJson = """{"agent_id":""" + "\"" + agentId + "\"" + ""","timeout_ms":""" + string timeoutMs + "}"
            return! this.Request("chat.poll", paramsJson)
        }

    member this.ChatSend(content: string, sender: string, ?sessionKey: string, ?isProgress: bool, ?toolName: string) =
        async {
            let progress = defaultArg isProgress false
            let mutable parts = [
                sprintf "\"content\":%s" (JsonSerializer.Serialize(content))
                sprintf "\"sender\":\"%s\"" sender
                sprintf "\"is_progress\":%s" (if progress then "true" else "false")
            ]
            match sessionKey with
            | Some sk -> parts <- parts @ [sprintf "\"session_key\":\"%s\"" sk]
            | None -> ()
            match toolName with
            | Some tn -> parts <- parts @ [sprintf "\"tool_name\":\"%s\"" tn]
            | None -> ()
            let paramsJson = "{" + String.concat "," parts + "}"
            let! _ = this.Request("chat.send", paramsJson)
            ()
        }

    member this.ChatActivity(phase: string, ?label: string, ?toolName: string) =
        async {
            let mutable parts = [sprintf "\"phase\":\"%s\"" phase]
            match label with
            | Some l -> parts <- parts @ [sprintf "\"label\":%s" (JsonSerializer.Serialize(l))]
            | None -> ()
            match toolName with
            | Some tn -> parts <- parts @ [sprintf "\"tool_name\":\"%s\"" tn]
            | None -> ()
            let paramsJson = "{" + String.concat "," parts + "}"
            let! _ = this.Request("chat.activity", paramsJson)
            ()
        }

    member _.Dispose() =
        writer |> Option.iter (fun w -> try w.Dispose() with _ -> ())
        reader |> Option.iter (fun r -> try r.Dispose() with _ -> ())
        socket |> Option.iter (fun s -> try s.Dispose() with _ -> ())
        sem.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

// ── Stream hook: token-by-token output via chat.send ────────────────────────

let pocketStreamHook (rpc: PocketRpcClient) (agentId: string) (sessionKey: string option ref) : AgentStreamHook =
    StreamingHook(
        // onDelta: each token increment
        (fun text -> async {
            let sk = sessionKey.Value
            do! rpc.ChatSend(text, agentId, ?sessionKey = sk, isProgress = true)
        }),
        // onStreamEnd: final newline
        (fun _hasTools -> async { () })
    )

// ── Agent hook: activity phases via chat.activity ────────────────────────────

let pocketAgentHook (rpc: PocketRpcClient) (sendToolHints: bool) : AgentHook =
    { AgentHook.none with
        BeforeExecuteTools = fun ctx ->
            async {
                for call in ctx.ToolCalls do
                    let (ToolName tn) = call.Tool
                    do! rpc.ChatActivity("tool_start", label = tn, toolName = tn)
                if sendToolHints then
                    let hint = formatToolHints ctx.ToolCalls
                    if hint <> "" then
                        eprintfn "[tools] %s" hint
            }
    }

// ── Channel port: ChannelPort implementation ─────────────────────────────────

let private pocketChannel = ChannelId "pocket"
let private pocketUser    = UserId    "user"

let createPocketPort (rpc: PocketRpcClient) (config: PocketChannelConfig) (sessionKey: string option ref) : ChannelPort = {
    Send = fun msg -> async {
        let sk = sessionKey.Value
        do! rpc.ChatSend(msg.Content, config.AgentId, ?sessionKey = sk, isProgress = false)
    }

    Receive = async {
        try
            let! result = rpc.ChatPoll(config.AgentId, config.PollTimeoutMs)

            // Extract session_key from response
            match result.TryGetProperty("session_key") with
            | true, sk when sk.ValueKind = JsonValueKind.String ->
                sessionKey.Value <- sk.GetString() |> Option.ofObj
            | _ -> ()

            // Extract messages array
            match result.TryGetProperty("messages") with
            | true, msgs when msgs.ValueKind = JsonValueKind.Array ->
                let arr = msgs.EnumerateArray() |> Seq.toArray
                if arr.Length = 0 then
                    return NoMessage
                else
                    let first = arr.[0]
                    let senderId =
                        match first.TryGetProperty("sender_id") with
                        | true, s -> s.GetString() |> Option.ofObj |> Option.defaultValue "user"
                        | _ -> "user"
                    let content =
                        match first.TryGetProperty("content") with
                        | true, c -> c.GetString() |> Option.ofObj |> Option.defaultValue ""
                        | _ -> ""
                    // Parse media paths
                    let media =
                        match first.TryGetProperty("media") with
                        | true, m when m.ValueKind = JsonValueKind.Array ->
                            m.EnumerateArray()
                            |> Seq.choose (fun v ->
                                if v.ValueKind = JsonValueKind.String then
                                    v.GetString() |> Option.ofObj
                                    |> Option.bind (fun path ->
                                        match LocalFilePath.create path with
                                        | Ok fp -> Some (ImageFile fp)
                                        | Error _ -> None)
                                else None)
                            |> Seq.toList
                        | _ -> []

                    let input =
                        match parseUserInput content with
                        | Result.Ok v  -> v
                        | Result.Error _ -> ChatMessage (content, media)

                    let sessionOverride =
                        sessionKey.Value |> Option.map SessionId

                    return Message {
                        Channel            = pocketChannel
                        Sender             = UserId senderId
                        Chat               = ChatId "pocket-session"
                        Input              = input
                        Metadata           = Map.empty
                        SessionKeyOverride = sessionOverride
                    }
            | _ ->
                return NoMessage
        with ex ->
            eprintfn "[pocket] poll error: %s" ex.Message
            // Brief pause before retry to avoid tight error loop
            do! Async.Sleep 1000
            return NoMessage
    }
}

// ── Pocket main loop ─────────────────────────────────────────────────────────

/// Main loop for pocket mode — similar to startCli but:
/// - No slash commands except /new and /clear (pocket UI handles the rest)
/// - Reports thinking activity before LLM calls
/// - Reports idle when waiting for input
let startPocket (coordinator: AgentCoordinator) (port: ChannelPort) (rpc: PocketRpcClient) : Async<unit> =
    let rec loop () = async {
        let! received = port.Receive
        match received with
        | ChannelClosed ->
            eprintfn "[pocket] Channel closed. Shutting down."
            ()

        | NoMessage ->
            return! loop ()

        | Message msg ->
            // Report thinking phase
            do! rpc.ChatActivity("thinking")

            match msg.Input with
            | Command StopProcessing ->
                eprintfn "[pocket] Stop requested."
                ()

            | _ ->
                let! result = coordinator.Route msg
                match result with
                | Result.Ok (PlainResponse text) ->
                    // Non-streamed: send the full reply
                    do! port.Send {
                        Channel     = pocketChannel
                        Chat        = ChatId "pocket-session"
                        Content     = text
                        ReplyTo     = None
                        Attachments = []
                        Buttons     = []
                    }
                | Result.Ok (StreamedResponse _) ->
                    ()   // Already sent token-by-token via StreamHook
                | Result.Error e ->
                    eprintfn "[pocket] Agent error: %A" e
                    do! port.Send {
                        Channel     = pocketChannel
                        Chat        = ChatId "pocket-session"
                        Content     = sprintf "[Error] %A" e
                        ReplyTo     = None
                        Attachments = []
                        Buttons     = []
                    }

                // Report idle
                do! rpc.ChatActivity("idle")
                return! loop ()
    }
    loop ()
