# BotSharp 渐进式记忆披露设计方案

> 借鉴 Codex 的 `read_path.md` 提示词工程模式，将 BotSharp 的记忆注入从"全量直注"升级为"摘要注入 + 按需检索 + 引用追踪"。

## 1. 问题分析

### 1.1 当前注入方式

BotSharp 将 MEMORY.md **全文注入**系统提示词（`ContextBuilder.fs:169-171`）：

```
系统提示词组装顺序：
1. IDENTITY.md
2. 频道格式提示
3. Bootstrap 文件（AGENTS.md, SOUL.md, USER.md, TOOLS.md）
4. ★ MEMORY.md 全文 ★    ← 无大小限制
5. 始终激活的技能（inline markdown）
6. 按需技能（XML 摘要）
7. HISTORY.md 尾部（32KB）
8. 系统提示词追加
```

**问题**：

| 问题 | 影响 |
|------|------|
| MEMORY.md 无大小限制 | 随着使用时间增长，MEMORY.md 持续膨胀，占用大量 token 预算 |
| 每轮全量注入 | 即使当前任务与记忆无关，也消耗 token（天气查询不需要知道项目架构） |
| 系统消息永不裁剪 | `trimToContextWindow` 保留所有 SystemMessage，巨大的 MEMORY.md 压缩了可用对话空间 |
| 无法追踪使用 | 不知道哪些记忆实际被 Agent 使用了，无法优化留存策略 |
| 无法分层检索 | 没有从粗到细的检索路径，要么全量注入要么完全不注入 |

### 1.2 Codex 的渐进式披露

Codex 的整个"渐进式披露系统"实际上是一个**提示词工程模式**，由以下组件构成：

1. **`memory_summary.md`**（≤ 5,000 token）注入 developer instructions — 提供记忆的导航索引
2. **`read_path.md`**（130 行模板）— 教会 Agent 如何按需检索更详细的记忆
3. **标准工具**（grep, read_file）— Agent 已有的文件访问能力
4. **引用解析**（`<oai-mem-citation>`）— 从 Agent 输出中追踪哪些记忆被使用

**关键发现**：Codex **没有**专门的记忆检索工具（`Feature::MemoryTool` 只控制是否注入指令，不是一个 tool handler）。Agent 使用 `exec_command`、`read_file` 等标准工具按照 `read_path.md` 的指令自主检索。

### 1.3 BotSharp 已有的基础设施

BotSharp **已经具备**实现渐进式披露的全部基础设施：

| 需求 | BotSharp 现有能力 | 来源 |
|------|-----------------|------|
| 文件读取 | `read_file`（含行范围） | `FileSystemTool.fs:271` |
| 内容搜索 | `grep`（正则） | `FileSystemTool.fs:923` |
| 目录浏览 | `list_dir` | `FileSystemTool.fs:292` |
| 文件发现 | `glob`（通配符） | `FileSystemTool.fs:908` |
| 渐进式加载先例 | 技能系统（always-active inline + on-demand XML 摘要 + read_file） | `SkillsLoader.fs:178-203` |

BotSharp 的技能系统已经实现了**完全相同的渐进式模式**：
- `# Active Skills`：始终激活的技能全文 inline 注入
- `# Available Skills`：按需技能仅注入 XML 摘要（名称 + 描述），Agent 通过 `read_file` 按需读取完整内容

记忆的渐进式披露本质上是**将技能系统的模式扩展到记忆**。

## 2. 目标架构

