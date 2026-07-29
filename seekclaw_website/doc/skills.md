# 技能插件体系 (Skills)

SeekClaw 包含**基于目录的技能（Skill）系统**。技能是一种轻量化、模块化的扩展包，包含定制化的 System Prompt 注入、工作流规划指引以及特定领域的工具使用范式。

---

## 技能文件目录结构

一个标准的 SeekClaw 技能包组织如下：

```
skills/
  code-review/
    skill.yaml          # 技能元数据与触发条件
    prompt.txt          # 核心 Prompt 模板
    tools/              # (可选) 技能专属工具实现
```

---

## `skill.yaml` 配置说明

```yaml
name: code-review
version: 1.0.0
description: 专门用于代码评审、代码风格检查与潜在性能陷阱扫描的领域技能
enabled: true
tags:
  - refactor
  - quality

# 当用户输入包含特定关键字时可自动激活或推荐该 Skill
triggers:
  - "review"
  - "代码审查"
  - "重构建议"

# Prompt 注入优先级 (Priority 越高越优先覆盖)
priority: 10
```

---

## `prompt.txt` 模板与变量

`prompt.txt` 中编写要注入 Agent 上下文的指导规则。支持动态变量插值：

```markdown
你现在是团队中资深的代码审查专家 (Code Reviewer)。

评审要求：
1. 检查当前工作区 ({{workspace}}) 中的修改代码。
2. 确保符合 SOLID 设计原则。
3. 检查是否有未处理的 NullReferenceException 风险或锁死问题。
4. 提供简洁、按优先级排序的修改建议。

当前运行时间：{{datetime}}
环境系统：{{os}} / {{platform}}
```

---

## 技能作用域与加载层级

SeekClaw 按照以下三级优先级加载技能：

1. **工作区特定技能** (`<workspace>/.seekclaw/skills/`)：最高优先级，仅对当前项目生效。
2. **全局用户技能** (`~/.seekclaw/skills/`)：对该机器上所有项目生效。
3. **系统内置技能** (`seekclaw_runtime` 自带)：默认垫底备份技能。

---

## 命令行管理 Skill

```bash
# 查看所有已发现的技能及其状态
seekclaw skill list

# 启用特定技能
seekclaw skill enable code-review

# 禁用特定技能
seekclaw skill disable legacy-migration
```
