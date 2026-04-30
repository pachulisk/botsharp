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
//   POST /inter-agent/chat          → 202 {task_id, status: "pending"}
//   GET  /inter-agent/task/{task_id} → {status, response, is_final}
//   GET  /inter-agent/health         → {status: "ok", instance, port}
//
// The initiating agent submits a task and polls for result — no blocking.
// ═══════════════════════════════════════════════════════════════════════════

// ── Task registry ────────────────────────────────────────────────────────

type TaskStatus =
    | Pending
    | Running
    | Done
    | Failed

let private taskStatusString = function
    | Pending -> "pending"
    | Running -> "running"
    | Done    -> "done"
    | Failed  -> "failed"

type AgentTask = {
    TaskId        : string
    SessionId     : string
    FromInstance   : string
    RoundCount    : int
    mutable Status     : TaskStatus
    mutable Response   : string option
    mutable IsFinal    : bool
    mutable Error      : string option
    CreatedAt     : DateTimeOffset
    mutable FinishedAt : DateTimeOffset option
}

let private taskToJson (instanceName: string) (task: AgentTask) : string =
    use ms = new MemoryStream()
    use w = new Utf8JsonWriter(ms)
    w.WriteStartObject()
    w.WriteString("task_id", task.TaskId)
    w.WriteString("status", taskStatusString task.Status)
    w.WriteString("instance", instanceName)
    w.WriteString("session_id", task.SessionId)
    w.WriteNumber("round_count", task.RoundCount)
    match task.Status with
    | Done ->
        w.WriteString("response", task.Response |> Option.defaultValue "")
        w.WriteBoolean("is_final", task.IsFinal)
    | Failed ->
        w.WriteString("error", task.Error |> Option.defaultValue "")
    | _ -> ()
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

// ── Consensus detection ──────────────────────────────────────────────────

let private finalSignals = [
    "最终方案"; "讨论结束"; "达成共识"; "已确认"
    "final proposal"; "discussion complete"; "consensus reached"
    "DISCUSSION_COMPLETE"
]

let private isFinal (text: string) : bool =
    let lower = text.ToLowerInvariant()
    finalSignals |> List.exists (fun s -> lower.Contains(s.ToLowerInvariant()))

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

type InterAgentServer(coordinator: AgentCoordinator, config: InterAgentChannelConfig, httpClient: HttpClient) =
    let listener = new HttpListener()
    let tasks    = ConcurrentDictionary<string, AgentTask>()

    let evictOldTasks () =
        let cutoff = DateTimeOffset.UtcNow.AddSeconds(- float config.TaskTtlSeconds)
        let toDelete =
            tasks
            |> Seq.filter (fun kv ->
                (kv.Value.Status = Done || kv.Value.Status = Failed) &&
                kv.Value.FinishedAt.IsSome &&
                kv.Value.FinishedAt.Value < cutoff)
            |> Seq.map (fun kv -> kv.Key)
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
                let getString (name: string) =
                    match body.TryGetProperty(name) with
                    | true, el when el.ValueKind = JsonValueKind.String -> el.GetString() |> Option.ofObj
                    | _ -> None
                let getInt (name: string) =
                    match body.TryGetProperty(name) with
                    | true, el when el.ValueKind = JsonValueKind.Number -> Some (el.GetInt32())
                    | _ -> None

                let message      = getString "message" |> Option.map (fun s -> s.Trim()) |> Option.defaultValue ""
                let sessionId    = getString "session_id" |> Option.defaultValue ""
                let fromInstance = getString "from_instance" |> Option.defaultValue "unknown"
                let roundCount   = getInt "round_count" |> Option.defaultValue 0

                if message = "" || sessionId = "" then
                    do! writeJsonError ctx 400 "message and session_id are required"
                else
                    let taskId = Guid.NewGuid().ToString()
                    let task = {
                        TaskId       = taskId
                        SessionId    = sessionId
                        FromInstance = fromInstance
                        RoundCount   = roundCount
                        Status       = Pending
                        Response     = None
                        IsFinal      = false
                        Error        = None
                        CreatedAt    = DateTimeOffset.UtcNow
                        FinishedAt   = None
                    }
                    tasks.[taskId] <- task

                    eprintfn "[InterAgent] Task %s created | from=%s session=%s round=%d"
                        taskId fromInstance sessionId roundCount

                    // Audit: inbound
                    match config.AuditWebhookUrl with
                    | Some url -> Async.Start(pushAudit httpClient url fromInstance config.InstanceName sessionId roundCount message)
                    | None -> ()

                    // Route to agent loop (non-blocking)
                    task.Status <- Running
                    Async.Start(async {
                        let inbound : InboundMessage = {
                            Channel            = ChannelId "interagent"
                            Sender             = UserId fromInstance
                            Chat               = ChatId taskId
                            Input              = ChatMessage (message, [])
                            Metadata           = Map.ofList [
                                "from_instance", fromInstance
                                "session_id", sessionId
                                "round_count", string roundCount ]
                            SessionKeyOverride = Some (SessionId $"interagent:{taskId}")
                        }
                        let! result = coordinator.Route inbound
                        match result with
                        | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) ->
                            task.Status <- Done
                            task.Response <- Some text
                            task.IsFinal <- isFinal text
                            task.FinishedAt <- Some DateTimeOffset.UtcNow
                            eprintfn "[InterAgent] Task %s done (session=%s)" taskId sessionId
                            // Audit: outbound
                            match config.AuditWebhookUrl with
                            | Some url -> do! pushAudit httpClient url config.InstanceName fromInstance sessionId roundCount text
                            | None -> ()
                        | Result.Error e ->
                            task.Status <- Failed
                            task.Error <- Some (sprintf "%A" e)
                            task.FinishedAt <- Some DateTimeOffset.UtcNow
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

            // Eviction timer
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
