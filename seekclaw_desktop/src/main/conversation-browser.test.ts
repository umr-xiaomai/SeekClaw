import { EventEmitter } from 'node:events'
import type { BrowserWindow } from 'electron'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ views: [] as Array<any>, sessions: new Map<string, any>() }))
vi.mock('electron', async () => {
  const { EventEmitter } = await import('node:events')
  class Page extends EventEmitter {
    url = ''; title = 'Test'; loading = false; stopped?: () => void
    debugger = Object.assign(new EventEmitter(), {
      attach: vi.fn(),
      isAttached: () => true,
      sendCommand: vi.fn(async () => undefined)
    })
    navigationHistory = { canGoBack: () => false, canGoForward: () => false }
    loadURL = vi.fn(async (url: string) => { this.url = url })
    downloadURL = vi.fn()
    capturePage = vi.fn(async () => ({ isEmpty: () => false, toPNG: () => Buffer.from('test image') }))
    executeJavaScriptInIsolatedWorld = vi.fn(async () => ({ snapshot_id: 'snapshot', url: this.url, text: 'page' }))
    executeJavaScript = vi.fn(async () => ({ value: 'eval-result', type: 'string' }))
    getTitle() { return this.title }
    getURL() { return this.url }
    isLoading() { return this.loading }
    isDestroyed() { return false }
    isLoadingMainFrame() { return this.loading }
    stop() { this.stopped?.() }
    close = vi.fn()
    focus() { throw new Error('Browser must never steal focus') }
    setWindowOpenHandler = vi.fn()
    reload() {}
  }
  class View {
    webContents = new Page()
    bounds = { x: 0, y: 0, width: 0, height: 0 }
    visible = false
    constructor(public options: unknown) { mocks.views.push(this) }
    setBounds(bounds: typeof this.bounds) { this.bounds = bounds }
    getBounds() { return this.bounds }
    getVisible() { return this.visible }
    setVisible(value: boolean) { this.visible = value }
  }
  return {
    BrowserWindow: class {},
    WebContentsView: View,
    session: {
      fromPartition: (partition: string) => {
        if (!mocks.sessions.has(partition)) {
          const emitter = new EventEmitter()
          const s = Object.assign(emitter, {
            permissionHandler: undefined as ((...args: any[]) => void) | undefined,
            permissionCheckHandler: undefined as ((...args: any[]) => boolean) | undefined,
            setPermissionRequestHandler: vi.fn((fn: any) => { s.permissionHandler = fn }),
            setPermissionCheckHandler: vi.fn((fn: any) => { s.permissionCheckHandler = fn })
          })
          mocks.sessions.set(partition, s)
        }
        return mocks.sessions.get(partition)
      }
    }
  }
})
import { browserCursorMoveScript, getVirtualCursorSvg } from './browser-cursor.js'
import { ConversationBrowser, formatPermissionLabel } from './conversation-browser.js'

