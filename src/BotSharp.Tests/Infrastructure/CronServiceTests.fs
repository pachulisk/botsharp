module BotSharp.Tests.Infrastructure.CronServiceTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Cron.CronService

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"cronsvc-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

let private noopFired : OnJobFired = fun _ -> async { return () }

let private makeSvc (dir: string) = CronService(dir, noopFired)

let private makeJob (id: string) (status: CronStatus) (nextRun: DateTimeOffset option) : CronJob = {
    Id             = TaskId id
    Label          = "test job"
    Task           = "do something"
    Schedule       = EveryN 30
    Timezone       = None
    Channel        = ChannelId "cli"
    Chat           = ChatId "test"
    Status         = status
    CreatedAt      = DateTimeOffset.UtcNow
    LastRun        = None
    NextRun        = nextRun
    DeleteAfterRun = false
}

let private baseNow = DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)

// ═══════════════════════════════════════════════════════════════════════════
// nextDelayMs — pure internal function
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``nextDelayMs returns 60000 for empty job list`` () =
    Assert.Equal(60_000, nextDelayMs [] baseNow)

[<Fact>]
let ``nextDelayMs returns correct ms until next active job`` () =
    let fireAt = baseNow.AddSeconds(5.0)
    let job    = makeJob "j1" Active (Some fireAt)
    let delay  = nextDelayMs [job] baseNow
    // (fireAt - baseNow) = 5000 ms
    Assert.True(delay >= 5000 && delay <= 5001, $"Expected ~5000 ms, got {delay}")

[<Fact>]
let ``nextDelayMs clamps to 1 when job NextRun is in the past`` () =
    let pastTime = baseNow.AddSeconds(-10.0)
    let job = makeJob "j1" Active (Some pastTime)
    let delay = nextDelayMs [job] baseNow
    Assert.Equal(1, delay)

[<Fact>]
let ``nextDelayMs ignores Paused jobs`` () =
    // Only the Paused job exists — should fall back to 60000 default
    let job = makeJob "j1" Paused (Some (baseNow.AddSeconds(5.0)))
    Assert.Equal(60_000, nextDelayMs [job] baseNow)

[<Fact>]
let ``nextDelayMs ignores Completed jobs`` () =
    let job = makeJob "j1" Completed (Some (baseNow.AddSeconds(5.0)))
    Assert.Equal(60_000, nextDelayMs [job] baseNow)

[<Fact>]
let ``nextDelayMs ignores jobs with no NextRun`` () =
    let job = makeJob "j1" Active None
    Assert.Equal(60_000, nextDelayMs [job] baseNow)

[<Fact>]
let ``nextDelayMs returns minimum when multiple active jobs`` () =
    let j1 = makeJob "j1" Active (Some (baseNow.AddSeconds(10.0)))   // 10000 ms
    let j2 = makeJob "j2" Active (Some (baseNow.AddSeconds(3.0)))    //  3000 ms ← minimum
    let j3 = makeJob "j3" Active (Some (baseNow.AddSeconds(20.0)))   // 20000 ms
    let delay = nextDelayMs [j1; j2; j3] baseNow
    Assert.True(delay >= 3000 && delay <= 3001, $"Expected ~3000 ms, got {delay}")

[<Fact>]
let ``nextDelayMs picks active job when mixed with paused`` () =
    let active = makeJob "j1" Active  (Some (baseNow.AddSeconds(8.0)))
    let paused = makeJob "j2" Paused  (Some (baseNow.AddSeconds(2.0)))
    let delay  = nextDelayMs [active; paused] baseNow
    Assert.True(delay >= 8000 && delay <= 8001, $"Expected ~8000 ms for active, got {delay}")

