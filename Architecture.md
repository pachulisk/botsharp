# BotSharp Architecture

BotSharp is an AI agent framework written in F# targeting .NET 9.0. It provides a multi-channel, multi-provider agent system with a CLIPS rule engine for runtime behavior control, a two-phase memory pipeline, and a SQLite-backed distributed job queue.

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
            | Adapter |        |
            +---------+        v
                 |        +----------+
                 v        | SQLite   |  (Job queue, state index, stage1_outputs)
          21 LLM Providers| StateDb  |
    OpenAI / Anthropic /  +----------+
    DeepSeek / Gemini /
    MiMo / Groq / ...
```

## Layer Architecture

### Domain Layer (`Domain/`)

Pure types with no external dependencies. The foundation of the type-driven design.

| File | Purpose |
|------|---------|
| `Types.fs` | Core discriminated unions: `Message`, `MediaContent`, `ToolResult`, `AgentState`, `SessionSnapshot`, `BotSharpConfig`, channel configs, `ClaimOutcome`, `Phase1Output`, `Stage1Output`, `JobSummary`, `TokenTracker` |
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
| `AgentLoop.fs` | Main agent loop: LLM call -> tool execution -> iterate. Handles streaming, retries, fallback, secret redaction, citation parsing, token tracking |
| `SessionActor.fs` | Per-session MailboxProcessor actor. Routes messages, manages `/new`, `/clear`, `/history`, `/model`, `/jobs` commands |
| `ContextBuilder.fs` | Builds LLM system prompt with progressive memory disclosure: memory_summary.md (preferred) or MEMORY.md full-inject (small files), plus skills, channel format hints |
| `MemoryConsolidator.fs` | Single-stage consolidation (backward compatible): session history -> MEMORY.md + HISTORY.md via LLM `save_memory` tool call |
| `Phase1Extractor.fs` | Two-phase Phase 1: per-session extraction producing `raw_memory` + `rollout_summary` -> `stage1_outputs` table. Uses cheap model (3-level fallback: config -> provider recommendation -> DefaultModel) |
| `Phase1Service.fs` | Background service running Phase 1 extraction every 15 minutes on idle sessions |
| `Phase2Consolidator.fs` | Two-phase Phase 2: cross-session consolidation with git workspace diff, producing `memory_summary.md` + `MEMORY.md` + `rollout_summaries/*.md`. Global singleton job with 6-hour cooldown |
| `Phase2Service.fs` | Background service running Phase 2 every 30 minutes (actual frequency controlled by cooldown) |
| `SubagentManager.fs` | Manages background subagents (spawn) and synchronous step execution (long_task) |
| `HeartbeatService.fs` | Periodic background task check with two-phase LLM decision |
| `AutoCompactService.fs` | Proactive consolidation of idle sessions via SQLite job queue (ownership tokens, heartbeat, retry tracking) |
| `SessionCleanupService.fs` | Deletes expired session files via SQLite job queue (configurable TTL) |

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
| Memory consolidation | `needs-consolidation-by-messages`, `needs-consolidation-by-tokens` |
| Subagent | `subagent-budget-warning` |
| Security | `secret-leak-detected` |
| Deduplication | `duplicate-tool-call` |

#### Memory (`Infrastructure/Memory/`)

| File | Purpose |
|------|---------|
| `CitationParser.fs` | Parses `<mem-citation>` blocks from agent output for usage tracking; strips citations when configured |
| `ModelRecommendation.fs` | 22-provider Phase 1/2 model recommendation table with 3-level fallback (config -> provider table -> DefaultModel) |

#### Tools (`Infrastructure/Tools/`)

| Tool | Description |
|------|-------------|
| `ShellTool` | Execute shell commands with SSRF protection, dangerous command detection |
| `FileSystemTool` | Read/write/edit files with workspace restriction |
| `WebTool` | HTTP fetch + web search (Brave/DuckDuckGo/Tavily/SearXNG) |
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
| `StateDb.fs` | SQLite derived index (schema v3): `sessions`, `consolidation_entries`, `memory_usage`, `stage1_outputs`, `jobs` tables. WAL mode, auto-migration, rebuild from JSONL |
| `JobQueue.fs` | Codex-style distributed job queue: `tryClaim` (BEGIN IMMEDIATE + ownership tokens + lease + watermarks + concurrency limits), `markSucceeded`/`markFailed`/`markFailedIfUnowned`, `heartbeat`/`startHeartbeat`, `removeJob`, `pruneCompletedJobs` |
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
  |-- Command SwitchModel? -> update config + rebuild provider
  |-- Command ShowJobs? -> query SQLite job stats
  |
  v
AgentLoop.runAgentLoop()
  |
  |-- Load session snapshot from JSONL file
  |-- Apply max_messages cap (CLIPS: session-truncation-pressure)
  |-- Build system prompt (progressive memory disclosure)
  |     |-- memory_summary.md exists? -> inject summary + retrieval instructions
  |     |-- MEMORY.md > threshold? -> auto-switch to progressive mode
  |     '-- MEMORY.md small? -> full injection (backward compatible)
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
Finalizing: citation parsing + persist session + return response
  |-- Parse <mem-citation> blocks -> update usage_count in SQLite
  |-- Optionally strip citations from visible output
  |
  v
Channel sends reply to user
```

## Two-Phase Memory Pipeline

```
Phase 1 (per-session, cheap model, high frequency)
──────────────────────────────────────────────────
Session idle > 30min + unconsolidated >= MemoryWindowSize
  |
  v
Phase1Extractor.extractSession()
  |-- Filter system messages
  |-- Call LLM with save_phase1 tool (raw_memory + rollout_summary + rollout_slug)
  |-- Minimum signal gate: skip sessions with no durable insight
  |-- Write to stage1_outputs table (SQLite)
  |-- Append rollout_summary to HISTORY.md (backward compat)
  |-- Advance session consolidation pointer
  |-- Enqueue Phase 2 (advance watermark)

Phase 2 (cross-session, strong model, 6h cooldown)
───────────────────────────────────────────────────
Global singleton job (max 1 concurrent)
  |
  v
Phase2Consolidator.runPhase2()
  |-- Claim global job via JobQueue (ownership token + lease)
  |-- Select top-N stage1_outputs (ranked by usage_count + recency)
  |-- Sync to filesystem (rollout_summaries/*.md, raw_memories.md)
  |-- Compute git diff since last baseline
  |-- Call LLM to produce:
  |     +-- memory_summary.md (navigational index, ≤ 5000 tokens)
  |     +-- MEMORY.md (searchable registry by task group)
  |     '-- rollout_summaries/*.md (distilled per-session)
  |-- Reset git baseline
  |-- Mark job succeeded + enforce cooldown

Model Selection (3-level fallback, 22 providers):
  Phase 1: config.Phase1Model -> provider recommendation (cheap) -> DefaultModel
  Phase 2: config.Phase2Model -> provider recommendation (strong) -> DefaultModel
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
  |   +-- MEMORY.md              Long-term memory (searchable registry)
  |   +-- HISTORY.md             Session history log (append-only)
  |   +-- memory_summary.md      Phase 2 navigational index (injected into system prompt)
  |   +-- raw_memories.md        Phase 2 temp input (merged stage1 outputs)
  |   +-- rollout_summaries/     Per-session distilled summaries
  |   +-- .dream_cursor          Last consolidated message index
  +-- sessions/
  |   +-- telegram_123.jsonl     Per-session JSONL files
  |   +-- cli_session.jsonl
  +-- skills/
  |   +-- weather/SKILL.md
  |   +-- github/SKILL.md
  |   +-- ...
  +-- rules/
  |   +-- custom.clp             User-defined CLIPS rules
  +-- botsharp.sqlite            Derived index (sessions, jobs, stage1_outputs)
```

## SQLite Job Queue

Codex-style distributed job queue (`jobs` table) powering AutoCompact, SessionCleanup, Phase 1, and Phase 2:

- **BEGIN IMMEDIATE** transactions for serialized writes
- **Ownership tokens** (UUID) verified on complete/fail — prevents stale async callbacks
- **Lease expiry** with heartbeat renewal — auto-reclaims stuck jobs
- **Watermark-based change detection** — skips unchanged sessions
- **Concurrent job limiting** (`max_running_jobs` per kind)
- **Retry with backoff** and exhaustion tracking
- **`/jobs` command** for observability across all channels

## Build & Test

```bash
# Build
cd src && dotnet build BotSharp.sln -c Release

# Test (2040 tests)
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
| Microsoft.Data.Sqlite | 9.* | SQLite hybrid storage, job queue, stage1_outputs |
| CLIPS 6.4.2 | Native C | Rule engine (built locally) |
