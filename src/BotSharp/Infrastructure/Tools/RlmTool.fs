module BotSharp.Infrastructure.Tools.RlmTool

#nowarn "3261"

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser
open BotSharp.Infrastructure.Memory.ModelRecommendation

// ═══════════════════════════════════════════════════════════════════════════
// RLM (Recursive Language Model) tool
//
// Port of DeepSeek-TUI's RLM implementation (Zhang et al., arXiv:2512.24601).
// Spawns a Python REPL subprocess with built-in LLM query functions.
// A root LLM generates Python code iteratively; Python code can call back
// into F# for LLM queries via a stdin/stdout line protocol.
//
// Architecture:
//   Agent calls `rlm` tool → content written to temp file →
//   Python REPL spawned → root LLM generates code → code executes →
//   output feeds back → repeat until FINAL() or max iterations.
//
// Built-in REPL functions:
//   llm_query(prompt)           → single LLM call (cheap model)
//   llm_query_batched(prompts)  → parallel LLM calls (max 16)
//   rlm_query(prompt)           → recursive sub-RLM turn (depth-1)
//   rlm_query_batched(prompts)  → parallel recursive sub-RLM turns
//   FINAL(value)                → end RLM turn, return value
//   context / ctx               → the large input content
// ═══════════════════════════════════════════════════════════════════════════

// ── Constants (matching DeepSeek-TUI) ───────────────────────────────────

let [<Literal>] private MaxBatch = 16
let [<Literal>] private ChildTimeoutSecs = 120
let [<Literal>] private DefaultMaxIterations = 25
let [<Literal>] private TurnTimeoutSecs = 180

// ── Child model resolution ──────────────────────────────────────────────

/// Resolve the model for RLM child calls (llm_query).
/// 3-level fallback: RlmChildModel config → Phase1 recommendation (cheap) → DefaultModel.
let resolveRlmChildModel (config: BotSharpConfig) : string =
    config.RlmChildModel
    |> Option.orElseWith (fun () ->
        recommendedModels
        |> Map.tryFind config.DefaultProvider
        |> Option.map (fun struct(p1, _) -> p1))
    |> Option.defaultValue config.DefaultModel

// ── Python bootstrap script ─────────────────────────────────────────────

