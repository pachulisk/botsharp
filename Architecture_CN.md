# BotSharp 架构文档

BotSharp 是一个用 F# 编写的 AI Agent 框架，运行在 .NET 9.0 上。提供多通道、多模型的 Agent 系统，集成 CLIPS 规则引擎进行运行时行为控制。

## 整体架构

```
                    通道层 (16 个)
    CLI / API / WebSocket / Telegram / Discord / Slack
    飞书 / 钉钉 / Email / QQ / Matrix / WhatsApp
    MoChat / Telnet / InterAgent / Pocket
                        |
                        v
                 +-------------+
                 | SessionActor |  (每个会话一个 MailboxProcessor)
                 +-------------+
                        |
                        v
                 +-------------+
                 |  AgentLoop   |  (状态机: 空闲 -> 构建提示 -> 等待LLM -> 执行工具 -> 完成)
                 +-------------+
                   |         |
                   v         v
            +----------+  +----------+
            | Provider |  |   工具   |  (12个: shell, 文件, web, 定时, 消息, spawn, long_task, mcp, notebook, my, pocket)
            | Registry |  +----------+
            +----------+       |
                 |             v
                 v        +----------+
            +---------+   |  CLIPS   |  (规则引擎: 15+ 条内置规则)
            | OpenAI  |   |  引擎    |
            | 兼容    |   +----------+
            | 适配器  |
            +---------+
                 |
                 v
          21 个 LLM 提供商
    OpenAI / Anthropic / DeepSeek / Gemini
    小米MiMo / Groq / Moonshot / 通义千问
    火山引擎 / Mistral / Together / Perplexity
    OpenRouter / SiliconFlow / AiHubMix
    Ollama / vLLM / LM Studio / MiniMax / 智谱
```

## 分层架构

### 领域层 (`Domain/`)

纯类型定义，无外部依赖。类型驱动设计的基础。

| 文件 | 职责 |
|------|------|
| `Types.fs` | 核心联合类型: `Message`, `MediaContent`, `ToolResult`, `AgentState`, `SessionSnapshot`, `BotSharpConfig` |
| `StateMachine.fs` | Agent 状态转换: `空闲 -> 构建提示 -> 等待LLM -> 执行工具 -> 完成` |
| `Errors.fs` | 结构化错误类型: `LlmError`, `ToolError`, `StorageError`, `ParseError` |

**核心设计原则：**
- **解析而非验证** — `NonEmptyList<'T>`, `LocalFilePath`, `ApiKey` 在构造时强制约束
- **使非法状态不可表示** — `TaskPhase = Queued | Processing | Finished of TaskOutcome * DateTimeOffset`
- **不用布尔标记** — 用联合类型: `StreamState = NotStarted | Streaming | Completed`

### 应用层 (`Application/`)

编排逻辑。依赖领域类型，不依赖基础设施。

| 文件 | 职责 |
|------|------|
| `AgentLoop.fs` | 主循环: LLM 调用 -> 工具执行 -> 迭代。处理流式输出、重试、fallback、密钥脱敏 |
| `SessionActor.fs` | 每会话 MailboxProcessor。路由消息，管理 `/new`, `/clear`, `/history` 命令 |
| `ContextBuilder.fs` | 构建系统提示: IDENTITY.md + AGENTS.md + MEMORY.md + skills + 通道格式提示 |
| `MemoryConsolidator.fs` | 将会话历史整理到 MEMORY.md + HISTORY.md（通过 LLM `save_memory` 工具调用） |
| `SubagentManager.fs` | 管理后台子 agent（spawn）和同步步骤执行（long_task） |
| `HeartbeatService.fs` | 定期后台任务检查（两阶段 LLM 决策） |
| `AutoCompactService.fs` | 空闲会话的主动整理 |
| `SessionCleanupService.fs` | 删除过期会话文件（可配置 TTL） |

### 基础设施层 (`Infrastructure/`)

外部系统集成。每个子目录处理一个关注点。

#### 通道 (`Infrastructure/Channels/`)

16 个通道实现，都遵循相同模式：接收消息 -> 创建 `InboundMessage` -> 路由到 `AgentCoordinator` -> 发送回复。

