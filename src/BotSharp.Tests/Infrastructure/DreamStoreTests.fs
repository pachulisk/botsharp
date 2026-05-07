module BotSharp.Tests.Infrastructure.DreamStoreTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Storage.DreamStore

// ═══════════════════════════════════════════════════════════════════════════
// makeSha — 8-char lowercase hex digest
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``makeSha produces an 8-character string`` () =
    Assert.Equal(8, (makeSha "hello world").Length)

[<Fact>]
let ``makeSha produces only lowercase hex characters`` () =
    let sha = makeSha "test content"
    Assert.True(sha |> Seq.forall (fun c -> "0123456789abcdef".Contains(c)),
                $"Non-hex char in sha: {sha}")

[<Fact>]
let ``makeSha is deterministic for the same input`` () =
    let a = makeSha "same input"
    let b = makeSha "same input"
    Assert.Equal(a, b)

[<Fact>]
let ``makeSha produces different output for different inputs`` () =
    let a = makeSha "input one"
    let b = makeSha "input two"
    Assert.NotEqual<string>(a, b)

[<Fact>]
let ``makeSha handles empty string without throwing`` () =
    let sha = makeSha ""
    Assert.Equal(8, sha.Length)

// ═══════════════════════════════════════════════════════════════════════════
// appendDreamEntry / loadDreamLog — round-trip via temp directory
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> Async<unit>) =
    async {
        let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        try
            do! f dir
        finally
            try Directory.Delete(dir, recursive = true) with _ -> ()
    }

[<Fact>]
let ``loadDreamLog returns empty list when file does not exist`` () =
    withTempDir (fun dir -> async {
        let! result = loadDreamLog dir
        match result with
        | Result.Ok entries -> Assert.Empty(entries)
        | Result.Error e    -> Assert.Fail($"Expected Ok [], got Error: {e}")
    }) |> Async.RunSynchronously

[<Fact>]
let ``appendDreamEntry then loadDreamLog round-trips a single entry`` () =
    withTempDir (fun dir -> async {
        let entry = {
            Sha          = makeSha "test"
            OccurredAt   = DateTimeOffset.UtcNow
            Summary      = "This is a test summary."
            MessageCount = 42
        }
        let! appended = appendDreamEntry dir entry
        Assert.Equal(Result.Ok (), appended)

        let! loaded = loadDreamLog dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadDreamLog failed: {e}")
        | Result.Ok entries ->
            Assert.Equal(1, entries.Length)
            let e = entries.[0]
            Assert.Equal(entry.Sha,          e.Sha)
            Assert.Equal(entry.Summary,      e.Summary)
            Assert.Equal(entry.MessageCount, e.MessageCount)
            // DateTimeOffset round-trips through ISO 8601 — allow 1-second tolerance
            Assert.True(abs (entry.OccurredAt - e.OccurredAt).TotalSeconds < 1.0)
    }) |> Async.RunSynchronously

[<Fact>]
let ``multiple entries are loaded in append order`` () =
    withTempDir (fun dir -> async {
        let entries =
            [ "first summary"; "second summary"; "third summary" ]
            |> List.mapi (fun i summary ->
                { Sha = makeSha summary; OccurredAt = DateTimeOffset.UtcNow; Summary = summary; MessageCount = i })

        for e in entries do
            let! _ = appendDreamEntry dir e
            ()

        let! loaded = loadDreamLog dir
        match loaded with
        | Result.Error e -> Assert.Fail($"loadDreamLog failed: {e}")
        | Result.Ok es ->
            Assert.Equal(3, es.Length)
            Assert.Equal("first summary",  es.[0].Summary)
            Assert.Equal("second summary", es.[1].Summary)
            Assert.Equal("third summary",  es.[2].Summary)
    }) |> Async.RunSynchronously

[<Fact>]
let ``findDreamEntry returns Some for matching sha prefix`` () =
    withTempDir (fun dir -> async {
        let entry = { Sha = "abcd1234"; OccurredAt = DateTimeOffset.UtcNow; Summary = "find me"; MessageCount = 1 }
        let! _ = appendDreamEntry dir entry
        let! result = findDreamEntry dir "abcd"
        match result with
        | Result.Error e    -> Assert.Fail($"findDreamEntry failed: {e}")
        | Result.Ok None    -> Assert.Fail("Expected Some entry, got None")
        | Result.Ok (Some e) -> Assert.Equal("abcd1234", e.Sha)
    }) |> Async.RunSynchronously

