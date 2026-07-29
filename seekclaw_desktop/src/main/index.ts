import { join, resolve } from 'node:path'
import { app, BrowserWindow, dialog, ipcMain, shell } from 'electron'
import { electronApp, is, optimizer } from '@electron-toolkit/utils'
import icon from '../../resources/logo.svg?asset'
import { DaemonClient } from './daemon-client.js'

const daemon = new DaemonClient()
let mainWindow: BrowserWindow | null = null

function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 760,
    minHeight: 620,
    show: false,
    backgroundColor: '#f7f7f5',
    title: 'SeekClaw',
    icon,
    titleBarStyle: 'hidden',
    titleBarOverlay: process.platform === 'darwin' ? false : {
      color: '#f2f2ed',
      symbolColor: '#343631',
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

  ipcMain.handle('daemon:connect', () => daemon.connect())
  ipcMain.handle('daemon:disconnect', () => daemon.disconnect())
  ipcMain.handle('daemon:request', (_event, method: string, params?: Record<string, unknown>) =>
    daemon.request(method, params))

  daemon.on('event', (message) => mainWindow?.webContents.send('daemon:event', message))
  daemon.on('state', (state) => mainWindow?.webContents.send('daemon:state', state))
}

app.whenReady().then(() => {
  electronApp.setAppUserModelId('com.hoilai.seekclaw')
  app.on('browser-window-created', (_, window) => optimizer.watchWindowShortcuts(window))
  registerIpc()
  createWindow()

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  daemon.disconnect()
  if (process.platform !== 'darwin') app.quit()
})
