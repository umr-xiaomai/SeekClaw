# Daemon and IPC Protocol

The SeekClaw Daemon exposes the Runtime to desktop clients, IDE plugins, and other local integrations. Protocol version `2.0` uses one JSON object per line (JSONL); it is not a full JSON-RPC 2.0 implementation.

## Endpoints

- Windows Named Pipe: `\\.\pipe\seekclaw`
- Linux / macOS Unix Socket: `~/.seekclaw/daemon.sock`

Every request and response must end with a newline. A connection remains able to send control requests while an Agent turn is streaming.

```json
{"id":1,"method":"chat","params":{"message":"Analyze this project"}}
{"id":1,"event":"thinking","data":"Inspecting the project structure"}
{"id":1,"event":"delta","data":"This is a .NET project."}
{"id":1,"event":"done","data":"This is a .NET project."}
```

Responses use a stable event envelope: `id` identifies the request, `event` identifies the event type, and `data` is always a string. Structured `result` data is encoded as a JSON string and must be parsed once more by the client.

## Protocol and Runtime State

| Method | Parameters | Description |
| --- | --- | --- |
| `ping` | none | Returns `pong` |
| `protocol.info` | none | Returns the protocol version, capabilities, and methods |
| `workspace.get` | none | Returns the active path, project kinds, and mode |
| `workspace.open` | `{ "path": "..." }` | Validates and switches the Runtime workspace |
| `workspace.init` | none | Initializes SeekClaw directories in the active workspace |
| `agent.mode.get` | none | Returns `plan`, `readonly`, `edit`, or `auto` |
| `agent.mode.switch` | `{ "mode": "edit" }` | Switches and persists the Agent mode |

`workspace.open` clears the resumed session for that connection. Workspace, mode, and model changes return an `error` while a turn is active so the execution context cannot change midway through a turn.

## Running and Cancelling Turns

`chat` remains the main method for compatibility with existing clients. `agent.runTurn` and `agent/runTurn` are aliases.

```json
{"id":10,"method":"chat","params":{"message":"Fix the tests"}}
{"id":11,"method":"agent.cancel","params":{"requestId":10}}
```

The `requestId` parameter is optional; omitting it cancels the active turn on the current connection. The cancellation request receives its own `result`, and the chat request terminates with `cancelled`:

```json
{"id":11,"event":"result","data":"cancellation requested for 10"}
{"id":10,"event":"cancelled","data":"partial text produced before cancellation"}
```

Streaming events are `thinking`, `delta`, `status`, `tool_start`, and `tool_done`. Terminal events are `done`, `cancelled`, and `error`.

## Other Methods

| Method | Parameters | Description |
| --- | --- | --- |
| `session.list` | `{ "workspace": "...", "global": false, "includeArchived": true }` | Lists sessions in a project or global scope |
| `session.get` | `{ "id": "...", "workspace": "..." }` | Reads a session and its messages |
| `session.update` | `{ "id": "...", "title": "..." }` | Updates title and other session metadata |
| `session.archive` | `{ "id": "...", "archived": true }` | Archives or restores a session |
| `session.delete` | `{ "id": "..." }` | Permanently deletes a session |
| `session.resume` | `{ "id": "...", "global": false }` | Resumes a session |
| `session.new` | `{ "global": false }` | Creates and binds a new session |
| `model.list` | none | Lists available `provider/model` references |
| `model.catalog` | none | Returns model details, capabilities, and active state |
| `model.switch` | `{ "model": "provider/model" }` | Switches and persists the model |
| `model.test` | `{ "model": "provider/model" }` | Sends a minimal real request through the model |
| `doctor` | none | Returns a Runtime health-check summary |
| `doctor.run` | none | Returns structured Runtime and Provider checks |
| `shutdown` | none | Cancels an active turn, returns `bye`, and gracefully stops the Daemon |

Session methods accept `workspace` for a concrete project or `global: true` for the directory-free global session store. `includeArchived` controls whether archived tasks are returned. Desktop waits until the first message to call `session.new`, so creating an empty task does not create an empty Runtime Session.

## Desktop Administration Methods

The Desktop settings workbench uses structured methods to manage the same configuration as the CLI without reading configuration files directly. An explicitly stored Provider `apiKey` is returned by `provider.list` so Desktop can display and edit it. The value referenced by `apiKeyEnv` is not resolved and returned. MCP environment variables expose names only, never values.

| Method | Description |
| --- | --- |
| `profile.list/upsert/use/remove` | Manages runtime profiles |
| `provider.list/upsert/use/remove/test` | Manages and probes providers |
| `mcp.list/upsert/remove/reload` | Manages and reconnects MCP servers and tool registrations |
| `skill.list/toggle` | Lists and enables or disables skills |
| `usage.get` | Returns model-level calls, tokens, cost, and latency aggregates |

The Daemon starts listening before MCP initialization continues in the background. `mcp.reload`, MCP configuration changes, and workspace switches unregister old tools and prompts before serially connecting the new configuration.

The current Daemon shares one `SeekClawRuntime`, one active workspace, and one event bus, so it allows one Agent turn globally. Other connections receive an explicit `error` while the Runtime is busy; clients should not attempt to bypass this protocol guarantee with parallel requests.

## Desktop Daemon lifecycle

Packaged Desktop first connects to the local endpoint. If no Daemon is present, it starts the self-contained Runtime under `resources/runtime` and probes for readiness using 24 short attempts. Desktop tracks only the child process it creates. On exit it sends `shutdown`, waits for graceful termination, and kills the child only after a timeout. If Desktop connected to an externally started Daemon, exiting Desktop only disconnects the client.
