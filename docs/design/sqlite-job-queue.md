# BotSharp SQLite 作业队列设计方案

> 完整复刻 Codex 的 SQLite `jobs` 表设计，包含多进程分布式领取、所有权令牌、租约过期、并发控制等全部机制。

## 1. 问题分析

### 1.1 现有后台任务一览

BotSharp 有 4 个后台任务，使用不同的调度机制：

| 任务 | 机制 | 文件 | 职责 |
|------|------|------|------|
| AutoCompactService | `Async.Start` 循环 | `AutoCompactService.fs` | 整合空闲会话的记忆 |
| SessionCleanupService | .NET `Timer` | `SessionCleanupService.fs` | 清理过期会话文件 |
| HeartbeatService | `Async.Start` 循环 | `HeartbeatService.fs` | 定期唤醒 Agent 执行 HEARTBEAT.md 任务 |
| CronService | `MailboxProcessor` + `crons.json` | `CronService.fs` | 用户定义的定时任务 |

### 1.2 当前痛点

**AutoCompactService**（问题最严重）：
- **无失败记录**：整合失败被 `try/with _ -> ()` 静默吞掉（`AutoCompactService.fs:84`）
- **无重试逻辑**：失败后等下一个 15 分钟周期盲目重试
- **无变更检测**：未变更的会话仍被加载检查
- **无进度追踪**：无法知道处理了多少、跳过了多少、失败了多少
- **崩溃丢失状态**：进程崩溃后所有进行中的状态丢失
- **无并发保护**：`AutoCompactService` 和 `SessionActor` 可能同时操作同一会话

**SessionCleanupService**：
- **无失败记录**：文件删除失败仅 `eprintfn` 输出
- **无可观测性**：无法查询历史清理状态

### 1.3 为什么需要完整复刻 Codex 的分布式模式

即使 BotSharp 当前是单进程，完整的分布式作业队列仍然必要：

1. **跨服务并发安全**：`AutoCompactService`（后台线程）和 `SessionActor`（MailboxProcessor 线程）通过各自的 `SqliteConnection` 访问同一数据库。MailboxProcessor 只序列化 actor 内部消息，**不序列化跨 service 的 SQLite 写入**。`BEGIN IMMEDIATE` 事务防止写入冲突。

2. **异步回调防护**：整合任务是 async 的。如果一个整合超时后被回收，但旧的 async 回调稍后完成，没有 `ownership_token` 校验它会错误地覆盖新作业的状态。

3. **运行时卡死检测**：没有 `lease_until`，如果某个 async 任务卡住但进程没崩溃，该作业会永久停留在 `running` 状态，直到手动重启。租约过期允许下一次领取自动回收。

4. **多实例就绪**：容器化部署、gateway 模式、多实例场景下无需重写。

5. **增量成本极低**：相比简化版，仅多 3 个字段（`worker_id`、`ownership_token`、`lease_until`）和 `BEGIN IMMEDIATE` 事务，约 30 行增量代码。

## 2. Schema

### 2.1 `jobs` 表

完整复刻 Codex 的 `jobs` 表（`state/migrations/0006_memories.sql:13-31`），加上 `created_at` / `updated_at` 审计字段：

```sql
CREATE TABLE jobs (
    kind                    TEXT NOT NULL,           -- 作业类型
    job_key                 TEXT NOT NULL,           -- 作业标识（如 session_id）
    status                  TEXT NOT NULL,           -- running / done / error
    worker_id               TEXT,                    -- 执行者标识（进程/线程 ID）
    ownership_token         TEXT,                    -- UUID 所有权令牌，完成/失败时必须匹配
    started_at              INTEGER,                 -- 开始执行时间（Unix 毫秒）
    finished_at             INTEGER,                 -- 完成时间（Unix 毫秒）
    lease_until             INTEGER,                 -- 租约过期时间（Unix 毫秒）
    retry_at                INTEGER,                 -- 下次可重试时间（Unix 毫秒）
    retry_remaining         INTEGER NOT NULL,        -- 剩余重试次数
    last_error              TEXT,                    -- 最后一次错误信息
    input_watermark         INTEGER,                 -- 输入数据版本（用于变更检测）
    last_success_watermark  INTEGER,                 -- 上次成功时的输入版本
    created_at              INTEGER NOT NULL,        -- 首次创建时间
    updated_at              INTEGER NOT NULL,        -- 最后更新时间
    PRIMARY KEY (kind, job_key)
);

CREATE INDEX idx_jobs_kind_status_retry_lease
    ON jobs(kind, status, retry_at, lease_until);
```

### 2.2 与 Codex 的字段对照

| Codex 字段 | BotSharp | 对应源码 |
|-----------|----------|---------|
| `kind` | 完整保留 | `memories.rs:19-20` |
| `job_key` | 完整保留 | — |
| `status` | 完整保留 | — |
| `worker_id` | **完整保留** | `memories.rs:536` |
| `ownership_token` | **完整保留** | `memories.rs:537` — `Uuid::new_v4()` |
| `started_at` | 完整保留 | — |
| `finished_at` | 完整保留 | — |
| `lease_until` | **完整保留** | `memories.rs:540` — `now + lease_seconds` |
| `retry_at` | 完整保留 | — |
| `retry_remaining` | 完整保留 | — |
| `last_error` | 完整保留 | — |
| `input_watermark` | 完整保留 | — |
| `last_success_watermark` | 完整保留 | — |
| `created_at` | BotSharp 新增 | Codex 无，增加审计能力 |
| `updated_at` | BotSharp 新增 | Codex 无，增加审计能力 |

