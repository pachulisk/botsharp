# BotSharp 混合存储设计方案

> 借鉴 Codex 的 JSONL + SQLite 双存储模式，为 BotSharp 引入 SQLite 派生索引层。

## 1. 设计目标

### 当前痛点

BotSharp 当前采用纯文件系统存储，存在以下问题：

| 痛点 | 现状 | 影响 |
|------|------|------|
| 会话列表 | 扫描 `sessions/*.jsonl` 目录 | O(n) 文件 stat，会话多时变慢 |
| 会话搜索 | 无法按标题/日期/频道筛选 | 用户无法检索历史对话 |
| 整合状态 | `.dream_cursor` 单行文本文件 | 脆弱，无法追踪失败/重试/时间线 |
| 记忆价值排序 | 无使用频率追踪 | 无法判断哪些记忆最有价值 |
| 过期清理 | 基于 mtime 遍历文件 | 无法按业务维度（消息数、最后活跃）清理 |
| 元数据查询 | 需加载整个 JSONL 反序列化 | 只为获取消息数/首条消息就要全量加载 |

### 设计原则

1. **JSONL 仍是数据源头**（source of truth）——所有写入先到 JSONL，再同步到 SQLite
2. **SQLite 是派生索引**（derived cache）——可随时从 JSONL 重建，容许丢失
3. **SQLite 写入失败不阻塞主流程**——记录日志后继续，不影响对话
4. **最小改动原则**——扩展现有模块，不引入新抽象层
5. **保持 BotSharp 简洁性**——不引入分布式锁、多阶段流水线等 Codex 的规模化机制

## 2. 架构总览

### 数据流

```
用户消息
    ↓
SessionActor（MailboxProcessor 序列化写入）
    ↓
┌───────────────────────────────────────┐
│            写入路径（有序）              │
│                                       │
│  1. 写入 JSONL（原子写 tmp→rename）     │
│  2. 同步到 SQLite（best-effort）        │
│         ↓ 失败则记录日志，不阻塞         │
└───────────────────────────────────────┘
    ↓
┌───────────────────────────────────────┐
│            读取路径                     │
│                                       │
│  列表/搜索/统计  →  SQLite（快速索引）   │
│  加载完整对话     →  JSONL（数据源头）    │
│  构建系统提示词   →  Markdown 文件       │
└───────────────────────────────────────┘
    ↓
┌───────────────────────────────────────┐
│            安全网                       │
│                                       │
│  rebuildIndex：扫描 JSONL + dreams.jsonl │
│  重建整个 SQLite，容错运行               │
└───────────────────────────────────────┘
```

### 写入顺序保证

```
persistSession snapshot =
    1. JsonlStore.persistSession snapshot wp        // JSONL 先写（原子）
    2. StateDb.syncSession snapshot |> bestEffort   // SQLite 后写（可失败）
```

这个顺序至关重要：如果步骤 2 失败，JSONL 仍然是完整的；下次启动时 `rebuildIndex` 会修复 SQLite。反过来则不行——如果 SQLite 写成功但 JSONL 失败，数据就丢了。

## 3. SQLite Schema 设计

### 数据库文件

```
{workspacePath}/botsharp.sqlite
```

单文件，WAL 模式，与 sessions/ 目录同级。

### 3.1 `sessions` 表

替代文件系统扫描，提供会话的结构化索引。

```sql
CREATE TABLE sessions (
    id                TEXT PRIMARY KEY,       -- SessionId（安全编码后）
    channel           TEXT NOT NULL,          -- 频道类型：telegram, discord, cli, unified
    chat_id           TEXT,                   -- 频道内的聊天 ID
    created_at        INTEGER NOT NULL,       -- Unix 毫秒
    updated_at        INTEGER NOT NULL,       -- Unix 毫秒
    message_count     INTEGER NOT NULL DEFAULT 0,
    last_consolidated INTEGER NOT NULL DEFAULT 0, -- 对应 SessionSnapshot.LastConsolidated_
    first_user_message TEXT,                  -- 首条用户消息（截断至 200 字符）
    title             TEXT,                   -- 对话标题（可由用户设置或从首条消息派生）
    archived_at       INTEGER                 -- 归档时间，NULL 表示活跃
);

CREATE INDEX idx_sessions_updated_at ON sessions(updated_at DESC);
CREATE INDEX idx_sessions_channel ON sessions(channel, updated_at DESC);
CREATE INDEX idx_sessions_archived ON sessions(archived_at, updated_at DESC);
```

