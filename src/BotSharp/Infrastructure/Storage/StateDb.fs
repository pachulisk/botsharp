module BotSharp.Infrastructure.Storage.StateDb

#nowarn "3261"

open System
open System.IO
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// StateDb — SQLite derived index for BotSharp sessions and memory
//
// Design: JSONL is source of truth; SQLite is a derived cache.
// All writes go to JSONL first, then best-effort sync to SQLite.
// SQLite can be deleted and rebuilt from JSONL at any time.
//
// Schema: sessions, consolidation_entries, memory_usage
// Mode: WAL (concurrent read/write safe)
// ═══════════════════════════════════════════════════════════════════════════

// ── Types ────────────────────────────────────────────────────────────────

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
    ConsolidationCount   : int
    TotalConsolidatedMsgs: int
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

// ── Helpers ──────────────────────────────────────────────────────────────

let private toUnixMs (dto: DateTimeOffset) : int64 = dto.ToUnixTimeMilliseconds()
let private fromUnixMs (ms: int64) : DateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(ms)

let private execute (conn: SqliteConnection) (sql: string) : Async<unit> =
    async {
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore
    }

let private executeParam (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) : Async<unit> =
    async {
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        for (name, value) in ps do
            cmd.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    }

let queryScalarInt (conn: SqliteConnection) (sql: string) : int =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    match cmd.ExecuteScalar() with
    | :? int64 as v -> int v
    | :? int as v -> v
    | _ -> 0

let private bestEffort (label: string) (op: Async<unit>) : Async<unit> =
    async {
        try do! op
        with ex -> eprintfn "[StateDb] %s failed (non-fatal): %s" label ex.Message
    }

// ── Migration ────────────────────────────────────────────────────────────

let private CURRENT_VERSION = 5

let private migrationV1 = """
CREATE TABLE IF NOT EXISTS sessions (
    id                TEXT PRIMARY KEY,
    channel           TEXT NOT NULL,
    chat_id           TEXT,
    created_at        INTEGER NOT NULL,
    updated_at        INTEGER NOT NULL,
    message_count     INTEGER NOT NULL DEFAULT 0,
    last_consolidated INTEGER NOT NULL DEFAULT 0,
    first_user_message TEXT,
    title             TEXT,
    archived_at       INTEGER
);
CREATE INDEX IF NOT EXISTS idx_sessions_updated_at ON sessions(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_channel ON sessions(channel, updated_at DESC);

CREATE TABLE IF NOT EXISTS consolidation_entries (
    sha               TEXT PRIMARY KEY,
    session_id        TEXT,
    occurred_at       INTEGER NOT NULL,
    summary           TEXT NOT NULL,
    message_count     INTEGER NOT NULL,
    generated_at      INTEGER NOT NULL,
    model_used        TEXT,
    status            TEXT NOT NULL DEFAULT 'completed',
    error_message     TEXT
);
CREATE INDEX IF NOT EXISTS idx_consolidation_time ON consolidation_entries(occurred_at DESC);

CREATE TABLE IF NOT EXISTS memory_usage (
    memory_key        TEXT PRIMARY KEY,
    source_session_id TEXT,
    usage_count       INTEGER NOT NULL DEFAULT 0,
    last_usage        INTEGER,
    created_at        INTEGER NOT NULL,
    summary           TEXT
);

PRAGMA user_version = 1;
"""

let private migrationV2 = """
CREATE TABLE IF NOT EXISTS jobs (
    kind                    TEXT NOT NULL,
    job_key                 TEXT NOT NULL,
    status                  TEXT NOT NULL,
    worker_id               TEXT,
    ownership_token         TEXT,
    started_at              INTEGER,
    finished_at             INTEGER,
    lease_until             INTEGER,
    retry_at                INTEGER,
    retry_remaining         INTEGER NOT NULL,
    last_error              TEXT,
    input_watermark         INTEGER,
    last_success_watermark  INTEGER,
    created_at              INTEGER NOT NULL,
    updated_at              INTEGER NOT NULL,
    PRIMARY KEY (kind, job_key)
);

CREATE INDEX IF NOT EXISTS idx_jobs_kind_status_retry_lease
    ON jobs(kind, status, retry_at, lease_until);

PRAGMA user_version = 2;
"""

