import type { RemoteBrowserFrame, RemoteBrowserInput } from './web-server.js'

export interface BrowserCommand {
  action: string
  tab_id?: string
  url?: string
  snapshot_id?: string
  element_id?: string
  target_element_id?: string
  text?: string
  value?: string
  key?: string
  modifiers?: string[]
  button?: 'left' | 'right' | 'middle'
  click_count?: number
  x?: number
  y?: number
  to_x?: number
  to_y?: number
  steps?: number
  delta_y?: number
  script?: string
  level?: string
  filter?: string
  status_filter?: 'all' | 'error'
  clear?: boolean
  limit?: number
  permission?: string
  reason?: string
  filename?: string
}

export interface BrowserApprovalRequest {
  id: string
  kind: 'permission' | 'download'
  scope: string
  sessionId: string
  origin: string
  url: string
  permission?: string
  permissionLabel?: string
  filename?: string
  totalBytes?: number
  savePath?: string
  reason?: string
  allowRememberSession?: boolean
}

export interface BrowserApprovalDecision {
  id: string
  approved: boolean
  rememberSession?: boolean
}

export interface BrowserTabState {
  id: string
  title: string
  url: string
  loading: boolean
  canGoBack: boolean
  canGoForward: boolean
  error?: string
}

export interface ConversationBrowserState {
  scope: string
  sessionId: string
  tabs: BrowserTabState[]
  activeTabId?: string
  paused: boolean
  busy: boolean
}

export interface BrowserPanelLayout {
  scope: string
  bounds: { x: number; y: number; width: number; height: number }
  visible: boolean
}

export interface ConversationBrowserApi {
  open(params: { sessionId: string; workspace?: string; global?: boolean }): Promise<ConversationBrowserState>
  state(sessionId: string): Promise<ConversationBrowserState | null>
  command(scope: string, command: BrowserCommand): Promise<ConversationBrowserState>
  pause(scope: string, paused: boolean): Promise<ConversationBrowserState>
  layout(layout: BrowserPanelLayout | null): Promise<void>
  overlay(visible: boolean): Promise<void>
  onChanged(listener: (state: ConversationBrowserState) => void): () => void
  onApprovalRequest?(listener: (req: BrowserApprovalRequest) => void): () => void
  decideApproval?(decision: BrowserApprovalDecision): Promise<void>
  subscribeStream?(
    scope: string,
    viewport: { width: number; height: number },
    listener: (frame: RemoteBrowserFrame) => void
  ): () => void
  sendRemoteInput?(scope: string, input: RemoteBrowserInput): Promise<void>
}
