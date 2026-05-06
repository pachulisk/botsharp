module BotSharp.Infrastructure.Storage.JobQueue

#nowarn "3261"

open System
open System.Threading
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// JobQueue — SQLite-backed distributed job queue (Codex-style)
//
// Complete port of Codex's jobs table mechanics:
//   - BEGIN IMMEDIATE transactions (serialized writes)
//   - Ownership tokens (UUID, verified on complete/fail)
//   - Lease expiry (heartbeat renewal, automatic reclaim)
//   - Watermark-based change detection
//   - Concurrent job count limiting
//   - Retry with backoff and exhaustion
//
// Design: all functions take an open SqliteConnection and return Async.
// The caller manages connection lifetime (open/close/dispose).
// ═══════════════════════════════════════════════════════════════════════════

// ── Constants (Codex parity) ────────────────────────────────────────────

/// Default retry count. Codex DEFAULT_RETRY_REMAINING = 3 (memories.rs:24).
let DefaultRetryRemaining = 3

/// Consolidation lease (ms). Codex JOB_LEASE_SECONDS = 3600 (write/lib.rs:80).
let ConsolidationLeaseMs = 60 * 60 * 1000         // 1 hour

/// Consolidation retry delay (ms). Codex JOB_RETRY_DELAY_SECONDS = 3600.
let ConsolidationRetryDelayMs = 15 * 60 * 1000    // 15 minutes

/// Cleanup lease (ms).
let CleanupLeaseMs = 10 * 60 * 1000               // 10 minutes

/// Cleanup retry delay (ms).
let CleanupRetryDelayMs = 60 * 60 * 1000          // 1 hour

/// Max concurrent running jobs per kind. Codex CONCURRENCY_LIMIT = 8 (write/lib.rs:78).
let DefaultMaxRunningJobs = 8

/// Heartbeat interval (ms). Codex JOB_HEARTBEAT_SECONDS = 90 (write/lib.rs:98).
let HeartbeatIntervalMs = 90 * 1000

// ── Helpers ─────────────────────────────────────────────────────────────

/// Worker identifier: "pid:threadId" format.
let makeWorkerId () : string =
    let pid = Diagnostics.Process.GetCurrentProcess().Id
    let tid = Threading.Thread.CurrentThread.ManagedThreadId
    sprintf "%d:%d" pid tid

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

/// Execute SQL returning affected row count, with explicit transaction support.
let private executeCount
    (conn: SqliteConnection) (tx: SqliteTransaction option)
    (sql: string) (ps: (string * obj) array) : int =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    tx |> Option.iter (fun t -> cmd.Transaction <- t)
    for (name, value) in ps do
        cmd.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value) |> ignore
    cmd.ExecuteNonQuery()

/// Execute SQL with no return value.
let private exec (conn: SqliteConnection) (sql: string) (ps: (string * obj) array) : unit =
    executeCount conn None sql ps |> ignore

/// Read a nullable int64 column.
let private readNullableInt64 (reader: SqliteDataReader) (ordinal: int) : int64 option =
    if reader.IsDBNull(ordinal) then None else Some (reader.GetInt64(ordinal))

/// Read a nullable string column.
let private readNullableString (reader: SqliteDataReader) (ordinal: int) : string option =
    if reader.IsDBNull(ordinal) then None else Some (reader.GetString(ordinal))

/// Parse a SqliteDataReader row into a JobSummary.
let private readJobSummary (reader: SqliteDataReader) : JobSummary =
    { Kind                 = reader.GetString(0)
      JobKey               = reader.GetString(1)
      Status               = reader.GetString(2)
      WorkerId             = readNullableString reader 3
      OwnershipToken       = readNullableString reader 4
      StartedAt            = readNullableInt64 reader 5
      FinishedAt           = readNullableInt64 reader 6
      LeaseUntil           = readNullableInt64 reader 7
      RetryAt              = readNullableInt64 reader 8
      RetryRemaining       = reader.GetInt32(9)
      LastError            = readNullableString reader 10
      InputWatermark       = readNullableInt64 reader 11
      LastSuccessWatermark = readNullableInt64 reader 12
      CreatedAt            = reader.GetInt64(13)
      UpdatedAt            = reader.GetInt64(14) }