### 2.3 作业类型

```fsharp
/// 对应 Codex 的 JOB_KIND_* 常量（memories.rs:19-22）
[<RequireQualifiedAccess>]
module JobKind =
    [<Literal>] let Consolidation = "consolidation"
    [<Literal>] let SessionCleanup = "session_cleanup"
```

## 3. 核心作业队列模块

### 3.1 常量

```fsharp
module BotSharp.Infrastructure.Storage.JobQueue

/// 对应 Codex DEFAULT_RETRY_REMAINING = 3（memories.rs:24）
let DefaultRetryRemaining = 3

/// 整合租约时长（毫秒）。对应 Codex JOB_LEASE_SECONDS = 3600（memories/write/lib.rs:80）
let ConsolidationLeaseMs = 60 * 60 * 1000       // 1 小时

/// 整合重试延迟（毫秒）。对应 Codex JOB_RETRY_DELAY_SECONDS = 3600
let ConsolidationRetryDelayMs = 15 * 60 * 1000  // 15 分钟

/// 清理租约时长（毫秒）
let CleanupLeaseMs = 10 * 60 * 1000             // 10 分钟

/// 清理重试延迟（毫秒）
let CleanupRetryDelayMs = 60 * 60 * 1000        // 1 小时

/// 最大并发 running 作业数。对应 Codex CONCURRENCY_LIMIT = 8（memories/write/lib.rs:78）
let DefaultMaxRunningJobs = 8

/// 心跳续租间隔（毫秒）。对应 Codex JOB_HEARTBEAT_SECONDS = 90（memories/write/lib.rs:98）
let HeartbeatIntervalMs = 90 * 1000
```

### 3.2 数据类型

```fsharp
/// 作业领取结果。
/// 完整对应 Codex Stage1JobClaimOutcome（state/src/model/memories.rs:79-90）
type ClaimOutcome =
    | Claimed of ownershipToken: string     // 成功领取，返回 ownership_token
    | SkippedUpToDate                       // last_success_watermark >= inputWatermark
    | SkippedRetryBackoff                   // retry_at > now
    | SkippedRetryExhausted                 // retry_remaining <= 0 且 watermark 未推进
    | SkippedRunning                        // 已在运行中且租约未过期

/// Worker 标识。在 BotSharp 中使用 "pid:threadId" 格式。
let makeWorkerId () : string =
    let pid = System.Diagnostics.Process.GetCurrentProcess().Id
    let tid = System.Threading.Thread.CurrentThread.ManagedThreadId
    sprintf "%d:%d" pid tid

/// 作业摘要（查询和展示用）
type JobSummary = {
    Kind                : string
    JobKey              : string
    Status              : string
    WorkerId            : string option
    OwnershipToken      : string option
    StartedAt           : int64 option
    FinishedAt          : int64 option
    LeaseUntil          : int64 option
    RetryAt             : int64 option
    RetryRemaining      : int
    LastError           : string option
    InputWatermark      : int64 option
    LastSuccessWatermark: int64 option
    CreatedAt           : int64
    UpdatedAt           : int64
}

/// 作业统计
type JobStats = {
    TotalJobs    : int
    Running      : int
    Done         : int
    Error        : int
}
```

### 3.3 `tryClaim` — 完整复刻 Codex 的领取逻辑

对应 Codex `try_claim_stage1_job`（`memories.rs:476-648`）。

使用 `BEGIN IMMEDIATE` 事务实现序列化隔离，确保多 worker / 多线程并发安全。

核心 SQL 使用 Codex 的单条 `INSERT ... ON CONFLICT DO UPDATE` 原子操作模式（`memories.rs:530-590`），在一条语句中同时处理"不存在则插入"和"存在则有条件更新"。

