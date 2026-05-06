# Nanobot F# 重写：类型驱动设计文档

> **前置阅读**：本文是 `FP_DDD_Research.md` 的实现层延伸，不重复理论。  
> **核心原则**：让错误状态在编译时不可表达；在系统边界用解析器转换数据，而非验证。

---

## 目录

1. [动机：Python 实现中的可表达非法状态](#1-动机python-实现中的可表达非法状态)
2. [领域类型设计（Type-Driven Domain Model）](#2-领域类型设计)
3. [系统边界与解析器（Parse, Don't Validate）](#3-系统边界与解析器)
4. [每会话 Actor（MailboxProcessor Per Session）](#4-每会话-actor)
5. [铁路型错误处理（Railway-Oriented Error Handling）](#5-铁路型错误处理)
6. [架构：六边形分层](#6-架构六边形分层)
7. [并发模型](#7-并发模型)
8. [模块文件结构](#8-模块文件结构)

---

## 1. 动机：Python 实现中的可表达非法状态

以下是 Python 实现（`nanobot/`）中**类型系统无法阻止**的问题，F# 改造后这些状态将在编译期不可表达：

| Python 中的非法状态 | 为何危险 | F# 的解法 |
|---|---|---|
| `sender_id` 和 `chat_id` 均为裸 `str`，可互换传入 | 参数顺序错误不报警 | Newtype 包装 |
| `LLMResponse.content` 和 `tool_calls` 可同时存在 | 调用方需逐一防御 | Discriminated Union |
| agent loop 是 `while True`，状态隐含在局部变量里 | 可在任意状态调用任意方法 | ADT 状态机 |
| 会话的 `last_consolidated > len(messages)` 逻辑上非法 | 须在每次访问时断言 | 智能构造器 |
| 工具参数是 `dict[str, Any]`，工具名是裸字符串 | 调用不存在的工具只在运行时报错 | Newtype + DU |
| `SlashCommand` 处理是 `if content.startswith("/new")` | 增加命令时可能遗漏分支 | 穷举模式匹配 |
| MCP 响应是裸 JSON，多处 `.get()` 访问 | 字段缺失只在运行时崩溃 | 组合式 JSON 解码器 |

---

## 2. 领域类型设计

类型定义遵循"**先类型，再逻辑**"原则：先写出所有可能的状态和转换，编译器会告知哪些分支未处理。

### 2.1 原语包装（Newtypes）

```fsharp
// ─── Domain/Types.fs ───────────────────────────────────────────────────────

/// 会话唯一标识，由 "channel:chatId" 或外部 override 构成
type SessionId = SessionId of string

/// 平台渠道标识：telegram | discord | slack | cli | system
type ChannelId = ChannelId of string

/// 平台内的对话/房间标识
type ChatId = ChatId of string

/// 发送者标识（用户 ID 或用户名）
type UserId = UserId of string

/// 工具名称，格式为 "tool_name" 或 "mcp_{server}_{tool}"
type ToolName = ToolName of string

/// LLM 工具调用的唯一 ID（来自 API 响应）
type ToolCallId = ToolCallId of string

/// 后台子任务的标识
type TaskId = TaskId of string
```

**为什么这样做**：`SessionId "telegram:12345"` 和 `ChatId "telegram:12345"` 是不同类型，
编译器在函数签名处即阻止混用，无需运行时断言。

---

### 2.2 媒体内容

```fsharp
/// 附件内容，穷举所有支持的媒体类型
type MediaContent =
    | ImageFile   of path: string
    | AudioFile   of path: string
    | DocumentFile of path: string
    | VideoFile   of path: string

// 非法状态消除：Python 中 media 是 list[str]（路径），
// 类型无法区分图片和音频，处理代码依赖文件扩展名猜测。
// F# 中构造时即明确类型，下游无需猜测。
```

---

### 2.3 用户输入：斜线命令 vs. 普通消息

```fsharp
/// 所有合法的斜线命令，新增命令必须在此 DU 中加分支
type SlashCommand =
    | NewSession        // /new
    | StopProcessing    // /stop
    | ShowHelp          // /help

/// 用户输入的两种形态，下游通过模式匹配处理，编译器强制穷举
type UserInput =
    | Command of SlashCommand
    | ChatMessage of content: string * media: MediaContent list
```

**消除的非法状态**：Python 中 `/unknowncmd` 会进入 LLM 处理（静默丢失命令语义）；
F# 中解析失败即返回 `ParseError`，未知命令无法构造 `SlashCommand`。

---

### 2.4 消息总线事件

```fsharp
/// 入站消息（从各平台渠道接收）
type InboundMessage = {
    Channel  : ChannelId
    Sender   : UserId
    Chat     : ChatId
    Input    : UserInput
    Metadata : Map<string, string>          // 渠道特定数据（thread_id 等）
    SessionKeyOverride : SessionId option   // Slack thread 等场景的 session 覆盖
}

/// 出站消息（发往平台渠道）
type OutboundMessage = {
    Channel   : ChannelId
    Chat      : ChatId
    Content   : string
    ReplyTo   : string option              // 平台消息 ID（如支持）
    Attachments : MediaContent list
    IsProgress  : bool                    // 是否为中间进度通知
}

/// 从入站消息派生会话 ID（单一职责，无歧义）
let sessionId (msg: InboundMessage) : SessionId =
    match msg.SessionKeyOverride with
    | Some id -> id
    | None ->
        let (ChannelId ch) = msg.Channel
        let (ChatId   ci) = msg.Chat
        SessionId $"{ch}:{ci}"
```

---

### 2.5 LLM 响应

```fsharp
/// 工具调用请求（来自 LLM 响应）
type ToolCall = {
    Id        : ToolCallId
    Tool      : ToolName
    Arguments : Map<string, JsonValue>   // 已通过 JSON 解码器解析的结构化参数
}

/// LLM 响应，四种情况互斥——Python 中 content 与 tool_calls 可同时非空
type LLMResponse =
    | TextResponse     of content: string
    | ToolCallResponse of calls: ToolCall list   // calls 不为空（list 非空由解析器保证）
    | EmptyResponse                              // 合法的空文本响应
    | ErrorResponse    of message: string * retryable: bool

// 消除的非法状态：
// Python:  LLMResponse(content="hello", tool_calls=[...])  -- 同时有文本和工具调用
// F#:      TextResponse "hello" 或 ToolCallResponse [...]  -- 编译器阻止混合构造
```

---

### 2.6 工具调用结果

```fsharp
/// 工具执行失败的分类
type ToolError =
    | ToolNotFound      of name: ToolName
    | ParameterMissing  of field: string
    | ParameterInvalid  of field: string * reason: string
    | ExecutionFailed   of exn: string
    | ExecutionTimeout  of after: System.TimeSpan
    | WorkspaceViolation of path: string   // 文件系统路径越界

/// 工具执行结果，成功/失败二元，无例外逃逸
type ToolResult =
    | ToolSuccess of content: string
    | ToolFailure of error: ToolError
```

---

### 2.7 Agent 状态机

```fsharp
/// LLM 调用的上下文，用于重试和日志
type LLMRequest = {
    Messages  : Message list    // 完整上下文
    Tools     : ToolSpec list   // 当前可用工具
    Model     : string
    MaxTokens : int
}

/// Agent 处理状态，所有合法状态的穷举
/// 非法转换（如 Idle → ExecutingTools）在类型层面无法构造
type AgentState =
    | Idle
    | BuildingPrompt  of history: Message list
    | AwaitingLLM     of request: LLMRequest * iteration: int
    | ExecutingTools  of
        calls           : ToolCall list
        pendingMessages : Message list
        iteration       : int
    | Consolidating   of session: SessionSnapshot
    | Finalizing      of response: string

// 非法状态消除示例：
// Python: 可在 Idle 状态下直接调用 registry.execute()（逻辑错误，不报警）
// F#: execute 只接受 ExecutingTools 状态中的 ToolCall list，
//     Idle 状态无法构造 ToolCall list 传入
```

---

### 2.8 会话快照（不可变）

```fsharp
/// 对话历史中的一条消息
type Message =
    | UserMessage      of content: string * media: MediaContent list
    | AssistantMessage of content: string
    | ToolCallMessage  of calls: ToolCall list
    | ToolResultMessage of id: ToolCallId * name: ToolName * content: string

/// 会话的不可变快照
/// last_consolidated 的合法范围：0 ≤ lastConsolidated ≤ messages.Length
[<Struct>]
type SessionSnapshot = private {
    Id_                : SessionId
    Messages_          : Message list
    LastConsolidated_  : int
    CreatedAt_         : System.DateTimeOffset
    UpdatedAt_         : System.DateTimeOffset
}

module SessionSnapshot =
    /// 智能构造器：唯一创建路径，强制不变量
    let create id messages lastConsolidated createdAt updatedAt =
        if lastConsolidated < 0 then
            Error $"lastConsolidated must be ≥ 0, got {lastConsolidated}"
        elif lastConsolidated > List.length messages then
            Error $"lastConsolidated ({lastConsolidated}) exceeds message count ({List.length messages})"
        else
            Ok {
                Id_               = id
                Messages_         = messages
                LastConsolidated_ = lastConsolidated
                CreatedAt_        = createdAt
                UpdatedAt_        = updatedAt
            }

    let empty id now =
        // 不变量必然满足，直接构造
        { Id_ = id; Messages_ = []; LastConsolidated_ = 0
          CreatedAt_ = now; UpdatedAt_ = now }

    // 只读访问器
    let id      s = s.Id_
    let messages s = s.Messages_
    let unconsolidated s = s.Messages_ |> List.skip s.LastConsolidated_

    /// 追加消息（返回新快照，不可变）
    let append (msg: Message) (s: SessionSnapshot) =
        { s with Messages_ = s.Messages_ @ [msg]; UpdatedAt_ = System.DateTimeOffset.UtcNow }

    /// 推进固化指针（不可倒退）
    let advanceConsolidated newIndex s =
        if newIndex < s.LastConsolidated_ then
            Error "Cannot move lastConsolidated backwards"
        elif newIndex > List.length s.Messages_ then
            Error "newIndex exceeds message count"
        else
            Ok { s with LastConsolidated_ = newIndex; UpdatedAt_ = System.DateTimeOffset.UtcNow }
```

---

### 2.9 工具规格（Tool Spec）

```fsharp
/// JSON Schema 的结构化表示（不是裸字符串）
type JsonSchemaType =
    | JsString
    | JsNumber
    | JsBoolean
    | JsArray  of items: JsonSchemaType
    | JsObject of properties: Map<string, JsonSchemaProperty>
    | JsEnum   of values: string list

and JsonSchemaProperty = {
    Type        : JsonSchemaType
    Description : string
    Required    : bool
}

/// 工具的完整规格说明
type ToolSpec = {
    Name        : ToolName
    Description : string
    Parameters  : Map<string, JsonSchemaProperty>
}
```

---

### 2.10 辅助子系统类型（待扩展）

以下子系统在 Python 实现中规模较大，此处仅定义核心 DU 以保证类型目录完整；
详细设计将在各子系统设计文档中展开。

```fsharp
// ─── 记忆固化（Memory Consolidation）─────────────────────────────────────────

/// 固化请求：将未固化的对话历史压缩入长期记忆
type ConsolidationRequest = {
    Session        : SessionSnapshot
    UnconsolidatedMessages : Message list    // 待固化的消息切片
}

/// 固化结果
type ConsolidationResult =
    | Consolidated of
        historyEntry  : string              // 追加到 HISTORY.md 的摘要行
        memoryUpdate  : string option       // 若记忆内容变化，新的 MEMORY.md 内容
        newLastIndex  : int                 // 新的 lastConsolidated 指针
    | ConsolidationSkipped                  // 消息数量未达阈值，跳过

// ─── 技能系统（Skills）───────────────────────────────────────────────────────

/// 技能元数据（来自 SKILL.md YAML frontmatter）
type SkillActivation = AlwaysActive | OnDemand

type Skill = {
    Name        : string
    Description : string
    Content     : string             // Markdown 正文，注入 system prompt
    Activation  : SkillActivation
}

// ─── 定时任务（Cron）─────────────────────────────────────────────────────────

/// Cron 调度规格（由 FParsec 从 cron 表达式字符串解析）
type CronSchedule =
    | EveryN    of minutes: int
    | Daily     of hour: int * minute: int
    | Weekly    of dayOfWeek: System.DayOfWeek * hour: int * minute: int
    | CronExpr  of raw: string              // 其他标准 cron 表达式（5 字段）

type CronStatus = Active | Paused | Completed

/// 一个定时任务定义
type CronJob = {
    Id        : TaskId
    Label     : string
    Task      : string                      // 发给 Agent 的任务描述
    Schedule  : CronSchedule
    Channel   : ChannelId
    Chat      : ChatId
    Status    : CronStatus
    CreatedAt : System.DateTimeOffset
    LastRun   : System.DateTimeOffset option
}

// ─── 心跳任务（Heartbeat）────────────────────────────────────────────────────

/// Heartbeat 服务的两阶段执行结果
type HeartbeatDecision =
    | RunHeartbeat  of tasks: string list   // Agent 决定执行的任务列表
    | SkipHeartbeat                         // Agent 决定跳过本次心跳
```

---

### 2.11 流式响应类型（SSE / Streaming）

**背景**：最新 nanobot 引入了三条流式路径——OpenAI-compat 的 SSE chunks、Anthropic SDK 的
`text_stream`、Responses API 的事件流——以及 WebSocket 多路复用层和 CLI Rich Live 渲染。
Python 实现中，流式状态散落在 `_LoopHook`、`on_stream` 回调、`asyncio.Queue` 等可变结构中。
F# 用类型将"流的每一个阶段产物"明确区分。

```fsharp
// ─── 流式增量单元 ─────────────────────────────────────────────────────────────

/// 一个流式增量片段的内容分类
/// Python: on_content_delta 回调传入裸 str，无法区分文本与思考块
type StreamDelta =
    | TextDelta       of content: string       // 普通文本片段
    | ThinkingDelta   of content: string       // <think>…</think> 思考块片段（DeepSeek-R1 等）
    | ToolArgDelta    of callId: string * chunk: string  // 工具调用参数的增量 JSON

// ─── 流式事件（单向、有序）────────────────────────────────────────────────────

/// Provider 向 Agent Loop 发出的流式事件序列
/// 合法顺序：ContentDelta* → (ToolCallStarted ToolArgDelta* ToolCallCompleted)* → StreamCompleted
/// 非法状态消除：Python 中 content 积累与 tool_call 缓冲共享可变状态；
///              F# 中每种事件携带自己的数据，状态由调用方累积（不可变 fold）
type StreamEvent =
    | ContentDelta      of delta: StreamDelta
    | ToolCallStarted   of id: ToolCallId * name: ToolName    // 新工具调用开始
    | ToolCallCompleted of call: ToolCall                      // 参数 JSON 拼接完毕
    | StreamCompleted   of finalResponse: LLMResponse          // 流结束，携带完整 usage
    | StreamError       of error: LlmError                     // 流中断

/// 流的类型别名：Provider → 消费者的异步序列
type LLMStream = IAsyncEnumerable<StreamEvent>

// ─── Agent Loop 流钩子 ────────────────────────────────────────────────────────

/// Agent loop 与下游输出层（CLI、WebSocket）的契约
/// Python: _LoopHook 类，含可变 _buf + _on_stream 回调
/// F#: 不可变 record，函数字段替代方法
type AgentStreamHook = {
    /// 接收文本增量；下游负责缓冲、节流和渲染
    OnDelta      : string -> Async<unit>
    /// 流结束信号；isResuming=true 表示后面还有工具调用（保持 spinner）
    OnStreamEnd  : isResuming: bool -> Async<unit>
    /// 是否启用流式模式（false → 使用 chat()，true → 使用 chatStream()）
    WantsStreaming : bool
}

// 无流式需求时的空钩子（替代 Python 中的 None 判断）
let noStreamHook = {
    OnDelta       = fun _ -> async.Return ()
    OnStreamEnd   = fun _ -> async.Return ()
    WantsStreaming = false
}

// ─── HTTP SSE 层（/v1/chat/completions）────────────────────────────────────

/// SSE 响应块，OpenAI 兼容格式
/// Python: _sse_chunk() 返回裸 bytes，调用方无法静态知晓字段
type SseChunk =
    | TextChunk    of id: string * model: string * content: string
    | DoneChunk    of id: string * model: string  // finish_reason="stop"
    | DoneSentinel                                // data: [DONE]

// ─── WebSocket 多路复用层（WebUI）─────────────────────────────────────────────

/// 渠道内的对话标识（WebUI 侧）
type WsChatId = WsChatId of string

/// 服务器 → 客户端事件（穷举替代 Python 侧的 event: string 字段检查）
type InboundWsEvent =
    | WsReady       of chatId: WsChatId * clientId: string
    | WsAttached    of chatId: WsChatId
    | WsDelta       of chatId: WsChatId * text: string
    | WsStreamEnd   of chatId: WsChatId
    | WsMessage     of chatId: WsChatId * text: string * kind: WsMessageKind option
    | WsError       of detail: string option

and WsMessageKind = ToolHint | Progress

/// 客户端 → 服务器事件
type OutboundWsEvent =
    | WsNewChat
    | WsAttach   of chatId: WsChatId
    | WsSend     of chatId: WsChatId * content: string * media: MediaContent list

// 非法状态消除：
// Python/TS: event.type / event.event 是裸字符串，typo 只在运行时发现
// F#: 新增事件类型必须在 DU 中注册，所有 match 处编译器自动报警
```

---

### 2.12 Provider 类型驱动建模

**背景**：Python 的 `LLMProvider` 是抽象基类，`LLMResponse` 含 13 个字段（其中 7 个仅在
`finish_reason="error"` 时有意义）。F# 将"成功响应"与"错误响应"用 DU 分开，并用
record-of-functions 替代继承，消除因 provider 差异导致的防御性 `if` 嵌套。

```fsharp
// ─── Provider 能力标签 ────────────────────────────────────────────────────────

/// Provider 支持的能力集合（替代 Python 中散落在各 provider 的 if/else）
type ProviderCapability =
    | PromptCaching        // 支持 cache_control ephemeral 标记（Anthropic、OpenRouter）
    | ExtendedThinking     // 支持思考块 / reasoning_effort
    | FunctionCalling      // 支持工具调用
    | VisionInput          // 支持图片输入
    | ResponsesApi         // 支持 OpenAI Responses API（GPT-5、o-series）
    | Streaming            // 支持流式输出

// ─── 思考风格（Thinking Style）──────────────────────────────────────────────

/// 不同 provider 的"思考"参数格式各不相同；用 DU 消除各处的字符串判断
type ThinkingStyle =
    | ThinkingType        // Anthropic: { type: "enabled", budget_tokens: N }
    | EnableThinking      // 部分 OpenAI-compat: enable_thinking=true
    | ReasoningSplit      // DeepSeek-R1: reasoning_content 字段分离
    | ReasoningEffortParam // OpenAI o-series: reasoning_effort="medium"

// ─── 推理强度 ────────────────────────────────────────────────────────────────

type ReasoningEffort = Low | Medium | High | Adaptive

// ─── 生成参数（冻结，不可变）─────────────────────────────────────────────────

[<Struct>]
type GenerationSettings = {
    Temperature     : float
    MaxTokens       : int
    ReasoningEffort : ReasoningEffort option  // None → 不传递该参数
}

module GenerationSettings =
    let defaults = { Temperature = 0.7; MaxTokens = 4096; ReasoningEffort = None }

// ─── LLM 错误（取代裸 finish_reason="error" + 7 个可选字段）──────────────────

/// 错误分类：Python 中 7 个可选字段同时存在于 LLMResponse，调用方须逐一检查
/// F# 中每种错误携带且仅携带相关数据
type LlmErrorKind =
    | RateLimited         of retryAfter: System.TimeSpan option   // HTTP 429，可重试
    | QuotaExceeded                                                // 余额不足，不可重试
    | ServerError         of statusCode: int                       // 5xx
    | Timeout             of kind: TimeoutKind
    | ConnectionFailed    of reason: string
    | ModelNotFound       of model: string
    | ContextTooLong                                               // finish_reason="length"
    | MalformedResponse   of parseError: ParseError               // 响应无法解析

and TimeoutKind = StreamIdleTimeout | RequestTimeout

type LlmError = {
    Kind          : LlmErrorKind
    RawMessage    : string           // 原始错误文本，用于日志
    ProviderCode  : string option    // provider 返回的语义代码（如 "rate_limit_exceeded"）
    ShouldRetry   : bool             // provider 建议是否重试
}

// ─── LLM 响应（非流式完整响应）──────────────────────────────────────────────

/// 工具调用请求（来自 LLM 响应）
/// 对应 Python ToolCallRequest；extra_content/provider_specific_fields 已收敛为 option
type ToolCall = {
    Id        : ToolCallId
    Tool      : ToolName
    Arguments : Map<string, JsonValue>
    ProviderMeta : Map<string, JsonValue> option  // Gemini 等 provider 的扩展字段
}

/// 思考块（Anthropic extended thinking）
type ThinkingBlock = {
    Type      : string        // "thinking"
    Thinking  : string        // 完整思考文本
    Signature : string option // Anthropic 内部签名
}

/// 完整 LLM 响应，用 DU 区分"有内容"与"工具调用"两种成功情况
/// 非法状态消除：Python content+tool_calls 同时非 None 是合法对象但语义矛盾
type LLMResponseBody =
    | TextOnly      of content: string
    | WithToolCalls of content: string option * calls: ToolCall list
    | Empty                                          // 合法空响应

type LLMResponse = {
    Body             : LLMResponseBody
    ReasoningContent : string option                 // 分离的推理内容（DeepSeek-R1 等）
    ThinkingBlocks   : ThinkingBlock list            // Anthropic extended thinking
    Usage            : TokenUsage
}

and TokenUsage = {
    PromptTokens     : int
    CompletionTokens : int
    CachedTokens     : int   // prompt caching 命中的 token 数（0 表示未命中）
}

// ─── 重试策略 ────────────────────────────────────────────────────────────────

type RetryMode =
    | FixedRetries of maxAttempts: int * delays: System.TimeSpan list
    | Persistent   of maxDelayPerAttempt: System.TimeSpan  // 无限重试直到成功

type RetryPolicy = {
    Mode              : RetryMode
    ImageFallback     : bool  // 非瞬态错误时，降级为去除图片后重试
    CircuitBreakerThreshold : int  // 连续相同错误 N 次后停止 Persistent 模式
}

module RetryPolicy =
    let standard  = { Mode = FixedRetries (3, [TimeSpan.FromSeconds 1.; TimeSpan.FromSeconds 2.; TimeSpan.FromSeconds 4.]); ImageFallback = true; CircuitBreakerThreshold = 10 }
    let persistent = { Mode = Persistent (TimeSpan.FromSeconds 60.); ImageFallback = true; CircuitBreakerThreshold = 10 }

// ─── Provider 接口（record-of-functions，替代抽象类继承）─────────────────────

/// Provider 能力声明 + 函数字段
/// Python: 抽象基类 LLMProvider + 多层继承；子类通过 override 实现差异
/// F#: record 组合替代继承；不同 provider 是不同 record 值，而非不同类型
type LLMProvider = {
    Id           : string                                 // "anthropic", "openai-compat" 等
    DefaultModel : string
    Capabilities : Set<ProviderCapability>

    /// 非流式调用：返回完整响应或错误
    Chat : GenerationSettings
          -> messages   : Message list
          -> tools      : ToolSpec list
          -> Async<Result<LLMResponse, LlmError>>

    /// 流式调用：向 emitter 推送 StreamEvent，结束后返回 Ok() 或 LlmError
    ChatStream : GenerationSettings
               -> messages  : Message list
               -> tools     : ToolSpec list
               -> emitter   : (StreamEvent -> Async<unit>)
               -> Async<Result<unit, LlmError>>

    /// 重试包装器（由 Application 层统一注入，provider 本身不含重试）
    RetryPolicy : RetryPolicy
}

/// 带重试的调用入口（Application 层使用，不直接调用 provider.Chat）
let chatWithRetry (provider: LLMProvider) settings messages tools : Async<Result<LLMResponse, LlmError>> =
    asyncResult {
        // 由 RetryPolicy 驱动；isTransient 判断基于 LlmErrorKind
        return! retryLoop provider.RetryPolicy (fun () ->
            provider.Chat settings messages tools)
    }

// ─── Provider 后端类型 ────────────────────────────────────────────────────────

/// Provider 的后端实现类型（用于注册表和工厂）
type ProviderBackend =
    | AnthropicBackend
    | OpenAICompatBackend
    | AzureOpenAIBackend
    | GitHubCopilotBackend
    | LocalBackend of host: string   // Ollama、vLLM 等

/// 注册表中的 provider 元数据（静态配置，不含运行时状态）
type ProviderSpec = {
    Id                   : string
    Keywords             : string list           // 模型名关键词，用于自动匹配
    Backend              : ProviderBackend
    IsGateway            : bool                  // 是否为网关（如 OpenRouter，可路由任意模型）
    Capabilities         : Set<ProviderCapability>
    ThinkingStyle        : ThinkingStyle option
    EnvKeyName           : string                // 对应的环境变量名（API Key）
}

/// 从 ProviderSpec + 运行时配置构造 LLMProvider（工厂函数）
type ProviderFactory = ProviderSpec -> apiKey: ApiKey -> apiBase: System.Uri option -> LLMProvider
```

**为什么用 record-of-functions 而非接口/抽象类**：

| Python 方式 | F# record 方式 |
|---|---|
| 新 provider = 新子类，需继承 `LLMProvider` | 新 provider = 新 `LLMProvider` record 值，无需子类型 |
| mock 测试需要 `MagicMock` 或 stub 子类 | 测试直接构造带假函数的 record |
| `isinstance(provider, AnthropicProvider)` 分支 | 通过 `Capabilities` 集合查询，无需类型转换 |
| retry 逻辑在基类方法里 | retry 在 Application 层统一注入，provider 纯粹 |

---

## 3. 系统边界与解析器

**原则**：所有非类型化数据进入系统的入口，都必须有对应的解析器。
解析成功则得到领域类型，失败则返回结构化的 `ParseError`，不产生"半初始化"的对象。

**两种解析工具，用途不同：**

| 工具 | 适用场景 | 原因 |
|---|---|---|
| **FParsec** | 原始文本（斜线命令、Cron 表达式、API Key 格式校验） | 需要字符级别的解析控制和有意义的错误位置 |
| **组合式 JSON 解码器**（`Result`-returning functions over `JsonElement`） | JSON 载荷（LLM 响应、MCP 消息、配置文件、JSONL 会话） | JSON 已预结构化，逐字段提取比字符解析更清晰 |

两者都遵循"parse, don't validate"原则：解析器的返回类型即是领域类型，不产生中间的"待验证"状态。

### 入口点清单

| 数据入口 | 来源 | 解析目标类型 | 解析工具 | 解析器模块 |
|---|---|---|---|---|
| 用户聊天输入 | 各渠道原始字符串 | `UserInput` | FParsec | `Input.Parser` |
| Cron 表达式 | `cron.json` 中的字符串字段 | `CronSchedule` | FParsec | `Cron.Parser` |
| SSE 文本帧 | HTTP 流式响应行 | `SseFrame` | FParsec | `Sse.Parser` |
| WebSocket 事件 | WS 消息 JSON body | `InboundWsEvent` | JSON 解码器 | `Ws.Parser` |
| 配置文件 | `~/.nanobot/config.json` | `NanobotConfig` | JSON 解码器 | `Config.Parser` |
| LLM API 响应（非流式）| OpenAI 兼容 JSON | `LLMResponse` | JSON 解码器 | `Llm.Parser` |
| LLM SSE 数据帧（流式）| SSE `data:` 字段 | `StreamEvent` | JSON 解码器 | `Llm.Parser` |
| MCP JSON-RPC 请求 | stdio / HTTP body | `McpRequest` | JSON 解码器 | `Mcp.Parser` |
| MCP JSON-RPC 响应 | MCP 服务器 | `McpResponse` | JSON 解码器 | `Mcp.Parser` |
| 工具调用参数 | LLM 响应内嵌 JSON 字符串 | `Map<string, JsonValue>` | JSON 解码器 | `Tool.Parser` |
| JSONL 会话文件 | 磁盘逐行读取 | `SessionSnapshot` | JSON 解码器 | `Session.Parser` |

---

### 3.1 原始文本解析（FParsec）

FParsec 用于解析**需要字符级控制**的原始字符串输入，包括用户聊天输入和 Cron 表达式。

**用户输入解析器**

```fsharp
// Infrastructure/Input/Parser.fs
open FParsec

/// 解析所有合法斜线命令
let private slashCommandParser : Parser<SlashCommand, unit> =
    choice [
        stringReturn "/new"  NewSession
        stringReturn "/stop" StopProcessing
        stringReturn "/help" ShowHelp
    ]
    .>> (eof <|> skipChar ' ')   // 命令后须是行尾或空格

/// 解析普通消息内容（任意文本）
let private chatContentParser : Parser<string, unit> =
    restOfLine true              // 读至行尾，保留换行

/// 入口：将原始字符串解析为 UserInput
/// 非法命令（如 /unknowncmd）→ Failure，而非悄悄传给 LLM
let parseUserInput (raw: string) : Result<UserInput, string> =
    let parser =
        choice [
            attempt (slashCommandParser |>> Command)
            chatContentParser |>> (fun c -> ChatMessage (c, []))
        ]
    match run parser (raw.Trim()) with
    | Success (result, _, _) -> Ok result
    | Failure (msg,  _, _)   -> Error msg
```

**Cron 表达式解析器**

```fsharp
// Infrastructure/Cron/Parser.fs
open FParsec

/// 解析 "every N minutes" 简写格式
let private everyNParser : Parser<CronSchedule, unit> =
    pstring "every " >>. pint32 .>> pstring " min" |>> EveryN

/// 解析 "daily HH:MM" 格式
let private dailyParser : Parser<CronSchedule, unit> =
    pstring "daily " >>.
    pipe2 (pint32 .>> pchar ':') pint32
        (fun h m -> Daily (h, m))

/// 解析标准 5 字段 cron 表达式（退化到裸字符串）
let private stdCronParser : Parser<CronSchedule, unit> =
    // 5 个空白分隔字段：分 时 日 月 周
    let field = many1Chars (noneOf " \t\n")
    pipe5 (field .>> spaces) (field .>> spaces) (field .>> spaces)
          (field .>> spaces) field
        (fun mi h d mo dow -> CronExpr $"{mi} {h} {d} {mo} {dow}")

/// 入口解析器
let parseCronSchedule (raw: string) : Result<CronSchedule, string> =
    let parser = choice [ attempt everyNParser; attempt dailyParser; stdCronParser ]
    match run parser (raw.Trim()) with
    | Success (sched, _, _) -> Ok sched
    | Failure (msg, _, _)   -> Error msg
```

**SSE 文本帧解析器**

SSE 是文本协议（`data: ...\n\n` 格式），属于原始文本解析，用 FParsec。
解析出 `SseFrame` 后，再用 JSON 解码器将 `data` 字段转换为 `StreamEvent`。

```fsharp
// Infrastructure/Providers/SseParser.fs
open FParsec

/// SSE 单帧（一个 data: 行 + 空行）
type SseFrame =
    | DataLine  of data: string   // "data: <json>"
    | DoneLine                    // "data: [DONE]"
    | CommentLine                 // ": keep-alive" 等心跳行

/// 解析单个 SSE 帧
let private sseDataParser : Parser<SseFrame, unit> =
    pstring "data: " >>. restOfLine false |>> fun payload ->
        if payload.TrimEnd() = "[DONE]" then DoneLine
        else DataLine payload

let private sseCommentParser : Parser<SseFrame, unit> =
    pchar ':' >>. restOfLine false >>% CommentLine

let private sseFrameParser : Parser<SseFrame, unit> =
    choice [ attempt sseDataParser; sseCommentParser ]
    .>> (skipChar '\n' <|> eof)

/// 将 SSE 响应体（字节流切行）解析为 SseFrame 序列
/// 解析失败的行 → ParseError，不丢弃（避免静默丢失 token）
let parseSseLine (line: string) : Result<SseFrame, ParseError> =
    if System.String.IsNullOrWhiteSpace line then
        Ok CommentLine  // 空行是帧分隔符，正常跳过
    else
        match run sseFrameParser line with
        | Success (frame, _, _) -> Ok frame
        | Failure (msg, _, _)   -> Error (JsonParseError (msg, 0))

/// 将 DataLine 中的 JSON 解码为 StreamEvent（使用 JSON 解码器，非 FParsec）
let decodeSseFrame (frame: SseFrame) : Result<StreamEvent option, ParseError> =
    match frame with
    | CommentLine       -> Ok None
    | DoneLine          -> Ok (Some (StreamCompleted (Unchecked.defaultof<_>)))  // 由上层补全 usage
    | DataLine jsonStr  ->
        use doc = System.Text.Json.JsonDocument.Parse jsonStr
        parseLlmChunk doc.RootElement |> Result.map Some
```

---

### 3.2 配置文件解析器（JSON 解码器）

```fsharp
// Infrastructure/Config/Parser.fs
open System.Text.Json
open FSharpPlus  // Result computation expression

type AnthropicConfig = { ApiKey: ApiKey; DefaultModel: string }
type ProviderConfig   = Anthropic of AnthropicConfig | OpenAI of OpenAIConfig | Custom of CustomConfig
type McpServerConfig  =
    | StdioServer  of command: string * args: string list * env: Map<string,string>
    | HttpServer   of url: System.Uri * headers: Map<string,string>

/// API Key 的 newtype，构造时即验证前缀
type ApiKey = private ApiKey of string

module ApiKey =
    let create (raw: string) =
        if System.String.IsNullOrWhiteSpace raw then
            Error "API key cannot be empty"
        else
            Ok (ApiKey raw)
    let value (ApiKey k) = k

/// 从 JSON 节点解析配置（返回类型化结果，不抛异常）
let parseConfig (json: JsonDocument) : Result<NanobotConfig, ParseError list> =
    result {
        let root = json.RootElement

        let! anthropicKey =
            root
            |> Json.tryGetString ["gateway"; "providers"; "anthropic"; "apiKey"]
            |> Result.bind ApiKey.create
            |> Result.mapError (fun e -> [SchemaError ("anthropic.apiKey", e)])

        let! defaultModel =
            root
            |> Json.tryGetString ["agents"; "defaults"; "model"]
            |> Result.mapError (fun e -> [SchemaError ("agents.defaults.model", e)])

        let! mcpServers =
            root
            |> Json.tryGetObject ["tools"; "mcpServers"]
            |> Result.map parseMcpServers
            |> Result.defaultWith (fun _ -> Ok Map.empty)

        return {
            AnthropicKey = anthropicKey
            DefaultModel = defaultModel
            McpServers   = mcpServers
        }
    }

// 非法状态消除：
// Python: Config 对象可以被构造出 api_key=""（Pydantic 仅在校验时报错）
// F#: ApiKey.create "" → Error，NanobotConfig 中的 ApiKey 字段类型保证非空
```

---

### 3.3 LLM 响应解析器（JSON 解码器）

```fsharp
// Infrastructure/Providers/LlmResponseParser.fs

/// 解析单个工具调用
let private parseToolCall (json: JsonElement) : Result<ToolCall, ParseError> =
    result {
        let! id   = json |> Json.requireString "id"   |> Result.mapError (SchemaError "tool_call.id")
        let! name = json |> Json.requireString ["function"; "name"]
                         |> Result.map ToolName
                         |> Result.mapError (SchemaError "tool_call.function.name")
        let! args = json |> Json.requireString ["function"; "arguments"]
                         |> Result.bind parseJsonArguments
                         |> Result.mapError (SchemaError "tool_call.function.arguments")
        return { Id = ToolCallId id; Tool = name; Arguments = args }
    }

/// 解析 LLM 完整响应（OpenAI chat/completions 格式）
let parseLlmResponse (json: JsonElement) : Result<LLMResponse, ParseError> =
    result {
        let! choices    = json |> Json.requireArray "choices"
        let! first      = choices |> Seq.tryHead |> Result.ofOption (SchemaError ("choices", "empty array"))
        let! finishReason = first |> Json.requireString "finish_reason"
        let  message    = first.GetProperty("message")

        return!
            match finishReason with
            | "stop" ->
                match message |> Json.tryGetString "content" with
                | Some c when c.Length > 0 -> Ok (TextResponse c)
                | _                        -> Ok EmptyResponse

            | "tool_calls" ->
                result {
                    let! rawCalls = message |> Json.requireArray "tool_calls"
                    let! calls    = rawCalls |> Seq.map parseToolCall |> Result.sequence
                    let  nonEmpty = calls |> Seq.toList
                    if List.isEmpty nonEmpty then
                        return! Error (SchemaError ("tool_calls", "empty array with finish_reason=tool_calls"))
                    else
                        return ToolCallResponse nonEmpty
                }

            | "error" ->
                result {
                    let! msg = json |> Json.tryGetString ["error"; "message"]
                                    |> Result.ofOption (SchemaError ("error.message", "missing"))
                    return ErrorResponse (msg, retryable = false)
                }

            | other ->
                Error (UnknownField $"finish_reason={other}")
    }

// 消除的非法状态：
// Python: response.content 和 response.tool_calls 同时非 None → 调用方需 if/elif
// F#: 解析器保证 TextResponse | ToolCallResponse | EmptyResponse | ErrorResponse 四选一
```

---

### 3.4 MCP JSON-RPC 解析器（JSON 解码器）

```fsharp
// Infrastructure/Mcp/Parser.fs

type McpRequest =
    | Initialize of id: string * clientInfo: McpClientInfo
    | ListTools  of id: string
    | CallTool   of id: string * name: ToolName * arguments: Map<string, JsonValue>
    | Ping       of id: string

type McpResponse =
    | InitializeOk  of id: string * capabilities: McpCapabilities
    | ToolList      of id: string * tools: ToolSpec list
    | ToolResult    of id: string * content: string * isError: bool
    | McpError      of id: string * code: int * message: string

/// 解析 MCP JSON-RPC 请求（stdio 或 HTTP body）
let parseMcpRequest (json: JsonElement) : Result<McpRequest, ParseError> =
    result {
        let! id     = json |> Json.requireString "id"
        let! method = json |> Json.requireString "method"

        return!
            match method with
            | "initialize" ->
                result {
                    let! clientInfo = json |> Json.getObject "params" |> parseClientInfo
                    return Initialize (id, clientInfo)
                }

            | "tools/list"  -> Ok (ListTools id)

            | "tools/call"  ->
                result {
                    let params = json.GetProperty("params")
                    let! name  = params |> Json.requireString "name" |> Result.map ToolName
                    let! args  = params |> Json.tryGetObject "arguments"
                                         |> Option.defaultValue JsonElement.Empty
                                         |> parseArguments
                    return CallTool (id, name, args)
                }

            | "ping" -> Ok (Ping id)

            | unknown ->
                Error (UnknownField $"MCP method: {unknown}")
    }

// 消除的非法状态：
// Python: mcp.py 使用 session.call_tool(name, arguments)，name 是裸字符串
//         调用不存在的工具只在 MCP 服务器侧才报错
// F#: ToolName 类型 + CallTool 的 name 字段在解析时即从 JSON 提取并包装
```

---

### 3.5 JSONL 会话文件解析器（JSON 解码器）

```fsharp
// Infrastructure/Storage/SessionParser.fs

/// 解析单行 JSONL 消息记录
let private parseMessageLine (json: JsonElement) : Result<Message, ParseError> =
    result {
        let! role = json |> Json.requireString "role"
        return!
            match role with
            | "user" ->
                result {
                    let! content = json |> Json.requireString "content"
                    return UserMessage (content, [])
                }
            | "assistant" ->
                match json |> Json.tryGetArray "tool_calls" with
                | Some calls ->
                    result {
                        let! parsed = calls |> Seq.map parseLlmToolCall |> Result.sequence
                        return ToolCallMessage (List.ofSeq parsed)
                    }
                | None ->
                    result {
                        let! content = json |> Json.requireString "content"
                        return AssistantMessage content
                    }
            | "tool" ->
                result {
                    let! callId = json |> Json.requireString "tool_call_id" |> Result.map ToolCallId
                    let! name   = json |> Json.requireString "name"         |> Result.map ToolName
                    let! content = json |> Json.requireString "content"
                    return ToolResultMessage (callId, name, content)
                }
            | unknown ->
                Error (UnknownField $"message role: {unknown}")
    }

/// 从 JSONL 文件内容解析完整会话快照
let parseSessionFile (sessionId: SessionId) (lines: string seq) : Result<SessionSnapshot, ParseError list> =
    let results = lines |> Seq.map (fun line ->
        match System.Text.Json.JsonDocument.Parse(line) with
        | doc  -> parseMessageLine doc.RootElement |> Result.mapError List.singleton
        | exception ex -> Error [JsonParseError (ex.Message, 0)]
    )
    result {
        let! messages = results |> Result.sequence
        let now = System.DateTimeOffset.UtcNow
        return!
            SessionSnapshot.create sessionId (List.ofSeq messages) 0 now now
            |> Result.mapError (fun e -> [SchemaError ("session", e)])
    }
```

---

## 4. 每会话 Actor

### 4.1 Session Actor 协议

```fsharp
// Application/SessionActor.fs

/// Session Actor 接收的消息类型（协议 DU）
type SessionActorMsg =
    | ProcessInput  of input: InboundMessage * reply: AsyncReplyChannel<Result<string, AgentError>>
    | CancelCurrent                                    // 对应 /stop
    | GetSnapshot   of reply: AsyncReplyChannel<SessionSnapshot>
    | Shutdown

/// 依赖注入接口（六边形架构的 Port）
type AgentDependencies = {
    Llm      : LlmRequest -> Async<Result<LLMResponse, LlmError>>
    Tools    : ToolName -> Map<string, JsonValue> -> Async<ToolResult>
    Storage  : SessionId -> Async<Result<SessionSnapshot, StorageError>>
    Persist  : SessionSnapshot -> Async<Result<unit, StorageError>>
    BuildPrompt : SessionSnapshot -> InboundMessage -> Result<LlmRequest, ParseError>
}
```

---

### 4.2 状态转换函数（纯函数）

```fsharp
// Domain/StateMachine.fs

/// Agent 状态机接收的事件 DU
/// 每个事件只在特定状态下合法——编译器通过穷举模式匹配强制处理所有组合
type AgentEvent =
    | MessageReceived    of InboundMessage
    | PromptBuilt        of LLMRequest
    | LlmRespondedWithText  of content: string
    | LlmRespondedWithTools of calls: ToolCall list
    | ToolsExecuted      of results: (ToolCall * ToolResult) list
    | ResponseSent

/// 纯函数：给定当前状态和事件，返回下一状态
/// 不含任何 IO；所有 IO 在 Application 层注入
/// 注意：无 catch-all 分支——编译器会对未覆盖的 (state, event) 组合发出警告
let transition (state: AgentState) (event: AgentEvent) : AgentState =
    match state, event with

    // Idle → BuildingPrompt
    | Idle, MessageReceived msg ->
        BuildingPrompt []

    // BuildingPrompt → AwaitingLLM（prompt 构建完成）
    | BuildingPrompt _, PromptBuilt request ->
        AwaitingLLM (request, iteration = 0)

    // AwaitingLLM → ExecutingTools（LLM 要求工具调用）
    | AwaitingLLM (req, iter), LlmRespondedWithTools calls ->
        let pending = req.Messages @ [ToolCallMessage calls]
        ExecutingTools (calls, pending, iter)

    // AwaitingLLM → Finalizing（LLM 直接回复文本）
    | AwaitingLLM _, LlmRespondedWithText content ->
        Finalizing content

    // ExecutingTools → AwaitingLLM（工具执行完毕，未超出迭代限制）
    | ExecutingTools (_, pending, iter), ToolsExecuted results when iter < 40 ->
        let resultMessages = results |> List.map (fun (call, res) ->
            let content = match res with ToolSuccess c -> c | ToolFailure e -> formatToolError e
            ToolResultMessage (call.Id, call.Tool, content))
        let updatedMessages = pending @ resultMessages
        AwaitingLLM ({ Messages = updatedMessages; Tools = []; Model = ""; MaxTokens = 0 }, iter + 1)

    // ExecutingTools → Finalizing（达到最大迭代次数）
    | ExecutingTools (_, _, _), ToolsExecuted _ ->
        Finalizing "(max iterations reached)"

    // Finalizing → Idle（响应已发送）
    | Finalizing _, ResponseSent ->
        Idle

    // 以下为 (state, event) 组合在当前设计中不应发生的情况。
    // 不提供 catch-all，让编译器列出所有未覆盖分支——这是类型驱动设计的护栏。
    // 如果出现编译器警告，说明状态机定义或调用方存在逻辑问题，需要显式处理。
    | Idle,           PromptBuilt _             -> state  // 不应在 Idle 时收到 PromptBuilt
    | Idle,           LlmRespondedWithText _    -> state
    | Idle,           LlmRespondedWithTools _   -> state
    | Idle,           ToolsExecuted _           -> state
    | Idle,           ResponseSent              -> state
    | BuildingPrompt _, MessageReceived _       -> state
    | BuildingPrompt _, LlmRespondedWithText _  -> state
    | BuildingPrompt _, LlmRespondedWithTools _ -> state
    | BuildingPrompt _, ToolsExecuted _         -> state
    | BuildingPrompt _, ResponseSent            -> state
    | AwaitingLLM _,  MessageReceived _         -> state
    | AwaitingLLM _,  PromptBuilt _             -> state
    | AwaitingLLM _,  ToolsExecuted _           -> state
    | AwaitingLLM _,  ResponseSent              -> state
    | ExecutingTools _, MessageReceived _       -> state
    | ExecutingTools _, PromptBuilt _           -> state
    | ExecutingTools _, LlmRespondedWithText _  -> state
    | ExecutingTools _, LlmRespondedWithTools _ -> state
    | ExecutingTools _, ResponseSent            -> state
    | Consolidating _,  _                       -> state
    | Finalizing _,     MessageReceived _       -> state
    | Finalizing _,     PromptBuilt _           -> state
    | Finalizing _,     LlmRespondedWithText _  -> state
    | Finalizing _,     LlmRespondedWithTools _ -> state
    | Finalizing _,     ToolsExecuted _         -> state
```

> **设计说明**：上述穷举所有非法转换并显式返回 `state`，而非使用 `_ -> state` 捕获。
> 理由：如果将来新增 `AgentState` 或 `AgentEvent` 分支，编译器的"不完整模式匹配"警告会精确定位
> 到需要决策的组合，而不是被 catch-all 静默吞掉。

---

### 4.3 Session Actor 实现

```fsharp
// Application/SessionActor.fs

/// 创建一个 per-session MailboxProcessor
let createSessionActor (sessionId: SessionId) (deps: AgentDependencies) =
    MailboxProcessor<SessionActorMsg>.Start(fun inbox ->
        let rec loop (state: AgentState) (session: SessionSnapshot) = async {
            let! msg = inbox.Receive()

            match msg with
            | Shutdown -> ()  // 终止递归，Actor 退出

            | GetSnapshot reply ->
                reply.Reply session
                return! loop state session

            | CancelCurrent ->
                // 取消当前 LLM 调用（通过 CancellationToken 实现）
                return! loop Idle session

            | ProcessInput (inbound, reply) ->
                match inbound.Input with
                | Command NewSession ->
                    // /new：清除会话
                    let cleared = SessionSnapshot.empty sessionId System.DateTimeOffset.UtcNow
                    do! deps.Persist cleared |> Async.Ignore
                    reply.Reply (Ok "Session cleared.")
                    return! loop Idle cleared

                | Command ShowHelp ->
                    reply.Reply (Ok helpText)
                    return! loop state session

                | Command StopProcessing ->
                    reply.Reply (Ok "Stopping.")
                    return! loop Idle session

                | ChatMessage _ ->
                    // 核心 agent 循环
                    let! result = runAgentLoop inbound session deps
                    match result with
                    | Ok (response, updatedSession) ->
                        reply.Reply (Ok response)
                        return! loop Idle updatedSession
                    | Error err ->
                        reply.Reply (Error err)
                        return! loop Idle session
        }

        async {
            let! loaded = deps.Storage sessionId
            let session =
                match loaded with
                | Ok s  -> s
                | Error _ -> SessionSnapshot.empty sessionId System.DateTimeOffset.UtcNow
            return! loop Idle session
        }
    )

/// 全局 Agent 协调器（路由消息到对应 Session Actor）
type AgentCoordinator(deps: AgentDependencies) =
    let actors =
        System.Collections.Concurrent.ConcurrentDictionary<SessionId, MailboxProcessor<SessionActorMsg>>()

    member _.Route (msg: InboundMessage) : Async<Result<string, AgentError>> =
        let sid    = sessionId msg
        let actor  = actors.GetOrAdd(sid, fun id -> createSessionActor id deps)
        actor.PostAndAsyncReply(fun reply -> ProcessInput (msg, reply))
```

---

### 4.4 Agent 核心循环

```fsharp
// Application/AgentLoop.fs

/// 单次迭代：调用 LLM → 处理响应
let rec private iterate
    (session: SessionSnapshot)
    (request: LlmRequest)
    (iteration: int)
    (deps: AgentDependencies)
    : Async<Result<string * SessionSnapshot, AgentError>> =

    asyncResult {
        if iteration >= 40 then
            return! Error (MaxIterationsReached 40)

        let! response = deps.Llm request |> AsyncResult.mapError LlmFailure

        match response with
        | TextResponse content ->
            let updated = session |> SessionSnapshot.append (AssistantMessage content)
            return content, updated

        | EmptyResponse ->
            return "", session

        | ErrorResponse (msg, _) ->
            return! Error (LlmFailure (ApiError (500, msg)))

        | ToolCallResponse calls ->
            // 追加 LLM 的工具调用消息
            let withCalls = session |> SessionSnapshot.append (ToolCallMessage calls)

            // 并行执行工具
            let! results =
                calls
                |> List.map (fun call ->
                    deps.Tools call.Tool call.Arguments
                    |> Async.map (fun r -> call, r))
                |> Async.Parallel
                |> Async.map Array.toList

            // 追加工具结果消息
            let withResults =
                results |> List.fold (fun s (call, result) ->
                    let content =
                        match result with
                        | ToolSuccess c -> c
                        | ToolFailure e -> formatToolError e
                    s |> SessionSnapshot.append
                        (ToolResultMessage (call.Id, call.Tool, content))
                ) withCalls

            // 构建下一轮请求
            let nextRequest = { request with Messages = withResults |> SessionSnapshot.messages }
            return! iterate withResults nextRequest (iteration + 1) deps
    }

/// 完整的 agent loop 入口
let runAgentLoop
    (inbound: InboundMessage)
    (session: SessionSnapshot)
    (deps: AgentDependencies)
    : Async<Result<string * SessionSnapshot, AgentError>> =

    asyncResult {
        let sessionWithInput =
            match inbound.Input with
            | ChatMessage (c, media) ->
                session |> SessionSnapshot.append (UserMessage (c, media))
            | Command _ -> session  // 斜线命令不追加消息

        let! request =
            deps.BuildPrompt sessionWithInput inbound
            |> Result.mapError ParseFailure
            |> Async.retn

        let! (response, finalSession) = iterate sessionWithInput request 0 deps

        do! deps.Persist finalSession |> AsyncResult.mapError StorageFailure

        return response, finalSession
    }
```

---

## 5. 铁路型错误处理

### 5.1 错误类型体系

```fsharp
// Domain/Errors.fs

/// 解析阶段错误（系统边界）
type ParseError =
    | JsonParseError   of message: string * position: int
    | SchemaError      of field: string * message: string
    | UnknownField     of name: string
    | MissingField     of name: string

/// LLM 调用错误
type LlmError =
    | ApiError            of statusCode: int * message: string
    | RateLimitError      of retryAfter: System.TimeSpan
    | ModelNotFound       of model: string
    | ContextLengthExceeded
    | MalformedResponse   of ParseError

/// 工具执行错误
type ToolError =
    | ToolNotFound       of name: ToolName
    | ParameterMissing   of field: string
    | ParameterInvalid   of field: string * reason: string
    | ExecutionFailed    of exn: string
    | ExecutionTimeout   of after: System.TimeSpan
    | WorkspaceViolation of path: string

/// 渠道通信错误
type ChannelError =
    | NotAuthenticated
    | MessageTooLong   of length: int * maxLength: int
    | RateLimited      of retryAfter: System.TimeSpan
    | ChannelClosed    of id: ChannelId

/// 持久化错误
type StorageError =
    | FileNotFound   of path: string
    | ParseFailure   of ParseError
    | WriteFailure   of reason: string

/// Agent 顶层错误（供协调器使用）
type AgentError =
    | ParseFailure      of ParseError
    | LlmFailure        of LlmError
    | ToolFailure       of ToolError
    | ChannelFailure    of ChannelError
    | StorageFailure    of StorageError
    | MaxIterationsReached of int
```

---

### 5.2 AsyncResult 计算表达式

```fsharp
// Shared/AsyncResult.fs

/// 将 Async<Result<'a, 'e>> 封装为计算表达式
type AsyncResultBuilder() =
    member _.Return x = async { return Ok x }
    member _.ReturnFrom x = x
    member _.Bind (m, f) = async {
        let! r = m
        match r with
        | Ok x    -> return! f x
        | Error e -> return Error e
    }
    member _.Zero () = async { return Ok () }
    member _.Combine (a, b) = async {
        let! r = a
        match r with
        | Ok ()   -> return! b
        | Error e -> return Error e
    }

let asyncResult = AsyncResultBuilder()

/// 辅助函数
module AsyncResult =
    let mapError f m = async {
        let! r = m
        return Result.mapError f r
    }
    let ofAsync (m: Async<'a>) : Async<Result<'a, 'e>> = async {
        let! r = m
        return Ok r
    }
    let sequence (xs: Async<Result<'a, 'e>> list) : Async<Result<'a list, 'e>> = async {
        let! results = xs |> Async.Parallel |> Async.map Array.toList
        return results |> List.traverseResultA id
    }
```

---

### 5.3 错误处理流水线示例

```fsharp
// 对比：Python 中的 try/except 链
// try:
//     response = await provider.chat(messages, tools)
//     if response.finish_reason == "error":
//         break
//     for call in response.tool_calls:
//         result = await registry.execute(call.name, call.arguments)
// except Exception as e:
//     return f"Error: {e}"

// F# 中的铁路型等价：
let handleMessage (input: InboundMessage) : Async<Result<string, AgentError>> =
    asyncResult {
        // 每个步骤失败时，流水线直接短路到 Error 轨道
        let! session  = loadSession (sessionId input) |> AsyncResult.mapError StorageFailure
        let! request  = buildPrompt session input     |> Result.mapError ParseFailure |> Async.retn
        let! response = callLlm request               |> AsyncResult.mapError LlmFailure
        let! final    = processResponse response session request deps

        do! persistSession final |> AsyncResult.mapError StorageFailure
        return fst final
    }

// 渠道层错误处理：将 AgentError 转换为用户友好消息
let formatError (err: AgentError) : string =
    match err with
    | ParseFailure (JsonParseError (msg, _)) -> $"解析错误：{msg}"
    | LlmFailure   RateLimitError ->            "请求过快，请稍后重试"
    | LlmFailure   ContextLengthExceeded ->     "对话过长，请使用 /new 开始新会话"
    | ToolFailure  (WorkspaceViolation p) ->    $"工具尝试访问受限路径：{p}"
    | MaxIterationsReached n ->                 $"已达最大迭代次数（{n}），停止处理"
    | _ ->                                      "内部错误，请稍后重试"
    // 编译器会警告任何未处理的 AgentError 分支
```

---

## 6. 架构：六边形分层

```
┌─────────────────────────────────────────────────────────────┐
│                        Domain 层                            │
│  Types.fs  StateMachine.fs  Errors.fs                       │
│  ─ 纯 F# 类型和函数，无 IO，无外部依赖                      │
│  ─ 所有 DU、record、不变量约束在此定义                      │
└─────────────────────┬───────────────────────────────────────┘
                      │ 依赖方向（单向向上）
┌─────────────────────▼───────────────────────────────────────┐
│                     Application 层                          │
│  AgentLoop.fs  SessionActor.fs  ContextBuilder.fs           │
│  ─ 用例编排：组合 Domain 类型 + Infrastructure 接口          │
│  ─ AgentCoordinator 路由消息到对应 MailboxProcessor          │
│  ─ 通过 AgentDependencies record 注入所有 IO                 │
└─────────────────────┬───────────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────────┐
│                   Infrastructure 层                         │
│                                                             │
│  Providers/         Channels/         Storage/              │
│  ─ LiteLlmAdapter   ─ TelegramChannel  ─ JsonlStore         │
│  ─ AnthropicAdapter ─ DiscordChannel   ─ SessionRepository  │
│                     ─ SlackChannel                          │
│                                                             │
│  Mcp/               Config/           Tools/                │
│  ─ McpClient        ─ ConfigParser     ─ FileSystemTool      │
│  ─ McpParser.fs     ─ ConfigLoader     ─ ShellTool           │
│    (FParsec)                           ─ WebTool             │
│                                        ─ McpToolWrapper      │
│                                                             │
│  ─ 所有 FParsec 解析器集中在 *Parser.fs 文件                 │
│  ─ 所有对外 IO 通过 AgentDependencies 的函数字段暴露         │
└─────────────────────────────────────────────────────────────┘
```

### 依赖规则

- Domain 层：不依赖任何外部库（仅 FSharp.Core）
- Application 层：依赖 Domain；通过接口（函数类型）依赖 Infrastructure
- Infrastructure 层：依赖 Domain + Application；实现具体 IO

### 端口（Ports）与适配器（Adapters）

```fsharp
// Application 层定义的端口（函数类型即接口）
type LlmPort    = LlmRequest -> Async<Result<LLMResponse, LlmError>>
type StoragePort = {
    Load    : SessionId -> Async<Result<SessionSnapshot, StorageError>>
    Persist : SessionSnapshot -> Async<Result<unit, StorageError>>
}
type ToolPort   = ToolName -> Map<string, JsonValue> -> Async<ToolResult>
type ChannelPort = {
    Send    : OutboundMessage -> Async<Result<unit, ChannelError>>
    Receive : unit -> IAsyncEnumerable<InboundMessage>
}

// Infrastructure 层提供具体实现（适配器）
let anthropicAdapter (apiKey: ApiKey) : LlmPort =
    fun request -> async {
        // 调用 Anthropic SDK，将响应经 LlmResponseParser 转换为 LLMResponse
        ...
    }

let jsonlStorageAdapter (workspacePath: string) : StoragePort = {
    Load    = fun sessionId -> async { ... }
    Persist = fun snapshot  -> async { ... }
}
```

---

## 7. 并发模型

### Python → F# 并发原语对照

| Python 概念 | F# 等价 | 说明 |
|---|---|---|
| `asyncio.Queue` | `MailboxProcessor<T>` | 消息驱动，内置并发安全 |
| `_processing_lock` | MailboxProcessor 的串行保证 | Actor 天然串行处理消息 |
| `asyncio.create_task` | `Async.Start` 或 `Task.Run` | 启动后台任务 |
| `asyncio.Task.cancel` | `CancellationTokenSource.Cancel()` | 结构化取消 |
| `asyncio.gather` | `Async.Parallel` | 并行执行 |
| `WeakValueDictionary` | `ConcurrentDictionary + 引用计数` | 会话锁管理 |

### 消息总线

```fsharp
// Infrastructure/MessageBus.fs

type MessageBus(coordinator: AgentCoordinator) =
    // 入站队列：各 Channel 适配器写入
    let inbound = System.Threading.Channels.Channel.CreateUnbounded<InboundMessage>()

    // 出站队列：Session Actor 写入，Channel 适配器读取
    let outbound = System.Threading.Channels.Channel.CreateUnbounded<OutboundMessage>()

    member _.PublishInbound  (msg: InboundMessage)  = inbound.Writer.WriteAsync(msg).AsTask()
    member _.PublishOutbound (msg: OutboundMessage) = outbound.Writer.WriteAsync(msg).AsTask()

    /// 消费入站消息，路由到对应 Session Actor
    member _.StartProcessing () = async {
        for msg in inbound.Reader.ReadAllAsync() do
            let! result = coordinator.Route msg
            let response =
                match result with
                | Ok content -> content
                | Error err  -> formatError err
            do! outbound.Writer.WriteAsync(
                    { Channel = msg.Channel; Chat = msg.Chat
                      Content = response; ReplyTo = None
                      Attachments = []; IsProgress = false }
                ).AsTask() |> Async.AwaitTask
    }

    /// 消费出站消息，路由到对应 Channel 适配器
    member _.StartDispatching (channels: Map<ChannelId, ChannelPort>) = async {
        for msg in outbound.Reader.ReadAllAsync() do
            match channels |> Map.tryFind msg.Channel with
            | Some ch -> do! ch.Send msg |> Async.Ignore
            | None    -> eprintfn $"No channel adapter for {msg.Channel}"
    }
```

### CancellationToken 管理

```fsharp
// /stop 命令：取消当前 Session Actor 的处理
type SessionActorState = {
    CurrentCts : System.Threading.CancellationTokenSource option
}

// Session Actor 内部
| CancelCurrent ->
    state.CurrentCts |> Option.iter (fun cts -> cts.Cancel(); cts.Dispose())
    return! loop { state with CurrentCts = None } session
```

---

## 8. 模块文件结构

```
Nanobot.FSharp/
├── Domain/
│   ├── Types.fs           ← 所有 newtype、record、DU（§2）
│   ├── StateMachine.fs    ← AgentState 转换函数（§4.2）
│   └── Errors.fs          ← 所有错误 DU（§5.1）
│
├── Application/
│   ├── AgentLoop.fs       ← runAgentLoop、iterate（§4.4）
│   ├── SessionActor.fs    ← MailboxProcessor、协调器（§4.3）
│   ├── ContextBuilder.fs  ← 组装 LlmRequest（prompt）
│   └── MemoryConsolidator.fs  ← 记忆固化逻辑
│
├── Infrastructure/
│   ├── Shared/
│   │   ├── AsyncResult.fs ← 计算表达式（§5.2）
│   │   └── Json.fs        ← JsonElement 辅助函数
│   │
│   ├── Config/
│   │   ├── ConfigParser.fs    ← FParsec: JSON → NanobotConfig
│   │   └── ConfigLoader.fs    ← 文件 IO
│   │
│   ├── Input/
│   │   └── InputParser.fs     ← FParsec: string → UserInput（§3.1）
│   │
│   ├── Providers/
│   │   ├── SseParser.fs          ← FParsec: SSE 行 → SseFrame（§3.1）
│   │   ├── LlmResponseParser.fs  ← JSON 解码器: JSON → LLMResponse / StreamEvent（§3.3）
│   │   ├── AnthropicAdapter.fs
│   │   ├── OpenAICompatAdapter.fs
│   │   └── ProviderRegistry.fs   ← ProviderSpec 注册表 + 工厂函数
│   │
│   ├── Mcp/
│   │   ├── McpParser.fs       ← JSON 解码器: JSON-RPC → McpRequest/Response（§3.4）
│   │   └── McpClient.fs       ← stdio/HTTP 连接
│   │
│   ├── Channels/
│   │   ├── ChannelBase.fs
│   │   ├── TelegramChannel.fs
│   │   ├── DiscordChannel.fs
│   │   ├── SlackChannel.fs
│   │   └── WsEventParser.fs   ← JSON 解码器: WebSocket body → InboundWsEvent（§2.11）
│   │
│   ├── Tools/
│   │   ├── ToolParser.fs      ← JSON 解码器: JSON → 工具参数类型
│   │   ├── FileSystemTool.fs
│   │   ├── ShellTool.fs
│   │   ├── WebTool.fs
│   │   └── McpToolWrapper.fs
│   │
│   ├── Storage/
│   │   ├── SessionParser.fs   ← JSON 解码器: JSONL → SessionSnapshot（§3.5）
│   │   └── JsonlStore.fs      ← 文件 IO
│   │
│   └── MessageBus.fs          ← 入站/出站队列（§7）
│
├── Program.fs                 ← 组装依赖，启动服务
└── Nanobot.FSharp.fsproj
```

### 关键设计约束

1. **FParsec 解析器只出现在 `*Parser.fs` 文件，且仅用于原始文本**（用户输入、SSE 帧、Cron 表达式）
2. **JSON 解码器（`Result`-returning over `JsonElement`）用于所有 JSON 载荷**，不使用 FParsec 解析 JSON
3. **Domain 层不引用 Infrastructure 的任何命名空间**
4. **所有 `MailboxProcessor` 在 `SessionActor.fs` 中创建**，其他模块通过 `AgentCoordinator` 使用
5. **`AsyncResult` 计算表达式贯穿 Application 和 Infrastructure 层**，消除 `try/catch`
6. **工具执行结果 `ToolResult` 是 DU**，不使用异常传递失败信息
7. **Provider 实现为 `LLMProvider` record 值**，不继承抽象类

---

## 9. 类型检查工具

> 问题：有没有可以随时对 F# 类型进行检查的工具？

F# 的类型检查工具分三个层次：**实时反馈**、**命令行检查**、**CI 集成**。

### 9.1 实时反馈（编辑器）

| 工具 | 方式 | 特点 |
|---|---|---|
| **Ionide**（VS Code 扩展） | Language Server Protocol | 悬停显示推断类型、内联错误、自动补全 DU 分支 |
| **JetBrains Rider** | 内置 F# 支持 | 最完整的 F# IDE 体验，类型提示精度高 |
| **Visual Studio 2022** | 内置 F# 语言服务 | Windows 首选，类型窗口 + 立即窗口 |

**Ionide 特别说明**：模式匹配不完整时，Ionide 会在 match 表达式处显示黄色警告，
直接对应本文档"无 catch-all 分支"的设计目标。新增 DU 分支后，所有需处理的 match 处立即标红。

### 9.2 命令行即时检查

**方式一：F# Interactive（最快）**

```bash
# 启动 REPL，直接粘贴类型定义，立即得到类型反馈
dotnet fsi

# 或者检查 .fsx 脚本文件（无需项目文件）
dotnet fsi --exec Domain/Types.fsx
```

F# Interactive 会即时报告类型错误，适合在设计文档阶段验证单个类型定义。

**方式二：仅类型检查，不执行**

```bash
# 检查整个项目，不运行；--no-restore 跳过包还原
dotnet build --no-restore -v minimal

# 只检查类型，不生成二进制（更快）
dotnet build -p:CopyLocalLockFileAssemblies=false --no-restore
```

**方式三：.fsx 脚本逐文件验证**

Domain 层类型不依赖外部库，可写成单独 `.fsx` 脚本，随时验证：

```bash
# 验证 Types.fsx（包含所有 §2 的类型定义）
dotnet fsi --nologo --check Types.fsx
```

`--check` 模式：只做类型检查，不执行任何代码，秒级响应。

### 9.3 属性测试：验证类型不变量（FsCheck）

光有类型还不够，智能构造器的逻辑约束也需验证：

```fsharp
// Tests/Domain/SessionSnapshotTests.fs
open FsCheck
open FsCheck.Xunit

[<Property>]
let ``SessionSnapshot 不允许 lastConsolidated 超过消息数量`` (messages: Message list) (excess: PositiveInt) =
    let lastConsolidated = List.length messages + excess.Get
    let result = SessionSnapshot.create (SessionId "test") messages lastConsolidated DateTimeOffset.UtcNow DateTimeOffset.UtcNow
    result = Error (SchemaError ("session", ...))  // 应当失败

[<Property>]
let ``append 后消息数量增加 1`` (snapshot: SessionSnapshot) (msg: Message) =
    let next = SessionSnapshot.append msg snapshot
    List.length (SessionSnapshot.messages next) = List.length (SessionSnapshot.messages snapshot) + 1
```

运行：

```bash
dotnet test --filter "FullyQualifiedName~SessionSnapshot"
```

### 9.4 编译期检查的快捷工作流

推荐在设计文档的类型定义阶段使用以下流程：

```
┌─────────────────────────────────────────────────────┐
│ 1. 在 Domain/Types.fsx 中写新的 DU / record 定义    │
│                                                     │
│ 2. dotnet fsi --check Domain/Types.fsx              │
│    → 秒级反馈，无需项目文件                          │
│                                                     │
│ 3. 在 Domain/StateMachine.fsx 中写状态转换函数      │
│    → Ionide 实时标出未覆盖的模式匹配分支             │
│                                                     │
│ 4. dotnet build（全项目检查）                        │
│    → 确认跨模块类型一致性                            │
│                                                     │
│ 5. dotnet test（FsCheck 验证不变量）                 │
│    → 100 次随机输入验证智能构造器约束                │
└─────────────────────────────────────────────────────┘
```

### 9.5 推荐工具组合

| 场景 | 工具 |
|---|---|
| 设计新类型时 | `dotnet fsi --check Types.fsx` + Ionide 实时提示 |
| 写状态转换函数 | Ionide 的"不完整模式匹配"警告 |
| 验证智能构造器约束 | FsCheck property tests |
| 提交前全量检查 | `dotnet build && dotnet test` |
| CI/CD | `dotnet build -warnaserror`（将警告升级为错误，含不完整模式匹配）|

`-warnaserror` 在 CI 中尤其重要：将 FS0025（不完整模式匹配）升级为编译错误，
确保新增 DU 分支后所有 match 处必须更新，机器层面强制本文档的"穷举"设计目标。

```xml
<!-- Nanobot.FSharp.fsproj -->
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <WarningsAsErrors>FS0025</WarningsAsErrors>  <!-- 不完整模式匹配 -->
</PropertyGroup>
```

---

## 附录：与 FP_DDD_Research.md 的对应关系

| 研究文档概念 | 本设计文档的具体体现 |
|---|---|
| Make illegal states inexpressible | §2 的所有类型设计，尤其是 SessionSnapshot 的智能构造器 |
| Parser combinators at boundaries | §3 的 10 个入口点（FParsec + JSON 解码器分工明确）|
| MailboxProcessor per session | §4 的 SessionActor 实现 |
| Railway-oriented error handling | §5 的 AgentError DU + AsyncResult |
| Hexagonal architecture | §6 的三层结构 + AgentDependencies |
| F# as primary language | 全文使用 F# 语法，无 Python 痕迹 |
| SSE 流式响应 | §2.11 的 StreamDelta / StreamEvent DU + SseParser（FParsec）|
| Provider 抽象 | §2.12 的 record-of-functions + ProviderCapability + LlmErrorKind |
