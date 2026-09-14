export type McpTransport = 'stdio' | 'sse' | 'http'
export type McpScope = 'workspace' | 'global'

export interface McpFormValue {
  name: string
  scope: McpScope
  transport: McpTransport
  command: string
  args: string
  url: string
  env: string
  enabled: boolean
}

export interface McpServerSummary {
  name: string
  scope: McpScope
  transport: string
  command?: string
  args: string[]
  url?: string
  envKeys: string[]
  enabled: boolean
  connected: boolean
  /** True while the runtime is still connecting this server in the background. */
  connecting?: boolean
  toolCount: number
  error?: string
}

export interface McpOption {
  value: string
  label: string
  description: string
}

export const MCP_SCOPE_OPTIONS: McpOption[] = [
  { value: 'workspace', label: '当前工作区', description: '仅在此工作区生效' },
  { value: 'global', label: '全局', description: '在所有工作区中生效' }
]

/**
 * `stdio` runs a local subprocess; the two remote transports differ in how the
 * JSON-RPC stream travels: `sse` is the legacy HTTP+SSE transport, `http` is the
 * Streamable HTTP transport from the current MCP specification.
 */
export const MCP_TRANSPORT_OPTIONS: McpOption[] = [
  { value: 'stdio', label: 'stdio', description: '通过本地子进程的标准输入输出通信' },
  { value: 'sse', label: 'HTTP SSE', description: '通过 SSE 长连接与远程服务器通信' },
  { value: 'http', label: 'Streamable HTTP', description: '通过流式 HTTP 请求与远程服务器通信' }
]

const TRANSPORT_LABELS: Record<string, string> = {
  stdio: 'stdio',
  sse: 'HTTP SSE',
  http: 'Streamable HTTP',
  'streamable-http': 'Streamable HTTP',
  streamable_http: 'Streamable HTTP',
  websocket: 'WebSocket'
}

/** Human label for a transport id coming from the runtime or from config. */
export function transportLabel(transport?: string): string {
  if (!transport) return ''
  return TRANSPORT_LABELS[transport.toLowerCase()] ?? transport
}

/** Normalizes any accepted transport id onto the three values the editor offers. */
export function normalizeTransport(transport?: string): McpTransport {
  const value = (transport ?? '').toLowerCase()
  if (value === 'sse') return 'sse'
  if (value === 'http' || value === 'streamable-http' || value === 'streamable_http') return 'http'
  return 'stdio'
}

export function isRemoteTransport(transport: string): boolean {
  return transport !== 'stdio'
}

export function createMcpFormValue(): McpFormValue {
  return {
    name: '',
    scope: 'workspace',
    transport: 'stdio',
    command: '',
    args: '',
    url: '',
    env: '',
    enabled: true
  }
}

/** Builds editor state from a runtime server entry. Environment values are never sent back to the UI. */
export function mcpFormFromServer(server: McpServerSummary): McpFormValue {
  return {
    name: server.name,
    scope: server.scope,
    transport: normalizeTransport(server.transport),
    command: server.command ?? '',
    args: (server.args ?? []).join('\n'),
    url: server.url ?? '',
    env: '',
    enabled: server.enabled
  }
}

/** Parses the `KEY=value` textarea into a plain environment map. */
export function parseEnvText(value: string): Record<string, string> | undefined {
  const entries = value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  if (entries.length === 0) return undefined
  return Object.fromEntries(entries.map((line) => {
    const index = line.indexOf('=')
    return index < 0 ? [line, ''] : [line.slice(0, index).trim(), line.slice(index + 1)]
  }))
}

/** Returns a user-facing validation message, or null when the form can be saved. */
export function mcpFormError(form: McpFormValue): string | null {
  if (!form.name.trim()) return '请填写 MCP 服务器名称'
  if (!isRemoteTransport(form.transport)) {
    if (!form.command.trim()) return 'stdio 连接需要填写启动命令，例如 npx'
    return null
  }
  const url = form.url.trim()
  if (!url) return `${transportLabel(form.transport)} 连接需要填写 URL`
  if (!/^https?:\/\//i.test(url)) return 'URL 需要以 http:// 或 https:// 开头'
  return null
}

/**
 * Payload for `mcp.upsert`. Always returns plain JSON values so it can cross the
 * Electron IPC boundary (Vue reactive proxies cannot be structured-cloned).
 */
export function buildMcpServerPayload(form: McpFormValue): Record<string, unknown> {
  const remote = isRemoteTransport(form.transport)
  const server: Record<string, unknown> = {
    transport: form.transport,
    command: remote ? '' : form.command.trim(),
    args: remote ? [] : form.args.split(/\r?\n/).map((value) => value.trim()).filter(Boolean),
    url: remote ? form.url.trim() : '',
    enabled: form.enabled
  }
  // Only stdio servers consume process environment variables today.
  const env = remote ? undefined : parseEnvText(form.env)
  if (env) server.env = env
  return server
}

/**
 * Payload for toggling an existing server. `mcp.upsert` merges the fields it
 * receives, so every editable field is resent from the plain snapshot to keep
 * the stored configuration intact.
 */
export function buildMcpTogglePayload(server: McpServerSummary): Record<string, unknown> {
  const remote = isRemoteTransport(server.transport)
  return {
    transport: server.transport,
    command: remote ? '' : server.command ?? '',
    args: remote ? [] : [...(server.args ?? [])],
    url: remote ? server.url ?? '' : '',
    enabled: !server.enabled
  }
}

/** Status line for one server row. */
export function mcpStatusText(
  server: Pick<McpServerSummary, 'connected' | 'connecting' | 'enabled' | 'toolCount' | 'error'>
): string {
  if (server.connected) return `${server.toolCount} 个工具`
  if (server.connecting) return '连接中…'
  if (!server.enabled) return '已禁用'
  return server.error?.trim() || '未连接'
}
