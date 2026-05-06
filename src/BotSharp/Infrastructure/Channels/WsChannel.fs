module BotSharp.Infrastructure.Channels.WsChannel

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// WebSocket server channel
//
// Protocol (JSON over WebSocket):
//   Client → server (legacy format):
//     { "text": "user message", "chat_id": "optional-id" }
//
//   Client → server (typed envelope format):
//     { "type": "new_chat" }
//     { "type": "attach",  "chat_id": "<id>" }
//     { "type": "message", "content": "<text>", "chat_id": "<id>",
//       "media": [{"data_url": "data:image/png;base64,..."}] }
//
//   Server → client:
//     { "type": "ready",    "chat_id": "<uuid>" }   — on connect
//     { "type": "attached", "chat_id": "<id>" }      — after new_chat / attach
//     { "type": "delta",    "text": "<chunk>", "chat_id": "..." }   — streaming
//     { "type": "done",     "chat_id": "..." }       — stream complete
//     { "type": "error",    "text": "...",    "chat_id": "...", "detail": "..." }
//
// Authentication:
//   If a static token is configured, clients must supply it as:
//     • Query param: ws://host:port/ws?token=<token>
//     • Header: Authorization: Bearer <token>
//   Connections that fail authentication receive HTTP 401 before the upgrade.
//
// Session model:
//   Each connection may supply ?chat_id=<id> to identify or resume a session.
//   If absent, a fresh UUID is generated. Session state is persisted via
//   LoadSession/PersistSession in baseDeps — reconnecting clients with the
//   same chat_id load the previous context from disk.
//
//   On disconnect the actor is shut down; the session snapshot remains on
//   disk for the next connection. The per-connection StreamingHook is
//   re-created for each new connection so deltas go to the active WebSocket.
//
// Design:
//   Uses System.Net.HttpListener for WebSocket upgrade (same as ApiChannel).
//   Streaming is always enabled — per-connection StreamingHook fires onDelta
//   for each text chunk; onStreamEnd sends the "done" event.
//   Multiple concurrent connections are handled via fire-and-forget Async.Start.
// ═══════════════════════════════════════════════════════════════════════════

let private wsChannel = ChannelId "ws"

// ── Typed outbound events ─────────────────────────────────────────────────────
//
// WsOutboundEvent is a DU over every event the server sends.
// All serialization happens in ONE place (serializeEvent). FS0025 fires if a
// new event case is added but its serialization is missing.
// This eliminates: misspelled field names, inconsistent "type" strings, and
// events that forget to include chat_id.

type private WsOutboundEvent =
    | Ready    of chatId: string
    | Attached of chatId: string
    | Delta    of chatId: string * text: string
    | Done     of chatId: string
    | WsError  of chatId: string * text: string * detail: string option

/// Serialize a WsOutboundEvent to UTF-8 JSON bytes.
/// Single serialization point — no string field names scattered in send calls.
let private serializeEvent (ev: WsOutboundEvent) : byte[] =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    match ev with
    | Ready chatId ->
        w.WriteString("type", "ready")
        w.WriteString("chat_id", chatId)
    | Attached chatId ->
        w.WriteString("type", "attached")
        w.WriteString("chat_id", chatId)
    | Delta (chatId, text) ->
        w.WriteString("type", "delta")
        w.WriteString("text", text)
        w.WriteString("chat_id", chatId)
    | Done chatId ->
        w.WriteString("type", "done")
        w.WriteString("chat_id", chatId)
    | WsError (chatId, text, detail) ->
        w.WriteString("type", "error")
        w.WriteString("text", text)
        match detail with Some d -> w.WriteString("detail", d) | None -> ()
        w.WriteString("chat_id", chatId)
    w.WriteEndObject()
    w.Flush()
    ms.ToArray()

// ── Image upload domain types ─────────────────────────────────────────────────
//
// ImageMime and ImageDecodeError are DUs at the parsing boundary.
// After parseMediaItem succeeds, the MIME type is a typed DU — not a string.
// ImageDecodeError cases are exhaustive (FS0025 fires if a new case is unhandled).