// ═══════════════════════════════════════════════════════════════════════════
// CronService — ListJobs
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ListJobs returns empty list when no jobs have been added`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Empty(jobs))

// ═══════════════════════════════════════════════════════════════════════════
// CronService — AddJob + ListJobs
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AddJob succeeds and ListJobs shows the added job`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job = makeJob "job-add-1" Active None
        let r   = svc.AddJob(job) |> Async.RunSynchronously
        Assert.True(r.IsOk, $"AddJob failed: {r}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Contains(jobs, fun (j: CronJob) -> j.Id = TaskId "job-add-1"))

[<Fact>]
let ``AddJob computes NextRun for EveryN schedule`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job = { makeJob "job-every" Active None with Schedule = EveryN 10 }
        let _   = svc.AddJob(job) |> Async.RunSynchronously
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        let stored = jobs |> List.find (fun j -> j.Id = TaskId "job-every")
        Assert.True(stored.NextRun.IsSome, "Expected NextRun to be set for EveryN schedule"))

[<Fact>]
let ``two AddJobs produce two entries in ListJobs`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let _   = svc.AddJob(makeJob "j-a" Active None) |> Async.RunSynchronously
        let _   = svc.AddJob(makeJob "j-b" Active None) |> Async.RunSynchronously
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Equal(2, jobs.Length))

// ═══════════════════════════════════════════════════════════════════════════
// CronService — RemoveJob
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``RemoveJob removes the job from ListJobs`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let _   = svc.AddJob(makeJob "job-rm" Active None) |> Async.RunSynchronously
        let r   = svc.RemoveJob(TaskId "job-rm") |> Async.RunSynchronously
        Assert.True(r.IsOk, $"RemoveJob failed: {r}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.DoesNotContain(jobs, fun (j: CronJob) -> j.Id = TaskId "job-rm"))

[<Fact>]
let ``RemoveJob returns Error for unknown job ID`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let r   = svc.RemoveJob(TaskId "no-such-job") |> Async.RunSynchronously
        match r with
        | Result.Error msg -> Assert.Contains("no-such-job", msg)
        | Result.Ok ()     -> Assert.Fail("Expected Error for unknown job ID"))

// ═══════════════════════════════════════════════════════════════════════════
// CronService — PauseJob / ResumeJob
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``PauseJob changes job status to Paused`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let _   = svc.AddJob(makeJob "job-pause" Active None) |> Async.RunSynchronously
        let r   = svc.PauseJob(TaskId "job-pause") |> Async.RunSynchronously
        Assert.True(r.IsOk, $"PauseJob failed: {r}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        let stored = jobs |> List.find (fun j -> j.Id = TaskId "job-pause")
        Assert.Equal(Paused, stored.Status))

[<Fact>]
let ``PauseJob returns Error for unknown job ID`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let r   = svc.PauseJob(TaskId "ghost") |> Async.RunSynchronously
        match r with
        | Result.Error _ -> ()
        | Result.Ok ()   -> Assert.Fail("Expected Error for unknown job ID"))

[<Fact>]
let ``ResumeJob changes paused job status back to Active`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let _   = svc.AddJob(makeJob "job-resume" Active None) |> Async.RunSynchronously
        let _   = svc.PauseJob(TaskId "job-resume") |> Async.RunSynchronously
        let r   = svc.ResumeJob(TaskId "job-resume") |> Async.RunSynchronously
        Assert.True(r.IsOk, $"ResumeJob failed: {r}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        let stored = jobs |> List.find (fun j -> j.Id = TaskId "job-resume")
        Assert.Equal(Active, stored.Status))

[<Fact>]
let ``ResumeJob returns Error for unknown job ID`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let r   = svc.ResumeJob(TaskId "ghost") |> Async.RunSynchronously
        match r with
        | Result.Error _ -> ()
        | Result.Ok ()   -> Assert.Fail("Expected Error for unknown job ID"))

