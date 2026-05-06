# BotSharp 架构文档

BotSharp 是一个用 F# 编写的 AI Agent 框架，运行在 .NET 9.0 上。提供多通道、多模型的 Agent 系统，集成 CLIPS 规则引擎进行运行时行为控制，配备两阶段记忆流水线和基于 SQLite 的分布式作业队列。

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
            | 适配器  |        |
            +---------+        v
                 |        +----------+
                 v        | SQLite   |  (作业队列、状态索引、stage1_outputs)
          21 个 LLM 提供商| StateDb  |
    OpenAI / Anthropic /  +----------+
    DeepSeek / Gemini /
    小米MiMo / Groq / ...
```

## 分层架构

### 领域层 (`Domain/`)

纯类型定义，无外部依赖。类型驱动设计的基础。

| 文件 | 职责 |
|------|------|
| `Types.fs` | 核心联合类型: `Message`, `MediaContent`, `ToolResult`, `AgentState`, `SessionSnapshot`, `BotSharpConfig`, 通道配置, `ClaimOutcome`, `Phase1Output`, `Stage1Output`, `JobSummary`, `TokenTracker` |
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
| `AgentLoop.fs` | 主循环: LLM 调用 -> 工具执行 -> 迭代。处理流式输出、重试、fallback、密钥脱敏、引用解析、token 追踪 |
| `SessionActor.fs` | 每会话 MailboxProcessor。路由消息，管理 `/new`, `/clear`, `/history`, `/model`, `/jobs` 命令 |
| `ContextBuilder.fs` | 构建系统提示，支持渐进式记忆披露: memory_summary.md（优先）或 MEMORY.md 全量注入（小文件回退），加上 skills、通道格式提示 |
| `MemoryConsolidator.fs` | 单阶段整理（向后兼容）: 会话历史 -> MEMORY.md + HISTORY.md（通过 LLM `save_memory` 工具调用） |
| `Phase1Extractor.fs` | 两阶段 Phase 1: 每会话提取，产出 `raw_memory` + `rollout_summary` -> `stage1_outputs` 表。使用廉价模型（三级回退: 配置值 -> 提供商推荐表 -> DefaultModel） |
| `Phase1Service.fs` | 后台服务，每 15 分钟对空闲会话运行 Phase 1 提取 |
| `Phase2Consolidator.fs` | 两阶段 Phase 2: 跨会话整合，使用 git 工作区 diff，产出 `memory_summary.md` + `MEMORY.md` + `rollout_summaries/*.md`。全局单例作业，6 小时冷却 |
| `Phase2Service.fs` | 后台服务，每 30 分钟运行 Phase 2（实际频率由冷却控制） |
| `SubagentManager.fs` | 管理后台子 agent（spawn）和同步步骤执行（long_task） |
| `HeartbeatService.fs` | 定期后台任务检查（两阶段 LLM 决策） |
| `AutoCompactService.fs` | 通过 SQLite 作业队列主动整理空闲会话（所有权令牌、心跳、重试追踪） |
| `SessionCleanupService.fs` | 通过 SQLite 作业队列删除过期会话文件（可配置 TTL） |

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
| 记忆整理 | `needs-consolidation-by-messages`, `needs-consolidation-by-tokens` |
| 子 Agent | `subagent-budget-warning` |
| 安全 | `secret-leak-detected` |
| 去重 | `duplicate-tool-call` |

用户可在 `{workspace}/rules/*.clp` 中添加自定义规则。

#### 记忆子系统 (`Infrastructure/Memory/`)

| 文件 | 职责 |
|------|------|
| `CitationParser.fs` | 解析 Agent 输出中的 `<mem-citation>` 块用于使用追踪；可配置是否从可见输出中剥离 |
| `ModelRecommendation.fs` | 22 个提供商的 Phase 1/2 模型推荐表，三级回退（配置值 -> 提供商推荐 -> DefaultModel） |

#### 工具 (`Infrastructure/Tools/`)

| 工具 | 描述 |
|------|------|
| `ShellTool` | 执行 shell 命令，SSRF 防护，危险命令检测 |
| `FileSystemTool` | 文件读/写/编辑，工作区限制 |
| `WebTool` | HTTP 抓取 + Web 搜索（Brave/DuckDuckGo/Tavily/SearXNG） |
| `CronTool` | 定时任务调度 |
| `MessageTool` | 向通道发送消息，支持媒体附件 |
| `SpawnTool` | 创建后台子 Agent |
| `LongTaskTool` | 多步骤任务编排，handoff/complete 信号机制 |
| `McpTool` | Model Context Protocol 客户端 |
| `NotebookTool` | Jupyter notebook 执行 |
| `MyTool` | Agent 自省（模型、token、迭代次数） |

#### 存储 (`Infrastructure/Storage/`)

| 文件 | 职责 |
|------|------|
| `SessionParser.fs` | 会话消息的 JSONL 序列化/反序列化 |
| `JsonlStore.fs` | 会话文件的原子读写（临时文件 + 重命名） |
| `DreamStore.fs` | Dream/整理条目持久化 |
| `StateDb.fs` | SQLite 派生索引（schema v3）: `sessions`, `consolidation_entries`, `memory_usage`, `stage1_outputs`, `jobs` 表。WAL 模式，自动迁移，可从 JSONL 重建 |
| `JobQueue.fs` | Codex 风格分布式作业队列: `tryClaim`（BEGIN IMMEDIATE + 所有权令牌 + 租约 + 水印 + 并发限制）、`markSucceeded`/`markFailed`/`markFailedIfUnowned`、`heartbeat`/`startHeartbeat`、`removeJob`、`pruneCompletedJobs` |
| `CronStore.fs` | 定时任务持久化 |

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
  |-- /model 命令? -> 更新配置 + 重建 provider
  |-- /jobs 命令? -> 查询 SQLite 作业统计
  |
  v
AgentLoop.runAgentLoop()
  |
  |-- 从 JSONL 文件加载会话快照
  |-- 应用 max_messages 限制（CLIPS: session-truncation-pressure）
  |-- 构建系统提示（渐进式记忆披露）
  |     |-- memory_summary.md 存在? -> 注入摘要 + 检索指令
  |     |-- MEMORY.md > 阈值? -> 自动切换为渐进式模式
  |     '-- MEMORY.md 较小? -> 全量注入（向后兼容）
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
完成: 引用解析 + 持久化会话 + 返回响应
  |-- 解析 <mem-citation> 块 -> 更新 SQLite usage_count
  |-- 可选剥离引用（配置控制）
  |
  v
通道发送回复给用户
```

## 两阶段记忆流水线

```
Phase 1（每会话提取，廉价模型，高频）
──────────────────────────────────────
会话空闲 > 30分钟 + 未整理消息 >= MemoryWindowSize
  |
  v
Phase1Extractor.extractSession()
  |-- 过滤系统消息
  |-- 调用 LLM（save_phase1 工具: raw_memory + rollout_summary + rollout_slug）
  |-- 最小信号门: 跳过无持久价值的会话
  |-- 写入 stage1_outputs 表（SQLite）
  |-- 追加 rollout_summary 到 HISTORY.md（向后兼容）
  |-- 推进会话整理指针
  |-- 入队 Phase 2（推进水印）

Phase 2（跨会话整合，强模型，6小时冷却）
──────────────────────────────────────
全局单例作业（最多 1 个并发）
  |
  v
Phase2Consolidator.runPhase2()
  |-- 通过 JobQueue 领取全局作业（所有权令牌 + 租约）
  |-- 选取 top-N stage1_outputs（按 usage_count + 新近度排名）
  |-- 同步到文件系统（rollout_summaries/*.md, raw_memories.md）
  |-- 计算 git diff（与上次基线比较）
  |-- 调用 LLM 产出:
  |     +-- memory_summary.md（导航索引，≤ 5000 token）
  |     +-- MEMORY.md（按任务组织的可搜索注册表）
  |     '-- rollout_summaries/*.md（精炼的每会话摘要）
  |-- 重置 git 基线
  |-- 标记成功 + 执行冷却

模型选择（三级回退，22 个提供商）:
  Phase 1: config.Phase1Model -> 提供商推荐（廉价模型）-> DefaultModel
  Phase 2: config.Phase2Model -> 提供商推荐（强力模型）-> DefaultModel
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
  +-- IDENTITY.md              Agent 人设（系统提示）
  +-- AGENTS.md                能力文档
  +-- SOUL.md                  性格特征
  +-- USER.md                  用户偏好
  +-- TOOLS.md                 自定义工具文档
  +-- memory/
  |   +-- MEMORY.md            长期记忆（可搜索注册表）
  |   +-- HISTORY.md           会话历史日志（追加写入）
  |   +-- memory_summary.md    Phase 2 导航索引（注入系统提示词）
  |   +-- raw_memories.md      Phase 2 临时输入（合并的 stage1 输出）
  |   +-- rollout_summaries/   每会话精炼摘要
  |   +-- .dream_cursor        上次整理的消息索引
  +-- sessions/
  |   +-- telegram_123.jsonl   每会话 JSONL 文件
  |   +-- cli_session.jsonl
  +-- skills/
  |   +-- weather/SKILL.md
  |   +-- github/SKILL.md
  |   +-- ...（共 9 个内置 skill）
  +-- rules/
  |   +-- custom.clp           用户自定义 CLIPS 规则
  +-- botsharp.sqlite          派生索引（sessions、jobs、stage1_outputs）
```

## SQLite 作业队列

Codex 风格分布式作业队列（`jobs` 表），驱动 AutoCompact、SessionCleanup、Phase 1、Phase 2：

- **BEGIN IMMEDIATE** 事务实现序列化写入
- **所有权令牌**（UUID）在完成/失败时校验 — 防止过期异步回调
- **租约过期** + 心跳续租 — 自动回收卡死的作业
- **水印变更检测** — 跳过未变更的会话
- **并发作业限制**（每种类型 `max_running_jobs`）
- **退避重试** + 重试耗尽追踪
- **`/jobs` 命令** 全通道可观测性

## 构建与测试

```bash
# 构建
cd src && dotnet build BotSharp.sln -c Release

# 测试（2040 个测试）
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
  "memory_window_size": 50,
  "phase1_model": null,
  "phase2_model": null,
  "phase2_cooldown_hours": 6,
  "memory_summary_token_limit": 5000,
  "memory_direct_inject_limit": 5000,
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
| Microsoft.Data.Sqlite | 9.* | SQLite 混合存储、作业队列、stage1_outputs |
| CLIPS 6.4.2 | 原生 C | 规则引擎（本地编译） |
