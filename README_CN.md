# BotSharp

[English](README.md)

BotSharp 是一个使用 F# 编写的类型驱动 AI Agent 框架。它是 Python 版 [nanobot](https://github.com/nano-bot/nanobot) 的 F# 移植，基于两个核心设计原则从零重建：

1. **类型驱动设计** — 让非法状态在编译期不可表达
2. **解析而非验证** — 在系统边界使用 [FParsec](https://www.quanttec.com/fparsec/) 解析器组合子，数据一旦进入领域层即已正确

## 特性

- **多模型供应商** — OpenAI、Anthropic (Claude)、DeepSeek、Groq、通义千问 (DashScope)、Moonshot (Kimi)、MiniMax、智谱 (GLM)、硅基流动、AiHubMix、Ollama
- **SSE 流式输出** — 基于 Server-Sent Events 的实时逐 token 输出
- **工具系统** — 文件读写、Shell 执行（可沙箱隔离）、网页抓取/搜索、定时任务、MCP 服务器集成、Notebook 编辑、子 Agent 派生
- **多通道接入** — CLI、Telegram、WebSocket、OpenAI 兼容 HTTP API
- **会话管理** — 基于 MailboxProcessor 的 Actor 模型，每会话独立，自动记忆整合
- **技能系统** — 基于工作区 SKILL.md 的技能加载器，支持依赖检查和内置默认技能
- **心跳服务** — 周期性自主后台任务
- **记忆整合 (Dream)** — 自动从对话历史中提炼长期记忆
- **六边形架构** — Domain / Application / Infrastructure 三层，依赖方向清晰

## 环境要求

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## 快速开始

### 1. 克隆并构建

```bash
git clone https://github.com/pachulisk/botsharp.git
cd botsharp
dotnet build src/BotSharp.sln
```

### 2. 运行

```bash
dotnet run --project src/BotSharp/BotSharp.fsproj
```

首次运行时，BotSharp 会启动交互式设置向导，引导你完成：

- 选择 LLM 供应商（OpenAI、Anthropic、DeepSeek 等）
- 输入 API Key
- 选择默认模型

配置保存在 `~/.botsharp/config.json`。

### 3. 命令行参数

```
--model <name>       覆盖默认模型
--workspace <path>   覆盖工作区目录
--api-port <port>    启动 OpenAI 兼容 HTTP API 服务
--ws-port <port>     启动 WebSocket 服务
```

示例 — 使用 Claude 模型并开启 API 服务：

```bash
dotnet run --project src/BotSharp/BotSharp.fsproj -- --model claude-sonnet-4-20250514 --api-port 8080
```

### 4. 配置文件

直接编辑 `~/.botsharp/config.json`，或删除该文件后重新运行以触发设置向导。核心字段：

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

### 5. 工作区

BotSharp 使用 `~/.botsharp/workspace/` 存储持久化状态：

```
~/.botsharp/workspace/
  SOUL.md          # Agent 身份与性格设定
  AGENTS.md        # 子 Agent 定义
  USER.md          # 用户画像（自动填充）
  TOOLS.md         # 工具使用指南
  HEARTBEAT.md     # 周期性任务指令
  memory/
    MEMORY.md      # 长期记忆（自动整合）
    HISTORY.md     # 对话历史日志
  skills/          # 已安装的技能定义（SKILL.md）
```

## 运行测试

```bash
dotnet test src/BotSharp.Tests/BotSharp.Tests.fsproj
```

共 2041 个测试，覆盖领域逻辑、解析器、工具实现和应用层。

## 架构

```
src/BotSharp/
  Domain/              # 纯类型、状态机、错误 DU — 零外部依赖
  Application/         # AgentLoop、SessionActor、MemoryConsolidator、ContextBuilder
  Infrastructure/
    Config/            # 基于 FParsec 的配置解析器 + JSON 序列化
    Providers/         # OpenAI 兼容 SSE 适配器、供应商注册表
    Channels/          # CLI、Telegram、WebSocket、API 通道适配器
    Tools/             # 文件、Shell、Web、定时、MCP、派生、Notebook 工具
    Skills/            # 技能加载器 + 内置默认技能
    Storage/           # JSONL 会话存储、记忆存储、定时任务存储
    Shared/            # AsyncResult 计算表达式、JSON 工具、字符串工具
  Program.fs           # 入口点、依赖注入、工作区引导
```

## 许可证

MIT