/// Supported image MIME types.
/// After parseMediaItem, invalid MIMEs are structurally impossible.
type private ImageMime = Png | Jpeg | Webp | Gif

module private ImageMime =
    let parse (raw: string) : ImageMime option =
        match raw.Trim().ToLowerInvariant() with
        | "image/png"  -> Some Png
        | "image/jpeg" -> Some Jpeg
        | "image/webp" -> Some Webp
        | "image/gif"  -> Some Gif
        | _            -> None

    /// File extension for the MIME type (no "." prefix).
    let extension = function
        | Png  -> "png"
        | Jpeg -> "jpg"
        | Webp -> "webp"
        | Gif  -> "gif"

/// Error cases for media decoding.
/// Using a DU instead of strings prevents typos and forces exhaustive handling.
type private ImageDecodeError =
    | Malformed            // can't parse the data URL structure
    | MimeNotAllowed       // MIME not in the allowed set
    | DecodeError          // base64 decode failed
    | TooLarge             // decoded bytes exceed size limit
    | TooManyImages        // more items than maxImagesPerMessage

module private ImageDecodeError =
    /// Convert to a stable short token for the client (matches Python's reason strings).
    let toToken = function
        | Malformed      -> "malformed"
        | MimeNotAllowed -> "mime_not_allowed"
        | DecodeError    -> "decode_error"
        | TooLarge       -> "too_large"
        | TooManyImages  -> "too_many_images"

/// A parsed media item: the data URL has been split into MIME + base64 payload.
/// After parseMediaItem, raw strings are gone; all downstream code is typed.
type private MediaItem = { Mime: ImageMime; Base64: string }

let private maxImagesPerMessage = 4
let private maxImageBytes       = 10_485_760   // 10 MB

/// Regex that extracts the MIME type from a data URL header.
/// Compiled once; used only by parseMediaItem (parser boundary).
let private dataUrlHeaderRe = Regex(@"^data:([^;]+);base64,", RegexOptions.Compiled)

/// Parse one raw data URL string into a typed MediaItem.
/// All string discrimination happens here — callers work with ImageMime/MediaItem.
let private parseMediaItem (dataUrl: string) : Result<MediaItem, ImageDecodeError> =
    let m = dataUrlHeaderRe.Match(dataUrl)
    if not m.Success then Result.Error Malformed
    else
        match ImageMime.parse (m.Groups.[1].Value) with
        | None    -> Result.Error MimeNotAllowed   // MIME unrecognised — no string leaks downstream
        | Some mime ->
            Result.Ok { Mime = mime; Base64 = dataUrl.Substring(m.Length) }

/// Parse the media array from a typed message envelope.
/// Returns Ok (MediaItem list) or Error (first failure).
/// Raw JsonElement does not escape this function.
let private parseMediaItems (items: JsonElement list) : Result<MediaItem list, ImageDecodeError> =
    if items.Length > maxImagesPerMessage then Result.Error TooManyImages
    else
        let rec loop acc = function
            | []                          -> Result.Ok (List.rev acc)
            | (item: JsonElement) :: rest ->
                match item.TryGetProperty("data_url") with
                | true, el when el.ValueKind = JsonValueKind.String ->
                    match parseMediaItem (el.GetString() |> Unchecked.nonNull) with
                    | Result.Error e -> Result.Error e
                    | Result.Ok mi   -> loop (mi :: acc) rest
                | _ -> Result.Error Malformed
        loop [] items

/// Decode a parsed MediaItem to disk and return the file path.
/// Fails only on base64 decoding or file-system errors — all MIME checks are upstream.
let private saveMediaItem (workspacePath: string) (item: MediaItem) : Result<LocalFilePath, ImageDecodeError> =
    try
        let bytes = Convert.FromBase64String(item.Base64)
        if bytes.Length > maxImageBytes then Result.Error TooLarge
        else
            let mediaDir = Path.Combine(workspacePath, "media")
            Directory.CreateDirectory(mediaDir) |> ignore
            // ImageMime.extension is a DU match — no "| _" catch-all possible
            let ext      = ImageMime.extension item.Mime
            let filePath = Path.Combine(mediaDir, sprintf "%s.%s" (Guid.NewGuid().ToString("N")) ext)
            File.WriteAllBytes(filePath, bytes)
            Result.Ok (LocalFilePath.ofAbsolute filePath)
    with :? FormatException ->
        Result.Error DecodeError

