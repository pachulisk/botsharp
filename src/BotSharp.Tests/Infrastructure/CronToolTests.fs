module BotSharp.Tests.Infrastructure.CronToolTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Cron.CronService
open BotSharp.Infrastructure.Tools.CronTool

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"crontool-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

/// No-op job fired callback — tests don't need job delivery.
let private noopFired : OnJobFired = fun _ -> async { return () }

let private makeSvc (dir: string) = CronService(dir, noopFired)

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private makeArgs (pairs: (string * string) list) : Map<string, JsonElement> =
    pairs |> List.map (fun (k, v) -> k, jsonStr v) |> Map.ofList

// ═══════════════════════════════════════════════════════════════════════════
// cronToolSpec — schema correctness
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``cronToolSpec has correct tool name`` () =
    let (ToolName n) = cronToolSpec.Name
    Assert.Equal("cron", n)

[<Fact>]
let ``cronToolSpec requires action parameter`` () =
    let param = cronToolSpec.Parameters.["action"]
    Assert.True(param.Required)
    Assert.Equal(JsString, param.Type)

[<Fact>]
let ``cronToolSpec task and job_id are NOT globally required (list/remove must be callable without them)`` () =
    // Python parity: test_top_level_required_stays_narrow — only "action" should be required.
    // If task or job_id creep into Required=true, 'list' and 'remove' fail schema validation.
    Assert.False(cronToolSpec.Parameters.["task"].Required)
    Assert.False(cronToolSpec.Parameters.["job_id"].Required)

[<Fact>]
let ``cronToolSpec task description mentions it is required for add`` () =
    // Python parity: TestSchemaSelfDescribesRequirements.test_message_description_flags_add_requirement
    // LLMs rely on field descriptions to infer when something is actually needed.
    let desc = cronToolSpec.Parameters.["task"].Description
    Assert.Contains("REQUIRED", desc)
    Assert.Contains("add", desc)

[<Fact>]
let ``cronToolSpec job_id description mentions it is required for remove`` () =
    // Python parity: TestSchemaSelfDescribesRequirements.test_job_id_description_flags_remove_requirement
    let desc = cronToolSpec.Parameters.["job_id"].Description
    Assert.Contains("required", desc, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("remove", desc)

[<Fact>]
let ``executeCron add without task returns error naming the missing parameter`` () =
    // Python parity: test_add_without_message_surfaces_actionable_runtime_error
    // The error must tell the LLM what is missing so it does not loop.
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "add"; "channel", "cli"; "chat", "x"; "at", "2030-01-01T00:00:00Z" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ParameterMissing "task") -> ()   // correct: names the missing field
        | ToolFailure (ParameterMissing f) ->
            Assert.Fail($"Expected ParameterMissing 'task', got ParameterMissing '{f}'")
        | other ->
            Assert.Fail($"Expected ToolFailure(ParameterMissing 'task') for add without task, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — unknown action
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron returns ParameterInvalid for unknown action`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "bogus" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ParameterInvalid ("action", msg)) ->
            Assert.Contains("bogus", msg)
        | other -> Assert.Fail($"Expected ParameterInvalid, got {other}"))

[<Fact>]
let ``executeCron returns ToolFailure when action argument is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = Map.empty
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing action, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — list (empty)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron list returns 'No cron jobs' when service is empty`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "list" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("No cron jobs", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with empty list, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — add: validation failures (no CronService call for these)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron add returns ToolFailure when task is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "add"; "channel", "cli"; "chat", "x"; "schedule", "every 30m" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing task, got {other}"))

[<Fact>]
let ``executeCron add returns ToolFailure when channel is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "add"; "task", "do something"; "chat", "x"; "schedule", "every 30m" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing channel, got {other}"))

[<Fact>]
let ``executeCron add accepts valid 5-field cron expression as CronExpr`` () =
    // parseCronSchedule requires exactly 5 whitespace-separated fields for CronExpr;
    // "0 9 * * 1" (fire at 09:00 on Mondays) is a valid unix cron expression and passes
    // the field-count gate. CronService computes NextRun from it at scheduling time.
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",   "add"
            "task",     "check things"
            "channel",  "cli"
            "chat",     "chat1"
            "schedule", "0 9 * * 1" ]   // valid 5-field cron expr → CronExpr "0 9 * * 1"
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("Cron job created", msg)
        | other -> Assert.Fail($"Expected ToolSuccess for raw cron expr, got {other}"))

