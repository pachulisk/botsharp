module BotSharp.Infrastructure.Providers.SseParser

open FParsec
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// SSE (Server-Sent Events) line parser
//
// The OpenAI-compat streaming API sends lines in the format:
//   data: <json>      → DataLine "<json>"
//   data: [DONE]      → DoneLine
//   : <comment>       → CommentLine   (heartbeat or empty keep-alive)
//   event: <name>     → CommentLine   (ignored; we only care about data:)
//   id: <value>       → CommentLine   (event ID; ignored)
//   retry: <ms>       → CommentLine   (reconnect hint; ignored)
//   <empty>           → CommentLine   (blank separator line)
//
// Each SSE frame is a single line.  The caller iterates lines from the HTTP
// response body and calls parseSseLine on each one.
// ═══════════════════════════════════════════════════════════════════════════

/// Parse "data: [DONE]" → DoneLine
let private pDoneLine : Parser<SseFrame, unit> =
    pstring "data:" >>. spaces >>. pstring "[DONE]" >>% DoneLine

/// Parse "data: <payload>" → DataLine payload
let private pDataLine : Parser<SseFrame, unit> =
    pstring "data:" >>. spaces >>. manyChars anyChar
    |>> DataLine

/// Parse a comment (": ..."), "event: ...", "id: ...", "retry: ...", or empty line → CommentLine
let private pCommentOrIgnored : Parser<SseFrame, unit> =
    choice [
        pchar ':' >>. skipRestOfLine true >>% CommentLine
        pstring "event:"  >>. skipRestOfLine true >>% CommentLine
        pstring "id:"     >>. skipRestOfLine true >>% CommentLine
        pstring "retry:"  >>. skipRestOfLine true >>% CommentLine
        eof >>% CommentLine
    ]

let private pSseFrame : Parser<SseFrame, unit> =
    choice [
        attempt pDoneLine
        attempt pDataLine
        pCommentOrIgnored
    ]

/// Parse a single SSE wire line into a SseFrame.
/// Returns Error only on genuinely malformed input (no valid prefix at all).
let parseSseLine (line: string) : Result<SseFrame, ParseError> =
    match run pSseFrame line with
    | Success (frame, _, _) -> Result.Ok frame
    | Failure (msg, _, _)   -> Result.Error (SchemaError ("sse-line", msg))
