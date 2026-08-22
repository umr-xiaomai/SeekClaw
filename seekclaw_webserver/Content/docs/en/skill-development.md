# Custom Skill Development & Publishing

Skills are modular extension packages for SeekClaw. A skill package bundles specialized system prompts, tailored tool constraints, automation scripts, and workflow recipes, enabling users to instantly augment the agent with domain-specific expertise (e.g., Code Reviewer, SQL Tuner, Docker Master).

---

## 1. Directory Structure

A standard SeekClaw Skill is organized as a directory (or `.zip` archive):

```text
my-awesome-skill/
├── SKILL.md             # [Required] Main YAML frontmatter and instruction prompt
├── prompts/             # [Optional] Dedicated prompt templates
│   └── review-rule.txt
├── scripts/             # [Optional] Helper scripts (Python, Bash, PowerShell)
│   └── analyze.py
└── README.md            # [Optional] Marketplace overview documentation
```

---

## 2. Crafting the SKILL.md File

`SKILL.md` uses standard YAML Frontmatter combined with Markdown instructions:

```markdown
---
name: "Docker Master"
slug: "docker-master"
version: "1.0.0"
author: "YourName"
summary: "Specialized in Dockerfile auditing, Compose multi-container orchestration, and image minimization."
tags: ["docker", "devops", "container", "optimization"]
homepage: "https://github.com/yourname/docker-master"
---

# Docker Master Directives

You are a premier containerization and DevOps expert. When assisting with Docker assets, observe the following rules:

## 1. Image Build & Size Optimization
- Always utilize multi-stage builds.
- Use minimal base images such as Alpine or Distroless for final runtime stages.
- Consolidate RUN instructions to reduce layers and purge package caches within the same layer.

## 2. Security & Privilege Separation
- Never run applications as the root user in production; create a dedicated non-root user.
- Secrets and connection strings must never be hardcoded into images.
```

### Frontmatter Schema

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `name` | string | **Yes** | Human-readable title (e.g. `Docker Master`) |
| `slug` | string | **Yes** | Unique identifier (lowercase letters, numbers, hyphens) |
| `version` | string | **Yes** | Semantic version (e.g. `1.0.0`) |
| `author` | string | **Yes** | Author or organization name |
| `summary` | string | **Yes** | 1-2 sentence description shown in marketplace lists |
| `tags` | string[]| No | Search tags |
| `homepage`| string | No | Source repository or website URL |

---

## 3. Local Development & Testing

During development, simply drop your skill folder into your personal skills directory:

- **Windows**: `%USERPROFILE%\.seekclaw\skills\my-awesome-skill\`
- **Linux / macOS**: `~/.seekclaw/skills/my-awesome-skill/`

Then inspect it via CLI:

```bash
# List all active skills
seekclaw skills list

# Verify syntax and health
seekclaw doctor
```

---

## 4. Publishing to the Official Marketplace

### Method 1: Web Portal Submission (Recommended)
1. Go to the marketplace publishing page: [/skills/submit](/en/skills/submit).
2. Log in with your developer account.
3. Fill in the metadata or upload your packaged `.zip` file directly.

### Method 2: Command Line Distribution
Package the folder into a standard `.zip` (ensure `SKILL.md` resides at the archive root). Once approved, developers worldwide can install it via:

```bash
seekclaw skill install <your-skill-slug>
```
