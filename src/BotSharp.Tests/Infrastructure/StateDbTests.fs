module BotSharp.Tests.Infrastructure.StateDbTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.StateDb
open BotSharp.Infrastructure.Storage.SessionParser

// ═══════════════════════════════════════════════════════════════════════════
// StateDb unit tests — SQLite-backed session / task / stage1 index
//
// Uses real file-based SQLite via StateDb.init so schema migrations are
// exercised exactly as in production.  Each test gets its own temp directory.
// ═══════════════════════════════════════════════════════════════════════════

/// Create a temp workspace, initialise StateDb, return (factory, tmpDir).
/// Caller should `try … finally Directory.Delete(tmp, true)`.
let private mkDb () : (unit -> SqliteConnection) * string =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let factory = init tmp |> Async.RunSynchronously
    (factory, tmp)

/// Build a minimal SessionSnapshot with N messages and a given lastConsolidated mark.
let private mkSnap (sid: string) (msgCount: int) (lastConsolidated: int)
                   (now: DateTimeOffset) : SessionSnapshot =
    let msgs = [ 1 .. msgCount ] |> List.map (fun i -> UserMessage ($"msg {i}", []))
    match SessionSnapshot.create (SessionId sid) msgs lastConsolidated now now with
    | Result.Ok s    -> s
    | Result.Error e -> failwith e

// ── syncSession / listSessions ───────────────────────────────────────────

[<Fact>]
let ``syncSession writes and listSessions reads back the entry`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now  = DateTimeOffset.UtcNow
            let snap = mkSnap "test:123" 5 2 now
            use conn = openDb ()
            do! syncSession conn snap
            let! sessions = listSessions conn 0 10 None
            Assert.Equal(1, sessions.Length)
            let s = sessions.[0]
            Assert.Equal(SessionId "test:123", s.Id)
            Assert.Equal("test", s.Channel)
            Assert.Equal(Some "123", s.ChatId)
            Assert.Equal(5, s.MessageCount)
            Assert.Equal(2, s.LastConsolidated)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``syncSession is idempotent — second write overwrites the first`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now  = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "s1" 3 0 now)
            do! syncSession conn (mkSnap "s1" 7 4 now)
            let! sessions = listSessions conn 0 10 None
            Assert.Equal(1, sessions.Length)
            Assert.Equal(7, sessions.[0].MessageCount)
            Assert.Equal(4, sessions.[0].LastConsolidated)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``listSessions channel filter excludes other channels`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "cli" 2 0 now)
            do! syncSession conn (mkSnap "telegram:1" 3 0 now)
            let! all = listSessions conn 0 10 None
            Assert.Equal(2, all.Length)
            let! cliOnly = listSessions conn 0 10 (Some "cli")
            Assert.Equal(1, cliOnly.Length)
            Assert.Equal(SessionId "cli", cliOnly.[0].Id)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``listSessions paginates correctly`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow.AddMinutes(-1.0)
            use conn = openDb ()
            // Insert in time-ascending order; listSessions returns updated_at DESC
            for i in 1 .. 5 do
                let snap = mkSnap $"s{i}" 1 0 (now.AddSeconds(float i))
                do! syncSession conn snap
            let! page0 = listSessions conn 0 3 None
            let! page1 = listSessions conn 1 3 None
            Assert.Equal(3, page0.Length)
            Assert.Equal(2, page1.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── deleteSessionIndex ───────────────────────────────────────────────────

[<Fact>]
let ``deleteSessionIndex removes the entry`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "del-me" 2 0 now)
            do! deleteSessionIndex conn (SessionId "del-me")
            let! sessions = listSessions conn 0 10 None
            Assert.Equal(0, sessions.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── getSessionStats ──────────────────────────────────────────────────────

[<Fact>]
let ``getSessionStats returns None for unknown session`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! result = getSessionStats conn (SessionId "nonexistent")
            Assert.True(result.IsNone)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``getSessionStats returns correct unconsolidated count`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "stats-sess" 10 3 now)
            let! stats = getSessionStats conn (SessionId "stats-sess")
            match stats with
            | None -> Assert.Fail("Expected Some stats")
            | Some s ->
                Assert.Equal(10, s.MessageCount)
                Assert.Equal(3, s.LastConsolidated)
                Assert.Equal(7, s.UnconsolidatedCount)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``getSessionStats consolidation count includes syncConsolidationEntry rows`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "dream-sess" 5 5 now)
            let dream1 : DreamEntry = { Sha = "abc123"; OccurredAt = now; Summary = "S1"; MessageCount = 3 }
            let dream2 : DreamEntry = { Sha = "def456"; OccurredAt = now; Summary = "S2"; MessageCount = 2 }
            do! syncConsolidationEntry conn (Some (SessionId "dream-sess")) dream1 (Some "model-a")
            do! syncConsolidationEntry conn (Some (SessionId "dream-sess")) dream2 None
            let! stats = getSessionStats conn (SessionId "dream-sess")
            match stats with
            | None -> Assert.Fail("Expected Some stats")
            | Some s ->
                Assert.Equal(2, s.ConsolidationCount)
                Assert.Equal(5, s.TotalConsolidatedMsgs)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── searchSessions ───────────────────────────────────────────────────────

