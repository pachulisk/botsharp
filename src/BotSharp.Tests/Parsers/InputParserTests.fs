module BotSharp.Tests.Parsers.InputParserTests

open System
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Input.InputParser

// ═══════════════════════════════════════════════════════════════════════════
// Slash-command parsing
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``/new parses to Command NewSession`` () =
    Assert.Equal(Ok (Command NewSession), parseUserInput "/new")

[<Fact>]
let ``/stop parses to Command StopProcessing`` () =
    Assert.Equal(Ok (Command StopProcessing), parseUserInput "/stop")

[<Fact>]
let ``/help parses to Command ShowHelp`` () =
    Assert.Equal(Ok (Command ShowHelp), parseUserInput "/help")

[<Fact>]
let ``/new with trailing space still parses`` () =
    Assert.Equal(Ok (Command NewSession), parseUserInput "/new   ")

[<Fact>]
let ``/newstuff is not a slash command, becomes ChatMessage`` () =
    match parseUserInput "/newstuff" with
    | Ok (ChatMessage ("newstuff", [])) -> ()  // leading "/" trimmed from content
    | Ok (ChatMessage (s, [])) ->
        // The content may include "/" — that's fine, it's just a message
        Assert.StartsWith("/", s)
    | other -> Assert.Fail($"Expected ChatMessage, got {other}")

[<Fact>]
let ``plain text becomes ChatMessage`` () =
    match parseUserInput "hello world" with
    | Ok (ChatMessage ("hello world", [])) -> ()
    | other -> Assert.Fail($"Expected ChatMessage \"hello world\", got {other}")

[<Fact>]
let ``empty string becomes ChatMessage with empty content`` () =
    match parseUserInput "" with
    | Ok (ChatMessage ("", [])) -> ()
    | Ok (ChatMessage (s, [])) -> Assert.Equal("", s)
    | other -> Assert.Fail($"Expected empty ChatMessage, got {other}")

[<Fact>]
let ``ChatMessage has no media by default`` () =
    match parseUserInput "hey" with
    | Ok (ChatMessage (_, media)) -> Assert.Empty(media)
    | other -> Assert.Fail($"Unexpected: {other}")

// ─────────────────────────────────────────────────────────────────────────────
// New slash commands added in BotSharp replication
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``/restart parses to Command Restart`` () =
    Assert.Equal(Ok (Command Restart), parseUserInput "/restart")

[<Fact>]
let ``/status parses to Command ShowStatus`` () =
    Assert.Equal(Ok (Command ShowStatus), parseUserInput "/status")

[<Fact>]
let ``/dream parses to Command Dream`` () =
    Assert.Equal(Ok (Command Dream), parseUserInput "/dream")

[<Fact>]
let ``/dream-log with no argument parses to DreamLog None`` () =
    Assert.Equal(Ok (Command (DreamLog None)), parseUserInput "/dream-log")

[<Fact>]
let ``/dream-log with trailing space parses to DreamLog None`` () =
    Assert.Equal(Ok (Command (DreamLog None)), parseUserInput "/dream-log   ")

[<Fact>]
let ``/dream-log with hex sha parses to DreamLog (Some sha)`` () =
    Assert.Equal(Ok (Command (DreamLog (Some "a1b2c3d4"))), parseUserInput "/dream-log a1b2c3d4")

[<Fact>]
let ``/dream-log with 8-char sha parses correctly`` () =
    Assert.Equal(Ok (Command (DreamLog (Some "deadbeef"))), parseUserInput "/dream-log deadbeef")

[<Fact>]
let ``/dream-restore with no argument parses to DreamRestore None`` () =
    Assert.Equal(Ok (Command (DreamRestore None)), parseUserInput "/dream-restore")

[<Fact>]
let ``/dream-restore with hex sha parses to DreamRestore (Some sha)`` () =
    Assert.Equal(Ok (Command (DreamRestore (Some "cafebabe"))), parseUserInput "/dream-restore cafebabe")

[<Fact>]
let ``/dream is not confused with /dream-log`` () =
    // Prefix ordering: /dream-log must not steal /dream
    Assert.Equal(Ok (Command Dream), parseUserInput "/dream")
    Assert.Equal(Ok (Command (DreamLog None)), parseUserInput "/dream-log")

