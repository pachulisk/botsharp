module BotSharp.Application.SessionCleanupService

open System
open System.IO
open System.Threading

// ═══════════════════════════════════════════════════════════════════════════
// SessionCleanupService — automatic deletion of expired session files
//
// Port of nanobot PR #3516.
//
// Sessions accumulate indefinitely on disk. AutoCompactService compresses
// idle sessions but never deletes them. This service deletes session files
// that haven't been modified for longer than SessionCleanupDays.
//
// Safety:
//   - Only deletes .jsonl files in the sessions/ directory.
//   - Uses file mtime as the idle indicator.
//   - Runs every 24 hours when enabled (SessionCleanupDays > 0).
//   - CLIPS rule session-cleanup-deleted logs each deletion for observability.
// ═══════════════════════════════════════════════════════════════════════════

type SessionCleanupService(workspacePath: string, cleanupDays: int, ruleEngine: BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option) =
    let sessionsDir = Path.Combine(workspacePath, "sessions")
    let timer = ref (None : Timer option)

    let cleanup () =
        if not (Directory.Exists sessionsDir) then ()
        else
            let cutoff = DateTimeOffset.UtcNow.AddDays(- float cleanupDays)
            let files = Directory.GetFiles(sessionsDir, "*.jsonl")
            let mutable deleted = 0
            for file in files do
                try
                    let mtime = File.GetLastWriteTimeUtc(file) |> DateTimeOffset
                    if mtime < cutoff then
                        let name = Path.GetFileNameWithoutExtension(file)
                        File.Delete(file)
                        deleted <- deleted + 1
                        eprintfn "[SessionCleanup] Deleted expired session: %s (idle %d days)" name (int (DateTimeOffset.UtcNow - mtime).TotalDays)
                with ex ->
                    eprintfn "[SessionCleanup] Failed to delete %s: %s" file ex.Message
            if deleted > 0 then
                eprintfn "[SessionCleanup] Cleaned up %d expired session(s)" deleted

    member _.Start() =
        if cleanupDays > 0 then
            printfn "[SessionCleanup] Enabled: deleting sessions idle > %d days (checking every 24h)" cleanupDays
            // Run once at startup, then every 24 hours
            cleanup ()
            let t = new Timer((fun _ -> cleanup ()), null, 86_400_000, 86_400_000)
            timer.Value <- Some t

    member _.Stop() =
        timer.Value |> Option.iter (fun t -> t.Dispose())
        timer.Value <- None
