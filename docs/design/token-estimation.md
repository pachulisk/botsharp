# BotSharp Token 估算系统升级设计方案

> 借鉴 Codex 的基于字节的精确估算 + API 报告值混合追踪机制，替换 BotSharp 当前的字符启发式估算。

## 1. 问题分析

### 1.1 断裂的反馈回路

BotSharp 当前的 token 管理存在一个核心缺陷：**API 返回的真实 token 用量没有反馈到上下文管理决策中**。

```
当前数据流（断裂）：

API 响应 ──→ TokenUsage 解析 ──→ LastTokenUsage ref ──→ my tool 展示
                                                           ↑
                                                       反馈到此为止
                                                           ✕
                                               trimToContextWindow ←── estimateTokens（独立估算）
                                               needsConsolidation  ←── 消息计数（与 token 无关）
```

`LlmResponseParser.fs` 已正确解析 API 返回的 `prompt_tokens`、`completion_tokens`、`cached_tokens`，`SessionActor` 也通过 `LastTokenUsage` ref 存储了它们，但这些真实数据**从未参与**上下文裁剪或整合触发决策。`trimToContextWindow` 完全依赖独立的字符估算，`needsConsolidation` 完全依赖消息计数。

### 1.2 CJK 文本的严重低估 Bug

当前估算函数（`AgentLoop.fs:410`）：

```fsharp
let estimateTokens (text: string) : int = max 1 (text.Length / 4)
```

`text.Length` 返回的是 **UTF-16 code unit 计数**，不是字节数。对于不同语言的影响：

| 文本 | UTF-16 Length | UTF-8 字节数 | 实际 token 数 | 当前估算 | 误差 |
|------|--------------|-------------|--------------|---------|------|
| `"hello world"` (11 chars) | 11 | 11 | ~3 | 2 | 约 -33% |
| `"你好世界"` (4 chars) | 4 | 12 | ~4 | 1 | **-75%** |
| `"こんにちは"` (5 chars) | 5 | 15 | ~5 | 1 | **-80%** |
| 1000 字中文 | 1000 | 3000 | ~700 | 250 | **-64%** |

Codex 的 Rust 实现（`truncate.rs:71-74`）：

```rust
pub fn approx_token_count(text: &str) -> usize {
    let len = text.len();  // Rust str.len() = UTF-8 字节数
    len.saturating_add(APPROX_BYTES_PER_TOKEN.saturating_sub(1)) / APPROX_BYTES_PER_TOKEN
}
```

Rust 的 `str.len()` 返回 UTF-8 字节数，因此 `bytes / 4` 对 CJK 文本给出 `3/4` token/字符的估算——仍是近似值但误差在合理范围内。

**后果**：在中文对话中，`trimToContextWindow` 认为上下文远未装满而拒绝裁剪，直到实际 token 数远超上下文窗口，导致 API 报错。

### 1.3 与 Codex 的差距总结

| 维度 | BotSharp 当前 | Codex | 差距 |
|------|-------------|-------|------|
| 估算基础 | UTF-16 字符数 / 4 | UTF-8 字节数 / 4（向上取整） | CJK 低估 4-8x |
| API 报告值 | 解析但不使用 | 作为 token 追踪主数据源 | 完全缺失 |
| 累计追踪 | 无 | `TokenUsageInfo.total_token_usage` 累计 | 无法判断总使用量 |
| 混合估算 | 无 | API 报告值 + 新增 item 字节估算 | 无法预判下一轮 |
| 上下文窗口感知 | 仅裁剪时用 | 归一化百分比 + 自动压缩触发 | 无法触发整合 |
| 裁剪触发 | 仅字符估算 | API 真实值 + 估算值取较大 | 可能漏裁剪 |
| 整合触发 | 仅消息计数 | 消息计数 + token 阈值双重判断 | 可能整合过晚 |

## 2. 目标架构

### 2.1 闭合反馈回路

