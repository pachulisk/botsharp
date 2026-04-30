module BotSharp.Infrastructure.Input.InputParser

open System
open FParsec
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Slash-command parser
//
// Recognized commands (case-sensitive):
//   /new            → Command NewSession
//   /stop           → Command StopProcessing
//   /help           → Command ShowHelp
//   /restart        → Command Restart
//   /status         → Command ShowStatus
//   /dream          → Command Dream
//   /dream-log      → Command (DreamLog None)
//   /dream-log <sha>→ Command (DreamLog (Some sha))
//   /dream-restore  → Command (DreamRestore None)
//   /dream-restore <sha> → Command (DreamRestore (Some sha))
//
// Design: each no-argument command appends its own .>> spaces .>> eof guard
// so "/newstuff" is rejected.  The two parameterised commands (/dream-log,
// /dream-restore) consume an optional lowercase-hex SHA argument before eof.
// Longer-prefix alternatives must appear before /dream in the choice list.
// All alternatives use `attempt` for clean backtracking.
//
// Everything else is a ChatMessage (no media; media is added by the channel
// layer from file-path arguments rather than inline markup).
//
// Note: FParsec shadows Ok/Error — always use Result.Ok / Result.Error here.
// ═══════════════════════════════════════════════════════════════════════════

// ── No-argument commands — each guards its own end-of-input ──────────────
let private pNewSession     = pstring "/new"     .>> spaces .>> eof >>% Command NewSession
let private pClearHistory   = pstring "/clear"   .>> spaces .>> eof >>% Command ClearHistory
let private pStopProcessing = pstring "/stop"    .>> spaces .>> eof >>% Command StopProcessing
let private pShowHelp       = pstring "/help"    .>> spaces .>> eof >>% Command ShowHelp
let private pRestart        = pstring "/restart" .>> spaces .>> eof >>% Command Restart
let private pShowStatus     = pstring "/status"  .>> spaces .>> eof >>% Command ShowStatus
let private pDream          = pstring "/dream"   .>> spaces .>> eof >>% Command Dream

let private pShowHistory : Parser<UserInput, unit> =
    pstring "/history" >>. spaces >>. opt (many1Chars digit) .>> eof
    |>> (fun nOpt -> Command (ShowHistory (nOpt |> Option.map int)))

// ── Optional SHA argument: lowercase hex only ([0-9a-f]+) ────────────────
// Restricts to lowercase hex so that /dream-log ZZZ is rejected at parse
// time rather than silently returning no results at lookup time.
let private pShaArg : Parser<string option, unit> =
    spaces >>. opt (many1Chars (digit <|> anyOf "abcdef"))

let private pDreamLog : Parser<UserInput, unit> =
    pstring "/dream-log" >>. pShaArg .>> eof
    |>> (fun shaOpt -> Command (DreamLog shaOpt))

let private pDreamRestore : Parser<UserInput, unit> =
    pstring "/dream-restore" >>. pShaArg .>> eof
    |>> (fun shaOpt -> Command (DreamRestore shaOpt))

/// Slash-command choice.
/// ORDERING: longer-prefix alternatives (/dream-log, /dream-restore) before
/// /dream; all use `attempt` so a failed longer match backtracks cleanly.
let private pSlashCommand : Parser<UserInput, unit> =
    choice [
        attempt pDreamLog      // must precede pDream (shares "/dream" prefix)
        attempt pDreamRestore  // must precede pDream (shares "/dream" prefix)
        attempt pNewSession
        attempt pClearHistory
        attempt pShowHistory
        attempt pStopProcessing
        attempt pShowHelp
        attempt pRestart
        attempt pShowStatus
        attempt pDream
    ]

let private pChatMessage =
    manyChars anyChar |>> (fun s -> ChatMessage (s.Trim(), []))

/// Parse a raw user input string into a typed UserInput.
/// Slash commands take precedence; anything else is a ChatMessage.
let parseUserInput (raw: string) : Result<UserInput, string> =
    match run (attempt pSlashCommand <|> pChatMessage) raw with
    | Success (v, _, _)   -> Result.Ok v
    | Failure (msg, _, _) -> Result.Error msg

// ═══════════════════════════════════════════════════════════════════════════
// Cron schedule parser
//
// Accepted surface syntax:
//   every <N>m                        → EveryN N          (N ≥ 1)
//   daily at <HH>:<MM>                → Daily(HH, MM)     (0 ≤ HH ≤ 23, 0 ≤ MM ≤ 59)
//   weekly <DayOfWeek> at <HH>:<MM>   → Weekly(day,HH,MM) (same time range)
//   <5 whitespace-separated fields>   → CronExpr raw      (unix cron expression)
//   anything else                     → Error             (parse-time rejection)
//
// Design: `attempt` wraps only the FORMAT-matching sub-parser, NOT the value
// validation. This means "every 0m" reports "Interval must be at least 1
// minute" rather than "Unrecognized schedule" — the parser commits once the
// keyword is matched, so the specific error surfaces rather than the fallback.
//
// Free-form strings (e.g. "garbage") are rejected at parse time rather than
// silently stored as CronExpr values that would never fire.
//
// Note: FParsec shadows Ok/Error — always use Result.Ok / Result.Error here.
// ═══════════════════════════════════════════════════════════════════════════