/// Parse + save all media items from a typed message envelope.
/// Returns Ok (MediaContent list) or Error with a stable client-facing token.
let private decodeMediaItems (workspacePath: string) (rawItems: JsonElement list) : Result<MediaContent list, string> =
    match parseMediaItems rawItems with
    | Result.Error e -> Result.Error (ImageDecodeError.toToken e)
    | Result.Ok parsed ->
        let rec loop acc = function
            | []          -> Result.Ok (List.rev acc)
            | mi :: rest  ->
                match saveMediaItem workspacePath mi with
                | Result.Error e  -> Result.Error (ImageDecodeError.toToken e)
                | Result.Ok path  -> loop (ImageFile path :: acc) rest
        loop [] parsed

// ── Typed inbound envelopes ───────────────────────────────────────────────────
//
// InboundEnvelope is parsed once at the WebSocket boundary.
// Downstream code matches on DU cases — no string comparisons on "type".
// JsonElement does not escape parseEnvelope; media items are parsed eagerly
// into MediaItem list so invalid data URLs are caught at the boundary.

/// Typed inbound envelope variants (after parser boundary — no raw strings).
type private InboundEnvelope =
    | NewChat
    | AttachChat      of chatId: string
    | MessageEnvelope of content: string * chatId: string option * media: JsonElement list
    | LegacyMessage   of text: string * chatId: string option

/// Parse a raw WebSocket frame into a typed envelope.
/// Returns None only for completely empty or unparseable JSON.
let private parseEnvelope (raw: string) : InboundEnvelope option =
    let s = raw.Trim()
    if s = "" then None
    elif s.StartsWith("{") then
        try
            use doc  = JsonDocument.Parse(s)
            let root = doc.RootElement
            match root.TryGetProperty("type") with
            | true, typeProp when typeProp.ValueKind = JsonValueKind.String ->
                match typeProp.GetString() with
                | "new_chat" ->
                    Some NewChat
                | "attach" ->
                    let cid =
                        match root.TryGetProperty("chat_id") with
                        | true, el when el.ValueKind = JsonValueKind.String ->
                            el.GetString() |> Unchecked.nonNull
                        | _ -> ""
                    if String.IsNullOrWhiteSpace cid then None
                    else Some (AttachChat cid)
                | "message" ->
                    let content =
                        match root.TryGetProperty("content") with
                        | true, el when el.ValueKind = JsonValueKind.String ->
                            el.GetString() |> Unchecked.nonNull
                        | _ -> ""
                    let chatId =
                        match root.TryGetProperty("chat_id") with
                        | true, el when el.ValueKind = JsonValueKind.String ->
                            el.GetString() |> Option.ofObj
                        | _ -> None
                    let mediaItems =
                        match root.TryGetProperty("media") with
                        | true, el when el.ValueKind = JsonValueKind.Array ->
                            [ for item in el.EnumerateArray() -> item.Clone() ]
                        | _ -> []
                    Some (MessageEnvelope (content, chatId, mediaItems))
                | _ -> None   // unknown typed envelope — ignore
            | _ ->
                // Legacy format: { "text": "..." } or { "content": "..." }
                let text =
                    [ "text"; "content"; "message" ]
                    |> List.tryPick (fun key ->
                        match root.TryGetProperty(key) with
                        | true, el when el.ValueKind = JsonValueKind.String ->
                            el.GetString() |> Option.ofObj
                        | _ -> None)
                    |> Option.defaultValue ""
                let chatId =
                    match root.TryGetProperty("chat_id") with
                    | true, el when el.ValueKind = JsonValueKind.String ->
                        el.GetString() |> Option.ofObj
                    | _ -> None
                Some (LegacyMessage (text, chatId))
        with :? JsonException -> None
    else
        // Plain text — treat as message content
        Some (LegacyMessage (s, None))