[<Fact>]
let ``executeCron add returns ToolFailure when neither schedule nor at is provided`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",  "add"
            "task",    "check things"
            "channel", "cli"
            "chat",    "chat1" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed msg) ->
            Assert.True(
                msg.Contains("schedule") || msg.Contains("at"),
                $"Expected error mentioning 'schedule' or 'at', got: {msg}")
        | other -> Assert.Fail($"Expected ToolFailure, got {other}"))

[<Fact>]
let ``executeCron add returns ToolFailure for invalid ISO 8601 'at' datetime`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",  "add"
            "task",    "check things"
            "channel", "cli"
            "chat",    "chat1"
            "at",      "not-a-date" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("not-a-date", msg)
        | other -> Assert.Fail($"Expected ToolFailure for invalid at datetime, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — add: success paths
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron add succeeds with valid 'every Nm' schedule`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",   "add"
            "task",     "check things"
            "channel",  "cli"
            "chat",     "chat1"
            "schedule", "every 30m" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains("Cron job created", msg)
            Assert.Contains("ID:", msg)
        | other -> Assert.Fail($"Expected ToolSuccess for valid schedule, got {other}"))

[<Fact>]
let ``executeCron add succeeds with valid ISO 8601 'at' parameter`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let futureDate = DateTimeOffset.UtcNow.AddDays(1.0).ToString("o")
        let args = makeArgs [
            "action",  "add"
            "task",    "one-shot task"
            "channel", "cli"
            "chat",    "chat1"
            "at",      futureDate ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("Cron job created", msg)
        | other -> Assert.Fail($"Expected ToolSuccess for 'at' param, got {other}"))

[<Fact>]
let ``executeCron list shows added job after add`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        // Add a job
        let addArgs = makeArgs [
            "action",   "add"
            "task",     "my recurring check"
            "channel",  "cli"
            "chat",     "chat1"
            "schedule", "every 10m" ]
        let _ = executeCron svc None addArgs |> Async.RunSynchronously
        // Wait a moment for the async AddJob to complete
        Async.Sleep 100 |> Async.RunSynchronously
        // List
        let listArgs = makeArgs [ "action", "list" ]
        let result = executeCron svc None listArgs |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("my recurring check", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with job in list, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — remove/pause/resume: missing job_id
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron remove returns ToolFailure when job_id is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "remove" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing job_id, got {other}"))

[<Fact>]
let ``executeCron pause returns ToolFailure when job_id is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "pause" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing job_id, got {other}"))

[<Fact>]
let ``executeCron resume returns ToolFailure when job_id is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "resume" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing job_id, got {other}"))

[<Fact>]
let ``executeCron run returns ToolFailure when job_id is missing`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "run" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing job_id, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — add: optional parameters (label, tz, delete_after_run)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron add with custom label shows label in confirmation`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",   "add"
            "task",     "do the thing"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "every 15m"
            "label",    "my-label" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("my-label", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with label, got {other}"))

[<Fact>]
let ``executeCron add with delete_after_run=true shows job created`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",          "add"
            "task",            "one-time"
            "channel",         "cli"
            "chat",            "c1"
            "schedule",        "every 60m"
            "delete_after_run","true" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("Cron job created", msg)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}"))

[<Fact>]
let ``executeCron add with daily schedule succeeds`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",   "add"
            "task",     "daily report"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "daily at 09:00" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("Cron job created", msg)
        | other -> Assert.Fail($"Expected ToolSuccess for daily schedule, got {other}"))

[<Fact>]
let ``executeCron add with weekly schedule succeeds`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",   "add"
            "task",     "weekly report"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "weekly Monday at 09:00" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("Cron job created", msg)
        | other -> Assert.Fail($"Expected ToolSuccess for weekly schedule, got {other}"))