**字段说明：**

- `id`：与 JSONL 文件名对应，如 `telegram_123_456`
- `channel` / `chat_id`：从 SessionId 解析，支持按频道筛选
- `message_count`：不需加载 JSONL 即可知道消息数量
- `last_consolidated`：替代 `.dream_cursor`，整合状态与会话绑定
- `first_user_message`：支持搜索和列表展示
- `title`：支持标题搜索

### 3.2 `consolidation_entries` 表

替代 `dreams.jsonl`，提供可查询的整合历史。

```sql
CREATE TABLE consolidation_entries (
    sha               TEXT PRIMARY KEY,       -- 8 字符 SHA256 前缀
    session_id        TEXT,                   -- 关联的会话 ID（可为 NULL，跨会话整合）
    occurred_at       INTEGER NOT NULL,       -- Unix 毫秒
    summary           TEXT NOT NULL,          -- 2-5 句摘要（history_entry）
    message_count     INTEGER NOT NULL,       -- 本次整合涵盖的消息数
    generated_at      INTEGER NOT NULL,       -- 整合完成时间（Unix 毫秒）
    model_used        TEXT,                   -- 使用的 LLM 模型名
    status            TEXT NOT NULL DEFAULT 'completed',  -- completed / failed
    error_message     TEXT,                   -- 失败原因（status=failed 时）
    FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE SET NULL
);

CREATE INDEX idx_consolidation_session ON consolidation_entries(session_id, occurred_at DESC);
CREATE INDEX idx_consolidation_time ON consolidation_entries(occurred_at DESC);
```

**相比 dreams.jsonl 的改进：**

- 可按会话筛选整合历史
- 可追踪整合失败（status + error_message）
- 可统计整合频率和模型使用
- 支持按时间范围查询

### 3.3 `memory_usage` 表

**全新能力**，借鉴 Codex 的 `stage1_outputs.usage_count` 和 `last_usage`。

```sql
CREATE TABLE memory_usage (
    memory_key        TEXT PRIMARY KEY,       -- 记忆标识（如 "memory:global" 或 session_id）
    source_session_id TEXT,                   -- 记忆来源的会话 ID
    usage_count       INTEGER NOT NULL DEFAULT 0,  -- 被引用次数
    last_usage        INTEGER,                -- 最后使用时间（Unix 毫秒）
    created_at        INTEGER NOT NULL,       -- 首次记录时间
    summary           TEXT,                   -- 记忆来源的简要描述
    FOREIGN KEY(source_session_id) REFERENCES sessions(id) ON DELETE SET NULL
);

CREATE INDEX idx_memory_usage_rank
    ON memory_usage(usage_count DESC, last_usage DESC);
```

**粒度说明：**

当前阶段（Phase 4），MEMORY.md 是单个整体文件，`memory_key` 仅使用 `"memory:global"` 这一个键——即整张表只有一行，追踪的是"MEMORY.md 作为整体被注入了多少次"。这已经足够衡量记忆系统的活跃度和为未来决策提供数据。

未来如果需要更细粒度的追踪（例如按 Markdown 二级标题分段），可以在整合时提取 `## ` 标题作为 `memory_key`（如 `"memory:section:用户偏好"`），将追踪下沉到段落级别。但当前设计有意保持简单，避免过早引入分段解析的复杂度。

**用途：**

- 追踪 MEMORY.md 整体使用频率（每次注入系统提示词时 +1）
- 为未来的记忆分层（保留高频记忆、淘汰低频记忆）提供数据基础
- 在整合时可参考使用频率决定记忆保留策略

## 4. 模块设计

### 4.1 新增模块：`StateDb.fs`

纯查询 + 同步模块，不涉及 JSONL 读写。

