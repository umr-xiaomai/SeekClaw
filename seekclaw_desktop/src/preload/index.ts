import { contextBridge, ipcRenderer } from 'electron'
import type { DaemonMessage, DaemonState, DesktopApi } from '../shared/ipc.js'

const api: DesktopApi = {
  getAppInfo: () => ipcRenderer.invoke('app:info'),
  selectWorkspace: () => ipcRenderer.invoke('app:select-workspace'),
  selectImages: () => ipcRenderer.invoke('app:select-images'),
  selectSkillFiles: () => ipcRenderer.invoke('app:select-skill-files'),
  showItemInFolder: (path) => ipcRenderer.invoke('app:show-item', path),
  closeApp: () => ipcRenderer.invoke('app:close'),
  openDevTools: () => ipcRenderer.invoke('app:open-devtools'),
  setTheme: (theme) => ipcRenderer.invoke('app:set-theme', theme),
  notify: (title, body) => ipcRenderer.invoke('app:notify', title, body),
  project: {
    openTerminal: (path) => ipcRenderer.invoke('project:open-terminal', path),
    gitOverview: (path) => ipcRenderer.invoke('project:git-overview', path),
    gitHistory: (path) => ipcRenderer.invoke('project:git-history', path)
  },
  daemon: {
    connect: () => ipcRenderer.invoke('daemon:connect'),
    disconnect: () => ipcRenderer.invoke('daemon:disconnect'),
    request: (method, params, options) => ipcRenderer.invoke('daemon:request', method, params, options),
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
