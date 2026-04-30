module BotSharp.Infrastructure.Rules.RuleEngine

open System
open System.IO
open BotSharp.Infrastructure.Rules.ClipsEnvironment

// ═══════════════════════════════════════════════════════════════════════════
// Agent-loop-specific CLIPS rule engine
//
// Wraps ClipsEnvironment with domain-specific fact assertion and action
// extraction. Rules are loaded from:
//   1. Built-in rules (embedded resource)
//   2. User rules from {workspace}/rules/*.clp
//
// The engine is created once per process and reused across turns.
// Facts are cleared between turns; rules persist.
// ═══════════════════════════════════════════════════════════════════════════

/// Actions that rules can trigger.
type RuleAction =
    | StopLoop of reason: string
    | InjectPrompt of text: string
    | SkipTool of toolName: string
    | StripReasoning of reason: string
    | AllowFallback of reason: string
    | BlockFallback of reason: string

type RuleEngine = {
    Env : ClipsEnv
    mutable TurnActive : bool
}

// ── Built-in rules ───────────────────────────────────────────────────────

let private builtinRules = """
;; BotSharp built-in agent loop rules (CLIPS 6.4)
;; ALL templates must be declared before ANY rules.

;; ═══════════════════════════════════════════════════════════════
;; Fact templates
;; ═══════════════════════════════════════════════════════════════

(deftemplate tool-result
  (slot tool (type STRING))
  (slot status (type STRING))
  (slot error (type STRING))
  (slot iteration (type INTEGER)))

(deftemplate iteration-info
  (slot count (type INTEGER)))

(deftemplate action
  (slot type (type STRING))
  (slot reason (type STRING))
  (slot tool (type STRING)))

(deftemplate llm-response
  (slot iteration (type INTEGER))
  (slot status (type STRING))
  (slot finish-reason (type STRING))
  (slot error-code (type STRING))
  (slot tokens-used (type INTEGER)))

(deftemplate tool-timeout
  (slot tool (type STRING))
  (slot iteration (type INTEGER))
  (slot duration-sec (type FLOAT)))

(deftemplate config-issue
  (slot cfg_field (type STRING))
  (slot severity (type STRING))
  (slot message (type STRING)))

(deftemplate long-task-step
  (slot step (type INTEGER))
  (slot signal (type STRING))
  (slot handoff-length (type INTEGER))
  (slot status (type STRING)))

(deftemplate inter-agent-response
  (slot content (type STRING)))

(deftemplate provider-fallback
  (slot from-provider (type STRING))
  (slot to-provider (type STRING))
  (slot from-thinking-style (type STRING))
  (slot to-thinking-style (type STRING)))

;; LLM error that may trigger a fallback to another provider.
(deftemplate llm-error
  (slot provider (type STRING))
  (slot error-kind (type STRING))
  (slot error-message (type STRING)))

;; Secret detected in tool output (for alerting).
(deftemplate secret-detected
  (slot tool (type STRING))
  (slot pattern (type STRING))
  (slot iteration (type INTEGER)))

;; Tool call signature for deduplication within a turn.
(deftemplate tool-call-sig
  (slot tool (type STRING))
  (slot signature (type STRING))
  (slot count (type INTEGER)))

;; Session clear request — used by /clear to check safety.
(deftemplate session-clear-request
  (slot unconsolidated-count (type INTEGER)))

;; Session message truncation event — tracks when max_messages cuts history.
(deftemplate session-truncated
  (slot total-messages (type INTEGER))
  (slot kept-messages (type INTEGER))
  (slot dropped-messages (type INTEGER)))

;; Subagent iteration budget — tracks when a subagent exhausts its iterations.
(deftemplate subagent-budget-exhausted
  (slot task-id (type STRING))
  (slot max-iterations (type INTEGER)))

;; ═══════════════════════════════════════════════════════════════
;; Tool failure rules
;; ═══════════════════════════════════════════════════════════════

;; /clear with large unconsolidated history — warn about data loss.
;; If > 20 unconsolidated messages, force consolidation first.
(defrule clear-with-unarchived-history
  (declare (salience 20))
  (session-clear-request (unconsolidated-count ?n&:(> ?n 20)))
  (not (action (type "block-fallback")))
  =>
  (assert (action (type "inject-prompt")
                  (reason (str-cat "Session has " ?n " unconsolidated messages. Archiving to MEMORY.md first."))
                  (tool ""))))

;; Session history heavily truncated by max_messages — trigger consolidation.
;; If more than half the messages were dropped, the session is growing
;; faster than it's being consolidated.
(defrule session-truncation-pressure
  (session-truncated (total-messages ?total) (dropped-messages ?dropped&:(> ?dropped (div ?total 2))))
  (not (action (type "inject-prompt")))
  =>
  (assert (action (type "inject-prompt")
                  (reason (str-cat "Session truncated: " ?dropped "/" ?total " messages dropped by max_messages cap. Consider consolidating."))
                  (tool ""))))

;; Subagent exhausted its iteration budget — log for observability.
;; If this happens repeatedly, the subagent_max_iterations may be too low.
(defrule subagent-budget-warning
  (subagent-budget-exhausted (task-id ?tid) (max-iterations ?n))
  =>
  (printout t "[RuleEngine] Subagent " ?tid " exhausted " ?n " iterations" crlf))

;; Workspace violation: agent tried to access files outside workspace.
;; Stop immediately — continuing lets the agent try to bypass the restriction.
;; (Port of nanobot#3493)
(defrule workspace-violation-stop
  (declare (salience 20))
  (tool-result (tool ?t) (status "failure") (error ?e&:(str-index "Access denied" ?e)))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason (str-cat "Workspace violation by " ?t ": " ?e " - stopping to prevent bypass attempts"))
                  (tool ?t))))

;; Same tool fails 3+ times with identical non-empty error.
(defrule repeated-tool-failure
  (tool-result (tool ?t) (status "failure") (error ?e&~"") (iteration ?i1))
  (tool-result (tool ?t) (status "failure") (error ?e) (iteration ?i2&:(> ?i2 ?i1)))
  (tool-result (tool ?t) (status "failure") (error ?e) (iteration ?i3&:(> ?i3 ?i2)))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason (str-cat "Tool '" ?t "' failed 3 times with: " ?e))
                  (tool ?t))))

;; Same tool called 5 consecutive iterations (regardless of success/failure).
(defrule excessive-tool-calls
  (tool-result (tool ?t) (iteration ?i1))
  (tool-result (tool ?t) (iteration ?i2&:(= ?i2 (+ ?i1 1))))
  (tool-result (tool ?t) (iteration ?i3&:(= ?i3 (+ ?i2 1))))
  (tool-result (tool ?t) (iteration ?i4&:(= ?i4 (+ ?i3 1))))
  (tool-result (tool ?t) (iteration ?i5&:(= ?i5 (+ ?i4 1))))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason (str-cat "Tool '" ?t "' called 5 consecutive iterations"))
                  (tool ?t))))

;; Same tool timed out 3 times — external service likely down.
(defrule repeated-tool-timeout
  (tool-timeout (tool ?t) (iteration ?i1))
  (tool-timeout (tool ?t) (iteration ?i2&:(> ?i2 ?i1)))
  (tool-timeout (tool ?t) (iteration ?i3&:(> ?i3 ?i2)))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason (str-cat "Tool '" ?t "' timed out 3 times - external service may be down"))
                  (tool ?t))))

;; ═══════════════════════════════════════════════════════════════
;; LLM response rules
;; ═══════════════════════════════════════════════════════════════

;; LLM returned empty content 3 consecutive times — provider issue.
(defrule consecutive-empty-responses
  (llm-response (status "empty") (iteration ?i1))
  (llm-response (status "empty") (iteration ?i2&:(> ?i2 ?i1)))
  (llm-response (status "empty") (iteration ?i3&:(> ?i3 ?i2)))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "LLM returned empty response 3 consecutive times")
                  (tool ""))))

;; Rate limited 3 consecutive times — pause to avoid quota exhaustion.
(defrule rate-limit-storm
  (llm-response (error-code "429") (iteration ?i1))
  (llm-response (error-code "429") (iteration ?i2&:(> ?i2 ?i1)))
  (llm-response (error-code "429") (iteration ?i3&:(> ?i3 ?i2)))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "Rate limited 3 consecutive times - pausing to avoid quota exhaustion")
                  (tool ""))))

;; Context window exceeded — trigger emergency consolidation.
(defrule context-too-long
  (llm-response (error-code "413") (iteration ?i))
  (not (action (type "inject-prompt")))
  =>
  (assert (action (type "inject-prompt")
                  (reason "Context window exceeded; triggering emergency consolidation")
                  (tool ""))))

;; ═══════════════════════════════════════════════════════════════
;; Configuration validation rules
;; ═══════════════════════════════════════════════════════════════

;; max_tokens exceeds context_window — impossible budget.
(defrule impossible-token-budget
  (config-issue (cfg_field "max_tokens") (severity "error") (message ?m))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason ?m)
                  (tool ""))))

;; ═══════════════════════════════════════════════════════════════
;; Long-task step rules
;; ═══════════════════════════════════════════════════════════════

;; 3 consecutive steps failed — abort long task.
(defrule long-task-consecutive-failures
  (long-task-step (step ?s1) (status "error"))
  (long-task-step (step ?s2&:(= ?s2 (+ ?s1 1))) (status "error"))
  (long-task-step (step ?s3&:(= ?s3 (+ ?s2 1))) (status "error"))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "Long task: 3 consecutive steps failed")
                  (tool "long_task"))))

;; 3 consecutive steps with no signal (no handoff/complete called) — stalled.
(defrule long-task-no-signal-stall
  (long-task-step (step ?s1) (signal "none"))
  (long-task-step (step ?s2&:(= ?s2 (+ ?s1 1))) (signal "none"))
  (long-task-step (step ?s3&:(= ?s3 (+ ?s2 1))) (signal "none"))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "Long task: 3 consecutive steps produced no handoff/complete signal — subagent may be stuck")
                  (tool "long_task"))))

;; Handoff content shrinking: progress getting shorter each step (losing context).
;; Step N has >0 chars, step N+1 has less than 1/3 of N, step N+2 has less than 1/2 of N+1.
(defrule long-task-shrinking-handoff
  (long-task-step (step ?s1) (signal "handoff") (handoff-length ?h1&:(> ?h1 30)))
  (long-task-step (step ?s2&:(= ?s2 (+ ?s1 1))) (signal "handoff") (handoff-length ?h2&:(< ?h2 (integer (/ ?h1 3)))))
  (long-task-step (step ?s3&:(= ?s3 (+ ?s2 1))) (signal "handoff") (handoff-length ?h3&:(< ?h3 (integer (/ ?h2 2)))))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "Long task: handoff summaries shrinking rapidly - subagent losing track of progress")
                  (tool "long_task"))))

;; ═══════════════════════════════════════════════════════════════
;; Inter-agent consensus detection
;; Users can add custom signal words via workspace/rules/*.clp
;; ═══════════════════════════════════════════════════════════════

;; Chinese consensus signals
(defrule inter-agent-consensus-zh
  (inter-agent-response (content ?c&:(or (str-index "最终方案" ?c) (str-index "讨论结束" ?c) (str-index "达成共识" ?c) (str-index "已确认" ?c))))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "consensus-reached")
                  (tool "interagent"))))

;; English consensus signals
(defrule inter-agent-consensus-en
  (inter-agent-response (content ?c&:(or (str-index "final proposal" ?c) (str-index "discussion complete" ?c) (str-index "consensus reached" ?c) (str-index "DISCUSSION_COMPLETE" ?c))))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason "consensus-reached")
                  (tool "interagent"))))

;; ═══════════════════════════════════════════════════════════════
;; Provider fallback reasoning compatibility
;; ═══════════════════════════════════════════════════════════════

;; When falling back from provider A to provider B, strip reasoning_content
;; if the thinking styles are different (incompatible formats).
;; Same style (e.g. both "ReasoningSplit") → keep reasoning.
;; Different styles or one is "None" → strip reasoning.
(defrule fallback-strip-reasoning
  (provider-fallback (from-provider ?p1) (to-provider ?p2&~?p1)
                     (from-thinking-style ?s1) (to-thinking-style ?s2&~?s1))
  (not (action (type "strip-reasoning")))
  =>
  (assert (action (type "strip-reasoning")
                  (reason (str-cat "Reasoning incompatible: " ?p1 " (" ?s1 ") -> " ?p2 " (" ?s2 ")"))
                  (tool ""))))

;; Same thinking style between different providers: still strip because
;; providers may reject reasoning_content from other providers even if
;; the format is the same (e.g. DeepSeek rejects MiMo's reasoning_content).
(defrule fallback-strip-reasoning-cross-provider
  (provider-fallback (from-provider ?p1) (to-provider ?p2&~?p1)
                     (from-thinking-style "ReasoningSplit") (to-thinking-style "ReasoningSplit"))
  (not (action (type "strip-reasoning")))
  =>
  (assert (action (type "strip-reasoning")
                  (reason (str-cat "Cross-provider ReasoningSplit: " ?p1 " -> " ?p2 " (different provider, strip to be safe)"))
                  (tool ""))))

;; Same provider fallback (e.g. two DeepSeek instances with different base URLs):
;; keep reasoning_content since the format is guaranteed compatible.
;; This rule has higher salience so it fires first and blocks strip-reasoning.
(defrule fallback-keep-reasoning-same-provider
  (declare (salience 10))
  (provider-fallback (from-provider ?p) (to-provider ?p))
  =>
  (assert (action (type "keep-reasoning")
                  (reason (str-cat "Same provider " ?p " -> " ?p ": keep reasoning_content"))
                  (tool ""))))

;; ═══════════════════════════════════════════════════════════════
;; Fallback eligibility rules
;;
;; Determines whether a given LLM error should trigger a provider
;; fallback. Errors that would affect ALL providers the same way
;; (e.g. context too long) should NOT trigger fallback.
;;
;; Action type "allow-fallback": the error is eligible for fallback.
;; Action type "block-fallback": the error is NOT eligible (with reason).
;; If neither fires, default behavior is to allow fallback.
;; ═══════════════════════════════════════════════════════════════

;; ContextTooLong: same messages will fail on any provider → block.
(defrule fallback-block-context-too-long
  (declare (salience 10))
  (llm-error (error-kind "ContextTooLong"))
  (not (action (type "block-fallback")))
  =>
  (assert (action (type "block-fallback")
                  (reason "Context too long - all providers will fail with the same messages")
                  (tool ""))))

;; EmptyResponse: usually misconfigured endpoint → block.
;; Switching provider won't help if base_url is wrong.
(defrule fallback-block-empty-response
  (declare (salience 10))
  (llm-error (error-kind "EmptyResponse"))
  (not (action (type "block-fallback")))
  =>
  (assert (action (type "block-fallback")
                  (reason "Empty response - likely misconfigured endpoint, fallback unlikely to help")
                  (tool ""))))

;; RateLimited: another provider has separate quota → allow.
(defrule fallback-allow-rate-limited
  (llm-error (error-kind "RateLimited"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Rate limited - fallback provider has separate quota")
                  (tool ""))))

;; QuotaExceeded: billing limit on this provider only → allow.
(defrule fallback-allow-quota-exceeded
  (llm-error (error-kind "QuotaExceeded"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Quota exceeded - fallback provider has separate billing")
                  (tool ""))))

;; ServerError: transient on this provider → allow.
(defrule fallback-allow-server-error
  (llm-error (error-kind "ServerError"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Server error - fallback provider may be healthy")
                  (tool ""))))

;; Timeout: this provider is slow/down → allow.
(defrule fallback-allow-timeout
  (llm-error (error-kind "Timeout"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Timeout - fallback provider may respond faster")
                  (tool ""))))

;; ModelNotFound: this provider doesn't have the model → allow.
(defrule fallback-allow-model-not-found
  (llm-error (error-kind "ModelNotFound"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Model not found - fallback provider may support it")
                  (tool ""))))

;; ConnectionFailed: auth issue on this provider → allow (other has own key).
(defrule fallback-allow-connection-failed
  (llm-error (error-kind "ConnectionFailed"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Connection failed - fallback provider has separate credentials")
                  (tool ""))))

;; MalformedResponse: request format issue → allow cautiously.
;; Different providers may accept different formats.
(defrule fallback-allow-malformed-response
  (llm-error (error-kind "MalformedResponse"))
  (not (action (type "allow-fallback")))
  =>
  (assert (action (type "allow-fallback")
                  (reason "Malformed response - fallback provider may handle the format differently")
                  (tool ""))))

;; ═══════════════════════════════════════════════════════════════
;; Secret detection alerting
;; When a tool output contains an API key pattern, log a warning.
;; The actual redaction is done in F# code (regex); this rule
;; provides observability into when redaction occurred.
;; ═══════════════════════════════════════════════════════════════

(defrule secret-leak-detected
  (secret-detected (tool ?t) (pattern ?p) (iteration ?i))
  =>
  (printout t "[RuleEngine] Secret redacted in " ?t " output (pattern: " ?p ", iteration: " ?i ")" crlf))

;; ═══════════════════════════════════════════════════════════════
;; Spawn deduplication
;; If the same tool+signature appears with count > 1, skip it.
;; The F# code increments count when asserting duplicate signatures.
;; ═══════════════════════════════════════════════════════════════

(defrule duplicate-tool-call
  (tool-call-sig (tool ?t) (signature ?s) (count ?c&:(> ?c 1)))
  (not (action (type "skip-tool")))
  =>
  (assert (action (type "skip-tool")
                  (reason (str-cat "Duplicate " ?t " call: " ?s))
                  (tool ?t))))
"""

