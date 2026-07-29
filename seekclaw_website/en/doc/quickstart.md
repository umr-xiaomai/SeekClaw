# Quick Start Guide

Get started with SeekClaw AI Agent Runtime in under 5 minutes.

---

## Prerequisites

Before building and running SeekClaw, ensure your environment meets the following:

1. **.NET 10.0 SDK** or higher
   - Check command: `dotnet --version`
2. **Git** (for repository workspace detection)
   - Check command: `git --version`
3. One or more **LLM API Keys** (OpenAI, Anthropic, Gemini, or local Ollama / LM Studio)

---

## Building from Source

```bash
# 1. Clone repository
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw

# 2. Build solution
dotnet build
```

---

## API Key Configuration

### Method A: Via CLI Commands (Recommended)

```bash
# Add OpenAI API Key
dotnet run --project seekclaw_cli -- provider add openai --api-key "sk-proj-xxxxxxxx"

# Add Anthropic API Key
dotnet run --project seekclaw_cli -- provider add anthropic --api-key "sk-ant-xxxxxxxx"

# Test provider connection
dotnet run --project seekclaw_cli -- provider test openai
```

### Method B: Edit Global Config File

Edit `~/.seekclaw/config.json`:

```json
{
  "providers": {
    "openai": {
      "apiKey": "sk-proj-xxxxxxxx",
      "baseUrl": "https://api.openai.com/v1"
    },
    "anthropic": {
      "apiKey": "sk-ant-xxxxxxxx"
    }
  },
  "profiles": {
    "default": {
      "provider": "openai",
      "model": "gpt-5.5"
    }
  }
}
```

---

## Running SeekClaw

### 1. Interactive Chat Mode

```bash
dotnet run --project seekclaw_cli
```

### 2. Single-shot Prompt Mode

```bash
dotnet run --project seekclaw_cli -- "Analyze the architecture of the current project"
```

### 3. Session Continuation

```bash
# Continue latest session
dotnet run --project seekclaw_cli -- --continue

# Resume specific Session ID
dotnet run --project seekclaw_cli -- --resume <session-id>
```

---

## Diagnostics (Doctor)

```bash
dotnet run --project seekclaw_cli -- doctor
```
