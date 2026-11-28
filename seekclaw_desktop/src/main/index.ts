import { spawn, type ChildProcess } from 'node:child_process'
import { chmodSync, existsSync, statSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { basename, extname, join, resolve } from 'node:path'
import { release } from 'node:os'
import { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, Notification, shell, Tray } from 'electron'
import { electronApp, is, optimizer } from '@electron-toolkit/utils'
import icon from '../../resources/logo.png?asset'
import type { AppInfo, DaemonMessage, DaemonRequestOptions, OpenLinkResult, RevertDiffItem } from '../shared/ipc.js'
import { isExecutableLinkPath, resolveMarkdownLinkTarget } from '../shared/markdown-links.js'
import { readLocalLinkPreview } from './file-preview.js'
import { ConversationBrowser } from './conversation-browser.js'
import type { BrowserApprovalDecision, BrowserApprovalRequest, BrowserCommand, BrowserPanelLayout } from '../shared/browser.js'
import { DaemonClient } from './daemon-client.js'
import { configureDesktopPaths } from './desktop-paths.js'
import { PendingApprovalCache } from './pending-approval-cache.js'
import { checkoutGitBranch, createGitBranch, getGitBranches, getGitCommitDiff, getGitHistory, getGitOverview, listWorkspaceFiles, openProjectTerminal, revertFileDiffs } from './project-tools.js'
import { createServerDirectory, listServerDirectory, listServerRoots, readServerImages, saveUploadedFile } from './web-fs.js'
import { isMutatingDaemonMethod, LanWebServer } from './web-server.js'
import type { ServerFilePickerKind, WebServerConfig } from '../shared/web-server.js'
import {
  destroyComputerOverlay,
  hideComputerOverlay,
  isComputerOverlayActive,
  scheduleHideComputerOverlay,
  showComputerOverlay
} from './computer-overlay.js'

const desktopPaths = configureDesktopPaths(app)
const daemon = new DaemonClient(desktopPaths.daemonEndpoint)
const pendingApprovals = new PendingApprovalCache()
const pendingBrowserApprovals = new Map<string, (decision: BrowserApprovalDecision) => void>()
let mainWindow: BrowserWindow | null = null
let conversationBrowser: ConversationBrowser | null = null
let browserAttachment: Promise<void> | null = null
let browserConnectionGeneration = 0
let tray: Tray | null = null
let isQuitting = false
let managedDaemon: ChildProcess | null = null
let runtimeShutdownStarted = false

function buildAppInfo(): Omit<AppInfo, 'runtimeMode'> {
  return {
    version: app.getVersion(),
    platform: process.platform as AppInfo['platform'],
    supportsMica,
    defaultWorkspace: is.dev ? resolve(app.getAppPath(), '..') : app.getPath('userData'),
    documentsPath: app.getPath('documents'),
    userProfilePath: desktopPaths.userProfile
  }
}

const lanWebServer = new LanWebServer({
  userDataPath: app.getPath('userData'),
  daemon,
  pendingApprovals,
  getBrowser: () => conversationBrowser,
  ensureBrowserAttached: () => attachBrowser(),
  getAppInfo: buildAppInfo,
  getRendererSource: () => ({
    devUrl: is.dev ? process.env.ELECTRON_RENDERER_URL : undefined,
    distDir: join(__dirname, '../renderer')
  }),
  onDesktopSyncRefresh: (reason) => {
    mainWindow?.webContents.send('sync:refresh', reason)
  }
})
const activeNotifications = new Set<Notification>()
const supportsMica = process.platform === 'win32' && Number(release().split('.')[2] ?? 0) >= 22000
const maxImageCount = 10
const maxImageBytes = 10 * 1024 * 1024
const maxTotalImageBytes = 40 * 1024 * 1024
const imageMediaTypes: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif'
}

const delay = (milliseconds: number): Promise<void> =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds))