let private migrationV3 = """
CREATE TABLE IF NOT EXISTS stage1_outputs (
    session_id                              TEXT PRIMARY KEY,
    source_updated_at                       INTEGER NOT NULL,
    raw_memory                              TEXT NOT NULL,
    rollout_summary                         TEXT NOT NULL,
    rollout_slug                            TEXT,
    generated_at                            INTEGER NOT NULL,
    cwd                                     TEXT,
    channel                                 TEXT,
    usage_count                             INTEGER DEFAULT 0,
    last_usage                              INTEGER,
    selected_for_phase2                     INTEGER NOT NULL DEFAULT 0,
    selected_for_phase2_source_updated_at   INTEGER,
    FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_stage1_outputs_source_updated_at
    ON stage1_outputs(source_updated_at DESC, session_id DESC);

PRAGMA user_version = 3;
"""

let private migrationV4 = """
CREATE TABLE IF NOT EXISTS tasks (
    id              TEXT PRIMARY KEY,
    session_id      TEXT,
    subject         TEXT NOT NULL,
    description     TEXT,
    status          TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL,
    completed_at    INTEGER,
    created_by      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status, updated_at DESC);

PRAGMA user_version = 4;
"""

let private migrationV5 = """
CREATE TABLE IF NOT EXISTS event_log (
    id          TEXT PRIMARY KEY,
    timestamp   INTEGER NOT NULL,
    category    TEXT NOT NULL,
    kind        TEXT NOT NULL,
    session_id  TEXT,
    data        TEXT,
    created_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_event_log_kind ON event_log(kind, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_session ON event_log(session_id, timestamp DESC);

PRAGMA user_version = 5;
"""

let private configureSqlite (conn: SqliteConnection) : unit =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;"
    cmd.ExecuteNonQuery() |> ignore

let private migrate (conn: SqliteConnection) : unit =
    let currentVersion = queryScalarInt conn "PRAGMA user_version"
    if currentVersion < 1 then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationV1
        cmd.ExecuteNonQuery() |> ignore
        eprintfn "[StateDb] Migrated to schema v1"
    if currentVersion < 2 then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationV2
        cmd.ExecuteNonQuery() |> ignore
        eprintfn "[StateDb] Migrated to schema v2 (jobs table)"
    if currentVersion < 3 then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationV3
        cmd.ExecuteNonQuery() |> ignore
        eprintfn "[StateDb] Migrated to schema v3 (stage1_outputs table)"
    if currentVersion < 4 then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationV4
        cmd.ExecuteNonQuery() |> ignore
        eprintfn "[StateDb] Migrated to schema v4 (tasks table)"
    if currentVersion < 5 then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationV5
        cmd.ExecuteNonQuery() |> ignore
        eprintfn "[StateDb] Migrated to schema v5 (event_log table)"

// ── Init ─────────────────────────────────────────────────────────────────

/// Initialize the SQLite database. Returns a connection factory.
let init (workspacePath: string) : Async<(unit -> SqliteConnection)> =
    async {
        let dbPath = Path.Combine(workspacePath, "botsharp.sqlite")
        let connStr = $"Data Source={dbPath}"

        // Ensure directory exists
        Directory.CreateDirectory(workspacePath) |> ignore

        // Open, configure, migrate
        let testConn = new SqliteConnection(connStr)
        testConn.Open()
        configureSqlite testConn
        migrate testConn
        testConn.Close()
        testConn.Dispose()

        eprintfn "[StateDb] Initialized at %s" dbPath

        // Return factory that creates new connections
        let factory () =
            let conn = new SqliteConnection(connStr)
            conn.Open()
            conn
        return factory
    }

// ── Session sync (write path) ────────────────────────────────────────────

