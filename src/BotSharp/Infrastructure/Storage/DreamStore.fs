module BotSharp.Infrastructure.Storage.DreamStore

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Dream log — persistent JSONL store for memory consolidation entries.
//
// Each entry is one JSON object per line in {workspacePath}/dreams.jsonl.
// The SHA is derived from a timestamp + summary so it is deterministic and
// unique without a separate counter.
// ═══════════════════════════════════════════════════════════════════════════

/// Compute an 8-char lowercase-hex SHA256 digest of a string.
/// Used as the dream entry ID — short enough to type, collision-resistant enough
/// for a personal log that grows by one entry per consolidation.
let makeSha (content: string) : string =
    use sha   = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
    Convert.ToHexString(bytes).[..7].ToLowerInvariant()

let private dreamFile (workspacePath: string) =
    Path.Combine(workspacePath, "dreams.jsonl")

let private serializeEntry (e: DreamEntry) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("sha",           e.Sha)
    w.WriteString("occurred_at",   e.OccurredAt.ToString("o"))
    w.WriteString("summary",       e.Summary)
    w.WriteNumber("message_count", e.MessageCount)
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

let private deserializeEntry (line: string) : DreamEntry option =
    try
        use doc = JsonDocument.Parse(line)
        let el  = doc.RootElement
        let tryGetString (name: string) : string option =
            match el.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.String ->
                v.GetString() |> Option.ofObj
            | _ -> None
        let getInt (name: string) : int =
            match el.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0
        match tryGetString "sha", tryGetString "occurred_at", tryGetString "summary" with
        | Some sha, Some occurredAt, Some summary ->
            Some {
                Sha          = sha
                OccurredAt   = DateTimeOffset.Parse(occurredAt)
                Summary      = summary
                MessageCount = getInt "message_count"
            }
        | _ -> None
    // Narrow to parse-specific exceptions only; non-parse exceptions propagate.
    with :? JsonException | :? FormatException -> None

/// Parse a single JSONL line into a DreamEntry (public wrapper for StateDb rebuild).
let parseDreamLine = deserializeEntry

/// Append a single dream entry to the workspace dreams.jsonl file.
let appendDreamEntry (workspacePath: string) (entry: DreamEntry) : Async<Result<unit, string>> =
    async {
        try
            let path = dreamFile workspacePath
            let line = serializeEntry entry + "\n"
            do! File.AppendAllTextAsync(path, line) |> Async.AwaitTask
            return Result.Ok ()
        with ex ->
            return Result.Error ex.Message
    }

/// Load all dream entries in chronological order (oldest first).
let loadDreamLog (workspacePath: string) : Async<Result<DreamEntry list, string>> =
    async {
        try
            let path = dreamFile workspacePath
            if not (File.Exists path) then return Result.Ok []
            else
                let! text = File.ReadAllTextAsync(path) |> Async.AwaitTask
                let entries =
                    text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose deserializeEntry
                    |> Array.toList
                return Result.Ok entries
        with ex ->
            return Result.Error ex.Message
    }

/// Find a dream entry by SHA prefix. Returns None if not found.
let findDreamEntry (workspacePath: string) (sha: string) : Async<Result<DreamEntry option, string>> =
    async {
        let! result = loadDreamLog workspacePath
        match result with
        | Result.Error e -> return Result.Error e
        | Result.Ok entries ->
            return Result.Ok (entries |> List.tryFind (fun e -> e.Sha.StartsWith(sha)))
    }