```
目标数据流（闭合）：

API 响应 ──→ TokenUsage 解析 ──→ TokenTracker.recordApiUsage()
                                       │
                                       ├──→ 累计 token 追踪（total_usage）
                                       ├──→ 上下文窗口剩余百分比
                                       │
                                       ▼
                            ┌─ trimToContextWindow ←── 混合估算
                            │     （API 值 + 新增 item 字节估算）
                            │
                            ├─ needsConsolidation ←── 消息计数 OR token 阈值
                            │     （双重触发条件）
                            │
                            └─ /status 输出 ←── 精确用量 + 剩余百分比
```

### 2.2 设计原则

1. **混合优先**：有 API 报告值时用真实值，没有时降级到字节估算
2. **UTF-8 字节基准**：所有估算基于 UTF-8 字节数，修复 CJK 低估
3. **累计追踪**：跨轮次累计 token 使用量，支持上下文窗口百分比计算
4. **Actor 内存态**：tracker 存活于 `SessionActor`，不持久化（首次 API 调用后即校准）
5. **向后兼容**：`ContextWindowTokens = 0` 时行为不变（不裁剪不追踪）

## 3. 详细设计

### 3.1 修复 `estimateTokens`：UTF-16 → UTF-8

**文件**：`AgentLoop.fs:410`

```fsharp
// ── 之前 ──
let estimateTokens (text: string) : int = max 1 (text.Length / 4)

// ── 之后 ──
/// 基于 UTF-8 字节数的 token 估算。
/// 比率：1 token ≈ 4 UTF-8 字节（与 Codex truncate.rs:4 的 APPROX_BYTES_PER_TOKEN 对齐）。
/// 使用向上取整：(bytes + 3) / 4（与 Codex approx_token_count 对齐）。
let estimateTokens (text: string) : int =
    let byteCount = System.Text.Encoding.UTF8.GetByteCount(text)
    max 1 ((byteCount + 3) / 4)
```

**影响范围**：`messageTokens`、`trimToContextWindow` 以及所有调用 `estimateTokens` 的地方自动受益，无需额外修改。

**修复效果**：

| 文本 | 之前 | 之后 | 实际 | 之前误差 | 之后误差 |
|------|------|------|------|---------|---------|
| `"hello world"` | 2 | 3 | ~3 | -33% | 0% |
| `"你好世界"` | 1 | 3 | ~4 | -75% | -25% |
| 1000 字中文 | 250 | 750 | ~700 | -64% | +7% |

### 3.2 新增 `TokenTracker` 类型

**文件**：`Types.fs`（在 `TokenUsage` 模块之后新增）

