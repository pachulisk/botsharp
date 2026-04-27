module BotSharp.Tests.Infrastructure.CronStoreTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.CronStore

// ═══════════════════════════════════════════════════════════════════════════
// computeNextRun — pure function, no I/O
// ═══════════════════════════════════════════════════════════════════════════

let private baseTime =
    DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)   // 2026-01-15 10:00 UTC (Thursday)

[<Fact>]
let ``computeNextRun EveryN adds the correct number of minutes`` () =
    match computeNextRun (EveryN 30) baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(baseTime.AddMinutes(30.0), next)

[<Fact>]
let ``computeNextRun EveryN 1 adds exactly 1 minute`` () =
    match computeNextRun (EveryN 1) baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next -> Assert.Equal(baseTime.AddMinutes(1.0), next)

[<Fact>]
let ``computeNextRun Daily schedules today when target time is in the future`` () =
    // baseTime = 10:00; target = 11:00 → same day
    match computeNextRun (Daily(11, 0)) baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(2026, next.Year)
        Assert.Equal(1,    next.Month)
        Assert.Equal(15,   next.Day)
        Assert.Equal(11,   next.Hour)
        Assert.Equal(0,    next.Minute)

[<Fact>]
let ``computeNextRun Daily advances to tomorrow when target time has passed`` () =
    // baseTime = 10:00; target = 09:00 (already past) → next day
    match computeNextRun (Daily(9, 0)) baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(2026, next.Year)
        Assert.Equal(1,    next.Month)
        Assert.Equal(16,   next.Day)   // +1 day
        Assert.Equal(9,    next.Hour)

[<Fact>]
let ``computeNextRun Weekly same day advances to next week when time passed`` () =
    // baseTime = Thursday 10:00; target = Thursday 09:00 (already passed) → +7 days
    match computeNextRun (Weekly(DayOfWeek.Thursday, 9, 0)) baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(DayOfWeek.Thursday, next.DayOfWeek)
        Assert.Equal(baseTime.AddDays(7.0).Day, next.Day)
        Assert.Equal(9, next.Hour)

[<Fact>]
let ``computeNextRun Weekly future day schedules correctly`` () =
    // baseTime = Thursday 10:00; target = Monday → should be Monday of next week (4 days away)
    match computeNextRun (Weekly(DayOfWeek.Monday, 9, 0)) baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek)
        Assert.Equal(9, next.Hour)
        // Monday from Thursday = 4 days ahead
        Assert.True((next - baseTime).TotalDays < 8.0)

// ── CronExpr tests ───────────────────────────────────────────────────────────
// baseTime = 2026-01-15 10:00 UTC (Thursday)

[<Fact>]
let ``computeNextRun CronExpr weekly Monday at 09:00 schedules next Monday`` () =
    // "0 9 * * 1" = Monday at 09:00. baseTime is Thursday 2026-01-15.
    // Next Monday is 2026-01-19.
    match computeNextRun (CronExpr "0 9 * * 1") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek)
        Assert.Equal(9,  next.Hour)
        Assert.Equal(0,  next.Minute)
        Assert.Equal(19, next.Day)
        Assert.Equal(1,  next.Month)
        Assert.Equal(2026, next.Year)

