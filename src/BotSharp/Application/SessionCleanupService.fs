module BotSharp.Application.SessionCleanupService

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.JobQueue
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// SessionCleanupService — automatic deletion of expired session files
//
// Port of nanobot PR #3516, enhanced with job queue tracking.
//
// Sessions accumulate indefinitely on disk. AutoCompactService compresses
// idle sessions but never deletes them. This service deletes session files
// that haven't been modified for longer than SessionCleanupDays.
//
// Uses the SQLite job queue for:
//   - Failure tracking (file deletion errors recorded with retry)
//   - Job removal on success (session is gone, no point keeping the job)
//   - Observability via /jobs command
// ═══════════════════════════════════════════════════════════════════════════

/// One cleanup pass: query stale sessions from SQLite, claim via job queue, delete.
let cleanupPass
    (openDb        : unit -> SqliteConnection)
    (workspacePath : string)
    (cleanupDays   : int)
    : Async<CleanupPassResult> =
    async {
        let mutable deleted = 0
        let mutable failed = 0

        use conn = openDb ()
        let! stale = listStaleSessionsForCleanup conn cleanupDays 100

        for entry in stale do
            let (SessionId sidStr) = entry.Id
            let cleanupWatermark = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

            use conn2 = openDb ()
            let! outcome =
                tryClaim conn2 JobKind.SessionCleanup sidStr
                    cleanupWatermark CleanupLeaseMs DefaultMaxRunningJobs

            match outcome with
            | Claimed token ->
                try
                    // Delete the session JSONL file
                    let sessionFile = Path.Combine(workspacePath, "sessions", sidStr + ".jsonl")
                    if File.Exists sessionFile then
                        File.Delete sessionFile

                    // Remove the SQLite session index entry
                    use c = openDb ()
                    do! deleteSessionIndex c entry.Id

                    // Remove the job record (session no longer exists)
                    use c2 = openDb ()
                    do! removeJob c2 JobKind.SessionCleanup sidStr

                    deleted <- deleted + 1
                    let idleDays = int (DateTimeOffset.UtcNow - entry.UpdatedAt).TotalDays
                    eprintfn "[SessionCleanup] Deleted expired session: %s (idle %d days)" sidStr idleDays
                with ex ->
                    use c = openDb ()
                    let! _ = markFailed c JobKind.SessionCleanup sidStr
                                 token ex.Message CleanupRetryDelayMs
                    failed <- failed + 1
                    eprintfn "[SessionCleanup] Failed to delete %s: %s" sidStr ex.Message
            | _ -> ()

        if deleted > 0 then
            eprintfn "[SessionCleanup] Cleaned up %d expired session(s)" deleted

        return { Deleted = deleted; Failed = failed }
    }

type SessionCleanupService(
    openDb        : unit -> SqliteConnection,
    workspacePath : string,
    cleanupDays   : int) =

    let cts = new CancellationTokenSource()

    member _.Start() =
        if cleanupDays > 0 then
            eprintfn "[SessionCleanup] Enabled: deleting sessions idle > %d days (checking every 24h)" cleanupDays
            let rec loop () = async {
                try
                    let! result = cleanupPass openDb workspacePath cleanupDays
                    ignore result
                with ex ->
                    eprintfn "[SessionCleanup] Pass failed: %s" ex.Message
                do! Async.Sleep 86_400_000   // 24 hours
                if not cts.Token.IsCancellationRequested then
                    return! loop ()
            }
            Async.Start(loop (), cancellationToken = cts.Token)

    member _.Stop() = cts.Cancel()
