import { EventEmitter } from 'node:events'
import type { BrowserWindow } from 'electron'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ views: [] as Array<any>, sessions: new Map<string, any>() }))
vi.mock('electron', async () => {
  const { EventEmitter } = await import('node:events')
  class Page extends EventEmitter {
    url = ''; title = 'Test'; loading = false; stopped?: () => void
    debugger = { attach: vi.fn(), isAttached: () => true, sendCommand: vi.fn(async () => undefined) }
    navigationHistory = { canGoBack: () => false, canGoForward: () => false }
    loadURL = vi.fn(async (url: string) => { this.url = url })
    capturePage = vi.fn(async () => ({ isEmpty: () => false, toPNG: () => Buffer.from('test image') }))
    executeJavaScriptInIsolatedWorld = vi.fn(async () => ({ snapshot_id: 'snapshot', url: this.url, text: 'page' }))
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
  return { BrowserWindow: class {}, WebContentsView: View, session: { fromPartition: (partition: string) => {
    if (!mocks.sessions.has(partition)) mocks.sessions.set(partition, { setPermissionRequestHandler: vi.fn(), setPermissionCheckHandler: vi.fn(), removeAllListeners: vi.fn(), on: vi.fn() })
    return mocks.sessions.get(partition)
  } } }
})
import { ConversationBrowser } from './conversation-browser.js'

function host() {
  const window = Object.assign(new EventEmitter(), {
    isDestroyed: () => false, webContents: Object.assign(new EventEmitter(), { getZoomFactor: () => 1 }),
    getContentBounds: () => ({ width: 1200, height: 800 }), contentView: { addChildView: vi.fn(), removeChildView: vi.fn() }
  })
  return new ConversationBrowser(window as unknown as BrowserWindow, vi.fn())
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
})
