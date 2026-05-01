module BotSharp.Program

open System
open System.IO
open System.Net.Http
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Infrastructure.Config.ConfigLoader
open BotSharp.Infrastructure.Providers.ProviderRegistry
open BotSharp.Infrastructure.Storage.JsonlStore
open BotSharp.Infrastructure.Tools.FileSystemTool
open BotSharp.Infrastructure.Tools.NotebookTool
open BotSharp.Infrastructure.Tools.ShellTool
open BotSharp.Infrastructure.Tools.WebTool
open BotSharp.Infrastructure.Tools.CronTool
open BotSharp.Infrastructure.Tools.MessageTool
open BotSharp.Infrastructure.Tools.MyTool
open BotSharp.Infrastructure.Cron.CronService
open BotSharp.Application.HeartbeatService
open BotSharp.Infrastructure.Skills.DefaultSkills
open BotSharp.Infrastructure.Channels.CliChannel
open BotSharp.Infrastructure.Channels.PocketChannel
open BotSharp.Infrastructure.Tools.PocketToolBridge
open BotSharp.Infrastructure.Channels.OnboardingWizard
open BotSharp.Infrastructure.Channels.TelegramChannel
open BotSharp.Infrastructure.Channels.ApiChannel
open BotSharp.Infrastructure.Channels.WsChannel
open BotSharp.Infrastructure.MessageBus
open BotSharp.Application.AgentLoop
open BotSharp.Application.ContextBuilder
open BotSharp.Application.SessionActor
open BotSharp.Infrastructure.Tools.McpTool

// ═══════════════════════════════════════════════════════════════════════════
// Entry point
//
// Subcommands:
//   gateway              Start headless gateway server (no CLI, API + WS + channels)
//
// Flags:
//   --model <name>       Override default model from config
//   --workspace <path>   Override workspace path from config
//   --port <port>        Gateway port (gateway subcommand only, default 18790)
//   --api-port <port>    Start OpenAI-compatible HTTP API on given port
//   --ws-port <port>     Start WebSocket server on given port
//   --verbose / -v       Verbose output (gateway subcommand only)
// ═══════════════════════════════════════════════════════════════════════════

/// Find a flag value in argv: --flag value → Some value
let private findFlag (flag: string) (argv: string[]) : string option =
    argv
    |> Array.pairwise
    |> Array.tryFind (fun (k, _) -> k = flag)
    |> Option.map snd

/// Check if a bare flag (no value) is present in argv.
let private hasFlag (flag: string) (argv: string[]) : bool =
    argv |> Array.exists (fun a -> a = flag)