```fsharp
/// 跨轮次的 token 使用追踪器。
/// 借鉴 Codex 的 TokenUsageInfo（protocol.rs:2044-2050），但简化为 BotSharp 场景。
/// 存活于 SessionActor 内存中，不持久化。会话加载后首次 API 调用即校准真实值。
type TokenTracker = {
    /// 最近一次 API 调用返回的 token 用量（等同 Codex 的 last_token_usage）
    LastUsage         : TokenUsage option

    /// 所有 API 调用的累计 token（等同 Codex 的 total_token_usage）
    TotalUsage        : TokenUsage

    /// 模型上下文窗口大小（从 BotSharpConfig.ContextWindowTokens 获取）
    ContextWindow     : int

    /// 最近一次 API 响应后新增的本地 item 的估算 token 数
    /// （工具调用结果、用户消息等尚未被 API 计入的内容）
    /// 等同 Codex 的 items_after_last_model_generated_item 估算
    EstimatedPending  : int
}

module TokenTracker =

    /// 创建空追踪器
    let empty (contextWindow: int) : TokenTracker =
        { LastUsage        = None
          TotalUsage       = { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }
          ContextWindow    = contextWindow
          EstimatedPending = 0 }

    /// 记录一次 API 响应的 token 用量。
    /// 将 pending 估算归零（API 已计入所有已发送内容）。
    /// 等同 Codex 的 TokenUsageInfo.append_last_usage()（protocol.rs:2079-2082）。
    let recordApiUsage (usage: TokenUsage) (tracker: TokenTracker) : TokenTracker =
        { tracker with
            LastUsage = Some usage
            TotalUsage =
                { PromptTokens     = tracker.TotalUsage.PromptTokens + usage.PromptTokens
                  CompletionTokens = tracker.TotalUsage.CompletionTokens + usage.CompletionTokens
                  CachedTokens     = tracker.TotalUsage.CachedTokens + usage.CachedTokens }
            EstimatedPending = 0 }

    /// 记录本地新增内容的估算 token 数（API 尚未见到的内容）。
    /// 对应 Codex 的 items_after_last_model_generated_item 估算。
    let addPendingEstimate (tokens: int) (tracker: TokenTracker) : TokenTracker =
        { tracker with
            EstimatedPending = tracker.EstimatedPending + tokens }

    /// 获取当前最佳 token 使用估算。
    /// 混合策略：API 报告值 + 本地 pending 估算。
    /// 对应 Codex 的 ContextManager.get_total_token_usage()（history.rs:309-327）。
    let currentUsageEstimate (tracker: TokenTracker) : int =
        match tracker.LastUsage with
        | Some last ->
            // 有 API 数据：用 API 的 total_tokens + 本地 pending
            last.PromptTokens + last.CompletionTokens + tracker.EstimatedPending
        | None ->
            // 无 API 数据（首轮）：纯估算
            tracker.EstimatedPending

    /// 上下文窗口剩余百分比。
    /// 对应 Codex 的 percent_of_context_window_remaining()（protocol.rs:2192-2203）。
    /// BASELINE_TOKENS 对应系统提示词 + 工具定义的基准开销。
    let contextRemainingPercent (tracker: TokenTracker) : int =
        if tracker.ContextWindow <= 0 then 100
        else
            let baselineTokens = 8000   // 系统提示词 + 工具定义 + IDENTITY.md 等的估算开销
            let effectiveWindow = max 1 (tracker.ContextWindow - baselineTokens)
            let used = currentUsageEstimate tracker
            let remaining = max 0 (effectiveWindow - used)
            min 100 (remaining * 100 / effectiveWindow)

    /// 是否应触发自动整合（token 维度）。
    /// 对应 Codex 的 auto_compact_token_limit 检查（turn.rs:714-729）。
    let shouldCompactByTokens (tracker: TokenTracker) : bool =
        if tracker.ContextWindow <= 0 then false
        else
            // 当使用量达到上下文窗口的 80% 时触发
            let threshold = tracker.ContextWindow * 80 / 100
            currentUsageEstimate tracker >= threshold
```

### 3.3 修改 `trimToContextWindow`：混合估算

**文件**：`AgentLoop.fs:431`

当前的 `trimToContextWindow` 仅使用 `messageTokens`（字符估算）计算预算。升级后，它可以接受一个可选的 `trackerEstimate` 参数，在 API 报告值可用时使用更精确的数据做裁剪决策。

```fsharp
/// 裁剪消息以适应上下文窗口预算。
/// 保留系统消息；从最旧的非系统消息开始丢弃。
/// trackerEstimate：TokenTracker 的当前使用估算（可选），
///   当有值时用于辅助判断是否需要裁剪（解决字符估算可能低估的问题）。
let trimToContextWindow
    (contextWindowTokens: int)
    (maxTokens: int)
    (contextBlockLimit: int option)
    (trackerEstimate: int option)
    (messages: Message list)
    : Message list =

    let budget =
        match contextBlockLimit with
        | Some limit -> limit
        | None ->
            if contextWindowTokens <= 0 then 0
            else contextWindowTokens - maxTokens - _SNIP_BUFFER

    if budget <= 0 then messages
    else
        let localEst = messages |> List.sumBy messageTokens

        // 混合判断：取本地估算和 tracker 估算中的较大值。
        // 本地估算可能因 CJK 而偏低（已通过 UTF-8 修复大部分），
        // tracker 估算基于 API 真实值更可靠但可能不含最新 pending。
        let effectiveEst =
            match trackerEstimate with
            | Some te -> max localEst te
            | None    -> localEst

        if effectiveEst <= budget then messages
        else
            // ...裁剪逻辑不变...
```

**调用侧修改**（`AgentLoop.fs:847`）：

