module BotSharp.Tests.Application.SessionCleanupServiceTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.SessionCleanupService
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// SessionCleanupService unit tests
//
// cleanupPass is tested directly (synchronous call, deterministic).
// Tests use real file-based SQLite via StateDb.init.
// ═══════════════════════════════════════════════════════════════════════════

/// Create a real file-based StateDb.
let private mkDb () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let factory = init tmp |> Async.RunSynchronously
    (factory, tmp)

/// Insert a session into the StateDb index via syncSession.
let private insertSession (openDb: unit -> SqliteConnection) (sid: SessionId) (updatedAt: DateTimeOffset) =
    let now = DateTimeOffset.UtcNow
    let msgs = [ UserMessage ("test", []) ]
    let snap =
        match SessionSnapshot.create sid msgs 0 now updatedAt with
        | Result.Ok s    -> s
        | Result.Error e -> failwith e
    use conn = openDb ()
    syncSession conn snap |> Async.RunSynchronously

// ── cleanupPass ──────────────────────────────────────────────────────────

[<Fact>]
let ``cleanupPass returns 0 deleted when no stale sessions`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        Directory.CreateDirectory(Path.Combine(wp, "sessions")) |> ignore
        let result = cleanupPass openDb wp 30 |> Async.RunSynchronously
        Assert.Equal(0, result.Deleted)
        Assert.Equal(0, result.Failed)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``cleanupPass skips sessions updated within cleanupDays`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        Directory.CreateDirectory(Path.Combine(wp, "sessions")) |> ignore
        // Session updated 1 hour ago, cleanupDays = 30
        let sid = SessionId "cli:recent-session"
        insertSession openDb sid (DateTimeOffset.UtcNow.AddHours(-1.0))
        let result = cleanupPass openDb wp 30 |> Async.RunSynchronously
        Assert.Equal(0, result.Deleted)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``cleanupPass deletes stale session from SQLite index`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        Directory.CreateDirectory(Path.Combine(wp, "sessions")) |> ignore
        // Session updated 31 days ago, cleanupDays = 30
        let sid = SessionId "cli:stale-session"
        insertSession openDb sid (DateTimeOffset.UtcNow.AddDays(-31.0))

        let result = cleanupPass openDb wp 30 |> Async.RunSynchronously
        Assert.Equal(1, result.Deleted)
        Assert.Equal(0, result.Failed)

        // Verify the session is gone from SQLite
        use conn = openDb ()
        let sessions = listSessions conn 0 100 None |> Async.RunSynchronously
        Assert.Equal(0, sessions.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``cleanupPass deletes the JSONL session file when it exists`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        let sessionsDir = Path.Combine(wp, "sessions")
        Directory.CreateDirectory(sessionsDir) |> ignore

        let sid = SessionId "cli:file-session"
        insertSession openDb sid (DateTimeOffset.UtcNow.AddDays(-31.0))

        // Create the JSONL file
        let filePath = Path.Combine(sessionsDir, "cli:file-session.jsonl")
        File.WriteAllText(filePath, "{\"msg\":\"hello\"}\n")
        Assert.True(File.Exists(filePath), "Setup: file should exist before cleanup")

        let _ = cleanupPass openDb wp 30 |> Async.RunSynchronously
        Assert.False(File.Exists(filePath), "File should be deleted after cleanup")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``cleanupPass only deletes sessions older than cleanupDays boundary`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        Directory.CreateDirectory(Path.Combine(wp, "sessions")) |> ignore

        // One session outside boundary (31 days), one inside (29 days)
        let staleSid  = SessionId "cli:stale-session"
        let recentSid = SessionId "cli:recent-session"
        insertSession openDb staleSid  (DateTimeOffset.UtcNow.AddDays(-31.0))
        insertSession openDb recentSid (DateTimeOffset.UtcNow.AddDays(-29.0))

        let result = cleanupPass openDb wp 30 |> Async.RunSynchronously
        Assert.Equal(1, result.Deleted)

        // recentSid should still be in the index
        use conn = openDb ()
        let sessions = listSessions conn 0 100 None |> Async.RunSynchronously
        Assert.Equal(1, sessions.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

// ── SessionCleanupService Start/Stop ─────────────────────────────────────

[<Fact>]
let ``SessionCleanupService Start/Stop does not crash`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        Directory.CreateDirectory(Path.Combine(wp, "sessions")) |> ignore
        let svc = SessionCleanupService(openDb, wp, 30)
        svc.Start()
        svc.Stop()
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``SessionCleanupService with cleanupDays=0 does not start background loop`` () =
    let openDb, tmp = mkDb ()
    try
        let wp = Path.Combine(tmp, "workspace")
        Directory.CreateDirectory(Path.Combine(wp, "sessions")) |> ignore
        let sid = SessionId "cli:old-session"
        insertSession openDb sid (DateTimeOffset.UtcNow.AddDays(-365.0))
        let svc = SessionCleanupService(openDb, wp, 0)
        svc.Start()
        System.Threading.Thread.Sleep(50)
        svc.Stop()
        // cleanupDays=0 disables the service; nothing should be deleted
        use conn = openDb ()
        let sessions = listSessions conn 0 100 None |> Async.RunSynchronously
        Assert.Equal(1, sessions.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()