```fsharp
/// 尝试领取一个作业。
///
/// 完整复刻 Codex try_claim_stage1_job（memories.rs:476-648）：
/// - BEGIN IMMEDIATE 事务（防多 worker 写入冲突）
/// - ownership_token（UUID，完成/失败时校验）
/// - lease_until（租约过期后其他 worker 可回收）
/// - worker_id（标识当前执行者）
/// - max_running_jobs 并发计数（限制同类型并发作业数）
/// - watermark 推进时重置 retry_remaining
let tryClaim
    (conn: SqliteConnection)
    (kind: string)
    (jobKey: string)
    (inputWatermark: int64)
    (leaseMs: int)
    (maxRunningJobs: int)
    : Async<ClaimOutcome> =

    async {
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let ownershipToken = System.Guid.NewGuid().ToString()
        let workerId = makeWorkerId()
        let leaseUntil = now + int64 leaseMs

        // ── 步骤 1：检查是否已经是最新 ──
        // 对应 Codex memories.rs:493-528
        let! existing = getJob conn kind jobKey
        match existing with
        | Some job ->
            match job.LastSuccessWatermark with
            | Some lsw when lsw >= inputWatermark -> return SkippedUpToDate
            | _ -> ()
        | None -> ()

        // ── 步骤 2：BEGIN IMMEDIATE 事务 ──
        // 对应 Codex memories.rs:491
        use! tx = beginImmediate conn

        // ── 步骤 3：原子 INSERT ... ON CONFLICT DO UPDATE ──
        // 完整复刻 Codex memories.rs:530-590 的单条 SQL
        let! rowsAffected = executeCount tx """
            INSERT INTO jobs (
                kind, job_key, status, worker_id, ownership_token,
                started_at, finished_at, lease_until,
                retry_at, retry_remaining, last_error,
                input_watermark, last_success_watermark,
                created_at, updated_at
            )
            SELECT
                @kind, @jobKey, 'running', @workerId, @ownershipToken,
                @now, NULL, @leaseUntil,
                NULL, @retryRemaining, NULL,
                @inputWatermark, NULL,
                @now, @now
            WHERE (
                SELECT COUNT(*)
                FROM jobs
                WHERE kind = @kind
                  AND status = 'running'
                  AND lease_until IS NOT NULL
                  AND lease_until > @now
            ) < @maxRunningJobs

            ON CONFLICT(kind, job_key) DO UPDATE SET
                status = 'running',
                worker_id = @workerId,
                ownership_token = @ownershipToken,
                started_at = @now,
                finished_at = NULL,
                lease_until = @leaseUntil,
                retry_at = NULL,
                retry_remaining = CASE
                    WHEN @inputWatermark > COALESCE(jobs.input_watermark, -1)
                        THEN @retryRemaining
                    ELSE jobs.retry_remaining
                END,
                last_error = NULL,
                input_watermark = @inputWatermark,
                updated_at = @now
            WHERE
                -- 条件 1：未运行，或租约已过期（可回收）
                (jobs.status != 'running'
                 OR jobs.lease_until IS NULL
                 OR jobs.lease_until <= @now)
                -- 条件 2：退避已到期，或 watermark 推进（忽略退避）
                AND (jobs.retry_at IS NULL
                     OR jobs.retry_at <= @now
                     OR @inputWatermark > COALESCE(jobs.input_watermark, -1))
                -- 条件 3：还有重试次数，或 watermark 推进（重置重试）
                AND (jobs.retry_remaining > 0
                     OR @inputWatermark > COALESCE(jobs.input_watermark, -1))
                -- 条件 4：并发计数限制（排除当前 job_key 自身）
                AND (
                    SELECT COUNT(*)
                    FROM jobs AS running_jobs
                    WHERE running_jobs.kind = @kind
                      AND running_jobs.status = 'running'
                      AND running_jobs.lease_until IS NOT NULL
                      AND running_jobs.lease_until > @now
                      AND running_jobs.job_key != @jobKey
                ) < @maxRunningJobs
        """ [| ("kind", kind); ("jobKey", jobKey); ("workerId", workerId);
               ("ownershipToken", ownershipToken); ("now", now);
               ("leaseUntil", leaseUntil); ("retryRemaining", DefaultRetryRemaining);
               ("inputWatermark", inputWatermark); ("maxRunningJobs", maxRunningJobs) |]

        do! commit tx

        // ── 步骤 4：判断结果 ──
        // 对应 Codex memories.rs:614-645 的 fallback 检查
        if rowsAffected > 0 then
            return Claimed ownershipToken
        else
            // 领取失败，诊断原因
            let! current = getJob conn kind jobKey
            match current with
            | None -> return SkippedRunning  // 并发计数限制
            | Some job ->
                if job.Status = "running"
                   && job.LeaseUntil |> Option.exists (fun lu -> lu > now)
                then return SkippedRunning
                elif job.RetryRemaining <= 0
                then return SkippedRetryExhausted
                elif job.RetryAt |> Option.exists (fun ra -> ra > now)
                then return SkippedRetryBackoff
                else return SkippedRunning  // 并发计数限制
    }
```

### 3.4 `markSucceeded` — 所有权校验

对应 Codex `mark_stage1_job_succeeded`（`memories.rs:676-695`）。

```fsharp
/// 标记作业成功。必须提供领取时返回的 ownershipToken。
/// WHERE ownership_token = @token 确保只有合法持有者能完成作业。
/// 对应 Codex memories.rs:676-695
let markSucceeded (conn: SqliteConnection) (kind: string) (jobKey: string)
                  (ownershipToken: string) : Async<bool> =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    async {
        let! rows = executeCount conn """
            UPDATE jobs SET
                status = 'done',
                finished_at = @now,
                lease_until = NULL,
                last_error = NULL,
                last_success_watermark = input_watermark,
                updated_at = @now
            WHERE kind = @kind AND job_key = @jobKey
              AND status = 'running'
              AND ownership_token = @token
        """ [| ("kind", kind); ("jobKey", jobKey); ("now", now); ("token", ownershipToken) |]
        return rows > 0
    }
```

### 3.5 `markFailed` — 所有权校验 + 退避

对应 Codex `mark_stage1_job_failed`（`memories.rs:819-855`）。

