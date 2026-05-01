module BotSharp.Domain.Types

open System
open System.IO
open System.Text.Json

// ═══════════════════════════════════════════════════════════════════════════
// § 1  Primitive newtypes
// Why: SessionId "telegram:123" and ChatId "telegram:123" are different types;
//      the compiler rejects mix-ups at function call sites.
// ═══════════════════════════════════════════════════════════════════════════

type SessionId  = SessionId  of string
type ChannelId  = ChannelId  of string
type ChatId     = ChatId     of string
type UserId     = UserId     of string
type ToolName   = ToolName   of string
type ToolCallId = ToolCallId of string
type TaskId     = TaskId     of string

// ═══════════════════════════════════════════════════════════════════════════
// § 1a  LocalFilePath — invariant: absolute, non-empty path
//
// MediaContent stores downloaded file paths.  Using raw string allows
// "" or relative paths which cause silent failures at tool call time.
// LocalFilePath enforces at construction that the path is non-empty
// and absolute (rooted), pushing the error to the boundary where the
// file is created rather than where it is consumed.
// ═══════════════════════════════════════════════════════════════════════════

type LocalFilePath = private LocalFilePath of string

module LocalFilePath =
    let create (path: string) =
        if String.IsNullOrWhiteSpace path then Error "File path cannot be empty"
        elif not (Path.IsPathRooted path)  then Error "File path must be absolute"
        else Ok (LocalFilePath path)

    /// Unsafe: use only when the path is known-good (e.g. system temp directory construction).
    let ofAbsolute (path: string) : LocalFilePath = LocalFilePath path

    let value (LocalFilePath p) = p

// ═══════════════════════════════════════════════════════════════════════════
// § 2  Media content
// Why: Python uses list[str] (file paths). No way to distinguish image vs audio
//      without peeking at the extension. F# encodes the distinction at construction.
// ═══════════════════════════════════════════════════════════════════════════

type MediaContent =
    | ImageFile    of path: LocalFilePath
    | AudioFile    of path: LocalFilePath
    | DocumentFile of path: LocalFilePath
    | VideoFile    of path: LocalFilePath

// ═══════════════════════════════════════════════════════════════════════════
// § 3  User input
// Why: Python does `if content.startswith("/new")` everywhere.
//      A missing branch is a runtime surprise; here it's a compile warning.
// ═══════════════════════════════════════════════════════════════════════════

type SlashCommand =
    | NewSession
    | ClearHistory
    | StopProcessing
    | ShowHelp
    | Restart
    | ShowStatus
    | ShowHistory   of count: int option
    | SwitchModel   of modelName: string option   // None = list available; Some = switch to model
    | ListSessions  of page: int option           // /sessions [page]
    | SearchSessions of query: string             // /search <keyword>
    | RebuildIndex                                // /rebuild-index
    | Dream
    | DreamLog     of sha: string option
    | DreamRestore of sha: string option

type UserInput =
    | Command     of SlashCommand
    | ChatMessage of content: string * media: MediaContent list

/// A persisted memory consolidation entry (written to {workspace}/dreams.jsonl).
type DreamEntry = {
    Sha          : string
    OccurredAt   : DateTimeOffset
    Summary      : string
    MessageCount : int
}

// ═══════════════════════════════════════════════════════════════════════════
// § 4a  Non-empty list (phantom-type constraint for tool call lists)
//
// WithToolCalls (_, []) is a representable but nonsensical state.
// NonEmptyList<ToolCall> makes the empty case impossible to construct.
// ═══════════════════════════════════════════════════════════════════════════

/// A list guaranteed at the type level to have at least one element.
type NonEmptyList<'T> = {
    Head : 'T
    Tail : 'T list
}