```
src/BotSharp/Infrastructure/Storage/StateDb.fs
```

```fsharp
module BotSharp.Infrastructure.Storage.StateDb

open Microsoft.Data.Sqlite

// ── 初始化 ──

/// 初始化数据库（创建 + 迁移），返回连接工厂
/// 连接工厂每次调用返回新的 SqliteConnection（线程安全）
/// SQLite WAL 模式允许多连接并发读写，但每个连接对象本身不是线程安全的
/// SessionActor（MailboxProcessor 线程）和 AutoCompactService（后台线程）各自通过工厂获取独立连接
val init : workspacePath: string -> Async<unit -> SqliteConnection>

/// 从 JSONL 和 dreams.jsonl 重建全部索引
val rebuildIndex : workspacePath: string -> conn: SqliteConnection -> Async<RebuildResult>

// ── 会话同步（写入路径，由 JsonlStore 调用） ──

/// 将 SessionSnapshot 元数据同步到 sessions 表
val syncSession : conn: SqliteConnection -> snapshot: SessionSnapshot -> Async<unit>

/// 删除会话索引
val deleteSessionIndex : conn: SqliteConnection -> sessionId: SessionId -> Async<unit>

/// 归档会话
val archiveSession : conn: SqliteConnection -> sessionId: SessionId -> Async<unit>

// ── 整合同步 ──

/// 将 DreamEntry 同步到 consolidation_entries 表
val syncConsolidationEntry
    : conn: SqliteConnection
    -> sessionId: SessionId
    -> entry: DreamEntry
    -> modelUsed: string option
    -> Async<unit>

/// 记录整合失败
val recordConsolidationFailure
    : conn: SqliteConnection
    -> sessionId: SessionId
    -> error: string
    -> Async<unit>

// ── 记忆使用追踪 ──

/// 记录记忆被引用（usage_count + 1，更新 last_usage）
val recordMemoryUsage : conn: SqliteConnection -> memoryKey: string -> Async<unit>

/// 获取按使用频率排序的记忆列表
val listMemoryByUsage : conn: SqliteConnection -> limit: int -> Async<MemoryUsageEntry list>

// ── 查询（读取路径） ──

/// 分页列出会话（按 updated_at 降序）
val listSessions
    : conn: SqliteConnection
    -> page: int
    -> pageSize: int
    -> channel: string option
    -> archived: bool option
    -> Async<SessionIndexEntry list>

/// 按关键词搜索会话
val searchSessions
    : conn: SqliteConnection
    -> query: string
    -> limit: int
    -> Async<SessionIndexEntry list>

/// 获取会话统计
val getSessionStats
    : conn: SqliteConnection
    -> sessionId: SessionId
    -> Async<SessionStats option>

/// 获取整合历史
val listConsolidationEntries
    : conn: SqliteConnection
    -> sessionId: SessionId option
    -> limit: int
    -> Async<ConsolidationIndexEntry list>

/// 查询待清理的过期会话
val listStaleSessionsForCleanup
    : conn: SqliteConnection
    -> staleDays: int
    -> limit: int
    -> Async<SessionIndexEntry list>

/// 查询待整合的空闲会话
val listIdleSessionsForCompaction
    : conn: SqliteConnection
    -> idleMinutes: int
    -> memoryWindowSize: int
    -> activeSids: SessionId Set
    -> limit: int
    -> Async<SessionIndexEntry list>
```

**返回类型定义：**

```fsharp
type SessionIndexEntry = {
    Id               : SessionId
    Channel          : string
    ChatId           : string option
    CreatedAt        : DateTimeOffset
    UpdatedAt        : DateTimeOffset
    MessageCount     : int
    LastConsolidated : int
    FirstUserMessage : string option
    Title            : string option
    ArchivedAt       : DateTimeOffset option
}

type SessionStats = {
    MessageCount         : int
    LastConsolidated     : int
    UnconsolidatedCount  : int
    ConsolidationCount   : int       // 历史整合次数
    TotalConsolidatedMsgs: int       // 累计整合消息数
}

type ConsolidationIndexEntry = {
    Sha          : string
    SessionId    : SessionId option
    OccurredAt   : DateTimeOffset
    Summary      : string
    MessageCount : int
    GeneratedAt  : DateTimeOffset
    ModelUsed    : string option
    Status       : string
    ErrorMessage : string option
}

type MemoryUsageEntry = {
    MemoryKey       : string
    SourceSessionId : SessionId option
    UsageCount      : int
    LastUsage       : DateTimeOffset option
    Summary         : string option
}

type RebuildResult = {
    SessionsIndexed       : int
    ConsolidationsIndexed : int
    Errors                : string list
}
```