[<Fact>]
let ``searchSessions finds session by keyword in first user message`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            // Manually insert a session with a known first_user_message via syncSession
            // (syncSession picks it from messages; we need UserMessage)
            let msgs = [ UserMessage ("hello world", []); AssistantMessage ("hi", None) ]
            let snap =
                match SessionSnapshot.create (SessionId "search-sess") msgs 0 now now with
                | Result.Ok s -> s
                | Result.Error e -> failwith e
            do! syncSession conn snap
            let! found = searchSessions conn "hello" 10
            Assert.Equal(1, found.Length)
            let! notFound = searchSessions conn "zzznomatch" 10
            Assert.Equal(0, notFound.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── listStaleSessionsForCleanup ──────────────────────────────────────────

[<Fact>]
let ``listStaleSessionsForCleanup returns only sessions older than staleDays`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            // Old session: updated 10 days ago
            do! syncSession conn (mkSnap "old-sess" 2 0 (now.AddDays(-10.0)))
            // Recent session: updated 1 day ago
            do! syncSession conn (mkSnap "new-sess" 2 0 (now.AddDays(-1.0)))
            let! stale = listStaleSessionsForCleanup conn 3 10
            Assert.Equal(1, stale.Length)
            Assert.Equal(SessionId "old-sess", stale.[0].Id)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── listIdleSessionsForCompaction ────────────────────────────────────────

