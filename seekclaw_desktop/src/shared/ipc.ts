export type DaemonEventName =
  | 'pong'
  | 'delta'
  | 'thinking'
  | 'status'
  | 'tool_start'
  | 'tool_done'
  | 'result'
  | 'done'
  | 'cancelled'
  | 'error'
  | 'bye'

export interface DaemonMessage {
  id: number
  event: DaemonEventName
  data: string
  requestMethod?: string
}

export interface DaemonState {
  connected: boolean
  endpoint: string
  error?: string
}

export interface AppInfo {
  version: string
  platform: 'aix' | 'darwin' | 'freebsd' | 'linux' | 'openbsd' | 'sunos' | 'win32' | 'android'
  supportsMica: boolean
  defaultWorkspace: string
}

export type AppearanceTheme = 'system' | 'light' | 'dark'

export interface DesktopApi {
  getAppInfo(): Promise<AppInfo>
  selectWorkspace(): Promise<string | null>
  showItemInFolder(path: string): Promise<void>
  closeApp(): Promise<void>
  setTheme(theme: AppearanceTheme): Promise<void>
  daemon: {
    connect(): Promise<DaemonState>
    disconnect(): Promise<void>
    request(method: string, params?: Record<string, unknown>): Promise<DaemonMessage>
    onEvent(listener: (message: DaemonMessage) => void): () => void
    onState(listener: (state: DaemonState) => void): () => void
  }
}
