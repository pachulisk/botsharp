module BotSharp.Infrastructure.Storage.CronStore

open System
open System.IO
open System.Text
open System.Text.Json
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// CronStore — persist/load CronJob list as JSON to {workspacePath}/crons.json
//
// JSON schema for CronJob:
//   { "id": "...", "label": "...", "task": "...",
//     "schedule": { "kind": "every"|"daily"|"weekly"|"cron", ... },
//     "channel": "...", "chat": "...",
//     "status": "active"|"paused"|"completed",
//     "created_at": "<ISO 8601>",
//     "last_run": "<ISO 8601>" | null,
//     "next_run": "<ISO 8601>" | null,
//     "delete_after_run": true|false }
//
// Design: Result-returning decoders, same pattern as ConfigParser/SessionParser.
// Corrupt lines are skipped (parse → None → filtered) rather than failing the
// whole load, matching the JSONL-style fault tolerance of DreamStore.
// ═══════════════════════════════════════════════════════════════════════════

// ── CronExpr parser and evaluator ────────────────────────────────────────
//
// Supports standard 5-field unix cron syntax:
//   minute(0-59)  hour(0-23)  dom(1-31)  month(1-12)  dow(0-6, 0=Sunday)
// Each field supports: *, N, N-M, */step, N-M/step, and comma lists.
// Day-of-week names (sun, mon, …) are not supported; use 0-6 numerals.

/// Parsed representation of one cron expression. Each field is the expanded
/// set of matching values. DomStar/DowStar track the original `*` so we
/// can apply the correct POSIX dom-vs-dow OR semantics.
type private ParsedCron = {
    Minutes : Set<int>   // 0-59
    Hours   : Set<int>   // 0-23
    Doms    : Set<int>   // day-of-month 1-31
    Months  : Set<int>   // 1-12
    Dows    : Set<int>   // day-of-week  0-6
    DomStar : bool       // true when the dom field was "*"
    DowStar : bool       // true when the dow field was "*"
}

/// Parse one cron field string (e.g. "*/15" or "1-5" or "2,4,6") into the
/// set of valid integers in the range [lo..hi].
/// Returns (values, wasStar).
let private parseCronField (raw: string) (lo: int) (hi: int) : Result<Set<int> * bool, string> =
    let isStar = raw = "*"
    let mutable values : Set<int> = Set.empty
    let mutable err : string option = None

    for part in raw.Split(',') do
        if err.IsNone then
            let r : Result<int list, string> =
                if part = "*" then
                    Result.Ok [ lo..hi ]
                elif part.Contains('/') then
                    let halves = part.Split([| '/' |], 2)
                    if halves.Length <> 2 then
                        Result.Error $"Bad step syntax: {part}"
                    else
                        match Int32.TryParse(halves.[1]) with
                        | false, _ | true, 0 ->
                            Result.Error $"Step must be a positive integer: '{halves.[1]}'"
                        | true, step ->
                            // Determine the base range from the left side of '/'
                            let baseRange : Result<int * int, string> =
                                if halves.[0] = "*" then
                                    Result.Ok (lo, hi)
                                elif halves.[0].Contains('-') then
                                    let lr = halves.[0].Split([| '-' |], 2)
                                    if lr.Length <> 2 then
                                        Result.Error $"Bad range in step: {halves.[0]}"
                                    else
                                        match Int32.TryParse(lr.[0]), Int32.TryParse(lr.[1]) with
                                        | (true, a), (true, b) when a <= b && a >= lo && b <= hi ->
                                            Result.Ok (a, b)
                                        | (true, a), (true, b) ->
                                            Result.Error $"Range {a}-{b} out of [{lo}..{hi}]"
                                        | _ ->
                                            Result.Error $"Non-integer range: {halves.[0]}"
                                else
                                    match Int32.TryParse(halves.[0]) with
                                    | true, n when n >= lo && n <= hi -> Result.Ok (n, hi)
                                    | true, n -> Result.Error $"Value {n} out of [{lo}..{hi}]"
                                    | _ -> Result.Error $"Invalid: {halves.[0]}"
                            match baseRange with
                            | Result.Error e -> Result.Error e
                            | Result.Ok (a, b) -> Result.Ok [ a..step..b ]
                elif part.Contains('-') then
                    let lr = part.Split([| '-' |], 2)
                    if lr.Length <> 2 then
                        Result.Error $"Bad range: {part}"
                    else
                        match Int32.TryParse(lr.[0]), Int32.TryParse(lr.[1]) with
                        | (true, a), (true, b) when a <= b && a >= lo && b <= hi ->
                            Result.Ok [ a..b ]
                        | (true, a), (true, b) ->
                            Result.Error $"Range {a}-{b} out of [{lo}..{hi}]"
                        | _ ->
                            Result.Error $"Non-integer range: {part}"
                else
                    match Int32.TryParse(part) with
                    | true, n when n >= lo && n <= hi -> Result.Ok [ n ]
                    | true, n -> Result.Error $"Value {n} out of [{lo}..{hi}]"
                    | _ -> Result.Error $"Invalid value: {part}"
            match r with
            | Result.Error e -> err <- Some e
            | Result.Ok vs   -> values <- Set.union values (Set.ofList vs)

    match err with
    | Some e -> Result.Error e
    | None   -> Result.Ok (values, isStar)

