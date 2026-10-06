# CI/CD 与无头模式自动化集成

SeekClaw 不仅支持图形化桌面和交互式终端，还支持在持续集成（CI/CD）流水线与自动化运维脚本中通过 **无头模式（Headless Mode）** 运行。

---

## 1. 单次非交互运行指令（One-Shot CLI）

在自动化流水线或脚本中，直接向 `seekclaw` 传入提示词即可进入单次执行模式，执行完成后输出结果并退出（0 表示成功，非 0 表示失败）：

```bash
# 单次执行指令并自动返回退出码
seekclaw "检查本次变更的代码格式并修复编译警告"

# 临时覆盖使用指定模型进行审查
seekclaw --model "openai/gpt-5.5" "审查当前代码并输出安全风险建议"
```

---

## 2. GitHub Actions 集成实战

在 GitHub 仓库的 `.github/workflows/ai-review.yml` 中添加自动化 PR 评审工作流：

```yaml
name: SeekClaw AI Code Review

on:
  pull_request:
    branches: [ main, develop ]

jobs:
  review:
    runs-on: windows-latest
    steps:
      - name: Checkout Code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: 20

      - name: Install SeekClaw CLI
        run: npm install -g seekclaw-cli

      - name: Configure Provider
        run: |
          seekclaw provider add --id deepseek --kind openai --base-url "https://api.deepseek.com/v1" --api-key "${{ secrets.DEEPSEEK_API_KEY }}" --model "deepseek-chat"
          seekclaw model use "deepseek/deepseek-chat"

      - name: Run SeekClaw Code Audit
        run: |
          seekclaw "分析当前 git diff 涉及的代码变更，检查潜在并发与内存泄漏隐患，以 Markdown 格式输出审查报告" > review-report.md

      - name: Post PR Comment
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const body = fs.readFileSync('review-report.md', 'utf8');
            github.rest.issues.createComment({
              issue_number: context.issue.number,
              owner: context.repo.owner,
              repo: context.repo.repo,
              body: `### 🤖 SeekClaw 自动化代码审查报告\n\n${body}`
            });
```

---

## 3. Docker 容器化部署

SeekClaw 可作为独立 Worker 节点或常驻 Daemon 运行于容器环境中：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /app
COPY . .
RUN dotnet publish seekclaw_cli -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine
WORKDIR /seekclaw
COPY --from=build /out .
ENTRYPOINT ["./seekclaw", "daemon"]
```

挂载持久化目录与代码工作区：

```bash
docker run -d \
  --name seekclaw-worker \
  -v ~/.seekclaw:/root/.seekclaw \
  -v /var/repos/project:/workspace \
  seekclaw-worker
```

---

## 4. 退出码与错误处理

在 CI 脚本中，可以通过退出码判断 Agent 执行状态：
- `0`：任务成功完成，且验证通过。
- `1`：模型执行失败或构建验证未能完全修复。
- `2`：环境或网络配置异常（如 API Key 失效、模型不可达）。
