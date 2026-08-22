# CI/CD 与无头模式自动化集成

SeekClaw 不仅支持图形化桌面和交互式终端，还支持在持续集成（CI/CD）流水线与自动化运维脚本中通过 **无头模式（Headless Mode）** 运行。

---

## 1. 无头运行命令（Headless CLI）

在自动化流水线中，通过 `seekclaw run` 配合非交互参数直接执行目标指令：

```bash
# 单次无头执行指令并返回退出码（0 表示成功，非 0 表示失败）
seekclaw run "检查本次变更的代码格式并修复编译警告" --headless

# 指定只读模式进行 PR 代码审查
seekclaw run "审查当前 git diff 并输出安全风险审查意见" --mode readonly --headless
```

---

## 2. GitHub Actions 集成实战

在 GitHub 仓库的 `.github/workflows/ai-review.yml` 中添加自动化 PR 评审与修复工作流：

```yaml
name: SeekClaw AI Code Review

on:
  pull_request:
    branches: [ main, develop ]

jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout Code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install SeekClaw CLI
        run: dotnet tool install -g seekclaw-cli || npm install -g seekclaw-cli

      - name: Run SeekClaw Code Audit
        env:
          DEEPSEEK_API_KEY: ${{ secrets.DEEPSEEK_API_KEY }}
        run: |
          seekclaw run "分析最近一次 commit 的改动，检查潜在并发与内存泄漏隐患，以 Markdown 格式输出审查报告" \
            --mode readonly \
            --headless > review-report.md

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

SeekClaw 支持打包为极简容器镜像，作为独立 Worker 节点运行于 Kubernetes 集群或云原生环境中：

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

启动并挂载代码目录：

```bash
docker run -d \
  --name seekclaw-worker \
  -v /var/repos/project:/workspace \
  -e SEEKCLAW_PROVIDER_APIKEY=sk-xxx \
  seekclaw-worker
```

---

## 4. 退出码与错误处理

在 CI 脚本中，可以通过退出码判断 Agent 执行状态：
- `0`：任务成功完成，且验证通过。
- `1`：模型执行失败或构建验证未能完全修复。
- `2`：环境或网络配置异常（如 API Key 失效、模型不可达）。