```fsharp
// ── 之前 ──
|> trimToContextWindow deps.Config.ContextWindowTokens deps.Config.MaxTokens deps.Config.ContextBlockLimit

// ── 之后 ──
|> trimToContextWindow
    deps.Config.ContextWindowTokens
    deps.Config.MaxTokens
    deps.Config.ContextBlockLimit
    (deps.TokenTracker.Value |> Option.map TokenTracker.currentUsageEstimate)
```

### 3.4 修改 `needsConsolidation`：双重触发

**文件**：`MemoryConsolidator.fs:166-169`

```fsharp
// ── 之前 ──
let needsConsolidation (snapshot: SessionSnapshot) (config: BotSharpConfig) : bool =
    let unconsolidatedCount = snapshot.messageCount - snapshot.lastConsolidated
    unconsolidatedCount >= config.MemoryWindowSize

// ── 之后 ──
/// 双重触发条件：消息计数 OR token 使用率。
/// 消息计数保持不变（原有逻辑）；
/// token 触发参考 Codex 的 auto_compact_token_limit（turn.rs:714-729）。
let needsConsolidation
    (snapshot: SessionSnapshot)
    (config: BotSharpConfig)
    (tracker: TokenTracker option)
    : bool =
    // 条件 1：消息计数（原有）
    let byCount =
        let unconsolidatedCount = snapshot.messageCount - snapshot.lastConsolidated
        unconsolidatedCount >= config.MemoryWindowSize
    // 条件 2：token 使用率（新增）
    let byTokens =
        match tracker with
        | Some t -> TokenTracker.shouldCompactByTokens t
        | None   -> false
    byCount || byTokens
```

**调用侧适配**：

- `SessionActor.ProcessInput`（整合判断）：传入 `deps'.TokenTracker.Value`（有值）
- `AutoCompactService`（后台整合）：传入 `None`（无 live actor，无 tracker）
- `forceConsolidate`（`/new` 命令）：传入 `None`（强制整合不需要 token 判断）

```fsharp
// SessionActor 中（有 tracker）：
if needsConsolidation snap deps'.Config deps'.TokenTracker.Value then ...

// AutoCompactService 中（无 tracker）：
if needsConsolidation snap config None then ...

// forceConsolidate 无需调用 needsConsolidation（直接整合）
```

### 3.5 扩展 `AgentDependencies` 和 `SessionActor`

#### AgentDependencies 新增字段

**文件**：`AgentLoop.fs:18-32`

```fsharp
type AgentDependencies = {
    // 现有字段...
    LastTokenUsage    : TokenUsage option ref

    // 新增
    TokenTracker      : TokenTracker option ref   // None = ContextWindowTokens=0，不追踪
}
```

#### SessionActor 初始化 tracker

**文件**：`SessionActor.fs:47-60`

```fsharp
// 每个 actor 创建独立的 tracker
let actorTracker =
    if config.ContextWindowTokens > 0 then
        ref (Some (TokenTracker.empty config.ContextWindowTokens))
    else
        ref None

let deps' = { deps with
    LastTokenUsage   = actorLastUsage
    TokenTracker     = actorTracker }
```

#### AgentLoop 中更新 tracker

**文件**：`AgentLoop.fs`，在 API 响应处理之后（约 line 954）：

```fsharp
// ── 之前 ──
deps.LastTokenUsage.Value <- Some response.Usage

// ── 之后 ──
deps.LastTokenUsage.Value <- Some response.Usage

// 更新 TokenTracker（如果启用）
match deps.TokenTracker.Value with
| Some tracker ->
    deps.TokenTracker.Value <-
        Some (TokenTracker.recordApiUsage response.Usage tracker)
| None -> ()
```

在用户消息和工具结果追加到上下文之后，更新 pending 估算：

```fsharp
// 在构建 LLM 请求之前，计算新增内容的估算 token 数
match deps.TokenTracker.Value with
| Some tracker ->
    let pendingTokens =
        newMessages |> List.sumBy messageTokens
    deps.TokenTracker.Value <-
        Some (TokenTracker.addPendingEstimate pendingTokens tracker)
| None -> ()
```

### 3.6 `/status` 输出增强

