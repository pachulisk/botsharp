# 函数式编程 × DDD × 形式化验证 研究笔记

> 整理自 2026-04-24 研究讨论

---

## 目录

1. [DDD 核心方法论](#1-ddd-核心方法论)
2. [Rust 生态：CQRS / Event Sourcing](#2-rust-生态cqrs--event-sourcing)
3. [函数式语言选型：改写 nanobot](#3-函数式语言选型改写-nanobot)
4. [各平台运行时分析](#4-各平台运行时分析)（F# / Chez Scheme）
5. [形式化验证工具谱系](#5-形式化验证工具谱系)
6. [推荐学习路径](#6-推荐学习路径)

---

## 1. DDD 核心方法论

### 战略设计（Strategic Design）

| 方法 | 用途 |
|------|------|
| **Ubiquitous Language** | 领域专家与开发者共用词汇，消除翻译损耗 |
| **Bounded Context** | 划定模型边界，每个 BC 内语言一致 |
| **Context Map** | 可视化 BC 间关系（ACL、Shared Kernel、Conformist 等）|

**发现工具：**
- **Event Storming**（Alberto Brandolini）— 最主流建模工作坊，橙色便利贴标领域事件，分 Big Picture / Design Level / Process Level 三层
- **Domain Storytelling** — 结构化叙事图，适合与业务方沟通
- **Example Mapping**（BDD 衍生）— 具体例子澄清规则，发现边界条件
- **Boris Diagram** — 可视化服务/Actor 同步/异步调用关系

### 战术设计（Tactical Design）

```
Aggregate Root → Entities + Value Objects
↑
Repository（持久化）
↑
Domain Service（跨聚合业务逻辑）
↑
Application Service（用例编排）
↑
Domain Events（跨 BC 通信）
```

### 架构模式组合

| 架构模式 | 与 DDD 的关系 |
|----------|--------------|
| **CQRS** | 读写模型分离，与聚合配合天然 |
| **Event Sourcing** | 用事件流替代状态存储，Domain Event 是一等公民 |
| **Hexagonal / Ports & Adapters** | 保护领域层，防止基础设施泄漏 |
| **Saga / Process Manager** | 跨聚合/BC 的长流程协调 |

### 常见陷阱

- 直接从数据库表映射 Entity（贫血模型）
- Bounded Context 划分过细，变成分布式单体
- CRUD 场景用 DDD 是过度设计
- Aggregate 过大，变成"上帝对象"

### 推荐书单

| 书 | 适合阶段 |
|----|---------|
| Eric Evans《领域驱动设计》（蓝皮书）| 基础理论 |
| Vaughn Vernon《实现领域驱动设计》（红皮书）| 实践落地 |
| Scott Wlaschin《Domain Modeling Made Functional》（F#）| **函数式 DDD，强烈推荐** |
| Vlad Khononov《Learning Domain-Driven Design》| 现代视角，含大量反模式 |

---

## 2. Rust 生态：CQRS / Event Sourcing

### 主要框架

#### `cqrs-es`（最成熟）

```toml
[dependencies]
cqrs-es = "0.4"
postgres-es = "0.4"  # 或 dynamo-es, mysql-es
```

```rust
#[async_trait]
impl Aggregate for BankAccount {
    type Command = BankAccountCommand;
    type Event = BankAccountEvent;
    type Error = BankAccountError;
    type Services = BankAccountServices;

    fn aggregate_type() -> &'static str { "bank_account" }

    async fn handle(&self, command: Self::Command, _: &Self::Services)
        -> Result<Vec<Self::Event>, Self::Error>
    {
        match command {
            BankAccountCommand::Deposit { amount } =>
                Ok(vec![BankAccountEvent::Deposited { amount }])
        }
    }

    fn apply(&mut self, event: Self::Event) {
        match event {
            BankAccountEvent::Deposited { amount } => self.balance += amount,
        }
    }
}
```

#### `disintegrate`（设计更现代）

事件是全局 append-only log，Decision 只订阅自己关心的事件切片，比传统 Aggregate 更灵活。

#### EventStoreDB Rust 客户端

```toml
eventstore = "3"
```

### DDD 战术模式的 Rust 惯用写法

```rust
// Value Object → newtype
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct OrderId(Uuid);

// Repository → Trait
#[async_trait]
pub trait OrderRepository {
    async fn find(&self, id: &OrderId) -> Result<Option<Order>>;
    async fn save(&self, order: &Order) -> Result<()>;
}

// Domain Event → enum
#[derive(Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum OrderEvent {
    OrderPlaced { order_id: OrderId, total: f64 },
    OrderShipped { shipped_at: DateTime<Utc> },
    OrderCancelled { reason: String },
}
```

### 技术栈组合

```
Web Layer:    Axum / Actix-web
CQRS/ES:      cqrs-es 或 disintegrate
Event Store:  PostgreSQL (via sqlx) 或 EventStoreDB
Read Model:   SQLx + 普通查询（CQRS 读侧独立）
Message Bus:  Kafka (rdkafka) 或 NATS (async-nats)
```

---

## 3. 函数式语言选型：改写 nanobot

### nanobot 架构概览

[nanobot](https://github.com/HKUDS/nanobot) 是一个 Python 异步 agent 框架，核心：

```
Channels (Input)
    ↓
Message Bus (routing)
    ↓
Agent Loop (core execution)
    ├─ Agent Runner (LLM + tool 迭代)
    ├─ Tool System (16 种内置工具 + MCP)
    ├─ Memory (3 层：MemoryStore / Consolidator / Dream)
    └─ Session Manager (per-session JSONL 持久化)
    ↓
Outbound Messages
```

**并发模型**：asyncio + per-session Lock + 工具并行/串行批次

### 架构 → FP 模式映射

| nanobot 组件 | 当前实现 | FP 改写思路 |
|-------------|---------|------------|
| AgentLoop 状态机 | asyncio + if/elif | ADT 状态 + 穷举 pattern match |
| Per-session 并发 | asyncio Lock + dict | Actor / Mailbox（每 session = 一个 actor）|
| AgentRunner 迭代 | while loop + 变量突变 | 尾递归 / fold over state |
| Tool dispatch | ABC + 运行时 if | 类型安全 registry |
| Config / JSONL / MCP JSON-RPC | 字典 + 运行时 KeyError | Parser Combinator |
| Hook lifecycle | callback 列表 | Effect system 或 monad transformer |
| 错误恢复 | try/except | Result / Either chain（Railway）|

### 语言候选对比

#### F#（最推荐）

```fsharp
// 状态机：非法状态不可表达
type AgentState =
    | WaitingForInput
    | ProcessingMessage of SessionContext
    | ExecutingTools    of ToolBatch * PendingMessages
    | AwaitingLLM       of LLMRequest
    | Finalizing        of Response

// Per-session actor：MailboxProcessor 与 nanobot 架构天然同构
let createSessionAgent () =
    MailboxProcessor.Start(fun inbox ->
        let rec loop state = async {
            let! msg = inbox.Receive()
            let! nextState = transition state msg  // 纯函数
            return! loop nextState
        }
        loop WaitingForInput)

// Railway-oriented error handling
let runTool tool params =
    asyncResult {
        let! validated = tool.Validate params
        let! result    = tool.Execute validated
        return result
    }
```

**优势**：
- `.NET` 生态有 Telegram.Bot / Discord.Net / Slack SDK → 端到端可验证
- `MailboxProcessor` 内置 actor，与 per-session 隔离模型完全同构
- Scott Wlaschin 的 DDD 书直接用 F# 写
- `Result` chain 直接映射 AgentRunner 错误恢复

#### OCaml 5（强力备选，FP 纯度更高）

```ocaml
(* Effect system：副作用结构化控制 *)
type _ Effect.t +=
  | CallLLM    : llm_request  -> llm_response  Effect.t
  | ExecuteTool: tool_call    -> tool_result   Effect.t
  | ReadMemory : memory_key   -> string option Effect.t

(* angstrom parser combinator *)
let mcp_request =
  let open Angstrom in
  lift3 (fun id method_ params -> { id; method_; params })
    (field "id"     json_int)
    (field "method" json_string)
    (field "params" json_value)
```

**优势**：OCaml 5 Effect system 是唯一内置效应系统的主流语言
**劣势**：聊天平台 SDK 需自己用 HTTP 实现

#### Haskell（不推荐作为验证项目）

类型系统最强，但学习曲线会吃掉大部分验证时间，适合"深度学习 FP 理论"。

#### Chez Scheme — 与用户目标的根本冲突

**背景：**
- 1985 年由 R. Kent Dybvig 创建，2016 年 Cisco 开源（Apache 2.0）
- Racket 7.1（2018）起作为 Racket 的底层编译器
- 编译到原生机器码，是最快的 Scheme 实现之一
- **动态类型** — 这是与用户目标的核心冲突点

**根本矛盾：**

| 用户目标 | Chez Scheme |
|---------|------------|
| 编译期暴露错误 | ❌ 动态类型，错误在运行时 |
| 非法状态不可表达（类型层）| ❌ 无静态类型系统 |
| Type-first 设计 | ❌ Value-first，类型是二等公民 |
| Parser Combinator 生态 | ⚠️ 需用 Racket 生态（Parsack / megaparsack）|

**Chez Scheme 真正的优势（与用户目标不重叠）：**

```scheme
;; 1. Hygienic Macros — 可构建强大的 DSL
(define-syntax agent-state-machine
  (syntax-rules (state transition)
    [(agent-state-machine
       (state s1 s2 ...)
       (transition s1 -> s2 on event) ...)
     ;; 宏展开期检查状态转移合法性（但不是类型检查）
     (make-state-machine '(s1 s2 ...) '((s1 s2 event) ...))]))

;; 2. First-class Continuations — 优雅的控制流（无需 async/await）
(define (agent-loop state)
  (call-with-current-continuation
    (lambda (escape)
      (let loop ((s state))
        (let ((msg (receive-or-timeout! 1.0)))
          (if msg
            (loop (handle-message s msg))
            (escape s)))))))  ; 超时直接逃逸，无需 try/catch

;; 3. Proper Tail Calls — 无限递归不爆栈
(define (run-forever state)
  (run-forever (process-next state)))  ; 编译为循环，O(1) 栈空间
```

**Racket on Chez — 更接近用户目标的路径：**

```racket
;; Typed Racket：渐进式静态类型（运行在 Chez 上）
#lang typed/racket

(define-type AgentStatus (U 'waiting 'processing 'executing 'done))

(struct agent-state
  ([status  : AgentStatus]
   [session : String]
   [history : (Listof String)])
  #:transparent)

;; 类型错误在编译期发现
(: transition (-> agent-state String agent-state))
(define (transition s msg)
  (match (agent-state-status s)
    ['waiting (struct-copy agent-state s [status 'processing])]
    [_        (error "invalid transition")]))
```

```racket
;; Racket Contract System — 运行时不变量（替代精化类型）
#lang racket

(define/contract (create-provider api-key model)
  (->i ([key  (and/c string? (not/c ""))]   ; key 必须非空
        [model string?])
       [result provider?])                   ; 返回值必须是 provider
  (make-provider api-key model))
```

**Scheme 生态的 Parser Combinator（Racket）：**

```racket
;; megaparsack — megaparsec 的 Racket 移植
(require megaparsack data/monad)

(define mcp-request/p
  (do [id     <- (json-key/p "id"     integer/p)]
      [method <- (json-key/p "method" string/p)]
      [params <- (json-key/p "params" json/p)]
      (pure (mcp-request id method params))))
```

**何时考虑 Chez Scheme / Racket：**
- 你想**构建自己的语言或 DSL**（Racket 是最好的"语言构建平台"）
- 你对 **continuations** 和控制流抽象感兴趣（无法在其他语言轻松实现）
- 你愿意接受**渐进式类型**（Typed Racket）而非全静态类型
- 你想用 **Rosette**（基于 Racket 的形式化验证框架，见第 5 节）

**对 nanobot 改写的结论：不推荐作为主选。** 动态类型与用户"编译期暴露问题"的核心诉求根本冲突。Racket + Typed Racket 可以接受，但不如 F# / OCaml 直接。

### 语言选择决策树

```
目标是验证 FP 设计思想 + 端到端可运行
→ F#（首选）

对纯 FP 哲学更感兴趣，愿意自己实现 HTTP 集成
→ OCaml 5

把验证项目当学习项目，接受更长时间投入
→ Haskell

想构建自己的语言/DSL，或对 continuations 感兴趣
→ Racket on Chez Scheme（但要放弃编译期类型保证）
```

### 建议验证路径（F#）

| 周次 | 目标 |
|------|------|
| 第 1 周 | 读《Domain Modeling Made Functional》前三章，用 F# 类型建模 5 个核心抽象 |
| 第 2 周 | 实现 AgentState 状态机 + MailboxProcessor session actor |
| 第 3 周 | 用 FParsec 写 MCP JSON-RPC 解析器 + OpenAI API response 解析 |
| 第 4 周 | 接入 Telegram.Bot，端到端跑一个完整 agent 对话 |

---

## 4. 各平台运行时分析

### 4.1 F# / .NET

#### 核心概念

```
JIT (Just-In-Time)  — 运行时编译 IL → 机器码，灵活但需要完整运行时
AOT (Ahead-Of-Time) — 编译期直接生成机器码，体积小但有功能限制
NativeAOT           — .NET 8+ 完全静态编译，单一原生二进制
```

#### Linux ✅ 最佳支持

- 运行时：.NET 8+ LTS
- 架构：x64 / arm64 / arm32 / RISC-V（预览）
- libc：glibc（Ubuntu/Debian/RHEL）或 musl（Alpine，.NET 8+）
- 所有 F# 功能完整支持
- NativeAOT 发布为单一无依赖二进制：

```bash
dotnet publish -r linux-x64 -p:PublishAot=true
# 输出：~15MB 原生二进制，无需安装 .NET
```

#### macOS ✅ 完整支持

- 运行时：.NET 8+，最低 macOS 12（Monterey）
- Apple Silicon 原生支持（arm64，非 Rosetta）
- Universal Binary（x64 + arm64）支持
- App Store 分发有沙盒限制（文件系统/网络权限）

#### Windows ✅ 完整支持（最成熟）

- 运行时：.NET 8+
- 架构：x64 / x86 / arm64
- 所有 .NET 功能，无任何限制

#### Android ⚠️ 有限支持

运行时：.NET MAUI for Android，最低 Android 5.0（API 21）

| 特性 | Debug | Release（发布）|
|------|-------|--------------|
| JIT | ✅ | ❌（需 AOT）|
| `Reflection.Emit` | ✅ | ⚠️ 受限 |
| F# DU / 模式匹配 | ✅ | ✅ |
| F# 计算表达式 | ✅ | ✅ |
| F# Type Providers | ❌ | ❌ |
| FParsec | ✅ | ✅ |
| System.Text.Json | ✅ | ⚠️ 需 source generator |

#### iOS ❌ 限制最多

Apple 政策：App Store **禁止 JIT**，强制全量 AOT。

| F# 特性 | iOS 可用性 |
|---------|-----------|
| Discriminated Unions | ✅ |
| Pattern matching | ✅ |
| 计算表达式（async, result）| ✅ |
| FParsec | ✅ |
| **Type Providers** | ❌ 完全不可用 |
| FSharp.Quotations | ⚠️ 仅静态 quotation |
| JSON with reflection | ⚠️ 需 source generator |

#### F# 平台汇总

| 平台 | JIT | 完整 F# | Agent 本体 | 备注 |
|------|-----|---------|-----------|------|
| **Linux** | ✅ | ✅ | ✅ 最优 | NativeAOT 单二进制 |
| **macOS** | ✅ | ✅ | ✅ | Apple Silicon 原生 |
| **Windows** | ✅ | ✅ | ✅ | 最成熟生态 |
| **Android** | Release 无 | 90% | ❌ 客户端只 | 后台执行受限 |
| **iOS** | ❌ Full AOT | 80% | ❌ 客户端只 | 无 Type Provider |

**结论**：Agent 本体跑在 Linux/macOS/Windows，移动端做 UI 客户端通过 REST/WebSocket 连接服务端。

---

### 4.2 Chez Scheme / Racket

#### 运行时特征

```
编译模型：源码 → 原生机器码（非字节码解释）
GC：世代式 GC，停顿低
尾调用：完全支持 Proper Tail Calls（规范保证，非优化）
Continuations：一等公民，保存完整运行时状态
类型：动态类型（运行时标签检查）
线程：POSIX threads（Unix）/ Windows threads
```

#### Linux ✅ 最佳支持

```bash
# 发行版包管理器
apt install chezscheme        # Ubuntu/Debian
brew install chezscheme        # macOS（同样适用）

# 或从源码编译
git clone https://github.com/cisco/ChezScheme
./configure && make && make install
```

- 架构：x86-64 ✅、arm64 ✅、arm32 ✅、RISC-V（实验性）
- 所有 Scheme 特性完整支持
- 单可执行文件：`--program` 模式打包 scheme + 依赖

#### macOS ✅ 良好支持

- x86-64 ✅（Intel）
- arm64 ✅（Apple Silicon，原生，非 Rosetta）
- 通过 Homebrew 安装即可，无需配置

#### Windows ✅ 支持（但不如 Linux/macOS 成熟）

- x86-64 ✅
- 需要 MSVC 运行时
- 功能完整，但社区工具链偏向 Unix

#### Android ❌ 无官方支持

- 无官方移植
- 理论上可交叉编译，但需要 JIT/AOT 权限（Android 5+ 限制）
- Racket on Android 有实验性项目，但不可用于生产

#### iOS ❌ 完全不支持

- Chez Scheme 需要 JIT（运行时代码生成）
- Apple 的 App Store 政策禁止 JIT → **根本无法运行**
- Continuation 实现依赖栈操作，与 iOS 沙盒冲突

#### Chez Scheme vs F# 平台汇总

| 平台 | F# / .NET | Chez Scheme | Racket |
|------|-----------|-------------|--------|
| **Linux** | ✅ NativeAOT | ✅ 原生编译 | ✅ |
| **macOS** | ✅ arm64 原生 | ✅ arm64 原生 | ✅ |
| **Windows** | ✅ 最成熟 | ✅ 可用 | ✅ |
| **Android** | ⚠️ MAUI（受限）| ❌ | ❌ |
| **iOS** | ⚠️ MAUI（受限）| ❌ JIT 禁止 | ❌ |
| **部署体积** | ~15MB NativeAOT | ~2MB | ~30MB |
| **启动时间** | 快（NativeAOT）| 极快 | 中等 |

**结论**：桌面/服务端用途两者相当；移动端 F# 勉强可用（MAUI），Chez Scheme 完全不可用。

---

## 5. 形式化验证工具谱系

### 理论基础：Curry-Howard 同构

```
命题（Proposition）  ←→  类型（Type）
证明（Proof）        ←→  程序（Program）
命题为真             ←→  类型有居民（有值存在）
```

类型定义本身就是规约，类型检查就是证明验证。关键在于类型系统有多强。

### 层 1：属性测试（Property-Based Testing）

成本最低，自动从类型生成测试用例，找反例。

```fsharp
// F# + FsCheck
let ``session state transitions are valid`` (state: AgentState) =
    match state with
    | ExecutingTools(batch, _) -> not (List.isEmpty batch)
    | _ -> true

Check.Quick ``session state transitions are valid``
// 自动生成 AgentState 随机值，找反例
```

**能发现**：运行时不变量违反、边界条件
**不能保证**：穷举所有情况

### 层 2：精化类型（Refinement Types）

类型 + 谓词，SMT 求解器自动验证，无需手写证明。

#### Liquid Haskell

```haskell
-- 精化类型：谓词在类型里，Z3 验证
{-@ div :: Int -> {v: Int | v /= 0} -> Int @-}
div x y = x `div` y

-- 合法状态转移编码在类型里
{-@ type ValidTransition S1 S2 =
      {v: () | legalTransition S1 S2} @-}
```

#### F*（微软研究院）

```
val parse_config :
  s: string ->
  Tot (result: config{well_formed result})
  // Tot = 全函数（终止），result 满足 well_formed 谓词

val call_llm :
  cfg: config{cfg.api_key <> ""} ->  // 前置条件
  ST response
    (requires fun h -> network_available h)
    (ensures  fun h0 r h1 -> valid_response r)
```

**F* 可提取为 F# / OCaml / C 代码**，验证后直接使用。

### 层 3：依赖类型（Dependent Types）

类型可以依赖值，类型 = 定理，程序 = 证明。

#### Idris 2（类型驱动开发的代表语言）

```idris
-- 长度在类型里编码，长度不匹配 → 编译错误
zip : Vect n a -> Vect n b -> Vect n (a, b)

-- Agent 状态机合法转移编码在类型里
data ValidTransition : AgentState -> AgentState -> Type where
  StartProcessing : ValidTransition Waiting Processing
  StartTools      : ValidTransition Processing ExecutingTools
  FinishTools     : ValidTransition ExecutingTools Processing
  Finalize        : ValidTransition Processing Done

-- 只有 ValidTransition 证明存在，才能调用 transition
transition : (s1: AgentState) -> ValidTransition s1 s2 -> IO (Agent s2)
```

**Hole-Driven Development**：写类型，编译器告诉你需要实现什么：

```idris
processMessage : Session -> Message -> IO Session
processMessage session msg = ?processMessage_rhs
-- 编译器：?processMessage_rhs 类型是 IO Session
-- 并列出当前作用域所有可用值
```

#### Lean 4（数学证明 + 编程语言）

```lean
-- 证明 parser round-trip 正确性
theorem parse_serialize_roundtrip (cfg : Config) :
    parse (serialize cfg) = some cfg := by
  cases cfg; simp [serialize, parse]

-- 证明 agent 状态机无死锁
theorem no_deadlock (s : AgentState) :
    ∃ s', CanTransition s s' ∨ s = Done := by
  cases s <;> simp [CanTransition]
```

### 层 4：模型检测（Model Checking）

#### Dafny（微软，自动验证，编译到多语言）

```dafny
method ParseConfig(json: string) returns (cfg: Config)
  requires json != ""                   // 前置条件
  ensures cfg.apiKey != ""             // 后置条件
  ensures ValidConfig(cfg)             // 返回值满足不变量

class SessionManager {
  var sessions: map<string, Session>
  var activeSessions: set<string>

  invariant activeSessions <= sessions.Keys  // 自动验证类不变量
}
```

Dafny 验证后可编译到 C# / Go / Rust / Python。

#### Rosette（基于 Racket / Chez Scheme 的符号执行框架）

```racket
#lang rosette/safe

;; 符号执行：用符号值代替具体值，让 Z3 穷举所有可能
(define-symbolic session-count integer?)
(define-symbolic msg-type (bitvector 8))

;; 验证：agent 在任意消息类型下不会进入非法状态
(verify
  #:assume  (assert (and (>= session-count 0) (< session-count 100)))
  #:guarantee (assert (valid-agent-state?
                        (run-agent-step init-state msg-type))))
;; 如果 Z3 找到反例 → 报告具体的 session-count 和 msg-type 值

;; 综合（Synthesis）：让 Z3 自动生成满足规约的实现
(define-symbolic choice integer?)
(synthesize
  #:forall    (list msg-type)
  #:guarantee (assert (correct-dispatch? (choose-handler choice) msg-type)))
```

**Rosette 独特能力**：不只是验证，还能**反向合成**满足规约的程序片段。代价是只能在 Racket 生态内使用，且符号执行范围受限（不适合大规模代码库）。

### 工具对比矩阵

| 工具 | 学习曲线 | 自动程度 | 适用场景 | 与 agent 项目关系 |
|------|---------|---------|---------|-----------------|
| **FsCheck** (F#) | 低 | 自动生成测试 | 快速找反例 | 直接用于 F# agent |
| **Liquid Haskell** | 中 | SMT 自动验证 | Haskell 代码精化 | Haskell 路线 |
| **F*** | 高 | SMT 半自动 | 安全关键代码 | 可提取为 F# |
| **Dafny** | 中 | SMT 全自动 | 算法正确性 | 提取为 C# |
| **Rosette** (Racket) | 中 | 符号执行 + 合成 | 验证 + 自动生成实现 | Racket 路线 |
| **Idris 2** | 高 | 类型引导实现 | 类型驱动开发 | 学习/验证用 |
| **Lean 4** | 很高 | 证明助手 | 数学级证明 | 研究方向 |

### 对 agent 框架能验证什么

| 验证目标 | 推荐工具 |
|---------|---------|
| 状态机合法性（无非法转移）| Idris 2 / F* 依赖类型 |
| Parser round-trip 正确性 | Liquid Haskell / F* |
| 配置不变量（api_key 非空才能创建 Provider）| Dafny / F* |
| Session 并发安全 | F* effect system |
| Tool 参数合法性（范围约束）| 精化类型 |

---

## 6. 推荐学习路径

### 短期（验证项目阶段）

```
F# + FsCheck
→ 属性测试，成本最低，立即可用
→ 覆盖 80% 的收益
```

### 中期（理解依赖类型）

```
《Type-Driven Development with Idris》（Edwin Brady）
→ 用 Idris 2 把 agent 状态机建模
→ 感受类型引导实现的开发体验
```

### 长期（形式化验证）

```
F* 精化类型 → 提取为 F# 使用
或
Dafny → 提取为 C#
```

### 核心原则回顾

> **在设计期就把错误的状态在数据结构上表达为不可能，
> 在编译期把尽可能多的问题暴露，
> 用 parser 把无序的二进制/JSON 表达为 type 来尽可能早暴露问题。**

对应工具链：
- **不可能状态** → ADT + 穷举模式匹配（F# / OCaml / Haskell）
- **编译期暴露** → 精化类型（F*）/ 依赖类型（Idris 2）
- **Parser 边界** → Parser Combinator（FParsec / angstrom / megaparsec / megaparsack）+ 类型驱动解析
- **Chez Scheme 的位置** → 动态类型，不满足编译期保证；但 Racket + Typed Racket + Rosette 提供渐进式替代路径；适合"构建语言平台"而非"类型安全 agent"