// ── Per-connection coordinator ────────────────────────────────────────────────
//
// Creates per-chatId session actors whose StreamHook is a closure over the
// active WebSocket's send function.  The per-connection actor is removed and
// shut down on disconnect so the next connection (same chatId, new WebSocket)
// creates a fresh actor with the new send closure.
//
// A connection has a "current chat ID" which starts as the connection-default
// (from ?chat_id= or a fresh UUID) and can be changed by new_chat / attach
// envelopes. Each actor is keyed by chatId; switching chatId makes a new actor.
//
// Compare: TelegramCoordinator uses the same pattern but per Telegram chat ID.

type private WsCoordinator(baseDeps: AgentDependencies) =
    let actors = ConcurrentDictionary<string, MailboxProcessor<SessionActorMsg>>()

    /// Derive a stable SessionId from the channel-scoped chatId.
    let sid (chatId: string) = SessionId (sprintf "ws:%s" chatId)

    /// Create a session actor whose streaming hook sends deltas over the WebSocket.
    let mkActor (chatId: string) (sendDelta: string -> Async<unit>) (onDone: bool -> Async<unit>) =
        let hook =
            StreamingHook(
                onDelta     = (fun delta ->
                    match delta with
                    | TextDelta t -> sendDelta t
                    | ThinkingDelta _ -> async { () }   // thinking not sent over WS (yet)
                    | ToolArgDelta _ -> async { () }),
                onStreamEnd = onDone)
        let deps = { baseDeps with StreamHook = hook }
        createSessionActor (sid chatId) deps

    /// Ensure an actor exists for chatId (re-create if stale).
    member _.EnsureActor(chatId: string, sendDelta: string -> Async<unit>, onDone: bool -> Async<unit>) : unit =
        if not (actors.ContainsKey chatId) then
            let actor = mkActor chatId sendDelta onDone
            actors.TryAdd(chatId, actor) |> ignore

    /// Create a fresh actor (removing any stale one for this chatId).
    member _.Connect(chatId: string, sendDelta: string -> Async<unit>, onDone: bool -> Async<unit>) : unit =
        // Remove any existing actor (e.g. stale from a previous connection).
        // The actor is shut down gracefully; session state is already on disk.
        match actors.TryRemove(chatId) with
        | true, old -> old.Post Shutdown
        | _ -> ()
        let actor = mkActor chatId sendDelta onDone
        actors.TryAdd(chatId, actor) |> ignore

    /// Route an inbound message through the actor for chatId.
    member _.Route(chatId: string) (inbound: InboundMessage) : Async<Result<string * SessionSnapshot, AgentError>> =
        async {
            match actors.TryGetValue(chatId) with
            | false, _ ->
                return Result.Error SessionActorStopped   // actor was removed (e.g. disconnect race)
            | true, actor ->
                return! actor.PostAndAsyncReply(fun ch -> ProcessInput(inbound, ch))
        }

    /// Remove and shutdown the actor for a disconnecting connection.
    member _.Disconnect(chatId: string) : unit =
        match actors.TryRemove(chatId) with
        | true, actor -> actor.Post Shutdown
        | _ -> ()

// ── WebSocket connection handler ─────────────────────────────────────────────