/// Parse a 5-field cron expression string into a ParsedCron.
let private parseCronExpr (expr: string) : Result<ParsedCron, string> =
    let fs =
        expr.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    if fs.Length <> 5 then
        Result.Error $"Expected 5 fields, got {fs.Length}: \"{expr}\""
    else
        match parseCronField fs.[0] 0 59 with
        | Result.Error e -> Result.Error $"Minute field error: {e}"
        | Result.Ok (mins, _) ->
        match parseCronField fs.[1] 0 23 with
        | Result.Error e -> Result.Error $"Hour field error: {e}"
        | Result.Ok (hrs, _) ->
        match parseCronField fs.[2] 1 31 with
        | Result.Error e -> Result.Error $"Day-of-month field error: {e}"
        | Result.Ok (doms, domStar) ->
        match parseCronField fs.[3] 1 12 with
        | Result.Error e -> Result.Error $"Month field error: {e}"
        | Result.Ok (months, _) ->
        match parseCronField fs.[4] 0 6 with
        | Result.Error e -> Result.Error $"Day-of-week field error: {e}"
        | Result.Ok (dows, dowStar) ->
        Result.Ok {
            Minutes = mins
            Hours   = hrs
            Doms    = doms
            Months  = months
            Dows    = dows
            DomStar = domStar
            DowStar = dowStar
        }

/// Returns the smallest element of `s` that is strictly greater than `n`,
/// or None if all elements are ≤ n.
let private nextAbove (s: Set<int>) (n: int) : int option =
    s |> Seq.tryFind (fun v -> v > n)

/// Returns true when time `t` satisfies the day-of-month/day-of-week fields
/// using standard POSIX cron semantics:
///   dom=* and dow restricted → only dow checked
///   dow=* and dom restricted → only dom checked
///   both restricted → dom OR dow must match
let private matchesDay (cron: ParsedCron) (t: DateTimeOffset) : bool =
    match cron.DomStar, cron.DowStar with
    | true,  true  -> true
    | true,  false -> Set.contains (int t.DayOfWeek) cron.Dows
    | false, true  -> Set.contains t.Day cron.Doms
    | false, false -> Set.contains t.Day cron.Doms || Set.contains (int t.DayOfWeek) cron.Dows