[<Fact>]
let ``executeCron list shows paused status after pause`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let addArgs = makeArgs [
            "action",   "add"
            "task",     "check status"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "every 10m" ]
        let _ = executeCron svc None addArgs |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        // Pause the job
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        match jobs with
        | [] -> Assert.Fail("Expected a job")
        | job :: _ ->
            let (TaskId jid) = job.Id
            let _ = executeCron svc None (makeArgs [ "action", "pause"; "job_id", jid ]) |> Async.RunSynchronously
            Async.Sleep 100 |> Async.RunSynchronously
            // Now list — should show "paused"
            let listResult = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
            match listResult with
            | ToolSuccess msg -> Assert.Contains("paused", msg)
            | other -> Assert.Fail($"Expected ToolSuccess with paused in output, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — remove/pause/resume/run: success paths
// ═══════════════════════════════════════════════════════════════════════════

/// Helper: add a test job and return its string ID.
let private addTestJob (svc: CronService) (id: string) =
    let addArgs = makeArgs [
        "action",   "add"
        "task",     $"test task {id}"
        "channel",  "cli"
        "chat",     "c"
        "schedule", "every 5m" ]
    let _ = executeCron svc None addArgs |> Async.RunSynchronously
    Async.Sleep 100 |> Async.RunSynchronously   // let actor process the add

[<Fact>]
let ``executeCron remove successfully removes a job`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        addTestJob svc "rm-1"
        // Find the job ID from the list
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        match jobs with
        | [] -> Assert.Fail("Expected at least one job after add")
        | job :: _ ->
            let (TaskId jid) = job.Id
            let args = makeArgs [ "action", "remove"; "job_id", jid ]
            let result = executeCron svc None args |> Async.RunSynchronously
            match result with
            | ToolSuccess msg -> Assert.Contains("removed", msg.ToLowerInvariant())
            | other -> Assert.Fail($"Expected ToolSuccess for remove, got {other}"))

[<Fact>]
let ``executeCron pause and resume change job status`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        addTestJob svc "pr-1"
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        match jobs with
        | [] -> Assert.Fail("Expected at least one job")
        | job :: _ ->
            let (TaskId jid) = job.Id
            // Pause
            let pauseArgs = makeArgs [ "action", "pause"; "job_id", jid ]
            let pr = executeCron svc None pauseArgs |> Async.RunSynchronously
            match pr with
            | ToolFailure e -> Assert.Fail($"Pause failed: {e}")
            | ToolSuccess _ -> ()
            // Resume
            let resumeArgs = makeArgs [ "action", "resume"; "job_id", jid ]
            let rr = executeCron svc None resumeArgs |> Async.RunSynchronously
            match rr with
            | ToolFailure e -> Assert.Fail($"Resume failed: {e}")
            | ToolSuccess _ -> ())

[<Fact>]
let ``executeCron run immediately fires the job`` () =
    withTempDir (fun dir ->
        let mutable firedIds : string list = []
        let trackFired : OnJobFired = fun job ->
            async {
                let (TaskId id) = job.Id
                firedIds <- id :: firedIds
            }
        let svc = CronService(dir, trackFired)
        addTestJob svc "run-1"
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        match jobs with
        | [] -> Assert.Fail("Expected at least one job")
        | job :: _ ->
            let (TaskId jid) = job.Id
            let args = makeArgs [ "action", "run"; "job_id", jid ]
            let result = executeCron svc None args |> Async.RunSynchronously
            match result with
            | ToolSuccess msg ->
                Assert.Contains("executed", msg.ToLowerInvariant())
                Assert.Contains(jid, firedIds)
            | other -> Assert.Fail($"Expected ToolSuccess for run, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — remove/pause/resume/run: service-level error paths
// (job_id supplied but job does not exist in the service)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron remove returns ToolFailure(ExecutionFailed) when job does not exist in service`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "remove"; "job_id", "nonexistent-job" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed _) -> ()
        | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed) for unknown job, got {other}"))

