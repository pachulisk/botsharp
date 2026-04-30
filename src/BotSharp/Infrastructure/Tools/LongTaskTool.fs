module BotSharp.Infrastructure.Tools.LongTaskTool

open System
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// LongTaskTool — meta-ReAct loop for long-running tasks
//
// Breaks a long task into sequential subagent steps, each starting fresh
// with the original goal + progress from the previous step. Two signal
// tools (handoff / complete) let the subagent control the orchestration.
//
// Mirrors nanobot PR #3460: LongTaskTool, HandoffTool, CompleteTool.
//
// Flow:
//   Agent calls long_task(goal="Audit 50 files", max_steps=20)
//     → Step 1: subagent runs with goal + 8-tool budget
//       → calls handoff("Processed files 1-10, results in audit_1.md")
//     → Step 2: subagent runs with goal + handoff summary
//       → calls handoff("Processed files 11-20, results in audit_2.md")
//     → ...
//     → Step N: subagent runs, calls complete("All done. See audit_report.md")
//     → LongTaskTool returns the complete summary
// ═══════════════════════════════════════════════════════════════════════════

/// Shared signal store — written by HandoffTool/CompleteTool, read by orchestrator.
type private SignalStore = {
    mutable SignalType : string option   // "handoff" | "complete" | None
    mutable Payload    : string
}

let private createSignalStore () = { SignalType = None; Payload = "" }

// ── Signal tool specs ────────────────────────────────────────────────────

let private handoffSpec : ToolSpec = {
    Name        = ToolName "handoff"
    Description = "REQUIRED after finishing your work in this step. Pass your progress summary to the next step. Use complete() instead if the entire goal is achieved."
    Parameters  = Map.ofList [
        "message", { Type = JsString; Description = "What you completed in this step and where results are saved"; Required = true }
    ]
    ConcurrencySafe = false
}

let private completeSpec : ToolSpec = {
    Name        = ToolName "complete"
    Description = "The ENTIRE goal is achieved. Call this only when nothing remains."
    Parameters  = Map.ofList [
        "summary", { Type = JsString; Description = "Final result summary of the entire task"; Required = true }
    ]
    ConcurrencySafe = false
}

let private executeHandoff (store: SignalStore) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "message" args with
        | Error e -> return ToolFailure e
        | Ok msg ->
            store.SignalType <- Some "handoff"
            store.Payload <- msg
            return ToolSuccess "Progress recorded. The next step will continue from here."
    }

let private executeComplete (store: SignalStore) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "summary" args with
        | Error e -> return ToolFailure e
        | Ok summary ->
            store.SignalType <- Some "complete"
            store.Payload <- summary
            return ToolSuccess "Task marked as complete."
    }

// ── Subagent step prompt ─────────────────────────────────────────────────

let private stepBudget = 8

let private longTaskSystemPrompt = """You are one step in a chain working toward a goal.

1. Check the filesystem to see what's already done.
2. Do the next piece of work. Write results to files as you go — do NOT just collect information without producing output.
3. When done with your chunk, call handoff() with a brief summary. If the entire goal is finished, call complete() instead.

IMPORTANT: Write output to files early and often. If you run out of tool calls, only what's on the filesystem survives."""

let private buildUserMessage (goal: string) (step: int) (handoff: string) : string =
    let budgetNote =
        $"\n\n---\nStep {step + 1}. You have {stepBudget} tool calls total. Call handoff() or complete() before you run out."
    if step = 0 then
        goal + budgetNote
    else
        $"{goal}\n\n## Previous Progress\n{handoff}{budgetNote}"

// ── Orchestrator ─────────────────────────────────────────────────────────

