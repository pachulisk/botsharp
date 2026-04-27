module BotSharp.Domain.StateMachine

open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Agent state transition function (pure — no IO)
//
// Design constraints:
//   1. No catch-all wildcard branch.
//      FS0025 is configured as a compile error, so adding a new AgentState or
//      AgentEvent case forces the developer to explicitly handle every new
//      (state, event) combination here.
//   2. All "illegal" transitions are listed explicitly, returning the current
//      state unchanged.  This is verbose on purpose — the verbosity is the
//      signal that a state machine redesign may be needed.
// ═══════════════════════════════════════════════════════════════════════════

// The iteration limit is enforced by AgentLoop using config.MaxIterations,
// not by the state machine. The second ToolsExecuted case below (fallback)
// is used when AgentLoop passes an empty result set to force finalization
// (e.g. if the state machine is invoked from tests or for unconditional stop).
let transition (state: AgentState) (event: AgentEvent) : AgentState =
    match state, event with

    // ── Legal transitions ────────────────────────────────────────────────

    // Idle → BuildingPrompt: a new message arrives
    | Idle, MessageReceived _ ->
        BuildingPrompt []

    // BuildingPrompt → AwaitingLLM: prompt has been assembled
    | BuildingPrompt _, PromptBuilt request ->
        AwaitingLLM (request, 0)

    // AwaitingLLM → Finalizing: LLM replied with plain text
    | AwaitingLLM _, LlmRespondedWithText (content, rc) ->
        Finalizing (content, rc)

    // AwaitingLLM → ExecutingTools: LLM requested tool calls
    | AwaitingLLM (req, iter), LlmRespondedWithTools (calls, rc) ->
        let pending = req.Messages @ [ ToolCallMessage (calls, rc) ]
        ExecutingTools (calls, pending, iter)

    // ExecutingTools → AwaitingLLM (results present) or Finalizing (empty stop signal)
    // AgentLoop enforces MaxIterations by going directly to Finalizing; ToolsExecuted []
    // is treated as a forced-stop signal (e.g. for tests or unconditional stop).
    | ExecutingTools (_, pending, iter), ToolsExecuted results ->
        match results with
        | [] ->
            Finalizing ($"(stopped after {iter} iterations)", None)
        | _ ->
            let resultMessages =
                results |> List.map (fun (call, res) ->
                    let content =
                        match res with
                        | ToolSuccess c -> c
                        | ToolFailure e ->
                            match e with
                            | ToolNotFound (ToolName n) -> $"[Tool not found: {n}]"
                            | ParameterMissing f        -> $"[Missing parameter: {f}]"
                            | ParameterInvalid (f, r)   -> $"[Invalid parameter {f}: {r}]"
                            | ExecutionFailed msg       -> $"[Tool failed: {msg}]"
                            | ExecutionTimeout t        -> $"[Tool timed out after {t.TotalSeconds}s]"
                            | WorkspaceViolation p      -> $"[Access denied: {p}]"
                    ToolResultMessage (call.Id, call.Tool, content))
            let updatedMessages = pending @ resultMessages
            AwaitingLLM ({ Messages = updatedMessages
                           Tools    = []        // will be refreshed by ContextBuilder
                           Model    = ""
                           Settings = GenerationSettings.defaults }, iter + 1)

    // Finalizing → Idle: response has been sent downstream
    | Finalizing _, ResponseSent ->
        Idle

    // ── Explicitly listed illegal transitions (no catch-all) ────────────
    // The compiler will warn here if a new state or event is added without
    // being handled.  Each line is a conscious "this should not happen".

    | Idle,           PromptBuilt _             -> state
    | Idle,           LlmRespondedWithText _    -> state
    | Idle,           LlmRespondedWithTools _   -> state
    | Idle,           ToolsExecuted _           -> state
    | Idle,           ResponseSent              -> state

    | BuildingPrompt _, MessageReceived _       -> state
    | BuildingPrompt _, LlmRespondedWithText _  -> state
    | BuildingPrompt _, LlmRespondedWithTools _ -> state
    | BuildingPrompt _, ToolsExecuted _         -> state
    | BuildingPrompt _, ResponseSent            -> state

    | AwaitingLLM _,  MessageReceived _         -> state
    | AwaitingLLM _,  PromptBuilt _             -> state
    | AwaitingLLM _,  ToolsExecuted _           -> state
    | AwaitingLLM _,  ResponseSent              -> state

    | ExecutingTools _, MessageReceived _       -> state
    | ExecutingTools _, PromptBuilt _           -> state
    | ExecutingTools _, LlmRespondedWithText _  -> state
    | ExecutingTools _, LlmRespondedWithTools _ -> state
    | ExecutingTools _, ResponseSent            -> state

    | Consolidating _,  MessageReceived _       -> state
    | Consolidating _,  PromptBuilt _           -> state
    | Consolidating _,  LlmRespondedWithText _  -> state
    | Consolidating _,  LlmRespondedWithTools _ -> state
    | Consolidating _,  ToolsExecuted _         -> state
    | Consolidating _,  ResponseSent            -> state

    | Finalizing _,     MessageReceived _       -> state
    | Finalizing _,     PromptBuilt _           -> state
    | Finalizing _,     LlmRespondedWithText _  -> state
    | Finalizing _,     LlmRespondedWithTools _ -> state
    | Finalizing _,     ToolsExecuted _         -> state