// ═══════════════════════════════════════════════════════════════════════════
// CronService — RunJobNow
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``RunJobNow invokes the OnJobFired callback`` () =
    withTempDir (fun dir ->
        let mutable firedIds : string list = []
        let trackFired : OnJobFired = fun job ->
            async {
                let (TaskId id) = job.Id
                firedIds <- id :: firedIds
            }
        let svc = CronService(dir, trackFired)
        let job = makeJob "job-run" Active None
        let _   = svc.AddJob(job) |> Async.RunSynchronously
        let r   = svc.RunJobNow(TaskId "job-run") |> Async.RunSynchronously
        Assert.True(r.IsOk, $"RunJobNow failed: {r}")
        Assert.Contains("job-run", firedIds))

[<Fact>]
let ``RunJobNow returns Error for unknown job ID`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let r   = svc.RunJobNow(TaskId "ghost") |> Async.RunSynchronously
        match r with
        | Result.Error _ -> ()
        | Result.Ok ()   -> Assert.Fail("Expected Error for unknown job ID"))

// ═══════════════════════════════════════════════════════════════════════════
// CronService — RunJobNow with DeleteAfterRun
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``RunJobNow with DeleteAfterRun removes job after firing`` () =
    withTempDir (fun dir ->
        let mutable fired = false
        let trackFired : OnJobFired = fun _ -> async { fired <- true }
        let svc = CronService(dir, trackFired)
        let job = { makeJob "job-del" Active None with DeleteAfterRun = true }
        let _   = svc.AddJob(job) |> Async.RunSynchronously
        let r   = svc.RunJobNow(TaskId "job-del") |> Async.RunSynchronously
        Assert.True(r.IsOk, $"RunJobNow failed: {r}")
        Assert.True(fired, "onJobFired should have been called")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.DoesNotContain(jobs, fun (j: CronJob) -> j.Id = TaskId "job-del"))

// ═══════════════════════════════════════════════════════════════════════════
// CronService — JobsSnapshot
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``JobsSnapshot reflects jobs after AddJob`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let _   = svc.AddJob(makeJob "snap-j1" Active None) |> Async.RunSynchronously
        // Allow actor one turn to update cache
        Async.Sleep 50 |> Async.RunSynchronously
        let snap = svc.JobsSnapshot()
        Assert.Contains(snap, fun (j: CronJob) -> j.Id = TaskId "snap-j1"))

// ═══════════════════════════════════════════════════════════════════════════
// AddJob — preset NextRun preserved (the Some _ branch)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AddJob with preset NextRun does not recompute it`` () =
    withTempDir (fun dir ->
        let svc    = makeSvc dir
        let preset = DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero)
        let job    = makeJob "job-preset" Active (Some preset)
        let _      = svc.AddJob(job) |> Async.RunSynchronously
        let jobs   = svc.ListJobs() |> Async.RunSynchronously
        let stored = jobs |> List.find (fun j -> j.Id = TaskId "job-preset")
        Assert.Equal(Some preset, stored.NextRun))

// ═══════════════════════════════════════════════════════════════════════════
// AddJob — expired Once schedule leaves NextRun = None (computeJobNextRun Error branch)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AddJob with expired Once schedule leaves NextRun as None`` () =
    // Once(pastTime) → computeJobNextRun returns Error → | Result.Error _ -> job (NextRun = None)
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let expiredOnce = DateTimeOffset.UtcNow.AddHours(-1.0)
        let job = { makeJob "once-expired" Active None with Schedule = Once expiredOnce }
        let _   = svc.AddJob(job) |> Async.RunSynchronously
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        let stored = jobs |> List.find (fun j -> j.Id = TaskId "once-expired")
        Assert.True(stored.NextRun.IsNone, "Expired Once job should have NextRun = None"))

