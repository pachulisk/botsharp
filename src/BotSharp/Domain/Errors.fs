module BotSharp.Domain.Errors

open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Top-level AgentError — used by the Application layer coordinator
//
// Why a separate file: AgentError aggregates errors from all bounded contexts.
// Keeping it separate from Types.fs prevents Types.fs from becoming a catch-all.
// ═══════════════════════════════════════════════════════════════════════════

type AgentError =
    | AgentParseFailure   of ParseError
    | AgentLlmFailure     of LlmError
    | AgentToolFailure    of ToolError
    | AgentChannelFailure of ChannelError
    | AgentStorageFailure of StorageError
    | MaxIterationsReached of count: int
    | SessionActorStopped

/// Format an AgentError as a user-facing message (no internal details leaked)
let formatError (err: AgentError) : string =
    match err with
    | AgentParseFailure (JsonParseError (msg, _)) -> $"解析错误：{msg}"
    | AgentParseFailure (SchemaError (field, msg)) -> $"格式错误（{field}）：{msg}"
    | AgentParseFailure (UnknownField name)        -> $"未知字段：{name}"
    | AgentParseFailure (MissingField name)        -> $"缺少必填字段：{name}"
    | AgentLlmFailure { Kind = RateLimited _ }    -> "请求过快，请稍后重试"
    | AgentLlmFailure { Kind = QuotaExceeded }    -> "API 额度不足，请检查账户余额"
    | AgentLlmFailure { Kind = ContextTooLong }   -> "对话过长，请使用 /new 开始新会话"
    | AgentLlmFailure { Kind = ModelNotFound m }  -> $"模型 {m} 不存在"
    | AgentLlmFailure { Kind = Timeout _ }        -> "请求超时，请重试"
    | AgentLlmFailure { Kind = ServerError code } -> $"服务器错误（HTTP {code}），请稍后重试"
    | AgentLlmFailure { Kind = ConnectionFailed _ } -> "网络连接失败，请检查网络"
    | AgentLlmFailure { Kind = MalformedResponse _ } -> "AI 响应格式异常，请重试"
    | AgentLlmFailure { Kind = EmptyResponse hint } ->
        $"AI 返回空响应，可能是 base_url、模型名或 API Key 格式不匹配。{hint}"
    | AgentToolFailure (WorkspaceViolation path)  -> $"工具尝试访问受限路径：{path}"
    | AgentToolFailure (ToolNotFound (ToolName n)) -> $"工具不存在：{n}"
    | AgentToolFailure (ExecutionTimeout _)       -> "工具执行超时"
    | AgentToolFailure (ExecutionFailed msg)      -> $"工具执行失败：{msg}"
    | AgentToolFailure _                          -> "工具调用失败"
    | AgentChannelFailure NotAuthenticated        -> "渠道认证失败"
    | AgentChannelFailure (MessageTooLong _)      -> "消息过长，无法发送"
    | AgentChannelFailure _                       -> "渠道通信错误"
    | AgentStorageFailure (FileNotFound path)     -> $"会话文件不存在：{path}"
    | AgentStorageFailure _                       -> "会话存储错误"
    | MaxIterationsReached n                      -> $"已达最大迭代次数（{n}），已停止处理"
    | SessionActorStopped                         -> "会话已停止"
