# CI/CD & Headless Automation

In addition to desktop and interactive terminal workflows, SeekClaw is built for headless automation across Continuous Integration and Continuous Deployment (CI/CD) pipelines.

---

## 1. Headless Execution Commands

Run tasks directly inside automated scripts with `seekclaw run` in non-interactive mode:

```bash
# Execute a task headlessly and return an exit code (0 for success)
seekclaw run "Format changed files and fix compiler warnings" --headless

# Run in read-only mode for pull request auditing
seekclaw run "Review git diff and output security audit findings" --mode readonly --headless
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
          seekclaw run "Analyze the latest commit for concurrency and memory risks; format as Markdown" \
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
              body: `### 🤖 SeekClaw Automated Review\n\n${body}`
            });
```

---

## 3. Docker Containerization

Deploy SeekClaw as an independent worker container in Kubernetes or cloud environments:

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

---

## 4. Exit Codes & Pipeline Integration

- `0`: Task completed successfully and validation passed.
- `1`: Execution or build verification encountered unresolved errors.
- `2`: Network or configuration failure (e.g. invalid API key, unreachable provider).
