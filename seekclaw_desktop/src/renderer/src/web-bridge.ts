import type { BrowserCommand, BrowserPanelLayout, ConversationBrowserState } from '@shared/browser'
import type {
  AppInfo,
  DaemonMessage,
  DaemonRequestOptions,
  DaemonState,
  DesktopApi,
  FilePreviewPayload,
  GitBranchInfo,
  GitHistory,
  GitOverview,
  RevertDiffItem,
  RevertDiffsResult,
  WorkspaceFileItem
} from '@shared/ipc'
import type {
  RemoteBrowserFrame,
  RemoteBrowserInput,
  ServerFilePickerKind,
  ServerFsListResult,
  ServerFsRoot,
  WebServerConfig,
  WebServerStatus
} from '@shared/web-server'
import { openServerFilePicker } from './server-file-picker'
import { resolveMarkdownLinkTarget } from '../../shared/markdown-links'

type Listener<T> = (payload: T) => void

export class WebUnauthorizedError extends Error {
  constructor(message = '会话已失效，请重新验证访问口令') {
    super(message)
    this.name = 'WebUnauthorizedError'
  }
}

let unauthorizedHandler: (() => void) | null = null

const webClientId =
  typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `web-${Date.now()}-${Math.random().toString(36).slice(2)}`

export function setWebUnauthorizedHandler(handler: (() => void) | null): void {
  unauthorizedHandler = handler
}

async function apiJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    credentials: 'same-origin',
    ...init,
    headers: {
      'X-SeekClaw-Client-Id': webClientId,
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init?.headers || {})
    }
  })
  if (response.status === 401) {
    unauthorizedHandler?.()
    throw new WebUnauthorizedError()
  }
  const text = await response.text()
  let parsed: unknown = null
  if (text) {
    try {
      parsed = JSON.parse(text)
    } catch {
      parsed = null
    }
  }
  if (!response.ok) {
    const errorMessage =
      parsed && typeof parsed === 'object' && 'error' in parsed && typeof (parsed as { error?: unknown }).error === 'string'
        ? (parsed as { error: string }).error
        : `HTTP ${response.status}`
    throw new Error(errorMessage)
  }
  return parsed as T
}

export async function checkWebAuthSession(): Promise<{ authenticated: boolean; rateLimitedSeconds?: number }> {
  const response = await fetch('/api/auth/session', { credentials: 'same-origin' })
  if (!response.ok) return { authenticated: false }
  return (await response.json()) as { authenticated: boolean; rateLimitedSeconds?: number }
}

export async function loginWebWithToken(
  token: string
): Promise<{ ok: boolean; error?: string; retryAfterSeconds?: number }> {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token })
  })
  const data = (await response.json().catch(() => ({}))) as {
    ok?: boolean
    error?: string
    retryAfterSeconds?: number
  }
  if (response.ok && data.ok) {
    return { ok: true }
  }
  return {
    ok: false,
    error: data.error || '访问口令无效',
    retryAfterSeconds: data.retryAfterSeconds
  }
}

export async function tryConsumeUrlToken(): Promise<boolean> {
  const url = new URL(window.location.href)
  const token = url.searchParams.get('token')
  if (!token) return false
  url.searchParams.delete('token')
  const cleanUrl = `${url.pathname}${url.search}${url.hash}`
  window.history.replaceState({}, '', cleanUrl || '/')
  const result = await loginWebWithToken(token)
  return result.ok
}

class WebSocketClientHub {
  private ws: WebSocket | null = null
  private reconnectTimer: number | null = null
  private pingTimer: number | null = null
  private closedExplicitly = false

  readonly daemonEventListeners = new Set<Listener<DaemonMessage>>()
  readonly daemonStateListeners = new Set<Listener<DaemonState>>()
  readonly browserChangedListeners = new Set<Listener<ConversationBrowserState>>()
  readonly webServerStatusListeners = new Set<Listener<WebServerStatus>>()
  readonly syncRefreshListeners = new Set<Listener<string | undefined>>()
  readonly browserFrameListeners = new Map<string, Set<Listener<RemoteBrowserFrame>>>()
  private activeStream: { scope: string; width: number; height: number } | null = null

