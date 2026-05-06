module BotSharp.Tests.Infrastructure.TaskToolTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.TaskTool
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// TaskTool unit tests
//
// formatTaskList is tested as a pure function.
// executeTaskCreate / executeTaskUpdate / executeTaskList are tested via a
// real file-based SQLite DB (same pattern as StateDbTests).
// ═══════════════════════════════════════════════════════════════════════════

/// Create a real file-based StateDb for testing.
let private mkDb () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let factory = init tmp |> Async.RunSynchronously
    (factory, tmp)

/// Build a TaskItem with minimal fields (for formatTaskList tests).
let private mkTask id subject status =
    let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    { Id          = id
      SessionId   = None
      Subject     = subject
      Description = None
      Status      = status
      CreatedAt   = now
      UpdatedAt   = now
      CompletedAt = None
      CreatedBy   = "agent" }

/// Build a Map<string, JsonElement> from a JSON object string.
let private parseArgs (json: string) : Map<string, JsonElement> =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.EnumerateObject()
    |> Seq.map (fun p -> p.Name, p.Value.Clone())
    |> Map.ofSeq

// ── formatTaskList (pure) ────────────────────────────────────────────────

[<Fact>]
let ``formatTaskList returns (no tasks) for empty list`` () =
    Assert.Equal("(no tasks)", formatTaskList [])

[<Fact>]
let ``formatTaskList single task shows correct header`` () =
    let tasks = [ mkTask "aaaaaa" "Fix bug" "pending" ]
    let result = formatTaskList tasks
    Assert.Contains("1 total", result)
    Assert.Contains("1 pending", result)
    Assert.Contains("0 in_progress", result)
    Assert.Contains("0 completed", result)

[<Fact>]
let ``formatTaskList counts each status correctly`` () =
    let tasks = [
        mkTask "aaaaaa" "Task A" "pending"
        mkTask "bbbbbb" "Task B" "in_progress"
        mkTask "cccccc" "Task C" "completed"
        mkTask "dddddd" "Task D" "pending"
    ]
    let result = formatTaskList tasks
    Assert.Contains("4 total", result)
    Assert.Contains("2 pending", result)
    Assert.Contains("1 in_progress", result)
    Assert.Contains("1 completed", result)

[<Fact>]
let ``formatTaskList truncates subject longer than 40 chars`` () =
    let longSubject = String.replicate 50 "x"
    let tasks = [ mkTask "aaaaaa" longSubject "pending" ]
    let result = formatTaskList tasks
    // Subject should be truncated to 40 chars + "..."
    let truncated = String.replicate 40 "x" + "..."
    Assert.Contains(truncated, result)

[<Fact>]
let ``formatTaskList does not truncate subject of exactly 40 chars`` () =
    let subject40 = String.replicate 40 "y"
    let tasks = [ mkTask "aaaaaa" subject40 "pending" ]
    let result = formatTaskList tasks
    Assert.Contains(subject40, result)
    Assert.DoesNotContain(subject40 + "...", result)

[<Fact>]
let ``formatTaskList shows correct status icons`` () =
    let tasks = [
        mkTask "aaaaaa" "Pending" "pending"
        mkTask "bbbbbb" "Active"  "in_progress"
        mkTask "cccccc" "Done"    "completed"
    ]
    let result = formatTaskList tasks
    Assert.Contains("\u25CB", result)   // ○ pending
    Assert.Contains("\u25C9", result)   // ◉ in_progress
    Assert.Contains("\u2713", result)   // ✓ completed

[<Fact>]
let ``formatTaskList includes task id and subject in output`` () =
    let tasks = [ mkTask "abc123" "My important task" "pending" ]
    let result = formatTaskList tasks
    Assert.Contains("abc123", result)
    Assert.Contains("My important task", result)

// ── executeTaskCreate ────────────────────────────────────────────────────