function resolveRuntimeExecutable(): string | null {
  const executable = process.platform === 'win32' ? 'seekclaw.exe' : 'seekclaw'
  const configured = process.env.SEEKCLAW_RUNTIME_EXECUTABLE?.trim()
  const platformRid = process.platform === 'win32' ? 'win-x64' : 'linux-x64'
  const candidates = [
    configured ? resolve(configured) : '',
    app.isPackaged ? join(process.resourcesPath, 'runtime', executable) : '',
    !app.isPackaged ? resolve(app.getAppPath(), 'runtime', platformRid, executable) : '',
    !app.isPackaged ? resolve(app.getAppPath(), 'runtime', 'win-x64', executable) : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'seekclaw_cli', 'bin', 'Debug', 'net10.0', executable)
      : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'seekclaw_cli', 'bin', 'Release', 'net10.0', platformRid, 'publish', executable)
      : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'seekclaw_cli', 'bin', 'Release', 'net10.0', 'win-x64', 'publish', executable)
      : ''
  ].filter(Boolean)
  const found = candidates.find((candidate) => existsSync(candidate)) ?? null
  if (found && process.platform !== 'win32') {
    try {
      const stat = statSync(found)
      if ((stat.mode & 0o111) === 0) {
        chmodSync(found, 0o755)
      }
    } catch {}
  }
  return found
}

