import { contextBridge, ipcRenderer, webUtils } from 'electron'
import { toIpcPayload } from '../shared/ipc.js'
import type { DaemonMessage, DaemonState, DesktopApi } from '../shared/ipc.js'

const api: DesktopApi = {
  getAppInfo: () => ipcRenderer.invoke('app:info'),
  selectWorkspace: () => ipcRenderer.invoke('app:select-workspace'),
  selectImages: () => ipcRenderer.invoke('app:select-images'),
  selectFiles: () => ipcRenderer.invoke('app:select-files'),
  readFileBase64: (path: string) => ipcRenderer.invoke('app:read-file-base64', path),
  getPathForFile: (file: File) => {
    try {
      return webUtils.getPathForFile(file)
    } catch {
      return (file as unknown as { path?: string }).path || ''
    }
  },
  selectSkillFiles: () => ipcRenderer.invoke('app:select-skill-files'),
  showItemInFolder: (path) => ipcRenderer.invoke('app:show-item', path),
  closeApp: () => ipcRenderer.invoke('app:close'),
  openDevTools: () => ipcRenderer.invoke('app:open-devtools'),
  setTheme: (theme) => ipcRenderer.invoke('app:set-theme', theme),
  notify: (title, body) => ipcRenderer.invoke('app:notify', title, body),
  project: {
    openTerminal: (path) => ipcRenderer.invoke('project:open-terminal', path),
    gitOverview: (path) => ipcRenderer.invoke('project:git-overview', path),
    gitHistory: (path) => ipcRenderer.invoke('project:git-history', path),
    revertFileDiffs: (workspace, patches) => ipcRenderer.invoke('project:revert-file-diffs', workspace, patches)
  },
  daemon: {
    connect: () => ipcRenderer.invoke('daemon:connect'),
    disconnect: () => ipcRenderer.invoke('daemon:disconnect'),
    request: (method, params, options) => ipcRenderer.invoke(
      'daemon:request',
      method,
      params === undefined ? undefined : toIpcPayload(params),
      options === undefined ? undefined : toIpcPayload(options)
    ),
    onEvent: (listener) => {
      const handler = (_event: Electron.IpcRendererEvent, message: DaemonMessage): void => listener(message)
      ipcRenderer.on('daemon:event', handler)
      return () => ipcRenderer.removeListener('daemon:event', handler)
    },
    onState: (listener) => {
      const handler = (_event: Electron.IpcRendererEvent, state: DaemonState): void => listener(state)
      ipcRenderer.on('daemon:state', handler)
      return () => ipcRenderer.removeListener('daemon:state', handler)
    }
  }
}

contextBridge.exposeInMainWorld('seekclaw', api)