```fsharp
/// 标记作业失败。必须提供 ownershipToken。
/// 对应 Codex memories.rs:830-852
let markFailed (conn: SqliteConnection) (kind: string) (jobKey: string)
               (ownershipToken: string) (error: string) (retryDelayMs: int)
               : Async<bool> =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let retryAt = now + int64 retryDelayMs
    async {
        let! rows = executeCount conn """
            UPDATE jobs SET
                status = 'error',
                finished_at = @now,
                lease_until = NULL,
                retry_at = @retryAt,
                retry_remaining = max(retry_remaining - 1, 0),
                last_error = @error,
                updated_at = @now
            WHERE kind = @kind AND job_key = @jobKey
              AND status = 'running'
              AND ownership_token = @token
        """ [| ("kind", kind); ("jobKey", jobKey); ("now", now);
               ("retryAt", retryAt); ("error", error); ("token", ownershipToken) |]
        return rows > 0
    }
```

### 3.6 `markFailedIfUnowned` — 所有权丢失恢复

对应 Codex `mark_global_phase2_job_failed_if_unowned`（`memories.rs:1151-1185`）。

当 worker 不确定自己是否仍持有所有权时（网络分区、async 超时等），使用此函数尝试恢复。

```fsharp
/// 标记作业失败，允许所有权缺失。
/// WHERE (ownership_token = @token OR ownership_token IS NULL)
/// 对应 Codex memories.rs:1151-1185
let markFailedIfUnowned (conn: SqliteConnection) (kind: string) (jobKey: string)
                        (ownershipToken: string) (error: string) (retryDelayMs: int)
                        : Async<bool> =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let retryAt = now + int64 retryDelayMs
    async {
        let! rows = executeCount conn """
            UPDATE jobs SET
                status = 'error',
                finished_at = @now,
                lease_until = NULL,
                retry_at = @retryAt,
                retry_remaining = max(retry_remaining - 1, 0),
                last_error = @error,
                updated_at = @now
            WHERE kind = @kind AND job_key = @jobKey
              AND status = 'running'
              AND (ownership_token = @token OR ownership_token IS NULL)
        """ [| ("kind", kind); ("jobKey", jobKey); ("now", now);
               ("retryAt", retryAt); ("error", error); ("token", ownershipToken) |]
        return rows > 0
    }
```

### 3.7 `heartbeat` — 租约续期

对应 Codex `heartbeat_global_phase2_job`（`memories.rs:1018-1042`）。

长时间运行的作业需要定期续租，防止被其他 worker 误回收。

```fsharp
/// 延长作业租约。在长时间运行的作业中定期调用（每 HeartbeatIntervalMs 毫秒）。
/// 对应 Codex memories.rs:1025-1041
let heartbeat (conn: SqliteConnection) (kind: string) (jobKey: string)
              (ownershipToken: string) (leaseMs: int) : Async<bool> =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let newLeaseUntil = now + int64 leaseMs
    async {
        let! rows = executeCount conn """
            UPDATE jobs SET
                lease_until = @newLeaseUntil,
                updated_at = @now
            WHERE kind = @kind AND job_key = @jobKey
              AND status = 'running'
              AND ownership_token = @token
        """ [| ("kind", kind); ("jobKey", jobKey); ("now", now);
               ("newLeaseUntil", newLeaseUntil); ("token", ownershipToken) |]
        return rows > 0
    }
```

### 3.8 `removeJob`、查询、统计

```fsharp
/// 删除作业记录（会话清理成功后删除）
let removeJob (conn: SqliteConnection) (kind: string) (jobKey: string) : Async<unit> =
    execute conn "DELETE FROM jobs WHERE kind = @kind AND job_key = @jobKey"
        [| ("kind", kind); ("jobKey", jobKey) |]

/// 获取单个作业
let getJob (conn: SqliteConnection) (kind: string) (jobKey: string)
    : Async<JobSummary option> = ...

/// 列出作业（按 updated_at 降序）
let listJobs (conn: SqliteConnection) (kind: string) (status: string option) (limit: int)
    : Async<JobSummary list> = ...

/// 作业统计
let getJobStats (conn: SqliteConnection) (kind: string) : Async<JobStats> =
    async {
        return! querySingle conn """
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN status = 'running' AND lease_until > @now THEN 1 ELSE 0 END) AS running,
                SUM(CASE WHEN status = 'done' THEN 1 ELSE 0 END) AS done,
                SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END) AS error
            FROM jobs WHERE kind = @kind
        """ [| ("kind", kind); ("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) |]
    }

/// 清理旧作业记录
let pruneCompletedJobs (conn: SqliteConnection) (kind: string) (olderThanDays: int)
    : Async<int> = ...
```

## 4. 心跳续租模式

长时间运行的作业（如 LLM 整合）需要后台心跳防止租约过期。

```fsharp
/// 启动后台心跳任务。返回 CancellationTokenSource 供完成时取消。
/// 对应 Codex memories/write/phase2.rs 中每 90 秒调用 heartbeat_global_phase2_job
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
                        use conn = openDb()
                        let! renewed = heartbeat conn kind jobKey ownershipToken leaseMs
                        if not renewed then
                            // 所有权丢失（被其他 worker 回收），停止心跳
                            Log.warning "Heartbeat failed: ownership lost for %s/%s" kind jobKey
                            cts.Cancel()
                    with ex ->
                        Log.warning "Heartbeat error for %s/%s: %s" kind jobKey ex.Message
        },
        cts.Token)
    cts
```

