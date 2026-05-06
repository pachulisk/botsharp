module BotSharp.Tests.Infrastructure.JobQueueTests

open System
open Microsoft.Data.Sqlite
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.JobQueue

// ═══════════════════════════════════════════════════════════════════════════
// JobQueue unit tests — SQLite-backed job lifecycle
// ═══════════════════════════════════════════════════════════════════════════

/// Create an in-memory SQLite connection factory with the `jobs` table.
/// Returns (factory, root). Caller MUST hold root alive; SQLite destroys the
/// in-memory DB when the last connection closes. Use `use root = ...`.
let private mkTestDb () : (unit -> SqliteConnection) * SqliteConnection =
    let name    = Guid.NewGuid().ToString("N")
    let connStr = sprintf "Data Source=%s;Mode=Memory;Cache=Shared" name
    let root = new SqliteConnection(connStr)
    root.Open()
    use cmd = root.CreateCommand()
    cmd.CommandText <-
        "CREATE TABLE IF NOT EXISTS jobs (" +
        "kind TEXT NOT NULL, job_key TEXT NOT NULL, status TEXT NOT NULL, " +
        "worker_id TEXT, ownership_token TEXT, started_at INTEGER, " +
        "finished_at INTEGER, lease_until INTEGER, retry_at INTEGER, " +
        "retry_remaining INTEGER NOT NULL, last_error TEXT, " +
        "input_watermark INTEGER, last_success_watermark INTEGER, " +
        "created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, " +
        "PRIMARY KEY (kind, job_key));"
    cmd.ExecuteNonQuery() |> ignore
    let factory () =
        let c = new SqliteConnection(connStr)
        c.Open()
        c
    (factory, root)

// ── tryClaim / basic lifecycle ──────────────────────────────────────────

[<Fact>]
let ``tryClaim returns Claimed for a new job`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! outcome = tryClaim conn "test" "job-1" 100L 60_000 8
        match outcome with
        | Claimed _ -> ()
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``tryClaim second call returns SkippedRunning while first lease active`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! first = tryClaim conn "test" "job-2" 100L 60_000 8
        match first with
        | Claimed _ ->
            let! second = tryClaim conn "test" "job-2" 100L 60_000 8
            match second with
            | SkippedRunning -> ()
            | other -> Assert.Fail(sprintf "Expected SkippedRunning, got %A" other)
        | other -> Assert.Fail(sprintf "Expected Claimed on first call, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``tryClaim returns SkippedUpToDate when watermark already satisfied`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "test" "job-wm" 100L 60_000 8
        match claimed with
        | Claimed token ->
            let! _ = markSucceeded conn "test" "job-wm" token
            let! second = tryClaim conn "test" "job-wm" 100L 60_000 8
            match second with
            | SkippedUpToDate -> ()
            | other -> Assert.Fail(sprintf "Expected SkippedUpToDate, got %A" other)
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``tryClaim with higher watermark reclaims after success`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "test" "job-wm2" 100L 60_000 8
        match claimed with
        | Claimed token ->
            let! _ = markSucceeded conn "test" "job-wm2" token
            let! second = tryClaim conn "test" "job-wm2" 200L 60_000 8
            match second with
            | Claimed _ -> ()
            | other -> Assert.Fail(sprintf "Expected Claimed with higher watermark, got %A" other)
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``markSucceeded sets job status to 'done'`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "test" "job-succ" 42L 60_000 8
        match claimed with
        | Claimed token ->
            let! ok = markSucceeded conn "test" "job-succ" token
            Assert.True(ok)
            let! job = getJob conn "test" "job-succ"
            match job with
            | Some j ->
                Assert.Equal("done", j.Status)
                Assert.Equal(Some 42L, j.LastSuccessWatermark)
            | None -> Assert.Fail("Job disappeared after markSucceeded")
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``markFailed sets job status to 'error'`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "test" "job-fail" 10L 60_000 8
        match claimed with
        | Claimed token ->
            let! ok = markFailed conn "test" "job-fail" token "boom" 0
            Assert.True(ok)
            let! job = getJob conn "test" "job-fail"
            match job with
            | Some j ->
                Assert.Equal("error", j.Status)
                Assert.Equal(Some "boom", j.LastError)
            | None -> Assert.Fail("Job disappeared after markFailed")
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

// ── ownership token protection ──────────────────────────────────────────

[<Fact>]
let ``markFailed with wrong token does not affect job`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "test" "job-token" 10L 60_000 8
        match claimed with
        | Claimed _ ->
            let! changed = markFailed conn "test" "job-token" "wrong-token" "should be ignored" 0
            Assert.False(changed, "markFailed with wrong token should return false")
            let! job = getJob conn "test" "job-token"
            match job with
            | Some j -> Assert.Equal("running", j.Status)
            | None -> Assert.Fail("Job disappeared")
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``markSucceeded with wrong token returns false`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "test" "job-succ-tok" 1L 60_000 8
        match claimed with
        | Claimed _ ->
            let! ok = markSucceeded conn "test" "job-succ-tok" "bad-token"
            Assert.False(ok)
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

// ── getJob / listJobs / getJobStats ────────────────────────────────────

[<Fact>]
let ``getJob returns None for non-existent key`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! result = getJob conn "test" "no-such-job"
        Assert.True(result.IsNone)
    } |> Async.RunSynchronously