let private pythonBootstrap = """
import sys, json, os

_SID = os.environ["RLM_SID"]
_MAX_DEPTH = int(os.environ.get("RLM_MAX_DEPTH", "1"))
_DEPTH = int(os.environ.get("RLM_DEPTH", "0"))
_CONTENT_FILE = os.environ.get("RLM_CONTENT_FILE", "")

context = ""
if _CONTENT_FILE and os.path.exists(_CONTENT_FILE):
    with open(_CONTENT_FILE, "r", encoding="utf-8") as f:
        context = f.read()
ctx = context

_REQ_PREFIX = f"__RLM_REQ_{_SID}__::"
_RESP_PREFIX = f"__RLM_RESP_{_SID}__::"
_CODE_PREFIX = f"__RLM_CODE_{_SID}__::"
_OUT_PREFIX = f"__RLM_OUT_{_SID}__::"

def _rpc(payload):
    sys.stdout.write(_REQ_PREFIX + json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    for line in sys.stdin:
        line = line.rstrip("\n")
        if line.startswith(_RESP_PREFIX):
            return json.loads(line[len(_RESP_PREFIX):])
    raise RuntimeError("RLM stdin closed")

def llm_query(prompt, model=None, system=None):
    resp = _rpc({"type": "llm", "prompt": str(prompt), "model": model, "system": system})
    if resp.get("error"):
        raise RuntimeError(resp["error"])
    return resp.get("text", "")

def llm_query_batched(prompts, model=None):
    if len(prompts) > 16:
        raise ValueError(f"MAX_BATCH is 16, got {len(prompts)}")
    resp = _rpc({"type": "llm_batched", "prompts": [str(p) for p in prompts], "model": model})
    if resp.get("error"):
        raise RuntimeError(resp["error"])
    return resp.get("texts", [])

def rlm_query(prompt, model=None):
    if _DEPTH >= _MAX_DEPTH:
        return llm_query(prompt, model)
    resp = _rpc({"type": "rlm", "prompt": str(prompt), "model": model})
    if resp.get("error"):
        raise RuntimeError(resp["error"])
    return resp.get("text", "")

def rlm_query_batched(prompts, model=None):
    if _DEPTH >= _MAX_DEPTH:
        return llm_query_batched(prompts, model)
    if len(prompts) > 16:
        raise ValueError(f"MAX_BATCH is 16, got {len(prompts)}")
    resp = _rpc({"type": "rlm_batched", "prompts": [str(p) for p in prompts], "model": model})
    if resp.get("error"):
        raise RuntimeError(resp["error"])
    return resp.get("texts", [])

class _FinalSignal(Exception):
    def __init__(self, v): self.value = v

def FINAL(value):
    sys.stdout.write(_REQ_PREFIX + json.dumps({"type": "final", "value": str(value)}, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    raise _FinalSignal(value)

def FINAL_VAR(name):
    FINAL(eval(name))

_user_vars = {}
def SHOW_VARS():
    return {k: repr(v) for k, v in _user_vars.items()}

# Main loop: receive code blocks, execute them
while True:
    code_json = None
    for line in sys.stdin:
        line = line.rstrip("\n")
        if line.startswith(_CODE_PREFIX):
            code_json = line[len(_CODE_PREFIX):]
            break
    if not code_json:
        break
    try:
        code = json.loads(code_json)
        exec(code, globals(), _user_vars)
        globals().update(_user_vars)
        sys.stdout.write(_OUT_PREFIX + "OK\n")
        sys.stdout.flush()
    except _FinalSignal:
        break
    except Exception as e:
        sys.stdout.write(_OUT_PREFIX + f"ERROR: {type(e).__name__}: {e}\n")
        sys.stdout.flush()
"""

// ── RPC message parsing ─────────────────────────────────────────────────

type private RpcType = Llm | LlmBatched | Rlm | RlmBatched | Final

type private RpcRequest = {
    Type    : RpcType
    Prompt  : string
    Prompts : string list
    Model   : string option
    System  : string option
    Value   : string
}

let private parseRpcRequest (json: string) : RpcRequest option =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let typ =
            match root.GetProperty("type").GetString() with
            | "llm"         -> Some Llm
            | "llm_batched" -> Some LlmBatched
            | "rlm"         -> Some Rlm
            | "rlm_batched" -> Some RlmBatched
            | "final"       -> Some Final
            | _             -> None
        typ |> Option.map (fun t ->
            { Type    = t
              Prompt  = (try root.GetProperty("prompt").GetString() with _ -> "")
              Prompts = (try [ for el in root.GetProperty("prompts").EnumerateArray() -> el.GetString() ] with _ -> [])
              Model   = (try match root.GetProperty("model").GetString() with null -> None | s -> Some s with _ -> None)
              System  = (try match root.GetProperty("system").GetString() with null -> None | s -> Some s with _ -> None)
              Value   = (try root.GetProperty("value").GetString() with _ -> "") })
    with _ -> None

let private serializeResponse (text: string option) (texts: string list option) (error: string option) : string =
    use ms = new MemoryStream()
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    match text with Some t -> w.WriteString("text", t) | None -> ()
    match texts with
    | Some ts ->
        w.WriteStartArray("texts")
        for t in ts do w.WriteStringValue(t)
        w.WriteEndArray()
    | None -> ()
    match error with Some e -> w.WriteString("error", e) | None -> ()
    w.WriteEndObject()
    w.Flush()
    Text.Encoding.UTF8.GetString(ms.ToArray())

// ── LLM call helpers ────────────────────────────────────────────────────

type private ChatFn =
    LLMProvider -> LLMProvider list -> BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option -> GenerationSettings -> Message list -> ToolSpec list -> Async<Result<LLMResponse, LlmError>>

