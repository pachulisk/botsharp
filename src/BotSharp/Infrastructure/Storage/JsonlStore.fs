module BotSharp.Infrastructure.Storage.JsonlStore

open System
open System.IO
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.SessionParser
open BotSharp.Infrastructure.Shared.AsyncResult

// ═══════════════════════════════════════════════════════════════════════════
// JSONL session store
//
// Each session is persisted as:
//   {workspacePath}/sessions/{safe-session-id}.jsonl
//
// Lines in the file map 1:1 to Message values (see SessionParser for format).
// The store is append-friendly: loading reads all lines; saving rewrites the
// full file (sessions are typically short enough that full rewrite is fine).
// ═══════════════════════════════════════════════════════════════════════════

let private sessionDir (workspacePath: string) : string =
    Path.Combine(workspacePath, "sessions")

/// Replace characters that are invalid in filenames with underscores.
let private safeFileName (SessionId id) : string =
    let invalid = Path.GetInvalidFileNameChars() |> Set.ofArray
    id |> String.collect (fun c -> if Set.contains c invalid then "_" else string c)

let private sessionPath (workspacePath: string) (sid: SessionId) : string =
    Path.Combine(sessionDir workspacePath, safeFileName sid + ".jsonl")

// ── Load ─────────────────────────────────────────────────────────────────

/// Load a session from disk.  Returns an empty snapshot when the file does
/// not exist (first message in a new session).
let loadSession
    (sid           : SessionId)
    (workspacePath : string)
    : Async<Result<SessionSnapshot, StorageError>> =
    async {
        let path = sessionPath workspacePath sid
        if not (File.Exists path) then
            return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow)
        else
            try
                let! lines = File.ReadAllLinesAsync(path) |> Async.AwaitTask
                match parseSessionFile sid lines with
                | Result.Ok snap   -> return Result.Ok snap
                | Result.Error errs ->
                    let first = NonEmptyList.head errs
                    return Result.Error (ParseFailure first)
            with ex ->
                return Result.Error (WriteFailure ex.Message)
    }

// ── Persist ───────────────────────────────────────────────────────────────

/// Write the entire session to disk using an atomic write pattern.
/// Writes to a .tmp file first, then renames to the real path (crash-safe on POSIX).
/// Matches Python SessionManager.save() atomic behaviour.
let persistSession
    (snap          : SessionSnapshot)
    (workspacePath : string)
    : Async<Result<unit, StorageError>> =
    async {
        let path    = sessionPath workspacePath (SessionSnapshot.id snap)
        let tmpPath = path + ".tmp"
        try
            let dir =
                match Path.GetDirectoryName(path) with
                | null -> path
                | d    -> d
            if not (Directory.Exists dir) then
                Directory.CreateDirectory(dir) |> ignore
            let lines =
                SessionSnapshot.messages snap
                |> List.map serializeMessage
                |> Array.ofList
            // Write to .tmp, then atomically rename — crash during write leaves .tmp only
            do! File.WriteAllLinesAsync(tmpPath, lines) |> Async.AwaitTask
            File.Move(tmpPath, path, overwrite = true)
            return Result.Ok ()
        with ex ->
            // Best-effort cleanup of the temp file on failure
            try if File.Exists tmpPath then File.Delete tmpPath with _ -> ()
            return Result.Error (WriteFailure ex.Message)
    }

/// Delete a session file (used by /new command after clearing the snapshot).
let deleteSession
    (sid           : SessionId)
    (workspacePath : string)
    : Async<Result<unit, StorageError>> =
    async {
        let path = sessionPath workspacePath sid
        try
            if File.Exists path then
                File.Delete path
            return Result.Ok ()
        with ex ->
            return Result.Error (WriteFailure ex.Message)
    }