/// The callback type for running a subagent step.
/// Takes: extra tool pairs, system prompt, user message → Result<string, AgentError>
type RunSubagentStep = (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list -> string -> string -> Async<Result<string, AgentError>>

/// Run a single subagent step with signal tools injected.
/// Returns (signalType option, payload, finalContent).
let private runStep
    (runSubagentStep : RunSubagentStep)
    (goal            : string)
    (step            : int)
    (handoff         : string)
    : Async<Result<string option * string * string, AgentError>> =
    async {
        let signalStore = createSignalStore ()
        let signalTools : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list = [
            handoffSpec, executeHandoff signalStore
            completeSpec, executeComplete signalStore
        ]
        let userMsg = buildUserMessage goal step handoff

        let! result = runSubagentStep signalTools longTaskSystemPrompt userMsg
        match result with
        | Result.Ok finalContent ->
            return Result.Ok (signalStore.SignalType, signalStore.Payload, finalContent)
        | Result.Error e ->
            return Result.Error e
    }

/// Execute the long task: loop through steps until complete or max_steps.
/// Rule engine (if available) monitors step patterns and can abort early.
let private executeLongTask
    (runSubagentStep : RunSubagentStep)
    (ruleEngine      : BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option)
    (goal            : string)
    (maxSteps        : int)
    : Async<ToolResult> =
    async {
        let mutable handoff = ""
        let mutable step = 0
        let mutable isDone = false
        let mutable finalResult = ""

        while step < maxSteps && not isDone do
            eprintfn "[long_task] Step %d/%d" (step + 1) maxSteps
            let! stepResult = runStep runSubagentStep goal step handoff
            match stepResult with
            | Result.Error e ->
                // Assert step failure into rule engine
                ruleEngine |> Option.iter (fun eng ->
                    BotSharp.Infrastructure.Rules.RuleEngine.assertLongTaskStep eng step "none" 0 "error")
                if handoff <> "" then
                    finalResult <- $"Long task failed at step {step + 1}/{maxSteps}. Last progress:\n{handoff}"
                else
                    finalResult <- $"Long task failed at step {step + 1}/{maxSteps}."
                isDone <- true
            | Result.Ok (signalType, payload, content) ->
                let signal, newHandoff =
                    match signalType with
                    | Some "complete" ->
                        eprintfn "[long_task] Completed at step %d" (step + 1)
                        finalResult <- payload
                        isDone <- true
                        "complete", payload
                    | Some "handoff" ->
                        eprintfn "[long_task] Handoff at step %d: %s" (step + 1) (if payload.Length > 100 then payload.[..99] + "..." else payload)
                        handoff <- payload
                        "handoff", payload
                    | _ ->
                        eprintfn "[long_task] Step %d auto-extract (no signal called)" (step + 1)
                        handoff <- content
                        "none", content
                // Assert step into rule engine and check for early abort
                ruleEngine |> Option.iter (fun eng ->
                    BotSharp.Infrastructure.Rules.RuleEngine.assertLongTaskStep eng step signal newHandoff.Length "ok"
                    let actions = BotSharp.Infrastructure.Rules.RuleEngine.evaluate eng
                    match actions |> List.tryPick (function BotSharp.Infrastructure.Rules.RuleEngine.StopLoop r -> Some r | _ -> None) with
                    | Some reason ->
                        eprintfn "[RuleEngine] %s" reason
                        if handoff <> "" then
                            finalResult <- $"{reason}\nLast progress:\n{handoff}"
                        else
                            finalResult <- reason
                        isDone <- true
                    | None -> ())
                step <- step + 1

        if not isDone then
            return ToolSuccess $"Long task reached max steps ({maxSteps}). Last progress:\n{handoff}"
        else
            return ToolSuccess finalResult
    }

// ── Tool spec and wiring ─────────────────────────────────────────────────

let longTaskSpec : ToolSpec = {
    Name        = ToolName "long_task"
    Description = "Execute a long-running task that cannot fit in a single context window. The work is broken into sequential steps, each starting fresh with the original goal and progress from the previous step. Use for batch processing, large-scale refactoring, or any multi-step task where you might lose track of the goal. For simple independent tasks, use spawn instead."
    Parameters  = Map.ofList [
        "goal",      { Type = JsString;  Description = "Description of the task to complete"; Required = true }
        "max_steps", { Type = JsNumber;  Description = "Maximum number of subagent steps (default 20, max 100)"; Required = false }
    ]
    ConcurrencySafe = false
}

/// Execute the long_task tool. `runSubagentStep` is provided by SubagentManager.
let executeLongTaskTool
    (runSubagentStep : RunSubagentStep)
    (ruleEngine      : BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option)
    (args            : Map<string, JsonElement>)
    : Async<ToolResult> =
    async {
        match requireStringArg "goal" args with
        | Error e -> return ToolFailure e
        | Ok goal ->
            let maxSteps =
                tryIntArg "max_steps" args
                |> Option.defaultValue 20
                |> max 1
                |> min 100
            return! executeLongTask runSubagentStep ruleEngine goal maxSteps
    }

/// All long_task tools as a (spec, execute) pair.
let allTools
    (runSubagentStep : RunSubagentStep)
    (ruleEngine      : BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ longTaskSpec, executeLongTaskTool runSubagentStep ruleEngine ]