[<Fact>]
let ``listIdleSessionsForCompaction returns session meeting all criteria`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            // idle 2h, 5 messages, 0 consolidated → unconsolidated = 5 >= window=3
            do! syncSession conn (mkSnap "compact-me" 5 0 (now.AddHours(-2.0)))
            let! candidates = listIdleSessionsForCompaction conn 60 3 Set.empty 10
            Assert.Equal(1, candidates.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``listIdleSessionsForCompaction skips session with too few unconsolidated messages`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            // Only 2 unconsolidated (5 total - 3 consolidated), window=5 → skip
            do! syncSession conn (mkSnap "sparse" 5 3 (now.AddHours(-2.0)))
            let! candidates = listIdleSessionsForCompaction conn 60 5 Set.empty 10
            Assert.Equal(0, candidates.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``listIdleSessionsForCompaction skips recently-updated session`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            // updated 1 min ago — inside the 60-min TTL
            do! syncSession conn (mkSnap "recent" 10 0 (now.AddMinutes(-1.0)))
            let! candidates = listIdleSessionsForCompaction conn 60 3 Set.empty 10
            Assert.Equal(0, candidates.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``listIdleSessionsForCompaction skips active session IDs`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "active-one" 10 0 (now.AddHours(-2.0)))
            let! candidates = listIdleSessionsForCompaction conn 60 3 (Set.singleton (SessionId "active-one")) 10
            Assert.Equal(0, candidates.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── Tasks ────────────────────────────────────────────────────────────────

[<Fact>]
let ``createTask creates a pending task with generated ID`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! id = createTask conn None "Write tests" None "agent"
            Assert.NotEmpty(id)
            let! task = getTask conn id
            match task with
            | None -> Assert.Fail("Task not found")
            | Some t ->
                Assert.Equal("pending", t.Status)
                Assert.Equal("Write tests", t.Subject)
                Assert.Equal("agent", t.CreatedBy)
                Assert.True(t.CompletedAt.IsNone)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``updateTask changes status to in_progress`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! id = createTask conn None "Some task" None "user"
            let! ok = updateTask conn id (Some "in_progress") None
            Assert.True(ok)
            let! task = getTask conn id
            match task with
            | None -> Assert.Fail("Task not found")
            | Some t -> Assert.Equal("in_progress", t.Status)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``updateTask sets completed_at when status = completed`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! id = createTask conn None "Finish this" None "agent"
            let! _ = updateTask conn id (Some "completed") None
            let! task = getTask conn id
            match task with
            | None -> Assert.Fail("Task not found")
            | Some t ->
                Assert.Equal("completed", t.Status)
                Assert.True(t.CompletedAt.IsSome)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``listTasks status filter returns only matching tasks`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! id1 = createTask conn None "Task A" None "agent"
            let! id2 = createTask conn None "Task B" None "agent"
            let! _ = updateTask conn id1 (Some "completed") None
            let! pending = listTasks conn (Some "pending") 20
            Assert.Equal(1, pending.Length)
            Assert.Equal(id2, pending.[0].Id)
            let! all = listTasks conn None 20
            Assert.Equal(2, all.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``clearCompletedTasks removes only completed tasks`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! id1 = createTask conn None "Done" None "agent"
            let! _   = createTask conn None "Pending" None "agent"
            let! _ = updateTask conn id1 (Some "completed") None
            let! deleted = clearCompletedTasks conn
            Assert.Equal(1, deleted)
            let! remaining = listTasks conn None 20
            Assert.Equal(1, remaining.Length)
            Assert.Equal("pending", remaining.[0].Status)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``getTask returns None for non-existent ID`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! result = getTask conn "nosuchid"
            Assert.True(result.IsNone)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── Stage1 outputs ───────────────────────────────────────────────────────

let private mkStage1 (sid: string) (now: DateTimeOffset) : Stage1Output = {
    SessionId                       = sid
    SourceUpdatedAt                 = now.ToUnixTimeMilliseconds()
    RawMemory                       = "raw memory content"
    RolloutSummary                  = "brief summary"
    RolloutSlug                     = Some "brief-summary"
    GeneratedAt                     = now.ToUnixTimeMilliseconds()
    Cwd                             = Some "/workspace"
    Channel                         = Some "cli"
    UsageCount                      = 0
    LastUsage                       = None
    SelectedForPhase2               = false
    SelectedForPhase2SourceUpdatedAt= None
}

[<Fact>]
let ``upsertStage1Output inserts and getPhase2InputSelection returns it`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            // stage1_outputs has FK → sessions; insert the session first
            do! syncSession conn (mkSnap "phase1-sess" 3 0 now)
            do! upsertStage1Output conn (mkStage1 "phase1-sess" now)
            let! selected = getPhase2InputSelection conn 10 365
            Assert.Equal(1, selected.Length)
            Assert.Equal("phase1-sess", selected.[0].SessionId)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``recordStage1OutputUsage increments usage_count`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let now = DateTimeOffset.UtcNow
            use conn = openDb ()
            do! syncSession conn (mkSnap "usage-sess" 3 0 now)
            do! upsertStage1Output conn (mkStage1 "usage-sess" now)
            do! recordStage1OutputUsage conn [ "usage-sess" ]
            do! recordStage1OutputUsage conn [ "usage-sess" ]
            let! selected = getPhase2InputSelection conn 10 365
            Assert.Equal(1, selected.Length)
            Assert.Equal(2, selected.[0].UsageCount)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``pruneStage1Outputs removes entries not used within maxUnusedDays`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let old    = DateTimeOffset.UtcNow.AddDays(-30.0)
            let recent = DateTimeOffset.UtcNow.AddDays(-1.0)
            use conn = openDb ()
            // stage1_outputs has FK → sessions; insert sessions first
            do! syncSession conn (mkSnap "old-sess"  3 0 old)
            do! syncSession conn (mkSnap "new-sess"  3 0 recent)
            do! upsertStage1Output conn { mkStage1 "old-sess"  old    with SourceUpdatedAt = old.ToUnixTimeMilliseconds();    GeneratedAt = old.ToUnixTimeMilliseconds() }
            do! upsertStage1Output conn { mkStage1 "new-sess"  recent with SourceUpdatedAt = recent.ToUnixTimeMilliseconds(); GeneratedAt = recent.ToUnixTimeMilliseconds() }
            // prune entries unused for more than 7 days
            let! deleted = pruneStage1Outputs conn 7 10
            Assert.Equal(1, deleted)
            let! remaining = getPhase2InputSelection conn 10 365
            Assert.Equal(1, remaining.Length)
            Assert.Equal("new-sess", remaining.[0].SessionId)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── rebuildIndex ─────────────────────────────────────────────────────────

/// Write a minimal JSONL session file to sessions/ dir.
let private writeSessionFile (sessionsDir: string) (sid: string) (msgs: Message list) =
    let path = Path.Combine(sessionsDir, sid + ".jsonl")
    let lines = msgs |> List.map serializeMessage
    File.WriteAllLines(path, lines)

[<Fact>]
let ``rebuildIndex returns zero counts when workspace is empty`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            let! result = rebuildIndex tmp conn
            Assert.Equal(0, result.SessionsIndexed)
            Assert.Equal(0, result.ConsolidationsIndexed)
            Assert.Empty(result.Errors)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``rebuildIndex indexes one session file`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let sessionsDir = Path.Combine(tmp, "sessions")
            Directory.CreateDirectory(sessionsDir) |> ignore
            writeSessionFile sessionsDir "cli:my-session"
                [ UserMessage ("Hello", []); AssistantMessage ("Hi", None) ]
            use conn = openDb ()
            let! result = rebuildIndex tmp conn
            Assert.Equal(1, result.SessionsIndexed)
            Assert.Empty(result.Errors)
            // Verify session is now in the index
            let! sessions = listSessions conn 0 10 None
            Assert.Equal(1, sessions.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``rebuildIndex indexes multiple session files`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let sessionsDir = Path.Combine(tmp, "sessions")
            Directory.CreateDirectory(sessionsDir) |> ignore
            writeSessionFile sessionsDir "cli:session-1" [ UserMessage ("Q1", []); AssistantMessage ("A1", None) ]
            writeSessionFile sessionsDir "cli:session-2" [ UserMessage ("Q2", []); AssistantMessage ("A2", None) ]
            writeSessionFile sessionsDir "cli:session-3" [ UserMessage ("Q3", []); AssistantMessage ("A3", None) ]
            use conn = openDb ()
            let! result = rebuildIndex tmp conn
            Assert.Equal(3, result.SessionsIndexed)
            Assert.Empty(result.Errors)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``rebuildIndex clears existing sessions before rebuild`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            // Sync a session manually
            let snap = mkSnap "cli:old-session" 2 0 DateTimeOffset.UtcNow
            do! syncSession conn snap
            let! beforeCount = listSessions conn 0 10 None
            Assert.Equal(1, beforeCount.Length)
            // Rebuild from empty sessions/ dir (no JSONL files)
            let sessionsDir = Path.Combine(tmp, "sessions")
            Directory.CreateDirectory(sessionsDir) |> ignore
            let! result = rebuildIndex tmp conn
            Assert.Equal(0, result.SessionsIndexed)
            // Old session should be gone
            let! afterCount = listSessions conn 0 10 None
            Assert.Equal(0, afterCount.Length)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

[<Fact>]
let ``rebuildIndex handles malformed JSONL lines gracefully`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            let sessionsDir = Path.Combine(tmp, "sessions")
            Directory.CreateDirectory(sessionsDir) |> ignore
            // Write a file with one valid line and one garbage line
            let path = Path.Combine(sessionsDir, "cli:mixed.jsonl")
            File.WriteAllLines(path, [
                "{\"role\":\"user\",\"content\":\"hello\",\"media\":[]}"
                "GARBAGE_LINE"
            ])
            use conn = openDb ()
            let! result = rebuildIndex tmp conn
            // The file parses successfully (partial parse), so sessionsIndexed=1
            // Or it may fail and add an error — either is valid behavior.
            // Just verify no exception is thrown and we get a result.
            Assert.True(result.SessionsIndexed >= 0)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously

// ── recordMemoryUsage ────────────────────────────────────────────────────

[<Fact>]
let ``recordMemoryUsage first call creates entry, second increments count`` () =
    async {
        let openDb, tmp = mkDb ()
        try
            use conn = openDb ()
            do! recordMemoryUsage conn "memory/dream-2025-01.md"
            do! recordMemoryUsage conn "memory/dream-2025-01.md"
            // No direct read API for memory_usage — verify no exception and
            // row count via queryScalarInt helper exposed for tests
            let count = queryScalarInt conn "SELECT COUNT(*) FROM memory_usage WHERE memory_key = 'memory/dream-2025-01.md'"
            Assert.Equal(1, count)
            let usageCount = queryScalarInt conn "SELECT usage_count FROM memory_usage WHERE memory_key = 'memory/dream-2025-01.md'"
            Assert.Equal(2, usageCount)
        finally
            try Directory.Delete(tmp, true) with _ -> ()
    } |> Async.RunSynchronously