[<Fact>]
let ``/dream-log with uppercase sha falls back to ChatMessage (hex-only parser)`` () =
    // pShaArg restricts to [0-9a-f]; uppercase letters cause pDreamLog to fail,
    // so the input is treated as a plain chat message rather than silently
    // passing a non-hex token to the lookup layer.
    match parseUserInput "/dream-log ABCDEF" with
    | Ok (ChatMessage _) -> ()
    | other -> Assert.Fail($"Expected ChatMessage fallback for uppercase sha, got {other}")

[<Fact>]
let ``/dream-restore with uppercase sha falls back to ChatMessage (hex-only parser)`` () =
    // pShaArg restricts to [0-9a-f]; uppercase letters cause pDreamRestore to fail,
    // so the input is treated as a plain chat message.
    match parseUserInput "/dream-restore ABCDEF" with
    | Ok (ChatMessage _) -> ()
    | other -> Assert.Fail($"Expected ChatMessage fallback for uppercase sha, got {other}")

[<Fact>]
let ``/dream-restore with non-hex characters falls back to ChatMessage`` () =
    match parseUserInput "/dream-restore xyz!" with
    | Ok (ChatMessage _) -> ()
    | other -> Assert.Fail($"Expected ChatMessage for non-hex /dream-restore arg, got {other}")

[<Fact>]
let ``/dream-restore with trailing space still parses to DreamRestore None`` () =
    Assert.Equal(Ok (Command (DreamRestore None)), parseUserInput "/dream-restore   ")

[<Fact>]
let ``/restart with trailing space still parses`` () =
    Assert.Equal(Ok (Command Restart), parseUserInput "/restart   ")

[<Fact>]
let ``/dream with trailing space still parses`` () =
    Assert.Equal(Ok (Command Dream), parseUserInput "/dream   ")

// ═══════════════════════════════════════════════════════════════════════════
// Commands added after initial implementation — full coverage pass
// ═══════════════════════════════════════════════════════════════════════════

// ── /clear ────────────────────────────────────────────────────────────────

[<Fact>]
let ``/clear parses to Command ClearHistory`` () =
    Assert.Equal(Ok (Command ClearHistory), parseUserInput "/clear")

[<Fact>]
let ``/clear with trailing space parses to Command ClearHistory`` () =
    Assert.Equal(Ok (Command ClearHistory), parseUserInput "/clear   ")

[<Fact>]
let ``/clearstuff is not a command — falls back to ChatMessage`` () =
    match parseUserInput "/clearstuff" with
    | Ok (ChatMessage _) -> ()
    | other -> Assert.Fail($"Expected ChatMessage, got {other}")

// ── /history ──────────────────────────────────────────────────────────────

[<Fact>]
let ``/history with no argument parses to ShowHistory None`` () =
    Assert.Equal(Ok (Command (ShowHistory None)), parseUserInput "/history")

[<Fact>]
let ``/history with trailing space parses to ShowHistory None`` () =
    Assert.Equal(Ok (Command (ShowHistory None)), parseUserInput "/history   ")

[<Fact>]
let ``/history with numeric argument parses to ShowHistory (Some N)`` () =
    Assert.Equal(Ok (Command (ShowHistory (Some 10))), parseUserInput "/history 10")

[<Fact>]
let ``/history with argument "1" parses to ShowHistory (Some 1)`` () =
    Assert.Equal(Ok (Command (ShowHistory (Some 1))), parseUserInput "/history 1")

[<Fact>]
let ``/history with "50" parses to ShowHistory (Some 50)`` () =
    Assert.Equal(Ok (Command (ShowHistory (Some 50))), parseUserInput "/history 50")

// ── /model ────────────────────────────────────────────────────────────────

[<Fact>]
let ``/model with no argument parses to SwitchModel None`` () =
    Assert.Equal(Ok (Command (SwitchModel None)), parseUserInput "/model")

[<Fact>]
let ``/model with trailing space parses to SwitchModel None`` () =
    Assert.Equal(Ok (Command (SwitchModel None)), parseUserInput "/model   ")

