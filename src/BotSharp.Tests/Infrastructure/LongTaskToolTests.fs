module BotSharp.Tests.Infrastructure.LongTaskToolTests

open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Infrastructure.Tools.LongTaskTool

// ═══════════════════════════════════════════════════════════════════════════
// LongTaskTool unit tests
//
// executeLongTaskTool is tested with a mocked RunSubagentStep.
// The mock receives the injected signal tool list (handoff/complete) and
// can call them to drive the orchestration loop.
// ═══════════════════════════════════════════════════════════════════════════

type ToolExec = Map<string, JsonElement> -> Async<ToolResult>
type ToolPair = ToolSpec * ToolExec

/// Build a Map<string, JsonElement> from a JSON object string.
let private parseArgs (json: string) : Map<string, JsonElement> =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.EnumerateObject()
    |> Seq.map (fun p -> p.Name, p.Value.Clone())
    |> Map.ofSeq

/// Find and call a named signal tool from the injected tool list.
let private callSignalTool (name: string) (argsJson: string) (tools: ToolPair list) =
    async {
        let exec = tools |> List.find (fun (spec, _) -> spec.Name = ToolName name) |> snd
        let args = parseArgs argsJson
        let! _ = exec args
        return ()
    }

// ── Argument validation ──────────────────────────────────────────────────

[<Fact>]
let ``executeLongTaskTool returns ToolFailure when goal is missing`` () =
    let runStub _ _ _ = async { return Result.Ok "done" }
    let result = executeLongTaskTool runStub None Map.empty |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | ToolSuccess s -> Assert.Fail($"Expected failure but got: {s}")

// ── Single-step completion ───────────────────────────────────────────────

[<Fact>]
let ``executeLongTaskTool returns ToolSuccess when subagent calls complete on first step`` () =
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            do! callSignalTool "complete" """{"summary":"All done"}""" tools
            return Result.Ok "step output"
        }
    let args = parseArgs """{"goal":"Test goal"}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("All done", msg)
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")

// ── Multi-step handoff then complete ───────────────────────────────────

[<Fact>]
let ``executeLongTaskTool loops through handoffs and returns final complete summary`` () =
    let mutable stepCount = 0
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            stepCount <- stepCount + 1
            if stepCount < 3 then
                do! callSignalTool "handoff" (sprintf """{"message":"Progress after step %d"}""" stepCount) tools
            else
                do! callSignalTool "complete" """{"summary":"Final complete"}""" tools
            return Result.Ok "output"
        }
    let args = parseArgs """{"goal":"Multi-step goal"}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Equal(3, stepCount)
        Assert.Contains("Final complete", msg)
    | ToolFailure e -> Assert.Fail($"Expected success: {e}")

// ── Max steps clamping ──────────────────────────────────────────────────

[<Fact>]
let ``executeLongTaskTool clamps max_steps to 1 when given 0`` () =
    let mutable stepCount = 0
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            stepCount <- stepCount + 1
            do! callSignalTool "complete" """{"summary":"done"}""" tools
            return Result.Ok "ok"
        }
    let args = parseArgs """{"goal":"g","max_steps":0}"""
    let _ = executeLongTaskTool runStep None args |> Async.RunSynchronously
    Assert.Equal(1, stepCount)

[<Fact>]
let ``executeLongTaskTool clamps max_steps to 100 when given 999`` () =
    let mutable stepCount = 0
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            stepCount <- stepCount + 1
            // Never complete → drives up to max
            do! callSignalTool "handoff" """{"message":"still going"}""" tools
            return Result.Ok "ok"
        }
    let args = parseArgs """{"goal":"g","max_steps":999}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    Assert.Equal(100, stepCount)
    match result with
    | ToolSuccess msg -> Assert.Contains("max steps", msg)
    | _ -> ()

// ── Max steps reached ───────────────────────────────────────────────────

[<Fact>]
let ``executeLongTaskTool returns max steps message when steps exhausted`` () =
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            do! callSignalTool "handoff" """{"message":"still going"}""" tools
            return Result.Ok "partial"
        }
    let args = parseArgs """{"goal":"Endless goal","max_steps":3}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("max steps", msg)
        Assert.Contains("3", msg)
    | ToolFailure e -> Assert.Fail($"Expected ToolSuccess but got failure: {e}")

// ── Subagent error handling ─────────────────────────────────────────────

[<Fact>]
let ``executeLongTaskTool returns ToolSuccess with failure message when subagent errors immediately`` () =
    let err : Result<string, AgentError> = Result.Error (AgentLlmFailure { Kind = ServerError 500; RawMessage = "internal error"; ProviderCode = None })
    let runStep _ _sys _usr = async { return err }
    let args = parseArgs """{"goal":"Will fail"}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("failed", msg.ToLowerInvariant())
    | ToolFailure e   -> Assert.Fail($"Expected ToolSuccess (error message) but got ToolFailure: {e}")

[<Fact>]
let ``executeLongTaskTool includes last handoff in failure message when available`` () =
    let mutable stepCount = 0
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            stepCount <- stepCount + 1
            if stepCount = 1 then
                do! callSignalTool "handoff" """{"message":"Handoff before failure"}""" tools
                return Result.Ok "ok"
            else
                let err2 : Result<string, AgentError> = Result.Error (AgentLlmFailure { Kind = ServerError 500; RawMessage = "fail"; ProviderCode = None })
                return! async { return err2 }
        }
    let args = parseArgs """{"goal":"Partial work","max_steps":5}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("Handoff before failure", msg)
    | ToolFailure e   -> Assert.Fail($"Expected ToolSuccess but got: {e}")

// ── No signal called (auto-extract) ─────────────────────────────────────

[<Fact>]
let ``executeLongTaskTool treats step with no signal as implicit handoff`` () =
    let mutable stepCount = 0
    let runStep (tools: ToolPair list) _sys _usr =
        async {
            stepCount <- stepCount + 1
            if stepCount >= 3 then
                do! callSignalTool "complete" """{"summary":"finally done"}""" tools
            // On step 1 and 2, no signal called — content used as handoff
            return Result.Ok (sprintf "auto-extract content step %d" stepCount)
        }
    let args = parseArgs """{"goal":"Auto extract test","max_steps":5}"""
    let result = executeLongTaskTool runStep None args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("finally done", msg)
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")
