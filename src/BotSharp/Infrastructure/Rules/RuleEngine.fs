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

;; If the same tool fails 3+ times with the same non-empty error, stop.
(defrule repeated-tool-failure
  (tool-result (tool ?t) (status "failure") (error ?e&~"") (iteration ?i1))
  (tool-result (tool ?t) (status "failure") (error ?e) (iteration ?i2&:(> ?i2 ?i1)))
  (tool-result (tool ?t) (status "failure") (error ?e) (iteration ?i3&:(> ?i3 ?i2)))
  (not (action (type "stop-loop")))
  =>
  (assert (action (type "stop-loop")
                  (reason (str-cat "Tool '" ?t "' failed 3 times with: " ?e))
                  (tool ?t))))

;; If the same tool is called 5+ times in a row (regardless of error), stop.
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
"""

// ── Lifecycle ────────────────────────────────────────────────────────────

/// Create a rule engine. Loads built-in rules + user rules from workspace.
/// Throws if CLIPS native library is not available.
let create (workspacePath: string) : RuleEngine =
    let env = ClipsEnvironment.create ()
    // Load built-in rules
    match loadFromString env builtinRules with
    | Ok ()     -> ()
    | Error msg -> eprintfn "[RuleEngine] Warning: built-in rules failed to load: %s" msg
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