**使用方式**：

```fsharp
let! claimResult = JobQueue.tryClaim conn kind jobKey watermark leaseMs maxRunning
match claimResult with
| Claimed token ->
    // 启动心跳
    let heartbeatCts = JobQueue.startHeartbeat openDb kind jobKey token leaseMs HeartbeatIntervalMs
    try
        // 执行长时间作业...
        do! longRunningWork()
        use conn2 = openDb()
        let! _ = JobQueue.markSucceeded conn2 kind jobKey token
        ()
    with ex ->
        use conn2 = openDb()
        let! _ = JobQueue.markFailed conn2 kind jobKey token ex.Message retryDelayMs
        ()
    finally
        heartbeatCts.Cancel()
        heartbeatCts.Dispose()
| _ -> ()
```

## 5. 租约过期回收

Codex 通过 `lease_until` 实现卡死作业的自动回收（`memories.rs:571-576`）。不需要额外的"崩溃恢复"步骤——过期租约在下一次 `tryClaim` 时自动被回收。

**回收条件**（已内嵌在 `tryClaim` 的 `ON CONFLICT` WHERE 子句中）：

```sql
-- 以下任一条件满足，即可覆盖现有 running 作业：
WHERE
    jobs.status != 'running'              -- 不是 running
    OR jobs.lease_until IS NULL           -- 无租约
    OR jobs.lease_until <= @now           -- 租约已过期（卡死/崩溃）
```

这意味着：
- 进程崩溃 → 租约到期后自动回收（最多等 `leaseMs`）
- async 卡死 → 心跳停止后租约到期，自动回收
- 正常运行 → 心跳续租，不会被误回收
- **无需启动时批量恢复**——过期租约是懒回收的

## 6. 并发控制

### 6.1 `BEGIN IMMEDIATE` 事务

对应 Codex `memories.rs:491`。

```fsharp
/// 开始 IMMEDIATE 事务。SQLite 中 IMMEDIATE 会立即获取 RESERVED 锁，
/// 防止其他写入事务并发执行，但允许读取继续。
/// 对应 Codex memories.rs:491 的 pool.begin_with("BEGIN IMMEDIATE")
let beginImmediate (conn: SqliteConnection) : Async<SqliteTransaction> =
    async {
        let tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable)
        // Microsoft.Data.Sqlite 在 Serializable 级别使用 BEGIN IMMEDIATE
        return tx
    }
```

### 6.2 并发作业计数

`tryClaim` 的 SQL 中包含子查询限制同类型并发 running 作业数（对应 Codex `memories.rs:553-559`）：

```sql
WHERE (
    SELECT COUNT(*)
    FROM jobs
    WHERE kind = @kind
      AND status = 'running'
      AND lease_until IS NOT NULL
      AND lease_until > @now
) < @maxRunningJobs
```

**只计算有效租约的 running 作业**。租约过期的 running 作业不计入并发数——它们被视为可回收的卡死作业。

## 7. 水印系统

### 7.1 整合作业水印

```fsharp
/// 从会话的 updated_at 时间戳计算输入水印。
/// 如果会话自上次整合后无新消息，水印不变，tryClaim 返回 SkippedUpToDate。
let sessionWatermark (entry: SessionIndexEntry) : int64 =
    entry.UpdatedAt.ToUnixTimeMilliseconds()
```

### 7.2 水印推进时重置重试

对应 Codex `memories.rs:564-567`。当 `inputWatermark > COALESCE(jobs.input_watermark, -1)` 时：
- `retry_remaining` 重置为 `DefaultRetryRemaining`（3）
- `retry_at` 清零（忽略退避）
- 即使之前重试次数耗尽，新数据也会触发全新的重试周期

### 7.3 清理作业

清理作业成功删除会话后调用 `removeJob` 删除作业记录（会话已不存在）。失败时调用 `markFailed` 记录错误以便重试。

## 8. 现有服务改造

### 8.1 AutoCompactService 改造