### 4.2 扩展现有模块

#### JsonlStore.fs 扩展

在 `persistSession` 和 `deleteSession` 之后追加 SQLite 同步调用：

```fsharp
// 现有签名不变，内部扩展

let persistSession (openDb: (unit -> SqliteConnection) option) (snapshot: SessionSnapshot)
                   (workspacePath: string) : Async<Result<unit, StorageError>> =
    async {
        // 步骤 1：原子写入 JSONL（不变）
        let! jsonlResult = persistSessionJsonl snapshot workspacePath
        match jsonlResult with
        | Error e -> return Error e
        | Ok () ->
            // 步骤 2：同步到 SQLite（best-effort）
            match openDb with
            | Some factory ->
                try
                    use conn = factory()
                    do! StateDb.syncSession conn snapshot
                with ex ->
                    Log.warning "SQLite sync failed for session %s: %s" (snapshot.id) (ex.Message)
            | None -> ()
            return Ok ()
    }

let deleteSession (openDb: (unit -> SqliteConnection) option) (sid: SessionId)
                  (workspacePath: string) : Async<Result<unit, StorageError>> =
    async {
        // 步骤 1：删除 JSONL 文件（不变）
        let! result = deleteSessionJsonl sid workspacePath
        match result with
        | Error e -> return Error e
        | Ok () ->
            // 步骤 2：删除索引（best-effort）
            match openDb with
            | Some factory ->
                try
                    use conn = factory()
                    do! StateDb.deleteSessionIndex conn sid
                with ex ->
                    Log.warning "SQLite delete failed for session %s: %s" sid (ex.Message)
            | None -> ()
            return Ok ()
    }
```

#### MemoryConsolidator.fs 扩展

整合完成后同步到 SQLite：

```fsharp
// consolidateImpl 内部，在写入 HISTORY.md / MEMORY.md 之后追加：

// 步骤：同步整合记录到 SQLite
match deps.OpenStateDb with
| Some openDb ->
    use conn = openDb()
    let entry = { Sha = sha; OccurredAt = now; Summary = historyEntry; MessageCount = count }
    do! StateDb.syncConsolidationEntry conn sid entry (Some modelName)
        |> Async.catchAndLog "consolidation sync"
| None -> ()
```

#### ContextBuilder.fs 扩展

注入 MEMORY.md 时追踪使用：

```fsharp
// buildSystemPrompt 内部，读取 MEMORY.md 后：

match deps.OpenStateDb with
| Some openDb when memoryContent <> "" ->
    use conn = openDb()
    do! StateDb.recordMemoryUsage conn "memory:global"
        |> Async.catchAndLog "memory usage tracking"
| _ -> ()
```

### 4.3 AgentDependencies 扩展

```fsharp
type AgentDependencies = {
    // 现有字段（不变）
    LoadSession       : SessionId -> Async<Result<SessionSnapshot, StorageError>>
    PersistSession    : SessionSnapshot -> Async<Result<unit, StorageError>>
    BuildSystemPrompt : string option -> string -> Async<string>
    Config            : BotSharpConfig

    // 新增
    OpenStateDb       : (unit -> SqliteConnection) option  // 连接工厂，每次调用返回新连接
                                                           // None = SQLite 不可用，降级到纯文件
}
```

## 5. 迁移（Migration）系统

### 5.1 版本管理

采用与 Codex 类似的文件名前缀版本方案：

```
{workspacePath}/botsharp.sqlite    -- 数据库文件（WAL 模式）
```

