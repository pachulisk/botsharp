module BotSharp.Infrastructure.Channels.InterAgentChannel

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open BotSharp.Domain.Types
open BotSharp.Application.SessionActor

// ═══════════════════════════════════════════════════════════════════════════
// Inter-agent communication channel
//
// Enables direct HTTP communication between BotSharp instances.
// Multiple agents can collaborate autonomously without human mediation.
//
// Design: Async Task Model (port of nanobot PR #2002)
//   POST /inter-agent/chat          → 202 {task_id, status}
//   GET  /inter-agent/task/{task_id} → poll for result
//   GET  /inter-agent/health         → instance health check
//
// Type-driven design:
//   - TaskOutcome DU replaces bool IsFinal + string option Response/Error
//   - ChatRequest parsed type replaces raw string validation
//   - Consensus detection via CLIPS rules (not hardcoded bool function)
// ═══════════════════════════════════════════════════════════════════════════

// ── Task lifecycle (type-driven) ─────────────────────────────────────────

/// The outcome of a completed task — no bool flags needed.
type TaskOutcome =
    | InProgress                                   // task is still running
    | Completed of response: string * consensus: bool  // agent replied
    | Faulted   of error: string                   // agent loop error

/// The full lifecycle state of an inter-agent task.
type TaskPhase =
    | Queued                          // received, not yet dispatched
    | Processing                     // agent loop is working
    | Finished of TaskOutcome * finishedAt: DateTimeOffset  // terminal

type AgentTask = {
    TaskId      : string
    Request     : ChatRequest
    CreatedAt   : DateTimeOffset
    mutable Phase : TaskPhase
}

/// A validated chat request — if this value exists, all required fields are present.
/// Replaces the `if message = "" || sessionId = ""` bool check.
and ChatRequest = {
    Message      : string        // guaranteed non-empty by parse
    SessionId    : string        // guaranteed non-empty by parse
    FromInstance : string
    RoundCount   : int
}

/// Parse a raw JSON body into a ChatRequest. Returns Error for invalid input.
let private parseChatRequest (body: JsonElement) : Result<ChatRequest, string> =
    let getString (name: string) =
        match body.TryGetProperty(name) with
        | true, el when el.ValueKind = JsonValueKind.String ->
            match el.GetString() with null -> None | s -> let t = s.Trim() in if t = "" then None else Some t
        | _ -> None
    let getInt (name: string) =
        match body.TryGetProperty(name) with
        | true, el when el.ValueKind = JsonValueKind.Number -> Some (el.GetInt32())
        | _ -> None
    match getString "message", getString "session_id" with
    | None, _    -> Error "message is required"
    | _, None    -> Error "session_id is required"
    | Some msg, Some sid ->
        Ok {
            Message      = msg
            SessionId    = sid
            FromInstance = getString "from_instance" |> Option.defaultValue "unknown"
            RoundCount   = getInt "round_count" |> Option.defaultValue 0
        }

// ── Task serialization ───────────────────────────────────────────────────

let private phaseToStatus = function
    | Queued       -> "pending"
    | Processing   -> "running"
    | Finished _   -> "done"  // overridden for Faulted below

let private taskToJson (instanceName: string) (task: AgentTask) : string =
    use ms = new MemoryStream()
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("task_id", task.TaskId)
    w.WriteString("instance", instanceName)
    w.WriteString("session_id", task.Request.SessionId)
    w.WriteNumber("round_count", task.Request.RoundCount)
    match task.Phase with
    | Queued ->
        w.WriteString("status", "pending")
    | Processing ->
        w.WriteString("status", "running")
    | Finished (Completed (response, consensus), _) ->
        w.WriteString("status", "done")
        w.WriteString("response", response)
        w.WriteBoolean("is_final", consensus)
    | Finished (Faulted error, _) ->
        w.WriteString("status", "failed")
        w.WriteString("error", error)
    | Finished (InProgress, _) ->
        w.WriteString("status", "running")  // shouldn't happen, defensive
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

// ── Consensus detection via CLIPS ────────────────────────────────────────
// Instead of a hardcoded `isFinal` bool function, we assert a fact into
// the rule engine and let CLIPS rules determine consensus.
// Users can add custom signal words via workspace/rules/*.clp.