**文件**：`MyTool.fs`（`_last_usage` 相关区域）

```fsharp
// ── 之前 ──
$"_last_usage: {lastUsageStr}"

// ── 之后 ──
let trackerStr =
    match deps.TokenTracker.Value with
    | Some tracker ->
        let used = TokenTracker.currentUsageEstimate tracker
        let pct  = TokenTracker.contextRemainingPercent tracker
        let totalStr = TokenUsage.formatUsage tracker.TotalUsage
        sprintf " | context: %d/%d tokens (%d%% remaining) | session total: %s" used tracker.ContextWindow pct totalStr
    | None -> ""

$"_last_usage: {lastUsageStr}{trackerStr}"
```

输出示例：
```
_last_usage: 3847 in / 512 out (72% cached) | context: 24359/131072 tokens (81% remaining) | session total: 18240 in / 3680 out (65% cached)
```

## 4. `messageTokens` 增强：ToolCall 参数的字节估算

当前 `messageTokens` 中 `ToolCallMessage` 的估算逻辑使用 `k.Length + 10 + v.ToString().Length`，同样受 UTF-16 影响。修复为字节一致：

```fsharp
// ── 之前 ──
| ToolCallMessage (calls, _) ->
    calls |> NonEmptyList.toList
    |> List.sumBy (fun c ->
        let args = c.Arguments |> Map.toList
                   |> List.sumBy (fun (k,v) -> k.Length + 10 + v.ToString().Length)
        4 + args)

// ── 之后 ──
| ToolCallMessage (calls, rcOpt) ->
    let rcTokens = rcOpt |> Option.map estimateTokens |> Option.defaultValue 0
    let callTokens =
        calls |> NonEmptyList.toList
        |> List.sumBy (fun c ->
            let argsBytes =
                c.Arguments |> Map.toList
                |> List.sumBy (fun (k, v) ->
                    let kb = System.Text.Encoding.UTF8.GetByteCount(k)
                    let vb = System.Text.Encoding.UTF8.GetByteCount(v.ToString())
                    kb + 10 + vb)  // 10 = JSON 结构开销（引号、冒号、逗号）
            4 + argsBytes / 4)    // 4 token overhead per call + args 转 token
    rcTokens + callTokens
```

## 5. 工具输出裁剪的 Token 感知

### 5.1 当前问题

`MaxToolResultChars` 使用字符计数，对 CJK 同样低估。工具结果 16,000 字符的中文文本实际约 12,000 token，可能消耗大量上下文预算。

### 5.2 新增 `MaxToolResultTokens` 配置

**文件**：`Types.fs`

```fsharp
type BotSharpConfig = {
    // 现有
    MaxToolResultChars   : int      // 字符级上限（保留，向后兼容）

    // 新增
    MaxToolResultTokens  : int      // Token 级上限（0 = 不启用，使用 MaxToolResultChars）
}
```

**默认值**：`MaxToolResultTokens = 0`（不启用，保持现有行为）

### 5.3 裁剪函数增强

**文件**：`AgentLoop.fs`

```fsharp
/// 基于 token 预算的工具结果裁剪。
/// 保留首尾内容（head+tail），中间截断并标注截断量。
/// 借鉴 Codex 的 truncate_middle_with_token_budget（truncate.rs:15-36）。
let truncateByTokenBudget (maxTokens: int) (text: string) : string =
    if maxTokens <= 0 then text
    else
        let maxBytes = maxTokens * 4
        let textBytes = System.Text.Encoding.UTF8.GetByteCount(text)
        if textBytes <= maxBytes then text
        else
            // 保留首尾各 50%
            let headBytes = maxBytes / 2
            let tailBytes = maxBytes - headBytes
            let headChars = text |> Seq.scan (fun acc c ->
                acc + System.Text.Encoding.UTF8.GetByteCount(string c)) 0
                |> Seq.takeWhile (fun b -> b <= headBytes) |> Seq.length |> fun n -> max 0 (n - 1)
            let tailChars = text |> Seq.rev |> Seq.scan (fun acc c ->
                acc + System.Text.Encoding.UTF8.GetByteCount(string c)) 0
                |> Seq.takeWhile (fun b -> b <= tailBytes) |> Seq.length |> fun n -> max 0 (n - 1)
            let truncatedTokens = estimateTokens text - maxTokens
            let head = text.[..headChars - 1]
            let tail = text.[text.Length - tailChars..]
            sprintf "%s\n…%d tokens truncated…\n%s" head truncatedTokens tail

/// 对工具结果应用预算（优先 token 级，降级到字符级）
let applyToolResultBudget (config: BotSharpConfig) (messages: Message list) : Message list =
    messages |> List.map (function
        | ToolResultMessage (id, name, content) ->
            let trimmed =
                if config.MaxToolResultTokens > 0 then
                    truncateByTokenBudget config.MaxToolResultTokens content
                elif config.MaxToolResultChars > 0 then
                    truncateResult config.MaxToolResultChars content
                else content
            ToolResultMessage (id, name, trimmed)
        | other -> other)
```