迁移脚本内嵌于 `StateDb.fs`，通过 `user_version` PRAGMA 追踪：

```fsharp
let private CURRENT_VERSION = 1

let private migrations = [|
    // v0 → v1：初始 schema
    """
    CREATE TABLE sessions ( ... );
    CREATE TABLE consolidation_entries ( ... );
    CREATE TABLE memory_usage ( ... );
    CREATE INDEX ...;
    PRAGMA user_version = 1;
    """
|]

let private migrate (conn: SqliteConnection) : Async<unit> =
    async {
        let! currentVersion = queryScalar conn "PRAGMA user_version"
        for i in currentVersion .. (CURRENT_VERSION - 1) do
            do! execute conn migrations.[i]
    }
```

### 5.2 SQLite 配置

```fsharp
let private configureSqlite (conn: SqliteConnection) =
    async {
        do! execute conn "PRAGMA journal_mode = WAL"
        do! execute conn "PRAGMA synchronous = NORMAL"
        do! execute conn "PRAGMA busy_timeout = 5000"
        do! execute conn "PRAGMA auto_vacuum = INCREMENTAL"
    }
```

选择理由：
- **WAL**：允许并发读写（AutoCompactService 读 + SessionActor 写）
- **NORMAL sync**：对于派生索引足够安全（数据源头在 JSONL）
- **5s busy timeout**：避免 MailboxProcessor 序列化写入时偶发的锁等待
- **INCREMENTAL auto_vacuum**：启动时执行一次 `PRAGMA incremental_vacuum`，避免数据库文件无限膨胀

## 6. 索引重建（安全网）

`rebuildIndex` 是整个设计的安全网——SQLite 可以随时被删除并从 JSONL 重建。

```fsharp
let rebuildIndex (workspacePath: string) (conn: SqliteConnection) : Async<RebuildResult> =
    async {
        let mutable sessionsIndexed = 0
        let mutable consolidationsIndexed = 0
        let mutable errors = []

        // 阶段 1：清空现有索引
        do! execute conn "DELETE FROM memory_usage"
        do! execute conn "DELETE FROM consolidation_entries"
        do! execute conn "DELETE FROM sessions"

        // 阶段 2：扫描 sessions/*.jsonl，提取元数据
        let sessionFiles = Directory.GetFiles(sessionsDir, "*.jsonl")
        for file in sessionFiles do
            try
                let sid = Path.GetFileNameWithoutExtension(file) |> SessionId
                let! lines = File.ReadAllLinesAsync(file)
                match SessionParser.parseSessionFile sid (Array.toSeq lines) with
                | Ok snapshot ->
                    do! syncSession conn snapshot
                    sessionsIndexed <- sessionsIndexed + 1
                | Error errs ->
                    errors <- (sprintf "Parse error in %s: %A" file errs) :: errors
            with ex ->
                errors <- (sprintf "Error processing %s: %s" file ex.Message) :: errors

        // 阶段 3：扫描 dreams.jsonl，提取整合记录
        let dreamFile = Path.Combine(workspacePath, "dreams.jsonl")
        if File.Exists(dreamFile) then
            let! dreamLines = File.ReadAllLinesAsync(dreamFile)
            for line in dreamLines do
                try
                    match DreamStore.parseDreamLine line with
                    | Some entry ->
                        do! syncConsolidationEntry conn None entry None
                        consolidationsIndexed <- consolidationsIndexed + 1
                    | None -> ()
                with ex ->
                    errors <- (sprintf "Dream parse error: %s" ex.Message) :: errors

        return {
            SessionsIndexed = sessionsIndexed
            ConsolidationsIndexed = consolidationsIndexed
            Errors = errors
        }
    }
```

**触发时机：**

1. **首次启动**：检测到 `botsharp.sqlite` 不存在时自动重建
2. **手动触发**：用户执行 `/rebuild-index` 命令
3. **版本升级**：当 `PRAGMA user_version` < `CURRENT_VERSION` 且无法增量迁移时

## 7. `.dream_cursor` 迁移与 `loadSession` 读取路径

### 问题