[<Fact>]
let ``findDreamEntry returns None for non-matching sha`` () =
    withTempDir (fun dir -> async {
        let entry = { Sha = "abcd1234"; OccurredAt = DateTimeOffset.UtcNow; Summary = "not this one"; MessageCount = 1 }
        let! _ = appendDreamEntry dir entry
        let! result = findDreamEntry dir "ffff"
        match result with
        | Result.Error e     -> Assert.Fail($"findDreamEntry failed: {e}")
        | Result.Ok (Some _) -> Assert.Fail("Expected None, got Some")
        | Result.Ok None     -> ()
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// Resilience — malformed lines are silently skipped
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadDreamLog skips malformed JSON lines without error`` () =
    withTempDir (fun dir -> async {
        // Write a dreams.jsonl with one valid entry and one garbage line
        let path = Path.Combine(dir, "dreams.jsonl")
        let goodEntry = { Sha = "aabbccdd"; OccurredAt = DateTimeOffset.UtcNow; Summary = "valid"; MessageCount = 3 }
        let! _ = appendDreamEntry dir goodEntry
        // Append a malformed line directly
        File.AppendAllText(path, "not valid json at all\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries ->
            // Only the valid entry should survive
            Assert.Equal(1, entries.Length)
            Assert.Equal("aabbccdd", entries.[0].Sha)
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadDreamLog skips entries with missing required fields`` () =
    withTempDir (fun dir -> async {
        // A JSON object missing the required "sha" field
        let path = Path.Combine(dir, "dreams.jsonl")
        File.WriteAllText(path, """{"occurred_at":"2026-01-01T00:00:00+00:00","summary":"no sha","message_count":1}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries -> Assert.Empty(entries)
    }) |> Async.RunSynchronously

[<Fact>]
let ``findDreamEntry returns Ok None when dreams.jsonl does not exist`` () =
    withTempDir (fun dir -> async {
        // loadDreamLog returns Ok [] for missing file; findDreamEntry returns Ok None
        let! result = findDreamEntry dir "anysha"
        match result with
        | Result.Error e      -> Assert.Fail($"Expected Ok None, got Error: {e}")
        | Result.Ok (Some e)  -> Assert.Fail($"Expected None, got Some {e.Sha}")
        | Result.Ok None      -> ()
    }) |> Async.RunSynchronously

[<Fact>]
let ``appendDreamEntry then loadDreamLog with unicode summary preserves text`` () =
    withTempDir (fun dir -> async {
        let entry = {
            Sha          = makeSha "unicode"
            OccurredAt   = DateTimeOffset.UtcNow
            Summary      = "你好世界 🌍 — Unicode summary"
            MessageCount = 7
        }
        let! _ = appendDreamEntry dir entry
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"loadDreamLog failed: {e}")
        | Result.Ok entries ->
            Assert.Equal(1, entries.Length)
            Assert.Equal("你好世界 🌍 — Unicode summary", entries.[0].Summary)
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// appendDreamEntry — error path (non-existent workspace directory)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``appendDreamEntry returns Result.Error when directory does not exist`` () =
    async {
        let entry = {
            Sha          = "aabbccdd"
            OccurredAt   = DateTimeOffset.UtcNow
            Summary      = "test"
            MessageCount = 1
        }
        let! result = appendDreamEntry "/nonexistent/path/that/does/not/exist/at/all" entry
        match result with
        | Result.Error _ -> ()
        | Result.Ok ()   -> Assert.Fail("Expected Result.Error for non-existent directory")
    } |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// deserializeEntry — FormatException path (invalid occurred_at)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``loadDreamLog skips entries with invalid occurred_at datetime format`` () =
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "dreams.jsonl")
        // Valid JSON but occurred_at is not a parseable DateTimeOffset
        File.WriteAllText(path, """{"sha":"aabbccdd","occurred_at":"not-a-date","summary":"skip me","message_count":1}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries -> Assert.Empty(entries)   // entry skipped due to FormatException
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadDreamLog deserializes entry with missing message_count as MessageCount 0`` () =
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "dreams.jsonl")
        // message_count absent → getInt "message_count" returns 0 (the | _ -> 0 branch)
        File.WriteAllText(path, """{"sha":"11223344","occurred_at":"2026-01-01T00:00:00+00:00","summary":"no count"}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries ->
            Assert.Equal(1, entries.Length)
            Assert.Equal(0, entries.[0].MessageCount)
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadDreamLog deserializes entry with string message_count as MessageCount 0`` () =
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "dreams.jsonl")
        // message_count is a string (wrong type) → getInt returns 0 (the | _ -> 0 branch)
        File.WriteAllText(path, """{"sha":"55667788","occurred_at":"2026-01-01T00:00:00+00:00","summary":"string count","message_count":"five"}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries ->
            Assert.Equal(1, entries.Length)
            Assert.Equal(0, entries.[0].MessageCount)
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadDreamLog skips entries with null sha field`` () =
    // JSON null (ValueKind = Null) fails the String guard → tryGetString returns None → entry skipped
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "dreams.jsonl")
        File.WriteAllText(path, """{"sha":null,"occurred_at":"2026-01-01T00:00:00+00:00","summary":"null sha","message_count":1}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries -> Assert.Empty(entries)
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadDreamLog skips entries with missing summary field`` () =
    // Missing summary → tryGetString "summary" = None → | _ -> None branch
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "dreams.jsonl")
        File.WriteAllText(path, """{"sha":"aabbccdd","occurred_at":"2026-01-01T00:00:00+00:00","message_count":1}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries -> Assert.Empty(entries)
    }) |> Async.RunSynchronously

[<Fact>]
let ``loadDreamLog skips entries with missing occurred_at field`` () =
    // Missing occurred_at → tryGetString "occurred_at" = None → | _ -> None branch
    withTempDir (fun dir -> async {
        let path = Path.Combine(dir, "dreams.jsonl")
        File.WriteAllText(path, """{"sha":"aabbccdd","summary":"no date","message_count":1}""" + "\n")
        let! result = loadDreamLog dir
        match result with
        | Result.Error e -> Assert.Fail($"Expected Ok, got Error: {e}")
        | Result.Ok entries -> Assert.Empty(entries)
    }) |> Async.RunSynchronously

[<Fact>]
let ``findDreamEntry matches on full SHA string`` () =
    withTempDir (fun dir -> async {
        let entry = { Sha = "deadbeef"; OccurredAt = DateTimeOffset.UtcNow; Summary = "full match"; MessageCount = 5 }
        let! _ = appendDreamEntry dir entry
        // findDreamEntry uses StartsWith — full SHA also satisfies StartsWith(fullSha)
        let! result = findDreamEntry dir "deadbeef"
        match result with
        | Result.Error e     -> Assert.Fail($"findDreamEntry failed: {e}")
        | Result.Ok None     -> Assert.Fail("Expected Some entry for full SHA match, got None")
        | Result.Ok (Some e) -> Assert.Equal("deadbeef", e.Sha)
    }) |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// parseDreamLine — public JSONL deserializer used by StateDb rebuild
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseDreamLine returns Some for valid JSONL entry`` () =
    let line = """{"sha":"aabb1122","occurred_at":"2026-03-01T10:00:00+00:00","summary":"Daily reflection","message_count":7}"""
    match parseDreamLine line with
    | None   -> Assert.Fail("Expected Some for valid JSONL entry")
    | Some e ->
        Assert.Equal("aabb1122", e.Sha)
        Assert.Equal("Daily reflection", e.Summary)
        Assert.Equal(7, e.MessageCount)

[<Fact>]
let ``parseDreamLine returns None for invalid JSON`` () =
    Assert.Equal(None, parseDreamLine "not-json-at-all")

[<Fact>]
let ``parseDreamLine returns None when sha is missing`` () =
    let line = """{"occurred_at":"2026-03-01T10:00:00+00:00","summary":"no sha","message_count":1}"""
    Assert.Equal(None, parseDreamLine line)

[<Fact>]
let ``parseDreamLine returns None when summary is missing`` () =
    let line = """{"sha":"aabb1122","occurred_at":"2026-03-01T10:00:00+00:00","message_count":1}"""
    Assert.Equal(None, parseDreamLine line)

[<Fact>]
let ``parseDreamLine returns None when occurred_at is not a valid DateTimeOffset`` () =
    let line = """{"sha":"aabb1122","occurred_at":"not-a-date","summary":"bad date","message_count":1}"""
    Assert.Equal(None, parseDreamLine line)

[<Fact>]
let ``parseDreamLine defaults message_count to 0 when absent`` () =
    let line = """{"sha":"ccdd3344","occurred_at":"2026-03-01T10:00:00+00:00","summary":"no count"}"""
    match parseDreamLine line with
    | None   -> Assert.Fail("Expected Some entry even without message_count")
    | Some e -> Assert.Equal(0, e.MessageCount)
