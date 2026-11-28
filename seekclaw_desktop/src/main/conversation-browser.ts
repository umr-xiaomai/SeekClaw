import { BrowserWindow, WebContentsView, session } from 'electron'
import { randomUUID } from 'node:crypto'
import type { BrowserCommand, BrowserPanelLayout, ConversationBrowserState } from '../shared/browser.js'
import type { RemoteBrowserFrame, RemoteBrowserInput } from '../shared/web-server.js'
import { browserBounds, browserUrl } from './browser-policy.js'
import { browserPageScript } from './browser-page.js'

interface Tab {
  id: string
  view: WebContentsView
  error?: string
  targetUrl?: string
  ready: Promise<void>
  hostWindow?: 'main' | 'remote'
  screencastKey?: string
}
interface BrowserSession { scope: string; sessionId: string; tabs: Map<string, Tab>; activeTabId?: string; paused: boolean; busy: boolean; queue: Promise<unknown> }
interface RemoteViewer { id: string; width: number; height: number; onFrame: (frame: RemoteBrowserFrame) => void }

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

/** Real Chromium views, with browser-local input. No OS input injection or window activation. */
export class ConversationBrowser {
  private readonly sessions = new Map<string, BrowserSession>()
  private readonly requests = new Map<string, { scope: string; controller: AbortController; agent: boolean }>()
  private readonly remoteStreams = new Map<string, Map<string, RemoteViewer>>()
  private remoteBackingWindow: BrowserWindow | null = null
  private layout: BrowserPanelLayout | null = null
  private disposed = false
  private modalVisible = false