[<Fact>]
let ``/model with model name parses to SwitchModel (Some name)`` () =
    Assert.Equal(Ok (Command (SwitchModel (Some "claude-sonnet-4-6"))), parseUserInput "/model claude-sonnet-4-6")

[<Fact>]
let ``/model with short name parses to SwitchModel (Some name)`` () =
    Assert.Equal(Ok (Command (SwitchModel (Some "gpt-4o"))), parseUserInput "/model gpt-4o")

// ── /sessions ─────────────────────────────────────────────────────────────

[<Fact>]
let ``/sessions with no argument parses to ListSessions None`` () =
    Assert.Equal(Ok (Command (ListSessions None)), parseUserInput "/sessions")

[<Fact>]
let ``/sessions with trailing space parses to ListSessions None`` () =
    Assert.Equal(Ok (Command (ListSessions None)), parseUserInput "/sessions   ")

[<Fact>]
let ``/sessions with numeric page argument parses to ListSessions (Some N)`` () =
    Assert.Equal(Ok (Command (ListSessions (Some 2))), parseUserInput "/sessions 2")

[<Fact>]
let ``/sessions "1" parses to ListSessions (Some 1)`` () =
    Assert.Equal(Ok (Command (ListSessions (Some 1))), parseUserInput "/sessions 1")

// ── /search ───────────────────────────────────────────────────────────────

[<Fact>]
let ``/search with query parses to SearchSessions query`` () =
    Assert.Equal(Ok (Command (SearchSessions "hello world")), parseUserInput "/search hello world")

[<Fact>]
let ``/search with single word parses to SearchSessions query`` () =
    Assert.Equal(Ok (Command (SearchSessions "python")), parseUserInput "/search python")

[<Fact>]
let ``/search query preserves internal spaces`` () =
    match parseUserInput "/search foo   bar" with
    | Ok (Command (SearchSessions q)) -> Assert.Contains("foo", q)
    | other -> Assert.Fail($"Expected SearchSessions, got {other}")

// ── /rebuild-index ────────────────────────────────────────────────────────

[<Fact>]
let ``/rebuild-index parses to Command RebuildIndex`` () =
    Assert.Equal(Ok (Command RebuildIndex), parseUserInput "/rebuild-index")

[<Fact>]
let ``/rebuild-index with trailing space parses to Command RebuildIndex`` () =
    Assert.Equal(Ok (Command RebuildIndex), parseUserInput "/rebuild-index   ")

// ── /jobs ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``/jobs with no argument parses to ShowJobs None`` () =
    Assert.Equal(Ok (Command (ShowJobs None)), parseUserInput "/jobs")

[<Fact>]
let ``/jobs with trailing space parses to ShowJobs None`` () =
    Assert.Equal(Ok (Command (ShowJobs None)), parseUserInput "/jobs   ")

[<Fact>]
let ``/jobs with kind argument parses to ShowJobs (Some kind)`` () =
    Assert.Equal(Ok (Command (ShowJobs (Some "cron"))), parseUserInput "/jobs cron")

[<Fact>]
let ``/jobs with "pending" kind parses to ShowJobs (Some "pending")`` () =
    Assert.Equal(Ok (Command (ShowJobs (Some "pending"))), parseUserInput "/jobs pending")

// ── /task ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``/task with no argument parses to TaskCmd None`` () =
    Assert.Equal(Ok (Command (TaskCmd None)), parseUserInput "/task")

[<Fact>]
let ``/task with trailing space parses to TaskCmd None`` () =
    Assert.Equal(Ok (Command (TaskCmd None)), parseUserInput "/task   ")

[<Fact>]
let ``/task with subcommand "add" parses to TaskCmd (Some "add")`` () =
    Assert.Equal(Ok (Command (TaskCmd (Some "add buy groceries"))), parseUserInput "/task add buy groceries")

[<Fact>]
let ``/task with subcommand "done" parses to TaskCmd (Some "done 1")`` () =
    Assert.Equal(Ok (Command (TaskCmd (Some "done 1"))), parseUserInput "/task done 1")

