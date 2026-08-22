# 自定义 Skill 插件开发与发布

Skill 是 SeekClaw 的模块化扩展单元。一个 Skill 插件可以包含专业提示词指令、专用工具链、自动化脚本以及工作流定义，使用户能够一键扩展 Agent 的领域专业能力（例如：代码审查大师、SQL 优化器、Docker 编排专家等）。

---

## 1. Skill 标准目录结构

一个标准 SeekClaw Skill 插件在文件系统中为一个独立目录（或 Zip 安装包）：

```text
my-awesome-skill/
├── SKILL.md             # [必需] 核心元数据定义与主指令文件
├── prompts/             # [可选] 专用 Prompt 模板目录
│   └── review-rule.txt
├── scripts/             # [可选] 辅助可执行脚本（Bash / PowerShell / Python）
│   └── analyze.py
└── README.md            # [可选] 市场详情页展示文档
```

---

## 2. 编写 SKILL.md 核心文件

`SKILL.md` 文件采用标准 YAML Frontmatter + Markdown 正文结构：

```markdown
---
name: "Docker Master"
slug: "docker-master"
version: "1.0.0"
author: "YourName"
summary: "专业 Dockerfile 审查、Compose 多容器编排与镜像体积深度优化专家。"
tags: ["docker", "devops", "container", "optimization"]
homepage: "https://github.com/yourname/docker-master"
---

# Docker Master 指令规范

你是一个顶级的容器化与 DevOps 专家。当用户要求编写或优化 Docker 资产时，请遵循以下原则：

## 1. 镜像构建与瘦身
- 必须优先使用多阶段构建（Multi-stage build）。
- 运行时阶段必须采用 Alpine 或 Distroless 极简基础镜像。
- 合并 RUN 指令以减少镜像层数，并在同层清理包管理器缓存。

## 2. 安全与权限
- 绝对禁止使用 root 用户运行生产应用，必须创建并切换至专门的无特权用户。
- 敏感配置（如连接字符串、私钥）禁止硬编码入镜像，必须通过环境变量或 Secret 挂载。
```

### Frontmatter 字段详解

| 字段 | 类型 | 是否必需 | 说明 |
| :--- | :--- | :--- | :--- |
| `name` | string | **是** | 显示名称（如 `Docker Master`） |
| `slug` | string | **是** | 唯一英文标识符，只能包含小写字母、数字与中划线（如 `docker-master`） |
| `version` | string | **是** | 语义化版本号（如 `1.0.0`） |
| `author` | string | **是** | 作者名称 |
| `summary` | string | **是** | 简短描述（一到两句话，展示在市场列表） |
| `tags` | string[]| 否 | 检索标签 |
| `homepage`| string | 否 | 开源仓库或项目主页链接 |

---

## 3. 本地调试与测试

在发布前，将你的 Skill 文件夹放置在用户技能目录即可直接在 CLI 或 Desktop 中加载：

- **Windows**: `%USERPROFILE%\.seekclaw\skills\my-awesome-skill\`
- **Linux / macOS**: `~/.seekclaw/skills/my-awesome-skill/`

放置后在终端中执行：

```bash
# 查看本地已加载的 Skills 列表
seekclaw skills list

# 验证特定 Skill 是否正确解析
seekclaw doctor
```

---

## 4. 打包与发布到官方技能市场

### 方式一：通过 Web 平台可视化发布（推荐）
1. 访问 SeekClaw 技能市场发布页：[/skills/submit](/skills/submit)。
2. 登录开发者账号。
3. 填写基本信息或直接上传打包好的 `.zip` 文件。
4. 提交后系统会自动进行元数据校验并发布上线。

### 方式二：CLI 打包
将插件目录打包为标准 `.zip` 压缩包（注意 `SKILL.md` 必须位于 zip 根目录），通过 API 或管理员审核后即可进入公共生态市场供全球开发者使用：

```bash
seekclaw skill install <your-skill-slug>
```
