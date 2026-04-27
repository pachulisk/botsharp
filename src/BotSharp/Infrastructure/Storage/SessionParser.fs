module BotSharp.Infrastructure.Storage.SessionParser

open System
open System.IO
open System.Text
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.Json

// ═══════════════════════════════════════════════════════════════════════════
// Session JSONL serializer / deserializer
//
// Each session is stored as a JSONL file where every line encodes one Message.
//
// Wire format per role:
//
// user:
//   { "role": "user", "content": "...", "media": [...] }
//
// assistant:
//   { "role": "assistant", "content": "..." }
//
// tool_calls:
//   { "role": "tool_calls", "calls": [
//       { "id": "call_1", "tool": "read_file",
//         "arguments": { "path": "." } }
//   ]}
//
// tool_result:
//   { "role": "tool_result", "id": "call_1",
//     "name": "read_file", "content": "..." }
// ═══════════════════════════════════════════════════════════════════════════

// ── Serialization ─────────────────────────────────────────────────────────

let private writeJsonString (write: Utf8JsonWriter -> unit) : string =
    use ms     = new MemoryStream()
    use writer = new Utf8JsonWriter(ms)
    write writer
    writer.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

let private serializeMedia (writer: Utf8JsonWriter) (media: MediaContent list) =
    writer.WriteStartArray()
    for m in media do
        writer.WriteStartObject()
        match m with
        | ImageFile    p -> writer.WriteString("type", "image");    writer.WriteString("path", LocalFilePath.value p)
        | AudioFile    p -> writer.WriteString("type", "audio");    writer.WriteString("path", LocalFilePath.value p)
        | DocumentFile p -> writer.WriteString("type", "document"); writer.WriteString("path", LocalFilePath.value p)
        | VideoFile    p -> writer.WriteString("type", "video");    writer.WriteString("path", LocalFilePath.value p)
        writer.WriteEndObject()
    writer.WriteEndArray()

let private serializeToolCall (writer: Utf8JsonWriter) (call: ToolCall) =
    writer.WriteStartObject()
    let (ToolCallId id)   = call.Id
    let (ToolName   name) = call.Tool
    writer.WriteString("id", id)
    writer.WriteString("tool", name)
    writer.WritePropertyName("arguments")
    writer.WriteStartObject()
    for kv in call.Arguments do
        writer.WritePropertyName(kv.Key)
        kv.Value.WriteTo(writer)
    writer.WriteEndObject()
    writer.WriteEndObject()

/// Serialize a single Message into a JSON line (no newline appended).
let serializeMessage (msg: Message) : string =
    writeJsonString (fun w ->
        w.WriteStartObject()
        match msg with
        | SystemMessage content ->
            w.WriteString("role", "system")
            w.WriteString("content", content)

        | UserMessage (content, media) ->
            w.WriteString("role", "user")
            w.WriteString("content", content)
            w.WritePropertyName("media")
            serializeMedia w media

        | AssistantMessage (content, rcOpt) ->
            w.WriteString("role", "assistant")
            w.WriteString("content", content)
            match rcOpt with
            | Some rc -> w.WriteString("reasoning_content", rc)
            | None    -> ()

        | ToolCallMessage (nel, rcOpt) ->
            w.WriteString("role", "tool_calls")
            match rcOpt with
            | Some rc -> w.WriteString("reasoning_content", rc)
            | None    -> ()
            w.WriteStartArray("calls")
            for call in NonEmptyList.toList nel do
                serializeToolCall w call
            w.WriteEndArray()

        | ToolResultMessage (id, name, content) ->
            w.WriteString("role", "tool_result")
            let (ToolCallId idStr)   = id
            let (ToolName   nameStr) = name
            w.WriteString("id", idStr)
            w.WriteString("name", nameStr)
            w.WriteString("content", content)
        w.WriteEndObject())

// ── Deserialization ───────────────────────────────────────────────────────

let private parseMediaItem (el: JsonElement) : MediaContent option =
    match tryGetString "type" el, tryGetString "path" el with
    | Some "image",    Some p -> Some (ImageFile    (LocalFilePath.ofAbsolute p))
    | Some "audio",    Some p -> Some (AudioFile    (LocalFilePath.ofAbsolute p))
    | Some "document", Some p -> Some (DocumentFile (LocalFilePath.ofAbsolute p))
    | Some "video",    Some p -> Some (VideoFile    (LocalFilePath.ofAbsolute p))
    | _                       -> None

let private parseToolCallRecord (el: JsonElement) : Result<ToolCall, ParseError> =
    result {
        let! id      = requireString "id" el
        let! toolStr = requireString "tool" el
        let argMap =
            match tryGetObject "arguments" el with
            | None     -> Map.empty
            | Some obj ->
                obj.EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.Clone())
                |> Map.ofSeq
        return {
            Id           = ToolCallId id
            Tool         = ToolName toolStr
            Arguments    = argMap
            ProviderMeta = None
        }
    }

/// Parse one JSONL line (already-parsed JsonElement) into a Message.
let parseMessageLine (el: JsonElement) : Result<Message, ParseError> =
    result {
        let! role = requireString "role" el
        match role with
        | "system" ->
            let! content = requireString "content" el
            return SystemMessage content

        | "user" ->
            let! content = requireString "content" el
            let media =
                tryGetArray "media" el
                |> Option.defaultValue []
                |> List.choose parseMediaItem
            return UserMessage (content, media)

        | "assistant" ->
            let! content = requireString "content" el
            let rc = tryGetString "reasoning_content" el
            return AssistantMessage (content, rc)

        | "tool_calls" ->
            let! callEls = requireArray "calls" el
            let! calls   = traverseResult parseToolCallRecord callEls
            let rc = tryGetString "reasoning_content" el
            match NonEmptyList.ofList calls with
            | Ok nel   -> return ToolCallMessage (nel, rc)
            | Error _  -> return! Error (SchemaError ("calls", "ToolCallMessage must have at least one call"))

        | "tool_result" ->
            let! idStr   = requireString "id" el
            let! nameStr = requireString "name" el
            let! content = requireString "content" el
            return ToolResultMessage (ToolCallId idStr, ToolName nameStr, content)

        | other ->
            return! Error (SchemaError ("role", $"unknown role '{other}'"))
    }

/// Parse a sequence of JSONL lines into a SessionSnapshot.
/// Collects all per-line errors (returning NonEmptyList on failure).
let parseSessionFile
    (id      : SessionId)
    (lines   : string seq)
    : Result<SessionSnapshot, NonEmptyList<ParseError>> =

    let now = DateTimeOffset.UtcNow
    let errors  = System.Collections.Generic.List<ParseError>()
    let messages = System.Collections.Generic.List<Message>()

    for line in lines do
        let trimmed = line.Trim()
        if trimmed <> "" then
            try
                use doc = JsonDocument.Parse(trimmed)
                match parseMessageLine doc.RootElement with
                | Ok msg -> messages.Add(msg)
                | Error e -> errors.Add(e)
            with ex ->
                errors.Add(JsonParseError (ex.Message, 0))

    if errors.Count > 0 then
        Error (errors |> Seq.toList |> NonEmptyList.ofListUnsafe)
    else
        let msgList = messages |> Seq.toList
        // SessionSnapshot.create can only fail if lastConsolidated is out of range;
        // we start with 0 which is always valid.
        match SessionSnapshot.create id msgList 0 now now with
        | Ok snap  -> Ok snap
        | Error msg -> Error (NonEmptyList.singleton (SchemaError ("session", msg)))