| 通道 | SDK/方式 | 协议 |
|------|---------|------|
| CLI | 内置 | stdin/stdout |
| API | HttpListener | OpenAI 兼容 REST |
| WebSocket | System.Net.WebSockets | 双向 WS |
| Telegram | Telegram.Bot NuGet | 长轮询 + REST |
| Discord | Discord.Net NuGet | Gateway WebSocket |
| Slack | 原生 HttpClient | Socket Mode WebSocket |
| 飞书 | 原生 HttpClient | Webhook + REST |
| 钉钉 | 原生 HttpClient | Webhook + REST |
| Email | MailKit NuGet | IMAP 轮询 + SMTP |
| QQ | 原生 HttpClient | Gateway WebSocket + REST |
| Matrix | 原生 HttpClient | /sync 长轮询 + REST |
| WhatsApp | 原生 HttpClient | Meta Cloud API webhook |
| MoChat | 原生 HttpClient | HTTP 轮询 |
| Telnet | TcpListener | 原始 TCP |
| InterAgent | HttpListener | 异步任务模型（提交 + 轮询） |
| Pocket | Unix domain socket | RPC 桥接 |

#### 提供商 (`Infrastructure/Providers/`)

| 文件 | 职责 |
|------|------|
| `ProviderRegistry.fs` | 21 个提供商规格，关键词检测，base URL，上下文窗口，fallback 链 |
| `OpenAICompatAdapter.fs` | OpenAI 兼容 chat/stream 端点的 HTTP 客户端 |
| `LlmResponseParser.fs` | 解析 LLM 响应 JSON 为 `TextOnly | WithToolCalls | Empty` |
| `SseParser.fs` | 基于 FParsec 的 SSE 流解析器，逐 token 流式处理 |
| `TranscriptionProvider.fs` | Whisper API（Groq/OpenAI）语音转文字 |

#### 规则引擎 (`Infrastructure/Rules/`)

通过 P/Invoke 集成 CLIPS 6.4 原生 C 库。

| 文件 | 职责 |
|------|------|
| `ClipsNative.fs` | CLIPS C API 的 P/Invoke 绑定 |
| `ClipsEnvironment.fs` | F# 封装: create, load, assert, run, query, dispose |
| `RuleEngine.fs` | Agent 循环专用 API + 15+ 条内置规则 |

**内置 CLIPS 规则：**

| 类别 | 规则 |
|------|------|
| 工具失败 | `repeated-tool-failure`, `excessive-tool-calls`, `workspace-violation-stop` |
| 工具超时 | `repeated-tool-timeout` |
| LLM 响应 | `consecutive-empty-responses`, `rate-limit-storm`, `context-too-long` |
| 配置校验 | `impossible-token-budget` |
| 长任务步骤 | `long-task-consecutive-failures`, `long-task-no-signal-stall`, `long-task-shrinking-handoff` |
| Provider Fallback | `fallback-strip-reasoning`, `fallback-strip-reasoning-cross-provider`, `fallback-keep-reasoning-same-provider` |
| Fallback 资格 | `fallback-block-context-too-long`, `fallback-block-empty-response`, `fallback-allow-*`（7条） |
| 跨 Agent 共识 | `inter-agent-consensus-zh`, `inter-agent-consensus-en` |
| 会话管理 | `clear-with-unarchived-history`, `session-truncation-pressure` |
| 子 Agent | `subagent-budget-warning` |
| 安全 | `secret-leak-detected` |
| 去重 | `duplicate-tool-call` |

用户可在 `{workspace}/rules/*.clp` 中添加自定义规则。

#### 工具 (`Infrastructure/Tools/`)

| 工具 | 描述 |
|------|------|
| `ShellTool` | 执行 shell 命令，SSRF 防护，危险命令检测 |
| `FileSystemTool` | 文件读/写/编辑，工作区限制 |
| `WebTool` | HTTP 抓取 + Web 搜索（Brave/DuckDuckGo/Tavily） |
| `CronTool` | 定时任务调度 |
| `MessageTool` | 向通道发送消息，支持媒体附件 |
| `SpawnTool` | 创建后台子 Agent |
| `LongTaskTool` | 多步骤任务编排，handoff/complete 信号机制 |
| `McpTool` | Model Context Protocol 客户端 |
| `NotebookTool` | Jupyter notebook 执行 |
| `MyTool` | Agent 自省（模型、token、迭代次数） |

## 数据流

