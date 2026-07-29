# 构建验证与自动自我修复 (Build Verification & Auto-Repair)

传统的 AI 编码助手最常见的问题是："看似修改了代码，但生成的内容包含语法错误或破坏了现有的单元测试，需要人工打断并手工修错"。

SeekClaw 引入了 **BuildVerifier 闭环与自愈修复循环（Auto-Repair Loop）**。

---

## 自愈闭环工作流

```mermaid
flowchart TD
    A[Agent 产生文件修改] --> B{工作区配置了 AutoVerify?}
    B -- 否 --> C[完成本 Turn 任务]
    B -- 是 --> D[BuildVerifier 自动执行项目构建/测试命令]
    D --> E{构建/测试是否成功?}
    E -- 成功 --> C
    E -- 失败 --> F{尝试修复次数 < MaxRepairAttempts?}
    F -- 超出限制 --> G[终止循环，返回完整报错日志给用户]
    F -- 允许重试 --> H[抽取编译 Error & StackTrace]
    H --> I[注入 builtin/repair 专有 Prompt 模板]
    I --> A
```

---

## 验证策略配置选项

全局或工作区 `.seekclaw/config.json` 中可配置自愈参数：

```json
"agent": {
  "autoVerify": true,
  "maxRepairAttempts": 3,
  "verificationCommand": "dotnet build seekclaw_tests"
}
```

### 自动识别逻辑：
如果未显式提供 `verificationCommand`，SeekClaw 会根据 `WorkspaceManager` 检测到的项目类型智能推导默认验证命令：
- `.NET`: `dotnet build`
- `Rust`: `cargo check`
- `Node.js`: `npm run build` 或 `pnpm run check`
- `Python`: `pytest`

---

## 防幻觉与无掩盖原则

SeekClaw 的 `BuildVerifier` 遵循严肃的技术规范：

- **拒绝静默吞异常**：如果构建超时或报错，必须完整提取编译器打出的标准错误输出 (stderr) 与绝对行号。
- **专有 Repair 提示**：自愈 Prompt 中包含精确指令，禁止 Agent 通过删除单元测试、注释断言或返回伪装 Dummy 数据来“掩盖”构建失败。
- **有界退避**：达到 `MaxRepairAttempts`（默认 3 次）后立即停止，向开发者呈现真实的错误上下文，避免死循环消耗 Token。