当前 `parseSessionFile` 创建的 `SessionSnapshot` 的 `lastConsolidated = 0`。真实的整合指针存储在 `.dream_cursor` 文件中，由 `SessionActor` 在加载后手动恢复。引入 SQLite 后，整合状态需要从三个源中恢复，按优先级：

1. **SQLite `sessions.last_consolidated`**（最新，最可靠）
2. **`.dream_cursor` 文件**（旧方式，作为回退）
3. **默认值 0**（两者都不可用时）

### `loadSession` 扩展

```fsharp
let loadSession (openDb: (unit -> SqliteConnection) option) (sid: SessionId)
                (workspacePath: string) : Async<Result<SessionSnapshot, StorageError>> =
    async {
        // 步骤 1：从 JSONL 加载消息（不变）
        let! baseResult = loadSessionJsonl sid workspacePath
        match baseResult with
        | Error e -> return Error e
        | Ok snapshot ->
            // 步骤 2：恢复 lastConsolidated（三级回退）
            let! lastConsolidated =
                async {
                    // 优先级 1：从 SQLite 读取
                    match openDb with
                    | Some factory ->
                        try
                            use conn = factory()
                            let! stats = StateDb.getSessionStats conn sid
                            match stats with
                            | Some s -> return s.LastConsolidated
                            | None -> return! readCursorFallback sid workspacePath
                        with _ ->
                            return! readCursorFallback sid workspacePath
                    | None ->
                        return! readCursorFallback sid workspacePath
                }
            // 应用恢复的指针
            match SessionSnapshot.advanceConsolidated lastConsolidated snapshot with
            | Ok restored -> return Ok restored
            | Error _ -> return Ok snapshot  // 指针越界时保留原值
    }

/// 优先级 2 + 3：从 .dream_cursor 文件回退，再回退到 0
let private readCursorFallback (sid: SessionId) (wp: string) : Async<int> =
    async {
        let cursorPath = Path.Combine(wp, "memory", ".dream_cursor")
        if File.Exists(cursorPath) then
            try
                let! text = File.ReadAllTextAsync(cursorPath) |> Async.AwaitTask
                match Int32.TryParse(text.Trim()) with
                | true, v -> return v
                | _ -> return 0
            with _ -> return 0
        else return 0
    }
```

### 迁移策略

首次启用 SQLite 时，`rebuildIndex` 会将 `.dream_cursor` 的值读入 `sessions.last_consolidated`。之后所有写入都同时更新 SQLite，`.dream_cursor` 文件保留但不再作为主要源。

在确认 SQLite 稳定运行一段时间后（建议 2-4 周），可选择性清理 `.dream_cursor` 文件。

## 8. 现有功能增强

### 8.1 AutoCompactService 优化

**现状**：扫描 `sessions/*.jsonl` 文件，逐个 stat mtime，加载 JSONL 反序列化判断消息数。

**优化后**：一条 SQL 查询获取所有候选会话。

```fsharp
// 之前（O(n) 文件扫描 + 反序列化）
let sessionFiles = Directory.GetFiles(sessionsDir, "*.jsonl")
for file in sessionFiles do
    let mtime = File.GetLastWriteTimeUtc(file)
    if mtime < cutoff then
        let! snap = loadSession sid wp
        if unconsolidatedCount snap >= memoryWindowSize then ...

// 之后（单次 SQL 查询）
let! candidates =
    StateDb.listIdleSessionsForCompaction conn idleMinutes memoryWindowSize activeSids 50
for entry in candidates do
    let! snap = loadSession entry.Id wp
    // 仅对候选会话加载 JSONL
    ...
```

### 8.2 SessionCleanupService 优化

**现状**：扫描所有 JSONL 文件的 mtime。

**优化后**：

```fsharp
let! stale = StateDb.listStaleSessionsForCleanup conn staleDays 100
for entry in stale do
    do! deleteSession conn entry.Id wp
```

### 8.3 会话列表与搜索（新功能）

现有系统无法列出或搜索历史会话。引入 SQLite 后可直接支持：

```fsharp
// 分页列出最近会话
let! sessions = StateDb.listSessions conn page 20 (Some "telegram") None

// 搜索会话
let! results = StateDb.searchSessions conn "部署 nginx" 10
```