let private extractText (resp: LLMResponse) : string =
    match resp.Body with
    | TextOnly c -> c
    | WithToolCalls (Some c, _) -> c
    | _ -> ""

let private callLlm
    (chatFn: ChatFn) (provider: LLMProvider) (prompt: string) (systemOpt: string option)
    : Async<Result<string, string>> =
    async {
        let sysMsg = systemOpt |> Option.defaultValue "You are a helpful assistant."
        let messages = [ UserMessage (sysMsg, []); UserMessage (prompt, []) ]
        let settings = { Temperature = 0.3; MaxTokens = 4096; ReasoningEffort = None }
        let! result = chatFn provider [] None settings messages []
        match result with
        | Result.Ok resp -> return Result.Ok (extractText resp)
        | Result.Error e -> return Result.Error e.RawMessage
    }

// ── RLM turn execution ─────────────────────────────────────────────────

/// Root LLM system prompt for code generation.
let private rootSystemPrompt (contentLength: int) (userPrompt: string) : string =
    "You are a code-generating agent in an RLM (Recursive Language Model) session.\n" +
    sprintf "The user provided %d characters of content (available as `context` or `ctx` variable).\n\n" contentLength +
    "Available Python functions:\n" +
    "- llm_query(prompt, model=None) -> str: Single LLM call\n" +
    "- llm_query_batched(prompts, model=None) -> list[str]: Parallel LLM calls (max 16)\n" +
    "- rlm_query(prompt, model=None) -> str: Recursive sub-RLM call\n" +
    "- rlm_query_batched(prompts, model=None) -> list[str]: Parallel sub-RLM calls (max 16)\n" +
    "- FINAL(value): End this session and return value as the result. MUST call when done.\n\n" +
    "Rules:\n" +
    "1. Write ONLY executable Python code. No markdown, no explanations.\n" +
    "2. Use `context` to access the input content.\n" +
    "3. Call FINAL(result) when you have the answer.\n" +
    "4. For large content, chunk and use llm_query_batched.\n" +
    "5. Only standard library imports allowed.\n\n" +
    "Task: " + userPrompt

