module BotSharp.Infrastructure.Cron.CronService

open System
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.CronStore

// ═══════════════════════════════════════════════════════════════════════════
// CronService — MailboxProcessor-based cron scheduler
//
// Architecture:
//   • One MailboxProcessor (actor) holds the mutable job list in its loop.
//   • A self-posted `Tick` wakes the actor to check which jobs are due.
//   • Adding/removing a job posts a `Tick` immediately so the timer rearms
//     to the correct next-fire time without cancellation plumbing.
//   • The callback `OnJobFired` is invoked for each due job; the caller
//     routes the job's task text to the appropriate channel.
//   • Jobs with DeleteAfterRun = true are removed after firing.
//
// Timer rearm pattern (prevents cascading ticks):
//   After each Tick the actor computes `delayMs` = ms until the next due job.
//   It schedules a single Async.Start that sleeps `delayMs` then posts Tick.
//   Early additions post Tick immediately; the old sleep eventually fires a
//   second Tick — harmless because check-due-jobs is idempotent.
// ═══════════════════════════════════════════════════════════════════════════

/// Mutable fields that can be changed on an existing cron job.
/// None means "leave unchanged". Mirrors Python's update_job signature.
type CronJobUpdate = {
    Label          : string option
    Task           : string option
    Schedule       : CronSchedule option
    Timezone       : string option option   // Some None = clear TZ; None = leave unchanged
    DeleteAfterRun : bool option
}

type CronServiceMsg =
    | AddJob     of CronJob       * AsyncReplyChannel<Result<unit, string>>
    | RemoveJob  of TaskId        * AsyncReplyChannel<Result<unit, string>>
    | UpdateJob  of TaskId * CronJobUpdate * AsyncReplyChannel<Result<unit, string>>
    | PauseJob   of TaskId        * AsyncReplyChannel<Result<unit, string>>
    | ResumeJob  of TaskId        * AsyncReplyChannel<Result<unit, string>>
    | RunJobNow  of TaskId        * AsyncReplyChannel<Result<unit, string>>
    | ListJobs   of AsyncReplyChannel<CronJob list>
    | Tick

/// Callback invoked when a job fires. The caller routes the job's task text
/// to the appropriate channel identified by job.Channel and job.Chat.
type OnJobFired = CronJob -> Async<unit>

// ── Module-level helpers (no access to class constructor params) ──────────

/// Post a Tick after `delayMs` milliseconds. Stale Ticks are harmless
/// because the check-due-jobs logic in the Tick handler is idempotent.
let internal scheduleTickAfter (mailbox: MailboxProcessor<CronServiceMsg>) (delayMs: int) =
    Async.Start (async {
        do! Async.Sleep delayMs
        mailbox.Post Tick
    })

/// Compute ms until the next active job fires (minimum 1 ms, default 60 s).
let internal nextDelayMs (jobs: CronJob list) (now: DateTimeOffset) : int =
    jobs
    |> List.choose (fun j ->
        if j.Status = Active then j.NextRun else None)
    |> List.map (fun nr -> (nr - now).TotalMilliseconds |> int |> max 1)
    |> (function [] -> 60_000 | ms -> List.min ms)

