# CI/CD & Headless Automation

In addition to desktop and interactive terminal workflows, SeekClaw is built for headless automation across Continuous Integration and Continuous Deployment (CI/CD) pipelines.

---

## 1. One-Shot CLI Execution

In automated pipelines or shell scripts, provide a prompt directly to `seekclaw` to run a non-interactive task (returns exit code 0 on success, non-zero on failure):

```bash
# Execute a one-shot task headlessly and return an exit code
seekclaw "Format changed files and fix compiler warnings"

# Override the active model for a single run
seekclaw --model "openai/gpt-5.5" "Review current git diff and output security audit findings"
```

---

## 2. GitHub Actions Integration

Add automated PR code reviews to `.github/workflows/ai-review.yml`:

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
          seekclaw "Analyze changed files in this git diff for concurrency and memory leaks; format as Markdown" > review-report.md

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
              body: `### 🤖 SeekClaw Automated Review\n\n${body}`
            });
```

---

## 3. Docker Containerization

Deploy SeekClaw as an independent worker container or persistent Daemon in container environments:

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

Mount persistent configuration and workspace directories:

```bash
docker run -d \
  --name seekclaw-worker \
  -v ~/.seekclaw:/root/.seekclaw \
  -v /var/repos/project:/workspace \
  seekclaw-worker
```

---

## 4. Exit Codes & Pipeline Integration

- `0`: Task completed successfully and validation passed.
- `1`: Execution or build verification encountered unresolved errors.
- `2`: Network or configuration failure (e.g. invalid API key, unreachable provider).
