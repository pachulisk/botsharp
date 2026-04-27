module BotSharp.Infrastructure.Tools.McpTool

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.ToolParser
open BotSharp.Infrastructure.Providers.SseParser

// ═══════════════════════════════════════════════════════════════════════════
// McpTool — wraps MCP server tools as native BotSharp tools
//
// At startup, connects to each configured MCP server, performs the MCP
// handshake (initialize → notifications/initialized → tools/list), and
// returns (ToolSpec × execute) pairs — one per remote tool.
//
// Tool names are prefixed: mcp_{serverName}_{originalName} to avoid
// collisions with built-in tools.
//
// Transports:
//   • Stdio — spawn process; newline-delimited JSON-RPC on stdin/stdout
//   • HTTP  — POST JSON-RPC; accept application/json or text/event-stream
// ═══════════════════════════════════════════════════════════════════════════

// ── JSON building ────────────────────────────────────────────────────────────

let private buildJson (fill: Utf8JsonWriter -> unit) : string =
    use ms = new MemoryStream()
    use w  = new Utf8JsonWriter(ms)
    fill w
    w.Flush()
    Encoding.UTF8.GetString(ms.ToArray())

// ── MCP request builders ─────────────────────────────────────────────────────

let private initReq (id: string) : string =
    buildJson (fun w ->
        w.WriteStartObject()
        w.WriteString("jsonrpc", "2.0")
        w.WriteString("id",      id)
        w.WriteString("method",  "initialize")
        w.WriteStartObject("params")
        w.WriteString("protocolVersion", "2024-11-05")
        w.WriteStartObject("capabilities")
        w.WriteEndObject()
        w.WriteStartObject("clientInfo")
        w.WriteString("name",    "botsharp")
        w.WriteString("version", "1.0")
        w.WriteEndObject()
        w.WriteEndObject()
        w.WriteEndObject())

let private initializedNotif () : string =
    buildJson (fun w ->
        w.WriteStartObject()
        w.WriteString("jsonrpc", "2.0")
        w.WriteString("method",  "notifications/initialized")
        w.WriteEndObject())

let private listToolsReq (id: string) : string =
    buildJson (fun w ->
        w.WriteStartObject()
        w.WriteString("jsonrpc", "2.0")
        w.WriteString("id",      id)
        w.WriteString("method",  "tools/list")
        w.WriteEndObject())

let private callToolReq (id: string) (toolName: string) (args: Map<string, JsonElement>) : string =
    buildJson (fun w ->
        w.WriteStartObject()
        w.WriteString("jsonrpc", "2.0")
        w.WriteString("id",      id)
        w.WriteString("method",  "tools/call")
        w.WriteStartObject("params")
        w.WriteString("name", toolName)
        w.WriteStartObject("arguments")
        for kv in args do
            w.WritePropertyName(kv.Key)
            kv.Value.WriteTo(w)
        w.WriteEndObject()
        w.WriteEndObject()
        w.WriteEndObject())

// ── JSON-RPC response helpers ─────────────────────────────────────────────────

type private RpcResp = {
    Id     : string option
    Result : JsonElement option
    Error  : (int * string) option
}

let private parseRpc (line: string) : Result<RpcResp, string> =
    try
        use doc = JsonDocument.Parse(line)
        let el = doc.RootElement.Clone()
        let id =
            match el.TryGetProperty("id") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
            | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetInt32() |> string)
            | _ -> None
        let result =
            match el.TryGetProperty("result") with
            | true, v -> Some (v.Clone())
            | _ -> None
        let error =
            match el.TryGetProperty("error") with
            | true, e ->
                let code = match e.TryGetProperty("code")    with | true, c -> c.GetInt32()  | _ -> -1
                let msg  = match e.TryGetProperty("message") with | true, m -> m.GetString() |> Option.ofObj |> Option.defaultValue "unknown" | _ -> "unknown"
                Some (code, msg)
            | _ -> None
        Ok { Id = id; Result = result; Error = error }
    with ex -> Error ex.Message

/// Read lines from a TextReader, skipping JSON-RPC notifications (no "id"),
/// until we get a proper response (has "id") or EOF.
let internal readNextResponse (reader: TextReader) : Async<Result<string, string>> =
    let rec loop () = async {
        let! line = reader.ReadLineAsync() |> Async.AwaitTask
        match Option.ofObj line with
        | None -> return Error "MCP server closed connection"
        | Some l ->
            try
                use doc = JsonDocument.Parse(l)
                match doc.RootElement.TryGetProperty("id") with
                | true, _ -> return Ok l       // has id → this is a response
                | false, _ -> return! loop ()   // no id → notification, skip
            with _ -> return! loop ()           // malformed line, skip
    }
    loop ()

