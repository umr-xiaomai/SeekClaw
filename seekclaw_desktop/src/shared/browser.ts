import type { RemoteBrowserFrame, RemoteBrowserInput } from './web-server.js'

export interface BrowserCommand {
  action: string
  tab_id?: string
  url?: string
  snapshot_id?: string
  element_id?: string
  text?: string
  key?: string
  x?: number
  y?: number
  delta_y?: number
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
  subscribeStream?(
    scope: string,
    viewport: { width: number; height: number },
    listener: (frame: RemoteBrowserFrame) => void
  ): () => void
  sendRemoteInput?(scope: string, input: RemoteBrowserInput): Promise<void>
}