  constructor(private readonly window: BrowserWindow, private readonly changed: (state: ConversationBrowserState) => void) {
    window.on('resize', () => this.applyLayout())
    window.webContents.on('zoom-changed', () => this.applyLayout())
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
    return { scope, sessionId: owner.sessionId, paused: owner.paused, busy: owner.busy, activeTabId: owner.activeTabId,
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
        return { id: tab.id, title, url, loading: page.isLoading(),
          canGoBack: page.navigationHistory.canGoBack(), canGoForward: page.navigationHistory.canGoForward(), error: tab.error }
      }) }
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
      this.sessions.set(scope, { scope, sessionId, tabs: new Map(), paused: false, busy: false, queue: Promise.resolve() })
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
      owner.busy = true; this.publish(owner)
      try { return await this.run(owner, command, controller.signal, !!requestId) }
      finally { owner.busy = false; this.publish(owner) }
    })
    owner.queue = operation
    try { return await operation }
    finally { this.requests.delete(operationId) }
  }

  cancel(requestId: string): void { this.requests.get(requestId)?.controller.abort() }
  pause(scope: string, paused: boolean): ConversationBrowserState {
    const owner = this.owner(scope)
    owner.paused = paused
    if (paused) for (const request of this.requests.values()) if (request.scope === scope && request.agent) request.controller.abort()
    this.publish(owner)
    return this.state(scope)
  }

  cancelAgentRequests(): void { for (const request of this.requests.values()) if (request.agent) request.controller.abort() }

  setLayout(layout: BrowserPanelLayout | null): void { this.layout = layout; this.applyLayout() }
  setModalVisible(visible: boolean): void { this.modalVisible = visible; this.applyLayout() }

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
    } catch {}
    try {
      if (this.remoteBackingWindow && !this.remoteBackingWindow.isDestroyed()) {
        this.remoteBackingWindow.contentView.removeChildView(tab.view)
      }
    } catch {}

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

  private newTab(owner: BrowserSession): Tab {
    if (owner.tabs.size >= 6) throw new Error('每个会话最多打开 6 个标签页')
    const partition = `persist:seekclaw-browser-${owner.scope}`
    const isolatedSession = session.fromPartition(partition)
    isolatedSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false))
    isolatedSession.setPermissionCheckHandler(() => false)
    if (!owner.tabs.size) {
      isolatedSession.removeAllListeners('will-download')
      isolatedSession.on('will-download', event => event.preventDefault())
    }
    const view = new WebContentsView({ webPreferences: {
      partition, sandbox: true, contextIsolation: true, nodeIntegration: false, webSecurity: true,
      allowRunningInsecureContent: false, backgroundThrottling: false, navigateOnDragDrop: false, disableDialogs: true,
    } })
    const tab: Tab = { id: randomUUID(), view, ready: Promise.resolve(), hostWindow: 'main' }
    owner.tabs.set(tab.id, tab); owner.activeTabId = tab.id
    this.window.contentView.addChildView(view); view.setBounds({ x: 0, y: 48, width: 1000, height: 700 }); view.setVisible(false)
    const page = view.webContents
    const guard = (event: Electron.Event, url: string) => { try { browserUrl(url) } catch { event.preventDefault() } }
    page.on('will-navigate', guard)
    page.on('will-redirect', guard)
    page.on('will-frame-navigate', (event) => guard(event, event.url))
    page.on('will-attach-webview', event => event.preventDefault())
    page.setWindowOpenHandler(({ url }) => {
      try {
        browserUrl(url)
        if (!owner.paused) void this.execute(owner.scope, owner.sessionId, { action: 'open', url }).catch(() => undefined)
      } catch { /* External protocols and local files stay blocked. */ }
      return { action: 'deny' }
    })
    page.on('did-start-loading', () => this.publish(owner))
    page.on('did-stop-loading', () => this.publish(owner))
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
    page.on('render-process-gone', () => { tab.error = '页面进程已退出，请刷新重试'; this.publish(owner) })
    page.on('will-prevent-unload', event => event.preventDefault())
    if (typeof page.debugger?.on === 'function') {
      page.debugger.on('message', (_event, method, params) => {
        if (method !== 'Page.screencastFrame' || this.disposed || page.isDestroyed()) return
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
      })
    }
    // Chromium's debugger needs an initialized renderer. Sending commands before the
    // first load can wait indefinitely; initialize a trusted blank page before guards.
    tab.ready = page.loadURL('about:blank').then(async () => {
      page.debugger.attach('1.3')
      await page.debugger.sendCommand('Page.setInterceptFileChooserDialog', { enabled: true })
    })
    void tab.ready.catch(() => undefined)
    return tab
  }

  private async run(owner: BrowserSession, command: BrowserCommand, signal: AbortSignal, agent: boolean): Promise<Record<string, unknown>> {
    const actions = new Set(['open', 'tabs', 'select', 'navigate', 'snapshot', 'screenshot', 'click', 'fill', 'press', 'scroll', 'back', 'forward', 'reload', 'close'])
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
    this.applyLayout(); this.publish(owner)
    this.check(owner, signal, agent)
    if (command.action === 'screenshot') {
      const bounds = tab.view.getBounds()
      const width = Math.min(1600, bounds.width || 1000), height = Math.min(1200, bounds.height || 700)
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
    if (command.action === 'press' || command.action === 'scroll' || (command.action === 'click' && !command.element_id))
      await this.input(page, command)
    const action = ['click', 'fill'].includes(command.action) && command.element_id ? command : { action: 'snapshot' }
    let result: Record<string, unknown>
    try {
      if (action.action !== 'snapshot') {
        const actionResult = await page.executeJavaScriptInIsolatedWorld(999, [{ code: browserPageScript({ ...action, actionOnly: true }, randomUUID()) }])
        if (actionResult.browser_error) throw new Error(String(actionResult.browser_error))
      }
      await this.settle(page, signal)
      this.check(owner, signal, agent)
      result = await page.executeJavaScriptInIsolatedWorld(999, [{ code: browserPageScript({ action: 'snapshot' }, randomUUID()) }])
      if (result.browser_error) throw new Error(String(result.browser_error))
    } catch (error) {
      // Navigation can destroy the isolated execution context after a click. Never replay it.
      throw new Error(`页面操作未完成或页面已变化，请重新读取页面后继续；操作未重试。${error instanceof Error ? error.message : ''}`)
    }
    this.check(owner, signal, agent)
    return { tab_id: tab.id, ...result }
  }

  private async capture(tab: Tab, width: number, height: number, signal: AbortSignal): Promise<Buffer> {
    // A newly visible native view may not have submitted its first composited
    // frame yet. Retry only this read; never show/activate the window or replay input.
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
    try { await tab.view.webContents.loadURL(url) }
    catch (error) {
      tab.error = signal.aborted ? '导航已取消' : error instanceof Error ? error.message : String(error)
      this.applyLayout()
      this.publish(owner)
      throw new Error(tab.error)
    }
    finally { clearTimeout(timer); signal.removeEventListener('abort', stop) }
  }

  private async settle(page: Electron.WebContents, signal: AbortSignal): Promise<void> {
    if (signal.aborted) throw new Error('浏览器操作已取消')
    if (!page.isLoadingMainFrame()) return
    await new Promise<void>((resolve, reject) => {
      const finish = () => { cleanup(); resolve() }
      const abort = () => { cleanup(); reject(new Error('浏览器操作已取消')) }
      const cleanup = () => { clearTimeout(timer); page.removeListener('did-stop-loading', finish); signal.removeEventListener('abort', abort) }
      const timer = setTimeout(finish, 15000)
      page.once('did-stop-loading', finish)
      signal.addEventListener('abort', abort, { once: true })
      if (signal.aborted) abort()
      else if (!page.isLoadingMainFrame()) finish()
    })
  }

  private async input(page: Electron.WebContents, command: BrowserCommand): Promise<void> {
    const debuggerApi = page.debugger
    if (!debuggerApi.isAttached()) debuggerApi.attach('1.3')
    if (command.action === 'press') {
      const keys: Record<string, { code: string; key: string; keyCode: number; text?: string }> = {
        Enter: { code: 'Enter', key: 'Enter', keyCode: 13, text: '\r' }, Tab: { code: 'Tab', key: 'Tab', keyCode: 9 }, Escape: { code: 'Escape', key: 'Escape', keyCode: 27 },
        Backspace: { code: 'Backspace', key: 'Backspace', keyCode: 8 }, Space: { code: 'Space', key: ' ', keyCode: 32, text: ' ' },
        ArrowUp: { code: 'ArrowUp', key: 'ArrowUp', keyCode: 38 }, ArrowDown: { code: 'ArrowDown', key: 'ArrowDown', keyCode: 40 },
        ArrowLeft: { code: 'ArrowLeft', key: 'ArrowLeft', keyCode: 37 }, ArrowRight: { code: 'ArrowRight', key: 'ArrowRight', keyCode: 39 },
      }
      const key = keys[command.key || '']
      if (!key) throw new Error('Unsupported browser key')
      await debuggerApi.sendCommand('Input.dispatchKeyEvent', { type: 'keyDown', key: key.key, code: key.code, windowsVirtualKeyCode: key.keyCode, text: key.text })
      await debuggerApi.sendCommand('Input.dispatchKeyEvent', { type: 'keyUp', key: key.key, code: key.code, windowsVirtualKeyCode: key.keyCode })
    } else {
      const x = command.x ?? 100, y = command.y ?? 100
      if (![x, y].every(value => Number.isFinite(value) && value >= 0 && value <= 10000)) throw new Error('Invalid browser coordinates')
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
    this.applyLayout(); this.publish(owner)
  }

  dispose(): void {
    this.disposed = true
    for (const request of this.requests.values()) request.controller.abort()
    for (const owner of this.sessions.values()) for (const tab of [...owner.tabs.values()]) this.closeTab(owner, tab)
    this.sessions.clear()
    this.remoteStreams.clear()
    if (this.remoteBackingWindow && !this.remoteBackingWindow.isDestroyed()) {
      this.remoteBackingWindow.close()
    }
    this.remoteBackingWindow = null
  }
}