/// Extract channel and chatId from SessionId (e.g., "telegram:123" → "telegram", "123")
let private parseSessionId (SessionId sid) : string * string option =
    match sid.IndexOf(':') with
    | -1 -> (sid, None)
    | i  -> (sid.[..i-1], Some sid.[i+1..])

/// Extract first user message from session messages (truncated to 200 chars)
let private extractFirstUserMessage (messages: Message list) : string option =
    messages
    |> List.tryPick (function UserMessage (text, _) when text.Trim() <> "" -> Some text | _ -> None)
    |> Option.map (fun t -> if t.Length > 200 then t.[..199] else t)

/// Sync a SessionSnapshot to the sessions table.
let syncSession (conn: SqliteConnection) (snapshot: SessionSnapshot) : Async<unit> =
    async {
        let (SessionId sid) = SessionSnapshot.id snapshot
        let channel, chatId = parseSessionId (SessionSnapshot.id snapshot)
        let msgs = SessionSnapshot.messages snapshot
        let firstMsg = extractFirstUserMessage msgs
        let sql = """
            INSERT OR REPLACE INTO sessions (id, channel, chat_id, created_at, updated_at, message_count, last_consolidated, first_user_message)
            VALUES (@id, @channel, @chatId, @createdAt, @updatedAt, @msgCount, @lastConsolidated, @firstMsg)
        """
        do! executeParam conn sql [
            "@id", box sid
            "@channel", box channel
            "@chatId", (match chatId with Some c -> box c | None -> box DBNull.Value)
            "@createdAt", box (toUnixMs (SessionSnapshot.createdAt snapshot))
            "@updatedAt", box (toUnixMs (SessionSnapshot.updatedAt snapshot))
            "@msgCount", box (SessionSnapshot.messageCount snapshot)
            "@lastConsolidated", box (SessionSnapshot.lastConsolidated snapshot)
            "@firstMsg", (match firstMsg with Some m -> box m | None -> box DBNull.Value)
        ]
    }

/// Delete a session index entry.
let deleteSessionIndex (conn: SqliteConnection) (sessionId: SessionId) : Async<unit> =
    async {
        let (SessionId sid) = sessionId
        do! executeParam conn "DELETE FROM sessions WHERE id = @id" [ "@id", box sid ]
    }

// ── Consolidation sync ───────────────────────────────────────────────────

/// Sync a consolidation/dream entry.
let syncConsolidationEntry
    (conn      : SqliteConnection)
    (sessionId : SessionId option)
    (entry     : DreamEntry)
    (modelUsed : string option)
    : Async<unit> =
    async {
        let sql = """
            INSERT OR REPLACE INTO consolidation_entries (sha, session_id, occurred_at, summary, message_count, generated_at, model_used, status)
            VALUES (@sha, @sid, @occurredAt, @summary, @msgCount, @generatedAt, @model, 'completed')
        """
        do! executeParam conn sql [
            "@sha", box entry.Sha
            "@sid", (match sessionId with Some (SessionId s) -> box s | None -> box DBNull.Value)
            "@occurredAt", box (toUnixMs entry.OccurredAt)
            "@summary", box entry.Summary
            "@msgCount", box entry.MessageCount
            "@generatedAt", box (toUnixMs DateTimeOffset.UtcNow)
            "@model", (match modelUsed with Some m -> box m | None -> box DBNull.Value)
        ]
    }

// ── Memory usage tracking ────────────────────────────────────────────────

/// Record a memory usage event (increment count + update last_usage).
let recordMemoryUsage (conn: SqliteConnection) (memoryKey: string) : Async<unit> =
    async {
        let now = toUnixMs DateTimeOffset.UtcNow
        let sql = """
            INSERT INTO memory_usage (memory_key, usage_count, last_usage, created_at)
            VALUES (@key, 1, @now, @now)
            ON CONFLICT(memory_key) DO UPDATE SET
                usage_count = usage_count + 1,
                last_usage = @now
        """
        do! executeParam conn sql [ "@key", box memoryKey; "@now", box now ]
    }