module NonEmptyList =
    let create head tail = { Head = head; Tail = tail }
    let singleton head   = { Head = head; Tail = [] }
    let toList nel       = nel.Head :: nel.Tail
    let length nel       = 1 + List.length nel.Tail
    let map f nel        = { Head = f nel.Head; Tail = List.map f nel.Tail }
    let head nel         = nel.Head

    /// Find the first element matching pred, or None.
    let tryFind (pred: 'T -> bool) (nel: NonEmptyList<'T>) : 'T option =
        if pred nel.Head then Some nel.Head
        else List.tryFind pred nel.Tail

    /// Convert a list — returns Error if empty.
    let ofList = function
        | []      -> Error "List must not be empty"
        | h :: t  -> Ok { Head = h; Tail = t }

    /// Convert a list, throwing if empty (use only in code paths guaranteed non-empty by invariant).
    let ofListUnsafe (lst: 'T list) : NonEmptyList<'T> =
        match lst with
        | []     -> invalidOp "NonEmptyList.ofListUnsafe called with empty list"
        | h :: t -> { Head = h; Tail = t }

// ═══════════════════════════════════════════════════════════════════════════
// § 4  Tool schema (JSON Schema as an ADT)
// ═══════════════════════════════════════════════════════════════════════════

type JsonSchemaType =
    | JsString
    | JsNumber
    | JsBoolean
    | JsArray   of items: JsonSchemaType
    | JsObject  of properties: Map<string, JsonSchemaProperty>
    | JsEnum    of values: string list
    | JsAny

and JsonSchemaProperty = {
    Type        : JsonSchemaType
    Description : string
    Required    : bool
}

type ToolSpec = {
    Name            : ToolName
    Description     : string
    Parameters      : Map<string, JsonSchemaProperty>
    /// Whether this tool can run concurrently with other concurrent-safe tools.
    /// Read-only tools (web_fetch, read_file, glob, grep) set this to true.
    /// Stateful / side-effectful tools (shell, write_file, message, spawn, cron) set this to false.
    ConcurrencySafe : bool
}

// ═══════════════════════════════════════════════════════════════════════════
// § 5  Tool call (from LLM response)
// ═══════════════════════════════════════════════════════════════════════════

type ToolCall = {
    Id           : ToolCallId
    Tool         : ToolName
    Arguments    : Map<string, JsonElement>   // decoded by JSON decoder, not raw string
    ProviderMeta : Map<string, JsonElement> option
}

// ═══════════════════════════════════════════════════════════════════════════
// § 6  Conversation message
// ═══════════════════════════════════════════════════════════════════════════

type Message =
    | SystemMessage     of content: string                              // sent as role:system; NOT persisted in session history
    | UserMessage       of content: string * media: MediaContent list
    | AssistantMessage  of content: string * reasoningContent: string option
    | ToolCallMessage   of calls: NonEmptyList<ToolCall> * reasoningContent: string option
    | ToolResultMessage of id: ToolCallId * name: ToolName * content: string

// ═══════════════════════════════════════════════════════════════════════════
// § 7  LLM response (non-streaming, complete)
// Why: Python LLMResponse has content: str|None AND tool_calls: list.
//      Both can be non-empty simultaneously — a contradictory state.
//      F# DU makes the cases mutually exclusive by construction.
// ═══════════════════════════════════════════════════════════════════════════

/// An Anthropic-style extended thinking block.
/// The `type` wire field is always "thinking" and carries no information
/// beyond the type name itself — it is omitted here.
type ThinkingBlock = {
    Thinking  : string
    Signature : string option
}

type TokenUsage = {
    PromptTokens     : int
    CompletionTokens : int
    CachedTokens     : int
}

/// Pure helpers for displaying token usage.
/// Python parity: nanobot.utils.helpers.build_status_content token section.
module TokenUsage =
    /// Format as "N in / M out (K% cached)" — Python parity for /status output.
    let formatUsage (u: TokenUsage) : string =
        let cacheStr =
            if u.CachedTokens > 0 && u.PromptTokens > 0 then
                let pct = int (float u.CachedTokens / float u.PromptTokens * 100.0)
                sprintf " (%d%% cached)" pct
            else ""
        sprintf "%d in / %d out%s" u.PromptTokens u.CompletionTokens cacheStr

type LLMResponseBody =
    | TextOnly      of content: string
    | WithToolCalls of content: string option * calls: NonEmptyList<ToolCall>
    | Empty

/// The reason the LLM stopped generating tokens.
/// Mirrors OpenAI finish_reason field; also used by other providers.
type FinishReason =
    | Stop          // natural end of generation
    | Length        // max_tokens reached — output may be truncated
    | ToolCalls     // model requested tool calls
    | ContentFilter // filtered by the provider
    | OtherReason   of reason: string

type LLMResponse = {
    Body             : LLMResponseBody
    ReasoningContent : string option
    ThinkingBlocks   : ThinkingBlock list
    Usage            : TokenUsage
    FinishReason     : FinishReason option  // None = not provided or unknown
}

// ═══════════════════════════════════════════════════════════════════════════
// § 8  Error types
// Why: Python has 7 optional error fields on LLMResponse (error_status_code,
//      error_kind, etc.) — all present even on success.
//      F# separates errors into typed DUs; success carries no error baggage.
// ═══════════════════════════════════════════════════════════════════════════

type ParseError =
    | JsonParseError of message: string * position: int
    | SchemaError    of field: string * message: string
    | UnknownField   of name: string
    | MissingField   of name: string

type TimeoutKind = StreamIdleTimeout | RequestTimeout

type LlmErrorKind =
    | RateLimited        of retryAfter: TimeSpan option   // HTTP 429, retryable
    | QuotaExceeded                                        // billing limit, not retryable
    | ServerError        of statusCode: int                // 5xx
    | Timeout            of kind: TimeoutKind
    | ConnectionFailed   of reason: string
    | ModelNotFound      of model: string
    | ContextTooLong
    | MalformedResponse  of parseError: ParseError
    | EmptyResponse      of hint: string                   // stream returned HTTP 200 but no tokens

type LlmError = {
    Kind         : LlmErrorKind
    RawMessage   : string
    ProviderCode : string option
    // ShouldRetry is intentionally absent: it is always derivable from Kind.
    // Storing it as a field would allow contradictory states like
    // { Kind = RateLimited _; ShouldRetry = false }.
    // Use LlmError.shouldRetry instead.
}

module LlmError =
    /// Whether this error warrants a retry.  Derived from Kind, never stored.
    let shouldRetry (err: LlmError) : bool =
        match err.Kind with
        | RateLimited _       -> true   // HTTP 429: back off and retry
        | ServerError _       -> true   // 5xx: transient server fault
        | Timeout _           -> true   // idle stream / request timeout
        | MalformedResponse _ -> false  // 400: request is wrong; retry won't help
        | ConnectionFailed _  -> false  // auth failure or unexpected status
        | ModelNotFound _     -> false  // 404: model doesn't exist
        | ContextTooLong      -> false  // 413: must truncate context first
        | QuotaExceeded       -> false  // billing limit exhausted
        | EmptyResponse _     -> false  // misconfigured endpoint; retry won't help

type ToolError =
    | ToolNotFound       of name: ToolName
    | ParameterMissing   of field: string
    | ParameterInvalid   of field: string * reason: string
    | ExecutionFailed    of message: string
    | ExecutionTimeout   of after: TimeSpan
    | WorkspaceViolation of path: string

type ToolResult =
    | ToolSuccess of content: string
    | ToolFailure of error: ToolError

type StorageError =
    | FileNotFound  of path: string
    | ParseFailure  of ParseError
    | WriteFailure  of reason: string

type ChannelError =
    | NotAuthenticated
    | MessageTooLong  of length: int * maxLength: int
    | ChannelRateLimited of retryAfter: TimeSpan
    | ChannelClosed   of id: ChannelId

// ═══════════════════════════════════════════════════════════════════════════
// § 9  Generation settings (frozen value type)
// ═══════════════════════════════════════════════════════════════════════════

type ReasoningEffort = Low | Medium | High | Adaptive

[<Struct>]
type GenerationSettings = {
    Temperature     : float
    MaxTokens       : int
    ReasoningEffort : ReasoningEffort option
}

module GenerationSettings =
    let defaults = { Temperature = 0.7; MaxTokens = 4096; ReasoningEffort = None }

// ═══════════════════════════════════════════════════════════════════════════
// § 10  LLM request (prompt sent to provider)
// ═══════════════════════════════════════════════════════════════════════════

type LLMRequest = {
    Messages  : Message list
    Tools     : ToolSpec list
    Model     : string
    Settings  : GenerationSettings
}

// ═══════════════════════════════════════════════════════════════════════════
// § 11  Channel message bus events
// ═══════════════════════════════════════════════════════════════════════════

type InboundMessage = {
    Channel             : ChannelId
    Sender              : UserId
    Chat                : ChatId
    Input               : UserInput
    Metadata            : Map<string, string>
    SessionKeyOverride  : SessionId option
}

/// An opaque reference to a prior message, used for reply-to threading.
/// Channel-agnostic: the channel adapter knows how to interpret the value.
type MessageRef = MessageRef of string

module MessageRef =
    let create s = MessageRef s
    let value (MessageRef s) = s

type OutboundMessage = {
    Channel     : ChannelId
    Chat        : ChatId
    Content     : string
    ReplyTo     : MessageRef option
    Attachments : MediaContent list
    /// Inline keyboard buttons: list of rows, each row is a list of button labels.
    /// Empty list means no buttons. Illegal: empty rows; enforced at parse boundary.
    Buttons     : string list list
    // IsProgress removed: since AgentResult DU, StreamedResponse never reaches
    // port.Send.  Every OutboundMessage is a complete, displayable reply.
}

/// Result of a single Receive call on a ChannelPort.
///
/// Message      — a new inbound message is available.
/// ChannelClosed — the channel is permanently gone (EOF, webhook deregistered).
///                 The loop must stop.
/// NoMessage    — this poll cycle had nothing; channel is still live.
///                 The loop should wait and poll again.
///
/// Using a DU instead of InboundMessage option makes the three cases
/// structurally distinct: ChannelClosed and NoMessage cannot be confused,
/// and a consumer that receives Message cannot ignore its payload.
type ReceiveResult =
    | Message      of InboundMessage
    | ChannelClosed
    | NoMessage

/// Derive session ID from inbound message (single source of truth)
let sessionId (msg: InboundMessage) : SessionId =
    match msg.SessionKeyOverride with
    | Some id -> id
    | None ->
        let (ChannelId ch) = msg.Channel
        let (ChatId   ci) = msg.Chat
        SessionId $"{ch}:{ci}"

// ═══════════════════════════════════════════════════════════════════════════
// § 12  Session snapshot (immutable, invariant-guarded)
// Invariant: 0 ≤ lastConsolidated ≤ messages.Length
// ═══════════════════════════════════════════════════════════════════════════

type SessionSnapshot = private {
    Id_               : SessionId
    Messages_         : Message list
    LastConsolidated_ : int
    CreatedAt_        : DateTimeOffset
    UpdatedAt_        : DateTimeOffset
}

module SessionSnapshot =
    /// Only creation path — enforces the invariant at construction time.
    let create (id: SessionId) (messages: Message list) (lastConsolidated: int)
               (createdAt: DateTimeOffset) (updatedAt: DateTimeOffset)
               : Result<SessionSnapshot, string> =
        if lastConsolidated < 0 then
            Error $"lastConsolidated must be ≥ 0, got {lastConsolidated}"
        elif lastConsolidated > List.length messages then
            Error $"lastConsolidated ({lastConsolidated}) exceeds message count ({List.length messages})"
        else
            Ok { Id_ = id; Messages_ = messages; LastConsolidated_ = lastConsolidated
                 CreatedAt_ = createdAt; UpdatedAt_ = updatedAt }

    /// Create a brand-new empty session (invariant trivially satisfied)
    let empty (id: SessionId) (now: DateTimeOffset) : SessionSnapshot =
        { Id_ = id; Messages_ = []; LastConsolidated_ = 0; CreatedAt_ = now; UpdatedAt_ = now }

    // ── Read-only accessors ──────────────────────────────────────────────
    let id               s = s.Id_
    let messages         s = s.Messages_
    let lastConsolidated s = s.LastConsolidated_
    let createdAt        s = s.CreatedAt_
    let updatedAt        s = s.UpdatedAt_
    let unconsolidated   s = s.Messages_ |> List.skip s.LastConsolidated_
    let messageCount     s = List.length s.Messages_

    /// Append a message; returns a new snapshot (immutable)
    let append (msg: Message) (s: SessionSnapshot) : SessionSnapshot =
        { s with Messages_ = s.Messages_ @ [msg]; UpdatedAt_ = DateTimeOffset.UtcNow }

    /// Advance the consolidation pointer (cannot go backwards)
    let advanceConsolidated (newIndex: int) (s: SessionSnapshot) : Result<SessionSnapshot, string> =
        if newIndex < s.LastConsolidated_ then
            Error "Cannot move lastConsolidated backwards"
        elif newIndex > List.length s.Messages_ then
            Error $"newIndex ({newIndex}) exceeds message count ({List.length s.Messages_})"
        else
            Ok { s with LastConsolidated_ = newIndex; UpdatedAt_ = DateTimeOffset.UtcNow }

    /// Clear all messages (used by /new command)
    let clear (s: SessionSnapshot) : SessionSnapshot =
        { s with Messages_ = []; LastConsolidated_ = 0; UpdatedAt_ = DateTimeOffset.UtcNow }

// ═══════════════════════════════════════════════════════════════════════════
// § 13  Agent state machine types
// ═══════════════════════════════════════════════════════════════════════════

type AgentState =
    | Idle
    | BuildingPrompt  of history: Message list
    | AwaitingLLM     of request: LLMRequest * iteration: int
    | ExecutingTools  of calls: NonEmptyList<ToolCall> * pendingMessages: Message list * iteration: int
    | Consolidating   of session: SessionSnapshot
    | Finalizing      of response: string * reasoningContent: string option

type AgentEvent =
    | MessageReceived       of InboundMessage
    | PromptBuilt           of LLMRequest
    | LlmRespondedWithText  of content: string * reasoningContent: string option
    | LlmRespondedWithTools of calls: NonEmptyList<ToolCall> * reasoningContent: string option
    | ToolsExecuted         of results: (ToolCall * ToolResult) list
    | ResponseSent

// ═══════════════════════════════════════════════════════════════════════════
// § 14  Streaming types (SSE / provider-level)
// ═══════════════════════════════════════════════════════════════════════════

type StreamDelta =
    | TextDelta     of content: string
    | ThinkingDelta of content: string      // DeepSeek-R1 / extended thinking
    | ToolArgDelta  of index: int * chunk: string

type StreamEvent =
    | ContentDelta      of delta: StreamDelta
    | ToolCallStarted   of index: int * id: ToolCallId * name: ToolName
    | ToolCallCompleted of call: ToolCall
    | StreamCompleted   of finalResponse: LLMResponse
    | StreamError       of error: LlmError
    | StreamFinished    of reason: string   // emitted when stop chunk arrives with finish_reason

/// Represents a single SSE line's parsed form (before JSON decoding)
type SseFrame =
    | DataLine    of data: string
    | DoneLine
    | CommentLine

/// Contract between the agent loop and downstream output layers (CLI, WebSocket, etc.)
///
/// NoStreaming     — caller wants a single PlainResponse; no delta callbacks.
/// StreamingHook   — caller wants token-by-token deltas via onDelta; onStreamEnd is
///                   called when the LLM finishes a segment (arg: true = tool calls follow).
///
/// Using a DU instead of { WantsStreaming: bool; OnDelta; OnStreamEnd } makes the
/// "streaming off" path structurally carry no callback fields, so there is no way to
/// accidentally call OnDelta when WantsStreaming = false.
type AgentStreamHook =
    | NoStreaming
    | StreamingHook of onDelta     : (string -> Async<unit>)
                     * onStreamEnd : (bool   -> Async<unit>)

// ═══════════════════════════════════════════════════════════════════════════
// § 15  WebSocket events (WebUI multiplexed channel)
// ═══════════════════════════════════════════════════════════════════════════

type WsChatId = WsChatId of string

type WsMessageKind = ToolHint | Progress

type InboundWsEvent =
    | WsReady    of chatId: WsChatId * clientId: string
    | WsAttached of chatId: WsChatId
    | WsDelta    of chatId: WsChatId * text: string
    | WsStreamEnd of chatId: WsChatId
    | WsMessage  of chatId: WsChatId * text: string * kind: WsMessageKind option
    | WsError    of detail: string option

type OutboundWsEvent =
    | WsNewChat
    | WsAttach of chatId: WsChatId
    | WsSend   of chatId: WsChatId * content: string * media: MediaContent list

// ═══════════════════════════════════════════════════════════════════════════
// § 16  Provider capability and configuration
// ═══════════════════════════════════════════════════════════════════════════

type ProviderCapability =
    | PromptCaching
    | ExtendedThinking
    | FunctionCalling
    | VisionInput
    | ResponsesApi
    | Streaming
    | StreamUsageTracking  // OpenAI stream_options.include_usage=true; omit for providers that don't support it

type ThinkingStyle =
    | ThinkingType         // Anthropic: { type: "enabled", budget_tokens }
    | EnableThinking       // some OpenAI-compat: enable_thinking=true
    | ReasoningSplit       // DeepSeek-R1: separate reasoning_content field
    | ReasoningEffortParam // OpenAI o-series: reasoning_effort="medium"

// ── Retry policy ────────────────────────────────────────────────────────────

type RetryMode =
    | FixedRetries of maxAttempts: int * delays: TimeSpan list
    | Persistent   of maxDelayPerAttempt: TimeSpan

type RetryPolicy = {
    Mode                    : RetryMode
    ImageFallback           : bool
    CircuitBreakerThreshold : int
}

module RetryPolicy =
    let standard =
        { Mode = FixedRetries (3, [ TimeSpan.FromSeconds 1.
                                    TimeSpan.FromSeconds 2.
                                    TimeSpan.FromSeconds 4. ])
          ImageFallback = true
          CircuitBreakerThreshold = 10 }

    let persistent =
        { Mode = Persistent (TimeSpan.FromSeconds 60.)
          ImageFallback = true
          CircuitBreakerThreshold = 10 }

// ── LLMProvider as record-of-functions (no inheritance) ─────────────────────

type LLMProvider = {
    Id           : string
    DefaultModel : string
    Capabilities : Set<ProviderCapability>
    RetryPolicy  : RetryPolicy

    /// Non-streaming: returns complete response or error
    Chat : GenerationSettings -> Message list -> ToolSpec list -> Async<Result<LLMResponse, LlmError>>

    /// Streaming: pushes StreamEvents to emitter, returns Ok() or LlmError
    ChatStream : GenerationSettings -> Message list -> ToolSpec list -> (StreamEvent -> Async<unit>) -> Async<Result<unit, LlmError>>
}

// ── Provider registry metadata ───────────────────────────────────────────────

type ProviderBackend =
    | OpenAICompatBackend
    | AnthropicBackend
    | AzureOpenAIBackend
    | GitHubCopilotBackend
    | LocalBackend of host: string

type ProviderSpec = {
    Id           : string
    Keywords     : string list
    Backend      : ProviderBackend
    IsGateway    : bool
    Capabilities : Set<ProviderCapability>
    ThinkingStyle : ThinkingStyle option
    EnvKeyName   : string
}

// ── API key (non-empty invariant enforced at construction) ───────────────────

type ApiKey = private ApiKey of string

module ApiKey =
    let create (raw: string) =
        if String.IsNullOrWhiteSpace raw then Error "API key cannot be empty"
        else Ok (ApiKey raw)
    let value (ApiKey k) = k
    let tryFromEnv (varName: string) =
        match Environment.GetEnvironmentVariable varName with
        | null | "" -> None
        | v -> Some (ApiKey v)

// ═══════════════════════════════════════════════════════════════════════════
// § 17  MCP (Model Context Protocol) types
// ═══════════════════════════════════════════════════════════════════════════

type McpClientInfo = {
    Name    : string
    Version : string
}

type McpCapabilities = {
    Tools    : bool
    Prompts  : bool
    Resources : bool
}

type McpServerConfig =
    | StdioServer      of command: string * args: string list * env: Map<string, string>
    | HttpServer       of url: Uri * headers: Map<string, string>
    | UnixSocketServer of socketPath: string

/// Per-MCP-server entry: transport config plus shared metadata.
/// `ToolTimeout` — per-call timeout in seconds (Python: tool_timeout; default 30).
/// `EnabledTools` — which server tools to register: ["*"] = all, [] = none, else exact original names.
type McpServerEntry = {
    Connection   : McpServerConfig
    ToolTimeout  : int
    EnabledTools : string list
}

type McpRequest =
    | McpInitialize  of id: string * clientInfo: McpClientInfo
    | McpListTools   of id: string
    | McpCallTool    of id: string * name: ToolName * arguments: Map<string, JsonElement>
    | McpPing        of id: string

type McpResponse =
    | McpInitializeOk of id: string * capabilities: McpCapabilities
    | McpToolList     of id: string * tools: ToolSpec list
    | McpToolResult   of id: string * content: string * isError: bool
    | McpErrorResp    of id: string * code: int * message: string

// ═══════════════════════════════════════════════════════════════════════════
// § 18  Config types
// ═══════════════════════════════════════════════════════════════════════════

/// Parsed allow-list for inbound channel senders.
///
/// AnyoneAllowed — every sender is permitted (no restriction).
/// AllowedSet    — only the listed user IDs are permitted.
///
/// Parsed at config-load time by AllowList.parse, so no channel adapter
/// ever sees or validates the raw "*" magic string at runtime.
type AllowList =
    | AnyoneAllowed
    | AllowedSet of permitted: Set<string>

module AllowList =
    /// Parse a raw string list into an AllowList once, at config-load time.
    let parse (raw: string list) : AllowList =
        if raw |> List.contains "*" then AnyoneAllowed
        else AllowedSet (Set.ofList raw)

    /// Check whether a sender is permitted by this allow-list.
    let permits (UserId uid) (list: AllowList) : bool =
        match list with
        | AnyoneAllowed                -> true
        | AllowedSet permitted         -> Set.contains uid permitted

// ═══════════════════════════════════════════════════════════════════════════
// § 18a  Telegram channel configuration types
// ═══════════════════════════════════════════════════════════════════════════

/// Controls whether the bot responds to all messages in a group, or only when mentioned/replied to.
type GroupPolicy =
    | OpenPolicy    // respond to every message
    | MentionPolicy // respond only when @mentioned or replied to by the bot

/// Telegram Bot API token with format validation: <bot_id>:<secret>
/// e.g. "123456789:ABCDEFGHijklmnopqrstuvwxyz-abc123"
type TelegramBotToken = private TelegramBotToken of string

module TelegramBotToken =
    /// Basic structural validation: must be "<digits>:<non-empty-string>".
    /// Full format validation (base64url secret) is done by parseTelegramBotToken in InputParser.
    let create (raw: string) : Result<TelegramBotToken, string> =
        if String.IsNullOrWhiteSpace raw then Error "Bot token cannot be empty"
        else
            let colon = raw.IndexOf(':')
            if colon <= 0 then Error "Bot token must contain ':' after numeric bot ID"
            elif not (raw.[..colon-1] |> Seq.forall Char.IsDigit) then
                Error "Bot token must start with numeric bot ID"
            elif colon = raw.Length - 1 then Error "Bot token secret part cannot be empty"
            else Ok (TelegramBotToken raw)

    let value (TelegramBotToken t) = t

type TelegramConfig = {
    Token              : TelegramBotToken  // validated bot token format
    AllowFrom          : AllowList         // user-level allow list for Telegram
    Proxy              : Uri option        // HTTP proxy for bot API calls
    ReplyToMessage     : bool              // whether to set reply_to_message_id
    ReactEmoji         : string option     // emoji to react with after response completes (e.g. "👍")
    GroupPolicy        : GroupPolicy       // how to handle group chats
    ConnectionPoolSize : int               // validated > 0 at parse time
    PoolTimeout        : TimeSpan          // was: float seconds
    Streaming          : bool              // stream tokens via EditMessageText
    InlineKeyboards    : bool              // render action buttons (reserved, not yet used)
    StreamEditInterval : TimeSpan          // was: float seconds; rate-limit between edits
}

type WsConfig = {
    Port    : int            // TCP port to listen on (default 8765)
    Token   : ApiKey option  // Static auth token; None = no authentication required
    Enabled : bool           // Must be true to start the server
}

type BotSharpConfig = {
    DefaultModel     : string
    DefaultProvider  : string
    Temperature      : float
    MaxTokens        : int
    WorkspacePath    : string
    ApiKeys          : Map<string, ApiKey>     // provider id → key
    BaseUrls         : Map<string, string>     // provider id → base URL (overrides registry defaults)
    McpServers       : Map<string, McpServerEntry>
    AllowFrom        : AllowList               // parsed at config load; no "*" magic strings at runtime
    BraveApiKey      : ApiKey option
    MemoryWindowSize   : int                     // message count before consolidation
    MaxIterations      : int
    SubagentMaxIterations : int                    // max iterations for spawn subagents (default 15; Python: agents.defaults.max_iterations for subagent)
    MaxMessages        : int                       // max messages loaded from session history (0 = unlimited; Python: agents.defaults.max_messages)
    MaxToolResultChars   : int                     // tool result content cap (chars); 0 = unlimited
    ReasoningEffort      : ReasoningEffort option  // None = use model default (no explicit effort)
    Telegram             : TelegramConfig option   // None = Telegram disabled
    Ws                   : WsConfig option          // None = WebSocket server disabled
    ContextWindowTokens  : int                     // estimated context window size in tokens; 0 = no trimming
    ContextBlockLimit    : int option              // override computed token budget (None = compute from ContextWindowTokens)
    MaxIterationsMessage : string option           // custom message when max_iterations is hit; None = default template
    FailOnToolError      : bool                    // halt on first tool failure instead of returning error text to LLM
    DisabledSkills       : string list             // skill names to exclude from loading (e.g. ["summarize"; "skill-creator"])
    SessionTtlMinutes    : int                     // idle minutes before auto-compact (0 = disabled)
    SessionCleanupDays   : int                     // delete idle session files older than N days (0 = disabled; Python: session_cleanup)
    EnableSqliteIndex    : bool                    // enable SQLite derived index (default true)
    SqliteRebuildOnError : bool                    // auto-rebuild SQLite if open fails (default true)
    Timezone             : string option           // IANA timezone for runtime context time display (None = system local)
    ExecTimeoutSeconds   : int                     // default timeout for shell exec tool (seconds; 0 = use tool default of 60)
    ExecAllowedEnvKeys   : string list             // env var allowlist for shell exec; [] = pass all (Python: exec.allowed_env_keys)
    ExecSandbox          : string                  // sandbox backend for shell exec: "" (none) or "bwrap" (Linux only; Python: exec.sandbox)
    HeartbeatEnabled        : bool                // enable periodic heartbeat checks (Python: heartbeat.enabled)
    HeartbeatIntervalSeconds : int                // seconds between heartbeat ticks (Python: heartbeat.interval_s; default 1800)
    HeartbeatKeepRecentMessages : int             // messages to retain in heartbeat session after each run (Python: heartbeat.keep_recent_messages; default 8)
    DreamModelOverride  : string option           // model for memory consolidation (Python: dream.model_override; None = use DefaultModel)
    DreamMaxIterations  : int                     // max LLM iterations per consolidation run (Python: dream.max_iterations; default 15)
    DreamIntervalHours  : int                     // hours between automatic consolidation runs (Python: dream.interval_h; 0 = disabled)
    WebSearchProvider   : string option           // preferred search provider: "brave", "duckduckgo", "tavily", "searxng"; None = auto (Brave if key present, else DDG)
    RestrictToWorkspace : bool                   // restrict shell exec working_dir to workspace subtree (Python: tools.exec.restrict_to_workspace)
    ProviderRetryMode   : string                 // "standard" (fixed 3 retries) or "persistent" (infinite until circuit-breaker; Python: provider_retry_mode)
    UnifiedSession      : bool                   // share one session across all channels (Python: unified_session; single-user multi-device)
    WebProxyUrl         : string option          // HTTP/SOCKS5 proxy for web tools (Python: web.proxy); None = no proxy
    WebSearchTimeout    : int                    // wall-clock timeout (seconds) for web search operations (Python: web.search.timeout; default 30)
    WebSearchMaxResults : int                    // max search results returned per query (Python: web.search.max_results; default 5)
    DreamMaxBatchSize   : int                    // max history entries passed to consolidation per run (Python: dream.max_batch_size; default 20)
    ExecPathAppend      : string                 // colon-separated path segments appended to PATH for shell exec (Python: exec.path_append; "" = no change)
    SendToolHints  : bool   // print tool hints before each tool round (Python: channels.send_tool_hints; default false)
    SendProgress   : bool   // emit intermediate streaming text (Python: channels.send_progress; default true)
    SendMaxRetries : int    // max delivery retries for channel send (Python: channels.send_max_retries; default 3)
    MyToolAllowSet     : bool        // allow agent to write scratchpad via 'my set' (Python: my_tool.allow_set; default false)
    SsrfWhitelist      : string list // CIDR ranges exempted from SSRF blocking in exec/web tools (Python: tools.ssrf_whitelist; default [])
    FileReadMaxChars   : int         // max chars returned by read_file before truncation (Python: tools.file_read_max_chars; default 131072)
    SystemPromptAppend : string option // extra text appended to the system prompt (Python: system_prompt_append; None = no append)
    WebSearchApiKey    : string      // API key for Tavily search (Python: tools.web.search.api_key; "" = use TAVILY_API_KEY env var)
    WebSearchBaseUrl   : string      // Base URL for SearXNG (Python: tools.web.search.base_url; "" = use SEARXNG_BASE_URL env var)
    ExecEnable             : bool        // register the shell exec tool (Python: tools.exec.enable; default true)
    WebEnable              : bool        // register web search + fetch tools (Python: tools.web.enable; default true)
    MyToolEnable           : bool        // register the agent self-inspection tool (Python: tools.my.enable; default true)
    TranscriptionProvider  : string      // voice transcription backend: "groq" or "openai" (Python: channels.transcription_provider; default "groq")
    TranscriptionLanguage  : string option // ISO-639-1 language hint for audio transcription (Python: channels.transcription_language; None = auto-detect)
    DreamAnnotateLineAges  : bool                      // annotate MEMORY.md lines with git-blame age in consolidation prompt (Python: dream.annotate_line_ages; default true)
    ProviderExtraHeaders   : Map<string, Map<string, string>> // per-provider custom HTTP headers (Python: providers.<id>.extra_headers; default {})
    ApiPort                : int option                // start OpenAI-compatible API server on this port from config file (Python: api.port; None = CLI flag only)
    ApiTimeoutSeconds      : int                       // per-request timeout for API server (Python: api.timeout; default 120)
    ApiHost                : string                    // listen address for API server (Python: api.host; default "localhost")
    Discord                : DiscordChannelConfig option      // None = Discord disabled
    Slack                  : SlackChannelConfig option        // None = Slack disabled
    Feishu                 : FeishuChannelConfig option       // None = Feishu/Lark disabled
    DingTalk               : DingTalkChannelConfig option     // None = DingTalk disabled
    Email                  : EmailChannelConfig option        // None = Email disabled
    Telnet                 : TelnetChannelConfig option       // None = Telnet disabled
    Matrix                 : MatrixChannelConfig option       // None = Matrix disabled
    QQ                     : QQChannelConfig option           // None = QQ disabled
    WhatsApp               : WhatsAppChannelConfig option     // None = WhatsApp disabled
    MoChat                 : MoChatChannelConfig option       // None = MoChat disabled
    InterAgent             : InterAgentChannelConfig option  // None = inter-agent channel disabled
    FallbackModels         : string list                   // ordered fallback model names when primary fails (e.g. ["deepseek-v4-pro"; "gpt-4o"])
}

and DiscordChannelConfig = {
    Token     : string
    AllowFrom : AllowList
}

and SlackChannelConfig = {
    BotToken      : string
    AppToken      : string
    AllowFrom     : AllowList
    ReplyInThread : bool
}

and FeishuChannelConfig = {
    AppId              : string
    AppSecret          : string
    VerificationToken  : string
    AllowFrom          : AllowList
    WebhookPort        : int
}

and DingTalkChannelConfig = {
    ClientId     : string
    ClientSecret : string
    AllowFrom    : AllowList
    WebhookPort  : int
}

and WhatsAppChannelConfig = {
    PhoneNumberId : string
    AccessToken   : string
    VerifyToken   : string
    WebhookPort   : int
    AllowFrom     : AllowList
}

and MoChatChannelConfig = {
    BaseUrl      : string
    ClawToken    : string
    PollSeconds  : int
    AllowFrom    : AllowList
}

and QQChannelConfig = {
    AppId     : string
    Secret    : string
    AllowFrom : AllowList
    Sandbox   : bool
}

and MatrixChannelConfig = {
    Homeserver  : string
    UserId      : string
    AccessToken : string
    AllowFrom   : AllowList
}

and TelnetChannelConfig = {
    Port      : int
    AllowFrom : AllowList
}

and EmailChannelConfig = {
    ImapHost    : string
    ImapPort    : int
    ImapUseSsl  : bool
    SmtpHost    : string
    SmtpPort    : int
    SmtpUseTls  : bool
    Username    : string
    Password    : string
    PollSeconds : int
    AllowFrom   : AllowList
}

and InterAgentChannelConfig = {
    Enabled             : bool
    Port                : int
    InstanceName        : string
    AuditWebhookUrl     : string option
    MaxRoundsPerSession : int
    TaskTtlSeconds      : int
}

module BotSharpConfig =
    let defaults = {
        DefaultModel       = "gpt-4o-mini"
        DefaultProvider    = "openai"
        Temperature        = 0.7
        MaxTokens          = 4096
        WorkspacePath      = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botsharp", "workspace")
        ApiKeys            = Map.empty
        BaseUrls           = Map.empty
        McpServers         = Map.empty
        AllowFrom          = AnyoneAllowed       // open by default; restrict in config
        BraveApiKey        = None
        MemoryWindowSize   = 50
        MaxIterations      = 40
        SubagentMaxIterations = 15
        MaxMessages        = 0    // 0 = no limit (load all session messages)
        MaxToolResultChars   = 16_000              // matches Python nanobot default
        ContextWindowTokens  = 0                   // 0 = no trimming; set e.g. 65536 for GPT-4o
        ContextBlockLimit    = None                // None = derive from ContextWindowTokens
        MaxIterationsMessage = None               // None = default "(stopped after N iterations)"
        FailOnToolError      = false              // false = return error text to LLM (Python default)
        DisabledSkills       = []                 // empty = load all skills
        SessionTtlMinutes    = 0                  // 0 = auto-compact disabled
        SessionCleanupDays   = 0                  // 0 = session cleanup disabled
        EnableSqliteIndex    = true               // SQLite index enabled by default
        SqliteRebuildOnError = true               // auto-rebuild on open failure
        Timezone             = None               // None = system local timezone
        ExecTimeoutSeconds   = 0                  // 0 = use tool default (60 s)
        ExecAllowedEnvKeys   = []                 // [] = pass all env vars through (no restriction)
        ExecSandbox          = ""                 // "" = no sandbox (Python: exec.sandbox default)
        HeartbeatEnabled           = true         // true = heartbeat service starts (Python default)
        HeartbeatIntervalSeconds   = 1800         // 30 minutes (Python heartbeat.interval_s default)
        HeartbeatKeepRecentMessages = 8           // messages retained after each run (Python heartbeat.keep_recent_messages default)
        DreamModelOverride  = None                // None = use DefaultModel for consolidation
        DreamMaxIterations  = 15                  // Python dream.max_iterations default
        DreamIntervalHours  = 0                   // 0 = no automatic dream scheduling
        WebSearchProvider   = None                // None = auto-detect (Brave if key present, else DDG)
        RestrictToWorkspace = false               // false = allow any working_dir (Python default)
        ProviderRetryMode   = "standard"          // "standard" = fixed retries; "persistent" = retry until circuit-breaker
        UnifiedSession      = false               // false = separate sessions per channel (Python default)
        WebProxyUrl         = None                // None = no proxy for web tools (Python: web.proxy)
        WebSearchTimeout    = 30                  // 30 seconds (Python: web.search.timeout default)
        WebSearchMaxResults = 5                   // 5 results per search query (Python: web.search.max_results default)
        DreamMaxBatchSize   = 20                  // 20 history entries per consolidation run (Python: dream.max_batch_size default)
        ExecPathAppend      = ""                  // "" = no PATH modification (Python: exec.path_append default)
        SendToolHints  = false                    // false = tool hints suppressed (Python: channels.send_tool_hints default)
        SendProgress   = true                     // true  = stream intermediate text (Python: channels.send_progress default)
        SendMaxRetries = 3                        // 3 delivery retries per outbound message (Python: channels.send_max_retries default)
        MyToolAllowSet     = false                // false = 'my set' is read-only (Python: my_tool.allow_set default)
        SsrfWhitelist      = []                   // [] = no CIDR exemptions from SSRF blocking (Python: tools.ssrf_whitelist default)
        FileReadMaxChars   = 131_072              // 128 K chars — matches Python nanobot default (Python: tools.file_read_max_chars)
        SystemPromptAppend = None                 // None = no extra text appended to system prompt (Python: system_prompt_append)
        WebSearchApiKey    = ""                   // "" = fall back to TAVILY_API_KEY env var (Python: tools.web.search.api_key)
        WebSearchBaseUrl   = ""                   // "" = fall back to SEARXNG_BASE_URL env var (Python: tools.web.search.base_url)
        ExecEnable             = true             // true = shell exec tool is registered (Python: tools.exec.enable)
        WebEnable              = true             // true = web search + fetch tools are registered (Python: tools.web.enable)
        MyToolEnable           = true             // true = agent self-inspection 'my' tool is registered (Python: tools.my.enable)
        TranscriptionProvider  = "groq"           // "groq" = default voice transcription backend (Python: channels.transcription_provider)
        TranscriptionLanguage  = None             // None = auto-detect language (Python: channels.transcription_language)
        DreamAnnotateLineAges  = true             // true = annotate MEMORY.md lines with git-blame age (Python: dream.annotate_line_ages)
        ProviderExtraHeaders   = Map.empty        // {} = no custom headers per provider (Python: providers.<id>.extra_headers)
        ApiPort                = None             // None = API server started only via --api-port CLI flag (Python: api.port)
        ApiTimeoutSeconds      = 120              // 120 s per-request timeout for API server (Python: api.timeout)
        ApiHost                = "localhost"      // local-only bind by default (Python: api.host defaults to "127.0.0.1")
        ReasoningEffort    = None
        Telegram           = None
        Ws                 = None
        Discord            = None
        Slack              = None
        Feishu             = None
        DingTalk           = None
        Email              = None
        Telnet             = None
        Matrix             = None
        QQ                 = None
        WhatsApp           = None
        MoChat             = None
        InterAgent         = None
        FallbackModels     = []
    }

// ═══════════════════════════════════════════════════════════════════════════
// § 19  Auxiliary subsystem types (Consolidation, Skills, Cron, Heartbeat)
// ═══════════════════════════════════════════════════════════════════════════

type ConsolidationResult =
    | Consolidated     of historyEntry: string * memoryUpdate: string option * newLastIndex: int
    | ConsolidationSkipped

type SkillActivation = AlwaysActive | OnDemand

type Skill = {
    Name        : string
    Description : string
    Content     : string
    Activation  : SkillActivation
}

type CronSchedule =
    | EveryN    of minutes: int
    | Daily     of hour: int * minute: int
    | Weekly    of dayOfWeek: DayOfWeek * hour: int * minute: int
    | CronExpr  of raw: string
    | Once      of at: DateTimeOffset   // fires once at the specified UTC time

type CronStatus = Active | Paused | Completed

type CronJob = {
    Id             : TaskId
    Label          : string
    Task           : string
    Schedule       : CronSchedule
    Timezone       : string option        // IANA timezone for Daily/Weekly schedules (e.g. "America/New_York")
    Channel        : ChannelId
    Chat           : ChatId
    Status         : CronStatus
    CreatedAt      : DateTimeOffset
    LastRun        : DateTimeOffset option
    NextRun        : DateTimeOffset option
    DeleteAfterRun : bool
}

type HeartbeatDecision =
    | RunHeartbeat  of tasks: string list
    | SkipHeartbeat

// ═══════════════════════════════════════════════════════════════════════════
// § 22  AgentHook — lifecycle callbacks for agent loop iterations
//
// Mirrors Python's AgentHook class (nanobot/agent/hook.py).
//
// Design (record-of-functions, not class hierarchy):
//   • AgentHookContext holds per-iteration mutable state set by the loop.
//   • AgentHook is an immutable record of optional callbacks; the defaults
//     in AgentHook.none are all no-ops, so partial implementations are trivial.
//   • AgentHook.compose fan-outs across a list of hooks with error isolation
//     on async callbacks (matching Python's CompositeHook).
//   • FinalizeContent is a pipeline: each hook in a composite may transform
//     the reply; a hook that returns None suppresses the reply.
//
// Comparison to AgentStreamHook:
//   AgentStreamHook is a structural DU — you either want streaming or not,
//   and the callback fields are only present in the streaming case.
//   AgentHook is a richer extensibility surface covering the full iteration
//   lifecycle; it always exists (defaulting to no-ops) rather than being
//   optionally absent.
// ═══════════════════════════════════════════════════════════════════════════

/// Mutable per-iteration state exposed to AgentHook callbacks.
/// Created fresh at the start of each AwaitingLLM iteration.
type AgentHookContext = {
    /// Zero-based iteration index within this agent turn.
    Iteration         : int
    /// Full conversation history at the start of this iteration.
    Messages          : Message list
    /// Set after the LLM responds.
    mutable Response  : LLMResponse option
    /// Set when the LLM requests tool calls.
    mutable ToolCalls : ToolCall list
    /// Set after all tool calls have been executed.
    mutable ToolResults : (ToolCall * ToolResult) list
    /// Set when the iteration produces a final text reply.
    mutable FinalContent : string option
    /// Set when the iteration fails with an error.
    mutable Error     : string option
}

/// Lifecycle hook record for the agent loop.
/// All fields default to no-ops via AgentHook.none.
type AgentHook = {
    /// True if any hook requires streaming text deltas.
    WantsStreaming      : bool
    /// Called at the start of each iteration, before the LLM request.
    BeforeIteration     : AgentHookContext -> Async<unit>
    /// Called with each streaming text delta (when WantsStreaming = true).
    OnStream            : AgentHookContext -> string -> Async<unit>
    /// Called when streaming finishes. resuming = true when tool calls follow.
    OnStreamEnd         : AgentHookContext -> bool -> Async<unit>
    /// Called immediately before tool calls are dispatched.
    BeforeExecuteTools  : AgentHookContext -> Async<unit>
    /// Called at the end of each iteration, after tools have been executed.
    AfterIteration      : AgentHookContext -> Async<unit>
    /// Pipeline to transform or suppress the final assistant reply.
    FinalizeContent     : AgentHookContext -> string option -> string option
}

module AgentHook =
    /// No-op hook — all callbacks are identity/unit.
    let none : AgentHook = {
        WantsStreaming     = false
        BeforeIteration    = fun _ -> async.Return ()
        OnStream           = fun _ _ -> async.Return ()
        OnStreamEnd        = fun _ _ -> async.Return ()
        BeforeExecuteTools = fun _ -> async.Return ()
        AfterIteration     = fun _ -> async.Return ()
        FinalizeContent    = fun _ content -> content
    }

    /// Create a fresh context for the given iteration and message list.
    let mkContext (iteration: int) (messages: Message list) : AgentHookContext = {
        Iteration    = iteration
        Messages     = messages
        Response     = None
        ToolCalls    = []
        ToolResults  = []
        FinalContent = None
        Error        = None
    }

    /// Invoke an async hook callback, swallowing any exception so that a faulty
    /// hook does not crash the agent loop. Mirrors Python's CompositeHook
    /// try/except pattern: each hook is isolated from the others.
    let private tryInvoke (f: unit -> Async<unit>) : Async<unit> =
        async {
            try do! f ()
            with _ -> ()   // suppress — bad hook must not crash the agent
        }

    /// Fan-out across multiple hooks with error isolation.
    /// Async callbacks are called sequentially; an exception in one hook is
    /// swallowed so the remaining hooks still run.
    /// FinalizeContent is a pipeline (no error isolation) — each hook transforms
    /// the result of the previous, so a None input/output stays None.
    /// Mirrors Python's CompositeHook fan-out + error-isolation semantics.
    let compose (hooks: AgentHook list) : AgentHook =
        match hooks with
        | []    -> none
        | [one] -> one   // fast path: no wrapping needed
        | _ ->
            { WantsStreaming     = hooks |> List.exists (fun h -> h.WantsStreaming)
              BeforeIteration    = fun ctx -> async { for h in hooks do do! tryInvoke (fun () -> h.BeforeIteration ctx) }
              OnStream           = fun ctx d -> async { for h in hooks do do! tryInvoke (fun () -> h.OnStream ctx d) }
              OnStreamEnd        = fun ctx r -> async { for h in hooks do do! tryInvoke (fun () -> h.OnStreamEnd ctx r) }
              BeforeExecuteTools = fun ctx -> async { for h in hooks do do! tryInvoke (fun () -> h.BeforeExecuteTools ctx) }
              AfterIteration     = fun ctx -> async { for h in hooks do do! tryInvoke (fun () -> h.AfterIteration ctx) }
              FinalizeContent    = fun ctx content ->
                  hooks |> List.fold (fun acc h -> h.FinalizeContent ctx acc) content }

// ── Path utilities ───────────────────────────────────────────────────────────

/// Expand ~ in paths
let expandPath (p: string) =
    if p.StartsWith("~") then
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.[2..])
    else p
