# Daemon and IPC Protocol

The SeekClaw Daemon exposes the Runtime to desktop clients, IDE plugins, and other local integrations. Protocol version `2.1` uses one JSON object per line (JSONL); it is not a full JSON-RPC 2.0 implementation.

## Endpoints

- Windows Named Pipe: `\\.\pipe\seekclaw`
- Linux / macOS Unix Socket: `~/.seekclaw/daemon.sock`

Every request and response must end with a newline. A connection remains able to send control requests while an Agent turn is streaming.

```json
{"id":1,"method":"chat","params":{"sessionId":"20260731-120000-a1b2c3","message":"Analyze this project"}}
{"id":1,"event":"thinking","sessionId":"20260731-120000-a1b2c3","data":"Inspecting the project structure"}
{"id":1,"event":"delta","sessionId":"20260731-120000-a1b2c3","data":"This is a .NET project."}
{"id":1,"event":"done","sessionId":"20260731-120000-a1b2c3","data":"This is a .NET project."}
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
| `agent.steer` | `{ "sessionId": "...", "message": "..." }` | Adds guidance to a running turn without cancelling the current request |

`workspace.open` clears the legacy resumed session for that connection. New `chat` requests should include both `sessionId` and (for project tasks) `workspace`; the turn then captures its own workspace and cannot be affected by later workspace changes.

## Running and Cancelling Turns

`chat` remains the main method for compatibility with existing clients. `agent.runTurn` and `agent/runTurn` are aliases.

Vision-capable models accept an `images` array. Each image carries a client-generated `id`, file name, MIME type, and Base64 data without a Data URL prefix. A turn accepts up to 10 images, 10 MB per image, and 40 MB total. Supported types are `image/png`, `image/jpeg`, `image/webp`, and `image/gif`; an image-only turn may omit `message`.

```json
{"id":9,"method":"chat","params":{"message":"Compare these images","images":[{"id":"a","name":"before.png","mediaType":"image/png","data":"..."},{"id":"b","name":"after.webp","mediaType":"image/webp","data":"..."}]}}
```

```json
{"id":10,"method":"chat","params":{"message":"Fix the tests","reasoningLevel":"high"}}
{"id":11,"method":"agent.cancel","params":{"requestId":10}}
```

A running turn can accept additional guidance. `agent.steer` places the message in the turn's guidance queue; after the in-flight model request finishes, the Agent adds it to the context and continues with another step without cancelling or interrupting that request:

```json
{"id":12,"method":"agent.steer","params":{"sessionId":"20260731-120000-a1b2c3","message":"Also check the edge cases"}}
{"id":12,"event":"result","sessionId":"20260731-120000-a1b2c3","data":"guidance queued"}
```

The `requestId` parameter is optional; omitting it cancels all active turns on the current connection. The cancellation request receives its own `result`, and the selected chat request terminates with `cancelled`:

```json
{"id":11,"event":"result","data":"cancellation requested for 10"}
{"id":10,"event":"cancelled","data":"partial text produced before cancellation"}
```

Assistant messages returned by `session.get` carry `modelRef` (`provider/model`) so clients can label which model produced each answer. Streaming events are `thinking`, `delta`, `steer`, `status`, `image_view`, `tool_start`, `tool_done`, and `workflow`. The `workflow` event carries `details` with `step`, `kind` (`start`/`think`/`tool`/`verify`/`repair`/`compact`/`done`/`error`), `label`, and `detail` so clients can draw the live execution flowchart. `steer` indicates that additional guidance has entered the active turn's context; `details.imageId` on `image_view` identifies the uploaded image entering the model request. Terminal events are `done`, `cancelled`, and `error`.

## Other Methods

| Method | Parameters | Description |
| --- | --- | --- |
| `session.list` | `{ "workspace": "...", "global": false, "includeArchived": true }` | Lists sessions in a project or global scope |
| `session.get` | `{ "id": "...", "workspace": "..." }` | Reads a session and its messages |
| `session.update` | `{ "id": "...", "title": "...", "reasoningLevel": "high" }` | Updates title, reasoning depth, and other Session metadata |
| `session.archive` | `{ "id": "...", "archived": true }` | Archives or restores a session |
| `session.delete` | `{ "id": "..." }` | Permanently deletes a session |
| `session.truncate` | `{ "id": "...", "keepCount": 5 }` | Keeps only the first N messages (used by "regenerate"); returns the remaining count |
| `session.resume` | `{ "id": "...", "global": false }` | Resumes a session |
| `session.new` | `{ "workspace": "...", "reasoningLevel": "high" }` or `{ "global": true }` | Creates and binds a new Session |
| `model.list` | none | Lists available `provider/model` references |
| `model.catalog` | none | Returns model details, capabilities, and active state |
| `model.switch` | `{ "model": "provider/model" }` | Switches and persists the model |
| `model.test` | `{ "model": "provider/model" }` | Sends a minimal real request through the model |
| `prompt.optimize` | `{ "text": "...", "model": "provider/model" }` | Optimizes the prompt with the specified or active model without creating a Session |
| `doctor` | none | Returns a Runtime health-check summary |
| `doctor.run` | none | Returns structured Runtime and Provider checks |
| `factory.reset` | none | Clears global configuration, sessions, and SQLite data, restores factory defaults, and rebuilds the database |
| `shutdown` | none | Cancels all active turns, returns `bye`, and gracefully stops the Daemon |

Session methods accept `workspace` for a concrete project or `global: true` for the directory-free global session store. `includeArchived` controls whether archived tasks are returned. Desktop waits until the first message to call `session.new`, so creating an empty task does not create an empty Runtime Session.

`reasoningLevel` uses the neutral values `none`, `low`, `medium`, `high`, `max`, `xhigh`, and `ultra`. It is not passed through as a UI-owned API parameter: Runtime first clamps it to model capabilities and then lets the Provider adapter generate wire parameters. `xhigh`/`ultra` are extended levels; model support defaults to `max`, and DeepSeek explicitly maps both to `max`.

## Desktop Administration Methods

The Desktop settings workbench uses structured methods to manage the same configuration as the CLI without reading configuration files directly. An explicitly stored Provider `apiKey` is returned by `provider.list` so Desktop can display and edit it. Runtime does not read API keys from environment variables. MCP environment variables expose names only, never values.

| Method | Description |
| --- | --- |
| `profile.list/upsert/use/remove` | Manages runtime profiles |
| `provider.list/upsert/use/remove/test` | Manages and probes providers |
| `mcp.list/upsert/remove/reload` | Manages and reconnects MCP servers and tool registrations |
| `skill.list/toggle` | Lists and enables or disables skills |
| `usage.get` | Returns model-level calls, tokens, cost, and latency aggregates |

`project.upsert` rejects registering the user profile or the SeekClaw global state directory (`~/.seekclaw`) as a project; `project.remove` accepts `keepSessions: true` so invalid project rows can be cleaned up without deleting the sessions stored in the database.

The Daemon starts listening before MCP initialization continues in the background. `mcp.reload`, MCP configuration changes, and workspace switches unregister old tools and prompts before serially connecting the new configuration.

Each `chat` request is assigned an isolated Runtime, workspace Prompt/Skills state, MCP registrations, and event subscription. A connection or multiple connections can therefore run any number of turns concurrently; practical concurrency is determined by CPU, memory, Provider throughput, and local I/O. Administrative configuration writes are serialized only to prevent file races and do not block running turns.

## Desktop Daemon lifecycle

Packaged Desktop first connects to the local endpoint. If no Daemon is present, it starts the self-contained Runtime under `resources/runtime` and probes for readiness using 24 short attempts. Desktop tracks only the child process it creates. On exit it sends `shutdown`, waits for graceful termination, and kills the child only after a timeout. If Desktop connected to an externally started Daemon, exiting Desktop only disconnects the client.