[<Fact>]
let ``ResumeJob with expired Once schedule sets Status to Active but leaves NextRun None`` () =
    // computeJobNextRun fails (Once expired) → | Result.Error _ -> { j with Status = Active }
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let expiredOnce = DateTimeOffset.UtcNow.AddHours(-1.0)
        let job = { makeJob "once-resume" Active None with Schedule = Once expiredOnce }
        let _   = svc.AddJob(job) |> Async.RunSynchronously
        // Pause it, then resume it
        let _   = svc.PauseJob(TaskId "once-resume") |> Async.RunSynchronously
        let r   = svc.ResumeJob(TaskId "once-resume") |> Async.RunSynchronously
        Assert.True(r.IsOk, $"ResumeJob failed: {r}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        let stored = jobs |> List.find (fun j -> j.Id = TaskId "once-resume")
        // Status should be Active; NextRun stays None (computeJobNextRun failed)
        Assert.Equal(Active, stored.Status)
        Assert.True(stored.NextRun.IsNone, "Expired Once job should keep NextRun = None after resume"))

// ═══════════════════════════════════════════════════════════════════════════
// Tick — automatically fires a due job
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``AddJob with past NextRun causes Tick to fire the job automatically`` () =
    withTempDir (fun dir ->
        let mutable firedCount = 0
        let trackFired : OnJobFired = fun _ ->
            async { System.Threading.Interlocked.Increment(&firedCount) |> ignore }
        let svc = CronService(dir, trackFired)
        // NextRun in the past → the Tick posted by AddJob should fire it immediately
        let job = makeJob "auto-fire" Active (Some (DateTimeOffset.UtcNow.AddSeconds(-1.0)))
        let r   = svc.AddJob(job) |> Async.RunSynchronously
        Assert.True(r.IsOk, $"AddJob failed: {r}")
        // Give the actor time to process the Tick
        Async.Sleep 300 |> Async.RunSynchronously
        Assert.Equal(1, firedCount))

[<Fact>]
let ``Tick auto-fires DeleteAfterRun job and removes it from the job list`` () =
    // The Tick handler's `elif j.DeleteAfterRun then None` branch:
    // different from the RunJobNow path which also tests DeleteAfterRun.
    withTempDir (fun dir ->
        let mutable firedCount = 0
        let trackFired : OnJobFired = fun _ ->
            async { System.Threading.Interlocked.Increment(&firedCount) |> ignore }
        let svc = CronService(dir, trackFired)
        let job = { makeJob "tick-del" Active (Some (DateTimeOffset.UtcNow.AddSeconds(-1.0))) with
                        DeleteAfterRun = true }
        let r = svc.AddJob(job) |> Async.RunSynchronously
        Assert.True(r.IsOk, $"AddJob failed: {r}")
        // Give the Tick actor time to fire and remove the job
        Async.Sleep 400 |> Async.RunSynchronously
        Assert.Equal(1, firedCount)
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.DoesNotContain(jobs, fun (j: CronJob) -> j.Id = TaskId "tick-del"))

// ─── AddJob — timezone validation ────────────────────────────────────────────

[<Fact>]
let ``AddJob rejects unknown timezone and returns Error`` () =
    withTempDir (fun dir ->
        let svc = CronService(dir, fun _ -> async { () })
        let job = { makeJob "tz-bad" Active None with Timezone = Some "America/Vancovuer" }
        let result = svc.AddJob(job) |> Async.RunSynchronously
        Assert.True(result.IsError, $"Expected Error for unknown timezone but got Ok")
        match result with
        | Result.Error msg -> Assert.Contains("Vancovuer", msg)
        | Result.Ok ()     -> ()
        // Job must NOT have been added
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Empty(jobs))