/// Read SSE frames from a stream, skipping comments/blanks, extracting
/// data payloads. Filters for JSON-RPC responses (have "id" field).
let internal readSseResponse (reader: TextReader) : Async<Result<string, string>> =
    let rec loop () = async {
        let! line = reader.ReadLineAsync() |> Async.AwaitTask
        match Option.ofObj line with
        | None -> return Error "MCP SSE stream closed without response"
        | Some l ->
            match parseSseLine l with
            | Ok (DataLine data) ->
                try
                    use doc = JsonDocument.Parse(data)
                    match doc.RootElement.TryGetProperty("id") with
                    | true, _ -> return Ok data    // response with id
                    | false, _ -> return! loop ()   // notification, skip
                with _ -> return! loop ()           // malformed data, skip
            | Ok DoneLine -> return Error "MCP SSE stream ended without response"
            | _ -> return! loop ()                  // comment/blank line, skip
    }
    loop ()

// ── MCP tool result content extraction ───────────────────────────────────────

let internal extractContent (result: JsonElement) : string =
    match result.TryGetProperty("content") with
    | false, _ -> result.GetRawText()
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray()
        |> Seq.choose (fun block ->
            match block.TryGetProperty("type") with
            | true, t when t.ValueKind = JsonValueKind.String ->
                match t.GetString() |> Option.ofObj with
                | Some "text" ->
                    match block.TryGetProperty("text") with
                    | true, v -> v.GetString() |> Option.ofObj
                    | _ -> None
                | _ -> Some (block.GetRawText())   // non-text block: return raw JSON
            | _ -> None)
        |> String.concat "\n"
        |> fun s -> if String.IsNullOrEmpty s then "(no output)" else s
    | _ -> "(no output)"

// ── Tool schema mapping: MCP inputSchema → ToolSpec.Parameters ───────────────

let internal parseInputSchema (toolEl: JsonElement) : Map<string, JsonSchemaProperty> =
    let rec typeOf (el: JsonElement) : JsonSchemaType =
        match el.TryGetProperty("type") with
        | true, t when t.ValueKind = JsonValueKind.String ->
            match t.GetString() |> Option.ofObj |> Option.defaultValue "" with
            | "string"             -> JsString
            | "number" | "integer" -> JsNumber
            | "boolean"            -> JsBoolean
            | "array" ->
                match el.TryGetProperty("items") with
                | true, items -> JsArray (typeOf items)
                | _ -> JsArray JsAny
            | _ -> JsAny
        | _ ->
            match el.TryGetProperty("enum") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray()
                |> Seq.choose (fun v ->
                    if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj else None)
                |> Seq.toList
                |> JsEnum
            | _ -> JsAny

    match toolEl.TryGetProperty("inputSchema") with
    | false, _ -> Map.empty
    | true, schema ->
        let required =
            match schema.TryGetProperty("required") with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray()
                |> Seq.choose (fun v ->
                    if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj else None)
                |> Set.ofSeq
            | _ -> Set.empty
        match schema.TryGetProperty("properties") with
        | false, _ -> Map.empty
        | true, props ->
            props.EnumerateObject()
            |> Seq.map (fun kv ->
                let desc =
                    match kv.Value.TryGetProperty("description") with
                    | true, d -> d.GetString() |> Option.ofObj |> Option.defaultValue ""
                    | _ -> ""
                kv.Name, {
                    Type        = typeOf kv.Value
                    Description = desc
                    Required    = Set.contains kv.Name required
                })
            |> Map.ofSeq

let internal parseMcpToolEntry (serverName: string) (toolEl: JsonElement) : (string * ToolSpec) option =
    try
        match toolEl.TryGetProperty("name") with
        | false, _ -> None
        | true, nameEl ->
            match nameEl.GetString() |> Option.ofObj with
            | None -> None
            | Some origName ->
                let desc =
                    match toolEl.TryGetProperty("description") with
                    | true, d -> d.GetString() |> Option.ofObj |> Option.defaultValue origName
                    | _ -> origName
                let spec = {
                    Name            = ToolName $"mcp_{serverName}_{origName}"
                    Description     = desc
                    Parameters      = parseInputSchema toolEl
                    ConcurrencySafe = false  // MCP tool side effects are unknown; safe default
                }
                Some (origName, spec)
    with _ -> None

