module BotSharp.Infrastructure.Tools.TaskTool

open System
open System.Text.Json
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// Task tool — dual agent/user task management
//
// Agent uses task_create/task_update/task_list to plan and track work.
// User uses /task commands to view, create, and manage tasks.
// Tasks are persisted in SQLite across sessions.
// ═══════════════════════════════════════════════════════════════════════════

// ── Tool specs ──────────────────────────────────────────────────────────

let taskCreateSpec : ToolSpec = {
    Name        = ToolName "task_create"
    Description =
        "Create a task to track work progress. Use when starting a multi-step task " +
        "to break it into trackable items. The user can see tasks via /task command."
    Parameters  = Map.ofList [
        "subject", { Type = JsString; Description = "Short task title (imperative form, e.g. 'Implement login API')"; Required = true }
        "description", { Type = JsString; Description = "Detailed description of what needs to be done"; Required = false }
    ]
    ConcurrencySafe = false
}

let taskUpdateSpec : ToolSpec = {
    Name        = ToolName "task_update"
    Description =
        "Update a task's status or subject. Set status to 'in_progress' when starting, " +
        "'completed' when done. Use task_list first to get the task ID."
    Parameters  = Map.ofList [
        "id", { Type = JsString; Description = "Task ID (6-char hex from task_create)"; Required = true }
        "status", { Type = JsEnum ["pending"; "in_progress"; "completed"]; Description = "New status"; Required = false }
        "subject", { Type = JsString; Description = "Updated subject text"; Required = false }
    ]
    ConcurrencySafe = false
}

let taskListSpec : ToolSpec = {
    Name        = ToolName "task_list"
    Description = "List current tasks with their status. Use to check progress before updating."
    Parameters  = Map.ofList [
        "status", { Type = JsEnum ["all"; "pending"; "in_progress"; "completed"]
                    Description = "Filter by status (default: all)"
                    Required = false }
    ]
    ConcurrencySafe = true
}

// ── Formatting ──────────────────────────────────────────────────────────

let formatTaskList (tasks: TaskItem list) : string =
    if tasks.IsEmpty then "(no tasks)"
    else
        let pending   = tasks |> List.filter (fun t -> t.Status = "pending") |> List.length
        let inProg    = tasks |> List.filter (fun t -> t.Status = "in_progress") |> List.length
        let completed = tasks |> List.filter (fun t -> t.Status = "completed") |> List.length
        let header = sprintf "Tasks (%d total: %d pending, %d in_progress, %d completed)" tasks.Length pending inProg completed
        let sep = String.replicate (min 54 header.Length) "\u2500"
        let lines =
            tasks |> List.map (fun t ->
                let icon = match t.Status with "completed" -> "\u2713" | "in_progress" -> "\u25C9" | _ -> "\u25CB"
                let subj = if t.Subject.Length > 40 then t.Subject.[..39] + "..." else t.Subject
                sprintf "  %s %s  %-40s %s" icon t.Id subj t.Status)
        sprintf "%s\n%s\n%s" header sep (String.concat "\n" lines)

// ── Execution ───────────────────────────────────────────────────────────

let executeTaskCreate (openDb: unit -> SqliteConnection) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "subject" args with
        | Result.Error e -> return ToolFailure e
        | Result.Ok subject ->
            let description =
                match args.TryFind "description" with
                | Some v when v.ValueKind = JsonValueKind.String -> match v.GetString() with null | "" -> None | s -> Some s
                | _ -> None
            try
                use conn = openDb ()
                let! id = createTask conn None subject description "agent"
                return ToolSuccess (sprintf "Created task %s: %s" id subject)
            with ex ->
                return ToolFailure (ExecutionFailed ex.Message)
    }

let executeTaskUpdate (openDb: unit -> SqliteConnection) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        match requireStringArg "id" args with
        | Result.Error e -> return ToolFailure e
        | Result.Ok id ->
            let status =
                match args.TryFind "status" with
                | Some v when v.ValueKind = JsonValueKind.String -> match v.GetString() with null | "" -> None | s -> Some s
                | _ -> None
            let subject =
                match args.TryFind "subject" with
                | Some v when v.ValueKind = JsonValueKind.String -> match v.GetString() with null | "" -> None | s -> Some s
                | _ -> None
            if status.IsNone && subject.IsNone then
                return ToolFailure (ParameterMissing "status or subject")
            else
                try
                    use conn = openDb ()
                    let! ok = updateTask conn id status subject
                    if ok then
                        let changes = [
                            match status with Some s -> yield sprintf "status \u2192 %s" s | None -> ()
                            match subject with Some s -> yield sprintf "subject \u2192 %s" s | None -> ()
                        ]
                        return ToolSuccess (sprintf "Updated task %s: %s" id (String.concat ", " changes))
                    else
                        return ToolFailure (ExecutionFailed (sprintf "Task %s not found" id))
                with ex ->
                    return ToolFailure (ExecutionFailed ex.Message)
    }

let executeTaskList (openDb: unit -> SqliteConnection) (args: Map<string, JsonElement>) : Async<ToolResult> =
    async {
        let statusFilter =
            match args.TryFind "status" with
            | Some v when v.ValueKind = JsonValueKind.String -> match v.GetString() with null | "" | "all" -> None | s -> Some s
            | _ -> None
        try
            use conn = openDb ()
            let! tasks = listTasks conn statusFilter 50
            return ToolSuccess (formatTaskList tasks)
        with ex ->
            return ToolFailure (ExecutionFailed ex.Message)
    }

// ── Registration ────────────────────────────────────────────────────────

let allTools (openDb: unit -> SqliteConnection) : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ taskCreateSpec, executeTaskCreate openDb
      taskUpdateSpec, executeTaskUpdate openDb
      taskListSpec,   executeTaskList openDb ]
