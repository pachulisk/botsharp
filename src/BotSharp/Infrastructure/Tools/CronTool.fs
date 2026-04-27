module BotSharp.Infrastructure.Tools.CronTool

open System
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser
open BotSharp.Infrastructure.Input.InputParser
open BotSharp.Infrastructure.Cron.CronService

// ═══════════════════════════════════════════════════════════════════════════
// CronTool — agent-facing tool for scheduling recurring tasks
//
// Actions:
//   add    — Schedule a new cron job
//   list   — List all cron jobs
//   remove — Remove a job by ID
//   pause  — Pause a job
//   resume — Resume a paused job
//   run    — Execute a job immediately (one-shot, outside its schedule)
//
// Design notes:
//   • `schedule` is parsed with parseCronSchedule before reaching the domain.
//     Illegal schedule strings are rejected at the tool boundary, not silently
//     stored as jobs that never fire.
//   • `channel` and `chat` are explicit required parameters — the tool has no
//     access to mutable session context. The LLM must supply them.
//   • TaskId is a random GUID so no two add calls collide.
// ═══════════════════════════════════════════════════════════════════════════

// ── Tool spec ──────────────────────────────────────────────────────────────

let cronToolSpec : ToolSpec = {
    Name            = ToolName "cron"
    Description     = """Schedule or manage recurring tasks.

Actions:
  add    — Create a new cron job. Required: action, task, channel, chat.
           Provide either schedule (recurring) or at (one-time).
           schedule format: "every 30m", "daily at 09:00", "weekly Monday at 09:00",
           or a raw 5-field cron expression like "0 9 * * 1".
           at format: ISO 8601 datetime, e.g. "2026-04-25T10:30:00Z" (one-shot).
  list   — List all cron jobs. Required: action.
  remove — Remove a job. Required: action, job_id.
  update — Update mutable fields of an existing job. Required: action, job_id.
           Optional: label, task, schedule, tz, delete_after_run. Omitted fields unchanged.
  pause  — Pause a job. Required: action, job_id.
  resume — Resume a paused job. Required: action, job_id.
  run    — Run a job immediately. Required: action, job_id."""
    Parameters      = Map.ofList [
        "action",          { Type = JsString; Description = "add | list | remove | update | pause | resume | run"; Required = true }
        "task",            { Type = JsString; Description = "Task text to send when the job fires. REQUIRED for action='add'."; Required = false }
        "schedule",        { Type = JsString; Description = "When to run (recurring): 'every 30m', 'daily at 09:00', 'weekly Monday at 09:00', or a cron expression"; Required = false }
        "at",              { Type = JsString; Description = "ISO 8601 datetime for a one-time job, e.g. '2026-04-25T10:30:00Z'. Mutually exclusive with schedule."; Required = false }
        "channel",         { Type = JsString; Description = "Channel ID for job delivery (required for add)"; Required = false }
        "chat",            { Type = JsString; Description = "Chat ID for job delivery (required for add)"; Required = false }
        "label",           { Type = JsString; Description = "Human-readable label for the job (optional for add/update)"; Required = false }
        "tz",              { Type = JsString; Description = "IANA timezone for Daily/Weekly schedules (e.g. 'America/New_York'). Defaults to UTC."; Required = false }
        "job_id",          { Type = JsString; Description = "Job ID (required for remove, update, pause, resume, run)"; Required = false }
        "delete_after_run",{ Type = JsString; Description = "true to delete the job after it fires once (default false)"; Required = false }
    ]
    ConcurrencySafe = false  // modifies cron state
}

// ── Execution ──────────────────────────────────────────────────────────────