let private handleConnection
    (coord       : WsCoordinator)
    (ws          : WebSocket)
    (defaultChatId : string)
    (workspacePath : string)
    : Async<unit> =
    async {
        // Serialise writes to the WebSocket so concurrent onDelta calls don't interleave.
        // Not `use` — the semaphore must outlive handleConnection: StreamingHook callbacks
        // run asynchronously inside the actor, which may still be active when readLoop exits.
        // The semaphore is GC'd after the actor processes its Shutdown message.
        let sendLock = new SemaphoreSlim(1, 1)

        let sendRaw (bytes: byte[]) : Async<unit> =
            async {
                let! acquired = sendLock.WaitAsync(5_000) |> Async.AwaitTask
                if acquired then
                    try
                        if ws.State = WebSocketState.Open then
                            do! ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                                |> Async.AwaitTask
                    with _ -> ()
                    sendLock.Release() |> ignore
            }

        // Single send entry point: takes a typed WsOutboundEvent, not raw strings.
        // serializeEvent is the only place that knows the JSON field names.
        let sendEvent (ev: WsOutboundEvent) : Async<unit> =
            sendRaw (serializeEvent ev)

        // Current active chat ID — mutable because new_chat / attach can change it.
        // Using a ref cell rather than a mutable local so closures capture the cell itself.
        let currentChatId = ref defaultChatId

        let sendDelta (text: string) : Async<unit> =
            sendEvent (Delta (!currentChatId, text))

        let onDone (_ : bool) : Async<unit> =
            sendEvent (Done !currentChatId)

        coord.Connect(defaultChatId, sendDelta, onDone)

        // Send "ready" event so the client knows its chatId.
        do! sendEvent (Ready defaultChatId)

        /// Switch the connection's active chat to a different chatId.
        let switchChat (newChatId: string) : Async<unit> =
            async {
                currentChatId := newChatId
                // Ensure an actor exists for the new chat (without destroying the old one —
                // another connection may still be using it).
                coord.EnsureActor(newChatId, sendDelta, onDone)
                do! sendEvent (Attached newChatId)
            }

        /// Route a user message through the active chat actor.
        let routeMessage (content: string) (media: MediaContent list) (chatId: string) : Async<unit> =
            async {
                let inbound : InboundMessage = {
                    Channel            = wsChannel
                    Sender             = UserId "ws-client"
                    Chat               = ChatId chatId
                    Input              = ChatMessage (content, media)
                    Metadata           = Map.ofList [ "source", "ws" ]
                    SessionKeyOverride = None
                }
                let! result = coord.Route chatId inbound
                match result with
                | Result.Error e ->
                    do! sendEvent (WsError (chatId, sprintf "%A" e, None))
                | Result.Ok _ ->
                    // Streaming: deltas already sent by onDelta; onDone sent by onStreamEnd.
                    ()
            }

        // Receive loop: read full WebSocket messages (may span multiple frames).
        let buffer = Array.zeroCreate 65536
        let segment = ArraySegment<byte>(buffer)

        let rec readLoop () =
            async {
                try
                    use ms = new MemoryStream()
                    let mutable finished = false
                    let mutable closing  = false

                    while not finished do
                        let! result =
                            ws.ReceiveAsync(segment, CancellationToken.None)
                            |> Async.AwaitTask
                        match result.MessageType with
                        | WebSocketMessageType.Close ->
                            closing <- true
                            finished <- true
                        | _ ->
                            ms.Write(buffer, 0, result.Count)
                            if result.EndOfMessage then finished <- true

                    if closing then
                        try
                            do! ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                                |> Async.AwaitTask
                        with _ -> ()
                    else
                        let raw = Encoding.UTF8.GetString(ms.ToArray())
                        match parseEnvelope raw with
                        | None ->
                            // Silently skip empty / unparseable frames; don't disconnect.
                            return! readLoop ()

                        | Some NewChat ->
                            let newId = Guid.NewGuid().ToString("N")
                            do! switchChat newId
                            return! readLoop ()

                        | Some (AttachChat newId) ->
                            do! switchChat newId
                            return! readLoop ()

                        | Some (MessageEnvelope (content, chatIdOpt, mediaItems)) ->
                            let chatId = chatIdOpt |> Option.defaultValue !currentChatId
                            // Ensure actor exists (auto-attach on first use)
                            coord.EnsureActor(chatId, sendDelta, onDone)
                            currentChatId := chatId
                            // Decode images (if any) — error token comes from ImageDecodeError DU
                            match decodeMediaItems workspacePath mediaItems with
                            | Result.Error token ->
                                do! sendEvent (WsError (chatId, token, Some "image_rejected"))
                            | Result.Ok media ->
                                if content.Trim() = "" && media.IsEmpty then
                                    do! sendEvent (WsError (chatId, "content cannot be empty", Some "missing_content"))
                                else
                                    do! routeMessage content media chatId
                            return! readLoop ()

                        | Some (LegacyMessage (text, chatIdOpt)) ->
                            // Legacy { "text": "..." } format — no image support
                            let chatId = chatIdOpt |> Option.defaultValue !currentChatId
                            if text.Trim() <> "" then
                                do! routeMessage text [] chatId
                            return! readLoop ()

                with _ ->
                    ()   // client disconnected mid-read — exit loop
            }

        do! readLoop ()
        coord.Disconnect(!currentChatId)
    }