## 6. 裁剪管线更新

**文件**：`AgentLoop.fs:840-849`

```fsharp
// ── 之前 ──
let trimmedMessages =
    req.Messages
    |> dropOrphanToolResults
    |> backfillMissingToolResults
    |> microcompact
    |> applyToolResultBudget deps.Config.MaxToolResultChars
    |> trimToContextWindow deps.Config.ContextWindowTokens deps.Config.MaxTokens deps.Config.ContextBlockLimit
    |> dropOrphanToolResults
    |> backfillMissingToolResults
    |> enforceRoleAlternation

// ── 之后 ──
let trackerEst =
    deps.TokenTracker.Value |> Option.map TokenTracker.currentUsageEstimate

let trimmedMessages =
    req.Messages
    |> dropOrphanToolResults
    |> backfillMissingToolResults
    |> microcompact
    |> applyToolResultBudget deps.Config
    |> trimToContextWindow
        deps.Config.ContextWindowTokens
        deps.Config.MaxTokens
        deps.Config.ContextBlockLimit
        trackerEst
    |> dropOrphanToolResults
    |> backfillMissingToolResults
    |> enforceRoleAlternation
```

## 7. 与 Codex 实现的对照表

| Codex 组件 | Codex 位置 | BotSharp 对应 | 说明 |
|-----------|-----------|--------------|------|
| `APPROX_BYTES_PER_TOKEN = 4` | `truncate.rs:4` | `estimateTokens` 中的 `/ 4` | 相同比率 |
| `approx_token_count()` 向上取整 | `truncate.rs:71-74` | `(byteCount + 3) / 4` | 完全对齐 |
| `TokenUsage` struct | `protocol.rs:2030-2041` | `Types.fs:190-194` | 已有，字段略不同 |
| `TokenUsageInfo` struct | `protocol.rs:2044-2050` | `TokenTracker` type | 简化版，无 `model_context_window` 独立字段 |
| `append_last_usage()` | `protocol.rs:2079-2082` | `TokenTracker.recordApiUsage` | 等价 |
| `get_total_token_usage()` | `history.rs:309-327` | `TokenTracker.currentUsageEstimate` | 简化版（无 reasoning 分支） |
| `items_after_last_model_generated` | `history.rs:298-305` | `TokenTracker.EstimatedPending` | 等价概念 |
| `percent_of_context_window_remaining()` | `protocol.rs:2192-2203` | `TokenTracker.contextRemainingPercent` | 等价，baselineTokens 调小 |
| `auto_compact_token_limit` check | `turn.rs:714-729` | `TokenTracker.shouldCompactByTokens` | 等价，阈值 80% |
| `truncate_middle_with_token_budget()` | `truncate.rs:15-36` | `truncateByTokenBudget` | 简化版 |
| `TruncationPolicy` enum | `protocol.rs:2865` | `MaxToolResultTokens` config | 不引入 enum，用配置区分 |
| `estimate_response_item_model_visible_bytes()` | `history.rs:530-555` | 未移植 | 复杂度过高（base64 图片估算等） |
| `fill_to_context_window()` | `protocol.rs:2084-2097` | 未移植 | 仅 Codex 的压缩检查点系统需要 |
| `TotalTokenUsageBreakdown` | `history.rs:53-59` | 未移植 | 调试级详情，BotSharp 无需 |
| Image cost estimation (LRU cache) | `history.rs:591-625` | 未移植 | BotSharp 图片使用有限 |

