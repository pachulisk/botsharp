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

;; ═══════════════════════════════════════════════════════════════
;; Tool failure rules
;; ═══════════════════════════════════════════════════════════════

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

// ── Evaluation ───────────────────────────────────────────────────────────

/// Run the inference engine and extract any triggered actions.
let evaluate (engine: RuleEngine) : RuleAction list =
    // Run all rules until agenda is empty
    let _ = run engine.Env -1L
    // Extract action facts
    let actions = queryActionFacts engine.Env
    actions |> List.choose (fun (typ, reason, tool) ->
        match typ with
        | "stop-loop"      -> Some (StopLoop reason)
        | "inject-prompt"  -> Some (InjectPrompt reason)
        | "skip-tool"      -> Some (SkipTool tool)
        | _                -> None)

// ── Turn management ──────────────────────────────────────────────────────

/// Reset facts for a new turn (rules are preserved).
let resetTurn (engine: RuleEngine) : unit =
    reset engine.Env
    engine.TurnActive <- true