```fsharp
let compactPass
    (deps: AgentDependencies)
    (openDb: unit -> SqliteConnection)
    (sessionTtlMinutes: int)
    (getActiveSids: unit -> SessionId Set)
    : Async<CompactPassResult> =

    async {
        use conn = openDb()
        let mutable processed = 0
        let mutable skipped = 0
        let mutable failed = 0
        let mutable succeeded = 0

        let! candidates =
            StateDb.listIdleSessionsForCompaction conn sessionTtlMinutes
                deps.Config.MemoryWindowSize (getActiveSids()) 50

        for entry in candidates do
            processed <- processed + 1
            let watermark = sessionWatermark entry

            use conn2 = openDb()
            let! outcome =
                JobQueue.tryClaim conn2 JobKind.Consolidation entry.Id
                    watermark ConsolidationLeaseMs DefaultMaxRunningJobs

            match outcome with
            | SkippedUpToDate | SkippedRetryBackoff
            | SkippedRetryExhausted | SkippedRunning ->
                skipped <- skipped + 1

            | Claimed token ->
                // 启动心跳（整合可能耗时数分钟）
                let heartbeatCts =
                    JobQueue.startHeartbeat openDb JobKind.Consolidation entry.Id
                        token ConsolidationLeaseMs HeartbeatIntervalMs
                try
                    let! snapResult = deps.LoadSession entry.Id
                    match snapResult with
                    | Error _ ->
                        use c = openDb()
                        let! _ = JobQueue.markFailed c JobKind.Consolidation entry.Id
                                     token "Failed to load session" ConsolidationRetryDelayMs
                        failed <- failed + 1
                    | Ok snap ->
                        if snap.messageCount - snap.lastConsolidated < deps.Config.MemoryWindowSize then
                            use c = openDb()
                            let! _ = JobQueue.markSucceeded c JobKind.Consolidation entry.Id token
                            skipped <- skipped + 1
                        else
                            let! consolidationResult = consolidate snap deps
                            match consolidationResult with
                            | Ok (Consolidated (_, _, newIndex)) ->
                                match SessionSnapshot.advanceConsolidated newIndex snap with
                                | Ok newSnap ->
                                    let! _ = deps.PersistSession newSnap
                                    use c = openDb()
                                    let! _ = JobQueue.markSucceeded c JobKind.Consolidation entry.Id token
                                    succeeded <- succeeded + 1
                                | Error e ->
                                    use c = openDb()
                                    let! _ = JobQueue.markFailed c JobKind.Consolidation entry.Id
                                                 token e ConsolidationRetryDelayMs
                                    failed <- failed + 1
                            | Ok ConsolidationSkipped ->
                                use c = openDb()
                                let! _ = JobQueue.markSucceeded c JobKind.Consolidation entry.Id token
                                skipped <- skipped + 1
                            | Error e ->
                                use c = openDb()
                                let! _ = JobQueue.markFailed c JobKind.Consolidation entry.Id
                                             token (sprintf "%A" e) ConsolidationRetryDelayMs
                                failed <- failed + 1
                with ex ->
                    try
                        use c = openDb()
                        let! ok = JobQueue.markFailed c JobKind.Consolidation entry.Id
                                      token ex.Message ConsolidationRetryDelayMs
                        if not ok then
                            // 所有权丢失，尝试 unowned 恢复
                            use c2 = openDb()
                            let! _ = JobQueue.markFailedIfUnowned c2 JobKind.Consolidation entry.Id
                                         token ex.Message ConsolidationRetryDelayMs
                            ()
                    with _ -> ()
                    failed <- failed + 1
                finally
                    heartbeatCts.Cancel()
                    heartbeatCts.Dispose()

        return { Processed = processed; Succeeded = succeeded; Skipped = skipped; Failed = failed }
    }
```

### 8.2 SessionCleanupService 改造

```fsharp
let cleanupPass
    (openDb: unit -> SqliteConnection)
    (workspacePath: string)
    (cleanupDays: int)
    : Async<CleanupPassResult> =

    async {
        use conn = openDb()
        let mutable deleted = 0
        let mutable failed = 0

        let! stale = StateDb.listStaleSessionsForCleanup conn cleanupDays 100

        for entry in stale do
            use conn2 = openDb()
            let cleanupWatermark = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let! outcome =
                JobQueue.tryClaim conn2 JobKind.SessionCleanup entry.Id
                    cleanupWatermark CleanupLeaseMs DefaultMaxRunningJobs

            match outcome with
            | Claimed token ->
                try
                    do! JsonlStore.deleteSession (Some openDb) entry.Id workspacePath
                    use c = openDb()
                    do! JobQueue.removeJob c JobKind.SessionCleanup entry.Id
                    deleted <- deleted + 1
                with ex ->
                    use c = openDb()
                    let! _ = JobQueue.markFailed c JobKind.SessionCleanup entry.Id
                                 token ex.Message CleanupRetryDelayMs
                    failed <- failed + 1
            | _ -> ()

        return { Deleted = deleted; Failed = failed }
    }
```

## 9. 与 Codex 实现的完整对照

### 9.1 领取逻辑对照

| 步骤 | Codex（`try_claim_stage1_job`） | BotSharp（`tryClaim`） |
|------|-------------------------------|----------------------|
| 事务类型 | `BEGIN IMMEDIATE`（`memories.rs:491`） | `BEGIN IMMEDIATE`（完整复刻） |
| 所有权 | `Uuid::new_v4()` ownership_token（`memories.rs:537`） | `System.Guid.NewGuid()` ownership_token |
| Worker 标识 | `worker_id`（线程 ID）（`memories.rs:536`） | `"pid:threadId"` 格式 |
| 租约 | `lease_until = now + lease_seconds`（`memories.rs:540`） | `lease_until = now + leaseMs` |
| 并发计数 | `SELECT COUNT(*) ... < max_running_jobs`（`memories.rs:553-559`） | 完整复刻，含排除自身的子查询 |
| 水印比较 | `last_success_watermark >= input_watermark`（`memories.rs:510-528`） | 完整复刻 |
| 水印重置 | `WHEN excluded.input_watermark > COALESCE(..., -1) THEN 3`（`memories.rs:564-567`） | 完整复刻 |
| 租约过期回收 | `jobs.lease_until <= excluded.started_at`（`memories.rs:571-576`） | 完整复刻 |
| 退避检查 | `retry_at <= excluded.started_at`（`memories.rs:579`） | 完整复刻 |
| Fallback 诊断 | `memories.rs:614-645`（读取现有 job 判断跳过原因） | 完整复刻 |