## 8. 不移植的 Codex 机制

| 机制 | 不移植原因 |
|------|----------|
| `TotalTokenUsageBreakdown`（4 字段分解） | BotSharp 无需调试级 token 分解 |
| `fill_to_context_window()` | 仅服务于 Codex 的 CompactionCheckpoint 系统 |
| 图片 token 估算（base64 解码 + patch 计算 + LRU 缓存） | 复杂度高，BotSharp 图片使用场景有限 |
| `TruncationPolicy` enum（Bytes / Tokens） | 用 `MaxToolResultTokens` 配置更简单 |
| 模型预设 JSON（`models.json` 内嵌上下文窗口） | BotSharp 已有 `ContextWindowTokens` 配置 |
| `ModelDownshift` 自动压缩 | BotSharp 不支持会话中途切换模型 |

## 9. 修改文件清单

| 文件 | 修改内容 | 复杂度 |
|------|---------|--------|
| `Domain/Types.fs` | 新增 `TokenTracker` 类型 + 模块；`BotSharpConfig` 新增 `MaxToolResultTokens` | 中 |
| `Application/AgentLoop.fs` | 修复 `estimateTokens`；修改 `messageTokens`；扩展 `trimToContextWindow` 签名；新增 `truncateByTokenBudget`；修改 `applyToolResultBudget`；更新裁剪管线；新增 tracker 更新点 | 高 |
| `Application/MemoryConsolidator.fs` | `needsConsolidation` 新增 `tracker` 参数 | 低 |
| `Application/SessionActor.fs` | 初始化 `actorTracker`；传递给 deps；整合判断传入 tracker | 低 |
| `Infrastructure/Tools/MyTool.fs` | `/status` 输出增加上下文窗口信息 | 低 |
| `Program.fs` | `AgentDependencies` 初始化增加 `TokenTracker` | 低 |

## 10. 实施计划

### Phase 1：修复 CJK 低估（1 天）

**最高优先级**，独立于其他阶段，可立即修复。

1. 修改 `estimateTokens`：`text.Length / 4` → `Encoding.UTF8.GetByteCount(text) / 4` 向上取整
2. 修改 `messageTokens` 中 ToolCallMessage 的参数估算
3. 单元测试：验证中文/日文/韩文/Emoji 的估算精度

### Phase 2：引入 TokenTracker（2 天）

1. 在 `Types.fs` 中定义 `TokenTracker` 类型和模块
2. 在 `SessionActor.fs` 中初始化 tracker
3. 在 `AgentLoop.fs` 中 API 响应后调用 `recordApiUsage`
4. 在 `AgentLoop.fs` 中新增消息后调用 `addPendingEstimate`
5. 单元测试：`recordApiUsage`、`addPendingEstimate`、`currentUsageEstimate`、`contextRemainingPercent`

### Phase 3：闭合反馈回路（1-2 天）

1. 修改 `trimToContextWindow`：接受 `trackerEstimate` 参数
2. 修改 `needsConsolidation`：双重触发（消息计数 OR token 阈值）
3. 更新裁剪管线
4. 集成测试：模拟长对话验证 tracker 驱动的裁剪和整合触发

### Phase 4：工具输出 Token 裁剪（1 天）

1. 实现 `truncateByTokenBudget`（head+tail 保留）
2. 新增 `MaxToolResultTokens` 配置
3. 修改 `applyToolResultBudget`
4. 单元测试：中文工具输出的截断精度

### Phase 5：可观测性（0.5 天）

1. `/status` 输出增加上下文窗口使用信息
2. 日志中输出 tracker 状态（每轮 API 调用后）

## 11. 测试策略

### 单元测试

