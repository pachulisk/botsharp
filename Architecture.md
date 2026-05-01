# BotSharp Architecture

BotSharp is an AI agent framework written in F# targeting .NET 9.0. It provides a multi-channel, multi-provider agent system with a CLIPS rule engine for runtime behavior control.

## High-Level Architecture

```
                    Channels (16)
    CLI / API / WebSocket / Telegram / Discord / Slack
    Feishu / DingTalk / Email / QQ / Matrix / WhatsApp
    MoChat / Telnet / InterAgent / Pocket
                        |
                        v
                 +-------------+
                 | SessionActor |  (MailboxProcessor per session)
                 +-------------+
                        |
                        v
                 +-------------+
                 |  AgentLoop   |  (State machine: Idle -> BuildingPrompt -> AwaitingLLM -> ExecutingTools -> Finalizing)
                 +-------------+
                   |         |
                   v         v
            +----------+  +----------+
            | Provider |  |  Tools   |  (12 tools: shell, filesystem, web, cron, message, spawn, long_task, mcp, notebook, my, pocket)
            | Registry |  +----------+
            +----------+       |
                 |             v
                 v        +----------+
            +---------+   |  CLIPS   |  (Rule engine: 15+ built-in rules)
            | OpenAI  |   |  Engine  |
            | Compat  |   +----------+
            | Adapter |
            +---------+
                 |
                 v
          21 LLM Providers
    OpenAI / Anthropic / DeepSeek / Gemini
    MiMo / Groq / Moonshot / Dashscope
    Volcengine / Mistral / Together / Perplexity
    OpenRouter / SiliconFlow / AiHubMix
    Ollama / vLLM / LM Studio / MiniMax / Zhipu
```

## Layer Architecture

### Domain Layer (`Domain/`)

Pure types with no external dependencies. The foundation of the type-driven design.

| File | Purpose |
|------|---------|
| `Types.fs` | Core discriminated unions: `Message`, `MediaContent`, `ToolResult`, `AgentState`, `SessionSnapshot`, `BotSharpConfig`, channel configs |
| `StateMachine.fs` | Agent state transitions: `Idle -> BuildingPrompt -> AwaitingLLM -> ExecutingTools -> Finalizing` |
| `Errors.fs` | Structured error types: `LlmError`, `ToolError`, `StorageError`, `ParseError` |