[<Fact>]
let ``AddJob accepts valid IANA timezone`` () =
    withTempDir (fun dir ->
        let svc = CronService(dir, fun _ -> async { () })
        let job = { makeJob "tz-ok" Active None with Timezone = Some "America/New_York" }
        let result = svc.AddJob(job) |> Async.RunSynchronously
        Assert.True(result.IsOk, $"Expected Ok for valid timezone but got: {result}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Equal(1, jobs.Length))

[<Fact>]
let ``AddJob with no timezone succeeds without validation`` () =
    withTempDir (fun dir ->
        let svc = CronService(dir, fun _ -> async { () })
        let job = { makeJob "tz-none" Active None with Timezone = None }
        let result = svc.AddJob(job) |> Async.RunSynchronously
        Assert.True(result.IsOk, $"Expected Ok for no timezone but got: {result}"))

// ═══════════════════════════════════════════════════════════════════════════
// UpdateJob — Python parity: test_cron_service.py test_update_job_*
// ═══════════════════════════════════════════════════════════════════════════

let private emptyUpdate : CronJobUpdate = {
    Label = None; Task = None; Schedule = None; Timezone = None; DeleteAfterRun = None }

[<Fact>]
let ``UpdateJob changes job label`` () =
    // Python parity: test_update_job_changes_name
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job = makeJob "upd-label" Active None
        svc.AddJob(job) |> Async.RunSynchronously |> ignore
        let upd = { emptyUpdate with Label = Some "new label" }
        let result = svc.UpdateJob(TaskId "upd-label", upd) |> Async.RunSynchronously
        Assert.True(result.IsOk, $"Expected Ok but got: {result}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Equal("new label", jobs.[0].Label))

[<Fact>]
let ``UpdateJob changes job schedule and recomputes NextRun`` () =
    // Python parity: test_update_job_changes_schedule
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job = makeJob "upd-sched" Active None
        svc.AddJob(job) |> Async.RunSynchronously |> ignore
        let upd = { emptyUpdate with Schedule = Some (EveryN 60) }
        let result = svc.UpdateJob(TaskId "upd-sched", upd) |> Async.RunSynchronously
        Assert.True(result.IsOk, $"Expected Ok but got: {result}")
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        let updated = List.head jobs
        Assert.Equal(EveryN 60, updated.Schedule)
        Assert.True(updated.NextRun.IsSome, "NextRun must be recomputed after schedule change"))

[<Fact>]
let ``UpdateJob changes job task text`` () =
    // Python parity: test_update_job_changes_message
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job = makeJob "upd-task" Active None
        svc.AddJob(job) |> Async.RunSynchronously |> ignore
        let upd = { emptyUpdate with Task = Some "send morning report" }
        svc.UpdateJob(TaskId "upd-task", upd) |> Async.RunSynchronously |> ignore
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        Assert.Equal("send morning report", jobs.[0].Task))

[<Fact>]
let ``UpdateJob returns Error for unknown job ID`` () =
    // Python parity: test_update_job_not_found
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let result = svc.UpdateJob(TaskId "ghost", emptyUpdate) |> Async.RunSynchronously
        Assert.True(result.IsError, "Expected Error for unknown job ID"))

[<Fact>]
let ``UpdateJob on paused job does not recompute NextRun`` () =
    // When a schedule changes on a paused job, NextRun must NOT be recomputed
    // (the job stays paused and resumes with the new schedule when explicitly resumed).
    withTempDir (fun dir ->
        let svc = makeSvc dir
        // AddJob computes an initial NextRun even for paused jobs; capture it.
        let job = makeJob "upd-paused" Paused None
        svc.AddJob(job) |> Async.RunSynchronously |> ignore
        let jobsBefore = svc.ListJobs() |> Async.RunSynchronously
        let nextRunBefore = (List.head jobsBefore).NextRun
        // Change the schedule — NextRun must remain unchanged (not recomputed to EveryN 120).
        let upd = { emptyUpdate with Schedule = Some (EveryN 120) }
        svc.UpdateJob(TaskId "upd-paused", upd) |> Async.RunSynchronously |> ignore
        let jobsAfter = svc.ListJobs() |> Async.RunSynchronously
        let updated = List.head jobsAfter
        Assert.Equal(EveryN 120, updated.Schedule)
        // NextRun unchanged: same value as before the update (not recomputed to 120-min interval).
        Assert.Equal(nextRunBefore, updated.NextRun))
