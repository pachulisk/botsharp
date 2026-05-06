# Codex vs BotSharp：Memory 子系统对比分析

> 对 OpenAI Codex CLI 和 BotSharp 两个项目的记忆子系统进行系统性对比，为 BotSharp 借鉴 Codex 设计提供决策依据。

## 共同的设计哲学

两个项目在记忆系统上做了一个相同且非显而易见的选择：**都没有使用向量数据库 / embedding 检索（RAG）**，而是选择了 **LLM 驱动的摘要式记忆**。这意味着：

- 记忆的提取和整合都依赖模型调用，而非 embedding 相似度搜索
- 记忆以**人类可读的 Markdown 文本**存储，而非高维向量
- 检索靠关键词匹配和文件读取，而非语义相似度

此外两者还共享以下设计原则：

| 共同点 | 说明 |
|--------|------|
| 会话存储格式 | 都使用 **JSONL** 作为对话历史的持久化格式（追加写入） |
| 记忆输出格式 | 都使用 **Markdown 文件**（MEMORY.md 等）保存整合后的长期记忆 |
| 阈值触发整合 | 都基于消息数量/token 预算阈值触发记忆整合，而非每轮都做 |
| 后台异步整合 | 都有后台服务/任务对空闲会话进行主动整合（BotSharp 的 AutoCompactService，Codex 的 Phase 1 job） |
| Token 预算裁剪 | 都实现了上下文窗口感知的消息裁剪，保留近期消息、丢弃旧消息 |
| 无向量数据库 | 都不依赖 Pinecone/Milvus/Weaviate 等向量存储 |

## 关键差异

### 1. 整合流水线：单阶段 vs 两阶段

**BotSharp** — 单阶段整合：
- 调用 LLM 的 `save_memory` 工具一次性提取两个结果：
  - `history_entry`（2-5 句摘要 → 追加到 HISTORY.md）
  - `memory_update`（完整的长期记忆 → 覆盖写入 MEMORY.md）
- 模型、参数与主对话共享（可通过 `DreamModelOverride` 覆盖）

**Codex** — 两阶段流水线：
- **Phase 1（提取）**：用轻量模型（gpt-5.4-mini, Low reasoning）从每个 rollout 中提取 `raw_memory`（Markdown）+ `rollout_summary`（一行描述）+ `rollout_slug`
- **Phase 2（整合）**：用更强模型（gpt-5.4, Medium reasoning）将多个 Phase 1 输出合并为结构化记忆工作区
- 两阶段解耦：Phase 1 可高频运行，Phase 2 有冷却间隔和全局锁

### 2. 存储后端：纯文件系统 vs SQLite + 文件混合

**BotSharp** — 纯文件系统：
- `sessions/{sid}.jsonl` — 对话历史
- `memory/MEMORY.md` — 长期记忆（覆盖写入）
- `memory/HISTORY.md` — 历史日志（追加写入）
- `memory/.dream_cursor` — 整合游标（指针文件）
- `dreams.jsonl` — 整合元数据日志
- 无数据库依赖，所有状态都是文件

**Codex** — 混合存储：
- **JSONL rollout 文件** — 事件源头（追加写入，不可变审计轨迹）
- **SQLite `stage1_outputs` 表** — Phase 1 输出的结构化索引（支持排序、过期清理、使用计数）
- **SQLite `jobs` 表** — 分布式任务调度（Phase 1/Phase 2 作业队列，带所有权令牌和租约）
- **文件系统记忆工作区** — `memory_summary.md`、`MEMORY.md`、`rollout_summaries/*.md`、`skills/`

### 3. 记忆输出结构：两文件 vs 多层工作区

**BotSharp** — 两个文件：
- `MEMORY.md`：当前状态的长期记忆（每次整合全量覆盖）
- `HISTORY.md`：时间线日志（追加，尾部 32KB 注入系统提示词）

**Codex** — 多层级渐进式工作区：
- `memory_summary.md`：导航索引（始终加载，限制 5000 token）
- `MEMORY.md`：可搜索的记忆注册表
- `rollout_summaries/*.md`：每个会话的精炼摘要
- `skills/<name>/`：从记忆中自动提取的可复用技能/流程

### 4. 记忆注入方式：系统提示词直注 vs 渐进式读取路径

**BotSharp** — 每轮直接注入：
- `MEMORY.md` 全文注入系统提示词
- `HISTORY.md` 尾部 32KB 注入系统提示词
- Agent 无需主动检索，记忆始终在上下文中

**Codex** — Agent 主动检索（渐进式披露）：
1. 先浏览 `memory_summary.md` 寻找关键词
2. 按需搜索 `MEMORY.md`
3. 跟随指针到 `rollout_summaries/` 或 `skills/`
4. 必要时回溯到 rollout 原文查证
5. 目标：4-6 步内完成查找，节省 token

### 5. 留存与排名：无机制 vs 使用频率排名

**BotSharp** — 无留存策略：
- MEMORY.md 每次整合全量覆盖，无历史版本
- 无使用计数或热度追踪
- 仅通过 `SessionCleanupDays` 清理过期会话文件