// ── HTTP request dispatch (upgrade or reject) ────────────────────────────────

let private addCorsHeaders (resp: HttpListenerResponse) =
    resp.Headers.["Access-Control-Allow-Origin"]  <- "*"
    resp.Headers.["Access-Control-Allow-Methods"] <- "GET, OPTIONS"
    resp.Headers.["Access-Control-Allow-Headers"] <- "Content-Type, Authorization"

let private writeText (body: string) (status: int) (ctx: HttpListenerContext) : Async<unit> =
    async {
        addCorsHeaders ctx.Response
        ctx.Response.StatusCode  <- status
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        let bytes = Encoding.UTF8.GetBytes(body)
        ctx.Response.ContentLength64 <- int64 bytes.Length
        try
            do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
        with _ -> ()
        ctx.Response.Close()
    }

// ── Issued-token store ────────────────────────────────────────────────────────
//
// WsTokenIssuer allows clients to trade the static master token for a short-lived
// per-connection token. The master token stays out of WebSocket URLs; the browser
// fetches a fresh token via GET /token (Authorization: Bearer <master>) and uses
// the returned token for the /ws connection.

/// Thread-safe store for short-lived issued tokens.
/// Tokens are issued by GET /token (requires master-token authentication) and are
/// accepted wherever the static master token is accepted, until they expire.
/// Expired tokens are lazily evicted on IsValid checks.
type private WsTokenIssuer(ttlSeconds: int) =
    let tokens = ConcurrentDictionary<string, DateTimeOffset>()

    /// Issue a new short-lived token and return its string value.
    member _.Issue() : string =
        let tok = Guid.NewGuid().ToString("N")
        tokens[tok] <- DateTimeOffset.UtcNow.AddSeconds(float ttlSeconds)
        tok

    /// Check if a short-lived token is valid (exists and not yet expired).
    /// Lazily evicts expired tokens.
    member _.IsValid(tok: string) : bool =
        match tokens.TryGetValue(tok) with
        | true, exp when exp > DateTimeOffset.UtcNow -> true
        | true, _ ->
            tokens.TryRemove(tok) |> ignore
            false
        | false, _ -> false

    member _.TtlSeconds = ttlSeconds

let private checkToken (configuredToken: string option) (issuer: WsTokenIssuer option) (req: HttpListenerRequest) : bool =
    match configuredToken with
    | None -> true   // no authentication configured
    | Some expected ->
        // Extract the presented token from Authorization header or query param.
        let fromHeader =
            match req.Headers.["Authorization"] with
            | null -> None
            | h when h.StartsWith("Bearer ") -> Some (h.Substring("Bearer ".Length).Trim())
            | _    -> None
        let fromQuery =
            match req.QueryString.["token"] with
            | null -> None
            | t    -> Some t
        let presented =
            match fromHeader, fromQuery with
            | Some t, _ -> Some t
            | _, Some t -> Some t
            | _         -> None
        match presented with
        | None   -> false
        | Some t ->
            // Accept if it matches the master token OR a valid issued token.
            t = expected ||
            (issuer |> Option.exists (fun iss -> iss.IsValid(t)))

/// Check whether a client_id is permitted by the allow list.
/// If no client_id is provided in the query string, the check passes.
/// This mirrors Python's WebSocket `allow_from` filtering on client_id.
let private checkClientId (allowFrom: AllowList) (req: HttpListenerRequest) : bool =
    match req.QueryString.["client_id"] with
    | null | "" -> true   // no client_id provided → no filtering applied
    | cid ->
        AllowList.permits (UserId cid) allowFrom