/// Find the first time strictly after `after` that matches all cron fields.
/// Uses cascade advancement (skips whole months/days/hours at a time) so the
/// loop terminates quickly even for sparse expressions.
/// Returns None only if no match is found within 4 years (degenerate expressions
/// like "29 Feb" on non-leap years with dom=29 and month=2).
let private nextAfterCron (cron: ParsedCron) (after: DateTimeOffset) : DateTimeOffset option =
    // Start at the next whole minute after `after`.
    let t0 =
        DateTimeOffset(after.Year, after.Month, after.Day,
                       after.Hour, after.Minute, 0, after.Offset)
            .AddMinutes(1.0)
    let rec advance (t: DateTimeOffset) (iters: int) : DateTimeOffset option =
        if iters > 2000 then None   // safety cap — normal expressions need < 100 iterations
        elif not (Set.contains t.Month cron.Months) then
            // Advance to the first day of the next valid month.
            let nextMo = nextAbove cron.Months t.Month
            let (yr, mo) =
                match nextMo with
                | Some m -> (t.Year, m)
                | None   -> (t.Year + 1, Set.minElement cron.Months)
            advance (DateTimeOffset(yr, mo, 1, 0, 0, 0, after.Offset)) (iters + 1)
        elif not (matchesDay cron t) then
            // Advance to midnight at the start of the next day.
            let next =
                DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, after.Offset)
                    .AddDays(1.0)
            advance next (iters + 1)
        elif not (Set.contains t.Hour cron.Hours) then
            // Advance to the start of the next valid hour (or next day if none remain today).
            let next =
                match nextAbove cron.Hours t.Hour with
                | Some h ->
                    DateTimeOffset(t.Year, t.Month, t.Day, h, 0, 0, after.Offset)
                | None   ->
                    DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, after.Offset)
                        .AddDays(1.0)
            advance next (iters + 1)
        elif not (Set.contains t.Minute cron.Minutes) then
            // Advance to the next valid minute (or next hour if none remain this hour).
            let next =
                match nextAbove cron.Minutes t.Minute with
                | Some m ->
                    DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, m, 0, after.Offset)
                | None   ->
                    DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, after.Offset)
                        .AddHours(1.0)
            advance next (iters + 1)
        else
            Some t   // all fields match
    advance t0 0

// ── computeNextRun ────────────────────────────────────────────────────────

/// Validate an IANA timezone string. Returns Error when the string is not
/// recognised by the system's timezone database.
/// Mirrors Python `ZoneInfo(schedule.tz)` validation in CronService.add_job.
let validateTimezone (tz: string) : Result<unit, string> =
    try
        TimeZoneInfo.FindSystemTimeZoneById(tz) |> ignore
        Result.Ok ()
    with
    | :? TimeZoneNotFoundException
    | :? InvalidTimeZoneException ->
        Result.Error $"unknown timezone '{tz}'"

/// Resolve an IANA timezone string to a TimeZoneInfo.
/// Returns None for unknown or null strings (falls back to UTC behaviour).
let private resolveTz (tzStr: string option) : TimeZoneInfo option =
    tzStr |> Option.bind (fun s ->
        try Some (TimeZoneInfo.FindSystemTimeZoneById(s))
        with _ -> None)

/// Compute the next scheduled fire time after `after` for a given schedule.
/// `tzInfo` is used for Daily and Weekly schedules to interpret HH:MM in a
/// specific timezone; pass None to use UTC.
/// Returns Error when the cron expression is syntactically invalid or has no
/// firing time within a 4-year horizon.
let computeNextRun (schedule: CronSchedule) (after: DateTimeOffset) (tzInfo: TimeZoneInfo option) : Result<DateTimeOffset, string> =
    match schedule with
    | EveryN minutes ->
        Result.Ok (after.AddMinutes(float minutes))

    | Daily (hour, minute) ->
        // Convert `after` to the target timezone; compute next HH:MM in local time;
        // convert back to UTC. Falls back to UTC when tzInfo = None.
        let afterLocal =
            match tzInfo with
            | Some tz -> TimeZoneInfo.ConvertTime(after, tz)
            | None    -> after
        let candidate =
            DateTimeOffset(afterLocal.Year, afterLocal.Month, afterLocal.Day,
                           hour, minute, 0, afterLocal.Offset)
        let nextLocal = if candidate > afterLocal then candidate else candidate.AddDays(1.0)
        match tzInfo with
        | None    -> Result.Ok nextLocal
        | Some tz ->
            let utcDt = TimeZoneInfo.ConvertTimeToUtc(nextLocal.DateTime, tz)
            Result.Ok (DateTimeOffset(utcDt, TimeSpan.Zero))

    | Weekly (dayOfWeek, hour, minute) ->
        // Same timezone conversion as Daily, but advance to the target day-of-week.
        let afterLocal =
            match tzInfo with
            | Some tz -> TimeZoneInfo.ConvertTime(after, tz)
            | None    -> after
        let today    = int afterLocal.DayOfWeek
        let target   = int dayOfWeek
        let daysAway = (target - today + 7) % 7
        let candidate =
            DateTimeOffset(afterLocal.Year, afterLocal.Month, afterLocal.Day,
                           hour, minute, 0, afterLocal.Offset)
                .AddDays(float daysAway)
        let nextLocal = if candidate > afterLocal then candidate else candidate.AddDays(7.0)
        match tzInfo with
        | None    -> Result.Ok nextLocal
        | Some tz ->
            let utcDt = TimeZoneInfo.ConvertTimeToUtc(nextLocal.DateTime, tz)
            Result.Ok (DateTimeOffset(utcDt, TimeSpan.Zero))

    | CronExpr raw ->
        match parseCronExpr raw with
        | Result.Error e ->
            Result.Error $"Invalid cron expression \"{raw}\": {e}"
        | Result.Ok parsed ->
            match nextAfterCron parsed after with
            | None   -> Result.Error $"No fire time found within 4 years for \"{raw}\""
            | Some t -> Result.Ok t

    | Once at ->
        // One-time job: fire at the specified time. If already past, return Error so
        // the CronService leaves NextRun = None and the job never fires again.
        if at > after then Result.Ok at
        else Result.Error "One-time job has already expired."

