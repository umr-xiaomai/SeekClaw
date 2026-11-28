export type WebServerHost = '0.0.0.0' | '127.0.0.1'

export interface WebServerConfig {
  enabled: boolean
  host: WebServerHost
  port: number
  token: string
  autoStart: boolean
}

export interface WebServerStatus {
  running: boolean
  config: WebServerConfig
  urls: string[]
  primaryUrl: string
  clientCount: number
  error?: string
}

export type ServerFilePickerKind = 'directory' | 'files' | 'images' | 'skills'

export interface ServerFsRoot {
  id: string
  label: string
  path: string
  kind: 'project' | 'home' | 'desktop' | 'documents' | 'drive'
}

export interface ServerFsEntry {
  name: string
  path: string
  isDirectory: boolean
  sizeBytes: number
  modifiedAt: number
  isImage?: boolean
}

export interface ServerFsListResult {
  path: string
  parentPath: string | null
  entries: ServerFsEntry[]
}

export interface RemoteBrowserFrame {
  scope: string
  tabId: string
  data: string
  width: number
  height: number
}

export type RemoteBrowserInput =
  | { type: 'mousePressed'; x: number; y: number; button: 'left' | 'right' | 'middle'; clickCount?: number }
  | { type: 'mouseReleased'; x: number; y: number; button: 'left' | 'right' | 'middle'; clickCount?: number }
  | { type: 'mouseMoved'; x: number; y: number; button?: 'none' | 'left' | 'right' | 'middle' }
  | { type: 'mouseWheel'; x: number; y: number; deltaX: number; deltaY: number }
  | { type: 'keyDown'; key: string; code: string; keyCode: number; text?: string; modifiers?: number }
  | { type: 'keyUp'; key: string; code: string; keyCode: number; modifiers?: number }
  | { type: 'insertText'; text: string }
