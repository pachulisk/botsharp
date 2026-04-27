# BotSharp

[中文文档](README_CN.md)

BotSharp is a type-driven AI agent framework written in F#. It is a port of the Python-based [nanobot](https://github.com/nano-bot/nanobot) agent framework, rebuilt from the ground up with two guiding principles:

1. **Type-driven design** — make illegal states inexpressible at compile time
2. **Parse, don't validate** — use [FParsec](https://www.quanttec.com/fparsec/) at system boundaries so that once data enters the domain it is already correct by construction

## Features

- **Multi-provider LLM support** — OpenAI, Anthropic (Claude), DeepSeek, Groq, DashScope (Qwen), Moonshot (Kimi), MiniMax, Zhipu (GLM), SiliconFlow, AiHubMix, Ollama
- **SSE streaming** — real-time token-by-token output via Server-Sent Events
- **Tool system** — file I/O, shell exec (sandboxable), web fetch/search, cron scheduling, MCP server integration, notebook editing, agent spawning
- **Multi-channel** — CLI, Telegram, WebSocket, OpenAI-compatible HTTP API
- **Session management** — per-session MailboxProcessor actor model with automatic memory consolidation
- **Skill system** — workspace-based SKILL.md loader with requirements checking and built-in defaults
- **Heartbeat service** — periodic autonomous background tasks
- **Dream / Memory consolidation** — automatic long-term memory distillation from conversation history
- **Hexagonal architecture** — Domain / Application / Infrastructure layers with clear dependency direction

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## Quick Start

### 1. Clone and build

```bash
git clone https://github.com/pachulisk/botsharp.git
cd botsharp
dotnet build src/BotSharp.sln
```

### 2. Run

```bash
dotnet run --project src/BotSharp/BotSharp.fsproj
```

On first run, BotSharp launches an interactive setup wizard that walks you through:

- Choosing an LLM provider (OpenAI, Anthropic, DeepSeek, etc.)
- Entering your API key
- Selecting a default model

Configuration is saved to `~/.botsharp/config.json`.

### 3. CLI flags

```
--model <name>       Override the default model
--workspace <path>   Override the workspace directory
--api-port <port>    Start an OpenAI-compatible HTTP API server
--ws-port <port>     Start a WebSocket server
```

Example — run with Claude and expose an API:

```bash
dotnet run --project src/BotSharp/BotSharp.fsproj -- --model claude-sonnet-4-20250514 --api-port 8080
```

### 4. Configuration

Edit `~/.botsharp/config.json` directly, or re-run the wizard by deleting the file. Key fields:

```json
{
  "default_model": "gpt-4o-mini",
  "default_provider": "openai",
  "temperature": 0.7,
  "max_tokens": 4096,
  "api_keys": {
    "openai": "sk-..."
  }
}
```

### 5. Workspace

BotSharp uses `~/.botsharp/workspace/` for persistent state:

```
~/.botsharp/workspace/
  SOUL.md          # Agent identity and personality
  AGENTS.md        # Sub-agent definitions
  USER.md          # User profile (auto-populated)
  TOOLS.md         # Tool usage guidelines
  HEARTBEAT.md     # Periodic task instructions
  memory/
    MEMORY.md      # Long-term memory (auto-consolidated)
    HISTORY.md     # Conversation history log
  skills/          # Installed skill definitions (SKILL.md)
```

## Running Tests

```bash
dotnet test src/BotSharp.Tests/BotSharp.Tests.fsproj
```

2041 tests covering domain logic, parsers, tool implementations, and application layer.

## Architecture

```
src/BotSharp/
  Domain/              # Pure types, state machine, error DUs — zero dependencies
  Application/         # AgentLoop, SessionActor, MemoryConsolidator, ContextBuilder
  Infrastructure/
    Config/            # FParsec-based config parser + JSON writer
    Providers/         # OpenAI-compatible SSE adapter, provider registry
    Channels/          # CLI, Telegram, WebSocket, API channel adapters
    Tools/             # File, Shell, Web, Cron, MCP, Spawn, Notebook tools
    Skills/            # Skill loader + built-in default skills
    Storage/           # JSONL session store, dream store, cron store
    Shared/            # AsyncResult CE, JSON helpers, string utilities
  Program.fs           # Entry point, dependency wiring, workspace bootstrap
```

## License

MIT