```fsharp
module TokenEstimationTests =

    [<Fact>]
    let ``estimateTokens handles ASCII correctly`` () =
        // "hello world" = 11 bytes → (11+3)/4 = 3 tokens
        Assert.Equal(3, estimateTokens "hello world")

    [<Fact>]
    let ``estimateTokens handles Chinese correctly`` () =
        // "你好世界" = 12 UTF-8 bytes → (12+3)/4 = 3 tokens
        Assert.Equal(3, estimateTokens "你好世界")

    [<Fact>]
    let ``estimateTokens handles mixed CJK and ASCII`` () =
        // "hello你好" = 5 + 6 = 11 bytes → (11+3)/4 = 3 tokens
        Assert.Equal(3, estimateTokens "hello你好")

    [<Fact>]
    let ``estimateTokens handles emoji`` () =
        // "😀" = 4 UTF-8 bytes → (4+3)/4 = 1 token
        Assert.Equal(1, estimateTokens "😀")

    [<Fact>]
    let ``estimateTokens returns minimum 1 for empty string`` () =
        Assert.Equal(1, estimateTokens "")

module TokenTrackerTests =

    [<Fact>]
    let ``recordApiUsage accumulates totals`` () =
        let t = TokenTracker.empty 131072
        let u1 = { PromptTokens = 1000; CompletionTokens = 200; CachedTokens = 500 }
        let u2 = { PromptTokens = 1500; CompletionTokens = 300; CachedTokens = 800 }
        let t' = t |> TokenTracker.recordApiUsage u1 |> TokenTracker.recordApiUsage u2
        Assert.Equal(2500, t'.TotalUsage.PromptTokens)
        Assert.Equal(500, t'.TotalUsage.CompletionTokens)
        Assert.Equal(Some u2, t'.LastUsage)

    [<Fact>]
    let ``recordApiUsage resets pending`` () =
        let t = TokenTracker.empty 131072
                |> TokenTracker.addPendingEstimate 500
        Assert.Equal(500, t.EstimatedPending)
        let t' = t |> TokenTracker.recordApiUsage { PromptTokens = 2000; CompletionTokens = 100; CachedTokens = 0 }
        Assert.Equal(0, t'.EstimatedPending)

    [<Fact>]
    let ``currentUsageEstimate combines API and pending`` () =
        let t = TokenTracker.empty 131072
                |> TokenTracker.recordApiUsage { PromptTokens = 3000; CompletionTokens = 500; CachedTokens = 0 }
                |> TokenTracker.addPendingEstimate 200
        Assert.Equal(3700, TokenTracker.currentUsageEstimate t)

    [<Fact>]
    let ``contextRemainingPercent reports correctly`` () =
        let t = TokenTracker.empty 100000
                |> TokenTracker.recordApiUsage { PromptTokens = 40000; CompletionTokens = 5000; CachedTokens = 0 }
        // effectiveWindow = 100000 - 8000 = 92000
        // used = 45000, remaining = 47000
        // pct = 47000 * 100 / 92000 = 51
        Assert.Equal(51, TokenTracker.contextRemainingPercent t)

    [<Fact>]
    let ``shouldCompactByTokens triggers at 80%`` () =
        let t = TokenTracker.empty 100000
                |> TokenTracker.recordApiUsage { PromptTokens = 75000; CompletionTokens = 6000; CachedTokens = 0 }
        // used = 81000, threshold = 80000 → true
        Assert.True(TokenTracker.shouldCompactByTokens t)

    [<Fact>]
    let ``shouldCompactByTokens false when disabled`` () =
        let t = TokenTracker.empty 0
        Assert.False(TokenTracker.shouldCompactByTokens t)

module TruncationTests =

    [<Fact>]
    let ``truncateByTokenBudget preserves head and tail`` () =
        let text = String.replicate 1000 "abcd"  // 4000 bytes = 1000 tokens
        let result = truncateByTokenBudget 100 text  // 400 bytes budget
        Assert.True(result.StartsWith("abcd"))
        Assert.True(result.EndsWith("abcd"))
        Assert.Contains("truncated", result)

    [<Fact>]
    let ``truncateByTokenBudget no-op when under budget`` () =
        let text = "short text"
        Assert.Equal(text, truncateByTokenBudget 100 text)
```