搜索实现使用 SQLite 的 `LIKE` 匹配 `first_user_message` 和 `title` 字段。对于个人 Agent 的数据量级，`LIKE '%keyword%'` 足够快，无需 FTS5。

### 8.4 整合历史查询（新功能）

```fsharp
// 查看某会话的整合历史
let! entries = StateDb.listConsolidationEntries conn (Some sid) 20

// 查看全局整合时间线
let! timeline = StateDb.listConsolidationEntries conn None 50
```

## 9. 配置扩展

在 `BotSharpConfig` 中添加：

```fsharp
type BotSharpConfig = {
    // 现有字段...

    // 新增
    EnableSqliteIndex    : bool    // 是否启用 SQLite 索引（默认 true）
    SqliteRebuildOnError : bool    // SQLite 打开失败时是否自动重建（默认 true）
}
```

**默认值：**

```fsharp
EnableSqliteIndex    = true
SqliteRebuildOnError = true
```

设置 `EnableSqliteIndex = false` 时完全降级到纯文件模式（`AgentDependencies.StateDbConn = None`），保持向后兼容。

## 10. 错误处理策略

### 核心原则

SQLite 是**增强层**，不是**必须层**。任何 SQLite 操作失败都不应影响主流程。

```fsharp
/// Best-effort 异步执行：失败时记录日志，不抛异常
let bestEffort (label: string) (op: Async<unit>) : Async<unit> =
    async {
        try
            do! op
        with ex ->
            Log.warning "[%s] SQLite operation failed (non-fatal): %s" label ex.Message
    }
```

### 降级策略

| 场景 | 行为 |
|------|------|
| SQLite 文件损坏 | 删除并从 JSONL 重建 |
| SQLite 写入失败 | 记录日志，继续主流程 |
| SQLite 读取失败 | 降级到文件系统扫描 |
| Schema 版本不匹配 | 尝试迁移；失败则删除重建 |

### 数据一致性

SQLite 与 JSONL 可能短暂不一致（写入 JSONL 后、同步 SQLite 前崩溃）。处理方式：

1. **启动时校验**：比较 `sessions/*.jsonl` 文件列表与 `sessions` 表，对不一致的条目触发增量同步
2. **定期修复**：AutoCompactService 每轮扫描时顺便修复索引
3. **手动重建**：`/rebuild-index` 命令全量重建

## 11. 实施计划

### Phase 1：基础设施（预计 2-3 天）

**目标**：引入 SQLite 依赖，建立迁移框架和重建能力。

1. 添加 `Microsoft.Data.Sqlite` NuGet 依赖
2. 实现 `StateDb.fs`：
   - `init`：打开/创建数据库 + 迁移
   - `configureSqlite`：WAL + NORMAL + busy_timeout
   - `migrate`：版本检查 + schema 创建
   - `rebuildIndex`：全量重建
3. 在 `Program.fs` 启动路径中调用 `StateDb.init`
4. 添加 `StateDbConn` 到 `AgentDependencies`
5. 单元测试：迁移、重建、降级

**新增文件：**
- `src/BotSharp/Infrastructure/Storage/StateDb.fs`

**修改文件：**
- `src/BotSharp/Domain/Types.fs`（添加新类型和配置字段）
- `src/BotSharp/Program.fs`（初始化 SQLite）

### Phase 2：写入路径集成（预计 2-3 天）

**目标**：所有写入操作同时同步到 SQLite。

1. 扩展 `JsonlStore.persistSession`：写入 JSONL 后调用 `StateDb.syncSession`
2. 扩展 `JsonlStore.deleteSession`：删除 JSONL 后调用 `StateDb.deleteSessionIndex`
3. 扩展 `MemoryConsolidator.consolidateImpl`：整合完成后调用 `StateDb.syncConsolidationEntry`
4. 实现启动时的增量一致性校验
5. 集成测试：确保 JSONL + SQLite 同步正确

**修改文件：**
- `src/BotSharp/Infrastructure/Storage/JsonlStore.fs`
- `src/BotSharp/Application/MemoryConsolidator.fs`

