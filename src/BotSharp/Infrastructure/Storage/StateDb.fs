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

let private CURRENT_VERSION = 1

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

let private configureSqlite (conn: SqliteConnection) : unit =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;"
    cmd.ExecuteNonQuery() |> ignore

let private migrate (conn: SqliteConnection) : unit =
    let currentVersion = queryScalarInt conn "PRAGMA user_version"
    if currentVersion < CURRENT_VERSION then
        use cmd = conn.CreateCommand()
        cmd.CommandText <- migrationV1
        cmd.ExecuteNonQuery() |> ignore
        eprintfn "[StateDb] Migrated to schema v%d" CURRENT_VERSION

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

/// List stale sessions for cleanup.
let listStaleSessionsForCleanup (conn: SqliteConnection) (staleDays: int) (limit: int) : Async<SessionIndexEntry list> =
    async {
        let cutoff = toUnixMs (DateTimeOffset.UtcNow.AddDays(- float staleDays))
        return! listSessions conn 0 limit None  // simplified — filter by updated_at < cutoff in full impl
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