let private formatJobLine (j: CronJob) : string =
    let statusStr =
        match j.Status with
        | Active    -> "active"
        | Paused    -> "paused"
        | Completed -> "completed"
    let schedStr =
        match j.Schedule with
        | EveryN n         ->
            // Format as hours when evenly divisible, matching Python's format_timing.
            if n >= 60 && n % 60 = 0 then $"every {n / 60}h"
            else $"every {n}m"
        | Daily(h, m)      -> $"daily at {h:D2}:{m:D2}"
        | Weekly(d, h, m)  -> $"weekly {d} at {h:D2}:{m:D2}"
        | CronExpr raw     -> raw
        | Once at          -> sprintf "once at %s UTC" (at.ToString("yyyy-MM-dd HH:mm"))
    let nextStr =
        match j.NextRun with
        | None    -> "never"
        | Some dt -> dt.ToString("yyyy-MM-dd HH:mm UTC")
    let (TaskId idVal) = j.Id
    $"[{idVal.[..7]}] {j.Label} | {schedStr} | next: {nextStr} | {statusStr}"

let private tryParseBool (raw: string) =
    match raw.Trim().ToLowerInvariant() with
    | "true" | "yes" | "1" -> Some true
    | "false" | "no" | "0" -> Some false
    | _ -> None