[<Fact>]
let ``listJobs returns claimed jobs under the right kind`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! _ = tryClaim conn "kind-A" "j1" 1L 60_000 8
        let! _ = tryClaim conn "kind-A" "j2" 1L 60_000 8
        let! _ = tryClaim conn "kind-B" "j3" 1L 60_000 8
        let! jobs = listJobs conn "kind-A" None 100
        Assert.Equal(2, jobs.Length)
        Assert.True(jobs |> List.forall (fun j -> j.Status = "running"))
    } |> Async.RunSynchronously

[<Fact>]
let ``listJobs filtered by status returns only matching`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! c1 = tryClaim conn "filt" "f1" 1L 60_000 8
        let! _  = tryClaim conn "filt" "f2" 1L 60_000 8
        match c1 with
        | Claimed tok -> let! _ = markSucceeded conn "filt" "f1" tok in ()
        | _ -> ()
        let! running = listJobs conn "filt" (Some "running") 100
        Assert.Equal(1, running.Length)
        Assert.Equal("f2", running.[0].JobKey)
    } |> Async.RunSynchronously

[<Fact>]
let ``getJobStats counts running and done jobs`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! c1 = tryClaim conn "stats" "s1" 1L 60_000 8
        let! _  = tryClaim conn "stats" "s2" 1L 60_000 8
        match c1 with
        | Claimed tok -> let! _ = markSucceeded conn "stats" "s1" tok in ()
        | _ -> ()
        let! stats = getJobStats conn "stats"
        Assert.Equal(1, stats.Running)
        Assert.Equal(1, stats.Done)
        Assert.Equal(0, stats.Error)
    } |> Async.RunSynchronously

// ── pruneCompletedJobs ──────────────────────────────────────────────────

[<Fact>]
let ``pruneCompletedJobs removes old done jobs`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "prune" "old-job" 1L 60_000 8
        match claimed with
        | Claimed token ->
            let! _ = markSucceeded conn "prune" "old-job" token
            // olderThanDays = -1 sets cutoff to tomorrow — prunes anything finished before then.
            let! deleted = pruneCompletedJobs conn "prune" -1
            Assert.True(deleted >= 1)
            let! after = getJob conn "prune" "old-job"
            Assert.True(after.IsNone)
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``pruneCompletedJobs does not remove running jobs`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! _ = tryClaim conn "prune2" "running-job" 1L 60_000 8
        let! deleted = pruneCompletedJobs conn "prune2" 0
        Assert.Equal(0, deleted)
        let! after = getJob conn "prune2" "running-job"
        Assert.True(after.IsSome)
    } |> Async.RunSynchronously

// ── removeJob ──────────────────────────────────────────────────────────

[<Fact>]
let ``removeJob deletes the job regardless of status`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! _ = tryClaim conn "del" "to-delete" 1L 60_000 8
        do! removeJob conn "del" "to-delete"
        let! result = getJob conn "del" "to-delete"
        Assert.True(result.IsNone)
    } |> Async.RunSynchronously

// ── heartbeat ──────────────────────────────────────────────────────────

[<Fact>]
let ``heartbeat extends lease_until for running job`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! claimed = tryClaim conn "hb" "hb-job" 1L 1_000 8
        match claimed with
        | Claimed token ->
            let! before = getJob conn "hb" "hb-job"
            let leaseBeforeHb = before |> Option.bind (fun j -> j.LeaseUntil)
            let! _ = heartbeat conn "hb" "hb-job" token 60_000
            let! after = getJob conn "hb" "hb-job"
            let leaseAfterHb = after |> Option.bind (fun j -> j.LeaseUntil)
            match leaseBeforeHb, leaseAfterHb with
            | Some before', Some after' -> Assert.True(after' > before')
            | _ -> Assert.Fail("lease_until missing before or after heartbeat")
        | other -> Assert.Fail(sprintf "Expected Claimed, got %A" other)
    } |> Async.RunSynchronously

[<Fact>]
let ``heartbeat with wrong token returns false`` () =
    async {
        let openDb, root = mkTestDb ()
        use _root = root
        use conn = openDb ()
        let! _ = tryClaim conn "hb2" "hb2-job" 1L 60_000 8
        let! ok = heartbeat conn "hb2" "hb2-job" "wrong-token" 60_000
        Assert.False(ok)
    } |> Async.RunSynchronously

// ── formatJobSummary / makeWorkerId smoke tests ─────────────────────────

[<Fact>]
let ``formatJobSummary returns a non-empty string`` () =
    let job : JobSummary = {
        Kind                 = "test"
        JobKey               = "j"
        Status               = "running"
        WorkerId             = Some "1:1"
        OwnershipToken       = None
        StartedAt            = None
        FinishedAt           = None
        LeaseUntil           = None
        RetryAt              = None
        RetryRemaining       = 3
        LastError            = None
        InputWatermark       = Some 42L
        LastSuccessWatermark = None
        CreatedAt            = 0L
        UpdatedAt            = 0L
    }
    let s = formatJobSummary job
    Assert.True(s.Length > 0)

[<Fact>]
let ``makeWorkerId returns a colon-separated string`` () =
    let wid = makeWorkerId ()
    Assert.Contains(":", wid)

[<Fact>]
let ``DefaultRetryRemaining equals 3`` () =
    Assert.Equal(3, DefaultRetryRemaining)
