export type DaemonEventName =
  | 'pong'
  | 'delta'
  | 'thinking'
  | 'steer'
  | 'image_view'
  | 'status'
  | 'tool_start'
  | 'tool_done'
  | 'file_diff'
  | 'result'
  | 'done'
  | 'cancelled'
  | 'error'
  | 'bye'

export interface DaemonMessage {
  id: number
  event: DaemonEventName
  data: string
  /** Structured metadata for tool and file-diff events. */
  details?: Record<string, unknown>
  /** Session that owns a streamed agent event. Present for chat turn events. */
  sessionId?: string
  requestMethod?: string
}

export interface DaemonState {
  connected: boolean
  endpoint: string
  error?: string
}

/** Optional per-request controls passed from the renderer to the daemon client. */
export interface DaemonRequestOptions {
  /** Rejects the request when no event or terminal response arrives within this window. */
  timeoutMs?: number
}

export interface AppInfo {
  version: string
  platform: 'aix' | 'darwin' | 'freebsd' | 'linux' | 'openbsd' | 'sunos' | 'win32' | 'android'
  supportsMica: boolean
  defaultWorkspace: string
  /** Used only to migrate the legacy project that Desktop implicitly created here. */
  documentsPath: string
}

export type AppearanceTheme = 'system' | 'light' | 'dark'

export interface GitOverview {
  isRepository: boolean
  root: string
  branch: string
  status: string[]
  diff: string
  error?: string
}

export interface GitCommit {
  hash: string
  shortHash: string
  author: string
  authoredAt: string
  subject: string
}

export interface GitHistory {
  commits: GitCommit[]
  error?: string
}

export interface DesktopImageFile {
  name: string
  mediaType: string
  data: string
  sizeBytes: number
}

export interface DesktopImageSelection {
  images: DesktopImageFile[]
  warning?: string
}

export interface DesktopApi {
  getAppInfo(): Promise<AppInfo>
  selectWorkspace(): Promise<string | null>
  selectImages(): Promise<DesktopImageSelection>
  showItemInFolder(path: string): Promise<void>
  closeApp(): Promise<void>
  setTheme(theme: AppearanceTheme): Promise<void>
  project: {
    openTerminal(path: string): Promise<void>
    gitOverview(path: string): Promise<GitOverview>
    gitHistory(path: string): Promise<GitHistory>
  }
  daemon: {
    connect(): Promise<DaemonState>
    disconnect(): Promise<void>
    request(
      method: string,
      params?: Record<string, unknown>,
      options?: DaemonRequestOptions
    ): Promise<DaemonMessage>
    onEvent(listener: (message: DaemonMessage) => void): () => void
    onState(listener: (state: DaemonState) => void): () => void
  }
}