let private allColumns =
    "kind, job_key, status, worker_id, ownership_token, started_at, finished_at, lease_until, retry_at, retry_remaining, last_error, input_watermark, last_success_watermark, created_at, updated_at"

// ── Query functions (defined before tryClaim which depends on getJob) ────

/// Get a single job by kind + job_key.
let getJob (conn: SqliteConnection) (kind: string) (jobKey: string) : Async<JobSummary option> =
    async {
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sprintf "SELECT %s FROM jobs WHERE kind = @kind AND job_key = @jobKey" allColumns
        cmd.Parameters.AddWithValue("@kind", kind) |> ignore
        cmd.Parameters.AddWithValue("@jobKey", jobKey) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then return Some (readJobSummary reader)
        else return None
    }

/// List jobs by kind, optionally filtered by status (ordered by updated_at DESC).
let listJobs (conn: SqliteConnection) (kind: string) (status: string option) (limit: int) : Async<JobSummary list> =
    async {
        let whereStatus = match status with Some _ -> " AND status = @status" | None -> ""
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sprintf "SELECT %s FROM jobs WHERE kind = @kind%s ORDER BY updated_at DESC LIMIT %d" allColumns whereStatus limit
        cmd.Parameters.AddWithValue("@kind", kind) |> ignore
        status |> Option.iter (fun s -> cmd.Parameters.AddWithValue("@status", s) |> ignore)
        use reader = cmd.ExecuteReader()
        let results = Collections.Generic.List<JobSummary>()
        while reader.Read() do
            results.Add(readJobSummary reader)
        return List.ofSeq results
    }

/// Job statistics for a kind.
let getJobStats (conn: SqliteConnection) (kind: string) : Async<JobStats> =
    async {
        let now = nowMs ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT COUNT(*) AS total, " +
            "SUM(CASE WHEN status = 'running' AND lease_until IS NOT NULL AND lease_until > @now THEN 1 ELSE 0 END) AS running, " +
            "SUM(CASE WHEN status = 'done' THEN 1 ELSE 0 END) AS done, " +
            "SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END) AS error " +
            "FROM jobs WHERE kind = @kind"
        cmd.Parameters.AddWithValue("@kind", kind) |> ignore
        cmd.Parameters.AddWithValue("@now", now) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            return { TotalJobs = reader.GetInt32(0)
                     Running   = if reader.IsDBNull(1) then 0 else reader.GetInt32(1)
                     Done      = if reader.IsDBNull(2) then 0 else reader.GetInt32(2)
                     Error     = if reader.IsDBNull(3) then 0 else reader.GetInt32(3) }
        else
            return { TotalJobs = 0; Running = 0; Done = 0; Error = 0 }
    }

// ── Core claim logic ────────────────────────────────────────────────────

// The INSERT ... ON CONFLICT DO UPDATE SQL for tryClaim.
// Separated as a constant to avoid F# indentation issues with multi-line strings in async CE.
let private tryClaimSql =
    "INSERT INTO jobs (" +
    "kind, job_key, status, worker_id, ownership_token, " +
    "started_at, finished_at, lease_until, " +
    "retry_at, retry_remaining, last_error, " +
    "input_watermark, last_success_watermark, " +
    "created_at, updated_at" +
    ") " +
    "SELECT " +
    "@kind, @jobKey, 'running', @workerId, @ownershipToken, " +
    "@now, NULL, @leaseUntil, " +
    "NULL, @retryRemaining, NULL, " +
    "@inputWatermark, NULL, " +
    "@now, @now " +
    "WHERE (" +
    "SELECT COUNT(*) FROM jobs " +
    "WHERE kind = @kind AND status = 'running' " +
    "AND lease_until IS NOT NULL AND lease_until > @now" +
    ") < @maxRunningJobs " +
    "ON CONFLICT(kind, job_key) DO UPDATE SET " +
    "status = 'running', " +
    "worker_id = @workerId, " +
    "ownership_token = @ownershipToken, " +
    "started_at = @now, " +
    "finished_at = NULL, " +
    "lease_until = @leaseUntil, " +
    "retry_at = NULL, " +
    "retry_remaining = CASE " +
    "WHEN @inputWatermark > COALESCE(jobs.input_watermark, -1) " +
    "THEN @retryRemaining ELSE jobs.retry_remaining END, " +
    "last_error = NULL, " +
    "input_watermark = @inputWatermark, " +
    "updated_at = @now " +
    "WHERE " +
    "(jobs.status != 'running' OR jobs.lease_until IS NULL OR jobs.lease_until <= @now) " +
    "AND (jobs.retry_at IS NULL OR jobs.retry_at <= @now " +
    "OR @inputWatermark > COALESCE(jobs.input_watermark, -1)) " +
    "AND (jobs.retry_remaining > 0 " +
    "OR @inputWatermark > COALESCE(jobs.input_watermark, -1)) " +
    "AND (" +
    "SELECT COUNT(*) FROM jobs AS running_jobs " +
    "WHERE running_jobs.kind = @kind AND running_jobs.status = 'running' " +
    "AND running_jobs.lease_until IS NOT NULL AND running_jobs.lease_until > @now " +
    "AND running_jobs.job_key != @jobKey" +
    ") < @maxRunningJobs"