### Phase 3：读取路径优化（预计 2-3 天）

**目标**：后台服务和查询切换到 SQLite。

1. 优化 `AutoCompactService`：用 `listIdleSessionsForCompaction` 替代文件扫描
2. 优化 `SessionCleanupService`：用 `listStaleSessionsForCleanup` 替代 mtime 扫描
3. 实现 `listSessions` / `searchSessions` 查询
4. 实现 `listConsolidationEntries` 查询
5. 集成测试：确保查询结果与文件系统一致

**修改文件：**
- `src/BotSharp/Application/AutoCompactService.fs`
- `src/BotSharp/Application/SessionCleanupService.fs`

### Phase 4：记忆使用追踪（预计 1-2 天）

**目标**：引入记忆使用频率追踪。

1. 在 `ContextBuilder.buildSystemPrompt` 中添加 `recordMemoryUsage` 调用
2. 实现 `listMemoryByUsage` 查询
3. （可选）在整合时利用使用频率排序记忆内容
4. 单元测试：使用计数递增、排序正确

**修改文件：**
- `src/BotSharp/Application/ContextBuilder.fs`

### Phase 5：用户界面暴露（预计 1-2 天）

**目标**：将新能力暴露给用户。

1. 实现 `/sessions` 命令：列出历史会话
2. 实现 `/search <keyword>` 命令：搜索会话
3. 实现 `/history` 命令增强：显示整合时间线
4. 实现 `/rebuild-index` 命令：手动重建
5. 实现 `/stats` 命令：显示会话和记忆统计

## 12. 测试策略

### 单元测试

```fsharp
module StateDbTests =

    [<Fact>]
    let ``syncSession roundtrips correctly`` () = ...

    [<Fact>]
    let ``rebuildIndex matches filesystem state`` () = ...

    [<Fact>]
    let ``SQLite failure does not block JSONL persist`` () = ...

    [<Fact>]
    let ``listIdleSessionsForCompaction filters correctly`` () = ...

    [<Fact>]
    let ``recordMemoryUsage increments count`` () = ...

    [<Fact>]
    let ``migrate from v0 to current succeeds`` () = ...

    [<Fact>]
    let ``rebuildIndex is idempotent`` () = ...
```

### 集成测试

```fsharp
module HybridStorageIntegrationTests =

    [<Fact>]
    let ``full session lifecycle: create → append → consolidate → archive → cleanup`` () = ...

    [<Fact>]
    let ``crash between JSONL write and SQLite sync recovers on restart`` () = ...

    [<Fact>]
    let ``disable SQLite gracefully degrades to file-only`` () = ...
```

### 一致性验证

在 CI 中添加一个检查：

```fsharp
/// 验证 SQLite 索引与文件系统的一致性
let verifyConsistency (conn: SqliteConnection) (workspacePath: string) : ConsistencyReport =
    // 1. 比较 sessions 表行数 vs sessions/*.jsonl 文件数
    // 2. 对每个 JSONL 文件，比较 message_count 与 sessions 表
    // 3. 比较 consolidation_entries 行数 vs dreams.jsonl 行数
    // 报告差异（不自动修复）
```

## 13. 不采纳的 Codex 机制（及原因）

| Codex 机制 | 不采纳原因 |
|-----------|-----------|
| 分布式作业队列（lease_until, ownership_token） | BotSharp 单进程，MailboxProcessor 已序列化并发 |
| 两阶段记忆流水线（Phase 1 + Phase 2） | BotSharp 单阶段 save_memory 在个人规模下已足够 |
| 独立日志数据库 | 个人 Agent 日志量小，不需要分离 |
| 线程生成图（thread_spawn_edges） | BotSharp 无多 Agent 层级 |
| Keyset 分页游标 | 个人 Agent 数据量小，OFFSET 分页足够 |
| 替换历史检查点（CompactionCheckpoint） | BotSharp 的 MEMORY.md 覆盖写入模式更简单 |
| 模型分级（轻量模型提取 + 强模型整合） | 增加配置复杂度，可在未来按需引入 |