function host(approvalHandler?: ConstructorParameters<typeof ConversationBrowser>[2]) {
  const window = Object.assign(new EventEmitter(), {
    isDestroyed: () => false, webContents: Object.assign(new EventEmitter(), { getZoomFactor: () => 1 }),
    getContentBounds: () => ({ width: 1200, height: 800 }), contentView: { addChildView: vi.fn(), removeChildView: vi.fn() }
  })
  return new ConversationBrowser(window as unknown as BrowserWindow, vi.fn(), approvalHandler)
}
const a = 'a'.repeat(64), b = 'b'.repeat(64)
beforeEach(() => { mocks.views.length = 0; mocks.sessions.clear() })
describe('conversation browser lifecycle', () => {
  it('isolates tabs, blocks popups to local files, and destroys views on disposal', async () => {
    const browser = host()
    await browser.execute(a, 'first', { action: 'open', url: 'https://example.com' })
    await browser.execute(b, 'second', { action: 'open', url: 'https://example.org' })
    expect(mocks.sessions.size).toBe(2)
    expect(mocks.views[0].options.webPreferences).toMatchObject({ sandbox: true, contextIsolation: true, nodeIntegration: false, disableDialogs: true })
    expect(mocks.views[0].options.webPreferences.preload).toBeUndefined()
    expect(mocks.views[0].getBounds().width).toBeGreaterThan(0)
    const popup = mocks.views[0].webContents.setWindowOpenHandler.mock.calls[0][0]
    expect(popup({ url: 'file:///C:/secret' })).toEqual({ action: 'deny' })
    expect(browser.state(a).tabs).toHaveLength(1)
    browser.dispose()
    for (const view of mocks.views) expect(view.webContents.close).toHaveBeenCalledOnce()
  })
  it('pauses only the owning conversation and refuses queued actions after cancellation', async () => {
    const browser = host()
    await browser.execute(a, 'first', { action: 'open' })
    await browser.execute(b, 'second', { action: 'open' })
    let release!: () => void
    mocks.views[0].webContents.executeJavaScriptInIsolatedWorld.mockImplementationOnce(() => new Promise(resolve => { release = () => resolve({ text: 'page' }) }))
    const active = browser.execute(a, 'first', { action: 'snapshot' }, 'active').catch((error: unknown) => error)
    await vi.waitFor(() => expect(release).toBeTypeOf('function'))
    const queued = browser.execute(a, 'first', { action: 'click', x: 10, y: 20 }, 'queued').catch((error: unknown) => error)
    browser.pause(a, true)
    expect(await browser.execute(b, 'second', { action: 'snapshot' }, 'other')).toMatchObject({ text: 'page' })
    release()
    expect(await active).toBeInstanceOf(Error)
    expect(await queued).toBeInstanceOf(Error)
    expect(mocks.views[0].webContents.debugger.sendCommand).not.toHaveBeenCalledWith('Input.dispatchMouseEvent', expect.anything())
    browser.dispose()
  })
  it('waits briefly for a newly visible frame without activating the window or repeating page input', async () => {
    const browser = host()
    await browser.execute(a, 'first', { action: 'open', url: 'https://example.com' })
    browser.setLayout({ scope: a, visible: true, bounds: { x: 0, y: 48, width: 1000, height: 700 } })
    const page = mocks.views[0].webContents
    page.capturePage.mockRejectedValueOnce(new Error('No composited surface'))
    const result = await browser.execute(a, 'first', { action: 'screenshot' }, 'capture')
    expect(result.image).toEqual({ data: Buffer.from('test image').toString('base64') })
    expect(page.capturePage).toHaveBeenCalledTimes(2)
    expect(page.capturePage).toHaveBeenLastCalledWith({ x: 0, y: 0, width: 1000, height: 700 }, { stayHidden: true, stayAwake: true })
    expect(page.debugger.sendCommand).not.toHaveBeenCalledWith('Input.dispatchMouseEvent', expect.anything())
    browser.dispose()
  })
  it('does not show a hidden view or keep retrying after screenshot cancellation', async () => {
    const browser = host()
    await browser.execute(a, 'first', { action: 'open' })
    const view = mocks.views[0], page = view.webContents
    page.capturePage.mockRejectedValue(new Error('No composited surface'))
    await expect(browser.execute(a, 'first', { action: 'screenshot' })).rejects.toThrow('页面尚未生成')
    expect(view.visible).toBe(false)
    expect(page.capturePage).toHaveBeenCalledTimes(1)
    browser.setLayout({ scope: a, visible: true, bounds: { x: 0, y: 48, width: 1000, height: 700 } })
    page.capturePage.mockClear()
    const capture = browser.execute(a, 'first', { action: 'screenshot' }, 'cancel-capture').catch((error: unknown) => error)
    await vi.waitFor(() => expect(page.capturePage).toHaveBeenCalledOnce())
    browser.cancel('cancel-capture')
    expect(await capture).toMatchObject({ message: expect.stringContaining('已取消') })
    expect(page.capturePage).toHaveBeenCalledOnce()
    browser.dispose()
  })
  it('records failed navigation errors with targetUrl, hides the native view, and handles recordCommandError safely', async () => {
    const browser = host()
    await browser.execute(a, 'first', { action: 'open', url: 'https://example.com' })
    browser.setLayout({ scope: a, visible: true, bounds: { x: 0, y: 48, width: 1000, height: 700 } })
    const view = mocks.views[0], page = view.webContents
    expect(view.visible).toBe(true)

    page.loadURL.mockRejectedValueOnce(new Error("ERR_NAME_NOT_RESOLVED (-105) loading 'https://xn--gzro94e9ms/'"))
    await expect(browser.execute(a, 'first', { action: 'navigate', url: 'https://xn--gzro94e9ms/' })).rejects.toThrow('ERR_NAME_NOT_RESOLVED')
    expect(view.visible).toBe(false)
    let state = browser.state(a)
    expect(state.tabs[0]?.url).toBe('https://xn--gzro94e9ms/')
    expect(state.tabs[0]?.error).toContain('ERR_NAME_NOT_RESOLVED')

    browser.recordCommandError(a, { action: 'navigate', url: 'https://broken.invalid/' }, new Error('ERR_CONNECTION_REFUSED (-102)'))
    state = browser.state(a)
    expect(state.tabs[0]?.url).toBe('https://broken.invalid/')
    expect(state.tabs[0]?.error).toContain('ERR_CONNECTION_REFUSED')
    expect(view.visible).toBe(false)

    // Unknown scope should be a no-op without throwing
    expect(() => browser.recordCommandError('c'.repeat(64), { action: 'navigate', url: 'https://example.com' }, new Error('ignored'))).not.toThrow()
    browser.dispose()
  })

  it('supports AI virtual cursor SVGs, hover/right_click/double_click/drag, combo shortcuts, evaluate, console, and network logs', async () => {
    expect(getVirtualCursorSvg('arrow')).toContain('#2563eb')
    expect(getVirtualCursorSvg('text')).toContain('<svg')
    expect(browserCursorMoveScript({ x: 120, y: 240, mode: 'arrow', effect: 'click' })).toContain('__seekclaw_ai_cursor_host__')
    expect(formatPermissionLabel('geolocation')).toContain('位置')

    const browser = host()
    await browser.execute(a, 'first', { action: 'open', url: 'https://example.com' })
    const page = mocks.views[0].webContents

    // Hover moves virtual cursor and dispatches mouseMoved
    await browser.execute(a, 'first', { action: 'hover', x: 120, y: 140 }, 'req-hover')
    expect(page.debugger.sendCommand).toHaveBeenCalledWith('Input.dispatchMouseEvent', expect.objectContaining({
      type: 'mouseMoved', x: 120, y: 140
    }))

    // Right click & double click
    await browser.execute(a, 'first', { action: 'right_click', x: 150, y: 160 }, 'req-rc')
    expect(page.debugger.sendCommand).toHaveBeenCalledWith('Input.dispatchMouseEvent', expect.objectContaining({
      type: 'mousePressed', button: 'right', x: 150, y: 160
    }))
    await browser.execute(a, 'first', { action: 'double_click', x: 180, y: 190 }, 'req-dc')
    expect(page.debugger.sendCommand).toHaveBeenCalledWith('Input.dispatchMouseEvent', expect.objectContaining({
      type: 'mousePressed', button: 'left', clickCount: 2
    }))

    // Drag
    await browser.execute(a, 'first', { action: 'drag', x: 10, y: 20, to_x: 110, to_y: 120, steps: 4 }, 'req-drag')
    expect(page.debugger.sendCommand).toHaveBeenCalledWith('Input.dispatchMouseEvent', expect.objectContaining({
      type: 'mouseReleased', x: 110, y: 120
    }))

    // Combo shortcut Ctrl+A
    await browser.execute(a, 'first', { action: 'press', key: 'Ctrl+A' }, 'req-key')
    expect(page.debugger.sendCommand).toHaveBeenCalledWith('Input.dispatchKeyEvent', expect.objectContaining({
      type: 'keyDown', key: 'a', modifiers: 2, commands: ['selectAll']
    }))

    // Evaluate JS
    const evalOut = await browser.execute(a, 'first', { action: 'evaluate', script: '1 + 1' }, 'req-eval')
    expect(page.executeJavaScript).toHaveBeenCalled()
    expect(evalOut.value).toBe('eval-result')

    // Console & Network log capture
    page.emit('console-message', { level: 'error', message: 'Boom error', sourceId: 'app.js', lineNumber: 12 })
    const consoleOut = await browser.execute(a, 'first', { action: 'console', level: 'error' }, 'req-con')
    expect(consoleOut.count).toBe(1)

    page.debugger.emit('message', {}, 'Network.requestWillBeSent', {
      requestId: 'r1', request: { url: 'https://api.example.com/data', method: 'POST' }, type: 'Fetch'
    })
    page.debugger.emit('message', {}, 'Network.responseReceived', {
      requestId: 'r1', response: { status: 500, statusText: 'Internal Server Error' }
    })
    page.debugger.emit('message', {}, 'Network.loadingFinished', { requestId: 'r1' })
    const netOut = await browser.execute(a, 'first', { action: 'network', status_filter: 'error' }, 'req-net')
    expect(netOut.count).toBe(1)

    browser.dispose()
  })

  it('prompts Desktop approval handler for permissions and downloads with session caching', async () => {
    const requests: any[] = []
    const browser = host(async (req) => {
      requests.push(req)
      if (req.kind === 'permission') return { id: req.id, approved: true, rememberSession: true }
      return { id: req.id, approved: req.filename === 'report.pdf' }
    })

    await browser.execute(a, 'first', { action: 'open', url: 'https://example.com' })
    const permRes = await browser.execute(a, 'first', {
      action: 'request_permission',
      permission: 'camera',
      reason: 'Need camera for QR scan'
    }, 'req-perm')
    expect(permRes.granted).toBe(true)
    expect(requests[0]).toMatchObject({
      kind: 'permission',
      permission: 'camera',
      permissionLabel: '摄像头',
      reason: 'Need camera for QR scan'
    })

    // Page-initiated permission check uses cached decision without extra popup
    const session = mocks.sessions.get(`persist:seekclaw-browser-${a}`)
    expect(session.permissionCheckHandler(mocks.views[0].webContents, 'media', 'https://example.com', { mediaType: 'video' })).toBe(true)

    // Download cancellation when rejected
    const deniedDl = await browser.execute(a, 'first', {
      action: 'download',
      url: 'https://example.com/malware.zip',
      filename: 'malware.zip'
    }, 'req-dl-deny')
    expect(deniedDl).toMatchObject({ approved: false, state: 'cancelled' })

    // Download triggers downloadURL when approved
    const okDlPromise = browser.execute(a, 'first', {
      action: 'download',
      url: 'https://example.com/report.pdf',
      filename: 'report.pdf'
    }, 'req-dl-ok')
    await vi.waitFor(() => expect(mocks.views[0].webContents.downloadURL).toHaveBeenCalledWith('https://example.com/report.pdf'))

    const fakeItem = Object.assign(new EventEmitter(), {
      getURL: () => 'https://example.com/report.pdf',
      getFilename: () => 'report.pdf',
      getTotalBytes: () => 1024,
      getReceivedBytes: () => 1024,
      setSavePath: vi.fn(),
      pause: vi.fn(),
      isPaused: () => false,
      resume: vi.fn(),
      cancel: vi.fn()
    })
    session.emit('will-download', {}, fakeItem)
    fakeItem.emit('done', {}, 'completed')
    const okDl = await okDlPromise
    expect(okDl).toMatchObject({ approved: true, state: 'completed', filename: 'report.pdf' })

    browser.dispose()
  })
})