[<Fact>]
let ``executeCron pause returns ToolFailure(ExecutionFailed) when job does not exist in service`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "pause"; "job_id", "nonexistent-job" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed _) -> ()
        | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed) for unknown job, got {other}"))

[<Fact>]
let ``executeCron resume returns ToolFailure(ExecutionFailed) when job does not exist in service`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "resume"; "job_id", "nonexistent-job" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed _) -> ()
        | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed) for unknown job, got {other}"))

[<Fact>]
let ``executeCron run returns ToolFailure(ExecutionFailed) when job does not exist in service`` () =
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "run"; "job_id", "nonexistent-job" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed _) -> ()
        | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed) for unknown job, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — formatJobLine: Completed status branch
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron list shows 'completed' status for a Completed job`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job : CronJob = {
            Id             = TaskId "completed-job-001"
            Label          = "completed task"
            Task           = "run once"
            Schedule       = EveryN 30
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Completed
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = None
            DeleteAfterRun = false
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let args = makeArgs [ "action", "list" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("completed", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with 'completed' status, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — formatJobLine: Once schedule and NextRun Some branches
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron list shows 'once at' for a Once-schedule job`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let futureAt = DateTimeOffset.UtcNow.AddDays(1.0)
        let job : CronJob = {
            Id             = TaskId "once-job-001"
            Label          = "one-time task"
            Task           = "run once"
            Schedule       = Once futureAt
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Active
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = None
            DeleteAfterRun = true
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let result = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("once at", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with 'once at' in output, got {other}"))

[<Fact>]
let ``executeCron list shows datetime when NextRun is Some`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let nextAt = DateTimeOffset.Parse("2027-06-15T09:30:00Z")
        let job : CronJob = {
            Id             = TaskId "nextrun-job-001"
            Label          = "scheduled task"
            Task           = "do it"
            Schedule       = EveryN 60
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Active
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = Some nextAt
            DeleteAfterRun = false
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let result = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            // NextRun = Some dt → formatted as "yyyy-MM-dd HH:mm UTC" (not "never")
            Assert.Contains("2027-06-15", msg)
            Assert.DoesNotContain("never", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with formatted NextRun date, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// formatJobLine — EveryN hours formatting (Python parity: format_timing)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron list formats EveryN 120 as 'every 2h'`` () =
    // Python parity: every_ms=7_200_000 (120 min) → "every 2h"
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job : CronJob = {
            Id             = TaskId "everyn-2h-001"
            Label          = "2h job"
            Task           = "run"
            Schedule       = EveryN 120
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Active
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = None
            DeleteAfterRun = false
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let result = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains("every 2h", msg)
            Assert.DoesNotContain("every 120m", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with 'every 2h', got {other}"))

[<Fact>]
let ``executeCron list formats EveryN 60 as 'every 1h'`` () =
    // 60 minutes → evenly divisible by 60 → "every 1h"
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job : CronJob = {
            Id             = TaskId "everyn-1h-001"
            Label          = "1h job"
            Task           = "run"
            Schedule       = EveryN 60
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Active
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = None
            DeleteAfterRun = false
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let result = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains("every 1h", msg)
            Assert.DoesNotContain("every 60m", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with 'every 1h', got {other}"))

[<Fact>]
let ``executeCron list formats EveryN 30 as 'every 30m'`` () =
    // Python parity: every_ms=1_800_000 (30 min) → "every 30m" (not divisible into whole hours)
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job : CronJob = {
            Id             = TaskId "everyn-30m-001"
            Label          = "30m job"
            Task           = "run"
            Schedule       = EveryN 30
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Active
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = None
            DeleteAfterRun = false
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let result = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("every 30m", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with 'every 30m', got {other}"))

[<Fact>]
let ``executeCron list formats EveryN 90 as 'every 90m' (not divisible into whole hours)`` () =
    // 90 min is not evenly divisible by 60 (remainder 30) → "every 90m", not "every 1h"
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let job : CronJob = {
            Id             = TaskId "everyn-90m-001"
            Label          = "90m job"
            Task           = "run"
            Schedule       = EveryN 90
            Timezone       = None
            Channel        = ChannelId "cli"
            Chat           = ChatId "test"
            Status         = Active
            CreatedAt      = DateTimeOffset.UtcNow
            LastRun        = None
            NextRun        = None
            DeleteAfterRun = false
        }
        let _ = svc.AddJob(job) |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let result = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains("every 90m", msg)
            Assert.DoesNotContain("every 1h", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with 'every 90m', got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// allTools registration
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``allTools returns exactly 1 tool named 'cron'`` () =
    withTempDir (fun dir ->
        let svc   = makeSvc dir
        let tools = allTools svc None
        Assert.Equal(1, List.length tools)
        let (spec, _) = List.head tools
        let (ToolName n) = spec.Name
        Assert.Equal("cron", n))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — add: missing chat covers the | _, _, Error e -> branch
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron add returns ToolFailure when chat is missing`` () =
    // | _, _, Error e -> return ToolFailure e  (third pattern in the triple match)
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [ "action", "add"; "task", "do something"; "channel", "cli"; "schedule", "every 30m" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing chat, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// tryParseBool — "false" / "no" / "0" → Some false branch
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron add with delete_after_run=false creates job successfully`` () =
    // tryParseBool "false" → Some false → deleteAfterRun = false
    withTempDir (fun dir ->
        let svc  = makeSvc dir
        let args = makeArgs [
            "action",          "add"
            "task",            "persistent task"
            "channel",         "cli"
            "chat",            "c1"
            "schedule",        "every 60m"
            "delete_after_run","false" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("Cron job created", msg)
        | other -> Assert.Fail($"Expected ToolSuccess for delete_after_run=false, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// formatJobLine — Weekly schedule shows "weekly <day>" in list output
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron list shows weekly format for a Weekly schedule job`` () =
    // formatJobLine: Weekly(d,h,m) → $"weekly {d} at {h:D2}:{m:D2}"
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let addArgs = makeArgs [
            "action",   "add"
            "task",     "weekly status"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "weekly Monday at 08:00" ]
        let _ = executeCron svc None addArgs |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let listResult = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
        match listResult with
        | ToolSuccess msg ->
            // formatJobLine produces "weekly Monday at 08:00"
            Assert.Contains("weekly", msg)
        | other -> Assert.Fail($"Expected ToolSuccess with weekly schedule in list, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — update action (Python parity)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron update changes job label`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        // Add a job first
        let addArgs = makeArgs [
            "action", "add"; "task", "original task"; "channel", "cli"; "chat", "c1"; "schedule", "every 30m"; "label", "old-label" ]
        let _ = executeCron svc None addArgs |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        // Get the full job ID via ListJobs (the stored ID is a full GUID, not the 8-char display prefix)
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        match jobs with
        | [] -> Assert.Fail("Expected a job after add")
        | job :: _ ->
            let (TaskId jid) = job.Id
            // Update the label
            let updateArgs = makeArgs [ "action", "update"; "job_id", jid; "label", "new-label" ]
            let result = executeCron svc None updateArgs |> Async.RunSynchronously
            match result with
            | ToolSuccess msg -> Assert.Contains("updated", msg)
            | other -> Assert.Fail($"Expected ToolSuccess for update, got {other}")
            // Verify label changed via list
            let listResult = executeCron svc None (makeArgs [ "action", "list" ]) |> Async.RunSynchronously
            match listResult with
            | ToolSuccess msg -> Assert.Contains("new-label", msg)
            | other -> Assert.Fail($"Expected ToolSuccess for list, got {other}"))

[<Fact>]
let ``executeCron update with invalid job_id returns ToolFailure`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let args = makeArgs [ "action", "update"; "job_id", "nonexistent"; "label", "x" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for unknown job_id, got {other}"))

[<Fact>]
let ``executeCron update without job_id returns ToolFailure`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let args = makeArgs [ "action", "update"; "label", "x" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | other -> Assert.Fail($"Expected ToolFailure for missing job_id, got {other}"))

[<Fact>]
let ``executeCron update with invalid schedule returns ToolFailure`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let addArgs = makeArgs [
            "action", "add"; "task", "t"; "channel", "cli"; "chat", "c1"; "schedule", "every 30m" ]
        let _ = executeCron svc None addArgs |> Async.RunSynchronously
        Async.Sleep 100 |> Async.RunSynchronously
        let jobs = svc.ListJobs() |> Async.RunSynchronously
        match jobs with
        | [] -> Assert.Fail("Expected a job after add")
        | job :: _ ->
            let (TaskId jid) = job.Id
            let updateArgs = makeArgs [ "action", "update"; "job_id", jid; "schedule", "not-a-schedule" ]
            let result = executeCron svc None updateArgs |> Async.RunSynchronously
            match result with
            | ToolFailure _ -> ()
            | other -> Assert.Fail($"Expected ToolFailure for bad schedule, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// executeCron — default timezone from config (Python parity)
// Python cron tool falls back to `loop.timezone` when `tz` arg is absent.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeCron add uses defaultTimezone when tz arg is omitted`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let defaultTz = Some "America/New_York"
        let args = makeArgs [
            "action",   "add"
            "task",     "daily standup"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "daily at 09:00" ]
        // tz arg is not present — should fall back to defaultTimezone
        let result = executeCron svc defaultTz args |> Async.RunSynchronously
        match result with
        | ToolSuccess _ ->
            // Verify that the stored job carries the default timezone
            let jobs = svc.ListJobs() |> Async.RunSynchronously
            match jobs with
            | [] -> Assert.Fail("Expected a job after add")
            | job :: _ ->
                Assert.Equal(defaultTz, job.Timezone)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}"))