async function ensureDaemonRunning(): Promise<void> {
  const existing = await daemon.connect()
  if (existing.connected) return

  const executable = resolveRuntimeExecutable()
  if (!executable) {
    throw new Error('Bundled SeekClaw Runtime was not found. Rebuild the Desktop release package.')
  }

  const child = spawn(executable, ['daemon'], {
    cwd: is.dev ? resolve(app.getAppPath(), '..') : app.getPath('userData'),
    env: {
      ...process.env,
      SEEKCLAW_MANAGED_BY_DESKTOP: '1',
      DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
      DOTNET_BUNDLE_EXTRACT_BASE_DIR: desktopPaths.dotnetBundle
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  })
  managedDaemon = child

  let startupError: Error | null = null
  let childStderr = ''
  child.stderr?.on('data', (chunk) => {
    childStderr += chunk.toString()
    if (childStderr.length > 4000) childStderr = childStderr.slice(-4000)
  })
  child.once('error', (error) => {
    startupError = error
  })
  child.once('exit', (code) => {
    if (managedDaemon === child) managedDaemon = null
    if (code !== 0 && code !== null) {
      console.error(`SeekClaw Runtime exited with code ${code}. Stderr: ${childStderr}`)
    }
  })

  for (let attempt = 0; attempt < 28; attempt++) {
    await delay(attempt === 0 ? 150 : 250)
    if (startupError) throw startupError
    if (child.exitCode !== null) {
      throw new Error(`SeekClaw Runtime exited with code ${child.exitCode}: ${childStderr.trim() || 'unknown error'}`)
    }
    const state = await daemon.connect()
    if (state.connected) return
  }

  if (child.exitCode === null) child.kill()
  throw new Error(`SeekClaw Runtime did not become ready in time. ${childStderr.trim() ? 'Stderr: ' + childStderr.trim() : ''}`)
}

async function stopManagedDaemon(): Promise<void> {
  const child = managedDaemon
  if (!child) {
    daemon.disconnect()
    return
  }

  await Promise.race([
    daemon.request('shutdown').catch(() => undefined),
    delay(1_000)
  ])
  daemon.disconnect()

  if (child.exitCode === null) {
    await Promise.race([
      new Promise<void>((resolveExit) => child.once('exit', () => resolveExit())),
      delay(1_500)
    ])
  }
  if (child.exitCode === null) child.kill()
  if (managedDaemon === child) managedDaemon = null
}

function nativeWindowColors(): { background: string; titlebar: string; symbols: string } {
  const dark = nativeTheme.shouldUseDarkColors
  return {
    background: dark ? '#181818' : '#fafafa',
    titlebar: dark ? '#191919' : '#eceef2',
    symbols: dark ? '#f2f2f2' : '#343434'
  }
}

function syncNativeWindowTheme(): void {
  if (!mainWindow) return
  const colors = nativeWindowColors()
  if (!supportsMica) mainWindow.setBackgroundColor(colors.background)
  if (process.platform !== 'darwin') {
    mainWindow.setTitleBarOverlay({
      color: supportsMica ? '#00000000' : colors.titlebar,
      symbolColor: colors.symbols,
      height: 48
    })
  }
}

function notificationText(message: DaemonMessage, key: string): string {
  const value = message.details?.[key]
  return typeof value === 'string' ? value : ''
}

function showMainWindow(): void {
  if (!mainWindow) {
    createWindow()
    return
  }
  if (mainWindow.isMinimized()) mainWindow.restore()
  if (!mainWindow.isVisible()) mainWindow.show()
  mainWindow.focus()
}

function createTray(): void {
  if (tray) return
  try {
    tray = new Tray(icon)
    tray.setToolTip('SeekClaw')

    const contextMenu = Menu.buildFromTemplate([
      {
        label: '显示',
        click: () => showMainWindow()
      },
      { type: 'separator' },
      {
        label: '退出',
        click: () => {
          isQuitting = true
          app.quit()
        }
      }
    ])

    tray.setContextMenu(contextMenu)

    tray.on('click', () => {
      showMainWindow()
    })

    tray.on('double-click', () => {
      showMainWindow()
    })
  } catch (error) {
    console.warn('System tray initialization skipped (not supported or failed):', error)
    tray = null
  }
}

function showNativeNotification(title: string, body: string): void {
  if (!Notification.isSupported()) return
  const notification = new Notification({ title, body })
  activeNotifications.add(notification)
  notification.once('click', () => {
    showMainWindow()
  })
  notification.once('close', () => activeNotifications.delete(notification))
  notification.show()
}

function showScheduleNativeNotification(message: DaemonMessage): void {
  if (message.event === 'schedule.upcoming') {
    const name = notificationText(message, 'name') || '计划任务'
    showNativeNotification('计划任务提醒', `一分钟后「${name}」将自动执行`)
    return
  }

  if (message.event !== 'schedule.updated') return
  const name =
    notificationText(message, 'name')
    || notificationText(message, 'taskId')
    || '计划任务'
  const status = notificationText(message, 'status')
  const error = notificationText(message, 'error')

  if (status === 'cancelled') {
    showNativeNotification('计划任务已取消', `「${name}」已取消`)
    return
  }
  if (status === 'error') {
    const suffix = error ? `：${error}` : ''
    showNativeNotification('计划任务执行失败', `「${name}」执行失败${suffix}`)
    return
  }
  showNativeNotification('计划任务完成', `「${name}」已完成`)
}

function createWindow(): void {
  const colors = nativeWindowColors()
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 760,
    minHeight: 620,
    show: false,
    backgroundColor: supportsMica ? '#00000000' : colors.background,
    ...(supportsMica ? { backgroundMaterial: 'mica' as const } : {}),
    title: 'SeekClaw',
    icon,
    titleBarStyle: 'hidden',
    titleBarOverlay: process.platform === 'darwin' ? false : {
      color: supportsMica ? '#00000000' : colors.titlebar,
      symbolColor: colors.symbols,
      height: 48
    },
    webPreferences: {
      preload: join(__dirname, '../preload/index.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false
    }
  })

  conversationBrowser = new ConversationBrowser(
    mainWindow,
    state => {
      mainWindow?.webContents.send('browser:changed', state)
      lanWebServer.broadcastBrowserState(state)
    },
    (req: BrowserApprovalRequest) => {
      const targetWindow = mainWindow
      if (!targetWindow || targetWindow.isDestroyed()) {
        return Promise.resolve({ id: req.id, approved: false })
      }
      conversationBrowser?.setModalVisible(true)
      return new Promise<BrowserApprovalDecision>((resolve) => {
        const timer = setTimeout(() => {
          finish({ id: req.id, approved: false })
        }, 120_000)
        const finish = (decision: BrowserApprovalDecision) => {
          clearTimeout(timer)
          pendingBrowserApprovals.delete(req.id)
          if (pendingBrowserApprovals.size === 0) {
            conversationBrowser?.setModalVisible(false)
          }
          resolve(decision)
        }
        pendingBrowserApprovals.set(req.id, finish)
        targetWindow.webContents.send('browser:approval-request', req)
      })
    }
  )

  mainWindow.on('ready-to-show', () => mainWindow?.show())
  mainWindow.on('close', (event) => {
    if (!isQuitting) {
      event.preventDefault()
      mainWindow?.hide()
    }
  })
  mainWindow.on('closed', () => {
    for (const [id, finish] of [...pendingBrowserApprovals.entries()]) {
      finish({ id, approved: false })
    }
    pendingBrowserApprovals.clear()
    conversationBrowser?.dispose()
    conversationBrowser = null
    mainWindow = null
  })
  // Artifact previews have an opaque sandbox origin. Keep them on srcdoc/blank,
  // including attempts to navigate via links, forms or script-assigned locations.
  mainWindow.webContents.on('will-frame-navigate', (event) => {
    if (!event.isMainFrame && !['about:srcdoc', 'about:blank'].includes(event.url)) event.preventDefault()
  })
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://') || url.startsWith('mailto:')) void shell.openExternal(url)
    return { action: 'deny' }
  })

  if (is.dev && process.env.ELECTRON_RENDERER_URL)
    void mainWindow.loadURL(process.env.ELECTRON_RENDERER_URL)
  else
    void mainWindow.loadFile(join(__dirname, '../renderer/index.html'))
}

