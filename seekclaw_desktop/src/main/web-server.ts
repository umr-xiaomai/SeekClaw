import { createHash, randomBytes, timingSafeEqual } from 'node:crypto'
import { existsSync, statSync } from 'node:fs'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import { networkInterfaces } from 'node:os'
import { extname, join, normalize, resolve, sep } from 'node:path'
import type { BrowserCommand } from '../shared/browser.js'
import type { AppInfo, DaemonRequestOptions, DaemonState, RevertDiffItem } from '../shared/ipc.js'
import type {
  RemoteBrowserInput,
  ServerFilePickerKind,
  WebServerConfig,
  WebServerHost,
  WebServerStatus
} from '../shared/web-server.js'
import type { ConversationBrowser } from './conversation-browser.js'
import type { DaemonClient } from './daemon-client.js'
import type { PendingApprovalCache } from './pending-approval-cache.js'
import {
  checkoutGitBranch,
  createGitBranch,
  getGitBranches,
  getGitCommitDiff,
  getGitHistory,
  getGitOverview,
  listWorkspaceFiles,
  openProjectTerminal,
  revertFileDiffs
} from './project-tools.js'
import {
  createServerDirectory,
  listServerDirectory,
  listServerRoots,
  readServerFileBase64,
  readServerImages,
  saveUploadedFile
} from './web-fs.js'
import { readLocalLinkPreview } from './file-preview.js'
import { MinimalWebSocket, upgradeToWebSocket } from './web-ws.js'

const TOKEN_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
const SESSION_COOKIE_NAME = 'seekclaw_web_session'
const DEFAULT_PORT = 19420
const MAX_AUTH_FAILURES = 5
const LOCKOUT_MS = 60_000
const DEFAULT_JSON_LIMIT_BYTES = 16 * 1024 * 1024
const UPLOAD_JSON_LIMIT_BYTES = 80 * 1024 * 1024

const MUTATING_SYNC_METHODS = new Set([
  'session.new',
  'session.create',
  'session.delete',
  'session.rename',
  'session.compact',
  'session.update',
  'session.archive',
  'session.restore',
  'session.fork',
  'session.truncate',
  'project.upsert',
  'project.remove',
  'ssh.upsert',
  'ssh.remove',
  'config.save',
  'config.rebuild',
  'provider.upsert',
  'provider.use',
  'provider.remove',
  'provider.toggle',
  'model.switch',
  'agent.mode.switch',
  'skill.toggle',
  'skill.import',
  'skill.remove',
  'skill.install',
  'mcp.upsert',
  'mcp.remove',
  'mcp.toggle',
  'mcp.reload',
  'mcp.auth.logout',
  'memory.delete',
  'memory.clear',
  'advanced.set',
  'routing.set',
  'schedule.create',
  'schedule.update',
  'schedule.delete',
  'schedule.toggle'
])

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.mjs': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf'
}

export function generateWebToken(length = 24): string {
  const bytes = randomBytes(length)
  let token = ''
  for (let i = 0; i < length; i++) {
    token += TOKEN_ALPHABET[bytes[i]! % TOKEN_ALPHABET.length]
  }
  return token
}

export function safeCompareToken(expected: string, candidate: string): boolean {
  if (!expected || !candidate) return false
  const digestA = createHash('sha256').update(expected).digest()
  const digestB = createHash('sha256').update(candidate).digest()
  return timingSafeEqual(digestA, digestB)
}

export function isMutatingDaemonMethod(method: string): boolean {
  return MUTATING_SYNC_METHODS.has(method)
}

export function discoverLanIpv4Addresses(): string[] {
  const interfaces = networkInterfaces()
  const privateMatches: string[] = []
  const otherMatches: string[] = []

  for (const [name, list] of Object.entries(interfaces)) {
    if (!list) continue
    const lowerName = name.toLowerCase()
    const isLikelyVirtual =
      lowerName.includes('vethernet') ||
      lowerName.includes('docker') ||
      lowerName.includes('wsl') ||
      lowerName.includes('vbox') ||
      lowerName.includes('vmware') ||
      lowerName.includes('tailscale')
    for (const addr of list) {
      if (addr.internal || addr.family !== 'IPv4') continue
      if (addr.address.startsWith('169.254.')) continue
      const isPrivate =
        addr.address.startsWith('192.168.') ||
        addr.address.startsWith('10.') ||
        /^172\.(1[6-9]|2\d|3[01])\./.test(addr.address)
      if (isPrivate && !isLikelyVirtual) {
        privateMatches.push(addr.address)
      } else {
        otherMatches.push(addr.address)
      }
    }
  }

  return [...new Set([...privateMatches, ...otherMatches])]
}