[<Fact>]
let ``executeCron add explicit tz arg overrides defaultTimezone`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let defaultTz = Some "America/New_York"
        let explicitTz = "Europe/London"
        let args = makeArgs [
            "action",   "add"
            "task",     "daily check"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "daily at 10:00"
            "tz",       explicitTz ]
        let result = executeCron svc defaultTz args |> Async.RunSynchronously
        match result with
        | ToolSuccess _ ->
            let jobs = svc.ListJobs() |> Async.RunSynchronously
            match jobs with
            | [] -> Assert.Fail("Expected a job after add")
            | job :: _ ->
                // Explicit tz wins over default
                Assert.Equal(Some explicitTz, job.Timezone)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}"))

[<Fact>]
let ``executeCron add with None defaultTimezone and no tz arg stores None timezone`` () =
    withTempDir (fun dir ->
        let svc = makeSvc dir
        let args = makeArgs [
            "action",   "add"
            "task",     "check"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "every 60m" ]
        let result = executeCron svc None args |> Async.RunSynchronously
        match result with
        | ToolSuccess _ ->
            let jobs = svc.ListJobs() |> Async.RunSynchronously
            match jobs with
            | [] -> Assert.Fail("Expected a job after add")
            | job :: _ ->
                Assert.Equal(None, job.Timezone)
        | other -> Assert.Fail($"Expected ToolSuccess, got {other}"))

[<Fact>]
let ``allTools passes defaultTimezone through to executeCron`` () =
    withTempDir (fun dir ->
        let svc   = makeSvc dir
        let tools = allTools svc (Some "Asia/Tokyo")
        Assert.Equal(1, List.length tools)
        let (_, execFn) = List.head tools
        let args = makeArgs [
            "action",   "add"
            "task",     "tokyo check"
            "channel",  "cli"
            "chat",     "c1"
            "schedule", "daily at 09:00" ]
        let result = execFn args |> Async.RunSynchronously
        match result with
        | ToolSuccess _ ->
            let jobs = svc.ListJobs() |> Async.RunSynchronously
            match jobs with
            | [] -> Assert.Fail("Expected a job after add via allTools")
            | job :: _ ->
                Assert.Equal(Some "Asia/Tokyo", job.Timezone)
        | other -> Assert.Fail($"Expected ToolSuccess from allTools executor, got {other}"))