[<EntryPoint>]
let main argv =
    // ── Subcommand detection ─────────────────────────────────────────────────
    let isGateway = argv.Length > 0 && argv.[0] = "gateway"
    // Strip "gateway" from argv so flag parsing works uniformly
    let argv = if isGateway then argv.[1..] else argv

    // ── Parse CLI flags ───────────────────────────────────────────────────────
    let modelFlag     = findFlag "--model"     argv
    let workspaceFlag = findFlag "--workspace" argv
    let apiPortFlag   =
        findFlag "--api-port" argv
        |> Option.bind (fun s -> match Int32.TryParse(s) with true, n -> Some n | _ -> None)
    let wsPortFlag    =
        findFlag "--ws-port" argv
        |> Option.bind (fun s -> match Int32.TryParse(s) with true, n -> Some n | _ -> None)
    let gatewayPortFlag =
        findFlag "--port" argv
        |> Option.orElse (findFlag "-p" argv)
        |> Option.bind (fun s -> match Int32.TryParse(s) with true, n -> Some n | _ -> None)
    let verbose = hasFlag "--verbose" argv || hasFlag "-v" argv
    let isPocket = hasFlag "--pocket" argv
    let pocketConfigFlag = findFlag "--config" argv

    // ── Load or bootstrap configuration ──────────────────────────────────────
    // If the config file does not yet exist, run the first-run wizard so the
    // user can configure a provider before any agent logic starts.
    let config =
        let configExists = IO.File.Exists(expandPath defaultConfigPath)
        let baseConfig =
            if configExists then
                loadConfig defaultConfigPath
                |> Async.RunSynchronously
                |> function
                   | Result.Ok c  -> c
                   | Result.Error e ->
                       eprintfn "Warning: config load failed (%s). Using defaults." e
                       BotSharpConfig.defaults
            else
                printfn "No configuration found. Starting setup wizard..."
                match runWizard defaultConfigPath with
                | None ->
                    eprintfn "Setup cancelled. Exiting."
                    Environment.Exit(0)
                    BotSharpConfig.defaults   // unreachable — satisfies type checker
                | Some c -> c
        baseConfig
        |> fun c -> match modelFlag     with Some m -> { c with DefaultModel   = m } | None -> c
        |> fun c -> match workspaceFlag with Some w -> { c with WorkspacePath  = w } | None -> c

    // ── Ensure workspace directory exists + seed default templates ───────────
    // Creates workspace, memory/, and skills/ dirs.
    // Default template files (SOUL.md, USER.md, AGENTS.md, HEARTBEAT.md,
    // TOOLS.md, memory/MEMORY.md) are only written if absent — never overwrites
    // user-customised files.  Mirrors Python's sync_workspace_templates().
    let wp0 = config.WorkspacePath
    Directory.CreateDirectory(wp0)                              |> ignore
    Directory.CreateDirectory(Path.Combine(wp0, "memory"))     |> ignore
    Directory.CreateDirectory(Path.Combine(wp0, "skills"))     |> ignore

    // Install built-in default skills (memory, my) if absent.
    // Existing user-modified SKILL.md files are never overwritten.
    installDefaults wp0

    let writeIfAbsent (path: string) (content: string) =
        if not (File.Exists path) then
            File.WriteAllText(path, content)

    writeIfAbsent (Path.Combine(wp0, "SOUL.md")) """# Soul

I am BotSharp, a personal AI assistant.

## Core Principles

- Solve by doing, not by describing what I would do.
- Keep responses short unless depth is asked for.
- Say what I know, flag what I don't, and never fake confidence.
- Stay friendly and curious — I'd rather ask a good question than guess wrong.
- Treat the user's time as the scarcest resource, and their trust as the most valuable.

## Execution Rules

- Act immediately on single-step tasks — never end a turn with just a plan or promise.
- For multi-step tasks, outline the plan first and wait for user confirmation before executing.
- Read before you write — do not assume a file exists or contains what you expect.
- If a tool call fails, diagnose the error and retry with a different approach before reporting failure.
- When information is missing, look it up with tools first. Only ask the user when tools cannot answer.
- After multi-step changes, verify the result (re-read the file, run the test, check the output).
"""

    writeIfAbsent (Path.Combine(wp0, "USER.md")) """# User Profile

Information about the user to help personalise interactions.

## Basic Information

- **Name**: (your name)
- **Timezone**: (your timezone, e.g., UTC+8)
- **Language**: (preferred language)

## Preferences

### Communication Style

- [ ] Casual
- [ ] Professional
- [ ] Technical

### Response Length

- [ ] Brief and concise
- [ ] Detailed explanations
- [ ] Adaptive based on question
"""

    writeIfAbsent (Path.Combine(wp0, "AGENTS.md")) """# Agent Instructions

## Scheduled Reminders

Use the built-in `cron` tool to create/list/remove recurring jobs.
Do NOT invoke the BotSharp CLI via `exec` for cron management.

## Heartbeat Tasks

`HEARTBEAT.md` is checked on the configured heartbeat interval.
Use file tools to add, remove, or rewrite periodic tasks.

When the user requests a recurring task, edit `HEARTBEAT.md` rather
than creating a one-time reminder.
"""

    writeIfAbsent (Path.Combine(wp0, "HEARTBEAT.md")) """# Heartbeat Tasks

This file is checked periodically by your BotSharp agent.
Add tasks below that you want the agent to work on at each heartbeat.

If this file has no tasks (only headers and comments), the agent will skip.

## Active Tasks

<!-- Add your periodic tasks below this line -->


## Completed

<!-- Move completed tasks here or delete them -->
"""

    writeIfAbsent (Path.Combine(wp0, "TOOLS.md")) """# Tool Usage Notes

Tool signatures are provided automatically via function calling.
This file documents non-obvious constraints and usage patterns.

## exec — Safety Limits

- Commands have a configurable timeout (default 60s).
- Output is truncated at the max_tool_result_chars limit.
- Do not use exec to call BotSharp itself recursively.

## glob — File Discovery

- Use glob to find files by pattern before falling back to shell commands.
- Prefer glob over exec when you only need file paths.

## grep — Content Search

- Default behaviour returns only matching file paths (files_with_matches).
- Use output_mode="content" to see matching lines.
- Use head_limit and offset to page through large result sets.
"""

    writeIfAbsent (Path.Combine(wp0, "memory", "MEMORY.md")) """# Long-term Memory

This file stores important information that persists across sessions.

## User Information

(Important facts about the user)

## Preferences

(User preferences learned over time)

## Project Context

(Information about ongoing projects)

## Important Notes

(Things to remember)

---

*This file is automatically updated by BotSharp when important information should be remembered.*
"""

    // ── Resolve provider ──────────────────────────────────────────────────────
    // LLM requests can take minutes (reasoning models, large context).
    // Set a generous timeout to prevent silent hangs while avoiding infinite waits.
    // Port of nanobot#3428 + #3478: bound request timeouts.
    use httpClient = new HttpClient()
    httpClient.Timeout <- TimeSpan.FromSeconds(300.0)   // 5 min — covers long reasoning + streaming

    // ── Dedicated web-tool HttpClient (proxy + timeout) ───────────────────────
    let webHandler =
        match config.WebProxyUrl with
        | None ->
            new System.Net.Http.HttpClientHandler()
        | Some proxyUrl ->
            let h = new System.Net.Http.HttpClientHandler()
            h.Proxy       <- new System.Net.WebProxy(proxyUrl)
            h.UseProxy    <- true
            h
    use webHttpClient = new HttpClient(webHandler)
    webHttpClient.Timeout <- System.TimeSpan.FromSeconds(float config.WebSearchTimeout)

    let provider =
        match resolve httpClient config.DefaultModel config with
        | Some p -> p
        | None   ->
            eprintfn "Warning: no API key found for model '%s'." config.DefaultModel
            eprintfn "Set the appropriate environment variable (e.g. OPENAI_API_KEY) and retry."
            eprintfn "Starting with a placeholder provider — responses will be empty."
            { Id           = "noop"
              DefaultModel = config.DefaultModel
              Capabilities = Set.empty
              RetryPolicy  = RetryPolicy.standard
              Chat         = fun _ _ _ -> async {
                  return Result.Ok {
                      Body             = TextOnly "(no API key configured)"
                      ReasoningContent = None
                      ThinkingBlocks   = []
                      Usage            = { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }
                      FinishReason     = None
                  }
              }
              ChatStream = fun _ _ _ _ ->
                  async {
                      return Result.Error {
                          Kind         = ConnectionFailed "No API key configured — set the appropriate environment variable"
                          RawMessage   = "noop provider"
                          ProviderCode = None }
                  } }

    // Resolve fallback providers from config.FallbackModels
    let fallbackProviders =
        config.FallbackModels
        |> List.choose (fun model -> resolve httpClient model config)
        |> List.filter (fun p -> p.Id <> provider.Id)
    if not fallbackProviders.IsEmpty then
        printfn "Fallback providers: %s" (fallbackProviders |> List.map (fun p -> $"{p.Id}:{p.DefaultModel}") |> String.concat " -> ")

    // ── Auto-detect context window when not configured ────────────────────────
    // When context_window_tokens = 0 (the default), look up the model name
    // in the registry table. This enables context-window trimming automatically
    // for well-known models without requiring manual config.
    let config =
        if config.ContextWindowTokens > 0 then config
        else
            let detected = resolveContextWindow config.DefaultModel
            if detected > 0 then { config with ContextWindowTokens = detected }
            else config

    // ── Workspace shorthand ───────────────────────────────────────────────────
    let wp = config.WorkspacePath

    // ── MCP servers — connect at startup, before building tool map ────────────
    // Servers that fail to connect are logged to stderr and skipped.
    let mcpToolPairs, disposeMcp =
        if Map.isEmpty config.McpServers then
            [], fun () -> ()
        else
            connectAllMcpServers config.McpServers httpClient
            |> Async.RunSynchronously

    // ── CronService — must be wired before deps (callback closes over coordinator) ──
    // The coordinator is created after deps, so we use a mutable reference to
    // break the cycle: cronSvc captures routeRef; Program.fs sets it after
    // coordinator is available.
    let mutable routeRef : (InboundMessage -> Async<unit>) =
        fun _ -> async { return () }   // placeholder; replaced below

    // Model switch callback — returns (newModelName, newProviderName) or error.
    // Set after provider is resolved; called by /model command handlers.
    let mutable switchModelRef : (string -> Async<Result<string * string, string>>) =
        fun _ -> async { return Error "not initialized" }

    // Cron coordinator ref — set once coordinator is built (same mutable ref cycle-break pattern).
    let mutable cronRouteRef : (InboundMessage -> Async<Result<AgentResult, AgentError>>) =
        fun _ -> async { return Result.Ok (PlainResponse "") }
    // Cron send ref — set once sendRef is wired up.
    let mutable cronSendRef : (OutboundMessage -> Async<unit>) =
        fun msg -> async { printfn "\n[cron] %s" msg.Content }

    let cronSvc =
        CronService(config.WorkspacePath, fun job ->
            async {
                // Port of nanobot on_cron_job (cli/commands.py:297-335):
                // 1. Route the scheduled task through the agent loop
                // 2. Send the agent's reply to the target channel
                let reminderNote =
                    $"[Scheduled Task] Timer finished.\n\nTask '{job.Label}' has been triggered.\nScheduled instruction: {job.Task}"
                let inbound : InboundMessage = {
                    Channel            = job.Channel
                    Sender             = UserId "cron"
                    Chat               = job.Chat
                    Input              = ChatMessage (reminderNote, [])
                    Metadata           = Map.ofList [ "source", "cron"; "job_id", (let (TaskId v) = job.Id in v) ]
                    SessionKeyOverride = Some (SessionId $"cron:{let (TaskId v) = job.Id in v}")
                }
                let! result = cronRouteRef inbound
                // Deliver the reply to the target channel
                match result with
                | Result.Ok (PlainResponse text) | Result.Ok (StreamedResponse text) when not (String.IsNullOrWhiteSpace text) ->
                    let outbound : OutboundMessage = {
                        Channel     = job.Channel
                        Chat        = job.Chat
                        Content     = text
                        ReplyTo     = None
                        Attachments = []
                        Buttons     = []
                    }
                    do! cronSendRef outbound
                | Result.Error e ->
                    eprintfn "[Cron] Job '%s' failed: %A" (let (TaskId v) = job.Id in v) e
                | _ -> ()
            })

    let cronToolPair =
        BotSharp.Infrastructure.Tools.CronTool.allTools cronSvc config.Timezone

    // MessageTool send callback uses a mutable reference (same cycle-break pattern
    // as cronSvc), since the port is created after deps.
    let mutable sendRef : (OutboundMessage -> Async<unit>) =
        fun msg -> async { printfn "\nassistant> %s" msg.Content }  // CLI fallback

    // Telegram outbound send — set by startTelegram's onBotReady callback once the bot is initialised.
    let mutable telegramSendOpt : (OutboundMessage -> Async<unit>) option = None

    let msgToolPair =
        BotSharp.Infrastructure.Tools.MessageTool.allTools (fun msg -> sendRef msg)

    // ── SubagentManager ───────────────────────────────────────────────────────
    // SubagentManager is created with the base tool set (file/shell/web/cron but
    // NOT spawn/message — subagents cannot recursively spawn or push messages).
    // The spawn route callback uses the same mutable routeRef as cronSvc.
    let mutable spawnRouteRef : (InboundMessage -> Async<unit>) =
        fun _ -> async { return () }   // placeholder; replaced after coordinator is built

    // ── Register tools ────────────────────────────────────────────────────────
    let addToolPairs
        (pairs : (ToolSpec * (Map<string, System.Text.Json.JsonElement> -> Async<ToolResult>)) list)
        (m     : Map<ToolName, ToolSpec * (Map<string, System.Text.Json.JsonElement> -> Async<ToolResult>)>)
        =
        pairs |> List.fold (fun acc (spec, fn) -> Map.add spec.Name (spec, fn) acc) m

    // Base tools shared by both the main agent and subagents.
    let baseToolMap : Map<ToolName, ToolSpec * (Map<string, System.Text.Json.JsonElement> -> Async<ToolResult>)> =
        let fsTools =
            Map.ofList [
                readFileSpec.Name,     (readFileSpec,     readFile       wp config.FileReadMaxChars)
                writeFileSpec.Name,    (writeFileSpec,    writeFile      wp)
                listDirSpec.Name,      (listDirSpec,      listDir        wp)
                editFileSpec.Name,     (editFileSpec,     editFile       wp)
                notebookEditSpec.Name, (notebookEditSpec, execNotebookEdit wp)
            ]
        // exec is optional (tools.exec.enable)
        let withExec =
            if config.ExecEnable then
                fsTools |> Map.add execSpec.Name (execSpec, exec wp config.ExecTimeoutSeconds config.RestrictToWorkspace config.ExecPathAppend config.ExecAllowedEnvKeys config.SsrfWhitelist config.ExecSandbox)
            else fsTools
        // web tools are optional (tools.web.enable)
        let withWeb =
            if config.WebEnable then
                withExec
                |> addToolPairs (BotSharp.Infrastructure.Tools.WebTool.allTools webHttpClient config.BraveApiKey config.WebSearchProvider config.WebSearchMaxResults config.WebSearchApiKey config.WebSearchBaseUrl)
            else withExec
        // my tool is optional (tools.my.enable)
        let withMy =
            if config.MyToolEnable then
                withWeb |> addToolPairs (BotSharp.Infrastructure.Tools.MyTool.allTools config (fun () -> None) (fun () -> 0))
            else withWeb
        withMy
        |> addToolPairs cronToolPair
        |> addToolPairs mcpToolPairs

    // Build a minimal deps for subagents: base tools, ephemeral sessions, no streaming.
    let subagentBaseDeps : AgentDependencies = {
        Provider          = provider
        Tools             = baseToolMap
        LoadSession       = fun sid -> async { return Result.Ok (BotSharp.Domain.Types.SessionSnapshot.empty sid System.DateTimeOffset.UtcNow) }
        PersistSession    = fun _   -> async { return Result.Ok () }
        BuildSystemPrompt = buildSystemPrompt config.DisabledSkills config.SystemPromptAppend
        Config            = config
        StreamHook        = NoStreaming
        Hook              = AgentHook.none
        CronService       = Some cronSvc
        LastTokenUsage    = ref None   // subagents are ephemeral; actors override per session
        CurrentIteration  = ref 0
        RuleEngine        = None   // subagents don't need rule engine
        FallbackProviders = fallbackProviders
    }

    let subagentMgr =
        BotSharp.Application.SubagentManager.SubagentManager(
            subagentBaseDeps,
            fun msg -> spawnRouteRef msg)

    let spawnToolPair =
        BotSharp.Infrastructure.Tools.SpawnTool.allTools subagentMgr

    // ── CLIPS rule engine (graceful fallback if native lib not available) ────
    let ruleEngine =
        try Some (BotSharp.Infrastructure.Rules.RuleEngine.create config.WorkspacePath)
        with ex ->
            eprintfn "[RuleEngine] CLIPS not available: %s" ex.Message
            None

    // Configuration validation via rule engine
    match ruleEngine with
    | Some engine ->
        if config.ContextWindowTokens > 0 && config.MaxTokens >= config.ContextWindowTokens then
            BotSharp.Infrastructure.Rules.RuleEngine.assertConfigIssue engine "max_tokens" "error"
                $"max_tokens ({config.MaxTokens}) exceeds context_window ({config.ContextWindowTokens})"
        let configActions = BotSharp.Infrastructure.Rules.RuleEngine.evaluate engine
        for action in configActions do
            match action with
            | BotSharp.Infrastructure.Rules.RuleEngine.StopLoop reason ->
                eprintfn "[RuleEngine] Configuration error: %s" reason
            | _ -> ()
        BotSharp.Infrastructure.Rules.RuleEngine.resetTurn engine
    | None -> ()

    let longTaskToolPair =
        BotSharp.Infrastructure.Tools.LongTaskTool.allTools
            (fun extraToolPairs sysPrompt userMsg -> subagentMgr.RunStep(extraToolPairs, sysPrompt, userMsg))
            ruleEngine

    let allToolsMap : Map<ToolName, ToolSpec * (Map<string, System.Text.Json.JsonElement> -> Async<ToolResult>)> =
        baseToolMap
        |> addToolPairs msgToolPair
        |> addToolPairs spawnToolPair
        |> addToolPairs longTaskToolPair

    // ── Build agent dependencies ──────────────────────────────────────────────
    let deps : AgentDependencies = {
        Provider          = provider
        Tools             = allToolsMap
        LoadSession       = fun sid -> loadSession sid wp
        PersistSession    = fun snap -> persistSession snap wp
        BuildSystemPrompt = buildSystemPrompt config.DisabledSkills config.SystemPromptAppend
        Config            = config
        StreamHook        = if isGateway then NoStreaming else cliStreamHook
        Hook              = if isGateway then AgentHook.none else cliAgentHook true config.SendToolHints
        CronService       = Some cronSvc
        LastTokenUsage    = ref None   // overridden per session actor in createSessionActor
        CurrentIteration  = ref 0
        RuleEngine        = ruleEngine
        FallbackProviders = fallbackProviders
    }

    // ── Wire up the system ────────────────────────────────────────────────────
    let coordinator = AgentCoordinator(deps)
    // Set routing callbacks now that coordinator exists.
    routeRef <- fun msg -> async {
        let! _ = coordinator.Route msg
        return ()
    }
    cronRouteRef <- fun msg -> coordinator.Route msg
    spawnRouteRef <- routeRef   // subagent announcements go through the same coordinator

    // List models that have API keys configured
    let listAvailableModels () =
        providers
        |> NonEmptyList.toList
        |> List.choose (fun spec ->
            match resolveApiKey spec config with
            | Some _ -> Some (spec.Id, config.DefaultModel)   // (providerId, currentDefault)
            | None -> None)
        |> List.map (fun (pid, current) ->
            let isActive = pid = config.DefaultProvider
            if isActive then $"  {config.DefaultModel} ({pid}) ✓"
            else $"  ({pid})")

    // Wire model switch callback
    switchModelRef <- fun modelName -> async {
        // 1. Detect provider for the new model
        match detectProvider modelName with
        | None -> return Error $"Unknown model '{modelName}'. No matching provider found."
        | Some spec ->
            // 2. Check if we have an API key for this provider
            match resolveApiKey spec config with
            | None -> return Error $"No API key configured for provider '{spec.Id}'."
            | Some _ ->
                // 3. Update config.json
                let configPath = expandPath defaultConfigPath
                let newConfig = { config with DefaultModel = modelName; DefaultProvider = spec.Id }
                let! saveResult = BotSharp.Infrastructure.Config.ConfigWriter.saveConfig configPath newConfig
                match saveResult with
                | Error msg -> return Error $"Failed to save config: {msg}"
                | Ok () ->
                    eprintfn "[/model] Switched to %s (provider: %s), saved to config.json" modelName spec.Id
                    return Ok (modelName, spec.Id)
    }

    // ── API server (start function, shared by both modes) ─────────────────────
    let startApiServer (port: int) (host: string) =
        let timeoutMs = config.ApiTimeoutSeconds * 1_000
        let apiDeps = { deps with StreamHook = NoStreaming }
        let apiCoordinator = AgentCoordinator(apiDeps)
        let server = ApiServer(apiCoordinator, config.DefaultModel, timeoutMs)
        Async.Start(server.Start(port, host))
        server

    // ── WS server (start function, shared by both modes) ──────────────────────
    let startWsServer (port: int) (token: string option) =
        let wsDeps = { deps with StreamHook = NoStreaming }
        let server = WsServer(wsDeps, token)
        Async.Start(server.Start(port))
        server

    // ── Discord server (start function, shared by both modes) ─────────────
    let startDiscordServer (dcCfg: DiscordChannelConfig) =
        let dcDeps = { deps with StreamHook = NoStreaming }
        let dcCoordinator = AgentCoordinator(dcDeps)
        let dcConfig : BotSharp.Infrastructure.Channels.DiscordChannel.DiscordConfig = {
            Token     = dcCfg.Token
            AllowFrom = dcCfg.AllowFrom
        }
        let server = BotSharp.Infrastructure.Channels.DiscordChannel.DiscordServer(dcCoordinator, dcConfig)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── Slack server (start function, shared by both modes) ────────────────
    let startSlackServer (slCfg: SlackChannelConfig) =
        let slDeps = { deps with StreamHook = NoStreaming }
        let slCoordinator = AgentCoordinator(slDeps)
        let slConfig : BotSharp.Infrastructure.Channels.SlackChannel.SlackConfig = {
            BotToken      = slCfg.BotToken
            AppToken      = slCfg.AppToken
            AllowFrom     = slCfg.AllowFrom
            ReplyInThread = slCfg.ReplyInThread
        }
        let server = BotSharp.Infrastructure.Channels.SlackChannel.SlackServer(slCoordinator, slConfig, httpClient)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── Feishu server (start function, shared by both modes) ───────────────
    let startFeishuServer (fsCfg: FeishuChannelConfig) =
        let fsDeps = { deps with StreamHook = NoStreaming }
        let fsCoordinator = AgentCoordinator(fsDeps)
        let fsConfig : BotSharp.Infrastructure.Channels.FeishuChannel.FeishuConfig = {
            AppId = fsCfg.AppId; AppSecret = fsCfg.AppSecret
            VerificationToken = fsCfg.VerificationToken
            AllowFrom = fsCfg.AllowFrom; WebhookPort = fsCfg.WebhookPort
        }
        let server = BotSharp.Infrastructure.Channels.FeishuChannel.FeishuServer(fsCoordinator, fsConfig, httpClient)
        Async.Start(server.Start())
        server

    // ── DingTalk server (start function, shared by both modes) ─────────────
    let startDingTalkServer (dtCfg: DingTalkChannelConfig) =
        let dtDeps = { deps with StreamHook = NoStreaming }
        let dtCoordinator = AgentCoordinator(dtDeps)
        let dtConfig : BotSharp.Infrastructure.Channels.DingTalkChannel.DingTalkConfig = {
            ClientId = dtCfg.ClientId; ClientSecret = dtCfg.ClientSecret
            AllowFrom = dtCfg.AllowFrom; WebhookPort = dtCfg.WebhookPort
        }
        let server = BotSharp.Infrastructure.Channels.DingTalkChannel.DingTalkServer(dtCoordinator, dtConfig, httpClient)
        Async.Start(server.Start())
        server

    // ── Email server (start function, shared by both modes) ────────────────
    let startEmailServer (emCfg: EmailChannelConfig) =
        let emDeps = { deps with StreamHook = NoStreaming }
        let emCoordinator = AgentCoordinator(emDeps)
        let emConfig : BotSharp.Infrastructure.Channels.EmailChannel.EmailConfig = {
            ImapHost = emCfg.ImapHost; ImapPort = emCfg.ImapPort; ImapUseSsl = emCfg.ImapUseSsl
            SmtpHost = emCfg.SmtpHost; SmtpPort = emCfg.SmtpPort; SmtpUseTls = emCfg.SmtpUseTls
            Username = emCfg.Username; Password = emCfg.Password
            PollSeconds = emCfg.PollSeconds; AllowFrom = emCfg.AllowFrom
        }
        let server = BotSharp.Infrastructure.Channels.EmailChannel.EmailServer(emCoordinator, emConfig)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── WhatsApp server (start function, shared by both modes) ─────────────
    let startWhatsAppServer (waCfg: WhatsAppChannelConfig) =
        let waDeps = { deps with StreamHook = NoStreaming }
        let waCoordinator = AgentCoordinator(waDeps)
        let waConf : BotSharp.Infrastructure.Channels.WhatsAppChannel.WhatsAppConfig = {
            PhoneNumberId = waCfg.PhoneNumberId; AccessToken = waCfg.AccessToken
            VerifyToken = waCfg.VerifyToken; WebhookPort = waCfg.WebhookPort; AllowFrom = waCfg.AllowFrom
        }
        let server = BotSharp.Infrastructure.Channels.WhatsAppChannel.WhatsAppServer(waCoordinator, waConf, httpClient)
        Async.Start(server.Start())
        server

    // ── MoChat server (start function, shared by both modes) ────────────────
    let startMoChatServer (mcCfg: MoChatChannelConfig) =
        let mcDeps = { deps with StreamHook = NoStreaming }
        let mcCoordinator = AgentCoordinator(mcDeps)
        let mcConf : BotSharp.Infrastructure.Channels.MoChatChannel.MoChatConfig = {
            BaseUrl = mcCfg.BaseUrl; ClawToken = mcCfg.ClawToken
            PollSeconds = mcCfg.PollSeconds; AllowFrom = mcCfg.AllowFrom
        }
        let server = BotSharp.Infrastructure.Channels.MoChatChannel.MoChatServer(mcCoordinator, mcConf, httpClient)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── QQ server (start function, shared by both modes) ───────────────────
    let startQQServer (qqCfg: QQChannelConfig) =
        let qqDeps = { deps with StreamHook = NoStreaming }
        let qqCoordinator = AgentCoordinator(qqDeps)
        let qqConf : BotSharp.Infrastructure.Channels.QQChannel.QQConfig = {
            AppId = qqCfg.AppId; Secret = qqCfg.Secret
            AllowFrom = qqCfg.AllowFrom; Sandbox = qqCfg.Sandbox
        }
        let server = BotSharp.Infrastructure.Channels.QQChannel.QQServer(qqCoordinator, qqConf, httpClient)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── Matrix server (start function, shared by both modes) ───────────────
    let startMatrixServer (mxCfg: MatrixChannelConfig) =
        let mxDeps = { deps with StreamHook = NoStreaming }
        let mxCoordinator = AgentCoordinator(mxDeps)
        let mxConfig : BotSharp.Infrastructure.Channels.MatrixChannel.MatrixConfig = {
            Homeserver = mxCfg.Homeserver; UserId = mxCfg.UserId
            AccessToken = mxCfg.AccessToken; AllowFrom = mxCfg.AllowFrom
        }
        let server = BotSharp.Infrastructure.Channels.MatrixChannel.MatrixServer(mxCoordinator, mxConfig, httpClient)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── Telnet server (start function, shared by both modes) ───────────────
    let startTelnetServer (tnCfg: TelnetChannelConfig) =
        let tnDeps = { deps with StreamHook = NoStreaming }
        let tnCoordinator = AgentCoordinator(tnDeps)
        let tnConfig : BotSharp.Infrastructure.Channels.TelnetChannel.TelnetConfig = {
            Port = tnCfg.Port; AllowFrom = tnCfg.AllowFrom
        }
        let server = BotSharp.Infrastructure.Channels.TelnetChannel.TelnetServer(tnCoordinator, tnConfig)
        let cts = new System.Threading.CancellationTokenSource()
        Async.Start(server.Start(), cts.Token)
        (server, cts)

    // ── Inter-agent server (start function, shared by both modes) ──────────
    let startInterAgentServer (iaCfg: InterAgentChannelConfig) =
        let iaDeps = { deps with StreamHook = NoStreaming }
        let iaCoordinator = AgentCoordinator(iaDeps)
        let server = BotSharp.Infrastructure.Channels.InterAgentChannel.InterAgentServer(iaCoordinator, iaCfg, httpClient, ruleEngine)
        Async.Start(server.Start())
        server

    // ── HeartbeatService ──────────────────────────────────────────────────────
    let heartbeatSvc =
        HeartbeatService(
            config.WorkspacePath,
            provider,
            config.DefaultModel,
            onExecute = (fun tasks ->
                async {
                    let taskText = String.concat "; " tasks
                    let msg : InboundMessage = {
                        Channel            = ChannelId "cli"
                        Sender             = UserId "heartbeat"
                        Chat               = ChatId "cli-session"
                        Input              = ChatMessage (taskText, [])
                        Metadata           = Map.ofList [ "source", "heartbeat" ]
                        SessionKeyOverride = None
                    }
                    let! result = coordinator.Route msg
                    return
                        match result with
                        | Result.Ok (PlainResponse text) -> Some text
                        | Result.Ok (StreamedResponse _) -> None   // already printed
                        | Result.Error _                 -> None
                }),
            onNotify = (fun text ->
                async {
                    if verbose then eprintfn "[heartbeat] %s" text
                }),
            intervalSeconds = config.HeartbeatIntervalSeconds)
    if config.HeartbeatEnabled then
        heartbeatSvc.Start()

    // ── Dream scheduler (DreamIntervalHours > 0 = automatic consolidation) ───
    // When DreamIntervalHours > 0, periodically run memory consolidation for all
    // active sessions and persist results via DreamStore (Python: dream.interval_h).
    if config.DreamIntervalHours > 0 then
        let dreamIntervalMs = config.DreamIntervalHours * 3600 * 1000
        let dreamCts = new System.Threading.CancellationTokenSource()
        Async.Start(
            async {
                while not dreamCts.Token.IsCancellationRequested do
                    try
                        do! Async.Sleep dreamIntervalMs
                        if not dreamCts.Token.IsCancellationRequested then
                            let sids = coordinator.GetActiveSessionIds()
                            for sid in sids do
                                let! result = coordinator.Consolidate(sid)
                                match result with
                                | Result.Ok (Consolidated (summary, _, newIdx)) ->
                                    let sha = BotSharp.Infrastructure.Storage.DreamStore.makeSha
                                                  (System.DateTimeOffset.UtcNow.ToString("o") + summary)
                                    let entry : DreamEntry = {
                                        Sha          = sha
                                        OccurredAt   = System.DateTimeOffset.UtcNow
                                        Summary      = summary
                                        MessageCount = newIdx
                                    }
                                    let! _ = BotSharp.Infrastructure.Storage.DreamStore.appendDreamEntry
                                                 config.WorkspacePath entry
                                    ()
                                | _ -> ()
                    with
                    | :? System.OperationCanceledException -> ()
                    | ex -> eprintfn "[dream] auto-consolidation error: %s" ex.Message
            },
            dreamCts.Token)

    // ── AutoCompactService (optional) ─────────────────────────────────────────
    let autoCompactTtl =
        if config.SessionTtlMinutes > 0 then config.SessionTtlMinutes
        elif config.MemoryWindowSize > 0 then 0
        else 0
    let autoCompactSvc =
        BotSharp.Application.AutoCompactService.AutoCompactService(
            deps,
            (fun () -> coordinator.GetActiveSessionIds()),
            autoCompactTtl)
    autoCompactSvc.Start()

    // ── Session cleanup service (delete expired session files) ──────────────
    let sessionCleanupSvc =
        BotSharp.Application.SessionCleanupService.SessionCleanupService(
            config.WorkspacePath, config.SessionCleanupDays, ruleEngine)
    sessionCleanupSvc.Start()

    // ═══════════════════════════════════════════════════════════════════════════
    // Mode dispatch: pocket vs gateway (headless) vs CLI (interactive)
    // ═══════════════════════════════════════════════════════════════════════════

    if isPocket then
        // ── Pocket mode ─────────────────────────────────────────────────────
        // Connects to botx-pocket HostBridge via Unix domain socket.
        // Replaces botx as primary agent inside the pocket mobile container.
        let pocketCfg =
            match pocketConfigFlag with
            | Some path ->
                try
                    let text = File.ReadAllText(path)
                    use doc = JsonDocument.Parse(text)
                    let el : JsonElement = doc.RootElement
                    let tryStr (name: string) =
                        match el.TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
                        | _ -> None
                    let tryInt (name: string) =
                        match el.TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetInt32())
                        | _ -> None
                    let tryArr (name: string) =
                        match el.TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.Array ->
                            v.EnumerateArray()
                            |> Seq.choose (fun (e: JsonElement) ->
                                if e.ValueKind = JsonValueKind.String then e.GetString() |> Option.ofObj else None)
                            |> Seq.toList |> Some
                        | _ -> None
                    { PocketChannelConfig.defaults with
                        SocketName    = tryStr "socket_name"    |> Option.defaultValue PocketChannelConfig.defaults.SocketName
                        AgentId       = tryStr "agent_id"       |> Option.defaultValue PocketChannelConfig.defaults.AgentId
                        DisplayName   = tryStr "display_name"   |> Option.defaultValue PocketChannelConfig.defaults.DisplayName
                        PollTimeoutMs = tryInt "poll_timeout_ms" |> Option.defaultValue PocketChannelConfig.defaults.PollTimeoutMs
                        Capabilities  = tryArr "capabilities"   |> Option.defaultValue PocketChannelConfig.defaults.Capabilities
                    }
                with ex ->
                    eprintfn "[pocket] Failed to load config from '%s': %s. Using defaults." path ex.Message
                    PocketChannelConfig.defaults
            | None ->
                PocketChannelConfig.defaults

        if pocketCfg.SocketName = "" then
            eprintfn "[pocket] Error: socket_name not configured. Use --config <path> with socket_name field."
            1
        else
            // Validate pocket config via CLIPS rules (or F# fallback)
            let configErrors = validatePocketConfig pocketCfg config ruleEngine
            if not (List.isEmpty configErrors) then
                eprintfn "[pocket] Config validation errors:"
                for (field, msg) in configErrors do
                    eprintfn "  [%s] %s" field msg

            eprintfn "[pocket] Connecting to HostBridge socket: %s" pocketCfg.SocketName
            let rpc = new PocketRpcClient(pocketCfg.SocketName)
            let sessionKeyRef : string option ref = ref None

            // Register pocket native tools (HostBridge JSON-RPC methods)
            let pocketTools = createPocketTools rpc
            let pocketToolMap =
                pocketTools
                |> List.fold (fun (m: Map<ToolName, ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)>) (spec, exec) ->
                    Map.add spec.Name (spec, exec) m) deps.Tools
            eprintfn "[pocket] Registered %s native device tools" (string pocketTools.Length)

            let pocketDeps = {
                deps with
                    Tools      = pocketToolMap
                    StreamHook = pocketStreamHook rpc pocketCfg.AgentId sessionKeyRef
                    Hook       = pocketAgentHook rpc config.SendToolHints
            }
            let pocketCoordinator = AgentCoordinator(pocketDeps)

            rpc.ChatRegister(pocketCfg.AgentId, pocketCfg.DisplayName, pocketCfg.Capabilities)
            |> Async.RunSynchronously
            eprintfn "[pocket] Registered as '%s'. Entering poll loop." pocketCfg.AgentId

            let port = createPocketPort rpc pocketCfg sessionKeyRef
            startPocket pocketCoordinator port rpc |> Async.RunSynchronously

            rpc.Dispose()
            autoCompactSvc.Stop()
            sessionCleanupSvc.Stop()
            disposeMcp ()
            0

    elif isGateway then
        // ── Gateway mode ─────────────────────────────────────────────────────
        // Headless server — no stdin, no CLI loop.
        // Starts API + WS + Telegram channels, blocks until Ctrl-C.
        let gatewayPort = gatewayPortFlag |> Option.defaultValue 18790
        let apiHost     = config.ApiHost

        // API server: --port sets the default; --api-port overrides if given
        let apiPort = apiPortFlag |> Option.defaultValue gatewayPort
        let apiServer = startApiServer apiPort apiHost

        // WS server: only if explicitly configured (--ws-port or config)
        let wsServerOpt =
            let wsEffective =
                match wsPortFlag with
                | Some port ->
                    let wsToken = config.Ws |> Option.bind (fun w -> w.Token) |> Option.map ApiKey.value
                    Some (port, wsToken)
                | None ->
                    config.Ws
                    |> Option.filter (fun w -> w.Enabled)
                    |> Option.map (fun w -> w.Port, w.Token |> Option.map ApiKey.value)
            match wsEffective with
            | Some (port, wsToken) -> Some (startWsServer port wsToken)
            | None -> None

        // Discord: start if configured
        let dcServerOpt =
            match config.Discord with
            | Some dcCfg -> Some (startDiscordServer dcCfg)
            | None -> None

        // Slack: start if configured
        let slServerOpt =
            match config.Slack with
            | Some slCfg -> Some (startSlackServer slCfg)
            | None -> None

        // Feishu: start if configured
        let fsServerOpt =
            match config.Feishu with
            | Some fsCfg -> Some (startFeishuServer fsCfg)
            | None -> None

        // DingTalk: start if configured
        let dtServerOpt =
            match config.DingTalk with
            | Some dtCfg -> Some (startDingTalkServer dtCfg)
            | None -> None

        // Email: start if configured
        let emServerOpt =
            match config.Email with
            | Some emCfg -> Some (startEmailServer emCfg)
            | None -> None

        // Telnet: start if configured
        let tnServerOpt =
            match config.Telnet with
            | Some tnCfg -> Some (startTelnetServer tnCfg)
            | None -> None

        // Matrix: start if configured
        let mxServerOpt =
            match config.Matrix with
            | Some mxCfg -> Some (startMatrixServer mxCfg)
            | None -> None

        // QQ: start if configured
        let qqServerOpt =
            match config.QQ with
            | Some qqCfg -> Some (startQQServer qqCfg)
            | None -> None

        // WhatsApp: start if configured
        let waServerOpt =
            match config.WhatsApp with
            | Some waCfg -> Some (startWhatsAppServer waCfg)
            | None -> None

        // MoChat: start if configured
        let mcServerOpt =
            match config.MoChat with
            | Some mcCfg -> Some (startMoChatServer mcCfg)
            | None -> None

        // Inter-agent: start if configured
        let iaServerOpt =
            match config.InterAgent with
            | Some iaCfg when iaCfg.Enabled -> Some (startInterAgentServer iaCfg)
            | _ -> None

        // Telegram: start if configured
        let tgCts = new System.Threading.CancellationTokenSource()
        match config.Telegram with
        | Some tgConfig ->
            Async.Start(
                startTelegram tgConfig deps httpClient tgCts.Token
                    (fun bot cfg -> telegramSendOpt <- Some (sendOutboundMessage bot cfg))
                    (fun m -> switchModelRef m) (fun () -> listAvailableModels ()),
                tgCts.Token)
            printfn "[gateway] Telegram channel started"
        | None -> ()

        // MessageTool send — route to Telegram when channel=telegram, else log
        sendRef <- fun msg -> async {
            let (ChannelId ch) = msg.Channel
            if ch = "telegram" then
                match telegramSendOpt with
                | Some tgSend -> do! tgSend msg
                | None -> eprintfn "[message] Telegram not ready, dropping message"
            else
                if verbose then eprintfn "[message] -> %s:%s  %s"
                                            ch (let (ChatId c) = msg.Chat in c) msg.Content
        }
        cronSendRef <- fun msg -> sendRef msg   // cron jobs deliver via same send pipeline

        // Banner
        printfn "BotSharp gateway — model: %s" config.DefaultModel
        printfn "Workspace:  %s" config.WorkspacePath
        printfn "API server: http://%s:%d/v1/chat/completions" apiHost apiPort
        wsServerOpt |> Option.iter (fun _ ->
            let wsPort = wsPortFlag |> Option.orElse (config.Ws |> Option.map (fun w -> w.Port)) |> Option.defaultValue 9090
            printfn "WS server:  ws://%s:%d/ws" apiHost wsPort)
        printfn "Press Ctrl-C to stop."

        // Block until Ctrl-C
        let exitEvent = new System.Threading.ManualResetEventSlim(false)
        Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true
            printfn "\nShutting down..."
            exitEvent.Set())
        exitEvent.Wait()

        // Teardown
        tgCts.Cancel()
        apiServer.Stop()
        wsServerOpt |> Option.iter (fun s -> s.Stop())
        dcServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        slServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        fsServerOpt |> Option.iter (fun s -> s.Stop())
        dtServerOpt |> Option.iter (fun s -> s.Stop())
        emServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        tnServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        mxServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        qqServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        waServerOpt |> Option.iter (fun s -> s.Stop())
        mcServerOpt |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        iaServerOpt |> Option.iter (fun s -> s.Stop())
        autoCompactSvc.Stop()
        sessionCleanupSvc.Stop()
        disposeMcp ()
        0

    else
        // ── CLI mode (original behaviour) ────────────────────────────────────
        let port = createCliPort ()
        sendRef <- fun msg -> async {
            let (ChannelId ch) = msg.Channel
            if ch = "telegram" then
                match telegramSendOpt with
                | Some tgSend -> do! tgSend msg
                | None -> do! port.Send msg
            else
                do! port.Send msg
        }
        cronSendRef <- fun msg -> sendRef msg

        let apiServerOpt =
            let effectiveApiPort =
                match apiPortFlag with
                | Some p -> Some p
                | None   -> config.ApiPort
            match effectiveApiPort with
            | Some port -> Some (startApiServer port config.ApiHost)
            | None -> None

        let wsServerOpt =
            let wsEffective =
                match wsPortFlag with
                | Some port ->
                    let wsToken = config.Ws |> Option.bind (fun w -> w.Token) |> Option.map ApiKey.value
                    Some (port, wsToken)
                | None ->
                    config.Ws
                    |> Option.filter (fun w -> w.Enabled)
                    |> Option.map (fun w -> w.Port, w.Token |> Option.map ApiKey.value)
            match wsEffective with
            | Some (port, wsToken) -> Some (startWsServer port wsToken)
            | None -> None

        // Discord: start if configured
        let dcServerOpt =
            match config.Discord with
            | Some dcCfg -> Some (startDiscordServer dcCfg)
            | None -> None

        // Slack: start if configured
        let slServerOpt =
            match config.Slack with
            | Some slCfg -> Some (startSlackServer slCfg)
            | None -> None

        // Feishu: start if configured
        let fsServerOpt =
            match config.Feishu with
            | Some fsCfg -> Some (startFeishuServer fsCfg)
            | None -> None

        // DingTalk: start if configured
        let dtServerOpt =
            match config.DingTalk with
            | Some dtCfg -> Some (startDingTalkServer dtCfg)
            | None -> None

        // Email: start if configured
        let emServerOpt =
            match config.Email with
            | Some emCfg -> Some (startEmailServer emCfg)
            | None -> None

        // Telnet: start if configured
        let tnServerOpt =
            match config.Telnet with
            | Some tnCfg -> Some (startTelnetServer tnCfg)
            | None -> None

        // Matrix: start if configured
        let mxServerOpt =
            match config.Matrix with
            | Some mxCfg -> Some (startMatrixServer mxCfg)
            | None -> None

        // QQ: start if configured
        let qqServerOpt =
            match config.QQ with
            | Some qqCfg -> Some (startQQServer qqCfg)
            | None -> None

        // WhatsApp: start if configured
        let waServerOpt =
            match config.WhatsApp with
            | Some waCfg -> Some (startWhatsAppServer waCfg)
            | None -> None

        // MoChat: start if configured
        let mcServerOpt =
            match config.MoChat with
            | Some mcCfg -> Some (startMoChatServer mcCfg)
            | None -> None

        // Inter-agent: start if configured
        let iaServerOpt =
            match config.InterAgent with
            | Some iaCfg when iaCfg.Enabled -> Some (startInterAgentServer iaCfg)
            | _ -> None

        printfn "BotSharp — model: %s" config.DefaultModel
        printfn "Workspace:  %s" config.WorkspacePath
        printfn "Type /help for commands, Ctrl-D (EOF) to exit."

        match config.Telegram with
        | Some tgConfig ->
            printfn "[Telegram] Starting bot..."
            use cts = new System.Threading.CancellationTokenSource()
            Async.Start(
                startTelegram tgConfig deps httpClient cts.Token
                    (fun bot cfg -> telegramSendOpt <- Some (sendOutboundMessage bot cfg))
                    (fun m -> switchModelRef m) (fun () -> listAvailableModels ()),
                cts.Token)
            startCli coordinator port deps switchModelRef listAvailableModels |> Async.RunSynchronously
            cts.Cancel()
        | None ->
            startCli coordinator port deps switchModelRef listAvailableModels |> Async.RunSynchronously

        apiServerOpt |> Option.iter (fun s -> s.Stop())
        wsServerOpt  |> Option.iter (fun s -> s.Stop())
        dcServerOpt  |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        slServerOpt  |> Option.iter (fun (s, cts) -> cts.Cancel(); s.Stop())
        fsServerOpt  |> Option.iter (fun s -> s.Stop())
        dtServerOpt  |> Option.iter (fun s -> s.Stop())
        iaServerOpt  |> Option.iter (fun s -> s.Stop())
        autoCompactSvc.Stop()
        sessionCleanupSvc.Stop()
        disposeMcp ()
        0