/// Resolve a CronJob's timezone string and call computeNextRun.
let computeJobNextRun (job: CronJob) (after: DateTimeOffset) : Result<DateTimeOffset, string> =
    computeNextRun job.Schedule after (resolveTz job.Timezone)

// ── JSON serialisation ────────────────────────────────────────────────────

let private scheduleToJson (w: Utf8JsonWriter) (s: CronSchedule) =
    w.WriteStartObject("schedule")
    match s with
    | EveryN n ->
        w.WriteString("kind", "every")
        w.WriteNumber("minutes", n)
    | Daily (h, m) ->
        w.WriteString("kind", "daily")
        w.WriteNumber("hour",   h)
        w.WriteNumber("minute", m)
    | Weekly (dow, h, m) ->
        w.WriteString("kind",   "weekly")
        w.WriteString("day",    dow.ToString())
        w.WriteNumber("hour",   h)
        w.WriteNumber("minute", m)
    | CronExpr raw ->
        w.WriteString("kind", "cron")
        w.WriteString("expr", raw)
    | Once at ->
        w.WriteString("kind", "once")
        w.WriteString("at",   at.ToString("o"))
    w.WriteEndObject()

let private statusToString (s: CronStatus) =
    match s with
    | Active    -> "active"
    | Paused    -> "paused"
    | Completed -> "completed"

let private serializeJob (w: Utf8JsonWriter) (j: CronJob) =
    w.WriteStartObject()
    w.WriteString("id",      (let (TaskId v)   = j.Id      in v))
    w.WriteString("label",   j.Label)
    w.WriteString("task",    j.Task)
    scheduleToJson w j.Schedule
    match j.Timezone with
    | Some tz -> w.WriteString("timezone", tz)
    | None    -> ()
    w.WriteString("channel", (let (ChannelId v) = j.Channel in v))
    w.WriteString("chat",    (let (ChatId v)    = j.Chat    in v))
    w.WriteString("status",  statusToString j.Status)
    w.WriteString("created_at", j.CreatedAt.ToString("o"))
    match j.LastRun with
    | None    -> w.WriteNull("last_run")
    | Some dt -> w.WriteString("last_run", dt.ToString("o"))
    match j.NextRun with
    | None    -> w.WriteNull("next_run")
    | Some dt -> w.WriteString("next_run", dt.ToString("o"))
    w.WriteBoolean("delete_after_run", j.DeleteAfterRun)
    w.WriteEndObject()

// ── JSON deserialisation ──────────────────────────────────────────────────

let private tryGetString (el: JsonElement) (name: string) : string option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
    | _ -> None

let private tryGetInt (el: JsonElement) (name: string) : int option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        try Some (v.GetInt32()) with _ -> None
    | _ -> None

let private tryGetBool (el: JsonElement) (name: string) : bool option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.True  -> Some true
    | true, v when v.ValueKind = JsonValueKind.False -> Some false
    | _ -> None

let private tryGetDateTimeOffset (el: JsonElement) (name: string) : DateTimeOffset option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String ->
        match v.GetString() with
        | null -> None
        | s    ->
            match DateTimeOffset.TryParse(s) with
            | true, dt -> Some dt
            | _        -> None
    | _ -> None