/// Execute one RLM turn (recursive-capable).
let rec private executeRlmTurn
    (chatFn         : ChatFn)
    (provider       : LLMProvider)
    (childProvider  : LLMProvider)
    (config         : BotSharpConfig)
    (content        : string)
    (prompt         : string)
    (maxIterations  : int)
    (depth          : int)
    (maxDepth       : int)
    : Async<string> =
    async {
        let sid = Guid.NewGuid().ToString("N").[..7]
        let reqPrefix  = sprintf "__RLM_REQ_%s__::" sid
        let respPrefix = sprintf "__RLM_RESP_%s__::" sid
        let codePrefix = sprintf "__RLM_CODE_%s__::" sid
        let outPrefix  = sprintf "__RLM_OUT_%s__::" sid

        // Write content to temp file
        let contentFile = Path.Combine(Path.GetTempPath(), sprintf "rlm_%s.txt" sid)
        do! File.WriteAllTextAsync(contentFile, content) |> Async.AwaitTask

        try
            // Spawn Python REPL
            let psi = ProcessStartInfo("python3")
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.Environment["RLM_SID"] <- sid
            psi.Environment["RLM_CONTENT_FILE"] <- contentFile
            psi.Environment["RLM_MAX_DEPTH"] <- string maxDepth
            psi.Environment["RLM_DEPTH"] <- string depth
            psi.ArgumentList.Add("-u")
            psi.ArgumentList.Add("-c")
            psi.ArgumentList.Add(pythonBootstrap)

            use proc = Process.Start(psi)
            use cts = new Threading.CancellationTokenSource(TurnTimeoutSecs * 1000)

            // Root LLM iteration loop
            let mutable messages : Message list = [
                UserMessage (rootSystemPrompt content.Length prompt, [])
            ]
            let mutable iteration = 0
            let mutable finalValue : string option = None

            while iteration < maxIterations && finalValue.IsNone && not cts.Token.IsCancellationRequested do
                // Call root LLM to generate Python code
                let settings = { Temperature = 0.3; MaxTokens = 4096; ReasoningEffort = None }
                let! result = chatFn provider [] None settings messages []
                match result with
                | Result.Error e ->
                    finalValue <- Some (sprintf "RLM root LLM error: %s" e.RawMessage)
                | Result.Ok resp ->
                    let code = extractText resp
                    if code.Trim() = "" then
                        finalValue <- Some "(RLM: root LLM returned empty code)"
                    else

                    // Send code to Python REPL
                    let codeJson = JsonSerializer.Serialize(code)
                    do! proc.StandardInput.WriteLineAsync(codePrefix + codeJson) |> Async.AwaitTask
                    do! proc.StandardInput.FlushAsync() |> Async.AwaitTask

                    // Read output lines from Python, dispatching RPC requests
                    let mutable waitingForOutput = true
                    let mutable iterOutput = ""

                    while waitingForOutput && not cts.Token.IsCancellationRequested do
                        let! lineTask = proc.StandardOutput.ReadLineAsync() |> Async.AwaitTask
                        match lineTask with
                        | null ->
                            waitingForOutput <- false
                            if finalValue.IsNone then
                                finalValue <- Some "(RLM: Python process exited unexpectedly)"
                        | line when line.StartsWith(reqPrefix) ->
                            // RPC request from Python
                            let json = line.Substring(reqPrefix.Length)
                            match parseRpcRequest json with
                            | None ->
                                let errResp = serializeResponse None None (Some "Invalid RPC request")
                                do! proc.StandardInput.WriteLineAsync(respPrefix + errResp) |> Async.AwaitTask
                                do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                            | Some req ->
                                match req.Type with
                                | Final ->
                                    finalValue <- Some req.Value
                                    waitingForOutput <- false
                                | Llm ->
                                    let! r = callLlm chatFn childProvider req.Prompt req.System
                                    let resp =
                                        match r with
                                        | Result.Ok t -> serializeResponse (Some t) None None
                                        | Result.Error e -> serializeResponse None None (Some e)
                                    do! proc.StandardInput.WriteLineAsync(respPrefix + resp) |> Async.AwaitTask
                                    do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                                | LlmBatched ->
                                    let prompts = req.Prompts |> List.truncate MaxBatch
                                    let! results =
                                        prompts
                                        |> List.map (fun p -> callLlm chatFn childProvider p None)
                                        |> fun tasks -> Async.Parallel(tasks, maxDegreeOfParallelism = MaxBatch)
                                    let texts = results |> Array.map (function Result.Ok t -> t | Result.Error e -> sprintf "[error: %s]" e) |> Array.toList
                                    let resp = serializeResponse None (Some texts) None
                                    do! proc.StandardInput.WriteLineAsync(respPrefix + resp) |> Async.AwaitTask
                                    do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                                | Rlm ->
                                    if depth + 1 >= maxDepth then
                                        // Degrade to llm_query
                                        let! r = callLlm chatFn childProvider req.Prompt req.System
                                        let resp =
                                            match r with
                                            | Result.Ok t -> serializeResponse (Some t) None None
                                            | Result.Error e -> serializeResponse None None (Some e)
                                        do! proc.StandardInput.WriteLineAsync(respPrefix + resp) |> Async.AwaitTask
                                        do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                                    else
                                        let! subResult =
                                            executeRlmTurn chatFn provider childProvider config
                                                req.Prompt req.Prompt maxIterations (depth + 1) maxDepth
                                        let resp = serializeResponse (Some subResult) None None
                                        do! proc.StandardInput.WriteLineAsync(respPrefix + resp) |> Async.AwaitTask
                                        do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                                | RlmBatched ->
                                    let prompts = req.Prompts |> List.truncate MaxBatch
                                    let! results =
                                        prompts
                                        |> List.map (fun p -> async {
                                            if depth + 1 >= maxDepth then
                                                let! r = callLlm chatFn childProvider p None
                                                return match r with Result.Ok t -> t | Result.Error e -> sprintf "[error: %s]" e
                                            else
                                                return! executeRlmTurn chatFn provider childProvider config
                                                            p p maxIterations (depth + 1) maxDepth
                                        })
                                        |> fun tasks -> Async.Parallel(tasks, maxDegreeOfParallelism = MaxBatch)
                                    let texts = results |> Array.toList
                                    let resp = serializeResponse None (Some texts) None
                                    do! proc.StandardInput.WriteLineAsync(respPrefix + resp) |> Async.AwaitTask
                                    do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                        | line when line.StartsWith(outPrefix) ->
                            // Execution result from Python
                            let result = line.Substring(outPrefix.Length)
                            iterOutput <- result
                            waitingForOutput <- false
                        | line ->
                            // Regular stdout — accumulate as output
                            iterOutput <- iterOutput + (if iterOutput = "" then "" else "\n") + line

                    if finalValue.IsNone then
                        // Feed output back to root LLM
                        messages <- messages @ [
                            AssistantMessage (code, None)
                            UserMessage (sprintf "Execution result:\n%s" iterOutput, [])
                        ]

                iteration <- iteration + 1

            // Cleanup
            if not proc.HasExited then
                try proc.Kill(true) with _ -> ()

            return finalValue |> Option.defaultValue "(RLM reached max iterations without calling FINAL)"

        finally
            try File.Delete(contentFile) with _ -> ()
    }