### 9.2 完成/失败对照

| 操作 | Codex | BotSharp |
|------|-------|---------|
| 成功 | `ownership_token` 校验 + `status='done'`（`memories.rs:676-695`） | 完整复刻 |
| 失败 | `ownership_token` 校验 + `retry_remaining -= 1`（`memories.rs:830-852`） | 完整复刻 |
| 失败（所有权丢失） | `ownership_token = ? OR IS NULL`（`memories.rs:1151-1185`） | 完整复刻为 `markFailedIfUnowned` |
| 心跳续租 | `lease_until` 延长（`memories.rs:1025-1041`） | 完整复刻为 `heartbeat` |
| 租约过期回收 | 下次 `tryClaim` 自动回收（`memories.rs:571-576`） | 完整复刻（无需启动时批量恢复） |

### 9.3 不采纳的 Codex 机制

以下是 BotSharp 当前场景不需要的 Codex 特有机制（与分布式无关）：

| Codex 机制 | 不采纳原因 |
|-----------|-----------|
| Phase 2 全局单例锁 + 6 小时冷却 | BotSharp 无两阶段记忆流水线 |
| `enqueue_global_consolidation` 水印递增 | BotSharp 无全局整合 |
| Agent Jobs / Agent Job Items 批量表 | BotSharp 无批量 CSV 处理场景 |
| Backfill State 单例表 | BotSharp 无 rollout 元数据回填 |

## 10. 可观测性

### 10.1 `/jobs` 命令

```
/jobs consolidation

Consolidation Jobs (3 total: 1 done, 1 error, 1 running)
─────────────────────────────────────────────────────────
  telegram_123    done     2026-05-01 14:30   watermark: 1714567890
  discord_abc     error    2026-05-01 14:25   retry in 12m (2 remaining)
                           Error: LLM rate limit exceeded
  unified_default running  2026-05-01 14:35   lease: 47m remaining
                           worker: 12345:8
```

### 10.2 日志

```
[auto-compact] Pass completed: 12 processed, 8 succeeded, 3 skipped (up-to-date), 1 failed
[auto-compact] Lease expired on consolidation/telegram_old — reclaimed by worker 12345:8
```

## 11. 修改文件清单

| 文件 | 修改内容 | 复杂度 |
|------|---------|--------|
| **新增** `Infrastructure/Storage/JobQueue.fs` | 完整作业队列（tryClaim/markSucceeded/markFailed/markFailedIfUnowned/heartbeat/removeJob 等） | 高 |
| `Infrastructure/Storage/StateDb.fs` | 迁移脚本新增 jobs 表 | 低 |
| `Application/AutoCompactService.fs` | 改用 JobQueue，含心跳续租 | 高 |
| `Application/SessionCleanupService.fs` | 改用 JobQueue | 中 |
| `Domain/Types.fs` | ClaimOutcome, JobSummary, JobStats, CompactPassResult, CleanupPassResult | 低 |
| `Program.fs` | 传入 openDb 到服务 | 低 |

## 12. 实施计划

### Phase 1：JobQueue 核心（3 天）

1. `jobs` 表迁移
2. `JobQueue.fs`：`tryClaim`（含 `BEGIN IMMEDIATE`、ownership_token、lease_until、并发计数）
3. `markSucceeded`、`markFailed`、`markFailedIfUnowned`
4. `heartbeat`、`startHeartbeat`
5. 查询和统计函数
6. 单元测试：并发领取、租约过期回收、所有权校验、水印逻辑

### Phase 2：AutoCompactService（2 天）

1. 重写 `compactPass`：完整生命周期（领取 → 心跳 → 完成/失败 → 所有权丢失恢复）
2. 集成测试

### Phase 3：SessionCleanupService（1 天）

1. 重写 `cleanupPass`
2. 集成测试

### Phase 4：可观测性（1 天）

1. `/jobs` 命令（含租约剩余时间、worker 标识显示）
2. 日志增强

## 13. 测试策略