[<Fact>]
let ``computeNextRun CronExpr every 15 minutes fires at next quarter`` () =
    // baseTime = 10:00; next valid minute in {0,15,30,45} after minute 0 is 15.
    // t0 starts at 10:01, so next hit is 10:15.
    match computeNextRun (CronExpr "*/15 * * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(10, next.Hour)
        Assert.Equal(15, next.Minute)

[<Fact>]
let ``computeNextRun CronExpr daily midnight schedules next day 00:00`` () =
    // "0 0 * * *" = midnight every day. baseTime is 10:00, so next is next day.
    match computeNextRun (CronExpr "0 0 * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(0, next.Hour)
        Assert.Equal(0, next.Minute)
        Assert.Equal(16, next.Day)   // next day from 15th

[<Fact>]
let ``computeNextRun CronExpr specific minute and hour fires correctly`` () =
    // "30 14 * * *" = 14:30 every day. baseTime is 10:00, so same day.
    match computeNextRun (CronExpr "30 14 * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(14, next.Hour)
        Assert.Equal(30, next.Minute)
        Assert.Equal(15, next.Day)   // same day (10:00 < 14:30)

[<Fact>]
let ``computeNextRun CronExpr invalid expression returns Error`` () =
    match computeNextRun (CronExpr "not a cron") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for invalid cron expression")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr field out of range returns Error`` () =
    match computeNextRun (CronExpr "99 * * * *") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for out-of-range minute")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr range syntax fires at correct time`` () =
    // "0 8-10 * * *" = 08:00, 09:00, or 10:00. baseTime is 10:00;
    // t0 starts at 10:01, so next valid hour is 8 on the next day.
    match computeNextRun (CronExpr "0 8-10 * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.True(Set.contains next.Hour (Set.ofList [8;9;10]),
                    $"Hour {next.Hour} not in 8-10")
        Assert.Equal(0, next.Minute)

[<Fact>]
let ``computeNextRun CronExpr comma list fires at next matching minute`` () =
    // "5,35 * * * *" = at minutes 5 and 35. baseTime is 10:00 → t0 = 10:01.
    // Next matching minute is 10:05.
    match computeNextRun (CronExpr "5,35 * * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(5, next.Minute)
        Assert.Equal(10, next.Hour)

[<Fact>]
let ``computeNextRun CronExpr dom and dow are ORed when both restricted`` () =
    // "0 12 15 * 3" = 12:00 on the 15th of month OR on Wednesday, whichever comes first.
    // baseTime = 2026-01-15 (Thursday 10:00). The 15th is today but time has passed (10:00 > 12:00 not yet, but today is the 15th at 10:00 UTC, so 12:00 today should match).
    // Actually 10:00 UTC < 12:00 UTC, so today (Jan 15) at 12:00 should be the answer.
    // t0 = 10:01, dom=15 is today, dow=3(Wednesday) is not today. But DomStar=false, DowStar=false → dom OR dow.
    // Day 15 IS in {15} → matchesDay = true. Hour 10 not in {12} → advance to 12:00.
    let afterEarlyToday = DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)
    match computeNextRun (CronExpr "0 12 15 * 3") afterEarlyToday None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(15, next.Day)   // same day (dom matches)
        Assert.Equal(12, next.Hour)
        Assert.Equal(0,  next.Minute)

[<Fact>]
let ``computeNextRun CronExpr month restriction skips non-matching months`` () =
    // "0 6 1 3 *" = March 1st at 06:00. baseTime is Jan 15.
    // Next March 1 is 2026-03-01.
    match computeNextRun (CronExpr "0 6 1 3 *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(3,  next.Month)   // March
        Assert.Equal(1,  next.Day)
        Assert.Equal(6,  next.Hour)
        Assert.Equal(0,  next.Minute)
        Assert.Equal(2026, next.Year)

// ═══════════════════════════════════════════════════════════════════════════
// JSON round-trip — serializeJobs / loadJobs via temp directory
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> Async<unit>) =
    async {
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        try
            do! f dir
        finally
            try Directory.Delete(dir, recursive = true) with _ -> ()
    }

let private makeJob (label: string) (sched: CronSchedule) : CronJob = {
    Id             = TaskId (Guid.NewGuid().ToString("N"))
    Label          = label
    Task           = $"do: {label}"
    Schedule       = sched
    Timezone       = None
    Channel        = ChannelId "cli"
    Chat           = ChatId "test-chat"
    Status         = Active
    CreatedAt      = DateTimeOffset.UtcNow
    LastRun        = None
    NextRun        = Some (DateTimeOffset.UtcNow.AddHours(1.0))
    DeleteAfterRun = false
}

[<Fact>]
let ``loadJobs returns empty list when file does not exist`` () =
    withTempDir (fun dir -> async {
        let! result = loadJobs dir
        match result with
        | Result.Error e   -> Assert.Fail($"Expected Ok [], got Error: {e}")
        | Result.Ok jobs   -> Assert.Empty(jobs)
    }) |> Async.RunSynchronously

[<Fact>]
let ``saveJobs then loadJobs round-trips a single EveryN job`` () =
    withTempDir (fun dir -> async {
        let job = makeJob "my-job" (EveryN 15)
        let! saved = saveJobs dir [ job ]
        Assert.Equal(Result.Ok (), saved)

        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            Assert.Equal(1, jobs.Length)
            let j = jobs.[0]
            Assert.Equal(job.Id,    j.Id)
            Assert.Equal(job.Label, j.Label)
            Assert.Equal(job.Task,  j.Task)
            Assert.Equal(EveryN 15, j.Schedule)
            Assert.Equal(Active,    j.Status)
    }) |> Async.RunSynchronously

[<Fact>]
let ``Daily schedule round-trips hour and minute correctly`` () =
    withTempDir (fun dir -> async {
        let job = makeJob "daily-job" (Daily(9, 30))
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            match jobs.[0].Schedule with
            | Daily(h, m) ->
                Assert.Equal(9,  h)
                Assert.Equal(30, m)
            | other -> Assert.Fail($"Expected Daily, got {other}")
    }) |> Async.RunSynchronously

[<Fact>]
let ``Weekly schedule round-trips day, hour, and minute correctly`` () =
    withTempDir (fun dir -> async {
        let job = makeJob "weekly-job" (Weekly(DayOfWeek.Friday, 18, 0))
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            match jobs.[0].Schedule with
            | Weekly(dow, h, m) ->
                Assert.Equal(DayOfWeek.Friday, dow)
                Assert.Equal(18, h)
                Assert.Equal(0,  m)
            | other -> Assert.Fail($"Expected Weekly, got {other}")
    }) |> Async.RunSynchronously

[<Fact>]
let ``CronExpr schedule round-trips raw expression correctly`` () =
    withTempDir (fun dir -> async {
        let job = makeJob "cron-job" (CronExpr "0 9 * * 1")
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            match jobs.[0].Schedule with
            | CronExpr raw -> Assert.Equal("0 9 * * 1", raw)
            | other -> Assert.Fail($"Expected CronExpr, got {other}")
    }) |> Async.RunSynchronously

[<Fact>]
let ``multiple jobs are loaded in saved order`` () =
    withTempDir (fun dir -> async {
        let jobs =
            [ makeJob "first"  (EveryN 5)
              makeJob "second" (Daily(8, 0))
              makeJob "third"  (EveryN 60) ]
        let! _ = saveJobs dir jobs
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok js ->
            Assert.Equal(3, js.Length)
            Assert.Equal("first",  js.[0].Label)
            Assert.Equal("second", js.[1].Label)
            Assert.Equal("third",  js.[2].Label)
    }) |> Async.RunSynchronously

[<Fact>]
let ``Paused status round-trips correctly`` () =
    withTempDir (fun dir -> async {
        let job = { makeJob "paused-job" (EveryN 10) with Status = Paused }
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs -> Assert.Equal(Paused, jobs.[0].Status)
    }) |> Async.RunSynchronously

[<Fact>]
let ``Completed status round-trips correctly`` () =
    withTempDir (fun dir -> async {
        let job = { makeJob "done-job" (EveryN 10) with Status = Completed }
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs -> Assert.Equal(Completed, jobs.[0].Status)
    }) |> Async.RunSynchronously

[<Fact>]
let ``DeleteAfterRun=true round-trips correctly`` () =
    withTempDir (fun dir -> async {
        let job = { makeJob "once" (EveryN 1) with DeleteAfterRun = true }
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs -> Assert.True(jobs.[0].DeleteAfterRun)
    }) |> Async.RunSynchronously

[<Fact>]
let ``LastRun and NextRun None round-trip correctly`` () =
    withTempDir (fun dir -> async {
        let job = { makeJob "no-runs" (EveryN 5) with LastRun = None; NextRun = None }
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            Assert.True(jobs.[0].LastRun.IsNone)
            Assert.True(jobs.[0].NextRun.IsNone)
    }) |> Async.RunSynchronously

[<Fact>]
let ``LastRun Some DateTimeOffset round-trips within 1-second tolerance`` () =
    withTempDir (fun dir -> async {
        let now = DateTimeOffset.UtcNow
        let job = { makeJob "ran-once" (EveryN 5) with LastRun = Some now }
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            match jobs.[0].LastRun with
            | None    -> Assert.Fail("Expected Some, got None")
            | Some dt -> Assert.True(abs (dt - now).TotalSeconds < 1.0)
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// Once schedule — computeNextRun + round-trip
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``computeNextRun Once returns Ok when at is in the future`` () =
    let future = baseTime.AddHours(2.0)
    match computeNextRun (Once future) baseTime None with
    | Result.Ok next  -> Assert.Equal(future, next)
    | Result.Error e  -> Assert.Fail($"Expected Ok, got Error: {e}")

[<Fact>]
let ``computeNextRun Once returns Error when at is in the past`` () =
    let past = baseTime.AddHours(-1.0)
    match computeNextRun (Once past) baseTime None with
    | Result.Ok _     -> Assert.Fail("Expected Error for expired Once schedule, got Ok")
    | Result.Error _  -> ()   // expected

[<Fact>]
let ``computeNextRun Once returns Error when at equals after`` () =
    match computeNextRun (Once baseTime) baseTime None with
    | Result.Ok _     -> Assert.Fail("Expected Error when at = after, got Ok")
    | Result.Error _  -> ()   // expected: not strictly in the future

[<Fact>]
let ``Once schedule round-trips datetime correctly`` () =
    withTempDir (fun dir -> async {
        let at  = DateTimeOffset(2026, 6, 15, 14, 30, 0, TimeSpan.Zero)
        let job = makeJob "one-shot" (Once at)
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            match jobs.[0].Schedule with
            | Once roundTripped ->
                Assert.True(abs (roundTripped - at).TotalSeconds < 1.0)
            | other -> Assert.Fail($"Expected Once, got {other}")
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// Timezone-aware computeNextRun
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``computeNextRun Daily with UTC-5 timezone adjusts UTC correctly`` () =
    // baseTime = 2026-01-15 10:00 UTC = 05:00 EST (UTC-5)
    // Daily at 09:00 EST means 14:00 UTC the same day (since 05:00 < 09:00).
    let estz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
    match computeNextRun (Daily(9, 0)) baseTime (Some estz) with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        // 09:00 EST = 14:00 UTC (in January when EST = UTC-5, no DST)
        Assert.Equal(14, next.Hour)
        Assert.Equal(0,  next.Minute)
        Assert.Equal(baseTime.Day, next.Day)

[<Fact>]
let ``Timezone field round-trips through saveJobs/loadJobs`` () =
    withTempDir (fun dir -> async {
        let job = { makeJob "tz-job" (Daily(9, 0)) with Timezone = Some "America/New_York" }
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs -> Assert.Equal(Some "America/New_York", jobs.[0].Timezone)
    }) |> Async.RunSynchronously

[<Fact>]
let ``Timezone None round-trips as absent field`` () =
    withTempDir (fun dir -> async {
        let job = makeJob "no-tz" (Daily(9, 0))
        let! _ = saveJobs dir [ job ]
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs -> Assert.Equal(None, jobs.[0].Timezone)
    }) |> Async.RunSynchronously

[<Fact>]
let ``overwriting crons.json with saveJobs replaces all previous jobs`` () =
    withTempDir (fun dir -> async {
        let old  = [ makeJob "old-job" (EveryN 5) ]
        let new_ = [ makeJob "new-job" (EveryN 10); makeJob "another" (Daily(7, 0)) ]
        let! _ = saveJobs dir old
        let! _ = saveJobs dir new_
        let! loaded = loadJobs dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadJobs failed: {e}")
        | Result.Ok jobs ->
            Assert.Equal(2, jobs.Length)
            Assert.Equal("new-job", jobs.[0].Label)
            Assert.Equal("another", jobs.[1].Label)
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// parseCronField — step, range, and per-field out-of-range branches
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``computeNextRun CronExpr step=0 returns Error`` () =
    // parseCronField: halves.[1]="0" → | true, 0 -> Result.Error "Step must be…"
    match computeNextRun (CronExpr "*/0 * * * *") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for step=0")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr step with range base fires at first step value`` () =
    // "0-30/15 * * * *" → halves.[0]="0-30" contains '-' → baseRange=(0,30)
    // → minutes {0,15,30}; baseTime=10:00 UTC, t0=10:01 → next minute >0 in set = 15
    match computeNextRun (CronExpr "0-30/15 * * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(10, next.Hour)
        Assert.Equal(15, next.Minute)

[<Fact>]
let ``computeNextRun CronExpr step with plain-number base fires at base`` () =
    // "5/15 * * * *" → halves.[0]="5" not '*' not range → baseRange=(5,59)
    // → minutes {5,20,35,50}; baseTime=10:00, t0=10:01 → next minute >0 in set = 5
    match computeNextRun (CronExpr "5/15 * * * *") baseTime None with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(10, next.Hour)
        Assert.Equal(5,  next.Minute)

[<Fact>]
let ``computeNextRun CronExpr range out of bounds returns Error`` () =
    // "10-100 * * * *" → a=10, b=100 > hi(59) → Error "Range 10-100 out of [0..59]"
    match computeNextRun (CronExpr "10-100 * * * *") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for range 10-100")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr hour field out of range returns Error`` () =
    // parseCronField fs.[1] 0 23 with "25": Value 25 > hi(23) → "Hour field error"
    match computeNextRun (CronExpr "0 25 * * *") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for hour=25")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr dom field out of range returns Error`` () =
    // parseCronField fs.[2] 1 31 with "32": Value 32 > hi(31) → "Day-of-month field error"
    match computeNextRun (CronExpr "0 0 32 * *") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for dom=32")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr month field out of range returns Error`` () =
    // parseCronField fs.[3] 1 12 with "13": Value 13 > hi(12) → "Month field error"
    match computeNextRun (CronExpr "0 0 * 13 *") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for month=13")
    | Result.Error _ -> ()

[<Fact>]
let ``computeNextRun CronExpr dow field out of range returns Error`` () =
    // parseCronField fs.[4] 0 6 with "7": Value 7 > hi(6) → "Day-of-week field error"
    match computeNextRun (CronExpr "0 0 * * 7") baseTime None with
    | Result.Ok _    -> Assert.Fail("Expected Error for dow=7")
    | Result.Error _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// loadJobs — non-array root and skipped-entry paths
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadJobs returns Error when crons.json root is not a JSON array`` () =
    // el.ValueKind <> JsonValueKind.Array → Result.Error "crons.json root is not a JSON array"
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "crons.json")
        File.WriteAllText(path, """{"not":"an array"}""")
        let! result = loadJobs dir
        match result with
        | Result.Error msg -> Assert.Contains("not a JSON array", msg)
        | Result.Ok _      -> Assert.Fail("Expected Error for non-array root")
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadJobs skips job with unknown schedule kind and returns empty list`` () =
    // deserializeSchedule: kind="hourly" → | _ -> None → deserializeJob → None → skipped
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "crons.json")
        let json = """[{"id":"x","label":"x","task":"do","schedule":{"kind":"hourly"},"channel":"cli","chat":"c","status":"active","created_at":"2026-01-15T10:00:00+00:00","delete_after_run":false}]"""
        File.WriteAllText(path, json)
        let! result = loadJobs dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok [], got Error: {e}")
        | Result.Ok jobs -> Assert.Empty(jobs)
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadJobs skips job with unknown status string and returns empty list`` () =
    // parseStatusString "unknown" → | _ -> None → deserializeJob → None → skipped
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "crons.json")
        let json = """[{"id":"x","label":"x","task":"do","schedule":{"kind":"every","minutes":5},"channel":"cli","chat":"c","status":"unknown","created_at":"2026-01-15T10:00:00+00:00","delete_after_run":false}]"""
        File.WriteAllText(path, json)
        let! result = loadJobs dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok [], got Error: {e}")
        | Result.Ok jobs -> Assert.Empty(jobs)
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// resolveTz / computeJobNextRun — timezone edge cases
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``computeJobNextRun falls back to UTC when timezone string is invalid`` () =
    // resolveTz (Some "Not/AValidTz"): FindSystemTimeZoneById throws → with _ -> None
    // → computeNextRun tzInfo=None → UTC behaviour for Daily(11, 0)
    let job = { makeJob "bad-tz" (Daily(11, 0)) with Timezone = Some "Not/AValidTz" }
    match computeJobNextRun job baseTime with
    | Result.Error e -> Assert.Fail($"Expected Ok (UTC fallback), got Error: {e}")
    | Result.Ok next ->
        // UTC: Daily(11,0) with baseTime=10:00 UTC → same day 11:00 UTC
        Assert.Equal(11, next.Hour)
        Assert.Equal(0,  next.Minute)
        Assert.Equal(baseTime.Day, next.Day)

[<Fact>]
let ``computeNextRun Weekly with timezone converts fire time to UTC`` () =
    // baseTime = 2026-01-15 (Thursday) 10:00 UTC = 05:00 EST (UTC-5)
    // Weekly(Monday, 9, 0) EST: next Monday Jan 19, 09:00 EST = 14:00 UTC
    let estz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
    match computeNextRun (Weekly(DayOfWeek.Monday, 9, 0)) baseTime (Some estz) with
    | Result.Error e -> Assert.Fail($"Unexpected error: {e}")
    | Result.Ok next ->
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek)
        Assert.Equal(14, next.Hour)    // 09:00 EST = 14:00 UTC (EST = UTC-5, no DST in January)
        Assert.Equal(0,  next.Minute)
        Assert.Equal(19, next.Day)     // Jan 19 = next Monday from Jan 15

// ─── validateTimezone ─────────────────────────────────────────────────────────

[<Fact>]
let ``validateTimezone returns Ok for a valid IANA timezone`` () =
    match validateTimezone "America/New_York" with
    | Result.Ok ()  -> ()
    | Result.Error e -> Assert.Fail($"Expected Ok but got Error: {e}")

[<Fact>]
let ``validateTimezone returns Ok for UTC`` () =
    match validateTimezone "UTC" with
    | Result.Ok ()  -> ()
    | Result.Error e -> Assert.Fail($"Expected Ok but got Error: {e}")

[<Fact>]
let ``validateTimezone returns Error for unknown timezone`` () =
    match validateTimezone "America/Vancovuer" with
    | Result.Error msg -> Assert.Contains("Vancovuer", msg)
    | Result.Ok ()     -> Assert.Fail("Expected Error for misspelled timezone")

[<Fact>]
let ``validateTimezone returns Error for completely bogus string`` () =
    match validateTimezone "Not/A/Timezone" with
    | Result.Error _ -> ()
    | Result.Ok ()   -> Assert.Fail("Expected Error for invalid timezone string")