```
┌────────────────────────────────────────────────────────────────┐
│                      系统提示词                                │
│                                                                │
│  1. IDENTITY.md                                                │
│  2. Bootstrap 文件                                             │
│  3. ★ memory_summary.md（≤ 5000 token）★                      │
│     └─ 包含渐进式检索指令                                       │
│  4. 技能（inline + XML 摘要）                                   │
│  5. HISTORY.md 尾部（32KB）                                     │
│                                                                │
│  ✕ MEMORY.md 全文不再注入                                       │
└────────────────────────────────────────────────────────────────┘
                            ↓
              Agent 根据指令自主决策
                            ↓
         ┌──── 任务与记忆无关 ────→ 跳过检索，直接工作
         │
         └──── 任务需要记忆 ────→ 渐进式检索
                                   ↓
                    ┌─ Step 1：浏览 memory_summary.md（已在上下文中）
                    │           提取关键词
                    │
                    ├─ Step 2：grep memory/MEMORY.md 搜索关键词
                    │           找到相关段落
                    │
                    ├─ Step 3：如 MEMORY.md 指向更详细文件
                    │           read_file rollout_summaries/*.md 或 skills/*/SKILL.md
                    │
                    └─ Step 4：如仍不够
                              grep sessions/{sid}.jsonl 查原始对话
                              ↓
                        最多 4-6 步，停止检索，开始工作
                                   ↓
                    Agent 在回复末尾附加引用标记
                    <mem-citation> ... </mem-citation>
                                   ↓
                    BotSharp 解析引用 → 更新 usage_count
```

## 3. 渐进式披露指令模板

### 3.1 模板文件

**新增文件**：`src/BotSharp/Templates/memory_read_path.md`

对应 Codex `memories/read/templates/memories/read_path.md`（130 行）。

```markdown
# Memory System

You have access to a persistent memory system at `{{ memory_base_path }}`.

## When to Use Memory

Skip memory lookup ONLY when the task is completely self-contained (current time, trivial formatting, simple math).

Use memory by default when:
- Query mentions a workspace, project, repo, module, or path referenced in MEMORY_SUMMARY below
- User asks for prior context, consistency with previous decisions, or "what did we do last time"
- Task is ambiguous and could depend on earlier choices or user preferences
- Non-trivial work on topics covered in MEMORY_SUMMARY

## Memory Layout

```
{{ memory_base_path }}/
├── memory_summary.md    ← ALREADY IN YOUR CONTEXT BELOW; DO NOT re-read
├── MEMORY.md            ← Searchable registry; PRIMARY FILE TO QUERY
├── rollout_summaries/   ← Per-session distilled recaps
│   └── *.md
└── skills/              ← Reusable procedures
    └── <skill-name>/SKILL.md
```

## Retrieval Protocol (4-6 steps max)

1. **Skim MEMORY_SUMMARY** below — extract task-relevant keywords
2. **Search `MEMORY.md`** — `grep "keyword" {{ memory_base_path }}/MEMORY.md`
3. **Only if MEMORY.md points elsewhere** — open 1-2 most relevant files:
   - `read_file {{ memory_base_path }}/rollout_summaries/<file>.md`
   - `read_file {{ memory_base_path }}/skills/<name>/SKILL.md`
4. **If unclear** — search session history for exact commands/errors:
   - `grep "error message" {{ sessions_path }}/<session>.jsonl`
5. **If no relevant hits** — STOP and continue normally

**Budget: ≤ 4-6 search steps before main work. Do NOT broadly scan all rollout summaries.**

During execution: if repeated errors occur, redo a quick memory pass for similar past failures.

## Verification Strategy

When using facts from memory:
- **Easy to verify + might be stale** → verify before answering (e.g., file paths, versions)
- **Expensive to verify + might be stale** → answer from memory, note it may be outdated, offer to refresh
- **Unlikely to change** → answer from memory directly (e.g., user preferences, architecture decisions)
- **Unverified memory** → say so; note possible staleness; offer to refresh

## Citation Format

When you use memory in your response, append exactly one citation block at the END of your reply:

```
<mem-citation>
MEMORY.md:12-15|note=[user prefers dark theme]
rollout_summaries/2026-05-01T14-30-deploy.md:3-8|note=[nginx deploy procedure]
</mem-citation>
```

Rules:
- One entry per line: `<file>:<line_start>-<line_end>|note=[brief usage description]`
- File paths relative to `{{ memory_base_path }}/` (e.g., `MEMORY.md`, `rollout_summaries/...`)
- Order by importance (most important first)
- `note` must be short, single line
- If no memory was used, omit the citation block entirely

---

## MEMORY_SUMMARY

{{ memory_summary }}
```