function normalizeHost(host: unknown): WebServerHost {
  return host === '127.0.0.1' ? '127.0.0.1' : '0.0.0.0'
}

function normalizePort(port: unknown): number {
  const numeric = typeof port === 'number' ? port : Number(port)
  if (!Number.isInteger(numeric) || numeric < 1024 || numeric > 65535) return DEFAULT_PORT
  return numeric
}

function normalizeToken(token: unknown): string {
  if (typeof token !== 'string') return ''
  const trimmed = token.trim()
  return trimmed.length >= 6 ? trimmed : ''
}

function parseCookies(headerValue: string | undefined): Record<string, string> {
  const cookies: Record<string, string> = {}
  if (!headerValue) return cookies
  for (const chunk of headerValue.split(';')) {
    const idx = chunk.indexOf('=')
    if (idx <= 0) continue
    const key = chunk.slice(0, idx).trim()
    const value = chunk.slice(idx + 1).trim()
    if (key) {
      try {
        cookies[key] = decodeURIComponent(value)
      } catch {
        cookies[key] = value
      }
    }
  }
  return cookies
}

function getClientIp(req: IncomingMessage): string {
  return req.socket.remoteAddress || 'unknown'
}

async function readJsonBody<T = Record<string, unknown>>(req: IncomingMessage, maxBytes = DEFAULT_JSON_LIMIT_BYTES): Promise<T> {
  const chunks: Buffer[] = []
  let totalBytes = 0
  for await (const chunk of req) {
    const buf = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
    totalBytes += buf.byteLength
    if (totalBytes > maxBytes) {
      throw Object.assign(new Error('请求体过大'), { statusCode: 413 })
    }
    chunks.push(buf)
  }
  const raw = Buffer.concat(chunks).toString('utf8').trim()
  if (!raw) return {} as T
  try {
    return JSON.parse(raw) as T
  } catch {
    throw Object.assign(new Error('无效的 JSON 请求体'), { statusCode: 400 })
  }
}

function sendJson(res: ServerResponse, statusCode: number, body: unknown, extraHeaders?: Record<string, string>): void {
  const json = JSON.stringify(body)
  res.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    ...extraHeaders
  })
  res.end(json)
}

interface WsClientRecord {
  ws: MinimalWebSocket
  clientId?: string
  streamUnsub?: () => void
  streamScope?: string
}

export interface LanWebServerOptions {
  userDataPath: string
  daemon: DaemonClient
  pendingApprovals: PendingApprovalCache
  getBrowser: () => ConversationBrowser | null
  ensureBrowserAttached: () => Promise<void>
  getAppInfo: () => Omit<AppInfo, 'runtimeMode'>
  getRendererSource: () => { devUrl?: string; distDir: string }
  onDesktopSyncRefresh?: (reason: string) => void
}

export class LanWebServer {
  private readonly configPath: string
  private config: WebServerConfig
  private server: Server | null = null
  private lastError: string | undefined
  private readonly sessions = new Map<string, { createdAt: number }>()
  private readonly authFailures = new Map<string, { count: number; blockedUntil: number }>()
  private readonly wsClients = new Set<WsClientRecord>()
  private readonly statusListeners = new Set<(status: WebServerStatus) => void>()
  private heartbeatTimer: NodeJS.Timeout | null = null
  private initialized = false

  constructor(private readonly options: LanWebServerOptions) {
    this.configPath = join(options.userDataPath, 'web-server.json')
    this.config = {
      enabled: false,
      host: '0.0.0.0',
      port: DEFAULT_PORT,
      token: generateWebToken(),
      autoStart: false
    }
  }

  async init(): Promise<WebServerStatus> {
    if (!this.initialized) {
      this.initialized = true
      await this.loadConfig()
      if (this.config.autoStart) {
        if (!this.config.enabled) {
          this.config = { ...this.config, enabled: true }
          await this.saveConfig().catch(() => undefined)
        }
        await this.start().catch(() => undefined)
      } else if (this.config.enabled) {
        this.config = { ...this.config, enabled: false }
        await this.saveConfig().catch(() => undefined)
      }
    }
    return this.getStatus()
  }

  onStatus(listener: (status: WebServerStatus) => void): () => void {
    this.statusListeners.add(listener)
    return () => this.statusListeners.delete(listener)
  }