  connect(): void {
    this.closedExplicitly = false
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) {
      return
    }
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    const ws = new WebSocket(`${protocol}//${window.location.host}/api/ws?clientId=${encodeURIComponent(webClientId)}`)
    this.ws = ws

    ws.addEventListener('open', () => {
      this.startPing()
      if (this.activeStream) {
        this.send({
          type: 'browser:stream:start',
          scope: this.activeStream.scope,
          width: this.activeStream.width,
          height: this.activeStream.height
        })
      }
    })

    ws.addEventListener('message', (event) => {
      if (typeof event.data !== 'string') return
      let parsed: { type?: string; payload?: unknown }
      try {
        parsed = JSON.parse(event.data) as { type?: string; payload?: unknown }
      } catch {
        return
      }
      if (parsed.type === 'daemon:event') {
        const payload = parsed.payload as DaemonMessage
        for (const listener of this.daemonEventListeners) listener(payload)
      } else if (parsed.type === 'daemon:state') {
        const payload = parsed.payload as DaemonState
        for (const listener of this.daemonStateListeners) listener(payload)
      } else if (parsed.type === 'browser:changed') {
        const payload = parsed.payload as ConversationBrowserState
        for (const listener of this.browserChangedListeners) listener(payload)
      } else if (parsed.type === 'web-server:status') {
        const payload = parsed.payload as WebServerStatus
        for (const listener of this.webServerStatusListeners) listener(payload)
      } else if (parsed.type === 'sync:refresh') {
        const reason = (parsed.payload as { reason?: string } | undefined)?.reason
        for (const listener of this.syncRefreshListeners) listener(reason)
      } else if (parsed.type === 'browser:frame') {
        const frame = parsed.payload as RemoteBrowserFrame
        const listeners = this.browserFrameListeners.get(frame.scope)
        if (listeners) {
          for (const listener of listeners) listener(frame)
        }
      }
    })

    ws.addEventListener('close', (event) => {
      this.stopPing()
      this.ws = null
      if (event.code === 4001) {
        unauthorizedHandler?.()
        return
      }
      if (!this.closedExplicitly) {
        this.scheduleReconnect()
      }
    })
  }

  disconnect(): void {
    this.closedExplicitly = true
    this.stopPing()
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }
    if (this.ws) {
      this.ws.close()
      this.ws = null
    }
  }

  send(payload: unknown): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(payload))
    }
  }

  subscribeStream(
    scope: string,
    viewport: { width: number; height: number },
    listener: Listener<RemoteBrowserFrame>
  ): () => void {
    let set = this.browserFrameListeners.get(scope)
    if (!set) {
      set = new Set()
      this.browserFrameListeners.set(scope, set)
    }
    set.add(listener)
    this.activeStream = { scope, width: viewport.width, height: viewport.height }
    this.connect()
    this.send({ type: 'browser:stream:start', scope, width: viewport.width, height: viewport.height })

    return () => {
      const current = this.browserFrameListeners.get(scope)
      if (!current) return
      current.delete(listener)
      if (current.size === 0) {
        this.browserFrameListeners.delete(scope)
        if (this.activeStream?.scope === scope) {
          this.activeStream = null
          this.send({ type: 'browser:stream:stop' })
        }
      }
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer !== null) return
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null
      this.connect()
    }, 1500)
  }

  private startPing(): void {
    this.stopPing()
    this.pingTimer = window.setInterval(() => {
      this.send({ type: 'ping' })
    }, 20_000)
  }

  private stopPing(): void {
    if (this.pingTimer !== null) {
      window.clearInterval(this.pingTimer)
      this.pingTimer = null
    }
  }
}

let activeHub: WebSocketClientHub | null = null

export function resetWebBridgeConnection(): void {
  activeHub?.disconnect()
  activeHub = null
  delete (window as { seekclaw?: DesktopApi }).seekclaw
}