/// Try to claim a job. Complete port of Codex try_claim_stage1_job (memories.rs:476-648).
///
/// Uses BEGIN IMMEDIATE transaction, ownership_token (UUID), lease_until,
/// worker_id, max_running_jobs concurrency, and watermark comparison.
let tryClaim
    (conn: SqliteConnection)
    (kind: string)
    (jobKey: string)
    (inputWatermark: int64)
    (leaseMs: int)
    (maxRunningJobs: int)
    : Async<ClaimOutcome> =
    async {
        let now = nowMs ()

        // Step 1: early check — is the job already up to date?
        // Codex memories.rs:493-528
        let! existing = getJob conn kind jobKey
        let upToDate =
            match existing with
            | Some job -> job.LastSuccessWatermark |> Option.exists (fun lsw -> lsw >= inputWatermark)
            | None -> false
        if upToDate then
            return SkippedUpToDate
        else

        // Step 2: BEGIN IMMEDIATE transaction (Codex memories.rs:491)
        let ownershipToken = Guid.NewGuid().ToString()
        let workerId = makeWorkerId ()
        let leaseUntil = now + int64 leaseMs

        use tx = conn.BeginTransaction(Data.IsolationLevel.Serializable)

        // Step 3: atomic INSERT ... ON CONFLICT DO UPDATE
        // Complete port of Codex memories.rs:530-590
        let ps =
            [| ("@kind", box kind); ("@jobKey", box jobKey); ("@workerId", box workerId)
               ("@ownershipToken", box ownershipToken); ("@now", box now)
               ("@leaseUntil", box leaseUntil); ("@retryRemaining", box DefaultRetryRemaining)
               ("@inputWatermark", box inputWatermark); ("@maxRunningJobs", box maxRunningJobs) |]
        let rowsAffected = executeCount conn (Some tx) tryClaimSql ps

        tx.Commit()

        // Step 4: diagnose result (Codex memories.rs:614-645)
        if rowsAffected > 0 then
            return Claimed ownershipToken
        else
            let! current = getJob conn kind jobKey
            match current with
            | None -> return SkippedRunning   // concurrency limit hit on INSERT
            | Some job ->
                if job.Status = "running"
                   && job.LeaseUntil |> Option.exists (fun lu -> lu > now)
                then return SkippedRunning
                elif job.RetryRemaining <= 0
                then return SkippedRetryExhausted
                elif job.RetryAt |> Option.exists (fun ra -> ra > now)
                then return SkippedRetryBackoff
                else return SkippedRunning   // concurrency limit
    }

// ── Completion / failure ────────────────────────────────────────────────

let private markSucceededSql =
    "UPDATE jobs SET status = 'done', finished_at = @now, lease_until = NULL, " +
    "last_error = NULL, last_success_watermark = input_watermark, updated_at = @now " +
    "WHERE kind = @kind AND job_key = @jobKey AND status = 'running' AND ownership_token = @token"