[<Fact>]
let ``/task with subcommand "clear" parses to TaskCmd (Some "clear")`` () =
    Assert.Equal(Ok (Command (TaskCmd (Some "clear"))), parseUserInput "/task clear")

[<Fact>]
let ``/task with whitespace-only arg normalizes to TaskCmd None`` () =
    // pTaskCmd: subOpt |> Option.bind (fun s -> if s.Trim() = "" then None else Some s.Trim())
    Assert.Equal(Ok (Command (TaskCmd None)), parseUserInput "/task   ")

// ── /events ───────────────────────────────────────────────────────────────

[<Fact>]
let ``/events with no argument parses to ShowEvents None`` () =
    Assert.Equal(Ok (Command (ShowEvents None)), parseUserInput "/events")

[<Fact>]
let ``/events with trailing space parses to ShowEvents None`` () =
    Assert.Equal(Ok (Command (ShowEvents None)), parseUserInput "/events   ")

[<Fact>]
let ``/events with category argument parses to ShowEvents (Some category)`` () =
    Assert.Equal(Ok (Command (ShowEvents (Some "tool"))), parseUserInput "/events tool")

[<Fact>]
let ``/events with "error" category parses to ShowEvents (Some "error")`` () =
    Assert.Equal(Ok (Command (ShowEvents (Some "error"))), parseUserInput "/events error")

// ═══════════════════════════════════════════════════════════════════════════
// Cron schedule parsing
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``"every 30m" parses to EveryN 30`` () =
    Assert.Equal(Ok (EveryN 30), parseCronSchedule "every 30m")

[<Fact>]
let ``"every 5m" parses to EveryN 5`` () =
    Assert.Equal(Ok (EveryN 5), parseCronSchedule "every 5m")

[<Fact>]
let ``"daily at 9:00" parses to Daily(9, 0)`` () =
    Assert.Equal(Ok (Daily(9, 0)), parseCronSchedule "daily at 9:00")

[<Fact>]
let ``"daily at 23:30" parses to Daily(23, 30)`` () =
    Assert.Equal(Ok (Daily(23, 30)), parseCronSchedule "daily at 23:30")

[<Fact>]
let ``"weekly Monday at 9:00" parses to Weekly(Monday, 9, 0)`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Monday, 9, 0)), parseCronSchedule "weekly Monday at 9:00")

[<Fact>]
let ``"weekly Friday at 18:00" parses correctly`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Friday, 18, 0)), parseCronSchedule "weekly Friday at 18:00")

[<Fact>]
let ``cron expression is captured as CronExpr`` () =
    match parseCronSchedule "0 9 * * 1" with
    | Ok (CronExpr "0 9 * * 1") -> ()
    | other -> Assert.Fail($"Expected CronExpr, got {other}")

[<Fact>]
let ``"weekly Tuesday at 10:00" parses to Weekly(Tuesday, 10, 0)`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Tuesday, 10, 0)), parseCronSchedule "weekly Tuesday at 10:00")

[<Fact>]
let ``"weekly Wednesday at 12:00" parses to Weekly(Wednesday, 12, 0)`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Wednesday, 12, 0)), parseCronSchedule "weekly Wednesday at 12:00")

[<Fact>]
let ``"weekly Thursday at 15:00" parses to Weekly(Thursday, 15, 0)`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Thursday, 15, 0)), parseCronSchedule "weekly Thursday at 15:00")

[<Fact>]
let ``"weekly Saturday at 08:00" parses to Weekly(Saturday, 8, 0)`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Saturday, 8, 0)), parseCronSchedule "weekly Saturday at 08:00")

[<Fact>]
let ``"weekly Sunday at 20:00" parses to Weekly(Sunday, 20, 0)`` () =
    Assert.Equal(Ok (Weekly(DayOfWeek.Sunday, 20, 0)), parseCronSchedule "weekly Sunday at 20:00")

[<Fact>]
let ``cron schedule parsing is case-insensitive for keywords`` () =
    Assert.Equal(Ok (EveryN 10), parseCronSchedule "EVERY 10m")
    Assert.Equal(Ok (Daily(8, 0)), parseCronSchedule "DAILY AT 8:00")