export function installWebBridge(): DesktopApi {
  if (window.seekclaw) return window.seekclaw

  const hub = new WebSocketClientHub()
  activeHub = hub
  hub.connect()

  let currentZoomFactor = Number(window.localStorage.getItem('seekclaw-web-zoom') || '1')
  if (!Number.isFinite(currentZoomFactor) || currentZoomFactor < 0.5 || currentZoomFactor > 3) {
    currentZoomFactor = 1
  }

  const serverFsApi: NonNullable<DesktopApi['serverFs']> = {
    roots: () => apiJson<ServerFsRoot[]>('/api/fs/roots'),
    list: (params: { path?: string; kind?: ServerFilePickerKind; showHidden?: boolean }) =>
      apiJson<ServerFsListResult>('/api/fs/list', {
        method: 'POST',
        body: JSON.stringify(params)
      }),
    mkdir: (parentPath: string, name: string) =>
      apiJson<{ path: string; name: string }>('/api/fs/mkdir', {
        method: 'POST',
        body: JSON.stringify({ parentPath, name })
      }),
    readImages: (paths: string[]) =>
      apiJson('/api/fs/read-images', {
        method: 'POST',
        body: JSON.stringify({ paths })
      }),
    uploadFile: (params: { name: string; dataBase64: string; targetDir?: string }) =>
      apiJson<{ path: string; name: string; sizeBytes: number }>('/api/fs/upload', {
        method: 'POST',
        body: JSON.stringify(params)
      })
  }

  const api: DesktopApi = {
    browser: {
      open: (params) =>
        apiJson<ConversationBrowserState>('/api/browser/open', {
          method: 'POST',
          body: JSON.stringify(params)
        }),
      state: (sessionId) =>
        apiJson<ConversationBrowserState | null>(`/api/browser/state?sessionId=${encodeURIComponent(sessionId)}`),
      command: (scope, command: BrowserCommand) =>
        apiJson<ConversationBrowserState>('/api/browser/command', {
          method: 'POST',
          body: JSON.stringify({ scope, command })
        }),
      pause: (scope, paused) =>
        apiJson<ConversationBrowserState>('/api/browser/pause', {
          method: 'POST',
          body: JSON.stringify({ scope, paused })
        }),
      layout: async (_layout: BrowserPanelLayout | null) => undefined,
      overlay: async (_visible: boolean) => undefined,
      onChanged: (listener) => {
        hub.browserChangedListeners.add(listener)
        return () => hub.browserChangedListeners.delete(listener)
      },
      subscribeStream: (scope, viewport, listener) => hub.subscribeStream(scope, viewport, listener),
      sendRemoteInput: async (scope: string, input: RemoteBrowserInput) => {
        hub.send({ type: 'browser:input', scope, input })
      }
    },
    getAppInfo: () => apiJson<AppInfo>('/api/app/info'),
    selectWorkspace: async () => {
      const picked = await openServerFilePicker({
        kind: 'directory',
        title: '选择服务器工作区目录',
        multiple: false
      })
      return picked?.[0] ?? null
    },
    selectImages: async () => {
      const picked = await openServerFilePicker({
        kind: 'images',
        title: '选择服务器图片（或从当前设备上传）',
        multiple: true
      })
      if (!picked || picked.length === 0) return { images: [] }
      return serverFsApi.readImages(picked)
    },
    selectFiles: async () => {
      const picked = await openServerFilePicker({
        kind: 'files',
        title: '选择服务器文件（或从当前设备上传）',
        multiple: true
      })
      return picked ?? []
    },
    readFileBase64: (path: string) =>
      apiJson<{ data: string; mediaType: string; sizeBytes: number } | null>('/api/fs/read-base64', {
        method: 'POST',
        body: JSON.stringify({ path })
      }),
    getPathForFile: (file: File) => (file as File & { path?: string }).path || '',
    selectSkillFiles: async () => {
      const picked = await openServerFilePicker({
        kind: 'skills',
        title: '从服务器导入全局技能 (.md / .zip)',
        multiple: true
      })
      return { paths: picked ?? [] }
    },
    showItemInFolder: async () => undefined,
    openLink: async (href: string, workspace?: string) => {
      const resolved = resolveMarkdownLinkTarget(href, workspace)
      if (resolved.kind === 'external') {
        window.open(resolved.url, '_blank', 'noopener,noreferrer')
        return { ok: true, kind: 'external', target: resolved.url }
      }
      if (resolved.kind === 'fragment') {
        return { ok: true, kind: 'external' }
      }
      if (resolved.kind === 'local') {
        return { ok: false, error: '网页访问模式下暂不支持直接打开宿主机本地文件' }
      }
      return { ok: false, error: resolved.reason }
    },
    readLinkPreview: (href: string, workspace?: string) =>
      apiJson<FilePreviewPayload>('/api/fs/preview', {
        method: 'POST',
        body: JSON.stringify({ href, workspace })
      }),
    closeApp: async () => undefined,
    exitApp: async () => undefined,
    openDevTools: async () => undefined,
    setTheme: async () => undefined,
    setZoomFactor: (factor: number) => {
      if (Number.isFinite(factor) && factor >= 0.5 && factor <= 3) {
        currentZoomFactor = factor
        window.localStorage.setItem('seekclaw-web-zoom', String(factor))
        document.documentElement.style.zoom = String(factor)
      }
    },
    getZoomFactor: () => currentZoomFactor,
    notify: async (title: string, body: string) => {
      if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
        new Notification(title, { body })
      }
    },
    project: {
      openTerminal: (path: string) =>
        apiJson('/api/project/open-terminal', { method: 'POST', body: JSON.stringify({ path }) }),
      gitOverview: (path: string) =>
        apiJson<GitOverview>('/api/project/git-overview', { method: 'POST', body: JSON.stringify({ path }) }),
      gitHistory: (path: string) =>
        apiJson<GitHistory>('/api/project/git-history', { method: 'POST', body: JSON.stringify({ path }) }),
      gitBranches: (path: string) =>
        apiJson<GitBranchInfo[]>('/api/project/git-branches', { method: 'POST', body: JSON.stringify({ path }) }),
      checkoutBranch: (path: string, branch: string) =>
        apiJson('/api/project/checkout-branch', { method: 'POST', body: JSON.stringify({ path, branch }) }),
      createBranch: (path: string, branch: string) =>
        apiJson('/api/project/create-branch', { method: 'POST', body: JSON.stringify({ path, branch }) }),
      revertFileDiffs: (workspace: string, patches: RevertDiffItem[]) =>
        apiJson<RevertDiffsResult>('/api/project/revert-file-diffs', {
          method: 'POST',
          body: JSON.stringify({ workspace, patches })
        }),
      gitCommitDiff: async (path: string, commitHash: string) => {
        const res = await apiJson<{ diff: string }>('/api/project/git-commit-diff', {
          method: 'POST',
          body: JSON.stringify({ path, commitHash })
        })
        return res.diff
      },
      listFiles: (path: string, query?: string) =>
        apiJson<WorkspaceFileItem[]>('/api/project/list-files', {
          method: 'POST',
          body: JSON.stringify({ path, query })
        })
    },
    daemon: {
      connect: async () => {
        hub.connect()
        return apiJson<DaemonState>('/api/daemon/connect', { method: 'POST' })
      },
      disconnect: () => apiJson('/api/daemon/disconnect', { method: 'POST' }),
      request: (method: string, params?: Record<string, unknown>, options?: DaemonRequestOptions) =>
        apiJson<DaemonMessage>('/api/daemon/request', {
          method: 'POST',
          body: JSON.stringify({ method, params, options })
        }),
      onEvent: (listener) => {
        hub.daemonEventListeners.add(listener)
        return () => hub.daemonEventListeners.delete(listener)
      },
      onState: (listener) => {
        hub.daemonStateListeners.add(listener)
        return () => hub.daemonStateListeners.delete(listener)
      }
    },
    webServer: {
      status: () => apiJson<WebServerStatus>('/api/web-server/status'),
      update: (patch: Partial<WebServerConfig>) =>
        apiJson<WebServerStatus>('/api/web-server/update', {
          method: 'POST',
          body: JSON.stringify(patch)
        }),
      regenerateToken: () => apiJson<WebServerStatus>('/api/web-server/regenerate-token', { method: 'POST' }),
      disconnectClients: () => apiJson<WebServerStatus>('/api/web-server/disconnect-clients', { method: 'POST' }),
      onStatus: (listener) => {
        hub.webServerStatusListeners.add(listener)
        return () => hub.webServerStatusListeners.delete(listener)
      }
    },
    onSyncRefresh: (listener) => {
      hub.syncRefreshListeners.add(listener)
      return () => hub.syncRefreshListeners.delete(listener)
    },
    serverFs: serverFsApi
  }

  Object.defineProperty(window, 'seekclaw', {
    value: api,
    configurable: true,
    enumerable: true
  })
  return api
}
