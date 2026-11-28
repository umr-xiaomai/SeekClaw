import { app, BrowserWindow, WebContentsView, session } from 'electron'
import { randomUUID } from 'node:crypto'
import { existsSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { basename, extname, join } from 'node:path'
import type {
  BrowserApprovalDecision,
  BrowserApprovalRequest,
  BrowserCommand,
  BrowserPanelLayout,
  ConversationBrowserState
} from '../shared/browser.js'
import type { RemoteBrowserFrame, RemoteBrowserInput } from '../shared/web-server.js'
import {
  browserCursorHideScript,
  browserCursorMoveScript,
  type VirtualCursorEffect,
  type VirtualCursorMode
} from './browser-cursor.js'
import { browserPageScript } from './browser-page.js'
import { browserBounds, browserUrl } from './browser-policy.js'

interface ConsoleLogEntry {
  level: string
  message: string
  source?: string
  line?: number
  timestamp: number
}

interface NetworkLogEntry {
  requestId: string
  url: string
  method: string
  type: string
  status?: number
  statusText?: string
  ok?: boolean
  error?: string
  timestamp: number
  durationMs?: number
}

interface DownloadRecord {
  id: string
  url: string
  filename: string
  savePath: string
  totalBytes: number
  receivedBytes: number
  state: 'in_progress' | 'completed' | 'cancelled' | 'interrupted'
  startedAt: number
  finishedAt?: number
}

interface Tab {
  id: string
  view: WebContentsView
  error?: string
  targetUrl?: string
  ready: Promise<void>
  hostWindow?: 'main' | 'remote'
  screencastKey?: string
  cursor?: { x: number; y: number }
  consoleLogs: ConsoleLogEntry[]
  networkLogs: NetworkLogEntry[]
  pendingNetwork: Map<string, NetworkLogEntry>
}

interface BrowserSession {
  scope: string
  sessionId: string
  tabs: Map<string, Tab>
  activeTabId?: string
  paused: boolean
  busy: boolean
  queue: Promise<unknown>
  sessionConfigured?: boolean
  permissionDecisions: Map<string, boolean>
  preApprovedDownloads: Set<string>
  downloads: DownloadRecord[]
  downloadWaiters: Array<(record: DownloadRecord) => void>
}

interface RemoteViewer {
  id: string
  width: number
  height: number
  onFrame: (frame: RemoteBrowserFrame) => void
}

export type BrowserApprovalHandler = (req: BrowserApprovalRequest) => Promise<BrowserApprovalDecision>

const MAX_LOG_ENTRIES = 200
const MAX_DOWNLOAD_ENTRIES = 50
const MAX_EVALUATE_BYTES = 32 * 1024

function pushCapped<T>(list: T[], entry: T, limit: number): void {
  list.push(entry)
  if (list.length > limit) {
    list.splice(0, list.length - limit)
  }
}

function isBlankOrErrorUrl(url?: string): boolean {
  return !url || url === 'about:blank' || url.startsWith('chrome-error://')
}

function fallbackTitleForUrl(url: string): string {
  if (isBlankOrErrorUrl(url)) return '无法访问页面'
  try {
    return new URL(url).hostname || url
  } catch {
    return url
  }
}

function originForUrl(url: string): string {
  try {
    const parsed = new URL(url)
    return parsed.origin === 'null' ? url : parsed.origin
  } catch {
    return url
  }
}

function resolveDownloadsDir(): string {
  try {
    const dir = app?.getPath?.('downloads')
    if (dir) return dir
  } catch {
    // ignore outside Electron app lifecycle
  }
  const candidate = join(homedir(), 'Downloads')
  return existsSync(candidate) ? candidate : tmpdir()
}

function sanitizeDownloadFilename(rawName: string): string {
  const base = basename(rawName || 'download.bin').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').trim()
  return base || 'download.bin'
}

function filenameFromUrl(url: string): string {
  try {
    const parsed = new URL(url)
    const seg = parsed.pathname.split('/').filter(Boolean).pop()
    if (seg) return decodeURIComponent(seg)
  } catch {
    // ignore
  }
  return 'download.bin'
}

function uniqueDownloadPath(filename: string): string {
  const dir = resolveDownloadsDir()
  const safe = sanitizeDownloadFilename(filename)
  let candidate = join(dir, safe)
  if (!existsSync(candidate)) return candidate
  const ext = extname(safe)
  const stem = ext ? safe.slice(0, -ext.length) : safe
  for (let i = 1; i <= 999; i++) {
    candidate = join(dir, `${stem} (${i})${ext}`)
    if (!existsSync(candidate)) return candidate
  }
  return join(dir, `${stem}-${Date.now()}${ext}`)
}

function normalizePermissionName(raw: string, mediaTypes?: string[]): string {
  const lower = raw.trim().toLowerCase()
  if (lower === 'media') {
    const set = new Set((mediaTypes ?? []).map((t) => t.toLowerCase()))
    if (set.has('video') && set.has('audio')) return 'camera+microphone'
    if (set.has('video')) return 'camera'
    if (set.has('audio')) return 'microphone'
    return 'media'
  }
  if (lower === 'camera' || lower === 'video' || lower === 'webcam') return 'camera'
  if (lower === 'microphone' || lower === 'mic' || lower === 'audio' || lower === 'microphone-access') return 'microphone'
  if (lower === 'geolocation' || lower === 'location') return 'geolocation'
  if (lower === 'notifications' || lower === 'notification') return 'notifications'
  if (lower === 'clipboard-read' || lower === 'clipboard-sanitized-write' || lower === 'clipboard') return 'clipboard-read'
  if (lower === 'display-capture' || lower === 'screen' || lower === 'desktopcapture') return 'display-capture'
  if (lower === 'midi' || lower === 'midisysex') return 'midi'
  return lower
}

export function formatPermissionLabel(permission: string, mediaTypes?: string[]): string {
  const normalized = normalizePermissionName(permission, mediaTypes)
  switch (normalized) {
    case 'camera':
      return '摄像头'
    case 'microphone':
      return '麦克风'
    case 'camera+microphone':
    case 'media':
      return '摄像头与麦克风'
    case 'geolocation':
      return '位置信息（定位）'
    case 'notifications':
      return '系统通知'
    case 'clipboard-read':
      return '剪贴板读取'
    case 'display-capture':
      return '屏幕/窗口共享'
    case 'midi':
      return 'MIDI 设备'
    default:
      return permission || '浏览器权限'
  }
}

interface KeyDescriptor {
  key: string
  code: string
  keyCode: number
  text?: string
  shiftRequired?: boolean
}

const SPECIAL_KEYS: Record<string, KeyDescriptor> = {
  enter: { code: 'Enter', key: 'Enter', keyCode: 13, text: '\r' },
  return: { code: 'Enter', key: 'Enter', keyCode: 13, text: '\r' },
  tab: { code: 'Tab', key: 'Tab', keyCode: 9 },
  escape: { code: 'Escape', key: 'Escape', keyCode: 27 },
  esc: { code: 'Escape', key: 'Escape', keyCode: 27 },
  backspace: { code: 'Backspace', key: 'Backspace', keyCode: 8 },
  delete: { code: 'Delete', key: 'Delete', keyCode: 46 },
  del: { code: 'Delete', key: 'Delete', keyCode: 46 },
  insert: { code: 'Insert', key: 'Insert', keyCode: 45 },
  space: { code: 'Space', key: ' ', keyCode: 32, text: ' ' },
  ' ': { code: 'Space', key: ' ', keyCode: 32, text: ' ' },
  home: { code: 'Home', key: 'Home', keyCode: 36 },
  end: { code: 'End', key: 'End', keyCode: 35 },
  pageup: { code: 'PageUp', key: 'PageUp', keyCode: 33 },
  pagedown: { code: 'PageDown', key: 'PageDown', keyCode: 34 },
  arrowup: { code: 'ArrowUp', key: 'ArrowUp', keyCode: 38 },
  up: { code: 'ArrowUp', key: 'ArrowUp', keyCode: 38 },
  arrowdown: { code: 'ArrowDown', key: 'ArrowDown', keyCode: 40 },
  down: { code: 'ArrowDown', key: 'ArrowDown', keyCode: 40 },
  arrowleft: { code: 'ArrowLeft', key: 'ArrowLeft', keyCode: 37 },
  left: { code: 'ArrowLeft', key: 'ArrowLeft', keyCode: 37 },
  arrowright: { code: 'ArrowRight', key: 'ArrowRight', keyCode: 39 },
  right: { code: 'ArrowRight', key: 'ArrowRight', keyCode: 39 }
}

for (let n = 1; n <= 12; n++) {
  SPECIAL_KEYS[`f${n}`] = { code: `F${n}`, key: `F${n}`, keyCode: 111 + n }
}

function resolveKeySpec(rawKey: string, extraModifiers?: string[]): { key: KeyDescriptor; modifiers: number; editingCommand?: string } {
  const parts = rawKey.split('+').map((p) => p.trim()).filter(Boolean)
  const modNames = new Set((extraModifiers ?? []).map((m) => m.toLowerCase()))
  let keyToken = parts.pop() || ''
  for (const prefix of parts) {
    modNames.add(prefix.toLowerCase())
  }
  if (!keyToken && parts.length === 0) throw new Error('Unsupported browser key')

  let modifiers = 0
  if (modNames.has('alt') || modNames.has('option')) modifiers |= 1
  if (modNames.has('ctrl') || modNames.has('control') || modNames.has('ctrlcmd')) modifiers |= 2
  if (modNames.has('meta') || modNames.has('cmd') || modNames.has('command') || modNames.has('win') || modNames.has('super')) modifiers |= 4
  if (modNames.has('shift')) modifiers |= 8

  const lower = keyToken.toLowerCase()
  let desc: KeyDescriptor | undefined = SPECIAL_KEYS[lower]
  if (!desc && keyToken.length === 1) {
    const codePoint = keyToken.charCodeAt(0)
    if (lower >= 'a' && lower <= 'z') {
      const upper = lower.toUpperCase()
      const hasShift = Boolean(modifiers & 8) || ((modifiers & (2 | 4 | 1)) === 0 && keyToken !== lower)
      desc = {
        key: hasShift ? upper : lower,
        code: `Key${upper}`,
        keyCode: upper.charCodeAt(0),
        text: (modifiers & (2 | 4 | 1)) === 0 ? (hasShift ? upper : lower) : undefined
      }
    } else if (lower >= '0' && lower <= '9') {
      desc = {
        key: keyToken,
        code: `Digit${keyToken}`,
        keyCode: codePoint,
        text: (modifiers & (2 | 4 | 1)) === 0 ? keyToken : undefined
      }
    } else if (codePoint >= 33 && codePoint <= 126) {
      desc = {
        key: keyToken,
        code: keyToken,
        keyCode: codePoint,
        text: (modifiers & (2 | 4 | 1)) === 0 ? keyToken : undefined
      }
    }
  }
  if (!desc) throw new Error(`Unsupported browser key: ${rawKey}`)

  const isPrimaryMod = Boolean((modifiers & 2) || (modifiers & 4)) && !(modifiers & 1)
  let editingCommand: string | undefined
  if (isPrimaryMod && !(modifiers & 8)) {
    if (lower === 'a') editingCommand = 'selectAll'
    else if (lower === 'c') editingCommand = 'copy'
    else if (lower === 'x') editingCommand = 'cut'
    else if (lower === 'v') editingCommand = 'paste'
    else if (lower === 'z') editingCommand = 'undo'
    else if (lower === 'y') editingCommand = 'redo'
  } else if (isPrimaryMod && (modifiers & 8) && lower === 'z') {
    editingCommand = 'redo'
  }

  return { key: desc, modifiers, editingCommand }
}

/** Real Chromium views, with browser-local input. No OS input injection or window activation. */
export class ConversationBrowser {
  private readonly sessions = new Map<string, BrowserSession>()
  private readonly requests = new Map<string, { scope: string; controller: AbortController; agent: boolean }>()
  private readonly remoteStreams = new Map<string, Map<string, RemoteViewer>>()
  private remoteBackingWindow: BrowserWindow | null = null
  private layout: BrowserPanelLayout | null = null
  private modalVisible = false
  private disposed = false

  constructor(
    private readonly window: BrowserWindow,
    private readonly changed: (state: ConversationBrowserState) => void,
    private readonly approvalHandler?: BrowserApprovalHandler
  ) {}

  private async askApproval(owner: BrowserSession, req: BrowserApprovalRequest): Promise<BrowserApprovalDecision> {
    if (!this.approvalHandler || this.disposed) {
      return { id: req.id, approved: false }
    }
    try {
      return await this.approvalHandler(req)
    } catch {
      return { id: req.id, approved: false }
    } finally {
      if (this.disposed || !this.sessions.has(owner.scope)) {
        // session already cleaned up
      }
    }
  }

  startRemoteStream(
    scope: string,
    viewport: { width: number; height: number },
    onFrame: (frame: RemoteBrowserFrame) => void
  ): () => void {
    const viewerId = randomUUID()
    let viewers = this.remoteStreams.get(scope)
    if (!viewers) {
      viewers = new Map()
      this.remoteStreams.set(scope, viewers)
    }
    viewers.set(viewerId, {
      id: viewerId,
      width: Math.max(280, Math.min(1920, Math.round(viewport.width) || 520)),
      height: Math.max(220, Math.min(1440, Math.round(viewport.height) || 680)),
      onFrame
    })
    this.applyLayout()
    this.syncRemoteScreencasts()
    return () => {
      const current = this.remoteStreams.get(scope)
      if (!current) return
      current.delete(viewerId)
      if (current.size === 0) this.remoteStreams.delete(scope)
      this.applyLayout()
      this.syncRemoteScreencasts()
    }
  }

  async dispatchRemoteInput(scope: string, input: RemoteBrowserInput): Promise<void> {
    if (this.disposed) return
    const owner = this.sessions.get(scope)
    if (!owner) return
    const tab = owner.tabs.get(owner.activeTabId || '')
    if (!tab) return
    await tab.ready
    if (this.disposed || tab.view.webContents.isDestroyed()) return
    const debuggerApi = tab.view.webContents.debugger
    if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')

    const clampCoord = (value: number): number =>
      Number.isFinite(value) ? Math.max(0, Math.min(10000, Math.round(value))) : 0

    switch (input.type) {
      case 'mousePressed':
      case 'mouseReleased':
      case 'mouseMoved':
        await debuggerApi.sendCommand('Input.dispatchMouseEvent', {
          type: input.type,
          x: clampCoord(input.x),
          y: clampCoord(input.y),
          button: input.button ?? 'none',
          clickCount: input.type === 'mouseMoved' ? undefined : (input.clickCount ?? 1),
          buttons: input.button === 'left' ? 1 : input.button === 'right' ? 2 : input.button === 'middle' ? 4 : 0
        })
        break
      case 'mouseWheel':
        await debuggerApi.sendCommand('Input.dispatchMouseEvent', {
          type: 'mouseWheel',
          x: clampCoord(input.x),
          y: clampCoord(input.y),
          deltaX: Math.max(-3000, Math.min(3000, Math.round(input.deltaX || 0))),
          deltaY: Math.max(-3000, Math.min(3000, Math.round(input.deltaY || 0)))
        })
        break
      case 'keyDown':
      case 'keyUp':
        await debuggerApi.sendCommand('Input.dispatchKeyEvent', {
          type: input.type,
          key: input.key,
          code: input.code,
          windowsVirtualKeyCode: input.keyCode,
          modifiers: input.modifiers ?? 0,
          ...(input.type === 'keyDown' && input.text ? { text: input.text, unmodifiedText: input.text } : {})
        })
        break
      case 'insertText':
        if (input.text) {
          await debuggerApi.sendCommand('Input.insertText', { text: input.text })
        }
        break
    }
  }

  state(scope: string): ConversationBrowserState {
    const owner = this.owner(scope)
    return {
      scope,
      sessionId: owner.sessionId,
      paused: owner.paused,
      busy: owner.busy,
      activeTabId: owner.activeTabId,
      tabs: [...owner.tabs.values()].map(tab => {
        const page = tab.view.webContents
        const rawUrl = page.getURL()
        const url = tab.error
          ? (tab.targetUrl || (isBlankOrErrorUrl(rawUrl) ? 'about:blank' : rawUrl))
          : (isBlankOrErrorUrl(rawUrl) ? (tab.targetUrl || 'about:blank') : rawUrl)
        const rawTitle = page.getTitle()
        const title = tab.error
          ? fallbackTitleForUrl(url)
          : (!rawTitle || rawTitle === 'about:blank' ? '新标签页' : rawTitle)
        return {
          id: tab.id,
          title,
          url,
          loading: page.isLoading(),
          canGoBack: page.navigationHistory.canGoBack(),
          canGoForward: page.navigationHistory.canGoForward(),
          error: tab.error
        }
      })
    }
  }

  stateForSession(sessionId: string): ConversationBrowserState | null {
    const owner = [...this.sessions.values()].find(item => item.sessionId === sessionId)
    return owner ? this.state(owner.scope) : null
  }

  ensure(scope: string, sessionId: string): ConversationBrowserState {
    if (!/^[a-f0-9]{64}$/.test(scope) || !sessionId || sessionId.length > 256) throw new Error('Invalid browser owner')
    if (this.disposed) throw new Error('浏览器已关闭')
    if (!this.sessions.has(scope)) {
      if (this.sessions.size >= 12) {
        const empty = [...this.sessions.values()].find(owner => !owner.tabs.size && !owner.busy)
        if (empty) this.sessions.delete(empty.scope)
        else throw new Error('浏览器会话数量已达上限，请关闭其他会话的标签页')
      }
      this.sessions.set(scope, {
        scope,
        sessionId,
        tabs: new Map(),
        paused: false,
        busy: false,
        queue: Promise.resolve(),
        permissionDecisions: new Map(),
        preApprovedDownloads: new Set(),
        downloads: [],
        downloadWaiters: []
      })
    }
    if (this.owner(scope).sessionId !== sessionId) throw new Error('Browser owner mismatch')
    return this.state(scope)
  }

  async execute(scope: string, sessionId: string, command: BrowserCommand, requestId?: string): Promise<Record<string, unknown>> {
    this.ensure(scope, sessionId)
    const owner = this.owner(scope)
    const controller = new AbortController()
    const operationId = requestId || randomUUID()
    this.requests.set(operationId, { scope, controller, agent: !!requestId })
    const operation = owner.queue.catch(() => undefined).then(async () => {
      this.check(owner, controller.signal, !!requestId)
      owner.busy = true
      this.publish(owner)
      try {
        return await this.run(owner, command, controller.signal, !!requestId)
      } finally {
        owner.busy = false
        this.publish(owner)
      }
    })
    owner.queue = operation
    try {
      return await operation
    } finally {
      this.requests.delete(operationId)
    }
  }

  cancel(requestId: string): void {
    this.requests.get(requestId)?.controller.abort()
  }

  pause(scope: string, paused: boolean): ConversationBrowserState {
    const owner = this.owner(scope)
    owner.paused = paused
    if (paused) {
      for (const request of this.requests.values()) {
        if (request.scope === scope && request.agent) request.controller.abort()
      }
      const activeTab = owner.tabs.get(owner.activeTabId || '')
      if (activeTab && !activeTab.view.webContents.isDestroyed()) {
        void activeTab.view.webContents.executeJavaScriptInIsolatedWorld(999, [{ code: browserCursorHideScript() }]).catch(() => undefined)
      }
    }
    this.publish(owner)
    return this.state(scope)
  }

  cancelAgentRequests(): void {
    for (const request of this.requests.values()) {
      if (request.agent) request.controller.abort()
    }
  }

  setLayout(layout: BrowserPanelLayout | null): void {
    this.layout = layout
    this.applyLayout()
  }

  setModalVisible(visible: boolean): void {
    this.modalVisible = visible
    this.applyLayout()
  }

  recordCommandError(scope: string, command: BrowserCommand, error: unknown): void {
    const owner = this.sessions.get(scope)
    if (!owner) return
    const tab = owner.tabs.get(command.tab_id || owner.activeTabId || '')
    if (!tab) return
    if (typeof command.url === 'string' && command.url.trim()) {
      tab.targetUrl = command.url.trim()
    }
    tab.error = error instanceof Error ? error.message : String(error)
    this.applyLayout()
    this.publish(owner)
  }

  private ensureRemoteBackingWindow(width: number, height: number): BrowserWindow {
    if (this.remoteBackingWindow && !this.remoteBackingWindow.isDestroyed()) {
      const [curW, curH] = this.remoteBackingWindow.getContentSize()
      if (Math.abs((curW ?? 0) - width) > 4 || Math.abs((curH ?? 0) - height) > 4) {
        this.remoteBackingWindow.setContentSize(width, height)
      }
      return this.remoteBackingWindow
    }
    const backing = new BrowserWindow({
      show: false,
      width,
      height,
      frame: false,
      skipTaskbar: true,
      focusable: false,
      paintWhenInitiallyHidden: true,
      enableLargerThanScreen: true,
      webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false }
    })
    this.remoteBackingWindow = backing
    return backing
  }

  private remoteViewportFor(scope: string): { width: number; height: number } | null {
    const viewers = this.remoteStreams.get(scope)
    if (!viewers || viewers.size === 0) return null
    let width = 0
    let height = 0
    for (const viewer of viewers.values()) {
      width = Math.max(width, viewer.width)
      height = Math.max(height, viewer.height)
    }
    return { width: Math.max(280, width), height: Math.max(220, height) }
  }

  private attachViewToHost(tab: Tab, host: 'main' | 'remote', backingWindow?: BrowserWindow): void {
    if (tab.hostWindow === host) return
    try {
      if (!this.window.isDestroyed()) this.window.contentView.removeChildView(tab.view)
    } catch {
      // ignore
    }
    try {
      if (this.remoteBackingWindow && !this.remoteBackingWindow.isDestroyed()) {
        this.remoteBackingWindow.contentView.removeChildView(tab.view)
      }
    } catch {
      // ignore
    }

    if (host === 'main' && !this.window.isDestroyed()) {
      this.window.contentView.addChildView(tab.view)
      tab.hostWindow = 'main'
    } else if (host === 'remote' && backingWindow && !backingWindow.isDestroyed()) {
      backingWindow.contentView.addChildView(tab.view)
      tab.hostWindow = 'remote'
    }
  }

  private applyLayout(): void {
    if (this.disposed || this.window.isDestroyed()) return
    for (const owner of this.sessions.values()) {
      const remoteViewport = this.remoteViewportFor(owner.scope)
      for (const tab of owner.tabs.values()) {
        const showDesktop = Boolean(
          this.layout?.visible
          && this.layout.bounds.width > 0
          && this.layout.bounds.height > 0
          && !this.modalVisible
          && this.layout.scope === owner.scope
          && owner.activeTabId === tab.id
          && !tab.error
        )
        const showRemote = Boolean(
          !showDesktop
          && remoteViewport
          && owner.activeTabId === tab.id
          && !tab.error
        )
        if (showDesktop && this.layout) {
          this.attachViewToHost(tab, 'main')
          tab.view.setBounds(browserBounds(this.layout.bounds, this.window.webContents.getZoomFactor(), this.window.getContentBounds()))
          tab.view.setVisible(true)
        } else if (showRemote && remoteViewport) {
          const backing = this.ensureRemoteBackingWindow(remoteViewport.width, remoteViewport.height)
          this.attachViewToHost(tab, 'remote', backing)
          tab.view.setBounds({ x: 0, y: 0, width: remoteViewport.width, height: remoteViewport.height })
          tab.view.setVisible(true)
        } else {
          this.attachViewToHost(tab, 'main')
          tab.view.setVisible(false)
        }
      }
    }
  }

  private syncRemoteScreencasts(): void {
    if (this.disposed) return
    for (const owner of this.sessions.values()) {
      const viewport = this.remoteViewportFor(owner.scope)
      for (const tab of owner.tabs.values()) {
        const currentUrl = tab.view.webContents.isDestroyed() ? 'about:blank' : tab.view.webContents.getURL()
        const shouldStream = Boolean(
          viewport
          && owner.activeTabId === tab.id
          && !tab.error
          && (!isBlankOrErrorUrl(currentUrl) || (tab.targetUrl && tab.targetUrl !== 'about:blank'))
        )
        const desiredKey = shouldStream && viewport ? `${viewport.width}x${viewport.height}` : undefined
        if (tab.screencastKey === desiredKey) continue
        tab.screencastKey = desiredKey
        void tab.ready.then(async () => {
          if (this.disposed || tab.view.webContents.isDestroyed()) return
          const debuggerApi = tab.view.webContents.debugger
          if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')
          if (!desiredKey || !viewport) {
            await debuggerApi.sendCommand('Page.stopScreencast').catch(() => undefined)
            return
          }
          if (tab.screencastKey !== desiredKey) return
          await debuggerApi.sendCommand('Page.startScreencast', {
            format: 'jpeg',
            quality: 80,
            maxWidth: viewport.width,
            maxHeight: viewport.height,
            everyNthFrame: 1
          }).catch(() => undefined)
        }).catch(() => undefined)
      }
    }
  }

  private owner(scope: string): BrowserSession {
    const owner = this.sessions.get(scope)
    if (!owner) throw new Error('尚未打开此会话的浏览器')
    return owner
  }

  private check(owner: BrowserSession, signal: AbortSignal, agent: boolean): void {
    if (this.disposed || signal.aborted) throw new Error('浏览器操作已取消；请读取页面后再继续')
    if (agent && owner.paused) throw new Error('用户已暂停 Agent 浏览器操作，请等待用户恢复')
  }

  private publish(owner: BrowserSession): void {
    if (!this.disposed && !this.window.isDestroyed()) {
      this.changed(this.state(owner.scope))
      this.syncRemoteScreencasts()
    }
  }

  private configureSession(owner: BrowserSession, isolatedSession: Electron.Session): void {
    if (owner.sessionConfigured) return
    owner.sessionConfigured = true

    isolatedSession.setPermissionRequestHandler((webContents, permission, callback, details) => {
      const mediaTypes = (details as { mediaTypes?: string[] } | undefined)?.mediaTypes
      const normalized = normalizePermissionName(permission, mediaTypes)
      const url = details?.requestingUrl || webContents.getURL() || 'about:blank'
      const origin = originForUrl(url)
      const cacheKey = `${origin}::${normalized}`

      if (owner.permissionDecisions.has(cacheKey)) {
        callback(owner.permissionDecisions.get(cacheKey) === true)
        return
      }
      if (!this.approvalHandler || this.disposed) {
        callback(false)
        return
      }

      void this.askApproval(owner, {
        id: randomUUID(),
        kind: 'permission',
        scope: owner.scope,
        sessionId: owner.sessionId,
        origin,
        url,
        permission: normalized,
        permissionLabel: formatPermissionLabel(permission, mediaTypes),
        allowRememberSession: true
      }).then((decision) => {
        if (decision.rememberSession) {
          owner.permissionDecisions.set(cacheKey, decision.approved)
        }
        callback(decision.approved)
      }).catch(() => callback(false))
    })

    isolatedSession.setPermissionCheckHandler((webContents, permission, requestingOrigin, details) => {
      const mediaTypes = (details as { mediaType?: string } | undefined)?.mediaType
        ? [(details as { mediaType?: string }).mediaType!]
        : undefined
      const normalized = normalizePermissionName(permission, mediaTypes)
      const origin = requestingOrigin || originForUrl(webContents?.getURL?.() || '')
      return owner.permissionDecisions.get(`${origin}::${normalized}`) === true
    })

    isolatedSession.removeAllListeners?.('will-download')
    isolatedSession.on?.('will-download', (_event, item) => {
      const url = item.getURL()
      const filename = sanitizeDownloadFilename(item.getFilename() || filenameFromUrl(url))
      const totalBytes = item.getTotalBytes()
      const origin = originForUrl(url)
      const savePath = uniqueDownloadPath(filename)
      const preApproved = owner.preApprovedDownloads.delete(url)

      const finishSetup = (approved: boolean) => {
        if (!approved || this.disposed) {
          try {
            item.cancel()
          } catch {
            // ignore
          }
          const cancelledRecord: DownloadRecord = {
            id: randomUUID(),
            url,
            filename,
            savePath: '',
            totalBytes,
            receivedBytes: 0,
            state: 'cancelled',
            startedAt: Date.now(),
            finishedAt: Date.now()
          }
          pushCapped(owner.downloads, cancelledRecord, MAX_DOWNLOAD_ENTRIES)
          const waiters = owner.downloadWaiters.splice(0)
          for (const resolve of waiters) resolve(cancelledRecord)
          return
        }

        try {
          item.setSavePath(savePath)
        } catch {
          // ignore
        }
        const record: DownloadRecord = {
          id: randomUUID(),
          url,
          filename,
          savePath,
          totalBytes,
          receivedBytes: 0,
          state: 'in_progress',
          startedAt: Date.now()
        }
        pushCapped(owner.downloads, record, MAX_DOWNLOAD_ENTRIES)
        item.on('updated', (_ev, state) => {
          record.receivedBytes = item.getReceivedBytes()
          if (item.getTotalBytes() > 0) record.totalBytes = item.getTotalBytes()
          if (state === 'interrupted') record.state = 'interrupted'
        })
        item.once('done', (_ev, state) => {
          record.receivedBytes = item.getReceivedBytes()
          if (item.getTotalBytes() > 0) record.totalBytes = item.getTotalBytes()
          record.state = state === 'completed' ? 'completed' : state === 'cancelled' ? 'cancelled' : 'interrupted'
          record.finishedAt = Date.now()
          const waiters = owner.downloadWaiters.splice(0)
          for (const resolve of waiters) resolve(record)
        })
        try {
          if (item.isPaused()) item.resume()
        } catch {
          // ignore
        }
      }

      if (preApproved) {
        finishSetup(true)
        return
      }

      // Pause synchronously while Desktop confirmation modal is presented.
      try {
        item.pause()
      } catch {
        // ignore
      }
      void this.askApproval(owner, {
        id: randomUUID(),
        kind: 'download',
        scope: owner.scope,
        sessionId: owner.sessionId,
        origin,
        url,
        filename,
        totalBytes: totalBytes > 0 ? totalBytes : undefined,
        savePath,
        allowRememberSession: false
      }).then((decision) => finishSetup(decision.approved))
    })
  }

  private newTab(owner: BrowserSession): Tab {
    if (owner.tabs.size >= 6) throw new Error('每个会话最多打开 6 个标签页')
    const partition = `persist:seekclaw-browser-${owner.scope}`
    const isolatedSession = session.fromPartition(partition)
    this.configureSession(owner, isolatedSession)

    const view = new WebContentsView({
      webPreferences: {
        partition,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        webSecurity: true,
        allowRunningInsecureContent: false,
        backgroundThrottling: false,
        navigateOnDragDrop: false,
        disableDialogs: true
      }
    })
    const tab: Tab = {
      id: randomUUID(),
      view,
      ready: Promise.resolve(),
      hostWindow: 'main',
      consoleLogs: [],
      networkLogs: [],
      pendingNetwork: new Map()
    }
    owner.tabs.set(tab.id, tab)
    owner.activeTabId = tab.id
    this.window.contentView.addChildView(view)
    view.setBounds({ x: 0, y: 48, width: 1000, height: 700 })
    view.setVisible(false)

    const page = view.webContents
    const guard = (event: Electron.Event, url: string) => {
      try {
        browserUrl(url)
      } catch {
        event.preventDefault()
      }
    }
    page.on('will-navigate', guard)
    page.on('will-redirect', guard)
    page.on('will-frame-navigate', (event) => guard(event, event.url))
    page.on('will-attach-webview', event => event.preventDefault())
    page.setWindowOpenHandler(({ url }) => {
      try {
        browserUrl(url)
        if (!owner.paused) void this.execute(owner.scope, owner.sessionId, { action: 'open', url }).catch(() => undefined)
      } catch {
        /* External protocols and local files stay blocked. */
      }
      return { action: 'deny' }
    })
    page.on('did-start-loading', () => this.publish(owner))
    page.on('did-stop-loading', () => {
      if (tab.cursor && !owner.paused && !this.disposed && !page.isDestroyed()) {
        void page.executeJavaScriptInIsolatedWorld(999, [{
          code: browserCursorMoveScript({
            x: tab.cursor.x,
            y: tab.cursor.y,
            fromX: tab.cursor.x,
            fromY: tab.cursor.y,
            durationMs: 0
          })
        }]).catch(() => undefined)
      }
      this.publish(owner)
    })
    page.on('page-title-updated', () => this.publish(owner))
    page.on('did-navigate', () => {
      const currentUrl = page.getURL()
      if (!isBlankOrErrorUrl(currentUrl)) {
        tab.targetUrl = currentUrl
        tab.error = undefined
      }
      this.applyLayout()
      this.publish(owner)
    })
    page.on('did-navigate-in-page', () => this.publish(owner))
    page.on('did-fail-load', (_event, code, description, validatedURL, isMainFrame) => {
      if (isMainFrame && code !== -3) {
        if (!isBlankOrErrorUrl(validatedURL)) {
          tab.targetUrl = validatedURL
        }
        tab.error = `${description} (${code})`
        this.applyLayout()
        this.publish(owner)
      }
    })
    page.on('render-process-gone', () => {
      tab.error = '页面进程已退出，请刷新重试'
      this.publish(owner)
    })
    page.on('will-prevent-unload', event => event.preventDefault())

    page.on('console-message', (eventOrArgs: unknown, level?: number, message?: string, line?: number, sourceId?: string) => {
      let levelText = 'info'
      let text = ''
      let src: string | undefined
      let lineNo: number | undefined
      if (eventOrArgs && typeof eventOrArgs === 'object' && 'message' in (eventOrArgs as Record<string, unknown>)) {
        const ev = eventOrArgs as { level?: string; message?: string; sourceId?: string; lineNumber?: number }
        levelText = String(ev.level ?? 'info').toLowerCase()
        text = String(ev.message ?? '')
        src = ev.sourceId
        lineNo = ev.lineNumber
      } else {
        const numeric = typeof level === 'number' ? level : 1
        levelText = numeric === 0 ? 'verbose' : numeric === 2 ? 'warning' : numeric >= 3 ? 'error' : 'info'
        text = String(message ?? '')
        src = sourceId
        lineNo = line
      }
      if (!text) return
      pushCapped(tab.consoleLogs, {
        level: levelText,
        message: text.slice(0, 2000),
        source: src,
        line: lineNo,
        timestamp: Date.now()
      }, MAX_LOG_ENTRIES)
    })

    if (typeof page.debugger?.on === 'function') {
      page.debugger.on('message', (_event, method, params) => {
        if (this.disposed || page.isDestroyed()) return
        if (method === 'Page.screencastFrame') {
          const frameParams = params as { sessionId?: number; data?: string; metadata?: { deviceWidth?: number; deviceHeight?: number } }
          if (typeof frameParams.sessionId === 'number') {
            void page.debugger.sendCommand('Page.screencastFrameAck', { sessionId: frameParams.sessionId }).catch(() => undefined)
          }
          const viewers = this.remoteStreams.get(owner.scope)
          if (!viewers || viewers.size === 0 || owner.activeTabId !== tab.id || !frameParams.data) return
          const payload: RemoteBrowserFrame = {
            scope: owner.scope,
            tabId: tab.id,
            data: frameParams.data,
            width: Math.max(1, Math.round(frameParams.metadata?.deviceWidth ?? 520)),
            height: Math.max(1, Math.round(frameParams.metadata?.deviceHeight ?? 680))
          }
          for (const viewer of viewers.values()) {
            viewer.onFrame(payload)
          }
          return
        }

        if (method === 'Network.requestWillBeSent') {
          const reqParams = params as {
            requestId?: string
            request?: { url?: string; method?: string }
            type?: string
            timestamp?: number
          }
          const reqId = reqParams.requestId
          if (!reqId) return
          tab.pendingNetwork.set(reqId, {
            requestId: reqId,
            url: String(reqParams.request?.url ?? '').slice(0, 1000),
            method: String(reqParams.request?.method ?? 'GET'),
            type: String(reqParams.type ?? 'Other'),
            timestamp: Date.now()
          })
          if (tab.pendingNetwork.size > 400) {
            const firstKey = tab.pendingNetwork.keys().next().value
            if (firstKey) tab.pendingNetwork.delete(firstKey)
          }
          return
        }

        if (method === 'Network.responseReceived') {
          const resParams = params as {
            requestId?: string
            response?: { status?: number; statusText?: string; url?: string }
            type?: string
          }
          const reqId = resParams.requestId
          if (!reqId) return
          const status = resParams.response?.status
          const entry = tab.pendingNetwork.get(reqId) ?? {
            requestId: reqId,
            url: String(resParams.response?.url ?? '').slice(0, 1000),
            method: 'GET',
            type: String(resParams.type ?? 'Other'),
            timestamp: Date.now()
          }
          entry.status = status
          entry.statusText = resParams.response?.statusText
          entry.ok = typeof status === 'number' ? status >= 200 && status < 400 : undefined
          tab.pendingNetwork.set(reqId, entry)
          return
        }

        if (method === 'Network.loadingFinished') {
          const finParams = params as { requestId?: string }
          const entry = finParams.requestId ? tab.pendingNetwork.get(finParams.requestId) : undefined
          if (!entry) return
          tab.pendingNetwork.delete(finParams.requestId!)
          entry.durationMs = Math.max(0, Date.now() - entry.timestamp)
          pushCapped(tab.networkLogs, entry, MAX_LOG_ENTRIES)
          return
        }

        if (method === 'Network.loadingFailed') {
          const failParams = params as { requestId?: string; errorText?: string }
          const entry = failParams.requestId ? tab.pendingNetwork.get(failParams.requestId) : undefined
          if (!entry) return
          tab.pendingNetwork.delete(failParams.requestId!)
          entry.ok = false
          entry.error = failParams.errorText || 'Failed'
          entry.durationMs = Math.max(0, Date.now() - entry.timestamp)
          pushCapped(tab.networkLogs, entry, MAX_LOG_ENTRIES)
        }
      })
    }

    tab.ready = page.loadURL('about:blank').then(async () => {
      page.debugger.attach('1.3')
      await page.debugger.sendCommand('Page.setInterceptFileChooserDialog', { enabled: true }).catch(() => undefined)
      await page.debugger.sendCommand('Network.enable').catch(() => undefined)
    })
    void tab.ready.catch(() => undefined)
    return tab
  }

  private async moveVirtualCursor(
    tab: Tab,
    agent: boolean,
    x: number,
    y: number,
    mode: VirtualCursorMode = 'arrow',
    effect: VirtualCursorEffect = 'none',
    durationMs?: number
  ): Promise<void> {
    if (!agent || this.disposed || tab.view.webContents.isDestroyed()) return
    const clampedX = Math.max(0, Math.min(10000, Math.round(x)))
    const clampedY = Math.max(0, Math.min(10000, Math.round(y)))
    const fromX = tab.cursor?.x
    const fromY = tab.cursor?.y
    tab.cursor = { x: clampedX, y: clampedY }
    await tab.view.webContents.executeJavaScriptInIsolatedWorld(999, [{
      code: browserCursorMoveScript({
        x: clampedX,
        y: clampedY,
        fromX,
        fromY,
        mode,
        effect,
        durationMs
      })
    }]).catch(() => undefined)
  }

  private async locateElement(
    page: Electron.WebContents,
    elementId: string,
    snapshotId?: string
  ): Promise<{ x: number; y: number; tag: string; isTextInput: boolean; isSelect: boolean; draggable: boolean }> {
    const res = await page.executeJavaScriptInIsolatedWorld(999, [{
      code: browserPageScript({ action: 'locate', element_id: elementId, snapshot_id: snapshotId }, randomUUID())
    }]) as { browser_error?: string; x?: number; y?: number; tag?: string; isTextInput?: boolean; isSelect?: boolean; draggable?: boolean }
    if (res?.browser_error) throw new Error(String(res.browser_error))
    return {
      x: typeof res?.x === 'number' ? res.x : 100,
      y: typeof res?.y === 'number' ? res.y : 100,
      tag: String(res?.tag ?? ''),
      isTextInput: Boolean(res?.isTextInput),
      isSelect: Boolean(res?.isSelect),
      draggable: Boolean(res?.draggable)
    }
  }

  private async run(owner: BrowserSession, command: BrowserCommand, signal: AbortSignal, agent: boolean): Promise<Record<string, unknown>> {
    const actions = new Set([
      'open', 'tabs', 'select', 'navigate', 'snapshot', 'screenshot',
      'click', 'right_click', 'double_click', 'hover', 'drag',
      'fill', 'select_option', 'press', 'scroll',
      'evaluate', 'console', 'network',
      'request_permission', 'download', 'downloads',
      'back', 'forward', 'reload', 'close'
    ])
    if (!actions.has(command.action)) throw new Error('Unsupported browser action')
    if (command.action === 'tabs') return { ...this.state(owner.scope) }

    let tab: Tab | undefined
    if (command.action === 'open') {
      tab = this.newTab(owner)
      await tab.ready
      if (command.url) tab.targetUrl = String(command.url)
      const url = browserUrl(command.url || 'about:blank')
      await this.navigate(owner, tab, url, signal)
    } else {
      tab = owner.tabs.get(command.tab_id || owner.activeTabId || '')
      if (!tab) throw new Error('标签页不存在，请先 open 或 tabs')
    }
    await tab.ready
    this.check(owner, signal, agent)
    const page = tab.view.webContents
    owner.activeTabId = tab.id

    if (command.action === 'close') {
      this.closeTab(owner, tab)
      return { ...this.state(owner.scope) }
    }
    if (command.action === 'console') {
      const levelFilter = command.level?.trim().toLowerCase()
      const textFilter = command.filter?.trim().toLowerCase()
      const limit = Math.max(1, Math.min(200, Math.round(command.limit ?? 50)))
      let logs = tab.consoleLogs.slice()
      if (levelFilter) logs = logs.filter((entry) => entry.level === levelFilter)
      if (textFilter) logs = logs.filter((entry) => entry.message.toLowerCase().includes(textFilter))
      logs = logs.slice(-limit)
      if (command.clear) tab.consoleLogs.length = 0
      return { tab_id: tab.id, url: page.getURL(), count: logs.length, logs }
    }
    if (command.action === 'network') {
      const textFilter = command.filter?.trim().toLowerCase()
      const statusFilter = command.status_filter ?? 'all'
      const limit = Math.max(1, Math.min(200, Math.round(command.limit ?? 50)))
      let requests = [...tab.networkLogs, ...tab.pendingNetwork.values()]
      if (statusFilter === 'error') {
        requests = requests.filter((entry) => entry.ok === false || (typeof entry.status === 'number' && entry.status >= 400))
      }
      if (textFilter) {
        requests = requests.filter((entry) => entry.url.toLowerCase().includes(textFilter) || entry.method.toLowerCase().includes(textFilter))
      }
      requests = requests.slice(-limit)
      if (command.clear) {
        tab.networkLogs.length = 0
        tab.pendingNetwork.clear()
      }
      return { tab_id: tab.id, url: page.getURL(), count: requests.length, requests }
    }
    if (command.action === 'downloads') {
      return { tab_id: tab.id, downloads: owner.downloads.slice(-50) }
    }
    if (command.action === 'request_permission') {
      const rawPerm = command.permission?.trim() || ''
      if (!rawPerm) throw new Error('请指定要申请的权限类型（例如 camera, microphone, geolocation, notifications）')
      const normalized = normalizePermissionName(rawPerm)
      const currentUrl = page.getURL() || tab.targetUrl || 'about:blank'
      const origin = originForUrl(currentUrl)
      const decision = await this.askApproval(owner, {
        id: randomUUID(),
        kind: 'permission',
        scope: owner.scope,
        sessionId: owner.sessionId,
        origin,
        url: currentUrl,
        permission: normalized,
        permissionLabel: formatPermissionLabel(normalized),
        reason: command.reason?.trim() || undefined,
        allowRememberSession: true
      })
      if (decision.approved && decision.rememberSession) {
        owner.permissionDecisions.set(`${origin}::${normalized}`, true)
      } else if (decision.approved) {
        owner.permissionDecisions.set(`${origin}::${normalized}`, true)
      }
      return {
        tab_id: tab.id,
        url: currentUrl,
        origin,
        permission: normalized,
        granted: decision.approved,
        rememberSession: Boolean(decision.rememberSession)
      }
    }
    if (command.action === 'download') {
      const url = browserUrl(command.url)
      const origin = originForUrl(url)
      const filename = sanitizeDownloadFilename(command.filename || filenameFromUrl(url))
      const savePath = uniqueDownloadPath(filename)
      const decision = await this.askApproval(owner, {
        id: randomUUID(),
        kind: 'download',
        scope: owner.scope,
        sessionId: owner.sessionId,
        origin,
        url,
        filename,
        savePath,
        reason: command.reason?.trim() || undefined,
        allowRememberSession: false
      })
      if (!decision.approved) {
        return { tab_id: tab.id, url, filename, approved: false, state: 'cancelled' }
      }
      owner.preApprovedDownloads.add(url)
      const waitForDownload = new Promise<DownloadRecord>((resolve) => {
        const timer = setTimeout(() => {
          resolve({
            id: randomUUID(),
            url,
            filename,
            savePath,
            totalBytes: 0,
            receivedBytes: 0,
            state: 'in_progress',
            startedAt: Date.now()
          })
        }, 12000)
        owner.downloadWaiters.push((record) => {
          clearTimeout(timer)
          resolve(record)
        })
      })
      page.downloadURL(url)
      const record = await waitForDownload
      return { tab_id: tab.id, approved: true, ...record }
    }

    if (command.action === 'navigate') {
      tab.targetUrl = String(command.url ?? '')
      await this.navigate(owner, tab, browserUrl(command.url), signal)
    }
    if (command.action === 'back' && page.navigationHistory.canGoBack()) {
      tab.error = undefined
      page.navigationHistory.goBack()
    }
    if (command.action === 'forward' && page.navigationHistory.canGoForward()) {
      tab.error = undefined
      page.navigationHistory.goForward()
    }
    if (command.action === 'reload') {
      tab.error = undefined
      const currentUrl = page.getURL()
      if (isBlankOrErrorUrl(currentUrl) && tab.targetUrl && tab.targetUrl !== 'about:blank') {
        await this.navigate(owner, tab, browserUrl(tab.targetUrl), signal)
      } else {
        page.reload()
      }
    }
    this.applyLayout()
    this.publish(owner)
    this.check(owner, signal, agent)

    if (command.action === 'screenshot') {
      const bounds = tab.view.getBounds()
      const width = Math.min(1600, bounds.width || 1000)
      const height = Math.min(1200, bounds.height || 700)
      let png: Buffer
      try {
        png = await this.capture(tab, width, height, signal)
      } catch {
        this.check(owner, signal, agent)
        throw new Error('页面尚未生成可截图的画面，请展开此会话的浏览器面板后重试截图。页面操作未重试。')
      }
      this.check(owner, signal, agent)
      if (png.length > 8 * 1024 * 1024) throw new Error('截图过大，请缩小浏览器面板')
      return { tab_id: tab.id, url: page.getURL(), image: { data: png.toString('base64') } }
    }

    if (command.action === 'evaluate') {
      const script = typeof command.script === 'string' ? command.script : ''
      if (!script.trim()) throw new Error('请提供要执行的 JavaScript 脚本')
      if (script.length > 64 * 1024) throw new Error('脚本长度超出限制（最大 64KB）')
      const evaluatorCode = `
        (async () => {
          const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
          const rawCode = ${JSON.stringify(script)};
          let fn;
          try {
            fn = new AsyncFunction('return (\\n' + rawCode + '\\n)');
          } catch {
            fn = new AsyncFunction(rawCode);
          }
          const result = await fn();
          if (result === undefined) return { value: null, type: 'undefined' };
          if (result === null || typeof result === 'string' || typeof result === 'number' || typeof result === 'boolean') {
            return { value: result, type: typeof result };
          }
          if (typeof result === 'bigint') return { value: result.toString(), type: 'bigint' };
          try {
            const seen = new WeakSet();
            const json = JSON.stringify(result, (_k, v) => {
              if (typeof v === 'bigint') return v.toString();
              if (typeof v === 'function') return '[Function]';
              if (v && typeof v === 'object') {
                if (seen.has(v)) return '[Circular]';
                seen.add(v);
              }
              return v;
            });
            if (json === undefined) return { value: String(result), type: typeof result };
            if (json.length > ${MAX_EVALUATE_BYTES}) {
              return { value: json.slice(0, ${MAX_EVALUATE_BYTES}), type: 'truncated_json' };
            }
            return { value: JSON.parse(json), type: 'json' };
          } catch {
            return { value: String(result).slice(0, ${MAX_EVALUATE_BYTES}), type: typeof result };
          }
        })()
      `
      const evalResult = await page.executeJavaScript(evaluatorCode, true) as { value?: unknown; type?: string }
      this.check(owner, signal, agent)
      await this.settle(page, signal)
      const snap = await page.executeJavaScriptInIsolatedWorld(999, [{ code: browserPageScript({ action: 'snapshot' }, randomUUID()) }]) as Record<string, unknown>
      return {
        tab_id: tab.id,
        value: evalResult?.value ?? null,
        value_type: evalResult?.type ?? 'unknown',
        ...snap
      }
    }

    if (command.action === 'press') {
      await this.input(page, command)
    } else if (command.action === 'scroll') {
      const sx = command.x ?? tab.cursor?.x ?? 200
      const sy = command.y ?? tab.cursor?.y ?? 200
      const deltaY = Math.max(-3000, Math.min(3000, command.delta_y ?? 600))
      await this.moveVirtualCursor(tab, agent, sx, sy, 'scroll', deltaY < 0 ? 'scroll_up' : 'scroll_down')
      await this.input(page, { ...command, x: sx, y: sy })
    } else if (command.action === 'hover') {
      const coords = command.element_id
        ? await this.locateElement(page, command.element_id, command.snapshot_id)
        : { x: command.x ?? 100, y: command.y ?? 100, isTextInput: false }
      await this.moveVirtualCursor(tab, agent, coords.x, coords.y, coords.isTextInput ? 'text' : 'arrow', 'hover')
      await this.dispatchMouseMove(page, coords.x, coords.y, 'none', 0)
    } else if (command.action === 'right_click' || (command.action === 'click' && command.button === 'right')) {
      const coords = command.element_id
        ? await this.locateElement(page, command.element_id, command.snapshot_id)
        : { x: command.x ?? 100, y: command.y ?? 100 }
      await this.moveVirtualCursor(tab, agent, coords.x, coords.y, 'arrow', 'right_click')
      await this.dispatchMouseClick(page, coords.x, coords.y, 'right', 1)
    } else if (command.action === 'double_click' || (command.action === 'click' && (command.click_count ?? 1) >= 2)) {
      const coords = command.element_id
        ? await this.locateElement(page, command.element_id, command.snapshot_id)
        : { x: command.x ?? 100, y: command.y ?? 100, isTextInput: false }
      await this.moveVirtualCursor(tab, agent, coords.x, coords.y, coords.isTextInput ? 'text' : 'arrow', 'double_click')
      await this.dispatchMouseClick(page, coords.x, coords.y, 'left', 1)
      await this.dispatchMouseClick(page, coords.x, coords.y, 'left', 2)
    } else if (command.action === 'drag') {
      const start = command.element_id
        ? await this.locateElement(page, command.element_id, command.snapshot_id)
        : { x: command.x ?? 100, y: command.y ?? 100, draggable: false }
      const end = command.target_element_id
        ? await this.locateElement(page, command.target_element_id, command.snapshot_id)
        : { x: command.to_x ?? start.x + 100, y: command.to_y ?? start.y }
      if (![start.x, start.y, end.x, end.y].every((v) => Number.isFinite(v) && v >= 0 && v <= 10000)) {
        throw new Error('Invalid drag coordinates')
      }
      const steps = Math.max(4, Math.min(20, Math.round(command.steps ?? 10)))
      await this.moveVirtualCursor(tab, agent, start.x, start.y, 'drag', 'drag_start')
      await this.dispatchMouseDown(page, start.x, start.y, 'left', 1)
      for (let i = 1; i <= steps; i++) {
        const px = Math.round(start.x + ((end.x - start.x) * i) / steps)
        const py = Math.round(start.y + ((end.y - start.y) * i) / steps)
        await this.dispatchMouseMove(page, px, py, 'left', 1)
        await new Promise((r) => setTimeout(r, 14))
      }
      await this.dispatchMouseUp(page, end.x, end.y, 'left', 1)
      await this.moveVirtualCursor(tab, agent, end.x, end.y, 'arrow', 'drag_end', 60)
      if (command.element_id && command.target_element_id && start.draggable) {
        await page.executeJavaScriptInIsolatedWorld(999, [{
          code: browserPageScript({
            action: 'drag_html5',
            element_id: command.element_id,
            target_element_id: command.target_element_id,
            snapshot_id: command.snapshot_id
          }, randomUUID())
        }]).catch(() => undefined)
      }
    } else if (command.action === 'click' && !command.element_id) {
      const cx = command.x ?? 100
      const cy = command.y ?? 100
      await this.moveVirtualCursor(tab, agent, cx, cy, 'arrow', 'click')
      await this.input(page, command)
    }

    const domAction = ['click', 'fill', 'select_option'].includes(command.action) && command.element_id ? command : { action: 'snapshot' }
    let result: Record<string, unknown>
    try {
      if (domAction.action !== 'snapshot') {
        if (agent && domAction.element_id) {
          const info = await this.locateElement(page, domAction.element_id, domAction.snapshot_id).catch(() => null)
          if (info) {
            const mode: VirtualCursorMode = domAction.action === 'fill' && info.isTextInput ? 'text' : 'arrow'
            await this.moveVirtualCursor(tab, agent, info.x, info.y, mode, 'click')
          }
        }
        const actionResult = await page.executeJavaScriptInIsolatedWorld(999, [{ code: browserPageScript({ ...domAction, actionOnly: true }, randomUUID()) }]) as Record<string, unknown>
        if (actionResult.browser_error) throw new Error(String(actionResult.browser_error))
      }
      await this.settle(page, signal)
      this.check(owner, signal, agent)
      result = await page.executeJavaScriptInIsolatedWorld(999, [{ code: browserPageScript({ action: 'snapshot' }, randomUUID()) }]) as Record<string, unknown>
      if (result.browser_error) throw new Error(String(result.browser_error))
    } catch (error) {
      // Navigation can destroy the isolated execution context after a click. Never replay it.
      throw new Error(`页面操作未完成或页面已变化，请重新读取页面后继续；操作未重试。${error instanceof Error ? error.message : ''}`)
    }
    this.check(owner, signal, agent)
    return { tab_id: tab.id, ...result }
  }

  private async capture(tab: Tab, width: number, height: number, signal: AbortSignal): Promise<Buffer> {
    for (let attempt = 0; ; attempt++) {
      if (signal.aborted) throw new Error('浏览器操作已取消')
      try {
        const image = await tab.view.webContents.capturePage({ x: 0, y: 0, width, height }, { stayHidden: true, stayAwake: true })
        if (image.isEmpty()) throw new Error('页面尚未生成画面')
        return image.toPNG()
      } catch (error) {
        if (attempt >= 2 || !tab.view.getVisible() || signal.aborted) throw error
        await new Promise<void>(resolve => setTimeout(resolve, 50 * (attempt + 1)))
      }
    }
  }

  private async navigate(owner: BrowserSession, tab: Tab, url: string, signal: AbortSignal): Promise<void> {
    tab.error = undefined
    tab.targetUrl = url
    this.applyLayout()
    const stop = () => tab.view.webContents.stop()
    signal.addEventListener('abort', stop, { once: true })
    const timer = setTimeout(stop, 30000)
    try {
      await tab.view.webContents.loadURL(url)
    } catch (error) {
      tab.error = signal.aborted ? '导航已取消' : error instanceof Error ? error.message : String(error)
      this.applyLayout()
      this.publish(owner)
      throw new Error(tab.error)
    } finally {
      clearTimeout(timer)
      signal.removeEventListener('abort', stop)
    }
  }

  private async settle(page: Electron.WebContents, signal: AbortSignal): Promise<void> {
    if (signal.aborted) throw new Error('浏览器操作已取消')
    if (!page.isLoadingMainFrame()) return
    await new Promise<void>((resolve, reject) => {
      const finish = () => { cleanup(); resolve() }
      const abort = () => { cleanup(); reject(new Error('浏览器操作已取消')) }
      const cleanup = () => {
        clearTimeout(timer)
        page.removeListener('did-stop-loading', finish)
        signal.removeEventListener('abort', abort)
      }
      const timer = setTimeout(finish, 15000)
      page.once('did-stop-loading', finish)
      signal.addEventListener('abort', abort, { once: true })
      if (signal.aborted) abort()
      else if (!page.isLoadingMainFrame()) finish()
    })
  }

  private async dispatchMouseMove(
    page: Electron.WebContents,
    x: number,
    y: number,
    button: 'none' | 'left' | 'right' | 'middle',
    buttons: number
  ): Promise<void> {
    const debuggerApi = page.debugger
    if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')
    await debuggerApi.sendCommand('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: Math.max(0, Math.min(10000, Math.round(x))),
      y: Math.max(0, Math.min(10000, Math.round(y))),
      button,
      buttons
    })
  }

  private async dispatchMouseDown(
    page: Electron.WebContents,
    x: number,
    y: number,
    button: 'left' | 'right' | 'middle',
    clickCount: number
  ): Promise<void> {
    const debuggerApi = page.debugger
    if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')
    const buttons = button === 'left' ? 1 : button === 'right' ? 2 : 4
    await debuggerApi.sendCommand('Input.dispatchMouseEvent', {
      type: 'mousePressed',
      x: Math.max(0, Math.min(10000, Math.round(x))),
      y: Math.max(0, Math.min(10000, Math.round(y))),
      button,
      buttons,
      clickCount
    })
  }

  private async dispatchMouseUp(
    page: Electron.WebContents,
    x: number,
    y: number,
    button: 'left' | 'right' | 'middle',
    clickCount: number
  ): Promise<void> {
    const debuggerApi = page.debugger
    if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')
    await debuggerApi.sendCommand('Input.dispatchMouseEvent', {
      type: 'mouseReleased',
      x: Math.max(0, Math.min(10000, Math.round(x))),
      y: Math.max(0, Math.min(10000, Math.round(y))),
      button,
      buttons: 0,
      clickCount
    })
  }

  private async dispatchMouseClick(
    page: Electron.WebContents,
    x: number,
    y: number,
    button: 'left' | 'right' | 'middle',
    clickCount: number
  ): Promise<void> {
    await this.dispatchMouseDown(page, x, y, button, clickCount)
    await this.dispatchMouseUp(page, x, y, button, clickCount)
  }

  private async input(page: Electron.WebContents, command: BrowserCommand): Promise<void> {
    const debuggerApi = page.debugger
    if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')
    if (command.action === 'press') {
      const { key, modifiers, editingCommand } = resolveKeySpec(command.key || '', command.modifiers)
      await debuggerApi.sendCommand('Input.dispatchKeyEvent', {
        type: 'keyDown',
        key: key.key,
        code: key.code,
        windowsVirtualKeyCode: key.keyCode,
        ...(modifiers ? { modifiers } : {}),
        ...(key.text ? { text: key.text, unmodifiedText: key.text } : {}),
        ...(editingCommand ? { commands: [editingCommand] } : {})
      })
      await debuggerApi.sendCommand('Input.dispatchKeyEvent', {
        type: 'keyUp',
        key: key.key,
        code: key.code,
        windowsVirtualKeyCode: key.keyCode,
        ...(modifiers ? { modifiers } : {})
      })
    } else {
      const x = command.x ?? 100
      const y = command.y ?? 100
      if (![x, y].every(value => Number.isFinite(value) && value >= 0 && value <= 10000)) {
        throw new Error('Invalid browser coordinates')
      }
      if (command.action === 'scroll') {
        const deltaY = Math.max(-3000, Math.min(3000, command.delta_y ?? 600))
        await debuggerApi.sendCommand('Input.dispatchMouseEvent', { type: 'mouseWheel', x, y, deltaX: 0, deltaY })
      } else {
        await debuggerApi.sendCommand('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 })
        await debuggerApi.sendCommand('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 })
      }
    }
  }

  private closeTab(owner: BrowserSession, tab: Tab): void {
    owner.tabs.delete(tab.id)
    if (!this.window.isDestroyed()) {
      try {
        this.window.contentView.removeChildView(tab.view)
      } catch {
        // Ignore if view was attached to the remote backing window.
      }
    }
    if (this.remoteBackingWindow && !this.remoteBackingWindow.isDestroyed()) {
      try {
        this.remoteBackingWindow.contentView.removeChildView(tab.view)
      } catch {
        // Ignore if view was not attached here.
      }
    }
    tab.screencastKey = undefined
    const page = tab.view.webContents
    if (page && !page.isDestroyed()) page.close({ waitForBeforeUnload: false })
    if (owner.activeTabId === tab.id) owner.activeTabId = [...owner.tabs.keys()].at(-1)
    this.applyLayout()
    this.publish(owner)
  }

  dispose(): void {
    this.disposed = true
    for (const request of this.requests.values()) request.controller.abort()
    for (const owner of this.sessions.values()) {
      for (const tab of [...owner.tabs.values()]) this.closeTab(owner, tab)
    }
    this.sessions.clear()
    this.remoteStreams.clear()
    if (this.remoteBackingWindow && !this.remoteBackingWindow.isDestroyed()) {
      this.remoteBackingWindow.close()
    }
    this.remoteBackingWindow = null
  }
}