  getStatus(): WebServerStatus {
    const port = this.config.port
    const lanIps = this.config.host === '0.0.0.0' ? discoverLanIpv4Addresses() : []
    const urls =
      this.config.host === '0.0.0.0'
        ? [...lanIps.map((ip) => `http://${ip}:${port}`), `http://127.0.0.1:${port}`]
        : [`http://127.0.0.1:${port}`]
    const primaryUrl = urls[0] ?? `http://127.0.0.1:${port}`

    const running = Boolean(this.server?.listening)
    return {
      running,
      config: { ...this.config, enabled: running },
      urls,
      primaryUrl,
      clientCount: this.wsClients.size,
      error: this.lastError
    }
  }

  async updateConfig(patch: Partial<WebServerConfig>): Promise<WebServerStatus> {
    const previous = { ...this.config }
    const next: WebServerConfig = {
      enabled: typeof patch.enabled === 'boolean' ? patch.enabled : Boolean(this.server?.listening),
      host: patch.host !== undefined ? normalizeHost(patch.host) : previous.host,
      port: patch.port !== undefined ? normalizePort(patch.port) : previous.port,
      token:
        patch.token !== undefined
          ? normalizeToken(patch.token) || previous.token
          : previous.token,
      autoStart: typeof patch.autoStart === 'boolean' ? patch.autoStart : previous.autoStart
    }

    if (patch.token !== undefined && !normalizeToken(patch.token)) {
      throw new Error('访问口令至少需要 6 个字符')
    }

    const tokenChanged = next.token !== previous.token
    const networkChanged = next.host !== previous.host || next.port !== previous.port

    this.config = next
    await this.saveConfig()

    if (tokenChanged) {
      this.disconnectAllClients('Token updated', 4001)
    }

    if (!next.enabled) {
      await this.stop()
    } else if (!this.server?.listening) {
      await this.start()
    } else if (networkChanged) {
      await this.restart()
    } else {
      this.emitStatus()
    }

    return this.getStatus()
  }

  async regenerateToken(): Promise<WebServerStatus> {
    this.config = {
      ...this.config,
      token: generateWebToken()
    }
    await this.saveConfig()
    this.disconnectAllClients('Token regenerated', 4001)
    this.emitStatus()
    return this.getStatus()
  }

  disconnectClients(): WebServerStatus {
    this.disconnectAllClients('Disconnected by host', 4001)
    this.emitStatus()
    return this.getStatus()
  }

  async start(): Promise<void> {
    if (this.server?.listening) return
    this.lastError = undefined

    const server = createServer((req, res) => {
      void this.handleHttpRequest(req, res)
    })

    server.on('upgrade', (req, socket, head) => {
      this.handleUpgrade(req, socket, head)
    })

    try {
      await new Promise<void>((resolveListen, rejectListen) => {
        const onError = (error: NodeJS.ErrnoException): void => {
          server.removeListener('listening', onListening)
          rejectListen(error)
        }
        const onListening = (): void => {
          server.removeListener('error', onError)
          resolveListen()
        }
        server.once('error', onError)
        server.once('listening', onListening)
        server.listen(this.config.port, this.config.host)
      })
    } catch (error) {
      const code = (error as NodeJS.ErrnoException)?.code
      this.lastError =
        code === 'EADDRINUSE'
          ? `端口 ${this.config.port} 已被占用，请更换端口后重试`
          : error instanceof Error
            ? error.message
            : String(error)
      this.config = { ...this.config, enabled: false }
      await this.saveConfig().catch(() => undefined)
      try {
        server.close()
      } catch {
        // ignore
      }
      this.server = null
      this.emitStatus()
      return
    }

    server.on('error', (error) => {
      this.lastError = error.message
      this.emitStatus()
    })

    this.server = server
    this.startHeartbeat()
    this.emitStatus()
  }

  async stop(): Promise<void> {
    this.stopHeartbeat()
    this.disconnectAllClients('Server stopping')

    const current = this.server
    this.server = null
    if (current) {
      await new Promise<void>((resolveClose) => {
        current.close(() => resolveClose())
      })
    }
    this.emitStatus()
  }

  async restart(): Promise<void> {
    await this.stop()
    await this.start()
  }

  broadcastDaemonEvent(message: unknown): void {
    this.broadcastWs({ type: 'daemon:event', payload: message })
  }

  broadcastDaemonState(state: DaemonState): void {
    this.broadcastWs({ type: 'daemon:state', payload: state })
  }

  broadcastBrowserState(state: unknown): void {
    this.broadcastWs({ type: 'browser:changed', payload: state })
  }