// ── Lifecycle ────────────────────────────────────────────────────────────

/// Create a rule engine. Loads built-in rules + user rules from workspace.
/// Throws if CLIPS native library is not available.
let create (workspacePath: string) : RuleEngine =
    let env = ClipsEnvironment.create ()
    // Load built-in rules. Split by construct so one bad rule doesn't block others.
    let constructs =
        builtinRules.Split([| "(def" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun s -> "(def" + s.TrimEnd())
        |> Array.filter (fun s -> s.Length > 10 && (s.StartsWith("(deftemplate") || s.StartsWith("(defrule")))
    for construct in constructs do
        match loadFromString env construct with
        | Ok ()     -> ()
        | Error msg -> eprintfn "[RuleEngine] Warning: failed to load construct: %s" msg
    // Load user rules from workspace/rules/*.clp
    let rulesDir = Path.Combine(workspacePath, "rules")
    if Directory.Exists rulesDir then
        for clpFile in Directory.EnumerateFiles(rulesDir, "*.clp") do
            match loadFile env clpFile with
            | Ok ()     -> printfn "[RuleEngine] Loaded user rules: %s" (Path.GetFileName clpFile)
            | Error msg -> eprintfn "[RuleEngine] Warning: %s" msg
    // Reset to activate initial-fact
    reset env
    { Env = env; TurnActive = false }

/// Dispose the engine and free native resources.
let dispose (engine: RuleEngine) : unit =
    destroy engine.Env

// ── Fact assertion ───────────────────────────────────────────────────────

/// Escape a string for CLIPS fact assertion (double quotes inside strings).
let private escapeClips (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// Assert a tool result fact.
let assertToolResult
    (engine    : RuleEngine)
    (tool      : string)
    (status    : string)
    (error     : string)
    (iteration : int)
    : unit =
    let factStr =
        sprintf "(tool-result (tool \"%s\") (status \"%s\") (error \"%s\") (iteration %d))"
            (escapeClips tool) (escapeClips status) (escapeClips error) iteration
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert failed: %s" msg

/// Assert iteration metadata.
let assertIteration (engine: RuleEngine) (iter: int) : unit =
    let factStr = sprintf "(iteration-info (count %d))" iter
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert iteration failed: %s" msg

/// Assert an LLM response fact (status, error code, finish reason, token usage).
let assertLlmResponse
    (engine       : RuleEngine)
    (status       : string)
    (errorCode    : string)
    (finishReason : string)
    (tokensUsed   : int)
    (iteration    : int)
    : unit =
    let factStr =
        sprintf "(llm-response (iteration %d) (status \"%s\") (error-code \"%s\") (finish-reason \"%s\") (tokens-used %d))"
            iteration (escapeClips status) (escapeClips errorCode) (escapeClips finishReason) tokensUsed
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert llm-response failed: %s" msg

/// Assert a tool timeout fact.
let assertToolTimeout
    (engine      : RuleEngine)
    (tool        : string)
    (iteration   : int)
    (durationSec : float)
    : unit =
    let factStr =
        sprintf "(tool-timeout (tool \"%s\") (iteration %d) (duration-sec %f))"
            (escapeClips tool) iteration durationSec
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert tool-timeout failed: %s" msg

/// Assert a configuration issue fact.
let assertConfigIssue
    (engine   : RuleEngine)
    (field    : string)
    (severity : string)
    (message  : string)
    : unit =
    let factStr =
        sprintf "(config-issue (cfg_field \"%s\") (severity \"%s\") (message \"%s\"))"
            (escapeClips field) (escapeClips severity) (escapeClips message)
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert config-issue failed: %s" msg

/// Assert a long-task step fact (used by LongTaskTool orchestrator).
let assertLongTaskStep
    (engine        : RuleEngine)
    (step          : int)
    (signal        : string)   // "handoff" | "complete" | "none"
    (handoffLength : int)
    (status        : string)   // "ok" | "error"
    : unit =
    let factStr =
        sprintf "(long-task-step (step %d) (signal \"%s\") (handoff-length %d) (status \"%s\"))"
            step (escapeClips signal) handoffLength (escapeClips status)
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert long-task-step failed: %s" msg

/// Assert a provider fallback fact for reasoning compatibility check.
let assertProviderFallback
    (engine            : RuleEngine)
    (fromProvider      : string)
    (toProvider        : string)
    (fromThinkingStyle : string)
    (toThinkingStyle   : string)
    : unit =
    let factStr =
        sprintf "(provider-fallback (from-provider \"%s\") (to-provider \"%s\") (from-thinking-style \"%s\") (to-thinking-style \"%s\"))"
            (escapeClips fromProvider) (escapeClips toProvider) (escapeClips fromThinkingStyle) (escapeClips toThinkingStyle)
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert provider-fallback failed: %s" msg

/// Assert a secret-detected fact (for observability/alerting).
let assertSecretDetected (engine: RuleEngine) (tool: string) (pattern: string) (iteration: int) : unit =
    let factStr =
        sprintf "(secret-detected (tool \"%s\") (pattern \"%s\") (iteration %d))"
            (escapeClips tool) (escapeClips pattern) iteration
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert secret-detected failed: %s" msg

/// Assert a tool call signature for dedup. Returns true if this is a duplicate.
let assertToolCallSig (engine: RuleEngine) (tool: string) (signature: string) (count: int) : unit =
    let factStr =
        sprintf "(tool-call-sig (tool \"%s\") (signature \"%s\") (count %d))"
            (escapeClips tool) (escapeClips signature) count
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert tool-call-sig failed: %s" msg

// ── Evaluation ───────────────────────────────────────────────────────────

/// Run the inference engine and extract any triggered actions.
let evaluate (engine: RuleEngine) : RuleAction list =
    // Run all rules until agenda is empty
    let _ = run engine.Env -1L
    // Extract action facts
    let actions = queryActionFacts engine.Env
    actions |> List.choose (fun (typ, reason, tool) ->
        match typ with
        | "stop-loop"        -> Some (StopLoop reason)
        | "inject-prompt"    -> Some (InjectPrompt reason)
        | "skip-tool"        -> Some (SkipTool tool)
        | "strip-reasoning"  -> Some (StripReasoning reason)
        | "keep-reasoning"   -> None   // explicitly no action needed
        | "allow-fallback"   -> Some (AllowFallback reason)
        | "block-fallback"   -> Some (BlockFallback reason)
        | _                -> None)

/// Assert a session-truncated fact when max_messages cuts history.
let assertSessionTruncated (engine: RuleEngine) (totalMessages: int) (keptMessages: int) : unit =
    let dropped = totalMessages - keptMessages
    let factStr =
        sprintf "(session-truncated (total-messages %d) (kept-messages %d) (dropped-messages %d))"
            totalMessages keptMessages dropped
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert session-truncated failed: %s" msg

/// Assert a subagent-budget-exhausted fact.
let assertSubagentBudgetExhausted (engine: RuleEngine) (taskId: string) (maxIterations: int) : unit =
    let factStr =
        sprintf "(subagent-budget-exhausted (task-id \"%s\") (max-iterations %d))"
            (escapeClips taskId) maxIterations
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert subagent-budget-exhausted failed: %s" msg

/// Check if session truncation triggered a consolidation recommendation.
let shouldConsolidateAfterTruncation (engine: RuleEngine) : bool =
    let actions = evaluate engine
    actions |> List.exists (function InjectPrompt r -> r.Contains("truncated") | _ -> false)

/// Check if reasoning_content should be stripped for a provider fallback.
/// Runs the engine and checks for StripReasoning actions.
let shouldStripReasoning (engine: RuleEngine) : bool =
    let actions = evaluate engine
    actions |> List.exists (function StripReasoning _ -> true | _ -> false)

/// Check if a tool call should be skipped (duplicate detected by CLIPS).
let shouldSkipTool (engine: RuleEngine) : string option =
    let actions = evaluate engine
    actions |> List.tryPick (function SkipTool tool -> Some tool | _ -> None)

/// Assert a session-clear-request fact for /clear safety check.
let assertSessionClearRequest (engine: RuleEngine) (unconsolidatedCount: int) : unit =
    let factStr = sprintf "(session-clear-request (unconsolidated-count %d))" unconsolidatedCount
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert session-clear-request failed: %s" msg

/// Check if /clear should force consolidation first (large unarchived history).
let shouldConsolidateBeforeClear (engine: RuleEngine) : bool =
    let actions = evaluate engine
    actions |> List.exists (function InjectPrompt r -> r.Contains("unconsolidated") | _ -> false)

/// Assert an LLM error fact for fallback eligibility evaluation.
let assertLlmError
    (engine       : RuleEngine)
    (providerId   : string)
    (errorKind    : string)
    (errorMessage : string)
    : unit =
    let factStr =
        sprintf "(llm-error (provider \"%s\") (error-kind \"%s\") (error-message \"%s\"))"
            (escapeClips providerId) (escapeClips errorKind) (escapeClips errorMessage)
    match assertFact engine.Env factStr with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Assert llm-error failed: %s" msg

/// Check whether a fallback should be attempted for the given error.
/// Returns true unless a BlockFallback rule fires.
/// Default (no rules matched): allow fallback.
let shouldFallback (engine: RuleEngine) : bool =
    let actions = evaluate engine
    let blocked = actions |> List.exists (function BlockFallback _ -> true | _ -> false)
    if blocked then
        let reason = actions |> List.tryPick (function BlockFallback r -> Some r | _ -> None) |> Option.defaultValue ""
        eprintfn "[RuleEngine] Fallback blocked: %s" reason
    not blocked

// ── Turn management ──────────────────────────────────────────────────────

/// Reset facts for a new turn (rules are preserved).
let resetTurn (engine: RuleEngine) : unit =
    reset engine.Env
    engine.TurnActive <- true
