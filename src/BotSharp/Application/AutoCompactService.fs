module BotSharp.Application.AutoCompactService

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.MemoryConsolidator
open BotSharp.Infrastructure.Storage.JobQueue
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// AutoCompactService — proactive consolidation of idle sessions
//
// Uses the SQLite job queue for:
//   - Failure tracking with per-session error messages and retry counts
//   - Watermark-based change detection (skip sessions that haven't changed)
//   - Lease-based ownership (heartbeat renewal prevents reclaim)
//   - Retry with backoff (ConsolidationRetryDelayMs between attempts)
//   - Concurrent job limiting (DefaultMaxRunningJobs cap)
//   - Observability via /jobs command
//
// The service iterates candidates from the SQLite sessions index rather
// than scanning the filesystem. Each candidate is claimed via tryClaim;
// the actual consolidation runs with a background heartbeat to prevent
// lease expiry on slow LLM calls.
// ═══════════════════════════════════════════════════════════════════════════

/// One compaction pass: query candidates from SQLite, claim via job queue, consolidate.
let compactPass
    (deps          : AgentDependencies)
    (openDb        : unit -> SqliteConnection)
    (ttlMinutes    : int)
    (getActiveSids : unit -> Set<SessionId>)
    : Async<CompactPassResult> =
    async {
        let mutable processed = 0
        let mutable succeeded = 0
        let mutable skipped = 0
        let mutable failed = 0

        // Query candidates from SQLite index (idle + unconsolidated threshold)
        use conn = openDb ()
        let! candidates =
            listIdleSessionsForCompaction conn ttlMinutes
                deps.Config.MemoryWindowSize (getActiveSids ()) 50

        for entry in candidates do
            processed <- processed + 1
            let (SessionId sidStr) = entry.Id
            let watermark = sessionWatermark entry

            // Try to claim the consolidation job
            use conn2 = openDb ()
            let! outcome =
                tryClaim conn2 JobKind.Consolidation sidStr watermark
                    ConsolidationLeaseMs DefaultMaxRunningJobs

            match outcome with
            | SkippedUpToDate | SkippedRetryBackoff
            | SkippedRetryExhausted | SkippedRunning ->
                skipped <- skipped + 1

            | Claimed token ->
                // Start heartbeat (consolidation may take minutes with slow LLM)
                let heartbeatCts =
                    startHeartbeat openDb JobKind.Consolidation sidStr
                        token ConsolidationLeaseMs HeartbeatIntervalMs
                try
                    try
                        let! loadResult = deps.LoadSession entry.Id
                        match loadResult with
                        | Result.Error _ ->
                            use c = openDb ()
                            let! _ = markFailed c JobKind.Consolidation sidStr
                                         token "Failed to load session" ConsolidationRetryDelayMs
                            failed <- failed + 1
                        | Result.Ok snap ->
                            let unconsolidated =
                                SessionSnapshot.messageCount snap - SessionSnapshot.lastConsolidated snap
                            if unconsolidated < deps.Config.MemoryWindowSize then
                                // Below threshold after re-check — mark as done
                                use c = openDb ()
                                let! _ = markSucceeded c JobKind.Consolidation sidStr token
                                skipped <- skipped + 1
                            else
                                let! consolidationResult = consolidate snap deps
                                match consolidationResult with
                                | Result.Ok (Consolidated (_, _, newIndex)) ->
                                    match SessionSnapshot.advanceConsolidated newIndex snap with
                                    | Result.Ok newSnap ->
                                        let! _ = deps.PersistSession newSnap
                                        use c = openDb ()
                                        let! _ = markSucceeded c JobKind.Consolidation sidStr token
                                        succeeded <- succeeded + 1
                                        eprintfn "[AutoCompact] compacted %s (%d messages consolidated)" sidStr newIndex
                                    | Result.Error e ->
                                        use c = openDb ()
                                        let! _ = markFailed c JobKind.Consolidation sidStr token e ConsolidationRetryDelayMs
                                        failed <- failed + 1
                                | Result.Ok ConsolidationSkipped ->
                                    use c = openDb ()
                                    let! _ = markSucceeded c JobKind.Consolidation sidStr token
                                    skipped <- skipped + 1
                                | Result.Error e ->
                                    use c = openDb ()
                                    let! _ = markFailed c JobKind.Consolidation sidStr
                                                 token (sprintf "%A" e) ConsolidationRetryDelayMs
                                    failed <- failed + 1
                    with ex ->
                        try
                            use c = openDb ()
                            let! ok = markFailed c JobKind.Consolidation sidStr
                                          token ex.Message ConsolidationRetryDelayMs
                            if not ok then
                                // Ownership lost — attempt unowned recovery
                                use c2 = openDb ()
                                let! _ = markFailedIfUnowned c2 JobKind.Consolidation sidStr
                                             token ex.Message ConsolidationRetryDelayMs
                                ()
                        with _ -> ()
                        failed <- failed + 1
                finally
                    heartbeatCts.Cancel()
                    heartbeatCts.Dispose()

        if processed > 0 then
            eprintfn "[AutoCompact] Pass completed: %d processed, %d succeeded, %d skipped, %d failed"
                processed succeeded skipped failed

        return { Processed = processed; Succeeded = succeeded; Skipped = skipped; Failed = failed }
    }

/// Background service that periodically compacts idle sessions.
///
/// `openDb`            — SQLite connection factory.
/// `sessionTtlMinutes` — minimum idle time before a session is eligible.
///   Set to 0 to disable the service entirely.
/// `intervalMinutes`   — how often the compaction pass runs (default 15).
/// `getActiveSids`     — callback returning live session IDs to skip.
type AutoCompactService(
    deps             : AgentDependencies,
    openDb           : unit -> SqliteConnection,
    getActiveSids    : unit -> Set<SessionId>,
    sessionTtlMinutes: int,
    ?intervalMinutes : int) =

    let interval = defaultArg intervalMinutes 15
    let cts = new CancellationTokenSource()

    member _.Start() : unit =
        if sessionTtlMinutes <= 0 then ()   // feature disabled
        else
            let intervalMs = interval * 60 * 1000
            let rec loop () = async {
                try
                    let! result = compactPass deps openDb sessionTtlMinutes getActiveSids
                    ignore result
                with ex ->
                    eprintfn "[AutoCompact] Pass failed: %s" ex.Message
                do! Async.Sleep intervalMs
                if not cts.Token.IsCancellationRequested then
                    return! loop ()
            }
            Async.Start(loop (), cancellationToken = cts.Token)

    member _.Stop() = cts.Cancel()
