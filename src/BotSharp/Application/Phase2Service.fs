module BotSharp.Application.Phase2Service

open System.Threading
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.Phase2Consolidator

// ═══════════════════════════════════════════════════════════════════════════
// Phase2Service — background service for cross-session memory consolidation
//
// Runs Phase 2 at a configurable interval (default 30 minutes).
// Actual frequency is controlled by the 6-hour cooldown (Phase2CooldownHours)
// embedded in the job queue — the service just checks if it's time to run.
//
// Mirrors Codex's Phase 2 scheduling with global singleton job.
// ═══════════════════════════════════════════════════════════════════════════

type Phase2Service(
    openDb           : unit -> SqliteConnection,
    deps             : AgentDependencies,
    ?intervalMinutes : int) =

    let interval = defaultArg intervalMinutes 30
    let cts = new CancellationTokenSource()

    member _.Start() : unit =
        if not deps.Config.Phase2Enabled then
            eprintfn "[Phase2Service] Disabled by config"
        else
            let intervalMs = interval * 60 * 1000
            let rec loop () = async {
                try
                    let! result = runPhase2 openDb deps
                    match result with
                    | Phase2Succeeded n when n > 0 ->
                        eprintfn "[Phase2Service] Consolidated %d memories" n
                    | Phase2Skipped reason ->
                        ()  // Normal: cooldown, up-to-date, etc.
                    | Phase2Failed err ->
                        eprintfn "[Phase2Service] Failed: %s" err
                    | _ -> ()
                with ex ->
                    eprintfn "[Phase2Service] Pass failed: %s" ex.Message
                do! Async.Sleep intervalMs
                if not cts.Token.IsCancellationRequested then
                    return! loop ()
            }
            Async.Start(loop (), cancellationToken = cts.Token)

    member _.Stop() = cts.Cancel()
