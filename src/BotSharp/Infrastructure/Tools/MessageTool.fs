module BotSharp.Infrastructure.Tools.MessageTool

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.StringUtils
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// MessageTool — agent-facing tool to push outbound messages to users
//
// The agent calls this tool to send a message without waiting for user input.
// Typical uses: progress updates during long tasks, cron job completion notices,
// or heartbeat results.
//
// Design:
//   • `send` callback is `OutboundMessage -> Async<unit>` — same type as
//     ChannelPort.Send. No mutable context per turn; channel/chat come from
//     tool args. If omitted, the default CLI channel/chat is used.
//   • No mutable `set_context` — the tool has no global state.
// ═══════════════════════════════════════════════════════════════════════════

// ── Tool spec ──────────────────────────────────────────────────────────────

let messageToolSpec : ToolSpec = {
    Name            = ToolName "message"
    Description     = """Send a message to the user. Use this to communicate progress,
results, or notifications without waiting for user input.
The message will be delivered to the current chat unless channel/chat are specified.
Use the 'media' parameter with file paths to attach images, documents, or audio files.
Use the 'buttons' parameter to add inline keyboard buttons (list of rows, each row is a list of labels)."""
    Parameters      = Map.ofList [
        "content", { Type = JsString;  Description = "The message text to send"; Required = true }
        "channel", { Type = JsString;  Description = "Target channel ID (default: cli)"; Required = false }
        "chat",    { Type = JsString;  Description = "Target chat ID (default: cli-session)"; Required = false }
        "media",   { Type = JsArray JsString
                     Description = "Optional list of file paths to attach (images, audio, documents)"; Required = false }
        "buttons", { Type = JsArray (JsArray JsString)
                     Description = "Optional inline keyboard: list of button rows, each row is a list of button labels"; Required = false }
    ]
    ConcurrencySafe = false  // sends messages; ordering matters
}

// ── Default routing constants ─────────────────────────────────────────────

let private defaultChannel = ChannelId "cli"
let private defaultChat    = ChatId    "cli-session"

// ── Media helpers ─────────────────────────────────────────────────────────

/// Infer MediaContent type from file extension (same heuristic as Python MessageTool).
let private classifyMedia (path: string) : MediaContent =
    let lp  = LocalFilePath.ofAbsolute path
    let ext = (Path.GetExtension(path) |> Unchecked.nonNull).ToLowerInvariant()
    match ext with
    | ".jpg" | ".jpeg" | ".png" | ".gif" | ".webp" | ".bmp" | ".tiff" | ".svg" ->
        ImageFile lp
    | ".mp3" | ".wav" | ".ogg" | ".m4a" | ".flac" | ".aac" | ".opus" ->
        AudioFile lp
    | ".mp4" | ".mov" | ".avi" | ".mkv" | ".webm" ->
        VideoFile lp
    | _ ->
        DocumentFile lp

// ── Buttons parser ────────────────────────────────────────────────────────

/// Parse `buttons` arg: array of arrays of strings.
/// Returns Error if the value exists but is not a valid 2-D string grid.
/// Returns Ok [] when the arg is absent (no buttons).
let private parseButtonsArg (args: Map<string, JsonElement>) : Result<string list list, ToolError> =
    match args.TryFind "buttons" with
    | None -> Ok []
    | Some v ->
        if v.ValueKind <> JsonValueKind.Array then
            Error (ParameterInvalid ("buttons", "must be an array of arrays of strings"))
        else
            let rows =
                v.EnumerateArray()
                |> Seq.toList
                |> List.mapi (fun rowIdx rowEl ->
                    if rowEl.ValueKind <> JsonValueKind.Array then
                        Error (ParameterInvalid ("buttons", $"row {rowIdx} must be an array of strings"))
                    else
                        let labels =
                            rowEl.EnumerateArray()
                            |> Seq.toList
                            |> List.mapi (fun colIdx el ->
                                if el.ValueKind <> JsonValueKind.String then
                                    Error (ParameterInvalid ("buttons", $"row {rowIdx} col {colIdx} must be a string"))
                                else
                                    Ok (el.GetString() |> Unchecked.nonNull))
                        labels |> List.fold (fun acc r ->
                            match acc, r with
                            | Ok xs, Ok x -> Ok (xs @ [x])
                            | Error e, _  -> Error e
                            | _, Error e  -> Error e) (Ok []))
            rows |> List.fold (fun acc r ->
                match acc, r with
                | Ok xs, Ok x -> Ok (xs @ [x])
                | Error e, _  -> Error e
                | _, Error e  -> Error e) (Ok [])

// ── Execution ──────────────────────────────────────────────────────────────

/// Execute the message tool. `send` is the outbound channel callback.
let executeMessage
    (send : OutboundMessage -> Async<unit>)
    (args : Map<string, JsonElement>)
    : Async<ToolResult> =
    async {
        match requireStringArg "content" args with
        | Error e -> return ToolFailure e
        | Ok rawContent ->
            // Strip <think>…</think> / <thought>…</thought> reasoning blocks before
            // sending — mirrors Python message.execute: content = strip_think(content).
            let content = stripThink rawContent
            match parseButtonsArg args with
            | Error e -> return ToolFailure e
            | Ok buttons ->
            let channel =
                tryStringArg "channel" args
                |> Option.map ChannelId
                |> Option.defaultValue defaultChannel
            let chat =
                tryStringArg "chat" args
                |> Option.map ChatId
                |> Option.defaultValue defaultChat
            let attachments =
                tryStringArrayArg "media" args
                |> Option.defaultValue []
                |> List.map classifyMedia
            let msg : OutboundMessage = {
                Channel     = channel
                Chat        = chat
                Content     = content
                ReplyTo     = None
                Attachments = attachments
                Buttons     = buttons
            }
            try
                do! send msg
                let dest = $"{let (ChannelId c) = channel in c}:{let (ChatId ch) = chat in ch}"
                let mediaInfo =
                    if attachments.IsEmpty then ""
                    else $" with {attachments.Length} attachment(s)"
                let buttonInfo =
                    if buttons.IsEmpty then ""
                    else
                        let total = buttons |> List.sumBy List.length
                        $" with {total} button(s)"
                return ToolSuccess $"Message delivered to {dest}{mediaInfo}{buttonInfo}"
            with ex ->
                return ToolFailure (ExecutionFailed $"Failed to send message: {ex.Message}")
    }

/// All message tools as a (spec, execute) pair, bound to the given send callback.
let allTools (send: OutboundMessage -> Async<unit>)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ messageToolSpec, executeMessage send ]
