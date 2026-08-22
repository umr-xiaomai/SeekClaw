# AGENTS.md 规范与提示词工程

SeekClaw 采用分层式的提示词与工程规范注入机制，允许团队通过代码库中的 `AGENTS.md` 文件和持久化 `MEMORY.md` 知识库，对 AI Agent 的行为、代码规范、架构红线进行精细化对齐。

---

## 1. AGENTS.md 规范体系

`AGENTS.md` 是人类开发者向 AI Agent 下达工作指令、代码风格约束与架构规则的标准入口。

### 层级覆盖机制（Directory Scoping）
- **就近原则**：`AGENTS.md` 可以放置在仓库的任意子目录中。
- **作用域继承**：子目录中的 `AGENTS.md` 会自动继承上层目录的规则，并在发生规则冲突时，**更深层级的规范优先于浅层规范**。
- **最高优先级**：系统预设指令与用户即时输入的指令，始终高于 `AGENTS.md` 中的建议。

### 推荐编写模板

```markdown
# AGENTS.md — 核心业务与架构规范

## 技术栈与版本
- 运行环境：.NET 10.0 (C# 13)
- 数据库：SQLite + Dapper
- 单元测试：xUnit + Moq

## 代码风格与红线
- 严格禁止在业务控制器中编写原生 SQL，所有数据访问必须通过 Repository 接口。
- 所有公共异步方法必须接受 `CancellationToken ct = default` 并向下传递。
- 新增领域模型必须提供 XML 格式注释。

## 验证与测试命令
- 单元测试运行命令：`dotnet test seekclaw_tests`
- 代码格式化检查：`dotnet format --verify-no-changes`
```

---

## 2. 长期工作区记忆（MEMORY.md）

在长生命周期项目中，Agent 需要记住跨会话的关键决策、历史坑点和特殊配置。

### 记忆生命周期
- **位置**：存于 `<workspace>/.seekclaw/MEMORY.md`。
- **自动挂载**：在每次初始化 System Prompt 时，运行时会自动将 `MEMORY.md` 裁剪并注入到上下文末尾。
- **更新策略**：Agent 可在解决重大疑难问题后，主动通过工具更新 `MEMORY.md` 沉淀经验。

---

## 3. 动态系统变量清单

自定义 Prompt 模板时支持引用以下内置双花括号变量：

| 变量名 | 含义说明 | 示例值 |
| :--- | :--- | :--- |
| `{{workspace}}` | 当前工作区绝对路径 | `D:\Projects\MyApp` |
| `{{project}}` | 项目名称 | `MyApp` |
| `{{language}}` | 自动探测的技术栈语言 | `dotnet, csharp` |
| `{{os}}` | 当前操作系统平台 | `Windows 11 (win-x64)` |
| `{{tool}}` | 当前启用的工具名称列表 | `read_file, edit_file, bash` |
| `{{mode}}` | 当前运行模式 | `edit` / `plan` |
| `{{agents_md}}` | 注入的 AGENTS.md 内容 | *(提取的内容文本)* |
| `{{memory}}` | 注入的 MEMORY.md 内容 | *(提取的内容文本)* |

---

## 4. 团队提示词最佳实践

1. **避免模糊指令**：使用明确的动词与约束，例如：“使用 FluentValidation 进行入参校验，不要手写 if-else 异常判断”。
2. **给出正确/错误范式（Few-shot）**：在 `AGENTS.md` 中展示一段推荐的代码片段与反例，模型的遵从度可提高 90% 以上。
3. **保持规则精简聚焦**：`AGENTS.md` 单文件建议控制在 500 行以内，将通用规范放于根目录，模块专有规范下沉至具体子目录。