let private detectConsensus
    (ruleEngine : BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option)
    (text       : string)
    : bool =
    match ruleEngine with
    | None ->
        // Fallback when CLIPS is not available: hardcoded signals
        let lower = text.ToLowerInvariant()
        [ "最终方案"; "讨论结束"; "达成共识"; "已确认"
          "final proposal"; "discussion complete"; "consensus reached"
          "DISCUSSION_COMPLETE" ]
        |> List.exists (fun s -> lower.Contains(s.ToLowerInvariant()))
    | Some engine ->
        // Assert the response text as a fact and let rules decide
        let escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let truncated = if escaped.Length > 500 then escaped.[..499] else escaped
        let factStr = sprintf "(inter-agent-response (content \"%s\"))" truncated
        match BotSharp.Infrastructure.Rules.ClipsEnvironment.assertFact engine.Env factStr with
        | Ok () ->
            let actions = BotSharp.Infrastructure.Rules.RuleEngine.evaluate engine
            let isConsensus =
                actions |> List.exists (function
                    | BotSharp.Infrastructure.Rules.RuleEngine.StopLoop reason ->
                        reason.Contains("consensus")
                    | _ -> false)
            isConsensus
        | Error _ -> false

// ── JSON helpers ─────────────────────────────────────────────────────────

let private writeJsonResponse (ctx: HttpListenerContext) (statusCode: int) (json: string) : Async<unit> =
    async {
        let bytes = Encoding.UTF8.GetBytes(json)
        ctx.Response.StatusCode <- statusCode
        ctx.Response.ContentType <- "application/json"
        ctx.Response.ContentLength64 <- int64 bytes.Length
        do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
        ctx.Response.Close()
    }

let private writeJsonError (ctx: HttpListenerContext) (statusCode: int) (msg: string) : Async<unit> =
    writeJsonResponse ctx statusCode $"""{{ "error": "{msg}" }}"""

let private readBody (ctx: HttpListenerContext) : Async<Result<JsonElement, string>> =
    async {
        try
            use reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)
            let! body = reader.ReadToEndAsync() |> Async.AwaitTask
            use doc = JsonDocument.Parse(body)
            return Ok (doc.RootElement.Clone())
        with ex ->
            return Error ex.Message
    }

// ── Audit webhook ────────────────────────────────────────────────────────

let private pushAudit
    (httpClient    : HttpClient)
    (webhookUrl    : string)
    (fromInstance  : string)
    (toInstance    : string)
    (sessionId    : string)
    (roundCount   : int)
    (message      : string)
    : Async<unit> =
    async {
        try
            let header = $"[Inter-Agent]\n{fromInstance} -> {toInstance}\nSession: {sessionId}  Round: {roundCount}\n\n"
            let payload = $"""{{ "msg_type": "text", "content": {{ "text": "{header}{message |> fun s -> s.Replace("\"", "\\\"")}" }} }}"""
            let content = new StringContent(payload, Encoding.UTF8, "application/json")
            let! _ = httpClient.PostAsync(webhookUrl, content) |> Async.AwaitTask
            ()
        with ex ->
            eprintfn "[InterAgent] Audit webhook failed: %s" ex.Message
    }

// ── Server ───────────────────────────────────────────────────────────────