**Key Design Principles:**
- **Parse, Don't Validate** — `NonEmptyList<'T>`, `LocalFilePath`, `ApiKey` enforce invariants at construction time
- **Make Illegal States Unrepresentable** — `TaskPhase = Queued | Processing | Finished of TaskOutcome * DateTimeOffset`
- **No Bool Flags** — Use DUs instead: `StreamState = NotStarted | Streaming | Completed`

### Application Layer (`Application/`)

Orchestration logic. Depends on Domain types but not on Infrastructure.

| File | Purpose |
|------|---------|
| `AgentLoop.fs` | Main agent loop: LLM call -> tool execution -> iterate. Handles streaming, retries, fallback, secret redaction |
| `SessionActor.fs` | Per-session MailboxProcessor actor. Routes messages, manages `/new`, `/clear`, `/history` commands |
| `ContextBuilder.fs` | Builds LLM system prompt from IDENTITY.md, AGENTS.md, MEMORY.md, skills, channel format hints |
| `MemoryConsolidator.fs` | Consolidates session history to MEMORY.md + HISTORY.md via LLM `save_memory` tool call |
| `SubagentManager.fs` | Manages background subagents (spawn) and synchronous step execution (long_task) |
| `HeartbeatService.fs` | Periodic background task check with two-phase LLM decision |
| `AutoCompactService.fs` | Proactive consolidation of idle sessions |
| `SessionCleanupService.fs` | Deletes expired session files (configurable TTL) |

### Infrastructure Layer (`Infrastructure/`)

External system integrations. Each subdirectory handles one concern.

#### Channels (`Infrastructure/Channels/`)

16 channel implementations, all following the same pattern: receive message -> create `InboundMessage` -> route to `AgentCoordinator` -> send reply.

| Channel | SDK/Method | Protocol |
|---------|-----------|----------|
| CLI | Built-in | stdin/stdout |
| API | HttpListener | OpenAI-compatible REST |
| WebSocket | System.Net.WebSockets | Bidirectional WS |
| Telegram | Telegram.Bot NuGet | Long-poll + REST |
| Discord | Discord.Net NuGet | Gateway WebSocket |
| Slack | Native HttpClient | Socket Mode WebSocket |
| Feishu | Native HttpClient | Webhook + REST |
| DingTalk | Native HttpClient | Webhook + REST |
| Email | MailKit NuGet | IMAP poll + SMTP |
| QQ | Native HttpClient | Gateway WebSocket + REST |
| Matrix | Native HttpClient | /sync long-poll + REST |
| WhatsApp | Native HttpClient | Meta Cloud API webhook |
| MoChat | Native HttpClient | HTTP polling |
| Telnet | TcpListener | Raw TCP |
| InterAgent | HttpListener | Async task model (submit + poll) |
| Pocket | Unix domain socket | RPC bridge |

#### Providers (`Infrastructure/Providers/`)

| File | Purpose |
|------|---------|
| `ProviderRegistry.fs` | 21 provider specs with keyword detection, base URLs, context windows, fallback chain |
| `OpenAICompatAdapter.fs` | HTTP client for OpenAI-compatible chat/stream endpoints |
| `LlmResponseParser.fs` | Parses LLM response JSON into `TextOnly | WithToolCalls | Empty` |
| `SseParser.fs` | FParsec-based SSE stream parser for token-by-token streaming |
| `TranscriptionProvider.fs` | Whisper API (Groq/OpenAI) for voice-to-text |

#### Rules (`Infrastructure/Rules/`)

CLIPS 6.4 native C library integration via P/Invoke.

| File | Purpose |
|------|---------|
| `ClipsNative.fs` | P/Invoke bindings for CLIPS C API |
| `ClipsEnvironment.fs` | F# wrapper: create, load, assert, run, query, dispose |
| `RuleEngine.fs` | Agent-loop-specific API + 15+ built-in rules |

**Built-in CLIPS Rules:**

| Category | Rules |
|----------|-------|
| Tool failure | `repeated-tool-failure`, `excessive-tool-calls`, `workspace-violation-stop` |
| Tool timeout | `repeated-tool-timeout` |
| LLM response | `consecutive-empty-responses`, `rate-limit-storm`, `context-too-long` |
| Config validation | `impossible-token-budget` |
| Long-task steps | `long-task-consecutive-failures`, `long-task-no-signal-stall`, `long-task-shrinking-handoff` |
| Provider fallback | `fallback-strip-reasoning`, `fallback-strip-reasoning-cross-provider`, `fallback-keep-reasoning-same-provider` |
| Fallback eligibility | `fallback-block-context-too-long`, `fallback-block-empty-response`, `fallback-allow-*` (7 rules) |
| Inter-agent consensus | `inter-agent-consensus-zh`, `inter-agent-consensus-en` |
| Session management | `clear-with-unarchived-history`, `session-truncation-pressure` |
| Subagent | `subagent-budget-warning` |
| Security | `secret-leak-detected` |
| Deduplication | `duplicate-tool-call` |

#### Tools (`Infrastructure/Tools/`)

| Tool | Description |
|------|-------------|
| `ShellTool` | Execute shell commands with SSRF protection, dangerous command detection |
| `FileSystemTool` | Read/write/edit files with workspace restriction |
| `WebTool` | HTTP fetch + web search (Brave/DuckDuckGo/Tavily) |
| `CronTool` | Schedule recurring tasks |
| `MessageTool` | Send messages to channels with media attachments |
| `SpawnTool` | Create background subagents |
| `LongTaskTool` | Multi-step task orchestration with handoff/complete signals |
| `McpTool` | Model Context Protocol client |
| `NotebookTool` | Jupyter notebook execution |
| `MyTool` | Agent self-inspection (model, tokens, iterations) |
| `ToolParser` | Type-safe argument validation (`JsonSchemaType` DU) |
| `ToolHints` | Generate tool documentation for the LLM |

#### Storage (`Infrastructure/Storage/`)

| File | Purpose |
|------|---------|
| `SessionParser.fs` | JSONL serializer/deserializer for session messages |
| `JsonlStore.fs` | Atomic read/write of session files (temp + rename) |
| `DreamStore.fs` | Dream/consolidation entry persistence |
| `CronStore.fs` | Cron job persistence |

## Data Flow

```
User Message
  |
  v
Channel.processMessage()
  |
  v
InboundMessage { Channel, Sender, Chat, Input, Metadata }
  |
  v