/// Mark job succeeded. Ownership token must match. Codex memories.rs:676-695.
let markSucceeded (conn: SqliteConnection) (kind: string) (jobKey: string)
                  (ownershipToken: string) : Async<bool> =
    async {
        let now = nowMs ()
        let ps = [| ("@kind", box kind); ("@jobKey", box jobKey); ("@now", box now); ("@token", box ownershipToken) |]
        let rows = executeCount conn None markSucceededSql ps
        return rows > 0
    }

let private markFailedSql =
    "UPDATE jobs SET status = 'error', finished_at = @now, lease_until = NULL, " +
    "retry_at = @retryAt, retry_remaining = max(retry_remaining - 1, 0), " +
    "last_error = @error, updated_at = @now " +
    "WHERE kind = @kind AND job_key = @jobKey AND status = 'running' AND ownership_token = @token"

/// Mark job failed with retry backoff. Ownership token must match. Codex memories.rs:830-852.
let markFailed (conn: SqliteConnection) (kind: string) (jobKey: string)
               (ownershipToken: string) (error: string) (retryDelayMs: int)
               : Async<bool> =
    async {
        let now = nowMs ()
        let retryAt = now + int64 retryDelayMs
        let ps =
            [| ("@kind", box kind); ("@jobKey", box jobKey); ("@now", box now)
               ("@retryAt", box retryAt); ("@error", box error); ("@token", box ownershipToken) |]
        let rows = executeCount conn None markFailedSql ps
        return rows > 0
    }

let private markFailedIfUnownedSql =
    "UPDATE jobs SET status = 'error', finished_at = @now, lease_until = NULL, " +
    "retry_at = @retryAt, retry_remaining = max(retry_remaining - 1, 0), " +
    "last_error = @error, updated_at = @now " +
    "WHERE kind = @kind AND job_key = @jobKey AND status = 'running' " +
    "AND (ownership_token = @token OR ownership_token IS NULL)"

/// Mark job failed, allowing ownership mismatch (lost ownership recovery).
/// Codex memories.rs:1151-1185.
let markFailedIfUnowned (conn: SqliteConnection) (kind: string) (jobKey: string)
                        (ownershipToken: string) (error: string) (retryDelayMs: int)
                        : Async<bool> =
    async {
        let now = nowMs ()
        let retryAt = now + int64 retryDelayMs
        let ps =
            [| ("@kind", box kind); ("@jobKey", box jobKey); ("@now", box now)
               ("@retryAt", box retryAt); ("@error", box error); ("@token", box ownershipToken) |]
        let rows = executeCount conn None markFailedIfUnownedSql ps
        return rows > 0
    }

// ── Heartbeat ───────────────────────────────────────────────────────────

let private heartbeatSql =
    "UPDATE jobs SET lease_until = @newLeaseUntil, updated_at = @now " +
    "WHERE kind = @kind AND job_key = @jobKey AND status = 'running' AND ownership_token = @token"

/// Extend job lease. Codex memories.rs:1025-1041.
let heartbeat (conn: SqliteConnection) (kind: string) (jobKey: string)
              (ownershipToken: string) (leaseMs: int) : Async<bool> =
    async {
        let now = nowMs ()
        let newLeaseUntil = now + int64 leaseMs
        let ps =
            [| ("@kind", box kind); ("@jobKey", box jobKey); ("@now", box now)
               ("@newLeaseUntil", box newLeaseUntil); ("@token", box ownershipToken) |]
        let rows = executeCount conn None heartbeatSql ps
        return rows > 0
    }

/// Start a background heartbeat loop. Returns CancellationTokenSource to stop it.
/// Codex memories/write/phase2.rs: calls heartbeat every 90 seconds.
let startHeartbeat
    (openDb: unit -> SqliteConnection)
    (kind: string)
    (jobKey: string)
    (ownershipToken: string)
    (leaseMs: int)
    (intervalMs: int)
    : CancellationTokenSource =

    let cts = new CancellationTokenSource()
    Async.Start(
        async {
            while not cts.Token.IsCancellationRequested do
                do! Async.Sleep intervalMs
                if not cts.Token.IsCancellationRequested then
                    try
                        use conn = openDb ()
                        let! renewed = heartbeat conn kind jobKey ownershipToken leaseMs
                        if not renewed then
                            eprintfn "[JobQueue] Heartbeat failed: ownership lost for %s/%s" kind jobKey
                            cts.Cancel()
                    with ex ->
                        eprintfn "[JobQueue] Heartbeat error for %s/%s: %s" kind jobKey ex.Message
        },
        cts.Token)
    cts