// ── Queries (read path) ──────────────────────────────────────────────────

/// List sessions with pagination.
let listSessions
    (conn     : SqliteConnection)
    (page     : int)
    (pageSize : int)
    (channel  : string option)
    : Async<SessionIndexEntry list> =
    async {
        let offset = page * pageSize
        let whereClause = match channel with Some ch -> $"WHERE channel = '{ch}'" | None -> ""
        let sql = $"SELECT id, channel, chat_id, created_at, updated_at, message_count, last_consolidated, first_user_message, title, archived_at FROM sessions {whereClause} ORDER BY updated_at DESC LIMIT {pageSize} OFFSET {offset}"
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        use reader = cmd.ExecuteReader()
        let results = System.Collections.Generic.List<SessionIndexEntry>()
        while reader.Read() do
            results.Add({
                Id               = SessionId (reader.GetString(0))
                Channel          = reader.GetString(1)
                ChatId           = if reader.IsDBNull(2) then None else Some (reader.GetString(2))
                CreatedAt        = fromUnixMs (reader.GetInt64(3))
                UpdatedAt        = fromUnixMs (reader.GetInt64(4))
                MessageCount     = reader.GetInt32(5)
                LastConsolidated = reader.GetInt32(6)
                FirstUserMessage = if reader.IsDBNull(7) then None else Some (reader.GetString(7))
                Title            = if reader.IsDBNull(8) then None else Some (reader.GetString(8))
                ArchivedAt       = if reader.IsDBNull(9) then None else Some (fromUnixMs (reader.GetInt64(9)))
            })
        return List.ofSeq results
    }

/// Search sessions by keyword in first_user_message or title.
let searchSessions (conn: SqliteConnection) (query: string) (limit: int) : Async<SessionIndexEntry list> =
    async {
        let sql = $"SELECT id, channel, chat_id, created_at, updated_at, message_count, last_consolidated, first_user_message, title, archived_at FROM sessions WHERE first_user_message LIKE @q OR title LIKE @q ORDER BY updated_at DESC LIMIT {limit}"
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.Parameters.AddWithValue("@q", $"%%{query}%%") |> ignore
        use reader = cmd.ExecuteReader()
        let results = System.Collections.Generic.List<SessionIndexEntry>()
        while reader.Read() do
            results.Add({
                Id               = SessionId (reader.GetString(0))
                Channel          = reader.GetString(1)
                ChatId           = if reader.IsDBNull(2) then None else Some (reader.GetString(2))
                CreatedAt        = fromUnixMs (reader.GetInt64(3))
                UpdatedAt        = fromUnixMs (reader.GetInt64(4))
                MessageCount     = reader.GetInt32(5)
                LastConsolidated = reader.GetInt32(6)
                FirstUserMessage = if reader.IsDBNull(7) then None else Some (reader.GetString(7))
                Title            = if reader.IsDBNull(8) then None else Some (reader.GetString(8))
                ArchivedAt       = if reader.IsDBNull(9) then None else Some (fromUnixMs (reader.GetInt64(9)))
            })
        return List.ofSeq results
    }

/// Get session stats.
let getSessionStats (conn: SqliteConnection) (sessionId: SessionId) : Async<SessionStats option> =
    async {
        let (SessionId sid) = sessionId
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT message_count, last_consolidated FROM sessions WHERE id = @id"
        cmd.Parameters.AddWithValue("@id", sid) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            let mc = reader.GetInt32(0)
            let lc = reader.GetInt32(1)
            // Count consolidation entries for this session
            use cmd2 = conn.CreateCommand()
            cmd2.CommandText <- "SELECT COUNT(*), COALESCE(SUM(message_count), 0) FROM consolidation_entries WHERE session_id = @id"
            cmd2.Parameters.AddWithValue("@id", sid) |> ignore
            use reader2 = cmd2.ExecuteReader()
            let cc, tcm = if reader2.Read() then (reader2.GetInt32(0), reader2.GetInt32(1)) else (0, 0)
            return Some { MessageCount = mc; LastConsolidated = lc; UnconsolidatedCount = mc - lc; ConsolidationCount = cc; TotalConsolidatedMsgs = tcm }
        else return None
    }

