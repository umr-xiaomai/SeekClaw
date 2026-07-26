# SeekClaw CLI 工程开发规范（优化版）

> **目标**
>
> 构建一个类似 Claude Code 的工业级 AI Agent Runtime，而不是简单的聊天
> CLI。 Runtime 为核心，CLI 只是默认前端，未来可扩展 GUI、Web、移动端。

## 核心原则

-   Runtime First
-   Plugin First
-   Interface First
-   Event Driven
-   Configuration First
-   Clean Architecture
-   Vertical Slice Architecture
-   SOLID
-   Native AOT Friendly
-   高内聚、低耦合、可测试、可扩展

所有业务能力必须位于 Runtime，不允许耦合 CLI。

------------------------------------------------------------------------

## Runtime 架构

CLI → Runtime → Agent → Tool → LLM

未来 GUI、Web、桌面端统一调用 Runtime。

支持：

-   Daemon 模式
-   Windows Named Pipe
-   Unix Socket
-   Session 恢复
-   Workspace
-   Memory
-   Plugin

------------------------------------------------------------------------

## 插件体系

系统采用 Plugin First。

统一支持：

-   Tool
-   Skill
-   MCP

全部通过 Registry 管理。

新增能力优先通过插件扩展，不允许修改 Runtime 核心。

------------------------------------------------------------------------

## Skill

Skill 是 Agent 能力扩展，而不是 Tool。

支持：

-   Prompt 注入
-   Workflow
-   Hook
-   Memory
-   Tool 注册
-   Context 注入

支持目录化安装、启用、禁用、更新、热加载。

------------------------------------------------------------------------

## MCP

原生支持 MCP Client。

支持：

-   stdio
-   SSE

预留：

-   HTTP
-   WebSocket

自动发现：

-   Tool
-   Prompt
-   Resource

------------------------------------------------------------------------

## Workspace

每个项目独立：

-   Config
-   Session
-   Cache
-   Memory
-   MCP
-   Skill

自动识别 Git、.NET、Node、Python、Rust 等项目。

建立增量索引。

------------------------------------------------------------------------

## Context

采用智能 Context Builder。

优先：

1.  用户指定
2.  当前文件
3.  最近修改
4.  Git Diff
5.  Search
6.  Symbol
7.  Summary

禁止一次发送整个项目。

------------------------------------------------------------------------

## 验证

修改代码后自动：

-   Build
-   Check
-   Retry
-   Repair

直到成功或达到最大重试次数。

------------------------------------------------------------------------

## Project Bootstrap

初始化时自动读取：

`seekclaw_cli/seekclaw_cli.csproj`

分析：

-   SDK
-   TargetFramework
-   PropertyGroup
-   PackageReference
-   ProjectReference

根据 NuGet 自动推断能力，禁止重复引入已有依赖。

自动生成：

-   .gitignore
-   .cache/
-   .session/
-   logs/
-   skills/
-   mcp/
-   docs/

最后自动：

-   dotnet restore
-   dotnet build

确保项目可编译。

------------------------------------------------------------------------

# Terminal UX（重点）

SeekClaw CLI 的终端体验必须达到 Claude Code、OpenAI Codex CLI、Gemini
CLI 的现代交互水准。

要求：

-   实时
-   流畅
-   无闪烁
-   增量刷新
-   Streaming
-   高可读性
-   专业、克制

禁止：

-   Console.WriteLine() 堆日志
-   一次性输出全部内容
-   阻塞等待
-   每一步新增大量新行

必须采用：

-   Event Driven Rendering
-   Streaming Rendering
-   Live UI
-   Incremental Update

支持：

-   Thinking
-   Spinner
-   Tool Call
-   Diff
-   Markdown
-   Code Highlight
-   Progress
-   Token Streaming
-   实时状态更新

Agent 每一步都必须实时反馈，例如：

-   Thinking
-   Reading Files
-   Searching
-   Editing
-   Building
-   Verifying

完成后自动更新状态，而不是新增日志。

------------------------------------------------------------------------

## Terminal 架构（必须遵守）

**不要把 Terminal UI 和 Agent 执行放在同一个线程。**

渲染层必须维护独立的 **Render Loop**（建议 **30～60 FPS**）。

架构：

Agent（业务线程） ↓ Event Bus ↓ Render Queue ↓ Terminal
Renderer（独立渲染线程） ↓ Console

要求：

-   Agent 线程只负责业务执行
-   Runtime 只发布事件
-   Render Queue 合并高频事件
-   Renderer 增量刷新终端
-   不允许 Runtime 直接 Console 输出
-   不允许 Tool 直接输出 Console
-   不允许 LLM Provider 输出 Console

渲染层负责：

-   Spinner
-   Thinking
-   Tool 状态
-   Streaming
-   Progress
-   Markdown
-   Diff
-   Status Bar

采用双缓冲或等效策略减少闪烁。

高频事件必须合并刷新，避免滚屏。

终端大小变化时自动重新布局。

Ctrl+C：

第一次取消当前任务；

第二次退出程序。

整个渲染系统应接近游戏 UI 的刷新模型，而不是传统命令行输出。

------------------------------------------------------------------------

## 输出要求

第一阶段仅输出：

-   架构设计
-   Mermaid
-   模块划分
-   生命周期
-   接口设计
-   开发计划

等待确认后，再按模块逐步开发。

所有模块必须：

-   可独立编译
-   可单元测试
-   易维护
-   易扩展
-   工业级质量