SessionActor.ProcessInput()
  |-- Command NewSession? -> forceConsolidate + clear session
  |-- Command ClearHistory? -> CLIPS check + optional consolidate + clear
  |-- Command ShowHistory? -> return recent messages
  |
  v
AgentLoop.runAgentLoop()
  |
  |-- Load session snapshot from JSONL file
  |-- Apply max_messages cap (CLIPS: session-truncation-pressure)
  |-- Build system prompt (IDENTITY.md + MEMORY.md + skills + history tail)
  |-- Build LLM request
  |
  v
iterate() state machine loop
  |
  |-- AwaitingLLM: chatWithRetry(primary, fallbacks, ruleEngine, ...)
  |     |-- Primary provider fails?
  |     |   |-- CLIPS: shouldAttemptFallback? (block ContextTooLong/EmptyResponse)
  |     |   |-- CLIPS: shouldStripReasoning? (cross-provider compatibility)
  |     |   '-- Try fallback providers in order
  |     |
  |     v
  |-- LLM responds with text -> Finalizing
  |-- LLM responds with tool_calls -> ExecutingTools
  |     |
  |     |-- Execute tools (concurrent-safe tools in parallel)
  |     |-- Secret redaction (regex scan, CLIPS: secret-leak-detected)
  |     |-- CLIPS: assert tool-result facts + evaluate rules
  |     |   |-- repeated-tool-failure? -> StopLoop
  |     |   |-- workspace-violation-stop? -> StopLoop
  |     |   |-- excessive-tool-calls? -> StopLoop
  |     |   '-- repeated-tool-timeout? -> StopLoop
  |     |
  |     v
  |-- Loop back to AwaitingLLM (increment iteration)
  |-- Max iterations reached -> Finalizing with stop message
  |
  v
Finalizing: persist session + return response
  |
  v
Channel sends reply to user
```

## Provider Fallback Chain

```
config.json:
  "default_model": "mimo-v2.5-pro"
  "fallback_models": ["deepseek-v4-pro", "gpt-4o"]

Request flow:
  MiMo-V2.5-Pro (primary)
    |-- 429 Rate Limited
    |-- Retry 3x (exponential backoff)
    |-- All retries exhausted
    |
    v
  CLIPS: shouldAttemptFallback?
    |-- RateLimited -> allow
    |-- ContextTooLong -> block (all providers would fail)
    |
    v
  CLIPS: shouldStripReasoning?
    |-- MiMo -> DeepSeek (cross-provider ReasoningSplit) -> strip
    |
    v
  DeepSeek-V4-Pro (fallback 1)
    |-- Success -> return response
    |-- Failure -> try next fallback
    |
    v
  GPT-4o (fallback 2)
    |-- Success -> return response
    |-- Failure -> return original error
```

## Memory System

```
Workspace Layout:
  ~/.botsharp/workspace/
  +-- IDENTITY.md          Agent persona (system prompt)
  +-- AGENTS.md            Capability documentation
  +-- SOUL.md              Personality traits
  +-- USER.md              User preferences
  +-- TOOLS.md             Custom tool documentation
  +-- memory/
  |   +-- MEMORY.md        Long-term memory (updated by consolidation)
  |   +-- HISTORY.md       Session history log
  |   +-- .dream_cursor    Last consolidated message index
  +-- sessions/
  |   +-- telegram_123.jsonl    Per-session JSONL files
  |   +-- cli_session.jsonl
  +-- skills/
  |   +-- weather/SKILL.md
  |   +-- github/SKILL.md
  |   +-- ...
  +-- rules/
      +-- custom.clp       User-defined CLIPS rules
```

## Build & Test

```bash
# Build
cd src && dotnet build BotSharp.sln -c Release

# Test (2041 tests)
dotnet test BotSharp.Tests/BotSharp.Tests.fsproj -c Release

# Run (CLI mode)
dotnet run --project BotSharp/BotSharp.fsproj

# Run (Gateway mode)
dotnet run --project BotSharp/BotSharp.fsproj -- gateway --port 18790

# Build CLIPS native library
cd BotSharp/Native && bash build-clips.sh
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| FParsec | 1.1.1 | SSE stream parsing, input parsing |
| Telegram.Bot | 21.* | Telegram channel |
| Discord.Net | 3.19.1 | Discord channel |
| MailKit | 4.* | Email channel (IMAP + SMTP) |
| CLIPS 6.4.2 | Native C | Rule engine (built locally) |