type InterAgentServer(coordinator: AgentCoordinator, config: InterAgentChannelConfig, httpClient: HttpClient, ruleEngine: BotSharp.Infrastructure.Rules.RuleEngine.RuleEngine option) =
    let listener = new HttpListener()
    let tasks    = ConcurrentDictionary<string, AgentTask>()

    let evictOldTasks () =
        let cutoff = DateTimeOffset.UtcNow.AddSeconds(- float config.TaskTtlSeconds)
        let toDelete =
            tasks
            |> Seq.choose (fun kv ->
                match kv.Value.Phase with
                | Finished (_, finishedAt) when finishedAt < cutoff -> Some kv.Key
                | _ -> None)
            |> Seq.toList
        for tid in toDelete do
            tasks.TryRemove(tid) |> ignore
        if not toDelete.IsEmpty then
            eprintfn "[InterAgent] Evicted %d expired tasks" toDelete.Length

    let handleHealth (ctx: HttpListenerContext) : Async<unit> =
        let json = $"""{{ "status": "ok", "instance": "{config.InstanceName}", "port": {config.Port} }}"""
        writeJsonResponse ctx 200 json

    let handleChat (ctx: HttpListenerContext) : Async<unit> =
        async {
            match! readBody ctx with
            | Error _ ->
                do! writeJsonError ctx 400 "invalid JSON"
            | Ok body ->
                // Parse, don't validate: ChatRequest type guarantees all required fields
                match parseChatRequest body with
                | Error msg ->
                    do! writeJsonError ctx 400 msg
                | Ok req ->
                    let taskId = Guid.NewGuid().ToString()
                    let task = {
                        TaskId    = taskId
                        Request   = req
                        CreatedAt = DateTimeOffset.UtcNow
                        Phase     = Processing
                    }
                    tasks.[taskId] <- task

                    eprintfn "[InterAgent] Task %s created | from=%s session=%s round=%d"
                        taskId req.FromInstance req.SessionId req.RoundCount

                    // Audit: inbound
                    match config.AuditWebhookUrl with
                    | Some url -> Async.Start(pushAudit httpClient url req.FromInstance config.InstanceName req.SessionId req.RoundCount req.Message)
                    | None -> ()

                    // Route to agent loop (non-blocking)
                    Async.Start(async {
                        let inbound : InboundMessage = {
                            Channel            = ChannelId "interagent"
                            Sender             = UserId req.FromInstance
                            Chat               = ChatId taskId
                            Input              = ChatMessage (req.Message, [])
                            Metadata           = Map.ofList [
                                "from_instance", req.FromInstance
                                "session_id", req.SessionId
                                "round_count", string req.RoundCount ]
                            SessionKeyOverride = Some (SessionId $"interagent:{taskId}")
                        }
                        let! result = coordinator.Route inbound
                        match result with
                        | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) ->
                            let consensus = detectConsensus ruleEngine text
                            task.Phase <- Finished (Completed (text, consensus), DateTimeOffset.UtcNow)
                            eprintfn "[InterAgent] Task %s done (session=%s, consensus=%b)" taskId req.SessionId consensus
                            match config.AuditWebhookUrl with
                            | Some url -> do! pushAudit httpClient url config.InstanceName req.FromInstance req.SessionId req.RoundCount text
                            | None -> ()
                        | Result.Error e ->
                            task.Phase <- Finished (Faulted (sprintf "%A" e), DateTimeOffset.UtcNow)
                            eprintfn "[InterAgent] Task %s failed: %A" taskId e
                    })

                    do! writeJsonResponse ctx 202 (taskToJson config.InstanceName task)
        }

    let handleTaskStatus (ctx: HttpListenerContext) (taskId: string) : Async<unit> =
        match tasks.TryGetValue(taskId) with
        | false, _ ->
            writeJsonError ctx 404 "task not found"
        | true, task ->
            writeJsonResponse ctx 200 (taskToJson config.InstanceName task)

    let handleRequest (ctx: HttpListenerContext) : Async<unit> =
        async {
            let path   = ctx.Request.Url |> Option.ofObj |> Option.map (fun u -> u.AbsolutePath) |> Option.defaultValue ""
            let method = ctx.Request.HttpMethod.ToUpperInvariant()
            try
                match method, path with
                | "GET",  "/inter-agent/health" ->
                    do! handleHealth ctx
                | "POST", "/inter-agent/chat" ->
                    do! handleChat ctx
                | "GET",  p when p.StartsWith("/inter-agent/task/") ->
                    let taskId = p.Substring("/inter-agent/task/".Length)
                    do! handleTaskStatus ctx taskId
                | "OPTIONS", _ ->
                    ctx.Response.Headers.["Access-Control-Allow-Origin"]  <- "*"
                    ctx.Response.Headers.["Access-Control-Allow-Methods"] <- "GET, POST, OPTIONS"
                    ctx.Response.Headers.["Access-Control-Allow-Headers"] <- "Content-Type"
                    ctx.Response.StatusCode <- 204
                    ctx.Response.Close()
                | _ ->
                    do! writeJsonError ctx 404 "not found"
            with ex ->
                eprintfn "[InterAgent] Handler error: %s" ex.Message
                try ctx.Response.Close() with _ -> ()
        }

    member _.Start() : Async<unit> =
        async {
            let prefix = $"http://localhost:{config.Port}/"
            listener.Prefixes.Add(prefix)
            listener.Start()
            printfn "[InterAgent] Listening on http://localhost:%d (instance: %s)" config.Port config.InstanceName
            printfn "[InterAgent]   POST /inter-agent/chat          Submit task"
            printfn "[InterAgent]   GET  /inter-agent/task/{{id}}     Poll task status"
            printfn "[InterAgent]   GET  /inter-agent/health         Health check"

            let evictionTimer = new Timer((fun _ -> evictOldTasks ()), null, 60_000, 60_000)

            try
                while listener.IsListening do
                    let! ctx = listener.GetContextAsync() |> Async.AwaitTask
                    Async.Start(handleRequest ctx)
            with
            | :? ObjectDisposedException -> ()
            | :? HttpListenerException -> ()

            evictionTimer.Dispose()
        }

    member _.Stop() =
        if listener.IsListening then
            listener.Stop()
            listener.Close()
