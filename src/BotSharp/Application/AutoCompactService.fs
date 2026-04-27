module BotSharp.Application.AutoCompactService

open System
open System.IO
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.AgentLoop
open BotSharp.Application.MemoryConsolidator

// ═══════════════════════════════════════════════════════════════════════════
// AutoCompactService — proactive consolidation of idle sessions
//
// Problem: sessions accumulate messages over time. When a user returns after
// hours away, the context window is stuffed with stale messages — making the
// first response slow and expensive.
//
// Solution: in the background, detect sessions that have been idle for more
// than `sessionTtlMinutes` and run MemoryConsolidator on them proactively.
// The next load of that session finds a compacted snapshot with a fresh
// MEMORY.md summary instead of the raw message backlog.
//
// Safety:
//   - Sessions whose IDs are returned by `getActiveSids` are skipped.
//     Callers typically pass coordinator.GetActiveSessionIds() to avoid
//     racing with live actors that are the authoritative writers.
//   - File mtime is used as a fast pre-filter before loading the full session.
//   - Any per-session failure is silently ignored (best-effort service).
//
// Layout on disk:
//   {workspacePath}/sessions/{safe-session-id}.jsonl
// ═══════════════════════════════════════════════════════════════════════════

let private sessionDir (workspacePath: string) : string =
    Path.Combine(workspacePath, "sessions")

/// Derive a SessionId from a session filename (strips ".jsonl").
let private sidFromFile (file: string) : SessionId =
    SessionId (Path.GetFileNameWithoutExtension(file) |> Unchecked.nonNull)

/// One compaction pass: scan session files, skip active/recent ones, compact the rest.
let private compactPass
    (deps          : AgentDependencies)
    (ttlMinutes    : int)
    (getActiveSids : unit -> Set<SessionId>)
    : Async<unit> =
    async {
        let dir = sessionDir deps.Config.WorkspacePath
        if not (Directory.Exists dir) then ()
        else
            let cutoff     = DateTimeOffset.UtcNow.AddMinutes(float -ttlMinutes)
            let activeSids = getActiveSids ()

            let candidates =
                try Directory.GetFiles(dir, "*.jsonl")
                with _ -> [||]

            for file in candidates do
                try
                    let sid = sidFromFile file
                    if Set.contains sid activeSids then ()   // live actor — skip
                    else
                        // Fast pre-filter: file mtime is updated each time the session is written.
                        let mtime = File.GetLastWriteTimeUtc(file) |> DateTimeOffset
                        if mtime > cutoff then ()   // recently active — skip
                        else
                            let! loadResult = deps.LoadSession sid
                            match loadResult with
                            | Result.Error _ -> ()
                            | Result.Ok snap ->
                                let unconsolidated =
                                    SessionSnapshot.messageCount snap - SessionSnapshot.lastConsolidated snap
                                if unconsolidated < deps.Config.MemoryWindowSize then ()
                                else
                                    let! result = consolidate snap deps
                                    match result with
                                    | Result.Error _ -> ()
                                    | Result.Ok ConsolidationSkipped -> ()
                                    | Result.Ok (Consolidated (_, _, newIdx)) ->
                                        match SessionSnapshot.advanceConsolidated newIdx snap with
                                        | Error _ -> ()
                                        | Ok compacted ->
                                            let! _ = deps.PersistSession compacted
                                            eprintfn "[AutoCompact] compacted %A (%d messages consolidated)" sid newIdx
                with _ -> ()   // per-session failure silently ignored
    }

/// Background service that periodically compacts idle sessions.
///
/// `sessionTtlMinutes` — minimum idle time before a session is eligible.
///   Set to 0 to disable the service entirely.
/// `intervalMinutes`   — how often the compaction pass runs (default 15).
/// `getActiveSids`     — callback returning live session IDs to skip.
///
/// Call `Start()` once to launch the background loop.
/// Call `Stop()` to cancel it on application shutdown.
type AutoCompactService(
    deps             : AgentDependencies,
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
                try do! compactPass deps sessionTtlMinutes getActiveSids
                with _ -> ()
                do! Async.Sleep intervalMs
                if not cts.Token.IsCancellationRequested then
                    return! loop ()
            }
            Async.Start(loop (), cancellationToken = cts.Token)

    member _.Stop() = cts.Cancel()
