module BotSharp.Application.Phase1Service

open System.Threading
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.Phase1Extractor

// ═══════════════════════════════════════════════════════════════════════════
// Phase1Service — background service for per-session memory extraction
//
// Runs Phase 1 extraction on idle sessions at a configurable interval.
// Uses the SQLite job queue for work tracking, retry, and concurrency control.
//
// Mirrors Codex's Phase 1 job scheduling (phase1.rs run_jobs).
// ═══════════════════════════════════════════════════════════════════════════

type Phase1Service(
    openDb           : unit -> SqliteConnection,
    deps             : AgentDependencies,
    getActiveSids    : unit -> Set<SessionId>,
    ?intervalMinutes : int) =

    let interval = defaultArg intervalMinutes 15
    let cts = new CancellationTokenSource()

    member _.Start() : unit =
        if deps.Config.MemoryWindowSize <= 0 then ()
        else
            BotSharp.Infrastructure.Memory.ModelRecommendation.logModelSelection deps.Config
            let intervalMs = interval * 60 * 1000
            let rec loop () = async {
                try
                    let! _ = runPhase1Pass openDb deps getActiveSids
                    ()
                with ex ->
                    eprintfn "[Phase1Service] Pass failed: %s" ex.Message
                do! Async.Sleep intervalMs
                if not cts.Token.IsCancellationRequested then
                    return! loop ()
            }
            Async.Start(loop (), cancellationToken = cts.Token)

    member _.Stop() = cts.Cancel()