/// Execute the cron tool with the given CronService and optional default timezone.
/// When the caller does not supply a `tz` argument, `defaultTimezone` is used
/// (Python parity: cron tool falls back to the workspace timezone from config).
let executeCron (svc: CronService) (defaultTimezone: string option) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "action" args with
        | Error e -> return ToolFailure e
        | Ok action ->
            match action.Trim().ToLowerInvariant() with

            | "add" ->
                match requireStringArg "task"    args,
                      requireStringArg "channel" args,
                      requireStringArg "chat"    args with
                | Error e, _, _ | _, Error e, _ | _, _, Error e ->
                    return ToolFailure e
                | Ok task, Ok channelRaw, Ok chatRaw ->
                    // Resolve schedule: `at` (one-time) takes precedence over `schedule` (recurring).
                    let scheduleResult =
                        match tryStringArg "at" args with
                        | Some atRaw ->
                            match DateTimeOffset.TryParse(atRaw) with
                            | true, dt -> Result.Ok (Once (dt.ToUniversalTime()), atRaw)
                            | _        -> Result.Error $"Invalid datetime '{atRaw}'. Use ISO 8601 format, e.g. '2026-04-25T10:30:00Z'."
                        | None ->
                            match tryStringArg "schedule" args with
                            | None -> Result.Error "Provide either 'schedule' (recurring) or 'at' (one-time) for add."
                            | Some schedRaw ->
                                match parseCronSchedule schedRaw with
                                | Result.Error msg -> Result.Error $"Invalid schedule '{schedRaw}': {msg}"
                                | Result.Ok sched  -> Result.Ok (sched, schedRaw)
                    match scheduleResult with
                    | Result.Error msg ->
                        return ToolFailure (ExecutionFailed msg)
                    | Result.Ok (schedule, schedDesc) ->
                        let label          = tryStringArg "label"            args |> Option.defaultValue task
                        // Python parity: fall back to workspace default timezone when tz not explicitly provided.
                        let timezone       = tryStringArg "tz" args |> Option.orElse defaultTimezone
                        let deleteAfterRun =
                            // One-time jobs default to delete_after_run = true.
                            match tryStringArg "delete_after_run" args |> Option.bind tryParseBool with
                            | Some v -> v
                            | None   ->
                                match schedule with
                                | Once _ -> true
                                | _      -> false
                        let job : CronJob = {
                            Id             = TaskId (Guid.NewGuid().ToString("N"))
                            Label          = label
                            Task           = task
                            Schedule       = schedule
                            Timezone       = timezone
                            Channel        = ChannelId channelRaw
                            Chat           = ChatId chatRaw
                            Status         = Active
                            CreatedAt      = DateTimeOffset.UtcNow
                            LastRun        = None
                            NextRun        = None
                            DeleteAfterRun = deleteAfterRun
                        }
                        let! result = svc.AddJob(job)
                        match result with
                        | Result.Ok () ->
                            let (TaskId idVal) = job.Id
                            return ToolSuccess $"Cron job created. ID: {idVal.[..7]} | label: {label} | schedule: {schedDesc}"
                        | Result.Error e ->
                            return ToolFailure (ExecutionFailed e)

            | "list" ->
                let! jobs = svc.ListJobs()
                if jobs.IsEmpty then
                    return ToolSuccess "No cron jobs scheduled."
                else
                    let lines = jobs |> List.map formatJobLine
                    return ToolSuccess (String.concat "\n" lines)

            | "remove" ->
                match requireStringArg "job_id" args with
                | Error e -> return ToolFailure e
                | Ok idRaw ->
                    let! result = svc.RemoveJob(TaskId idRaw)
                    match result with
                    | Result.Ok ()  -> return ToolSuccess $"Job {idRaw} removed."
                    | Result.Error e -> return ToolFailure (ExecutionFailed e)

            | "update" ->
                match requireStringArg "job_id" args with
                | Error e -> return ToolFailure e
                | Ok idRaw ->
                    // Parse the new schedule if provided (either `at` or `schedule`).
                    let scheduleResult : Result<CronSchedule option, string> =
                        match tryStringArg "at" args with
                        | Some atRaw ->
                            match DateTimeOffset.TryParse(atRaw) with
                            | true, dt -> Result.Ok (Some (Once (dt.ToUniversalTime())))
                            | _        -> Result.Error $"Invalid datetime '{atRaw}'. Use ISO 8601 format."
                        | None ->
                            match tryStringArg "schedule" args with
                            | None -> Result.Ok None   // no change
                            | Some schedRaw ->
                                match parseCronSchedule schedRaw with
                                | Result.Error msg -> Result.Error $"Invalid schedule '{schedRaw}': {msg}"
                                | Result.Ok sched  -> Result.Ok (Some sched)
                    match scheduleResult with
                    | Result.Error msg -> return ToolFailure (ExecutionFailed msg)
                    | Result.Ok schedOpt ->
                        let upd : CronJobUpdate = {
                            Label          = tryStringArg "label"            args
                            Task           = tryStringArg "task"             args
                            Schedule       = schedOpt
                            Timezone       = None   // tz update not yet exposed (leave unchanged)
                            DeleteAfterRun =
                                tryStringArg "delete_after_run" args
                                |> Option.bind tryParseBool
                        }
                        let! result = svc.UpdateJob(TaskId idRaw, upd)
                        match result with
                        | Result.Ok ()   -> return ToolSuccess $"Job {idRaw} updated."
                        | Result.Error e -> return ToolFailure (ExecutionFailed e)

            | "pause" ->
                match requireStringArg "job_id" args with
                | Error e -> return ToolFailure e
                | Ok idRaw ->
                    let! result = svc.PauseJob(TaskId idRaw)
                    match result with
                    | Result.Ok ()   -> return ToolSuccess $"Job {idRaw} paused."
                    | Result.Error e -> return ToolFailure (ExecutionFailed e)

            | "resume" ->
                match requireStringArg "job_id" args with
                | Error e -> return ToolFailure e
                | Ok idRaw ->
                    let! result = svc.ResumeJob(TaskId idRaw)
                    match result with
                    | Result.Ok ()   -> return ToolSuccess $"Job {idRaw} resumed."
                    | Result.Error e -> return ToolFailure (ExecutionFailed e)

            | "run" ->
                match requireStringArg "job_id" args with
                | Error e -> return ToolFailure e
                | Ok idRaw ->
                    let! result = svc.RunJobNow(TaskId idRaw)
                    match result with
                    | Result.Ok ()   -> return ToolSuccess $"Job {idRaw} executed immediately."
                    | Result.Error e -> return ToolFailure (ExecutionFailed e)

            | other ->
                return ToolFailure (ParameterInvalid ("action", $"Unknown action '{other}'. Use: add | list | remove | update | pause | resume | run"))
    }

/// All cron tools as a (spec, execute) pair, bound to the given CronService and default timezone.
/// Pass `defaultTimezone = config.Timezone` so jobs without an explicit `tz` argument
/// inherit the workspace timezone (Python parity: cron tool uses `loop.timezone` as fallback).
let allTools (svc: CronService) (defaultTimezone: string option)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ cronToolSpec, executeCron svc defaultTimezone ]
