import { contextBridge, ipcRenderer, webUtils, webFrame } from 'electron'
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
  openLink: (href, workspace) => ipcRenderer.invoke('app:open-link', href, workspace),
  readLinkPreview: (href, workspace) => ipcRenderer.invoke('app:read-link-preview', href, workspace),
  closeApp: () => ipcRenderer.invoke('app:close'),
  exitApp: () => ipcRenderer.invoke('app:exit'),
  openDevTools: () => ipcRenderer.invoke('app:open-devtools'),
  setTheme: (theme) => ipcRenderer.invoke('app:set-theme', theme),
  setZoomFactor: (factor: number) => {
    try {
      webFrame.setZoomFactor(factor)
    } catch {
      // ignore
    }
    void ipcRenderer.invoke('app:set-zoom-factor', factor)
  },
  getZoomFactor: () => {
    try {
      return webFrame.getZoomFactor()
    } catch {
      return 1.0
    }
  },
  notify: (title, body) => ipcRenderer.invoke('app:notify', title, body),
  project: {
    openTerminal: (path) => ipcRenderer.invoke('project:open-terminal', path),
    gitOverview: (path) => ipcRenderer.invoke('project:git-overview', path),
    gitHistory: (path) => ipcRenderer.invoke('project:git-history', path),
    gitBranches: (path) => ipcRenderer.invoke('project:git-branches', path),
    checkoutBranch: (path, branch) => ipcRenderer.invoke('project:checkout-branch', path, branch),
    createBranch: (path, branch) => ipcRenderer.invoke('project:create-branch', path, branch),
    revertFileDiffs: (workspace, patches) => ipcRenderer.invoke('project:revert-file-diffs', workspace, patches),
    gitCommitDiff: (path, hash) => ipcRenderer.invoke('project:git-commit-diff', path, hash),
    listFiles: (path, query) => ipcRenderer.invoke('project:list-files', path, query)
  },
  browser: {
    open: params => ipcRenderer.invoke('browser:open', toIpcPayload(params)),
    state: id => ipcRenderer.invoke('browser:state', id),
    command: (scope, command) => ipcRenderer.invoke('browser:command', scope, toIpcPayload(command)),
    pause: (scope, paused) => ipcRenderer.invoke('browser:pause', scope, paused),
    layout: layout => ipcRenderer.invoke('browser:layout', layout === null ? null : toIpcPayload(layout)),
    overlay: visible => ipcRenderer.invoke('browser:overlay', visible),
    onChanged: listener => {
      const handler = (_event: Electron.IpcRendererEvent, state: import('../shared/browser.js').ConversationBrowserState) => listener(state)
      ipcRenderer.on('browser:changed', handler)
      return () => ipcRenderer.removeListener('browser:changed', handler)
    }
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
  },
  webServer: {
    status: () => ipcRenderer.invoke('web-server:status'),
    update: (patch) => ipcRenderer.invoke('web-server:update', patch),
    regenerateToken: () => ipcRenderer.invoke('web-server:regenerate-token'),
    disconnectClients: () => ipcRenderer.invoke('web-server:disconnect-clients'),
    onStatus: (listener) => {
      const handler = (_event: Electron.IpcRendererEvent, status: import('../shared/web-server.js').WebServerStatus): void =>
        listener(status)
      ipcRenderer.on('web-server:status', handler)
      return () => ipcRenderer.removeListener('web-server:status', handler)
    }
  },
  onSyncRefresh: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, reason?: string): void => listener(reason)
    ipcRenderer.on('sync:refresh', handler)
    return () => ipcRenderer.removeListener('sync:refresh', handler)
  },
  serverFs: {
    roots: () => ipcRenderer.invoke('server-fs:roots'),
    list: (params) => ipcRenderer.invoke('server-fs:list', params),
    mkdir: (parentPath, name) => ipcRenderer.invoke('server-fs:mkdir', parentPath, name),
    readImages: (paths) => ipcRenderer.invoke('server-fs:read-images', paths),
    uploadFile: (params) => ipcRenderer.invoke('server-fs:upload', params)
  }
}

contextBridge.exposeInMainWorld('seekclaw', api)