let private deserializeSchedule (el: JsonElement) : CronSchedule option =
    match tryGetString el "kind" with
    | Some "every" ->
        tryGetInt el "minutes" |> Option.map EveryN
    | Some "daily" ->
        match tryGetInt el "hour", tryGetInt el "minute" with
        | Some h, Some m -> Some (Daily(h, m))
        | _              -> None
    | Some "weekly" ->
        match tryGetString el "day", tryGetInt el "hour", tryGetInt el "minute" with
        | Some dayStr, Some h, Some m ->
            match Enum.TryParse<DayOfWeek>(dayStr, ignoreCase = true) with
            | true, dow -> Some (Weekly(dow, h, m))
            | _         -> None
        | _ -> None
    | Some "cron" ->
        tryGetString el "expr" |> Option.map CronExpr
    | Some "once" ->
        tryGetDateTimeOffset el "at" |> Option.map Once
    | _ -> None

let private parseStatusString (s: string) : CronStatus option =
    match s.ToLowerInvariant() with
    | "active"    -> Some Active
    | "paused"    -> Some Paused
    | "completed" -> Some Completed
    | _           -> None

let private deserializeJob (el: JsonElement) : CronJob option =
    match el.TryGetProperty("schedule") with
    | false, _ -> None
    | true, schedEl ->
    match tryGetString el "id",
          tryGetString el "task",
          deserializeSchedule schedEl,
          tryGetString el "channel",
          tryGetString el "chat",
          tryGetString el "status" |> Option.bind parseStatusString,
          tryGetDateTimeOffset el "created_at" with
    | Some id, Some task, Some sched, Some channel, Some chat, Some status, Some createdAt ->
        Some {
            Id             = TaskId id
            Label          = tryGetString el "label" |> Option.defaultValue ""
            Task           = task
            Schedule       = sched
            Timezone       = tryGetString el "timezone"
            Channel        = ChannelId channel
            Chat           = ChatId chat
            Status         = status
            CreatedAt      = createdAt
            LastRun        = tryGetDateTimeOffset el "last_run"
            NextRun        = tryGetDateTimeOffset el "next_run"
            DeleteAfterRun = tryGetBool el "delete_after_run" |> Option.defaultValue false
        }
    | _ -> None

// ── Storage layer ─────────────────────────────────────────────────────────

let private cronFile (workspacePath: string) =
    Path.Combine(workspacePath, "crons.json")

/// Serialize CronJob list to JSON bytes.
let serializeJobs (jobs: CronJob list) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms, JsonWriterOptions(Indented = true))
    w.WriteStartArray()
    for job in jobs do serializeJob w job
    w.WriteEndArray()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

/// Load all cron jobs from disk. Returns Ok [] if file does not exist.
/// Corrupt individual entries are skipped; the rest are returned.
let loadJobs (workspacePath: string) : Async<Result<CronJob list, string>> =
    async {
        try
            let path = cronFile workspacePath
            if not (File.Exists path) then return Result.Ok []
            else
                let! text = File.ReadAllTextAsync(path) |> Async.AwaitTask
                use doc   = JsonDocument.Parse(text)
                let el    = doc.RootElement
                if el.ValueKind <> JsonValueKind.Array then
                    return Result.Error "crons.json root is not a JSON array"
                else
                    let jobs =
                        el.EnumerateArray()
                        |> Seq.choose deserializeJob
                        |> Seq.toList
                    return Result.Ok jobs
        with ex ->
            return Result.Error ex.Message
    }

/// Save all cron jobs to disk using an atomic write pattern (write to .tmp then rename).
/// Matches Python's atomic session-save approach — crash during write leaves only .tmp.
let saveJobs (workspacePath: string) (jobs: CronJob list) : Async<Result<unit, string>> =
    async {
        let dest    = cronFile workspacePath
        let tmpPath = dest + ".tmp"
        try
            if not (Directory.Exists workspacePath) then
                Directory.CreateDirectory(workspacePath) |> ignore
            let json = serializeJobs jobs
            do! File.WriteAllTextAsync(tmpPath, json) |> Async.AwaitTask
            File.Move(tmpPath, dest, overwrite = true)
            return Result.Ok ()
        with ex ->
            try if File.Exists tmpPath then File.Delete tmpPath with _ -> ()
            return Result.Error ex.Message
    }