let private dispatchRequest
    (coord         : WsCoordinator)
    (token         : string option)
    (issuer        : WsTokenIssuer option)
    (allowFrom     : AllowList)
    (workspacePath : string)
    (ctx           : HttpListenerContext)
    : Async<unit> =
    async {
        let path = (Unchecked.nonNull ctx.Request.Url).AbsolutePath.TrimEnd('/')
        try
            match ctx.Request.HttpMethod, path with

            | "OPTIONS", _ ->
                addCorsHeaders ctx.Response
                ctx.Response.StatusCode <- 204
                ctx.Response.Close()

            | "GET", ("" | "/") ->
                // Minimal chat WebUI — auto-connects to /ws on same host/port.
                let html = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>BotSharp</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:system-ui,sans-serif;background:#0d1117;color:#c9d1d9;display:flex;flex-direction:column;height:100vh}
#log{flex:1;overflow-y:auto;padding:1rem;display:flex;flex-direction:column;gap:.5rem}
.msg{max-width:80%;padding:.5rem .75rem;border-radius:.5rem;white-space:pre-wrap;word-break:break-word}
.user{align-self:flex-end;background:#1f6feb;color:#fff}
.assistant{align-self:flex-start;background:#21262d}
.system{align-self:center;font-size:.75rem;color:#6e7681}
#bar{display:flex;padding:.5rem;gap:.5rem;border-top:1px solid #21262d}
#inp{flex:1;padding:.5rem .75rem;background:#161b22;border:1px solid #30363d;border-radius:.375rem;color:#c9d1d9;font-size:1rem;outline:none}
#inp:focus{border-color:#58a6ff}
#send{padding:.5rem 1rem;background:#238636;border:none;border-radius:.375rem;color:#fff;cursor:pointer;font-size:1rem}
#send:hover{background:#2ea043}
</style>
</head>
<body>
<div id="log"><div class="msg system">Connecting…</div></div>
<div id="bar">
  <input id="inp" placeholder="Type a message…" autocomplete="off"/>
  <button id="send">Send</button>
</div>
<script>
const log=document.getElementById('log');
const inp=document.getElementById('inp');
const proto=location.protocol==='https:'?'wss':'ws';
const ws=new WebSocket(`${proto}://${location.host}/ws`);
let pending=null;

function addMsg(cls,text){
  const d=document.createElement('div');
  d.className='msg '+cls;
  d.textContent=text;
  log.appendChild(d);
  log.scrollTop=log.scrollHeight;
  return d;
}

ws.onopen=()=>{log.querySelector('.system').textContent='Connected';};
ws.onclose=()=>addMsg('system','Disconnected');
ws.onerror=()=>addMsg('system','Connection error');
ws.onmessage=e=>{
  const m=JSON.parse(e.data);
  if(m.type==='ready'){addMsg('system','Ready · session: '+m.chat_id);}
  else if(m.type==='delta'){
    if(!pending){pending=addMsg('assistant','');}
    pending.textContent+=m.text;
    log.scrollTop=log.scrollHeight;
  }
  else if(m.type==='done'){pending=null;}
  else if(m.type==='error'){addMsg('system','Error: '+m.text);}
};

function send(){
  const t=inp.value.trim();
  if(!t||ws.readyState!==1)return;
  addMsg('user',t);
  ws.send(JSON.stringify({text:t}));
  inp.value='';
}
document.getElementById('send').onclick=send;
inp.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();send();}});
</script>
</body>
</html>"""
                ctx.Response.ContentType <- "text/html; charset=utf-8"
                ctx.Response.StatusCode  <- 200
                let bytes = Encoding.UTF8.GetBytes(html)
                ctx.Response.ContentLength64 <- bytes.LongLength
                do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                ctx.Response.Close()

            | "GET", "/health" ->
                do! writeText """{"status":"ok"}""" 200 ctx

            | "GET", "/token" ->
                // Issue a short-lived token in exchange for the master token.
                // Requires the master token for authentication (issued tokens cannot
                // be used to mint more tokens — prevents token amplification).
                if not (checkToken token None ctx.Request) then
                    do! writeText "Unauthorized" 401 ctx
                else
                    match issuer with
                    | None ->
                        // No authentication is configured — token issuance is unnecessary.
                        do! writeText """{"error":"token issuance is disabled when no authentication is configured"}""" 400 ctx
                    | Some iss ->
                        let tok  = iss.Issue()
                        let json = sprintf """{"token":"%s","expires_in":%d}""" tok iss.TtlSeconds
                        addCorsHeaders ctx.Response
                        ctx.Response.StatusCode  <- 200
                        ctx.Response.ContentType <- "application/json; charset=utf-8"
                        let bytes = Encoding.UTF8.GetBytes(json)
                        ctx.Response.ContentLength64 <- bytes.LongLength
                        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                        ctx.Response.Close()

            | "GET", "/ws" when ctx.Request.IsWebSocketRequest ->
                if not (checkToken token issuer ctx.Request) then
                    do! writeText "Unauthorized" 401 ctx
                elif not (checkClientId allowFrom ctx.Request) then
                    do! writeText "Forbidden" 403 ctx
                else
                    let chatId =
                        match ctx.Request.QueryString.["chat_id"] with
                        | null -> Guid.NewGuid().ToString("N")
                        | id when String.IsNullOrWhiteSpace(id) -> Guid.NewGuid().ToString("N")
                        | id -> id

                    try
                        let! wsCtx = ctx.AcceptWebSocketAsync(null) |> Async.AwaitTask
                        do! handleConnection coord wsCtx.WebSocket chatId workspacePath
                    with ex ->
                        eprintfn "[WS] Upgrade error for chatId %s: %s" chatId ex.Message

            | "GET", "/ws" ->
                // Upgrade header missing — send 400 rather than hanging.
                do! writeText "WebSocket upgrade required" 400 ctx

            | _ ->
                do! writeText "Not found" 404 ctx

        with ex ->
            try
                ctx.Response.StatusCode <- 500
                ctx.Response.Close()
            with _ -> ()
    }

// ── Public server type ────────────────────────────────────────────────────────

/// WebSocket server that accepts connections on `ws://localhost:{port}/ws`.
///
/// Authentication: if `token` is Some, clients must supply it via
///   `?token=<value>` query param or `Authorization: Bearer <value>` header.
///
/// Sessions: each connection is bound to a chatId (from `?chat_id=` or a
///   freshly generated UUID). Session state is persisted across reconnects.
type WsServer(baseDeps: AgentDependencies, token: string option) =
    let listener = new HttpListener()
    let coord    = WsCoordinator(baseDeps)
    // Create a token issuer only when authentication is configured.
    // ttl = 300 s (5 minutes): enough for a browser to fetch + establish a WS connection.
    let issuer   = token |> Option.map (fun _ -> WsTokenIssuer(300))

    /// Start listening on `http://localhost:{port}/`.
    /// Blocks until `Stop()` is called.
    member _.Start(port: int) : Async<unit> =
        async {
            let prefix = sprintf "http://localhost:%d/" port
            listener.Prefixes.Add(prefix)
            listener.Start()
            eprintfn "[WS] Listening on ws://localhost:%d/ws" port
            eprintfn "[WS]   GET  /ws      WebSocket endpoint"
            eprintfn "[WS]   GET  /token   Issue short-lived token (requires master token)"
            eprintfn "[WS]   GET  /health  Health check"
            let workspacePath = baseDeps.Config.WorkspacePath
            let rec loop () =
                async {
                    try
                        let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                        // Fire-and-forget: each connection runs concurrently.
                        Async.Start(dispatchRequest coord token issuer baseDeps.Config.AllowFrom workspacePath ctx)
                        return! loop ()
                    with :? HttpListenerException ->
                        ()   // listener stopped — exit
                }
            return! loop ()
        }

    /// Stop the HTTP listener.
    member _.Stop() =
        try listener.Stop()  with _ -> ()
        try listener.Close() with _ -> ()