/// List stale sessions for cleanup (idle > staleDays).
let listStaleSessionsForCleanup (conn: SqliteConnection) (staleDays: int) (limit: int) : Async<SessionIndexEntry list> =
    async {
        let cutoff = toUnixMs (DateTimeOffset.UtcNow.AddDays(- float staleDays))
        let sql = $"SELECT id, channel, chat_id, created_at, updated_at, message_count, last_consolidated, first_user_message, title, archived_at FROM sessions WHERE updated_at < @cutoff ORDER BY updated_at ASC LIMIT {limit}"
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.Parameters.AddWithValue("@cutoff", cutoff) |> ignore
        use reader = cmd.ExecuteReader()
        let results = System.Collections.Generic.List<SessionIndexEntry>()
        while reader.Read() do
            results.Add({
                Id               = SessionId (reader.GetString(0))
                Channel          = reader.GetString(1)
                ChatId           = if reader.IsDBNull(2) then None else Some (reader.GetString(2))
                CreatedAt        = fromUnixMs (reader.GetInt64(3))
                UpdatedAt        = fromUnixMs (reader.GetInt64(4))
                MessageCount     = reader.GetInt32(5)
                LastConsolidated = reader.GetInt32(6)
                FirstUserMessage = if reader.IsDBNull(7) then None else Some (reader.GetString(7))
                Title            = if reader.IsDBNull(8) then None else Some (reader.GetString(8))
                ArchivedAt       = if reader.IsDBNull(9) then None else Some (fromUnixMs (reader.GetInt64(9)))
            })
        return List.ofSeq results
    }

/// List sessions eligible for background compaction:
///   - idle > ttlMinutes
///   - unconsolidated messages >= memoryWindowSize
///   - not in the active session set
let listIdleSessionsForCompaction
    (conn: SqliteConnection)
    (ttlMinutes: int)
    (memoryWindowSize: int)
    (activeSids: Set<SessionId>)
    (limit: int)
    : Async<SessionIndexEntry list> =
    async {
        let cutoff = toUnixMs (DateTimeOffset.UtcNow.AddMinutes(- float ttlMinutes))
        let sql = $"SELECT id, channel, chat_id, created_at, updated_at, message_count, last_consolidated, first_user_message, title, archived_at FROM sessions WHERE updated_at < @cutoff AND (message_count - last_consolidated) >= @windowSize ORDER BY updated_at ASC LIMIT {limit}"
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.Parameters.AddWithValue("@cutoff", cutoff) |> ignore
        cmd.Parameters.AddWithValue("@windowSize", memoryWindowSize) |> ignore
        use reader = cmd.ExecuteReader()
        let results = System.Collections.Generic.List<SessionIndexEntry>()
        while reader.Read() do
            let entry = {
                Id               = SessionId (reader.GetString(0))
                Channel          = reader.GetString(1)
                ChatId           = if reader.IsDBNull(2) then None else Some (reader.GetString(2))
                CreatedAt        = fromUnixMs (reader.GetInt64(3))
                UpdatedAt        = fromUnixMs (reader.GetInt64(4))
                MessageCount     = reader.GetInt32(5)
                LastConsolidated = reader.GetInt32(6)
                FirstUserMessage = if reader.IsDBNull(7) then None else Some (reader.GetString(7))
                Title            = if reader.IsDBNull(8) then None else Some (reader.GetString(8))
                ArchivedAt       = if reader.IsDBNull(9) then None else Some (fromUnixMs (reader.GetInt64(9)))
            }
            // Filter out active sessions (in-memory MailboxProcessor actors)
            if not (Set.contains entry.Id activeSids) then
                results.Add(entry)
        return List.ofSeq results
    }

// ── Rebuild index (safety net) ───────────────────────────────────────────

