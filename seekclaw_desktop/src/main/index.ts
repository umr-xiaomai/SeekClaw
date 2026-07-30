import { spawn, type ChildProcess } from 'node:child_process'
import { existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { release } from 'node:os'
import { app, BrowserWindow, dialog, ipcMain, nativeTheme, shell } from 'electron'
import { electronApp, is, optimizer } from '@electron-toolkit/utils'
import icon from '../../resources/logo.png?asset'
import { DaemonClient } from './daemon-client.js'
import { getGitHistory, getGitOverview, openProjectTerminal } from './project-tools.js'

const daemon = new DaemonClient()
let mainWindow: BrowserWindow | null = null
let managedDaemon: ChildProcess | null = null
let runtimeShutdownStarted = false
const supportsMica = process.platform === 'win32' && Number(release().split('.')[2] ?? 0) >= 22000

const delay = (milliseconds: number): Promise<void> =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds))

function resolveRuntimeExecutable(): string | null {
  const executable = process.platform === 'win32' ? 'seekclaw.exe' : 'seekclaw'
  const configured = process.env.SEEKCLAW_RUNTIME_EXECUTABLE?.trim()
  const candidates = [
    configured ? resolve(configured) : '',
    app.isPackaged ? join(process.resourcesPath, 'runtime', executable) : '',
    !app.isPackaged ? resolve(app.getAppPath(), 'runtime', 'win-x64', executable) : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'seekclaw_cli', 'bin', 'Debug', 'net10.0', executable)
      : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'seekclaw_cli', 'bin', 'Release', 'net10.0', 'win-x64', 'publish', executable)
      : ''
  ].filter(Boolean)
  return candidates.find((candidate) => existsSync(candidate)) ?? null
}

async function ensureDaemonRunning(): Promise<void> {
  const existing = await daemon.connect()
  if (existing.connected) return

  const executable = resolveRuntimeExecutable()
  if (!executable)
    throw new Error('Bundled SeekClaw Runtime was not found. Rebuild the Desktop release package.')

  const child = spawn(executable, ['daemon'], {
    cwd: app.getPath('documents'),
    env: { ...process.env, SEEKCLAW_MANAGED_BY_DESKTOP: '1' },
    stdio: 'ignore',
    windowsHide: true
  })
  managedDaemon = child
  let startupError: Error | null = null
  child.once('error', (error) => { startupError = error })
  child.once('exit', () => {
    if (managedDaemon === child) managedDaemon = null
  })

  for (let attempt = 0; attempt < 24; attempt++) {
    await delay(attempt === 0 ? 120 : 250)
    if (startupError) throw startupError
    if (child.exitCode !== null) throw new Error(`SeekClaw Runtime exited with code ${child.exitCode}.`)
    const state = await daemon.connect()
    if (state.connected) return
  }

  if (child.exitCode === null) child.kill()
  throw new Error('SeekClaw Runtime did not become ready in time.')
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
    background: dark ? '#181818' : '#f7f7f7',
    titlebar: dark ? '#202020' : '#f3f3f3',
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
      height: 42
    })
  }
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
      height: 42
    },
    webPreferences: {
      preload: join(__dirname, '../preload/index.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false
    }
  })

  mainWindow.on('ready-to-show', () => mainWindow?.show())
  mainWindow.on('closed', () => { mainWindow = null })
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) void shell.openExternal(url)
    return { action: 'deny' }
  })

  if (is.dev && process.env.ELECTRON_RENDERER_URL)
    void mainWindow.loadURL(process.env.ELECTRON_RENDERER_URL)
  else
    void mainWindow.loadFile(join(__dirname, '../renderer/index.html'))
}

function registerIpc(): void {
  ipcMain.handle('app:info', () => ({
    version: app.getVersion(),
    platform: process.platform,
    supportsMica,
    defaultWorkspace: is.dev ? resolve(app.getAppPath(), '..') : app.getPath('documents')
  }))

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

  ipcMain.handle('app:show-item', async (_event, path: string) => {
    shell.showItemInFolder(path)
  })

  ipcMain.handle('app:close', () => mainWindow?.close())

  ipcMain.handle('app:set-theme', (_event, theme: 'system' | 'light' | 'dark') => {
    if (theme === 'system' || theme === 'light' || theme === 'dark') {
      nativeTheme.themeSource = theme
      syncNativeWindowTheme()
    }
  })

  ipcMain.handle('project:open-terminal', (_event, path: string) => openProjectTerminal(path))
  ipcMain.handle('project:git-overview', (_event, path: string) => getGitOverview(path))
  ipcMain.handle('project:git-history', (_event, path: string) => getGitHistory(path))

  ipcMain.handle('daemon:connect', () => daemon.connect())
  ipcMain.handle('daemon:disconnect', () => daemon.disconnect())
  ipcMain.handle('daemon:request', (_event, method: string, params?: Record<string, unknown>) =>
    daemon.request(method, params))

  daemon.on('event', (message) => mainWindow?.webContents.send('daemon:event', message))
  daemon.on('state', (state) => mainWindow?.webContents.send('daemon:state', state))
}

const hasSingleInstanceLock = app.requestSingleInstanceLock()
if (!hasSingleInstanceLock) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (!mainWindow) return
    if (mainWindow.isMinimized()) mainWindow.restore()
    mainWindow.show()
    mainWindow.focus()
  })

  app.whenReady().then(async () => {
    electronApp.setAppUserModelId('com.hoilai.seekclaw')
    app.on('browser-window-created', (_, window) => optimizer.watchWindowShortcuts(window))
    registerIpc()
    await ensureDaemonRunning().catch((error) => console.error('Unable to start SeekClaw Runtime:', error))
    createWindow()
    nativeTheme.on('updated', syncNativeWindowTheme)

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow()
    })
  })
}

app.on('before-quit', (event) => {
  if (!managedDaemon || runtimeShutdownStarted) {
    daemon.disconnect()
    return
  }
  event.preventDefault()
  runtimeShutdownStarted = true
  void stopManagedDaemon().finally(() => app.quit())
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit()
})