async function attachBrowser(): Promise<void> {
  const generation = browserConnectionGeneration
  if (!browserAttachment) browserAttachment = daemon.request('browser.attach', undefined, { timeoutMs: 5000 })
    .then(() => undefined).catch(error => { if (generation === browserConnectionGeneration) browserAttachment = null; throw error })
  return browserAttachment
}

function browserFor(event: Electron.IpcMainInvokeEvent): ConversationBrowser {
  if (!mainWindow || event.sender !== mainWindow.webContents || event.senderFrame !== mainWindow.webContents.mainFrame || !conversationBrowser)
    throw new Error('Browser control is available only to the main application')
  return conversationBrowser
}

function registerIpc(): void {
  ipcMain.handle('app:info', () => ({
    ...buildAppInfo(),
    runtimeMode: 'desktop' as const
  }))

  ipcMain.handle('web-server:status', () => lanWebServer.getStatus())
  ipcMain.handle('web-server:update', (_event, patch: Partial<WebServerConfig>) => lanWebServer.updateConfig(patch))
  ipcMain.handle('web-server:regenerate-token', () => lanWebServer.regenerateToken())
  ipcMain.handle('web-server:disconnect-clients', () => lanWebServer.disconnectClients())

  ipcMain.handle('server-fs:roots', async () => {
    const info = buildAppInfo()
    let projects: Array<{ id: string; name: string; path: string }> = []
    if (daemon.state.connected) {
      try {
        const msg = await daemon.request('project.list')
        const parsed = JSON.parse(msg.data) as { projects?: Array<{ id: string; name: string; path: string }> }
        if (Array.isArray(parsed.projects)) projects = parsed.projects
      } catch {
        // ignore if project list fails
      }
    }
    return listServerRoots({ userProfile: info.userProfilePath, documentsPath: info.documentsPath, projects })
  })
  ipcMain.handle('server-fs:list', (_event, params: { path?: string; kind?: ServerFilePickerKind; showHidden?: boolean }) =>
    listServerDirectory({ ...params, fallbackPath: desktopPaths.userProfile })
  )
  ipcMain.handle('server-fs:mkdir', (_event, parentPath: string, name: string) => createServerDirectory(parentPath, name))
  ipcMain.handle('server-fs:read-images', (_event, paths: string[]) => readServerImages(paths))
  ipcMain.handle('server-fs:upload', (_event, params: { name: string; dataBase64: string; targetDir?: string }) =>
    saveUploadedFile({
      ...params,
      fallbackTempDir: join(desktopPaths.userProfile, '.seekclaw', 'uploads')
    })
  )

  lanWebServer.onStatus((status) => {
    mainWindow?.webContents.send('web-server:status', status)
  })

  ipcMain.handle('app:select-workspace', async () => {
    const options: Electron.OpenDialogOptions = {
      title: 'Open workspace',
      properties: ['openDirectory', 'createDirectory']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    return result.canceled ? null : result.filePaths[0] ?? null
  })

  ipcMain.handle('app:select-images', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择图片',
      filters: [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif'] }],
      properties: ['openFile', 'multiSelections']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    if (result.canceled) return { images: [] }

    const images: Array<{ name: string; mediaType: string; data: string; sizeBytes: number }> = []
    const warnings: string[] = []
    let totalBytes = 0
    if (result.filePaths.length > maxImageCount)
      warnings.push(`一次最多添加 ${maxImageCount} 张图片，已保留前 ${maxImageCount} 张。`)
    for (const path of result.filePaths.slice(0, maxImageCount)) {
      const name = basename(path)
      const mediaType = imageMediaTypes[extname(path).toLocaleLowerCase()]
      if (!mediaType) {
        warnings.push(`${name} 的格式暂不支持。`)
        continue
      }
      let data: Buffer
      try { data = await readFile(path) }
      catch {
        warnings.push(`${name} 无法读取，未添加。`)
        continue
      }
      if (data.byteLength > maxImageBytes) {
        warnings.push(`${name} 超过 10 MB，未添加。`)
        continue
      }
      if (totalBytes + data.byteLength > maxTotalImageBytes) {
        warnings.push(`图片合计不能超过 40 MB，${name} 未添加。`)
        continue
      }
      totalBytes += data.byteLength
      images.push({ name, mediaType, data: data.toString('base64'), sizeBytes: data.byteLength })
    }
    return { images, warning: warnings.join(' ') || undefined }
  })

  ipcMain.handle('app:select-skill-files', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '导入全局技能',
      filters: [{ name: 'Skill 文件', extensions: ['md', 'zip'] }],
      properties: ['openFile', 'multiSelections']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    if (result.canceled) return { paths: [] }
    return { paths: result.filePaths }
  })

  ipcMain.handle('app:select-files', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择文件',
      properties: ['openFile', 'multiSelections']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    if (result.canceled) return []
    return result.filePaths
  })

  ipcMain.handle('app:read-file-base64', async (_event, path: string) => {
    try {
      const data = await readFile(path)
      const ext = extname(path).toLocaleLowerCase()
      const mediaType = imageMediaTypes[ext] || 'image/png'
      return {
        data: data.toString('base64'),
        mediaType,
        sizeBytes: data.byteLength
      }
    } catch {
      return null
    }
  })

  ipcMain.handle('app:show-item', async (_event, path: string) => {
    shell.showItemInFolder(path)
  })

  ipcMain.handle('app:open-link', async (_event, href: string, workspace?: string): Promise<OpenLinkResult> => {
    const resolved = resolveMarkdownLinkTarget(typeof href === 'string' ? href : '', workspace, process.platform)
    if (resolved.kind === 'external') {
      await shell.openExternal(resolved.url)
      return { ok: true, kind: 'external', target: resolved.url }
    }
    if (resolved.kind === 'fragment') {
      return { ok: true, kind: 'external' }
    }
    if (resolved.kind === 'unsupported') {
      return { ok: false, error: resolved.reason }
    }
    if (!existsSync(resolved.path)) {
      return { ok: false, error: `找不到目标文件：${resolved.path}` }
    }
    const isDir = statSync(resolved.path).isDirectory()
    if (!isDir && isExecutableLinkPath(resolved.path)) {
      shell.showItemInFolder(resolved.path)
      return { ok: true, kind: 'folder', target: resolved.path }
    }
    const openError = await shell.openPath(resolved.path)
    if (openError) {
      shell.showItemInFolder(resolved.path)
      return { ok: true, kind: 'folder', target: resolved.path }
    }
    return { ok: true, kind: isDir ? 'folder' : 'path', target: resolved.path }
  })

  ipcMain.handle('app:read-link-preview', (_event, href: string, workspace?: string) =>
    readLocalLinkPreview(typeof href === 'string' ? href : '', workspace)
  )

  ipcMain.handle('app:close', () => mainWindow?.close())
  ipcMain.handle('app:exit', () => app.quit())

  ipcMain.handle('app:open-devtools', () => {
    mainWindow?.webContents.openDevTools({ mode: 'detach' })
  })

  ipcMain.handle('app:notify', (_event, title: string, body: string) => {
    if (typeof title === 'string' && typeof body === 'string') showNativeNotification(title, body)
  })

  ipcMain.handle('app:set-theme', (_event, theme: 'system' | 'light' | 'dark') => {
    if (theme === 'system' || theme === 'light' || theme === 'dark') {
      nativeTheme.themeSource = theme
      syncNativeWindowTheme()
    }
  })

  ipcMain.handle('app:set-zoom-factor', (_event, factor: number) => {
    if (typeof factor === 'number' && Number.isFinite(factor) && factor >= 0.5 && factor <= 3) {
      mainWindow?.webContents.setZoomFactor(factor)
    }
  })

  ipcMain.handle('project:open-terminal', (_event, path: string) => openProjectTerminal(path))
  ipcMain.handle('project:git-overview', (_event, path: string) => getGitOverview(path))
  ipcMain.handle('project:git-history', (_event, path: string) => getGitHistory(path))
  ipcMain.handle('project:git-branches', (_event, path: string) => getGitBranches(path))
  ipcMain.handle('project:checkout-branch', (_event, path: string, branch: string) => checkoutGitBranch(path, branch))
  ipcMain.handle('project:create-branch', (_event, path: string, branch: string) => createGitBranch(path, branch))
  ipcMain.handle('project:revert-file-diffs', (_event, workspace: string, patches: RevertDiffItem[]) =>
    revertFileDiffs(workspace, patches)
  )
  ipcMain.handle('project:git-commit-diff', (_event, path: string, hash: string) =>
    getGitCommitDiff(path, hash)
  )
  ipcMain.handle('project:list-files', (_event, path: string, query?: string) =>
    listWorkspaceFiles(path, query)
  )

  ipcMain.handle('browser:open', async (event, params: { sessionId: string; workspace?: string; global?: boolean }) => {
    const browser = browserFor(event)
    const response = await daemon.request('browser.scope', params)
    const { scope, sessionId } = JSON.parse(response.data) as { scope: string; sessionId: string }
    const state = browser.ensure(scope, sessionId)
    if (!state.tabs.length) await browser.execute(scope, sessionId, { action: 'open' })
    return browser.state(scope)
  })
  ipcMain.handle('browser:state', (event, id: string) => browserFor(event).stateForSession(id))
  ipcMain.handle('browser:command', async (event, scope: string, command: BrowserCommand) => {
    const browser = browserFor(event)
    const state = browser.state(scope)
    try {
      await browser.execute(scope, state.sessionId, command)
    } catch (error) {
      browser.recordCommandError(scope, command, error)
    }
    return browser.state(scope)
  })
  ipcMain.handle('browser:pause', (event, scope: string, paused: boolean) => browserFor(event).pause(scope, !!paused))
  ipcMain.handle('browser:layout', (event, layout: BrowserPanelLayout | null) => browserFor(event).setLayout(layout))
  ipcMain.handle('browser:overlay', (event, visible: boolean) => browserFor(event).setModalVisible(!!visible))
  ipcMain.handle('browser:approval-decide', (_event, decision: BrowserApprovalDecision) => {
    if (!decision || typeof decision.id !== 'string') return
    const finish = pendingBrowserApprovals.get(decision.id)
    if (finish) {
      finish({ id: decision.id, approved: Boolean(decision.approved), rememberSession: Boolean(decision.rememberSession) })
    }
  })

  ipcMain.handle('daemon:connect', async () => {
    const state = await daemon.connect()
    if (state.connected) {
      await attachBrowser()
      // The renderer subscribes before connecting, so replay approvals that were
      // emitted while a reload was in progress. Without this, the runtime waits
      // for its full approval timeout even though the user can see the turn.
      for (const message of pendingApprovals.snapshot())
        mainWindow?.webContents.send('daemon:event', message)
    }
    return state
  })
  ipcMain.handle('daemon:disconnect', () => {
    pendingApprovals.clear()
    daemon.disconnect()
  })
  ipcMain.handle(
    'daemon:request',
    async (_event, method: string, params?: Record<string, unknown>, options?: DaemonRequestOptions) => {
      if (method === 'chat') await attachBrowser()
      const response = await daemon.request(method, params, options)
      if (method === 'agent.approve' || method === 'agent.approval')
        pendingApprovals.acknowledge(params?.approvalId)
      if (isMutatingDaemonMethod(method)) lanWebServer.broadcastSyncRefresh(method)
      return response
    })

  daemon.on('event', (message) => {
    if (message.event === 'browser.cancel') {
      if (typeof message.details?.requestId === 'string') conversationBrowser?.cancel(message.details.requestId)
      return
    }
    if (message.event === 'browser.request') {
      const { requestId, scope, sessionId, command } = message.details ?? {}
      if (typeof requestId !== 'string' || typeof scope !== 'string' || typeof sessionId !== 'string' || !command || typeof command !== 'object') return
      const browser = conversationBrowser
      const connection = browserAttachment
      const generation = browserConnectionGeneration
      void (async () => {
        let result: Record<string, unknown>
        try {
          if (!browser) throw new Error('Desktop browser is unavailable')
          result = await browser.execute(scope, sessionId, command as BrowserCommand, requestId)
        } catch (error) { result = { error: error instanceof Error ? error.message : String(error) } }
        // Never reconnect to deliver a response from an obsolete connection.
        if (connection && connection === browserAttachment && generation === browserConnectionGeneration)
          await daemon.request('browser.reply', { requestId, result }).catch(() => undefined)
      })()
      return
    }
    pendingApprovals.remember(message)
    pendingApprovals.finish(message)
    if (message.event === 'tool_start' && (message.data === 'computer' || message.data === 'computer_inspect')) {
      showComputerOverlay(() => {
        const params = message.sessionId
          ? { sessionId: message.sessionId }
          : { requestId: message.id }
        void daemon.request('agent.cancel', params).catch(() => undefined)
      })
    } else if (message.event === 'tool_done' && (message.data?.includes('computer') || isComputerOverlayActive())) {
      scheduleHideComputerOverlay(900)
    } else if (message.event === 'done' || message.event === 'cancelled' || message.event === 'error') {
      hideComputerOverlay(true)
    }

    showScheduleNativeNotification(message)
    mainWindow?.webContents.send('daemon:event', message)
    lanWebServer.broadcastDaemonEvent(message)
  })
  daemon.on('state', (state) => {
    if (!state.connected) { pendingApprovals.clear(); browserConnectionGeneration++; browserAttachment = null; conversationBrowser?.cancelAgentRequests() }
    mainWindow?.webContents.send('daemon:state', state)
    lanWebServer.broadcastDaemonState(state)
  })
}