/// Rebuild the entire SQLite index from JSONL files.
let rebuildIndex (workspacePath: string) (conn: SqliteConnection) : Async<RebuildResult> =
    async {
        let mutable sessionsIndexed = 0
        let mutable consolidationsIndexed = 0
        let mutable errors = []

        // Clear existing index
        do! execute conn "DELETE FROM memory_usage"
        do! execute conn "DELETE FROM consolidation_entries"
        do! execute conn "DELETE FROM sessions"

        // Scan sessions/*.jsonl
        let sessionsDir = Path.Combine(workspacePath, "sessions")
        if Directory.Exists sessionsDir then
            let files = Directory.GetFiles(sessionsDir, "*.jsonl")
            for file in files do
                try
                    let safeName = Path.GetFileNameWithoutExtension(file)
                    let sid = SessionId safeName
                    let lines = File.ReadAllLines(file)
                    match BotSharp.Infrastructure.Storage.SessionParser.parseSessionFile sid (Array.toSeq lines) with
                    | Ok snapshot ->
                        do! syncSession conn snapshot
                        sessionsIndexed <- sessionsIndexed + 1
                    | Error _ ->
                        errors <- $"Parse error in {file}" :: errors
                with ex ->
                    errors <- $"Error processing {file}: {ex.Message}" :: errors

        // Scan dreams.jsonl
        let dreamFile = Path.Combine(workspacePath, "dreams.jsonl")
        if File.Exists dreamFile then
            let lines = File.ReadAllLines(dreamFile)
            for line in lines do
                try
                    match BotSharp.Infrastructure.Storage.DreamStore.parseDreamLine line with
                    | Some entry ->
                        do! syncConsolidationEntry conn None entry None
                        consolidationsIndexed <- consolidationsIndexed + 1
                    | None -> ()
                with ex ->
                    errors <- $"Dream parse error: {ex.Message}" :: errors

        eprintfn "[StateDb] Rebuild complete: %d sessions, %d consolidations, %d errors"
            sessionsIndexed consolidationsIndexed errors.Length

        return { SessionsIndexed = sessionsIndexed; ConsolidationsIndexed = consolidationsIndexed; Errors = errors }
    }

// ── Stage 1 outputs (two-phase memory) ──────────────────────────────────

/// Upsert a Phase 1 extraction output. Only overwrites if source is newer.
let upsertStage1Output (conn: SqliteConnection) (output: Stage1Output) : Async<unit> =
    async {
        let sql =
            "INSERT INTO stage1_outputs (" +
            "session_id, source_updated_at, raw_memory, rollout_summary, " +
            "rollout_slug, generated_at, cwd, channel" +
            ") VALUES (@sid, @srcUpd, @rawMem, @summary, @slug, @genAt, @cwd, @ch) " +
            "ON CONFLICT(session_id) DO UPDATE SET " +
            "source_updated_at = excluded.source_updated_at, " +
            "raw_memory = excluded.raw_memory, " +
            "rollout_summary = excluded.rollout_summary, " +
            "rollout_slug = excluded.rollout_slug, " +
            "generated_at = excluded.generated_at " +
            "WHERE excluded.source_updated_at >= stage1_outputs.source_updated_at"
        do! executeParam conn sql [
            "@sid", box output.SessionId
            "@srcUpd", box output.SourceUpdatedAt
            "@rawMem", box output.RawMemory
            "@summary", box output.RolloutSummary
            "@slug", (match output.RolloutSlug with Some s -> box s | None -> box DBNull.Value)
            "@genAt", box output.GeneratedAt
            "@cwd", (match output.Cwd with Some s -> box s | None -> box DBNull.Value)
            "@ch", (match output.Channel with Some s -> box s | None -> box DBNull.Value)
        ]
    }