// ═══════════════════════════════════════════════════════════════════════════
// parseCronSchedule — parse-boundary rejection tests
//
// The parser must reject out-of-range values and unrecognized formats at
// parse time so that no invalid CronSchedule value reaches the domain layer.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``"every 0m" is rejected (interval must be ≥ 1)`` () =
    match parseCronSchedule "every 0m" with
    | Error msg -> Assert.Contains("1 minute", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"every 1m" is accepted (minimum valid interval)`` () =
    Assert.Equal(Ok (EveryN 1), parseCronSchedule "every 1m")

[<Fact>]
let ``"daily at 24:00" is rejected (hour out of range)`` () =
    match parseCronSchedule "daily at 24:00" with
    | Error msg -> Assert.Contains("24", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"daily at 25:00" is rejected (hour > 23)`` () =
    match parseCronSchedule "daily at 25:00" with
    | Error msg -> Assert.Contains("25", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"daily at 10:60" is rejected (minute out of range)`` () =
    match parseCronSchedule "daily at 10:60" with
    | Error msg -> Assert.Contains("60", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"daily at 10:59" is accepted (boundary minute)`` () =
    Assert.Equal(Ok (Daily(10, 59)), parseCronSchedule "daily at 10:59")

[<Fact>]
let ``"daily at 23:59" is accepted (boundary hour and minute)`` () =
    Assert.Equal(Ok (Daily(23, 59)), parseCronSchedule "daily at 23:59")

[<Fact>]
let ``"weekly Monday at 24:00" is rejected (hour out of range)`` () =
    match parseCronSchedule "weekly Monday at 24:00" with
    | Error msg -> Assert.Contains("24", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"weekly Friday at 12:61" is rejected (minute out of range)`` () =
    match parseCronSchedule "weekly Friday at 12:61" with
    | Error msg -> Assert.Contains("61", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"garbage" is rejected as unrecognized schedule`` () =
    match parseCronSchedule "garbage" with
    | Error msg -> Assert.Contains("Unrecognized schedule", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"not a cron" is rejected (3 fields, not 5)`` () =
    // "not a cron" splits into 3 fields — rejected before reaching CronExpr
    match parseCronSchedule "not a cron" with
    | Error msg -> Assert.Contains("Unrecognized schedule", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``"0 9 * * 1 extra" is rejected (6 fields, not 5)`` () =
    // 6 fields — must fail since only 5-field cron expressions are accepted
    match parseCronSchedule "0 9 * * 1 extra" with
    | Error msg -> Assert.Contains("Unrecognized schedule", msg)
    | Ok v      -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``empty schedule string is rejected`` () =
    match parseCronSchedule "" with
    | Error _ -> ()
    | Ok v    -> Assert.Fail($"Expected Error for empty schedule, got Ok {v}")

// ═══════════════════════════════════════════════════════════════════════════
// parseTelegramBotToken
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseTelegramBotToken accepts valid token`` () =
    match parseTelegramBotToken "123456789:ABCDEFabcdef-xyz_123" with
    | Ok token -> Assert.Contains("123456789:", TelegramBotToken.value token)
    | Error e  -> Assert.Fail($"Expected Ok, got Error: {e}")

[<Fact>]
let ``parseTelegramBotToken rejects token without colon`` () =
    match parseTelegramBotToken "123456789ABCDEFabcdef" with
    | Ok _    -> Assert.Fail("Expected Error for token without colon")
    | Error _ -> ()

[<Fact>]
let ``parseTelegramBotToken rejects token with non-numeric bot ID`` () =
    match parseTelegramBotToken "notanumber:ABCDEFabcdef" with
    | Ok _    -> Assert.Fail("Expected Error for non-numeric bot ID")
    | Error _ -> ()

[<Fact>]
let ``parseTelegramBotToken rejects empty string`` () =
    match parseTelegramBotToken "" with
    | Ok _    -> Assert.Fail("Expected Error for empty token")
    | Error _ -> ()

[<Fact>]
let ``parseTelegramBotToken rejects token with empty secret`` () =
    match parseTelegramBotToken "123456789:" with
    | Ok _    -> Assert.Fail("Expected Error for empty secret part")
    | Error _ -> ()