const hasSingleInstanceLock = app.requestSingleInstanceLock()
if (!hasSingleInstanceLock) {
  app.quit()
} else {
  app.on('second-instance', () => {
    showMainWindow()
  })

  app.whenReady().then(async () => {
    registerIpc()
    electronApp.setAppUserModelId('com.hoilai.seekclaw')
    app.on('browser-window-created', (_, window) => optimizer.watchWindowShortcuts(window))
    createTray()
    await ensureDaemonRunning().catch((error) => console.error('Unable to start SeekClaw Runtime:', error))
    await lanWebServer.init().catch((error) => console.error('Unable to initialize LAN Web Server:', error))
    createWindow()
    nativeTheme.on('updated', syncNativeWindowTheme)

    app.on('activate', () => {
      showMainWindow()
    })
  })
}

app.on('before-quit', (event) => {
  isQuitting = true
  pendingApprovals.clear()
  destroyComputerOverlay()
  conversationBrowser?.dispose()
  void lanWebServer.stop()
  if (tray) {
    tray.destroy()
    tray = null
  }
  if (!managedDaemon || runtimeShutdownStarted) {
    daemon.disconnect()
    return
  }
  event.preventDefault()
  runtimeShutdownStarted = true
  void stopManagedDaemon().finally(() => app.quit())
})

app.on('window-all-closed', () => {
  if (isQuitting) app.quit()
})