/// Select top-N stage1_outputs for Phase 2 input, ranked by usage_count and recency.
/// Codex get_phase2_input_selection (memories.rs:347-413).
let getPhase2InputSelection (conn: SqliteConnection) (maxCount: int) (maxUnusedDays: int) : Async<Stage1Output list> =
    async {
        let cutoff = toUnixMs (DateTimeOffset.UtcNow.AddDays(float -maxUnusedDays))
        let sql =
            "SELECT session_id, source_updated_at, raw_memory, rollout_summary, " +
            "rollout_slug, generated_at, cwd, channel, usage_count, last_usage, " +
            "selected_for_phase2, selected_for_phase2_source_updated_at " +
            "FROM stage1_outputs " +
            "WHERE (length(trim(raw_memory)) > 0 OR length(trim(rollout_summary)) > 0) " +
            "AND ((last_usage IS NOT NULL AND last_usage >= @cutoff) " +
            "OR (last_usage IS NULL AND source_updated_at >= @cutoff)) " +
            "ORDER BY COALESCE(usage_count, 0) DESC, " +
            "COALESCE(last_usage, source_updated_at) DESC, " +
            "source_updated_at DESC, session_id DESC " +
            "LIMIT @maxCount"
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.Parameters.AddWithValue("@cutoff", cutoff) |> ignore
        cmd.Parameters.AddWithValue("@maxCount", maxCount) |> ignore
        use reader = cmd.ExecuteReader()
        let results = System.Collections.Generic.List<Stage1Output>()
        while reader.Read() do
            results.Add({
                SessionId = reader.GetString(0)
                SourceUpdatedAt = reader.GetInt64(1)
                RawMemory = reader.GetString(2)
                RolloutSummary = reader.GetString(3)
                RolloutSlug = if reader.IsDBNull(4) then None else Some (reader.GetString(4))
                GeneratedAt = reader.GetInt64(5)
                Cwd = if reader.IsDBNull(6) then None else Some (reader.GetString(6))
                Channel = if reader.IsDBNull(7) then None else Some (reader.GetString(7))
                UsageCount = if reader.IsDBNull(8) then 0 else reader.GetInt32(8)
                LastUsage = if reader.IsDBNull(9) then None else Some (reader.GetInt64(9))
                SelectedForPhase2 = if reader.IsDBNull(10) then false else reader.GetInt32(10) <> 0
                SelectedForPhase2SourceUpdatedAt = if reader.IsDBNull(11) then None else Some (reader.GetInt64(11))
            })
        return List.ofSeq results
    }

/// Increment usage_count for stage1_outputs matching the given session IDs.
let recordStage1OutputUsage (conn: SqliteConnection) (sessionIds: string list) : Async<unit> =
    async {
        let now = toUnixMs DateTimeOffset.UtcNow
        for sid in sessionIds do
            let sql = "UPDATE stage1_outputs SET usage_count = usage_count + 1, last_usage = @now WHERE session_id = @sid"
            do! executeParam conn sql [ "@sid", box sid; "@now", box now ]
    }

/// Prune old stage1_outputs that haven't been used within maxUnusedDays.
let pruneStage1Outputs (conn: SqliteConnection) (maxUnusedDays: int) (batchSize: int) : Async<int> =
    async {
        let cutoff = toUnixMs (DateTimeOffset.UtcNow.AddDays(float -maxUnusedDays))
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "DELETE FROM stage1_outputs WHERE session_id IN (" +
            "SELECT session_id FROM stage1_outputs " +
            "WHERE (last_usage IS NOT NULL AND last_usage < @cutoff) " +
            "OR (last_usage IS NULL AND source_updated_at < @cutoff) " +
            "LIMIT @limit)"
        cmd.Parameters.AddWithValue("@cutoff", cutoff) |> ignore
        cmd.Parameters.AddWithValue("@limit", batchSize) |> ignore
        return cmd.ExecuteNonQuery()
    }

// ── Task management (dual: agent + user) ────────────────────────────────