// ── Transport record ──────────────────────────────────────────────────────────

type private McpTransport = {
    /// Send a request and receive the matching JSON-RPC response.
    Request : string -> Async<Result<string, string>>
    /// Send a one-way notification and await its delivery (response ignored).
    Notify  : string -> Async<unit>
    Dispose : unit -> unit
}

// ── Stdio transport ───────────────────────────────────────────────────────────

let private createStdioTransport
    (cmd    : string)
    (args   : string list)
    (envVars: Map<string, string>)
    : Result<McpTransport, string> =
    try
        let psi = ProcessStartInfo(cmd)
        for a in args do psi.ArgumentList.Add(a)
        for kv in envVars do psi.Environment.[kv.Key] <- kv.Value
        psi.RedirectStandardInput  <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError  <- true
        psi.UseShellExecute        <- false
        let proc = Process.Start(psi)
        let writer = proc.StandardInput
        let reader = proc.StandardOutput
        let sem  = new System.Threading.SemaphoreSlim(1, 1)

        let request (line: string) = async {
            do! sem.WaitAsync() |> Async.AwaitTask
            try
                do! writer.WriteLineAsync(line) |> Async.AwaitTask
                do! writer.FlushAsync() |> Async.AwaitTask
                return! readNextResponse reader
            finally
                sem.Release() |> ignore
        }

        // Notifications: write to stdin; no response expected.
        let notify (line: string) = async {
            do! sem.WaitAsync() |> Async.AwaitTask
            try
                do! writer.WriteLineAsync(line) |> Async.AwaitTask
                do! writer.FlushAsync() |> Async.AwaitTask
            finally
                sem.Release() |> ignore
        }

        let dispose () =
            try writer.Dispose() with _ -> ()
            try if not proc.HasExited then proc.Kill() with _ -> ()
            proc.Dispose()
            sem.Dispose()

        Ok { Request = request; Notify = notify; Dispose = dispose }
    with ex ->
        Error ex.Message

// ── HTTP transport ────────────────────────────────────────────────────────────

let private createHttpTransport
    (client : HttpClient)
    (url    : Uri)
    (headers: Map<string, string>)
    : McpTransport =
    let post (body: string) = async {
        try
            let content = new StringContent(body, Encoding.UTF8, "application/json")
            use req = new HttpRequestMessage(HttpMethod.Post, url)
            req.Content <- content
            req.Headers.Accept.ParseAdd("application/json, text/event-stream")
            for kv in headers do
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value) |> ignore
            let! resp = client.SendAsync(req) |> Async.AwaitTask
            if not resp.IsSuccessStatusCode then
                return Error $"HTTP {int resp.StatusCode} {resp.ReasonPhrase}"
            else
                let ct =
                    match resp.Content.Headers.ContentType with
                    | null -> ""
                    | hdr  -> hdr.MediaType |> Option.ofObj |> Option.defaultValue ""
                if ct.Contains("text/event-stream") then
                    let! stream = resp.Content.ReadAsStreamAsync() |> Async.AwaitTask
                    use sseReader = new StreamReader(stream, Encoding.UTF8)
                    return! readSseResponse sseReader
                else
                    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Ok (if String.IsNullOrWhiteSpace text then "{}" else text)
        with ex ->
            return Error ex.Message
    }

    { Request = post
      Notify  = fun body -> async { try let! _ = post body in () with _ -> () }
      Dispose = fun () -> () }

// ── Server initialization + tool listing ─────────────────────────────────────

let private initServer
    (serverName: string)
    (t         : McpTransport)
    : Async<Result<(string * ToolSpec) list, string>> =
    async {
        // 1. initialize handshake
        match! t.Request (initReq "mcp-init") with
        | Error e -> return Error $"[{serverName}] initialize failed: {e}"
        | Ok initLine ->
            match parseRpc initLine with
            | Error e -> return Error $"[{serverName}] initialize parse error: {e}"
            | Ok rpc ->
                match rpc.Error with
                | Some (code, msg) -> return Error $"[{serverName}] initialize error {code}: {msg}"
                | None ->
                    // 2. notify server that client is ready (must complete before tools/list)
                    do! t.Notify (initializedNotif ())
                    // 3. list tools
                    match! t.Request (listToolsReq "mcp-list") with
                    | Error e -> return Error $"[{serverName}] tools/list failed: {e}"
                    | Ok listLine ->
                        match parseRpc listLine with
                        | Error e -> return Error $"[{serverName}] tools/list parse error: {e}"
                        | Ok rpc ->
                            match rpc.Error with
                            | Some (code, msg) -> return Error $"[{serverName}] tools/list error {code}: {msg}"
                            | None ->
                                let entries =
                                    match rpc.Result with
                                    | None -> []
                                    | Some result ->
                                        match result.TryGetProperty("tools") with
                                        | true, arr when arr.ValueKind = JsonValueKind.Array ->
                                            arr.EnumerateArray()
                                            |> Seq.choose (parseMcpToolEntry serverName)
                                            |> Seq.toList
                                        | _ -> []
                                return Ok entries
    }

