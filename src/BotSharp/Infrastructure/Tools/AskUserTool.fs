module BotSharp.Infrastructure.Tools.AskUserTool

#nowarn "3261"

open System
open System.Text.Json
open System.Threading.Tasks
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser

// ═══════════════════════════════════════════════════════════════════════════
// ask_user tool — interactive multiple-choice user query
//
// Presents a question with options to the user, pauses the agent loop
// via TaskCompletionSource, and resumes when the user responds.
//
// Channel rendering:
//   CLI       → numbered list (user types number or label)
//   Telegram  → InlineKeyboard buttons
//   Other     → text-based option list
//
// The tool registers a PendingUserQuery on the AgentCoordinator.
// When the user's next message arrives, Route() intercepts it before
// the SessionActor and completes the TCS, unblocking the tool.
// ═══════════════════════════════════════════════════════════════════════════

let askUserToolSpec : ToolSpec = {
    Name        = ToolName "ask_user"
    Description =
        "Present a question with options to the user and wait for their selection. " +
        "Use when you need explicit user confirmation or a choice between alternatives. " +
        "The user will see clickable buttons (Telegram) or a numbered list (CLI). " +
        "Returns the selected option text, or a timeout error if the user doesn't respond."
    Parameters  = Map.ofList [
        "question", { Type = JsString
                      Description = "The question text to display"
                      Required = true }
        "options",  { Type = JsArray JsString
                      Description = "List of option labels (2-10 items)"
                      Required = true }
        "timeout",  { Type = JsNumber
                      Description = "Timeout in seconds (default 120, max 600)"
                      Required = false }
    ]
    ConcurrencySafe = false
}

// ── Option matching ─────────────────────────────────────────────────────

/// Match user input against option list. Supports:
///   - Exact text match (case-insensitive)
///   - 1-based numeric index ("2" → second option)
/// Returns the matched option label, or None if no match.
let private matchOption (options: string list) (input: string) : string option =
    let trimmed = input.Trim()
    // Try exact match (case-insensitive)
    match options |> List.tryFind (fun o -> String.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase)) with
    | Some matched -> Some matched
    | None ->
        // Try numeric index (1-based)
        match Int32.TryParse(trimmed) with
        | true, n when n >= 1 && n <= options.Length -> Some options.[n - 1]
        | _ -> None

// ── Option parsing ──────────────────────────────────────────────────────

let private parseOptions (args: Map<string, JsonElement>) : Result<string list, ToolError> =
    match args.TryFind "options" with
    | None -> Result.Error (ParameterMissing "options")
    | Some v ->
        if v.ValueKind <> JsonValueKind.Array then
            Result.Error (ParameterInvalid ("options", "must be an array of strings"))
        else
            let items =
                [ for i in 0 .. v.GetArrayLength() - 1 do
                    let el = v[i]
                    if el.ValueKind = JsonValueKind.String then
                        match el.GetString() with
                        | null -> ()
                        | s when s.Trim() <> "" -> yield s.Trim()
                        | _ -> ()
                ]
            if items.Length < 2 then
                Result.Error (ParameterInvalid ("options", "must have at least 2 options"))
            elif items.Length > 10 then
                Result.Error (ParameterInvalid ("options", "must have at most 10 options"))
            else
                Result.Ok items

// ── Execution ───────────────────────────────────────────────────────────

/// Execute the ask_user tool.
///
/// `registerPending` — registers the TCS on AgentCoordinator so Route() can resolve it.
/// `send`            — sends the OutboundMessage (question + buttons) to the user.
/// `getSessionId`    — returns the current session ID (set before each agent loop run).
/// `getChannel`      — returns the current channel ID.
/// `getChatId`       — returns the current chat ID.
let executeAskUser
    (registerPending : SessionId -> PendingUserQuery -> unit)
    (removePending   : SessionId -> unit)
    (send            : OutboundMessage -> Async<unit>)
    (getSessionId    : unit -> SessionId)
    (getChannel      : unit -> ChannelId)
    (getChatId       : unit -> ChatId)
    (args            : Map<string, JsonElement>)
    : Async<ToolResult> =
    async {
        // Parse arguments
        match requireStringArg "question" args with
        | Result.Error e -> return ToolFailure e
        | Result.Ok question ->

        match parseOptions args with
        | Result.Error e -> return ToolFailure e
        | Result.Ok options ->

        let timeoutSec =
            match args.TryFind "timeout" with
            | Some v when v.ValueKind = JsonValueKind.Number -> min 600 (max 10 (v.GetInt32()))
            | _ -> 120

        let sid = getSessionId ()
        let tcs = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        let query : PendingUserQuery = {
            Question  = question
            Options   = options
            Tcs       = tcs
            TimeoutMs = timeoutSec * 1000
            CreatedAt = DateTimeOffset.UtcNow
        }

        // Send question + buttons to user
        let msg : OutboundMessage = {
            Channel     = getChannel ()
            Chat        = getChatId ()
            Content     = question
            ReplyTo     = None
            Attachments = []
            Buttons     = [ options ]   // single row of buttons
        }
        do! send msg

        // Register pending query (Route() will intercept the user's response)
        registerPending sid query

        // Wait for user response or timeout
        let timeoutTask = Task.Delay(timeoutSec * 1000)
        let! completed = Async.AwaitTask(Task.WhenAny(tcs.Task, timeoutTask))

        if completed = (tcs.Task :> Task) then
            // User responded
            let! response = Async.AwaitTask tcs.Task
            match matchOption options response with
            | Some matched ->
                return ToolSuccess (sprintf "User selected: %s" matched)
            | None ->
                return ToolSuccess (sprintf "User replied: %s (not a listed option)" response)
        else
            // Timeout
            removePending sid
            return ToolFailure (ExecutionTimeout (TimeSpan.FromSeconds(float timeoutSec)))
    }
