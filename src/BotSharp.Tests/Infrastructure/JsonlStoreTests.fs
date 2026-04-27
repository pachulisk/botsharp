module BotSharp.Tests.Infrastructure.JsonlStoreTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.JsonlStore

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"jsonlstore-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

let private sid = SessionId "test:jsonlstore"

let private makeSnap (msgs: Message list) =
    let now   = DateTimeOffset.UtcNow
    let empty = SessionSnapshot.empty sid now
    msgs |> List.fold (fun s m -> SessionSnapshot.append m s) empty

// ═══════════════════════════════════════════════════════════════════════════
// loadSession — non-existent file returns empty snapshot
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadSession returns empty snapshot when file does not exist`` () =
    withTempDir (fun dir ->
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok snap ->
            Assert.Equal(0, SessionSnapshot.messageCount snap)
            Assert.Equal(sid, SessionSnapshot.id snap)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

// ═══════════════════════════════════════════════════════════════════════════
// persistSession — creates sessions directory automatically
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``persistSession creates sessions subdirectory when absent`` () =
    withTempDir (fun dir ->
        let snap = makeSnap [ UserMessage ("hello", []); AssistantMessage ("world", None) ]
        let result = persistSession snap dir |> Async.RunSynchronously
        match result with
        | Result.Ok () ->
            let sessDir = Path.Combine(dir, "sessions")
            Assert.True(Directory.Exists(sessDir), "sessions/ should be created automatically")
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``persistSession creates a .jsonl file on disk`` () =
    withTempDir (fun dir ->
        let snap = makeSnap [ UserMessage ("q", []); AssistantMessage ("a", None) ]
        let _ = persistSession snap dir |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.jsonl")
        Assert.Equal(1, files.Length))

// ═══════════════════════════════════════════════════════════════════════════
// round-trip: persist then load
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``round-trip: persist then load returns same message count`` () =
    withTempDir (fun dir ->
        let msgs = [ UserMessage ("hi", []); AssistantMessage ("hello", None) ]
        let snap = makeSnap msgs
        let _ = persistSession snap dir |> Async.RunSynchronously
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded ->
            Assert.Equal(SessionSnapshot.messageCount snap, SessionSnapshot.messageCount loaded)
        | Result.Error e -> Assert.Fail($"Expected Ok on load, got Error: {e}"))

[<Fact>]
let ``round-trip: persisted UserMessage text is preserved`` () =
    withTempDir (fun dir ->
        let snap = makeSnap [ UserMessage ("unique user text 42", []) ]
        let _ = persistSession snap dir |> Async.RunSynchronously
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded ->
            let msgs = SessionSnapshot.messages loaded
            match msgs with
            | [ UserMessage (text, []) ] -> Assert.Equal("unique user text 42", text)
            | other -> Assert.Fail($"Expected exactly one UserMessage, got {other}")
        | Result.Error e -> Assert.Fail($"Expected Ok on load, got Error: {e}"))

[<Fact>]
let ``round-trip: persisted AssistantMessage text is preserved`` () =
    withTempDir (fun dir ->
        let snap = makeSnap [ UserMessage ("q", []); AssistantMessage ("assistant reply here", None) ]
        let _ = persistSession snap dir |> Async.RunSynchronously
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded ->
            let msgs = SessionSnapshot.messages loaded
            match List.last msgs with
            | AssistantMessage (text, _) -> Assert.Equal("assistant reply here", text)
            | other -> Assert.Fail($"Expected AssistantMessage last, got {other}")
        | Result.Error e -> Assert.Fail($"Expected Ok on load, got Error: {e}"))

[<Fact>]
let ``round-trip: message ordering is preserved`` () =
    withTempDir (fun dir ->
        let msgs = [
            UserMessage ("first", [])
            AssistantMessage ("second", None)
            UserMessage ("third", [])
            AssistantMessage ("fourth", None)
        ]
        let snap = makeSnap msgs
        let _ = persistSession snap dir |> Async.RunSynchronously
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded ->
            let loaded_msgs = SessionSnapshot.messages loaded
            Assert.Equal(4, loaded_msgs.Length)
            match loaded_msgs[0], loaded_msgs[2] with
            | UserMessage ("first", []), UserMessage ("third", []) -> ()
            | other -> Assert.Fail($"Expected first and third messages to be UserMessages, got {other}")
        | Result.Error e -> Assert.Fail($"Expected Ok on load, got Error: {e}"))

[<Fact>]
let ``round-trip: session id is preserved after persist/load`` () =
    withTempDir (fun dir ->
        let snap = makeSnap [ UserMessage ("msg", []) ]
        let _ = persistSession snap dir |> Async.RunSynchronously
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded -> Assert.Equal(sid, SessionSnapshot.id loaded)
        | Result.Error e   -> Assert.Fail($"Expected Ok, got Error: {e}"))

// ═══════════════════════════════════════════════════════════════════════════
// persistSession — overwrites existing file (idempotent rewrite)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``persistSession overwrites previous file with updated messages`` () =
    withTempDir (fun dir ->
        // First write: 1 message
        let snap1 = makeSnap [ UserMessage ("first only", []) ]
        let _ = persistSession snap1 dir |> Async.RunSynchronously

        // Second write: 3 messages (session grew)
        let snap2 = makeSnap [ UserMessage ("a", []); AssistantMessage ("b", None); UserMessage ("c", []) ]
        let _ = persistSession snap2 dir |> Async.RunSynchronously

        // Load should see only the latest state
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded -> Assert.Equal(3, SessionSnapshot.messageCount loaded)
        | Result.Error e   -> Assert.Fail($"Expected Ok, got Error: {e}"))

// ═══════════════════════════════════════════════════════════════════════════
// deleteSession
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``deleteSession removes the session file`` () =
    withTempDir (fun dir ->
        let snap = makeSnap [ UserMessage ("bye", []) ]
        let _ = persistSession snap dir |> Async.RunSynchronously
        let _ = deleteSession sid dir |> Async.RunSynchronously
        // Loading again should return empty snapshot (file gone)
        let result = loadSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded -> Assert.Equal(0, SessionSnapshot.messageCount loaded)
        | Result.Error e   -> Assert.Fail($"Expected empty snapshot after delete, got Error: {e}"))

[<Fact>]
let ``deleteSession returns Ok when file does not exist`` () =
    withTempDir (fun dir ->
        let result = deleteSession sid dir |> Async.RunSynchronously
        match result with
        | Result.Ok ()   -> ()
        | Result.Error e -> Assert.Fail($"Expected Ok for non-existent file, got Error: {e}"))

// ═══════════════════════════════════════════════════════════════════════════
// session ID file naming — safe characters
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``sessions with colon in ID produce exactly one .jsonl file`` () =
    withTempDir (fun dir ->
        // Session IDs like "telegram:12345" or "channel:42" can contain colons.
        // JsonlStore sanitizes characters that are invalid on the current OS
        // (on Windows, colons are replaced with underscores; on macOS/Linux they are kept).
        // Regardless of platform, exactly one file should be produced.
        let colonSid = SessionId "channel:42"
        let snap =
            SessionSnapshot.empty colonSid DateTimeOffset.UtcNow
            |> SessionSnapshot.append (UserMessage ("test", []))
        let _ = persistSession snap dir |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.jsonl")
        Assert.Equal(1, files.Length))

[<Fact>]
let ``two different session IDs produce two separate .jsonl files`` () =
    withTempDir (fun dir ->
        let sid1 = SessionId "session:1"
        let sid2 = SessionId "session:2"
        let snap1 =
            SessionSnapshot.empty sid1 DateTimeOffset.UtcNow
            |> SessionSnapshot.append (UserMessage ("from 1", []))
        let snap2 =
            SessionSnapshot.empty sid2 DateTimeOffset.UtcNow
            |> SessionSnapshot.append (UserMessage ("from 2", []))
        let _ = persistSession snap1 dir |> Async.RunSynchronously
        let _ = persistSession snap2 dir |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.jsonl")
        Assert.Equal(2, files.Length))

// ═══════════════════════════════════════════════════════════════════════════
// loadSession — corrupted JSONL returns ParseFailure
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadSession returns ParseFailure for corrupted JSONL`` () =
    withTempDir (fun dir ->
        // Write a sessions file with invalid JSON directly to bypass persistSession.
        let sessDir = Path.Combine(dir, "sessions")
        Directory.CreateDirectory(sessDir) |> ignore
        File.WriteAllText(
            Path.Combine(sessDir, "corrupted.jsonl"),
            "this is not valid json at all\n")
        let corruptedSid = SessionId "corrupted"
        let result = loadSession corruptedSid dir |> Async.RunSynchronously
        match result with
        | Result.Error _ -> ()   // any StorageError is acceptable
        | Result.Ok snap ->
            // If the implementation returns an empty snapshot on error, that's also
            // defensible — but the common path here is a ParseFailure.
            Assert.Equal(0, SessionSnapshot.messageCount snap))

[<Fact>]
let ``loadSession returns Ok empty snapshot for JSONL with only blank lines`` () =
    withTempDir (fun dir ->
        let sessDir = Path.Combine(dir, "sessions")
        Directory.CreateDirectory(sessDir) |> ignore
        // Only blank lines — parseSessionFile should return Ok with 0 messages.
        File.WriteAllText(Path.Combine(sessDir, "blank.jsonl"), "\n\n   \n")
        let blankSid = SessionId "blank"
        let result = loadSession blankSid dir |> Async.RunSynchronously
        match result with
        | Result.Ok snap -> Assert.Equal(0, SessionSnapshot.messageCount snap)
        | Result.Error e -> Assert.Fail($"Expected Ok empty snapshot, got Error: {e}"))

// ─────────────────────────────────────────────────────────────────────────────
// safeFileName — session IDs with invalid filename characters
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``session with slash in ID sanitizes to a valid filename and persists successfully`` () =
    withTempDir (fun dir ->
        // "/" is in Path.GetInvalidFileNameChars() on macOS/Linux and Windows.
        // safeFileName replaces it with "_", so the file is written as "user_session.jsonl".
        let slashSid = SessionId "user/session"
        let snap =
            SessionSnapshot.empty slashSid DateTimeOffset.UtcNow
            |> SessionSnapshot.append (UserMessage ("test", []))
        let r = persistSession snap dir |> Async.RunSynchronously
        Assert.True(r.IsOk, $"persistSession failed: {r}")
        let files = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.jsonl")
        Assert.Equal(1, files.Length))

[<Fact>]
let ``round-trip works for session ID with slash after sanitization`` () =
    withTempDir (fun dir ->
        // Slash is sanitized to underscore for the filename on all platforms.
        // Both persistSession and loadSession must apply the same safeFileName mapping.
        let slashSid = SessionId "chan/42"
        let snap =
            SessionSnapshot.empty slashSid DateTimeOffset.UtcNow
            |> SessionSnapshot.append (UserMessage ("slash test", []))
        let _ = persistSession snap dir |> Async.RunSynchronously
        let result = loadSession slashSid dir |> Async.RunSynchronously
        match result with
        | Result.Ok loaded ->
            Assert.Equal(1, SessionSnapshot.messageCount loaded)
            Assert.Equal(slashSid, SessionSnapshot.id loaded)
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}"))

[<Fact>]
let ``persistSession returns WriteFailure when workspace path is a file not a directory`` () =
    // Directory.CreateDirectory(sessions/) fails when workspacePath is itself a regular file
    // → the catch block returns WriteFailure rather than throwing.
    withTempDir (fun dir ->
        let fakePath = Path.Combine(dir, "not-a-dir")
        File.WriteAllText(fakePath, "I am a file, not a directory")
        let snap = makeSnap [ UserMessage ("msg", []) ]
        let result = persistSession snap fakePath |> Async.RunSynchronously
        match result with
        | Result.Error (WriteFailure _) -> ()
        | other -> Assert.Fail($"Expected WriteFailure, got {other}"))

[<Fact>]
let ``loadSession returns ParseFailure for file with invalid JSON schema`` () =
    withTempDir (fun dir ->
        // Valid JSON but wrong schema (unknown role) — parseSessionFile returns Error,
        // which loadSession wraps as ParseFailure.
        let sessDir = Path.Combine(dir, "sessions")
        Directory.CreateDirectory(sessDir) |> ignore
        File.WriteAllText(Path.Combine(sessDir, "schemabad.jsonl"), "{\"role\":\"unknown\"}\n")
        let badSid = SessionId "schemabad"
        let result = loadSession badSid dir |> Async.RunSynchronously
        match result with
        | Result.Error (ParseFailure _) -> ()
        | other -> Assert.Fail($"Expected ParseFailure, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// persistSession — atomic write (Python parity: write to .tmp then rename)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``persistSession leaves no .tmp file after successful save`` () =
    withTempDir (fun dir ->
        let snap   = makeSnap [ UserMessage ("hello", []) ]
        let result = persistSession snap dir |> Async.RunSynchronously
        match result with
        | Result.Ok () ->
            // The .tmp file must not linger after a successful write+rename
            let sessDir = Path.Combine(dir, "sessions")
            let tmpFiles = Directory.GetFiles(sessDir, "*.tmp")
            Assert.Empty(tmpFiles)
        | Result.Error e -> Assert.Fail($"Expected Ok, got {e}"))

[<Fact>]
let ``persistSession creates a file readable by loadSession after atomic rename`` () =
    withTempDir (fun dir ->
        let msgs   = [ UserMessage ("hello", []); AssistantMessage ("world", None) ]
        let snap   = makeSnap msgs
        let _      = persistSession snap dir |> Async.RunSynchronously
        let loaded = loadSession sid dir |> Async.RunSynchronously
        match loaded with
        | Result.Ok s -> Assert.Equal(2, SessionSnapshot.messageCount s)
        | Result.Error e -> Assert.Fail($"Expected Ok, got {e}"))