// ── Cleanup / maintenance ───────────────────────────────────────────────

/// Delete a job record (e.g. after successful session cleanup).
let removeJob (conn: SqliteConnection) (kind: string) (jobKey: string) : Async<unit> =
    async {
        exec conn "DELETE FROM jobs WHERE kind = @kind AND job_key = @jobKey"
            [| ("@kind", box kind); ("@jobKey", box jobKey) |]
    }

/// Prune completed jobs older than N days. Returns number of records deleted.
let pruneCompletedJobs (conn: SqliteConnection) (kind: string) (olderThanDays: int) : Async<int> =
    async {
        let cutoff = DateTimeOffset.UtcNow.AddDays(- float olderThanDays).ToUnixTimeMilliseconds()
        let sql = "DELETE FROM jobs WHERE kind = @kind AND status = 'done' AND finished_at IS NOT NULL AND finished_at < @cutoff"
        return executeCount conn None sql [| ("@kind", box kind); ("@cutoff", box cutoff) |]
    }

// ── Watermark helper ────────────────────────────────────────────────────

/// Compute input watermark from a session's updated_at timestamp.
/// If unchanged since last consolidation, tryClaim returns SkippedUpToDate.
let sessionWatermark (entry: BotSharp.Infrastructure.Storage.StateDb.SessionIndexEntry) : int64 =
    entry.UpdatedAt.ToUnixTimeMilliseconds()

// ── Display helpers ─────────────────────────────────────────────────────

/// Format a job summary for /jobs command output.
let formatJobSummary (job: JobSummary) : string =
    let now = nowMs ()
    let statusLine =
        match job.Status with
        | "running" ->
            let leaseRemaining =
                match job.LeaseUntil with
                | Some lu when lu > now -> sprintf "  lease: %dm remaining" ((lu - now) / 60_000L |> int)
                | Some _ -> "  lease: EXPIRED"
                | None -> ""
            let workerInfo = job.WorkerId |> Option.map (sprintf "  worker: %s") |> Option.defaultValue ""
            sprintf "running%s%s" leaseRemaining workerInfo
        | "error" ->
            let retryInfo =
                match job.RetryAt, job.RetryRemaining with
                | Some ra, rem when ra > now && rem > 0 ->
                    sprintf "  retry in %dm (%d remaining)" ((ra - now) / 60_000L |> int) rem
                | _, rem when rem <= 0 -> "  retries exhausted"
                | _ -> ""
            let errorMsg = job.LastError |> Option.map (sprintf "\n                           Error: %s") |> Option.defaultValue ""
            sprintf "error%s%s" retryInfo errorMsg
        | "done" ->
            let finishedStr =
                job.FinishedAt
                |> Option.map (fun f -> DateTimeOffset.FromUnixTimeMilliseconds(f).ToString("yyyy-MM-dd HH:mm"))
                |> Option.defaultValue ""
            sprintf "done     %s" finishedStr
        | s -> s
    let watermarkStr =
        job.InputWatermark |> Option.map (sprintf "  watermark: %d") |> Option.defaultValue ""
    sprintf "  %-18s %s%s" job.JobKey statusLine watermarkStr

/// Format job stats and job list for /jobs command output.
let formatJobsOutput (kind: string) (stats: JobStats) (jobs: JobSummary list) : string =
    let kindTitle = kind.[0..0].ToUpper() + kind.[1..]
    let header = sprintf "%s Jobs (%d total: %d done, %d error, %d running)" kindTitle stats.TotalJobs stats.Done stats.Error stats.Running
    let separator = String.replicate (String.length header) "\u2500"
    let lines = jobs |> List.map formatJobSummary
    if lines.IsEmpty then
        sprintf "%s\n%s\n  (no jobs)" header separator
    else
        sprintf "%s\n%s\n%s" header separator (String.concat "\n" lines)