```fsharp
module JobQueueTests =

    [<Fact>]
    let ``tryClaim returns ownership token`` () =
        use conn = createTestDb()
        let! outcome = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        match outcome with
        | Claimed token -> Assert.False(String.IsNullOrEmpty token)
        | _ -> Assert.Fail "Expected Claimed"

    [<Fact>]
    let ``markSucceeded requires matching ownership token`` () =
        use conn = createTestDb()
        let! (Claimed token) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        // 错误 token → 不更新
        let! ok = JobQueue.markSucceeded conn "test" "key1" "wrong-token"
        Assert.False(ok)
        // 正确 token → 更新成功
        let! ok = JobQueue.markSucceeded conn "test" "key1" token
        Assert.True(ok)

    [<Fact>]
    let ``expired lease allows reclaim by another worker`` () =
        use conn = createTestDb()
        // 领取，租约 1 毫秒（立即过期）
        let! (Claimed token1) = JobQueue.tryClaim conn "test" "key1" 100L 1 8
        Async.Sleep 10 |> Async.RunSynchronously
        // 另一个 worker 可以回收
        let! outcome = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        match outcome with
        | Claimed token2 ->
            Assert.NotEqual(token1, token2)
            // 旧 token 无法完成
            let! ok = JobQueue.markSucceeded conn "test" "key1" token1
            Assert.False(ok)
            // 新 token 可以完成
            let! ok = JobQueue.markSucceeded conn "test" "key1" token2
            Assert.True(ok)
        | _ -> Assert.Fail "Expected Claimed after lease expiry"

    [<Fact>]
    let ``valid lease prevents reclaim`` () =
        use conn = createTestDb()
        let! (Claimed _) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        // 租约未过期 → 不能回收
        let! outcome = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        Assert.Equal(SkippedRunning, outcome)

    [<Fact>]
    let ``max running jobs limits concurrency`` () =
        use conn = createTestDb()
        // maxRunningJobs = 2
        let! (Claimed _) = JobQueue.tryClaim conn "test" "a" 1L 3600000 2
        let! (Claimed _) = JobQueue.tryClaim conn "test" "b" 2L 3600000 2
        // 第 3 个被拒绝
        let! outcome = JobQueue.tryClaim conn "test" "c" 3L 3600000 2
        Assert.Equal(SkippedRunning, outcome)

    [<Fact>]
    let ``expired lease does not count toward max running`` () =
        use conn = createTestDb()
        // 2 个作业，租约 1ms
        let! (Claimed _) = JobQueue.tryClaim conn "test" "a" 1L 1 2
        let! (Claimed _) = JobQueue.tryClaim conn "test" "b" 2L 1 2
        Async.Sleep 10 |> Async.RunSynchronously
        // 租约过期后不计入并发数，新作业可领取
        let! outcome = JobQueue.tryClaim conn "test" "c" 3L 3600000 2
        match outcome with
        | Claimed _ -> ()
        | _ -> Assert.Fail "Expected Claimed: expired leases should not count"

    [<Fact>]
    let ``heartbeat extends lease`` () =
        use conn = createTestDb()
        let! (Claimed token) = JobQueue.tryClaim conn "test" "key1" 100L 100 8  // 100ms 租约
        // 续租到 1 小时
        let! ok = JobQueue.heartbeat conn "test" "key1" token 3600000
        Assert.True(ok)
        // 原 100ms 后仍然不能被回收
        Async.Sleep 200 |> Async.RunSynchronously
        let! outcome = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        Assert.Equal(SkippedRunning, outcome)

    [<Fact>]
    let ``heartbeat fails with wrong token`` () =
        use conn = createTestDb()
        let! (Claimed _) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        let! ok = JobQueue.heartbeat conn "test" "key1" "wrong-token" 3600000
        Assert.False(ok)

    [<Fact>]
    let ``markFailedIfUnowned recovers lost ownership`` () =
        use conn = createTestDb()
        let! (Claimed token1) = JobQueue.tryClaim conn "test" "key1" 100L 1 8
        Async.Sleep 10 |> Async.RunSynchronously
        // 被另一个 worker 回收
        let! (Claimed token2) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        // 旧 worker 用 markFailed 失败（token 不匹配）
        let! ok = JobQueue.markFailed conn "test" "key1" token1 "timeout" 0
        Assert.False(ok)
        // 新 worker 完成后，ownership_token 被清除...
        // 或用 markFailedIfUnowned 尝试恢复
        let! ok = JobQueue.markFailedIfUnowned conn "test" "key1" token2 "cancel" 0
        Assert.True(ok)

    [<Fact>]
    let ``watermark advance resets retries`` () =
        use conn = createTestDb()
        let! (Claimed t) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        let! _ = JobQueue.markFailed conn "test" "key1" t "err" 0
        let! (Claimed t) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        let! _ = JobQueue.markFailed conn "test" "key1" t "err" 0
        let! (Claimed t) = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        let! _ = JobQueue.markFailed conn "test" "key1" t "err" 0
        // retry_remaining = 0 → 跳过
        let! outcome = JobQueue.tryClaim conn "test" "key1" 100L 3600000 8
        Assert.Equal(SkippedRetryExhausted, outcome)
        // watermark 推进 → 重置重试，可再次领取
        let! outcome = JobQueue.tryClaim conn "test" "key1" 200L 3600000 8
        match outcome with
        | Claimed _ -> ()
        | _ -> Assert.Fail "Expected Claimed after watermark advance"

    [<Fact>]
    let ``BEGIN IMMEDIATE prevents concurrent claim`` () =
        // 此测试需要两个独立连接模拟并发
        use conn1 = createTestDb()
        use conn2 = openSameDb conn1
        // 两个 worker 同时尝试领取同一个 job
        let task1 = JobQueue.tryClaim conn1 "test" "key1" 100L 3600000 8 |> Async.StartAsTask
        let task2 = JobQueue.tryClaim conn2 "test" "key1" 100L 3600000 8 |> Async.StartAsTask
        Task.WaitAll(task1, task2)
        let results = [task1.Result; task2.Result]
        let claimed = results |> List.filter (function Claimed _ -> true | _ -> false)
        let skipped = results |> List.filter (function SkippedRunning -> true | _ -> false)
        // 恰好一个成功，一个被拒绝
        Assert.Equal(1, claimed.Length)
        Assert.Equal(1, skipped.Length)
```