**Codex** — 基于使用频率的排名与清理：
- `usage_count`：每次记忆被引用时递增
- `last_usage`：最后使用时间戳
- Phase 2 选择输入时按 `usage_count DESC, last_usage DESC` 排名
- `prune_stage1_outputs_for_retention()`：清理长期未使用且未被 Phase 2 锁定的记忆
- `max_unused_days`：可配置的过期天数

### 6. 引用与可追溯性

**BotSharp** — 无引用机制：
- 记忆注入系统提示词后，无法追溯记忆来源

**Codex** — 结构化引用：
- `MemoryCitation`：记录引用的文件路径、行范围（`line_start`/`line_end`）、使用说明（`note`）
- `rollout_ids`：追溯到原始会话
- 引用触发 `record_stage1_output_usage()` 更新使用计数

### 7. 上下文压缩：覆盖写入 vs 检查点替换

**BotSharp** — 覆盖 MEMORY.md：
- 整合后 MEMORY.md 被新版本覆盖
- 旧版本不保留（HISTORY.md 保留摘要）
- 对话历史在 JSONL 中保留完整

**Codex** — 替换历史检查点：
- 压缩生成 `replacement_history`（新的精简历史）
- 通过 `CompactionCheckpoint` 安装到 rollout 中
- 原始事件仍保留在 rollout 文件中（完整审计轨迹）
- 会话恢复时从最新检查点向前重放

### 8. 技能提取

**BotSharp** — 手动编写：
- 技能以 `skills/<name>/SKILL.md` 形式手动编写
- 记忆系统不生成技能

**Codex** — Phase 2 自动生成：
- 整合过程中自动识别可复用的操作流程
- 生成 `skills/<skill-name>/` 目录
- 从经验中学习并编纂为可复用知识

## 总结对比表

| 维度 | BotSharp | Codex |
|------|----------|-------|
| **整合阶段** | 单阶段（save_memory 工具调用） | 两阶段（Phase 1 提取 + Phase 2 整合） |
| **整合模型** | 与主模型共享（可覆盖） | 分级：Phase 1 用轻量模型，Phase 2 用强模型 |
| **存储后端** | 纯文件系统 | SQLite + 文件系统混合 |
| **记忆输出** | 2 个文件（MEMORY.md + HISTORY.md） | 多层工作区（summary + MEMORY + rollout_summaries + skills） |
| **记忆注入** | 系统提示词直注（被动） | Agent 主动检索、渐进式披露（主动） |
| **留存策略** | 无（全量覆盖） | 使用频率排名 + 过期清理 |
| **引用追溯** | 无 | MemoryCitation（文件 + 行范围） |
| **上下文压缩** | MEMORY.md 覆盖写入 | 替换历史检查点（原始事件保留） |
| **技能提取** | 手动编写 | Phase 2 自动生成 |
| **任务调度** | 后台服务（AutoCompactService） | SQLite 作业队列（分布式锁 + 租约） |
| **Token 估算** | 4 字符 ≈ 1 token 启发式 | 基于字节的精确估算 + API 报告值 |
| **适用规模** | 个人 Agent，单机文件系统 | 多用户产品，支持云端变体 |

## 设计取舍

两者的差异本质上反映了**应用场景的不同**：

- **BotSharp** 是个人 Agent 框架，追求**简洁、透明、可调试**。纯文件存储意味着用户可以直接用编辑器查看和修改记忆，git 可以追踪变化。单阶段整合减少了系统复杂度。
- **Codex** 是面向大规模用户的产品级系统，需要**高吞吐、精细控制、可扩展**。两阶段流水线允许异步解耦和模型分级；SQLite 作业队列支持分布式调度；使用频率排名确保有限的 token 预算分配给最有价值的记忆。

两者都做对了一件事：**不盲目追随 RAG/向量检索的潮流**，而是根据实际场景选择了 LLM 驱动的文本摘要路线——对于以对话为核心的 Agent 系统，这往往是更实用的选择。

## 借鉴建议

基于以上对比，Codex 中以下机制对 BotSharp 有直接借鉴价值：

| 建议借鉴 | 理由 | 详细设计 |
|---------|------|---------|
| SQLite 作为派生索引 | 解决会话列表、搜索、元数据查询的性能问题 | 见 [hybrid-storage.md](hybrid-storage.md) |
| 记忆使用频率追踪 | 为记忆保留/淘汰决策提供数据基础 | 见 [hybrid-storage.md](hybrid-storage.md) §3.3 |
| 整合状态结构化存储 | 替代脆弱的 `.dream_cursor` 文本文件 | 见 [hybrid-storage.md](hybrid-storage.md) §3.1 |
| 索引重建安全网 | SQLite 可随时从 JSONL 重建，降低风险 | 见 [hybrid-storage.md](hybrid-storage.md) §6 |

不建议借鉴的机制（规模不匹配）：分布式作业队列、两阶段流水线、独立日志数据库、线程生成图。