// ── Tool executor factory ─────────────────────────────────────────────────────

let private makeExecutor
    (t           : McpTransport)
    (originalName: string)
    (toolTimeout : int)
    : Map<string, JsonElement> -> Async<ToolResult> =
    fun args -> async {
        let guid = Guid.NewGuid().ToString("N").[..7]
        let id   = $"mcp-{guid}"
        let req = callToolReq id originalName args
        use cts = new System.Threading.CancellationTokenSource(toolTimeout * 1000)
        let requestTask = t.Request req
        let! result =
            async {
                try
                    return! requestTask
                with :? OperationCanceledException ->
                    return Error $"tool call timed out after {toolTimeout}s"
            }
        match result with
        | Error e -> return ToolFailure (ExecutionFailed $"[mcp] {e}")
        | Ok line ->
            match parseRpc line with
            | Error e -> return ToolFailure (ExecutionFailed $"[mcp] parse error: {e}")
            | Ok rpc ->
                match rpc.Error with
                | Some (_, msg) -> return ToolFailure (ExecutionFailed $"[mcp] {msg}")
                | None ->
                    match rpc.Result with
                    | None -> return ToolSuccess "(no result)"
                    | Some result ->
                        let isErr =
                            match result.TryGetProperty("isError") with
                            | true, v when v.ValueKind = JsonValueKind.True -> true
                            | _ -> false
                        let content = extractContent result
                        if isErr then return ToolFailure (ExecutionFailed content)
                        else return ToolSuccess content
    }

// ═══════════════════════════════════════════════════════════════════════════
// Public API
// ═══════════════════════════════════════════════════════════════════════════

type ToolPair = ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)

/// Filter an entries list by the server's EnabledTools list.
/// - ["*"] → all tools (default)
/// - []     → no tools
/// - otherwise → only tools whose original name appears in the list
let private filterByEnabledTools (enabledTools: string list) (entries: (string * ToolSpec) list) : (string * ToolSpec) list =
    match enabledTools with
    | ["*"] -> entries          // all tools
    | []    -> []               // none
    | allowed ->
        entries |> List.filter (fun (origName, _) -> List.contains origName allowed)

/// Connect to all configured MCP servers, initialize each, and return:
///   • A list of (ToolSpec × execute) pairs — one per remote tool.
///   • A dispose function that kills stdio processes on shutdown.
///
/// Servers that fail to connect are logged to stderr and skipped;
/// the remaining servers continue to register their tools.
let connectAllMcpServers
    (mcpServers: Map<string, McpServerEntry>)
    (httpClient: HttpClient)
    : Async<ToolPair list * (unit -> unit)> =
    async {
        let pairs    = ResizeArray<ToolPair>()
        let disposes = ResizeArray<unit -> unit>()

        for kv in mcpServers do
            let serverName = kv.Key
            let entry      = kv.Value
            let transportResult =
                match entry.Connection with
                | StdioServer (cmd, args, env) -> createStdioTransport cmd args env
                | HttpServer  (url, headers)   -> Ok (createHttpTransport httpClient url headers)
            match transportResult with
            | Error e ->
                eprintfn "[mcp] server '%s': failed to create transport: %s" serverName e
            | Ok t ->
                disposes.Add(t.Dispose)
                let! result = initServer serverName t
                match result with
                | Error e ->
                    eprintfn "[mcp] server '%s': %s" serverName e
                    t.Dispose()
                | Ok rawEntries ->
                    let filtered = filterByEnabledTools entry.EnabledTools rawEntries
                    for (origName, spec) in filtered do
                        pairs.Add(spec, makeExecutor t origName entry.ToolTimeout)
                    eprintfn "[mcp] server '%s': %d/%d tool(s) registered" serverName filtered.Length rawEntries.Length

        let dispose () =
            for d in disposes do
                try d() with _ -> ()

        return Seq.toList pairs, dispose
    }