let private pTime : Parser<int * int, unit> =
    pint32 .>> pchar ':' .>>. pint32

let private pDayOfWeek : Parser<DayOfWeek, unit> =
    choice [
        pstringCI "monday"    >>% DayOfWeek.Monday
        pstringCI "tuesday"   >>% DayOfWeek.Tuesday
        pstringCI "wednesday" >>% DayOfWeek.Wednesday
        pstringCI "thursday"  >>% DayOfWeek.Thursday
        pstringCI "friday"    >>% DayOfWeek.Friday
        pstringCI "saturday"  >>% DayOfWeek.Saturday
        pstringCI "sunday"    >>% DayOfWeek.Sunday
    ]

let private pCronSchedule : Parser<CronSchedule, unit> =
    choice [
        // `attempt` covers the keyword/format match; the `>>=` validation is
        // intentionally outside `attempt` so that once the keyword is matched
        // an out-of-range value produces a specific error instead of falling
        // through to the generic fallback.
        attempt (pstringCI "every" >>. spaces >>. pint32 .>> pchar 'm') >>= fun n ->
            if n > 0 then preturn (EveryN n)
            else fail $"Interval must be at least 1 minute, got {n}m"

        attempt (pstringCI "daily" >>. spaces >>. pstringCI "at" >>. spaces >>. pTime) >>= fun (h, m) ->
            if h >= 0 && h <= 23 && m >= 0 && m <= 59 then preturn (Daily(h, m))
            else fail $"Invalid time {h:D2}:{m:D2}: hour must be 0–23 and minute 0–59"

        attempt (pstringCI "weekly" >>. spaces
                 >>. pDayOfWeek .>> spaces
                 .>> pstringCI "at" .>> spaces
                 .>>. pTime)
            >>= fun (day, (h, m)) ->
                if h >= 0 && h <= 23 && m >= 0 && m <= 59 then preturn (Weekly(day, h, m))
                else fail $"Invalid time {h:D2}:{m:D2}: hour must be 0–23 and minute 0–59"

        // Fallback: require exactly 5 whitespace-separated fields (unix cron syntax).
        // Rejects free-form strings at parse time instead of storing a job that never fires.
        manyChars anyChar >>= fun raw ->
            let trimmed = raw.Trim()
            let fields  = trimmed.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)
            if fields.Length = 5 then preturn (CronExpr trimmed)
            else fail $"Unrecognized schedule '{trimmed}'. Use: 'every <N>m', 'daily at HH:MM', 'weekly <Day> at HH:MM', or a 5-field cron expression like '0 9 * * 1'."
    ]

/// Parse a cron schedule string into a typed CronSchedule.
/// Rejects invalid time ranges and unrecognized formats at parse time so that
/// no invalid schedule value reaches the domain layer.
let parseCronSchedule (raw: string) : Result<CronSchedule, string> =
    match run pCronSchedule (raw.Trim()) with
    | Success (v, _, _)   -> Result.Ok v
    | Failure (msg, _, _) -> Result.Error msg

// ═══════════════════════════════════════════════════════════════════════════
// Telegram bot token parser
//
// Accepted format: <numeric_bot_id>:<secret>
// Bot ID: one or more ASCII digits
// Secret: one or more of [A-Za-z0-9_-]  (base64url alphabet + underscore)
// Example: "123456789:ABCDEFGHijklmnopqrstuvwxyz-abc123"
//
// Using FParsec rather than a regex/split so that the grammar is explicit
// and the error message pinpoints exactly which component failed.
// ═══════════════════════════════════════════════════════════════════════════

let private pBotId : Parser<string, unit> =
    many1Chars digit

let private pBotSecret : Parser<string, unit> =
    many1Chars (letter <|> digit <|> pchar '_' <|> pchar '-')

let private pBotToken : Parser<TelegramBotToken, unit> =
    pBotId .>> pchar ':' .>>. pBotSecret .>> eof
    |>> (fun (id, secret) ->
            match TelegramBotToken.create (id + ":" + secret) with
            | Result.Ok t    -> t
            | Result.Error e -> failwith e)

/// Parse and structurally validate a Telegram bot token string.
/// Returns Error with a human-readable message if the format is wrong.
let parseTelegramBotToken (raw: string) : Result<TelegramBotToken, string> =
    match run pBotToken (raw.Trim()) with
    | Success (t, _, _)   -> Result.Ok t
    | Failure (msg, _, _) -> Result.Error (sprintf "Invalid Telegram bot token: %s" msg)