[<Fact>]
let ``executeTaskCreate returns ToolSuccess with subject when valid`` () =
    let openDb, tmp = mkDb ()
    try
        let args = parseArgs """{"subject":"Implement feature X"}"""
        let result = executeTaskCreate openDb args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains("Created task", msg)
            Assert.Contains("Implement feature X", msg)
        | ToolFailure e -> Assert.Fail($"Expected success but got failure: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskCreate returns ToolFailure when subject is missing`` () =
    let openDb, tmp = mkDb ()
    try
        let result = executeTaskCreate openDb Map.empty |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | ToolSuccess msg -> Assert.Fail($"Expected failure but got: {msg}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskCreate accepts optional description`` () =
    let openDb, tmp = mkDb ()
    try
        let args = parseArgs """{"subject":"Task with desc","description":"Detailed steps here"}"""
        let result = executeTaskCreate openDb args |> Async.RunSynchronously
        match result with
        | ToolSuccess _ -> ()
        | ToolFailure e -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskCreate assigns a 6-char hex id`` () =
    let openDb, tmp = mkDb ()
    try
        let args = parseArgs """{"subject":"Check id format"}"""
        let result = executeTaskCreate openDb args |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            // Message format: "Created task XXXXXX: subject"
            let parts = msg.Split(' ')
            let id = parts.[2].TrimEnd(':')
            Assert.Equal(6, id.Length)
            Assert.True(id |> Seq.forall (fun c -> "0123456789abcdef".Contains(c)),
                        $"ID '{id}' should be hex")
        | ToolFailure e -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

// ── executeTaskUpdate ────────────────────────────────────────────────────

[<Fact>]
let ``executeTaskUpdate returns ToolFailure when id is missing`` () =
    let openDb, tmp = mkDb ()
    try
        let args = parseArgs """{"status":"in_progress"}"""
        let result = executeTaskUpdate openDb args |> Async.RunSynchronously
        match result with
        | ToolFailure _ -> ()
        | ToolSuccess msg -> Assert.Fail($"Expected failure but got: {msg}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskUpdate returns ParameterMissing when neither status nor subject given`` () =
    let openDb, tmp = mkDb ()
    try
        let args = parseArgs """{"id":"aaaaaa"}"""
        let result = executeTaskUpdate openDb args |> Async.RunSynchronously
        match result with
        | ToolFailure (ParameterMissing _) -> ()
        | other -> Assert.Fail($"Expected ParameterMissing but got: {other}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskUpdate updates status on existing task`` () =
    let openDb, tmp = mkDb ()
    try
        let createArgs = parseArgs """{"subject":"Status target"}"""
        let created = executeTaskCreate openDb createArgs |> Async.RunSynchronously
        let taskId =
            match created with
            | ToolSuccess msg -> msg.Split(' ').[2].TrimEnd(':')
            | ToolFailure e   -> failwith $"Setup failed: {e}"

        let updateArgs = parseArgs (sprintf """{"id":"%s","status":"in_progress"}""" taskId)
        let result = executeTaskUpdate openDb updateArgs |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains(taskId, msg)
            Assert.Contains("in_progress", msg)
        | ToolFailure e -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskUpdate updates subject on existing task`` () =
    let openDb, tmp = mkDb ()
    try
        let createArgs = parseArgs """{"subject":"Old subject"}"""
        let created = executeTaskCreate openDb createArgs |> Async.RunSynchronously
        let taskId =
            match created with
            | ToolSuccess msg -> msg.Split(' ').[2].TrimEnd(':')
            | ToolFailure e   -> failwith $"Setup failed: {e}"

        let updateArgs = parseArgs (sprintf """{"id":"%s","subject":"New subject"}""" taskId)
        let result = executeTaskUpdate openDb updateArgs |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("New subject", msg)
        | ToolFailure e   -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskUpdate returns failure for non-existent task id`` () =
    let openDb, tmp = mkDb ()
    try
        let args = parseArgs """{"id":"zzzzzz","status":"completed"}"""
        let result = executeTaskUpdate openDb args |> Async.RunSynchronously
        match result with
        | ToolFailure (ExecutionFailed msg) -> Assert.Contains("not found", msg)
        | other -> Assert.Fail($"Expected ExecutionFailed 'not found' but got: {other}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskUpdate can mark task as completed`` () =
    let openDb, tmp = mkDb ()
    try
        let createArgs = parseArgs """{"subject":"To complete"}"""
        let created = executeTaskCreate openDb createArgs |> Async.RunSynchronously
        let taskId =
            match created with
            | ToolSuccess msg -> msg.Split(' ').[2].TrimEnd(':')
            | ToolFailure e   -> failwith $"Setup failed: {e}"

        let updateArgs = parseArgs (sprintf """{"id":"%s","status":"completed"}""" taskId)
        let result = executeTaskUpdate openDb updateArgs |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("completed", msg)
        | ToolFailure e   -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

// ── executeTaskList ──────────────────────────────────────────────────────

[<Fact>]
let ``executeTaskList returns (no tasks) for empty database`` () =
    let openDb, tmp = mkDb ()
    try
        let result = executeTaskList openDb Map.empty |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Equal("(no tasks)", msg)
        | ToolFailure e   -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskList returns all tasks when no status filter`` () =
    let openDb, tmp = mkDb ()
    try
        for i in 1..3 do
            let args = parseArgs (sprintf """{"subject":"Task %d"}""" i)
            executeTaskCreate openDb args |> Async.RunSynchronously |> ignore

        let result = executeTaskList openDb Map.empty |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains("3 total", msg)
        | ToolFailure e -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskList with status=all returns all tasks`` () =
    let openDb, tmp = mkDb ()
    try
        let args1 = parseArgs """{"subject":"Task A"}"""
        executeTaskCreate openDb args1 |> Async.RunSynchronously |> ignore
        let args2 = parseArgs """{"subject":"Task B"}"""
        executeTaskCreate openDb args2 |> Async.RunSynchronously |> ignore

        let result = executeTaskList openDb (parseArgs """{"status":"all"}""") |> Async.RunSynchronously
        match result with
        | ToolSuccess msg -> Assert.Contains("2 total", msg)
        | ToolFailure e   -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskList filters to only pending tasks`` () =
    let openDb, tmp = mkDb ()
    try
        let c1 = executeTaskCreate openDb (parseArgs """{"subject":"Pending task"}""") |> Async.RunSynchronously
        let id1 = match c1 with ToolSuccess msg -> msg.Split(' ').[2].TrimEnd(':') | _ -> failwith "setup"

        let c2 = executeTaskCreate openDb (parseArgs """{"subject":"Active task"}""") |> Async.RunSynchronously
        let id2 = match c2 with ToolSuccess msg -> msg.Split(' ').[2].TrimEnd(':') | _ -> failwith "setup"

        let upd = parseArgs (sprintf """{"id":"%s","status":"in_progress"}""" id2)
        executeTaskUpdate openDb upd |> Async.RunSynchronously |> ignore

        let result = executeTaskList openDb (parseArgs """{"status":"pending"}""") |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains(id1, msg)
            Assert.DoesNotContain(id2, msg)
        | ToolFailure e -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``executeTaskList filters to only completed tasks`` () =
    let openDb, tmp = mkDb ()
    try
        let c1 = executeTaskCreate openDb (parseArgs """{"subject":"Will complete"}""") |> Async.RunSynchronously
        let id1 = match c1 with ToolSuccess msg -> msg.Split(' ').[2].TrimEnd(':') | _ -> failwith "setup"

        executeTaskCreate openDb (parseArgs """{"subject":"Still pending"}""") |> Async.RunSynchronously |> ignore

        let upd = parseArgs (sprintf """{"id":"%s","status":"completed"}""" id1)
        executeTaskUpdate openDb upd |> Async.RunSynchronously |> ignore

        let result = executeTaskList openDb (parseArgs """{"status":"completed"}""") |> Async.RunSynchronously
        match result with
        | ToolSuccess msg ->
            Assert.Contains(id1, msg)
            Assert.Contains("1 total", msg)
        | ToolFailure e -> Assert.Fail($"Expected success: {e}")
    finally
        try Directory.Delete(tmp, true) with _ -> ()