/// Create a task. Returns the generated task ID.
let createTask (conn: SqliteConnection) (sessionId: string option) (subject: string) (description: string option) (createdBy: string) : Async<string> =
    async {
        let id = Guid.NewGuid().ToString("N").[..5]
        let now = toUnixMs DateTimeOffset.UtcNow
        let sql =
            "INSERT INTO tasks (id, session_id, subject, description, status, created_at, updated_at, created_by) " +
            "VALUES (@id, @sid, @subject, @desc, 'pending', @now, @now, @by)"
        do! executeParam conn sql [
            "@id", box id
            "@sid", (match sessionId with Some s -> box s | None -> box DBNull.Value)
            "@subject", box subject
            "@desc", (match description with Some d -> box d | None -> box DBNull.Value)
            "@now", box now
            "@by", box createdBy
        ]
        return id
    }

/// Update a task's status and/or subject.
let updateTask (conn: SqliteConnection) (id: string) (status: string option) (subject: string option) : Async<bool> =
    async {
        let now = toUnixMs DateTimeOffset.UtcNow
        let mutable sets = [ "updated_at = @now" ]
        let mutable ps : (string * obj) list = [ "@id", box id; "@now", box now ]
        match status with
        | Some s ->
            sets <- "status = @status" :: sets
            ps <- ("@status", box s) :: ps
            if s = "completed" then
                sets <- "completed_at = @now" :: sets
        | None -> ()
        match subject with
        | Some s ->
            sets <- "subject = @subject" :: sets
            ps <- ("@subject", box s) :: ps
        | None -> ()
        let sql = sprintf "UPDATE tasks SET %s WHERE id = @id" (String.concat ", " sets)
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        for (name, value) in ps do
            cmd.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value) |> ignore
        return cmd.ExecuteNonQuery() > 0
    }

/// List tasks, optionally filtered by status.
let listTasks (conn: SqliteConnection) (statusFilter: string option) (limit: int) : Async<TaskItem list> =
    async {
        let where = match statusFilter with Some s when s <> "all" -> sprintf " AND status = '%s'" s | _ -> ""
        let sql =
            sprintf "SELECT id, session_id, subject, description, status, created_at, updated_at, completed_at, created_by FROM tasks WHERE status != 'deleted'%s ORDER BY CASE status WHEN 'in_progress' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END, updated_at DESC LIMIT %d" where limit
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        use reader = cmd.ExecuteReader()
        let results = Collections.Generic.List<TaskItem>()
        while reader.Read() do
            results.Add({
                Id          = reader.GetString(0)
                SessionId   = if reader.IsDBNull(1) then None else Some (reader.GetString(1))
                Subject     = reader.GetString(2)
                Description = if reader.IsDBNull(3) then None else Some (reader.GetString(3))
                Status      = reader.GetString(4)
                CreatedAt   = reader.GetInt64(5)
                UpdatedAt   = reader.GetInt64(6)
                CompletedAt = if reader.IsDBNull(7) then None else Some (reader.GetInt64(7))
                CreatedBy   = reader.GetString(8)
            })
        return List.ofSeq results
    }

/// Get a single task by ID.
let getTask (conn: SqliteConnection) (id: string) : Async<TaskItem option> =
    async {
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, session_id, subject, description, status, created_at, updated_at, completed_at, created_by FROM tasks WHERE id = @id"
        cmd.Parameters.AddWithValue("@id", id) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            return Some {
                Id = reader.GetString(0)
                SessionId = if reader.IsDBNull(1) then None else Some (reader.GetString(1))
                Subject = reader.GetString(2)
                Description = if reader.IsDBNull(3) then None else Some (reader.GetString(3))
                Status = reader.GetString(4)
                CreatedAt = reader.GetInt64(5)
                UpdatedAt = reader.GetInt64(6)
                CompletedAt = if reader.IsDBNull(7) then None else Some (reader.GetInt64(7))
                CreatedBy = reader.GetString(8)
            }
        else return None
    }

/// Delete all completed tasks.
let clearCompletedTasks (conn: SqliteConnection) : Async<int> =
    async {
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM tasks WHERE status = 'completed'"
        return cmd.ExecuteNonQuery()
    }