  broadcastSyncRefresh(reason: string, excludeClientId?: string): void {
    this.broadcastWs({ type: 'sync:refresh', payload: { reason } }, excludeClientId)
  }

  private broadcastWs(payload: unknown, excludeClientId?: string): void {
    if (this.wsClients.size === 0) return
    const serialized = JSON.stringify(payload)
    for (const client of [...this.wsClients]) {
      if (excludeClientId && client.clientId && client.clientId === excludeClientId) {
        continue
      }
      if (!client.ws.isOpen) {
        this.cleanupWsClient(client)
        continue
      }
      try {
        client.ws.sendText(serialized)
      } catch {
        this.cleanupWsClient(client)
      }
    }
  }

  private emitStatus(): void {
    const status = this.getStatus()
    for (const listener of this.statusListeners) {
      try {
        listener(status)
      } catch {
        // ignore listener error
      }
    }
    this.broadcastWs({ type: 'web-server:status', payload: status })
  }

  private startHeartbeat(): void {
    this.stopHeartbeat()
    this.heartbeatTimer = setInterval(() => {
      for (const client of [...this.wsClients]) {
        if (!client.ws.isOpen) {
          this.cleanupWsClient(client)
          continue
        }
        client.ws.ping()
      }
    }, 25_000)
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = null
    }
  }

  private disconnectAllClients(reason: string, code = 1000): void {
    this.sessions.clear()
    for (const client of [...this.wsClients]) {
      this.cleanupWsClient(client)
      client.ws.close(code, reason)
    }
  }

  private cleanupWsClient(client: WsClientRecord): void {
    if (client.streamUnsub) {
      try {
        client.streamUnsub()
      } catch {
        // ignore
      }
      client.streamUnsub = undefined
      client.streamScope = undefined
    }
    this.wsClients.delete(client)
  }

  private async loadConfig(): Promise<void> {
    if (!existsSync(this.configPath)) {
      await this.saveConfig()
      return
    }
    try {
      const raw = await readFile(this.configPath, 'utf8')
      const parsed = JSON.parse(raw) as Partial<WebServerConfig>
      const token = normalizeToken(parsed.token) || generateWebToken()
      this.config = {
        enabled: Boolean(parsed.enabled),
        host: normalizeHost(parsed.host),
        port: normalizePort(parsed.port),
        token,
        autoStart: Boolean(parsed.autoStart)
      }
      if (!parsed.token || parsed.token !== token) {
        await this.saveConfig()
      }
    } catch {
      await this.saveConfig()
    }
  }

  private async saveConfig(): Promise<void> {
    await mkdir(this.options.userDataPath, { recursive: true })
    await writeFile(this.configPath, JSON.stringify(this.config, null, 2), 'utf8')
  }

  private getRemainingLockoutSeconds(ip: string): number {
    const entry = this.authFailures.get(ip)
    if (!entry || entry.blockedUntil <= Date.now()) return 0
    return Math.ceil((entry.blockedUntil - Date.now()) / 1000)
  }

  private recordAuthFailure(ip: string): number {
    const now = Date.now()
    const current = this.authFailures.get(ip)
    const count = current && current.blockedUntil <= now ? current.count + 1 : !current ? 1 : current.count + 1
    if (count >= MAX_AUTH_FAILURES) {
      this.authFailures.set(ip, { count: 0, blockedUntil: now + LOCKOUT_MS })
      return Math.ceil(LOCKOUT_MS / 1000)
    }
    this.authFailures.set(ip, { count, blockedUntil: 0 })
    return 0
  }

  private isAuthenticatedRequest(req: IncomingMessage, urlToken?: string | null): boolean {
    const cookies = parseCookies(req.headers.cookie)
    const sessionId = cookies[SESSION_COOKIE_NAME]
    if (sessionId && this.sessions.has(sessionId)) return true

    const authHeader = req.headers.authorization
    if (authHeader?.startsWith('Bearer ')) {
      const bearer = authHeader.slice(7).trim()
      if (safeCompareToken(this.config.token, bearer)) return true
    }

    if (urlToken && safeCompareToken(this.config.token, urlToken.trim())) {
      return true
    }

    return false
  }

  private createSessionCookie(): string {
    const sessionId = randomBytes(24).toString('hex')
    this.sessions.set(sessionId, { createdAt: Date.now() })
    return `${SESSION_COOKIE_NAME}=${sessionId}; HttpOnly; SameSite=Strict; Path=/`
  }

  private async handleHttpRequest(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const method = (req.method || 'GET').toUpperCase()
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`)
    const pathname = url.pathname

    try {
      if (pathname.startsWith('/api/')) {
        await this.handleApiRequest(method, pathname, url, req, res)
        return
      }

      if (method !== 'GET' && method !== 'HEAD') {
        sendJson(res, 405, { error: 'Method Not Allowed' })
        return
      }

      await this.serveRendererAsset(pathname, url.search, req, res)
    } catch (error) {
      const statusCode =
        typeof (error as { statusCode?: unknown })?.statusCode === 'number'
          ? (error as { statusCode: number }).statusCode
          : 500
      const message = error instanceof Error ? error.message : String(error)
      if (!res.headersSent) {
        sendJson(res, statusCode, { error: message })
      } else {
        res.end()
      }
    }
  }

  private async handleApiRequest(
    method: string,
    pathname: string,
    _url: URL,
    req: IncomingMessage,
    res: ServerResponse
  ): Promise<void> {
    const clientIp = getClientIp(req)

    if (method === 'GET' && pathname === '/api/auth/session') {
      const rateLimitedSeconds = this.getRemainingLockoutSeconds(clientIp)
      sendJson(res, 200, {
        authenticated: this.isAuthenticatedRequest(req),
        rateLimitedSeconds: rateLimitedSeconds > 0 ? rateLimitedSeconds : undefined
      })
      return
    }

    if (method === 'POST' && pathname === '/api/auth/login') {
      const waitSeconds = this.getRemainingLockoutSeconds(clientIp)
      if (waitSeconds > 0) {
        sendJson(res, 429, { error: `尝试次数过多，请在 ${waitSeconds} 秒后重试`, retryAfterSeconds: waitSeconds })
        return
      }
      const body = await readJsonBody<{ token?: string }>(req)
      const token = typeof body.token === 'string' ? body.token.trim() : ''
      if (!safeCompareToken(this.config.token, token)) {
        const retryAfterSeconds = this.recordAuthFailure(clientIp)
        if (retryAfterSeconds > 0) {
          sendJson(res, 429, {
            error: `口令错误次数过多，已锁定 ${retryAfterSeconds} 秒`,
            retryAfterSeconds
          })
          return
        }
        sendJson(res, 401, { error: '访问口令无效' })
        return
      }
      this.authFailures.delete(clientIp)
      const cookie = this.createSessionCookie()
      sendJson(res, 200, { ok: true }, { 'Set-Cookie': cookie })
      return
    }

    if (method === 'POST' && pathname === '/api/auth/logout') {
      const cookies = parseCookies(req.headers.cookie)
      const sessionId = cookies[SESSION_COOKIE_NAME]
      if (sessionId) this.sessions.delete(sessionId)
      sendJson(
        res,
        200,
        { ok: true },
        { 'Set-Cookie': `${SESSION_COOKIE_NAME}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0` }
      )
      return
    }

    if (!this.isAuthenticatedRequest(req)) {
      sendJson(res, 401, { error: '未登录或会话已失效，请重新输入访问口令' })
      return
    }

    if (method === 'GET' && pathname === '/api/app/info') {
      sendJson(res, 200, { ...this.options.getAppInfo(), runtimeMode: 'web' as const })
      return
    }

    if (method === 'POST' && pathname === '/api/daemon/connect') {
      const state = await this.options.daemon.connect()
      if (state.connected) {
        await this.options.ensureBrowserAttached().catch(() => undefined)
      }
      sendJson(res, 200, state)
      return
    }

    if (method === 'POST' && pathname === '/api/daemon/disconnect') {
      this.options.pendingApprovals.clear()
      this.options.daemon.disconnect()
      sendJson(res, 200, { ok: true })
      return
    }

    if (method === 'GET' && pathname === '/api/daemon/pending-approvals') {
      sendJson(res, 200, this.options.pendingApprovals.snapshot())
      return
    }

    if (method === 'POST' && pathname === '/api/daemon/request') {
      const body = await readJsonBody<{
        method?: string
        params?: Record<string, unknown>
        options?: DaemonRequestOptions
      }>(req)
      if (!body.method || typeof body.method !== 'string') {
        sendJson(res, 400, { error: '缺少 daemon method 参数' })
        return
      }
      if (body.method === 'chat') {
        await this.options.ensureBrowserAttached().catch(() => undefined)
      }
      const response = await this.options.daemon.request(body.method, body.params, body.options)
      if (body.method === 'agent.approve' || body.method === 'agent.approval') {
        this.options.pendingApprovals.acknowledge(body.params?.approvalId)
      }
      if (isMutatingDaemonMethod(body.method)) {
        const sourceClientId =
          typeof req.headers['x-seekclaw-client-id'] === 'string'
            ? req.headers['x-seekclaw-client-id'].trim() || undefined
            : undefined
        this.broadcastSyncRefresh(body.method, sourceClientId)
        this.options.onDesktopSyncRefresh?.(body.method)
      }
      sendJson(res, 200, response)
      return
    }

    if (method === 'POST' && pathname === '/api/project/open-terminal') {
      const body = await readJsonBody<{ path: string }>(req)
      await openProjectTerminal(body.path)
      sendJson(res, 200, { ok: true })
      return
    }
    if (method === 'POST' && pathname === '/api/project/git-overview') {
      const body = await readJsonBody<{ path: string }>(req)
      sendJson(res, 200, await getGitOverview(body.path))
      return
    }
    if (method === 'POST' && pathname === '/api/project/git-history') {
      const body = await readJsonBody<{ path: string }>(req)
      sendJson(res, 200, await getGitHistory(body.path))
      return
    }
    if (method === 'POST' && pathname === '/api/project/git-branches') {
      const body = await readJsonBody<{ path: string }>(req)
      sendJson(res, 200, await getGitBranches(body.path))
      return
    }
    if (method === 'POST' && pathname === '/api/project/checkout-branch') {
      const body = await readJsonBody<{ path: string; branch: string }>(req)
      sendJson(res, 200, await checkoutGitBranch(body.path, body.branch))
      return
    }
    if (method === 'POST' && pathname === '/api/project/create-branch') {
      const body = await readJsonBody<{ path: string; branch: string }>(req)
      sendJson(res, 200, await createGitBranch(body.path, body.branch))
      return
    }
    if (method === 'POST' && pathname === '/api/project/revert-file-diffs') {
      const body = await readJsonBody<{ workspace: string; patches: RevertDiffItem[] }>(req)
      sendJson(res, 200, await revertFileDiffs(body.workspace, body.patches))
      return
    }
    if (method === 'POST' && pathname === '/api/project/git-commit-diff') {
      const body = await readJsonBody<{ path: string; commitHash: string }>(req)
      sendJson(res, 200, { diff: await getGitCommitDiff(body.path, body.commitHash) })
      return
    }
    if (method === 'POST' && pathname === '/api/project/list-files') {
      const body = await readJsonBody<{ path: string; query?: string }>(req)
      sendJson(res, 200, await listWorkspaceFiles(body.path, body.query))
      return
    }

    if (method === 'POST' && pathname === '/api/browser/open') {
      const browser = this.options.getBrowser()
      if (!browser) {
        sendJson(res, 503, { error: '会话浏览器暂不可用' })
        return
      }
      const body = await readJsonBody<{ sessionId: string; workspace?: string; global?: boolean }>(req)
      const response = await this.options.daemon.request('browser.scope', body)
      const { scope, sessionId } = JSON.parse(response.data) as { scope: string; sessionId: string }
      const state = browser.ensure(scope, sessionId)
      if (!state.tabs.length) await browser.execute(scope, sessionId, { action: 'open' })
      sendJson(res, 200, browser.state(scope))
      return
    }
    if (method === 'GET' && pathname === '/api/browser/state') {
      const browser = this.options.getBrowser()
      if (!browser) {
        sendJson(res, 200, null)
        return
      }
      const sessionId = _url.searchParams.get('sessionId') || ''
      sendJson(res, 200, browser.stateForSession(sessionId))
      return
    }
    if (method === 'POST' && pathname === '/api/browser/command') {
      const browser = this.options.getBrowser()
      if (!browser) {
        sendJson(res, 503, { error: '会话浏览器暂不可用' })
        return
      }
      const body = await readJsonBody<{ scope: string; command: BrowserCommand }>(req)
      const state = browser.state(body.scope)
      try {
        await browser.execute(body.scope, state.sessionId, body.command)
      } catch (error) {
        browser.recordCommandError(body.scope, body.command, error)
      }
      sendJson(res, 200, browser.state(body.scope))
      return
    }
    if (method === 'POST' && pathname === '/api/browser/pause') {
      const browser = this.options.getBrowser()
      if (!browser) {
        sendJson(res, 503, { error: '会话浏览器暂不可用' })
        return
      }
      const body = await readJsonBody<{ scope: string; paused: boolean }>(req)
      sendJson(res, 200, browser.pause(body.scope, !!body.paused))
      return
    }

    if (method === 'GET' && pathname === '/api/fs/roots') {
      const info = this.options.getAppInfo()
      let projects: Array<{ id: string; name: string; path: string }> = []
      if (this.options.daemon.state.connected) {
        try {
          const msg = await this.options.daemon.request('project.list')
          const parsed = JSON.parse(msg.data) as { projects?: Array<{ id: string; name: string; path: string }> }
          if (Array.isArray(parsed.projects)) projects = parsed.projects
        } catch {
          // ignore if project list fails
        }
      }
      sendJson(
        res,
        200,
        listServerRoots({
          userProfile: info.userProfilePath,
          documentsPath: info.documentsPath,
          projects
        })
      )
      return
    }
    if (method === 'POST' && pathname === '/api/fs/list') {
      const info = this.options.getAppInfo()
      const body = await readJsonBody<{ path?: string; kind?: ServerFilePickerKind; showHidden?: boolean }>(req)
      const result = await listServerDirectory({
        path: body.path,
        kind: body.kind,
        showHidden: body.showHidden,
        fallbackPath: info.userProfilePath
      })
      sendJson(res, 200, result)
      return
    }
    if (method === 'POST' && pathname === '/api/fs/mkdir') {
      const body = await readJsonBody<{ parentPath: string; name: string }>(req)
      sendJson(res, 200, await createServerDirectory(body.parentPath, body.name))
      return
    }
    if (method === 'POST' && pathname === '/api/fs/read-images') {
      const body = await readJsonBody<{ paths: string[] }>(req)
      sendJson(res, 200, await readServerImages(Array.isArray(body.paths) ? body.paths : []))
      return
    }
    if (method === 'POST' && pathname === '/api/fs/read-base64') {
      const body = await readJsonBody<{ path: string }>(req)
      sendJson(res, 200, await readServerFileBase64(body.path))
      return
    }
    if (method === 'POST' && pathname === '/api/fs/preview') {
      const body = await readJsonBody<{ href: string; workspace?: string }>(req)
      sendJson(res, 200, await readLocalLinkPreview(typeof body.href === 'string' ? body.href : '', body.workspace))
      return
    }
    if (method === 'POST' && pathname === '/api/fs/upload') {
      const info = this.options.getAppInfo()
      const body = await readJsonBody<{ name: string; dataBase64: string; targetDir?: string }>(
        req,
        UPLOAD_JSON_LIMIT_BYTES
      )
      const saved = await saveUploadedFile({
        name: body.name,
        dataBase64: body.dataBase64,
        targetDir: body.targetDir,
        fallbackTempDir: join(info.userProfilePath, '.seekclaw', 'uploads')
      })
      sendJson(res, 200, saved)
      return
    }

    if (method === 'GET' && pathname === '/api/web-server/status') {
      sendJson(res, 200, this.getStatus())
      return
    }
    if (method === 'POST' && pathname === '/api/web-server/update') {
      const patch = await readJsonBody<Partial<WebServerConfig>>(req)
      if (patch.enabled === false) {
        const nextConfig: WebServerConfig = {
          ...this.config,
          ...patch,
          enabled: false
        }
        this.config = nextConfig
        await this.saveConfig()
        const responseStatus: WebServerStatus = {
          ...this.getStatus(),
          running: false
        }
        sendJson(res, 200, responseStatus)
        setTimeout(() => {
          void this.stop()
        }, 120)
        return
      }
      const nextStatus = await this.updateConfig(patch)
      sendJson(res, 200, nextStatus)
      return
    }
    if (method === 'POST' && pathname === '/api/web-server/regenerate-token') {
      const nextStatus = await this.regenerateToken()
      sendJson(res, 200, nextStatus)
      return
    }
    if (method === 'POST' && pathname === '/api/web-server/disconnect-clients') {
      const nextStatus = this.disconnectClients()
      sendJson(res, 200, nextStatus)
      return
    }

    sendJson(res, 404, { error: `Unknown API route: ${method} ${pathname}` })
  }

  private handleUpgrade(req: IncomingMessage, socket: import('node:stream').Duplex, head: Buffer): void {
    const preAuthErrorGuard = (): void => {
      try {
        socket.destroy()
      } catch {
        // ignore destroy error
      }
    }
    socket.on('error', preAuthErrorGuard)

    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`)
    if (url.pathname !== '/api/ws') {
      try {
        socket.write('HTTP/1.1 404 Not Found\r\n\r\n')
      } catch {
        // ignore write error
      }
      socket.destroy()
      return
    }

    if (!this.isAuthenticatedRequest(req, url.searchParams.get('token'))) {
      try {
        socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n')
      } catch {
        // ignore write error
      }
      socket.destroy()
      return
    }

    const ws = upgradeToWebSocket(req, socket, head)
    socket.removeListener('error', preAuthErrorGuard)
    if (!ws) return

    const clientId = url.searchParams.get('clientId')?.trim() || undefined
    const client: WsClientRecord = { ws, clientId }
    this.wsClients.add(client)

    ws.on('message', (text: string) => {
      void this.handleWsMessage(client, text)
    })
    ws.on('error', () => {
      this.cleanupWsClient(client)
      this.emitStatus()
    })
    ws.on('close', () => {
      this.cleanupWsClient(client)
      this.emitStatus()
    })

    this.emitStatus()
    ws.sendJson({ type: 'daemon:state', payload: this.options.daemon.state })
    ws.sendJson({ type: 'web-server:status', payload: this.getStatus() })
    for (const pending of this.options.pendingApprovals.snapshot()) {
      ws.sendJson({ type: 'daemon:event', payload: pending })
    }
  }

  private async handleWsMessage(client: WsClientRecord, text: string): Promise<void> {
    let message: Record<string, unknown>
    try {
      message = JSON.parse(text) as Record<string, unknown>
    } catch {
      return
    }

    const type = message.type
    if (type === 'ping') {
      client.ws.sendJson({ type: 'pong' })
      return
    }

    const browser = this.options.getBrowser()
    if (type === 'browser:stream:start') {
      const scope = typeof message.scope === 'string' ? message.scope : ''
      if (!browser || !scope) return
      if (client.streamUnsub) {
        client.streamUnsub()
        client.streamUnsub = undefined
      }
      client.streamScope = scope
      const width = typeof message.width === 'number' ? message.width : 520
      const height = typeof message.height === 'number' ? message.height : 680
      client.streamUnsub = browser.startRemoteStream(scope, { width, height }, (frame) => {
        if (client.ws.isOpen) {
          client.ws.sendJson({ type: 'browser:frame', payload: frame })
        }
      })
      return
    }

    if (type === 'browser:stream:stop') {
      if (client.streamUnsub) {
        client.streamUnsub()
        client.streamUnsub = undefined
        client.streamScope = undefined
      }
      return
    }

    if (type === 'browser:input') {
      const scope = typeof message.scope === 'string' ? message.scope : ''
      const input = message.input as RemoteBrowserInput | undefined
      if (!browser || !scope || !input || typeof input !== 'object') return
      await browser.dispatchRemoteInput(scope, input).catch(() => undefined)
    }
  }

  private async serveRendererAsset(
    pathname: string,
    search: string,
    req: IncomingMessage,
    res: ServerResponse
  ): Promise<void> {
    const source = this.options.getRendererSource()
    if (source.devUrl) {
      const targetUrl = new URL(pathname + search, source.devUrl)
      const upstream = await fetch(targetUrl, {
        method: req.method || 'GET',
        headers: {
          accept: req.headers.accept || '*/*'
        }
      })
      const headers: Record<string, string> = {}
      const contentType = upstream.headers.get('content-type')
      if (contentType) headers['Content-Type'] = contentType
      const cacheControl = upstream.headers.get('cache-control')
      if (cacheControl) headers['Cache-Control'] = cacheControl
      res.writeHead(upstream.status, headers)
      if (req.method === 'HEAD') {
        res.end()
        return
      }
      const buffer = Buffer.from(await upstream.arrayBuffer())
      res.end(buffer)
      return
    }

    const distDir = resolve(source.distDir)
    const relativePath = pathname === '/' ? 'index.html' : normalize(decodeURIComponent(pathname)).replace(/^[/\\]+/, '')
    let resolvedFile = resolve(distDir, relativePath)

    if (resolvedFile !== distDir && !resolvedFile.startsWith(distDir + sep)) {
      sendJson(res, 403, { error: 'Forbidden' })
      return
    }

    const isExistingFile = existsSync(resolvedFile) && statSync(resolvedFile).isFile()
    if (!isExistingFile) {
      if (!extname(relativePath)) {
        resolvedFile = join(distDir, 'index.html')
      } else {
        sendJson(res, 404, { error: 'Not Found' })
        return
      }
    }

    const ext = extname(resolvedFile).toLowerCase()
    const mime = MIME_TYPES[ext] || 'application/octet-stream'
    const data = await readFile(resolvedFile)
    res.writeHead(200, {
      'Content-Type': mime,
      'Cache-Control': ext === '.html' ? 'no-cache' : 'public, max-age=3600'
    })
    res.end(req.method === 'HEAD' ? undefined : data)
  }
}
