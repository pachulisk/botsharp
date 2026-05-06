// Formal verification of the Nanobot agent state machine.
// Corresponds to Domain/StateMachine.fs.
//
// Proves:
//   1. FinalizingReachability  — Finalizing is only reached from AwaitingLLM or ExecutingTools
//   2. IterationMonotonicity   — Each tool-round increments the iteration counter
//   3. LoopTermination         — Iteration counter at/above limit always terminates
//   4. TwoHopTermination       — AwaitingLLM(≥Max) → ExecutingTools → Finalizing in two hops

module StateMachine {

  datatype AgentState =
    | Idle
    | BuildingPrompt
    | AwaitingLLM(iter: nat)
    | ExecutingTools(iter: nat)
    | Consolidating
    | Finalizing(response: string)

  datatype AgentEvent =
    | MessageReceived
    | PromptBuilt
    | LlmRespondedWithText(content: string)
    | LlmRespondedWithTools
    | ToolsExecuted
    | ResponseSent

  const MaxIterations: nat := 40

  function Transition(state: AgentState, event: AgentEvent): AgentState {
    match (state, event) {
      case (Idle,              MessageReceived)          => BuildingPrompt
      case (BuildingPrompt,    PromptBuilt)              => AwaitingLLM(0)
      case (AwaitingLLM(_),   LlmRespondedWithText(c))  => Finalizing(c)
      case (AwaitingLLM(i),   LlmRespondedWithTools)    => ExecutingTools(i)
      case (ExecutingTools(i), ToolsExecuted) =>
           if i < MaxIterations then AwaitingLLM(i + 1)
           else Finalizing("max iterations reached")
      case (Finalizing(_),    ResponseSent)              => Idle
      case _                                             => state
    }
  }

  // Lemma 1: Finalizing is only reachable from AwaitingLLM or ExecutingTools
  //           (when the current state is not already Finalizing).
  //           The wildcard arm returns `state` unchanged, so a non-Finalizing state
  //           cannot spontaneously become Finalizing.
  lemma FinalizingReachability(state: AgentState, event: AgentEvent)
    requires !state.Finalizing?
    ensures Transition(state, event).Finalizing? ==>
            state.AwaitingLLM? || state.ExecutingTools?
  {}

  // Lemma 2: Each ToolsExecuted step strictly increments the iteration counter.
  lemma IterationMonotonicity(iter: nat)
    requires iter < MaxIterations
    ensures var next := Transition(ExecutingTools(iter), ToolsExecuted);
            next.AwaitingLLM? && next.iter == iter + 1
  {}

  // Lemma 3: When the counter is at or above the limit, the loop must terminate.
  lemma LoopTermination(iter: nat)
    requires iter >= MaxIterations
    ensures Transition(ExecutingTools(iter), ToolsExecuted).Finalizing?
  {}

  // Lemma 4: Two-hop termination — AwaitingLLM(≥Max) leads to Finalizing in two transitions.
  lemma TwoHopTermination(iter: nat)
    requires iter >= MaxIterations
    ensures Transition(
              Transition(AwaitingLLM(iter), LlmRespondedWithTools),
              ToolsExecuted).Finalizing?
  {}
}