// ── Tool spec ───────────────────────────────────────────────────────────

let rlmToolSpec : ToolSpec = {
    Name        = ToolName "rlm"
    Description =
        "Process large content that exceeds the context window using a Python REPL with built-in LLM functions. " +
        "A root LLM generates Python code iteratively. The code can call llm_query() for LLM assistance " +
        "and llm_query_batched() for parallel processing. Call FINAL(result) to return the answer. " +
        "Use for: batch analysis, large file processing, map-reduce over documents, " +
        "tasks requiring many LLM calls, or recursive sub-task decomposition."
    Parameters  = Map.ofList [
        "content", { Type = JsString
                     Description = "The large content to process (will be available as `context` variable in Python)"
                     Required = true }
        "prompt",  { Type = JsString
                     Description = "What to do with the content"
                     Required = true }
        "model",   { Type = JsString
                     Description = "Override the child model for LLM calls (default: auto-select cheap model)"
                     Required = false }
        "max_iterations", { Type = JsNumber
                            Description = "Max code generation iterations (default 25, max 50)"
                            Required = false }
    ]
    ConcurrencySafe = false
}

// ── Entry point ─────────────────────────────────────────────────────────

let executeRlm
    (chatFn        : ChatFn)
    (provider      : LLMProvider)
    (childProvider : LLMProvider)
    (config        : BotSharpConfig)
    (args          : Map<string, JsonElement>)
    : Async<ToolResult> =
    async {
        match requireStringArg "content" args, requireStringArg "prompt" args with
        | Result.Error e, _ | _, Result.Error e -> return ToolFailure e
        | Result.Ok content, Result.Ok prompt ->
            let maxIter =
                match args.TryFind "max_iterations" with
                | Some v when v.ValueKind = JsonValueKind.Number -> min 50 (max 1 (v.GetInt32()))
                | _ -> DefaultMaxIterations
            try
                let! result =
                    executeRlmTurn chatFn provider childProvider config
                        content prompt maxIter 0 config.RlmMaxDepth
                return ToolSuccess result
            with
            | :? OperationCanceledException ->
                return ToolFailure (ExecutionTimeout (TimeSpan.FromSeconds(float TurnTimeoutSecs)))
            | ex ->
                return ToolFailure (ExecutionFailed (sprintf "RLM failed: %s" ex.Message))
    }

let allTools
    (chatFn        : ChatFn)
    (provider      : LLMProvider)
    (childProvider : LLMProvider)
    (config        : BotSharpConfig)
    : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    [ rlmToolSpec, executeRlm chatFn provider childProvider config ]