### 3.2 模板变量

| 变量 | 来源 | 示例值 |
|------|------|--------|
| `{{ memory_base_path }}` | `config.WorkspacePath + "/memory"` | `/home/user/.botsharp/workspace/memory` |
| `{{ sessions_path }}` | `config.WorkspacePath + "/sessions"` | `/home/user/.botsharp/workspace/sessions` |
| `{{ memory_summary }}` | `memory_summary.md` 内容（截断至 5000 token） | Markdown 文本 |

### 3.3 与 Codex `read_path.md` 的对照

| Codex read_path.md 部分 | BotSharp 对应 | 差异说明 |
|------------------------|-------------|---------|
| 决策边界（何时用/何时跳过） | 完整保留 | 措辞适配 |
| Memory Layout（目录结构） | 适配为 BotSharp 路径 | 无 rollout_path，用 sessions/ 替代 |
| 检索协议（4-6 步） | 完整保留 | Step 4 用 sessions/*.jsonl 替代 rollout 文件 |
| 验证策略（成本-收益分析） | 完整保留 | — |
| 引用格式 | 简化 | 去掉 `<rollout_ids>`（BotSharp 用 session_id 追踪） |
| Token 预算（"keep lightweight"） | 完整保留 | — |
| 运行时重查（repeated errors） | 完整保留 | — |

**有意省略**：
- Codex 的 `<oai-mem-citation>` 使用 XML 嵌套格式（`<citation_entries>` + `<rollout_ids>`），BotSharp 简化为单层 `<mem-citation>` 纯文本格式
- Codex 的"隐藏引用"渲染（`strip_citations` 从可见输出中移除引用）—— BotSharp 初期保留引用在输出中，后续可选择性隐藏

## 4. ContextBuilder 修改

### 4.1 记忆注入重构

**文件**：`ContextBuilder.fs`

```fsharp
// ── 之前（全量注入）──
// Line 169-171
match memory with
| Some txt -> yield $"# Memory\n\n{txt}"
| None     -> ()

// ── 之后（渐进式披露）──

// 1. 尝试加载 memory_summary.md（Phase 2 产物）
let! summaryOpt = readOptional (join "memory" "memory_summary.md")

// 2. 回退到 MEMORY.md（单阶段模式 / Phase 2 尚未运行）
let! memoryOpt = readOptional (join "memory" "MEMORY.md")

match summaryOpt with
| Some summary ->
    // Phase 2 模式：渐进式披露
    let truncatedSummary = truncateToTokenBudget 5000 summary
    let memoryInstructions =
        renderTemplate memoryReadPathTemplate
            [ "memory_base_path", memoryBasePath
              "sessions_path", sessionsPath
              "memory_summary", truncatedSummary ]
    yield memoryInstructions

| None ->
    match memoryOpt with
    | Some txt when txt.Length > memoryDirectInjectLimit ->
        // MEMORY.md 过大：也切换到渐进式模式
        let truncatedMemory = truncateToTokenBudget 5000 txt
        let memoryInstructions =
            renderTemplate memoryReadPathTemplate
                [ "memory_base_path", memoryBasePath
                  "sessions_path", sessionsPath
                  "memory_summary", truncatedMemory ]
        yield memoryInstructions

    | Some txt ->
        // MEMORY.md 较小：保持全量注入（向后兼容）
        yield $"# Memory\n\n{txt}"

    | None -> ()
```

### 4.2 截断函数

```fsharp
/// 截断文本到指定 token 预算。
/// 对应 Codex read/src/prompts.rs 的 TruncationPolicy::Tokens(5_000)
let truncateToTokenBudget (maxTokens: int) (text: string) : string =
    let maxBytes = maxTokens * 4  // 1 token ≈ 4 UTF-8 bytes
    let textBytes = System.Text.Encoding.UTF8.GetByteCount(text)
    if textBytes <= maxBytes then text
    else
        // 保留头部，在 UTF-8 字符边界截断
        let mutable byteCount = 0
        let mutable charCount = 0
        for c in text do
            let cb = System.Text.Encoding.UTF8.GetByteCount(string c)
            if byteCount + cb <= maxBytes then
                byteCount <- byteCount + cb
                charCount <- charCount + 1
        text.[..charCount - 1] + "\n\n…(truncated)…"
```

### 4.3 触发模式决策

| 条件 | 注入模式 | 说明 |
|------|---------|------|
| `memory_summary.md` 存在 | **渐进式** | Phase 2 已运行，使用完整渐进式披露 |
| `MEMORY.md` 存在且 > `memoryDirectInjectLimit` | **渐进式** | MEMORY.md 过大，自动切换到渐进式 |
| `MEMORY.md` 存在且 ≤ `memoryDirectInjectLimit` | **全量直注** | MEMORY.md 较小，保持向后兼容 |
| 两者都不存在 | **无注入** | 无记忆 |

```fsharp
/// MEMORY.md 超过此大小时自动切换到渐进式模式（token 数）。
/// 对应 Codex 的 MEMORY_TOOL_DEVELOPER_INSTRUCTIONS_SUMMARY_TOKEN_LIMIT = 5_000
let memoryDirectInjectLimit = 5000  // token
```

## 5. 引用解析与使用追踪

### 5.1 引用格式

Agent 在回复末尾附加（仅在使用了记忆时）：

```
<mem-citation>
MEMORY.md:12-15|note=[user prefers dark theme]
rollout_summaries/2026-05-01T14-30-deploy.md:3-8|note=[nginx deploy procedure]
skills/deploy-nginx/SKILL.md:1-20|note=[reusable deploy steps]
</mem-citation>
```

### 5.2 引用解析

**新增文件**：`src/BotSharp/Infrastructure/Memory/CitationParser.fs`

对应 Codex `memories/read/src/citations.rs`（86 行）：

```fsharp
module BotSharp.Infrastructure.Memory.CitationParser

/// 单条引用条目。
/// 对应 Codex MemoryCitationEntry（protocol/src/memory_citation.rs）
type CitationEntry = {
    Path      : string       // 相对于 memory/ 的文件路径
    LineStart : int
    LineEnd   : int
    Note      : string       // 使用说明
}

/// 解析结果。
/// 对应 Codex MemoryCitation
type MemoryCitation = {
    Entries : CitationEntry list
}

/// 从 Agent 输出中提取引用块。
/// 对应 Codex citations.rs parse_memory_citation
let parseCitation (text: string) : MemoryCitation option =
    // 1. 查找 <mem-citation> ... </mem-citation> 块
    let startTag = "<mem-citation>"
    let endTag = "</mem-citation>"
    match text.IndexOf(startTag), text.IndexOf(endTag) with
    | s, e when s >= 0 && e > s ->
        let block = text.[s + startTag.Length .. e - 1].Trim()
        let entries =
            block.Split('\n')
            |> Array.choose parseCitationLine
            |> Array.toList
        if entries.IsEmpty then None
        else Some { Entries = entries }
    | _ -> None

/// 解析单行引用。
/// 格式：path:start-end|note=[description]
/// 对应 Codex citations.rs parse_memory_citation_entry（lines 53-70）
let private parseCitationLine (line: string) : CitationEntry option =
    let line = line.Trim()
    if String.IsNullOrWhiteSpace line then None
    else
        match line.LastIndexOf("|note=[") with
        | -1 -> None
        | noteStart ->
            let location = line.[..noteStart - 1]
            let noteRaw = line.[noteStart + 7..]
            let note = noteRaw.TrimEnd(']').Trim()
            match location.LastIndexOf(':') with
            | -1 -> None
            | colonIdx ->
                let path = location.[..colonIdx - 1]
                let range = location.[colonIdx + 1..]
                match range.Split('-') with
                | [| s; e |] ->
                    match Int32.TryParse(s), Int32.TryParse(e) with
                    | (true, ls), (true, le) ->
                        Some { Path = path; LineStart = ls; LineEnd = le; Note = note }
                    | _ -> None
                | _ -> None

/// 从 Agent 输出中剥离引用块，返回（可见文本, 引用）。
/// 对应 Codex strip_citations
let stripCitation (text: string) : string * MemoryCitation option =
    let startTag = "<mem-citation>"
    let endTag = "</mem-citation>"
    match text.IndexOf(startTag), text.IndexOf(endTag) with
    | s, e when s >= 0 && e > s ->
        let visible = (text.[..s - 1].TrimEnd() + text.[e + endTag.Length..]).Trim()
        let citation = parseCitation text
        (visible, citation)
    | _ -> (text, None)
```

### 5.3 引用追踪集成

**修改文件**：`AgentLoop.fs`（Agent 输出处理部分）

```fsharp
// 在 Agent 响应处理之后（约 line 950）

// 1. 解析引用
let (visibleText, citationOpt) = CitationParser.stripCitation response.Content

// 2. 记录使用（如果有引用）
match citationOpt, deps.OpenStateDb with
| Some citation, Some openDb ->
    // 从引用条目中提取 session_id（如果引用了 rollout_summaries）
    let sessionIds =
        citation.Entries
        |> List.choose (fun e ->
            if e.Path.StartsWith("rollout_summaries/") then
                extractSessionIdFromSummaryPath e.Path
            else None)
    if not sessionIds.IsEmpty then
        try
            use conn = openDb()
            do! StateDb.recordStage1OutputUsage conn sessionIds
        with ex ->
            Log.warning "Citation usage tracking failed: %s" ex.Message
| _ -> ()

// 3. 向用户展示不含引用的文本
let displayText = visibleText
```

### 5.4 记忆文件访问追踪

对应 Codex `memories/read/src/usage.rs`。追踪 Agent 通过 `read_file` / `grep` 访问了哪些记忆文件：

**修改文件**：`FileSystemTool.fs`

```fsharp
/// 检测文件路径是否为记忆文件。
/// 对应 Codex usage.rs MemoriesUsageKind 枚举
type MemoryFileKind =
    | MemoryMd
    | MemorySummary
    | RolloutSummary
    | Skill
    | None

let detectMemoryFileKind (path: string) : MemoryFileKind =
    if path.Contains("/memory/MEMORY.md") then MemoryMd
    elif path.Contains("/memory/memory_summary.md") then MemorySummary
    elif path.Contains("/memory/rollout_summaries/") then RolloutSummary
    elif path.Contains("/memory/skills/") then Skill
    else None

// 在 read_file / grep 工具执行后
let kind = detectMemoryFileKind filePath
match kind with
| None -> ()
| _ ->
    Log.info "[memory-usage] Agent accessed %A: %s" kind filePath
    // 可选：记录到 SQLite 或发送遥测
```

## 6. 配置

### 6.1 新增配置字段

**文件**：`Types.fs` 的 `BotSharpConfig`

```fsharp
type BotSharpConfig = {
    // 现有字段...

    /// memory_summary.md 注入的 token 预算上限。
    /// 对应 Codex MEMORY_TOOL_DEVELOPER_INSTRUCTIONS_SUMMARY_TOKEN_LIMIT = 5000
    MemorySummaryTokenLimit : int         // 默认 5000

    /// MEMORY.md 超过此 token 数时自动切换到渐进式模式。
    /// 低于此值时保持全量直注（向后兼容）。
    MemoryDirectInjectLimit : int         // 默认 5000

    /// 是否启用记忆引用追踪。
    MemoryCitationTracking  : bool        // 默认 true

    /// 是否从 Agent 输出中剥离引用块（用户不可见）。
    /// false = 引用保留在输出中（调试友好）。
    /// true = 引用被剥离，仅用于内部追踪。
    MemoryCitationStrip     : bool        // 默认 false（初期保留，方便调试）
}
```

### 6.2 默认值

```fsharp
MemorySummaryTokenLimit = 5000
MemoryDirectInjectLimit = 5000
MemoryCitationTracking  = true
MemoryCitationStrip     = false   // 初期保留引用在输出中，方便验证
```

## 7. HISTORY.md 注入保持不变

HISTORY.md 尾部 32KB 注入**保持现有行为不变**。理由：

1. HISTORY.md 是时间线日志，已经有 32KB 的大小限制
2. 它提供的是"最近发生了什么"的上下文，不适合按需检索
3. Codex 没有等价的 HISTORY.md 注入（Codex 的历史在 rollout_summaries 中），这是 BotSharp 的特色保留

## 8. 与其他设计文档的关系

| 文档 | 关系 |
|------|------|
| [two-phase-memory.md](two-phase-memory.md) §5 | 本文档是其"记忆读取路径变更"的详细展开 |
| [hybrid-storage.md](hybrid-storage.md) §3.3 | 引用追踪的 `usage_count` 更新写入 `memory_usage` 表 |
| [token-estimation.md](token-estimation.md) | `truncateToTokenBudget` 使用 UTF-8 字节基准的 token 估算 |
| [sqlite-job-queue.md](sqlite-job-queue.md) | 无直接关系（读取路径不涉及作业队列） |

## 9. 修改文件清单

| 文件 | 修改内容 | 复杂度 |
|------|---------|--------|
| **新增** `Templates/memory_read_path.md` | 渐进式检索指令模板（~100 行） | 高（提示词工程） |
| **新增** `Infrastructure/Memory/CitationParser.fs` | 引用解析 + 剥离（parseCitation, stripCitation） | 中 |
| `Application/ContextBuilder.fs` | 记忆注入重构（摘要优先 + 渐进式回退 + 截断） | 中 |
| `Application/AgentLoop.fs` | Agent 输出引用解析 + usage 追踪 | 低 |
| `Infrastructure/Tools/FileSystemTool.fs` | 记忆文件访问检测（read_file/grep 后检查路径） | 低 |
| `Domain/Types.fs` | CitationEntry, MemoryCitation, 配置字段 | 低 |

## 10. 实施计划

### Phase 1：ContextBuilder 重构 + 模板（2-3 天）

1. 编写 `memory_read_path.md` 模板
2. 实现 `truncateToTokenBudget`
3. 修改 `ContextBuilder.fs`：摘要优先 + 直注回退 + 渐进式切换
4. 测试：验证三种模式（memory_summary.md 存在 / MEMORY.md 过大 / MEMORY.md 较小）

### Phase 2：引用解析与追踪（2 天）

1. 实现 `CitationParser.fs`：`parseCitation`、`parseCitationLine`、`stripCitation`
2. 在 `AgentLoop.fs` 中集成引用解析
3. 连接 `StateDb.recordStage1OutputUsage`（使用频率递增）
4. 测试：引用解析的各种格式、空引用、畸形引用

### Phase 3：文件访问追踪（1 天）

1. 在 `FileSystemTool.fs` 中添加 `detectMemoryFileKind`
2. 日志输出记忆文件访问事件
3. 可选：记录到 SQLite

### Phase 4：提示词调优（1-2 天）

1. 用真实会话测试渐进式检索的效果
2. 调整检索指令的措辞和步骤
3. 验证 Agent 是否在不需要记忆时跳过检索
4. 验证 Agent 是否在需要记忆时正确检索
5. 调整 `MemorySummaryTokenLimit` 和 `MemoryDirectInjectLimit`

## 11. 测试策略

```fsharp
module CitationParserTests =

    [<Fact>]
    let ``parseCitation extracts single entry`` () =
        let text = """Some response text.
<mem-citation>
MEMORY.md:12-15|note=[user prefers dark theme]
</mem-citation>"""
        let result = CitationParser.parseCitation text
        Assert.True(result.IsSome)
        Assert.Equal(1, result.Value.Entries.Length)
        Assert.Equal("MEMORY.md", result.Value.Entries.[0].Path)
        Assert.Equal(12, result.Value.Entries.[0].LineStart)
        Assert.Equal(15, result.Value.Entries.[0].LineEnd)
        Assert.Equal("user prefers dark theme", result.Value.Entries.[0].Note)

    [<Fact>]
    let ``parseCitation extracts multiple entries`` () =
        let text = """Response.
<mem-citation>
MEMORY.md:1-5|note=[architecture]
rollout_summaries/deploy.md:10-20|note=[deploy steps]
skills/nginx/SKILL.md:1-30|note=[nginx config]
</mem-citation>"""
        let result = CitationParser.parseCitation text
        Assert.True(result.IsSome)
        Assert.Equal(3, result.Value.Entries.Length)

    [<Fact>]
    let ``parseCitation returns None when no citation`` () =
        let text = "Just a normal response without any citations."
        Assert.True((CitationParser.parseCitation text).IsNone)

    [<Fact>]
    let ``parseCitation handles empty citation block`` () =
        let text = """Text.
<mem-citation>
</mem-citation>"""
        Assert.True((CitationParser.parseCitation text).IsNone)

    [<Fact>]
    let ``parseCitation ignores malformed lines`` () =
        let text = """Text.
<mem-citation>
MEMORY.md:12-15|note=[valid]
this is not a valid citation line
also:invalid
MEMORY.md:20-25|note=[also valid]
</mem-citation>"""
        let result = CitationParser.parseCitation text
        Assert.True(result.IsSome)
        Assert.Equal(2, result.Value.Entries.Length)

    [<Fact>]
    let ``stripCitation separates visible text from citation`` () =
        let text = """Here is my response about the deploy process.

<mem-citation>
MEMORY.md:12-15|note=[deploy notes]
</mem-citation>"""
        let (visible, citation) = CitationParser.stripCitation text
        Assert.Equal("Here is my response about the deploy process.", visible)
        Assert.True(citation.IsSome)

    [<Fact>]
    let ``stripCitation returns original text when no citation`` () =
        let text = "Normal response."
        let (visible, citation) = CitationParser.stripCitation text
        Assert.Equal("Normal response.", visible)
        Assert.True(citation.IsNone)

module ContextBuilderTests =

    [<Fact>]
    let ``uses progressive disclosure when memory_summary.md exists`` () =
        // Setup: memory/memory_summary.md present
        // Assert: output contains "Retrieval Protocol" and "MEMORY_SUMMARY"
        // Assert: output does NOT contain full MEMORY.md content
        ...

    [<Fact>]
    let ``falls back to progressive when MEMORY.md exceeds limit`` () =
        // Setup: no memory_summary.md, MEMORY.md > 5000 tokens
        // Assert: output contains truncated MEMORY.md in template
        ...

    [<Fact>]
    let ``direct injects small MEMORY.md`` () =
        // Setup: no memory_summary.md, MEMORY.md < 5000 tokens
        // Assert: output contains "# Memory\n\n{full content}"
        ...

    [<Fact>]
    let ``truncateToTokenBudget respects UTF-8 byte limit`` () =
        let chinese = String.replicate 2000 "你"  // 2000 chars, 6000 UTF-8 bytes
        let result = truncateToTokenBudget 1000 chinese  // 4000 bytes budget
        let resultBytes = System.Text.Encoding.UTF8.GetByteCount(result)
        Assert.True(resultBytes <= 4000 + 20)  // 20 bytes for truncation marker

module MemoryFileDetectionTests =

    [<Fact>]
    let ``detects MEMORY.md access`` () =
        Assert.Equal(MemoryMd, detectMemoryFileKind "/home/user/.botsharp/workspace/memory/MEMORY.md")

    [<Fact>]
    let ``detects rollout summary access`` () =
        Assert.Equal(RolloutSummary,
            detectMemoryFileKind "/home/user/.botsharp/workspace/memory/rollout_summaries/deploy.md")

    [<Fact>]
    let ``detects skill access`` () =
        Assert.Equal(Skill,
            detectMemoryFileKind "/home/user/.botsharp/workspace/memory/skills/nginx/SKILL.md")

    [<Fact>]
    let ``returns None for non-memory files`` () =
        Assert.Equal(None, detectMemoryFileKind "/home/user/project/src/main.py")
```