```
用户消息
  |
  v
Channel.processMessage()
  |
  v
InboundMessage { Channel, Sender, Chat, Input, Metadata }
  |
  v
SessionActor.ProcessInput()
  |-- /new 命令? -> 强制整理 + 清空会话
  |-- /clear 命令? -> CLIPS 检查 + 可选整理 + 清空
  |-- /history 命令? -> 返回最近消息
  |
  v
AgentLoop.runAgentLoop()
  |
  |-- 从 JSONL 文件加载会话快照
  |-- 应用 max_messages 限制（CLIPS: session-truncation-pressure）
  |-- 构建系统提示（IDENTITY.md + MEMORY.md + skills + 历史尾部）
  |
  v
iterate() 状态机循环
  |
  |-- 等待LLM: chatWithRetry(primary, fallbacks, ruleEngine, ...)
  |     |-- 主提供商失败?
  |     |   |-- CLIPS: shouldAttemptFallback?（阻止 ContextTooLong/EmptyResponse）
  |     |   |-- CLIPS: shouldStripReasoning?（跨提供商兼容性）
  |     |   '-- 按顺序尝试 fallback 提供商
  |     |
  |     v
  |-- LLM 返回文本 -> 完成
  |-- LLM 返回 tool_calls -> 执行工具
  |     |
  |     |-- 执行工具（并发安全的工具并行执行）
  |     |-- 密钥脱敏（正则扫描，CLIPS: secret-leak-detected）
  |     |-- CLIPS: assert tool-result 事实 + 评估规则
  |     |   |-- repeated-tool-failure? -> 停止循环
  |     |   |-- workspace-violation-stop? -> 停止循环
  |     |   '-- excessive-tool-calls? -> 停止循环
  |     |
  |     v
  |-- 回到等待LLM（迭代次数 +1）
  |-- 达到最大迭代 -> 完成并附停止消息
  |
  v
完成: 持久化会话 + 返回响应
  |
  v
通道发送回复给用户
```

## Provider Fallback 链

```
config.json:
  "default_model": "mimo-v2.5-pro"
  "fallback_models": ["deepseek-v4-pro", "gpt-4o"]

请求流程:
  MiMo-V2.5-Pro（主模型）
    |-- 429 限流
    |-- 重试 3 次（指数退避）
    |-- 全部耗尽
    |
    v
  CLIPS: shouldAttemptFallback?
    |-- RateLimited -> 允许
    |-- ContextTooLong -> 阻止（所有提供商都会失败）
    |
    v
  CLIPS: shouldStripReasoning?
    |-- MiMo -> DeepSeek（跨提供商 ReasoningSplit）-> 清除 reasoning
    |
    v
  DeepSeek-V4-Pro（fallback 1）
    |-- 成功 -> 返回响应
    |-- 失败 -> 尝试下一个
    |
    v
  GPT-4o（fallback 2）
    |-- 成功 -> 返回响应
    |-- 失败 -> 返回原始错误
```

## 记忆系统

```
工作区布局:
  ~/.botsharp/workspace/
  +-- IDENTITY.md          Agent 人设（系统提示）
  +-- AGENTS.md            能力文档
  +-- SOUL.md              性格特征
  +-- USER.md              用户偏好
  +-- TOOLS.md             自定义工具文档
  +-- memory/
  |   +-- MEMORY.md        长期记忆（整理时更新）
  |   +-- HISTORY.md       会话历史日志
  |   +-- .dream_cursor    上次整理的消息索引
  +-- sessions/
  |   +-- telegram_123.jsonl    每会话 JSONL 文件
  |   +-- cli_session.jsonl
  +-- skills/
  |   +-- weather/SKILL.md
  |   +-- github/SKILL.md
  |   +-- ...（共 9 个内置 skill）
  +-- rules/
      +-- custom.clp       用户自定义 CLIPS 规则
```

## 构建与测试

```bash
# 构建
cd src && dotnet build BotSharp.sln -c Release

# 测试（2041 个测试）
dotnet test BotSharp.Tests/BotSharp.Tests.fsproj -c Release

# 运行（CLI 模式）
dotnet run --project BotSharp/BotSharp.fsproj

# 运行（Gateway 模式）
dotnet run --project BotSharp/BotSharp.fsproj -- gateway --port 18790

# 编译 CLIPS 原生库
cd BotSharp/Native && bash build-clips.sh
```

## 配置示例

```json
{
  "default_model": "mimo-v2.5-pro",
  "default_provider": "xiaomi-mimo",
  "fallback_models": ["deepseek-v4-pro", "gpt-4o"],
  "temperature": 0.1,
  "max_tokens": 4096,
  "max_iterations": 40,
  "subagent_max_iterations": 15,
  "max_messages": 0,
  "session_cleanup_days": 0,
  "api_keys": {
    "xiaomi-mimo": "tp-xxx",
    "deepseek": "sk-xxx",
    "openai": "sk-xxx"
  },
  "telegram": { "token": "xxx", "allow_from": ["*"], "streaming": true },
  "discord": { "token": "xxx", "allow_from": ["*"] },
  "slack": { "bot_token": "xoxb-xxx", "app_token": "xapp-xxx" }
}
```

## 依赖

| 包 | 版本 | 用途 |
|----|------|------|
| FParsec | 1.1.1 | SSE 流解析、输入解析 |
| Telegram.Bot | 21.* | Telegram 通道 |
| Discord.Net | 3.19.1 | Discord 通道 |
| MailKit | 4.* | Email 通道（IMAP + SMTP） |
| CLIPS 6.4.2 | 原生 C | 规则引擎（本地编译） |