type CronService(workspacePath: string, onJobFired: OnJobFired) =

    // Snapshot of in-memory jobs, written only from the actor loop thread.
    // Reads from other threads are best-effort (accepted race for snapshot use).
    let mutable jobsCache : CronJob list = []

    let persist (jobs: CronJob list) : Async<unit> =
        async {
            let! _ = saveJobs workspacePath jobs
            return ()
        }

    let mailbox =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (jobs: CronJob list) = async {
                let! msg = inbox.Receive()
                match msg with

                | AddJob (job, reply) ->
                    // Validate timezone before accepting the job.
                    // Mirrors Python CronService.add_job → _validate_schedule timezone check.
                    let tzValidation =
                        match job.Timezone with
                        | None    -> Result.Ok ()
                        | Some tz -> validateTimezone tz
                    match tzValidation with
                    | Result.Error msg ->
                        reply.Reply (Result.Error msg)
                        return! loop jobs
                    | Result.Ok () ->
                    // Compute NextRun now if not already set.
                    let jobWithNext =
                        match job.NextRun with
                        | Some _ -> job
                        | None   ->
                            match computeJobNextRun job DateTimeOffset.UtcNow with
                            | Result.Ok next -> { job with NextRun = Some next }
                            | Result.Error _ -> job   // CronExpr/Once-expired — leave NextRun None
                    let updated = jobWithNext :: jobs
                    do! persist updated
                    jobsCache <- updated
                    reply.Reply (Result.Ok ())
                    inbox.Post Tick   // rearm timer immediately
                    return! loop updated

                | RemoveJob (id, reply) ->
                    let updated = jobs |> List.filter (fun j -> j.Id <> id)
                    if updated.Length = jobs.Length then
                        reply.Reply (Result.Error $"Job {id} not found.")
                    else
                        do! persist updated
                        jobsCache <- updated
                        reply.Reply (Result.Ok ())
                    inbox.Post Tick
                    return! loop updated

                | UpdateJob (id, upd, reply) ->
                    match jobs |> List.tryFind (fun j -> j.Id = id) with
                    | None ->
                        reply.Reply (Result.Error $"Job {id} not found.")
                        return! loop jobs
                    | Some existing ->
                        // Apply each optional field (None = leave unchanged)
                        let patched =
                            { existing with
                                Label          = upd.Label          |> Option.defaultValue existing.Label
                                Task           = upd.Task           |> Option.defaultValue existing.Task
                                Schedule       = upd.Schedule       |> Option.defaultValue existing.Schedule
                                Timezone       = upd.Timezone       |> Option.defaultValue existing.Timezone
                                DeleteAfterRun = upd.DeleteAfterRun |> Option.defaultValue existing.DeleteAfterRun }
                        // Recompute NextRun when schedule changes and job is active
                        let withNext =
                            if upd.Schedule.IsSome && patched.Status = Active then
                                match computeJobNextRun patched DateTimeOffset.UtcNow with
                                | Result.Ok next -> { patched with NextRun = Some next }
                                | Result.Error _ -> patched
                            else patched
                        let updated = jobs |> List.map (fun j -> if j.Id = id then withNext else j)
                        do! persist updated
                        jobsCache <- updated
                        reply.Reply (Result.Ok ())
                        inbox.Post Tick
                        return! loop updated

                | PauseJob (id, reply) ->
                    match jobs |> List.tryFind (fun j -> j.Id = id) with
                    | None ->
                        reply.Reply (Result.Error $"Job {id} not found.")
                        return! loop jobs
                    | Some _ ->
                        let updated =
                            jobs |> List.map (fun j ->
                                if j.Id = id then { j with Status = Paused } else j)
                        do! persist updated
                        jobsCache <- updated
                        reply.Reply (Result.Ok ())
                        return! loop updated

                | ResumeJob (id, reply) ->
                    match jobs |> List.tryFind (fun j -> j.Id = id) with
                    | None ->
                        reply.Reply (Result.Error $"Job {id} not found.")
                        return! loop jobs
                    | Some j ->
                        let withNext =
                            match computeJobNextRun j DateTimeOffset.UtcNow with
                            | Result.Ok next -> { j with Status = Active; NextRun = Some next }
                            | Result.Error _ -> { j with Status = Active }
                        let updated =
                            jobs |> List.map (fun existing ->
                                if existing.Id = id then withNext else existing)
                        do! persist updated
                        jobsCache <- updated
                        reply.Reply (Result.Ok ())
                        inbox.Post Tick
                        return! loop updated

                | RunJobNow (id, reply) ->
                    match jobs |> List.tryFind (fun j -> j.Id = id) with
                    | None ->
                        reply.Reply (Result.Error $"Job {id} not found.")
                        return! loop jobs
                    | Some job ->
                        do! onJobFired job
                        let now  = DateTimeOffset.UtcNow
                        let next = computeJobNextRun job now |> Result.toOption
                        let updated =
                            jobs |> List.choose (fun j ->
                                if j.Id <> id then Some j
                                elif j.DeleteAfterRun then None
                                else Some { j with LastRun = Some now; NextRun = next })
                        do! persist updated
                        jobsCache <- updated
                        reply.Reply (Result.Ok ())
                        return! loop updated

                | ListJobs reply ->
                    reply.Reply jobs
                    return! loop jobs

                | Tick ->
                    let now = DateTimeOffset.UtcNow
                    // Collect all active jobs whose NextRun has arrived.
                    let due =
                        jobs |> List.filter (fun j ->
                            j.Status = Active &&
                            j.NextRun |> Option.exists (fun nr -> nr <= now))

                    // Fire each due job sequentially.
                    for job in due do
                        do! onJobFired job

                    // Advance NextRun; remove DeleteAfterRun jobs that fired.
                    let updated =
                        jobs |> List.choose (fun j ->
                            let isDue = due |> List.exists (fun d -> d.Id = j.Id)
                            if not isDue then Some j
                            elif j.DeleteAfterRun then None
                            else
                                let next = computeJobNextRun j now |> Result.toOption
                                Some { j with LastRun = Some now; NextRun = next })

                    if not due.IsEmpty then
                        do! persist updated
                        jobsCache <- updated

                    // Rearm the timer.
                    let delay = nextDelayMs updated now
                    scheduleTickAfter inbox delay
                    return! loop updated
            }
            // Load persisted jobs on startup, then arm the initial timer.
            async {
                let! loaded  = loadJobs workspacePath
                let initial  =
                    match loaded with
                    | Result.Ok js -> js
                    | Result.Error _ -> []
                jobsCache <- initial
                inbox.Post Tick
                return! loop initial
            }
        )

    // ── Public API ────────────────────────────────────────────────────────

    member _.AddJob(job: CronJob) : Async<Result<unit, string>> =
        mailbox.PostAndAsyncReply(fun ch -> AddJob(job, ch))

    member _.RemoveJob(id: TaskId) : Async<Result<unit, string>> =
        mailbox.PostAndAsyncReply(fun ch -> RemoveJob(id, ch))

    member _.PauseJob(id: TaskId) : Async<Result<unit, string>> =
        mailbox.PostAndAsyncReply(fun ch -> PauseJob(id, ch))

    member _.ResumeJob(id: TaskId) : Async<Result<unit, string>> =
        mailbox.PostAndAsyncReply(fun ch -> ResumeJob(id, ch))

    member _.UpdateJob(id: TaskId, upd: CronJobUpdate) : Async<Result<unit, string>> =
        mailbox.PostAndAsyncReply(fun ch -> UpdateJob(id, upd, ch))

    member _.RunJobNow(id: TaskId) : Async<Result<unit, string>> =
        mailbox.PostAndAsyncReply(fun ch -> RunJobNow(id, ch))

    member _.ListJobs() : Async<CronJob list> =
        mailbox.PostAndAsyncReply(fun ch -> ListJobs ch)

    /// Best-effort synchronous snapshot of the in-memory job list.
    /// No mailbox round-trip — safe for display; may lag by one actor turn.
    member _.JobsSnapshot() : CronJob list = jobsCache
