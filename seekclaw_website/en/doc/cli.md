# CLI Command Reference

`seekclaw_cli` provides a comprehensive command line interface built with `System.CommandLine`.

---

## Core Commands

```bash
# Start interactive chat
seekclaw chat

# Single shot prompt
seekclaw "Refactor AuthService to use JWT rotation"

# Resume session
seekclaw --continue
seekclaw session resume <session-id>

# Manage providers & models
seekclaw provider list
seekclaw model list
seekclaw doctor
```
