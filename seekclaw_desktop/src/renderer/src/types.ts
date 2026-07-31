export interface ProjectItem {
  id: string
  name: string
  path: string
  loaded?: boolean
}

/** Provider-neutral reasoning depth. Runtime wire clients perform API-specific mapping. */
export enum ReasoningLevel {
  None = 'none',
  Low = 'low',
  Medium = 'medium',
  High = 'high',
  Max = 'max',
  XHigh = 'xhigh',
  Ultra = 'ultra'
}

export interface ToolActivity {
  id: string
  callId?: string
  name: string
  detail?: string
  filePath?: string
  diff?: string
  addedLines?: number
  removedLines?: number
  state: 'running' | 'done' | 'error'
}

export interface ImageAttachment {
  id: string
  name: string
  mediaType: string
  data: string
  sizeBytes: number
}

export interface ImageReference {
  id: string
  name: string
}

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  images?: ImageAttachment[]
  thinking?: string
  viewedImages?: ImageReference[]
  state?: 'thinking' | 'streaming' | 'done' | 'error'
  tools?: ToolActivity[]
  createdAt: number
}

export interface ThreadItem {
  id: string
  title: string
  projectId?: string
  updatedAt: number
  messages: ChatMessage[]
  sessionId?: string
  sessionLoaded?: boolean
  archived?: boolean
  /** True while this task has an agent turn running, independent of the selected task. */
  running?: boolean
  /** Local request id used to cancel this task without affecting other tasks. */
  requestId?: number
  /** Assistant placeholder receiving streamed output for the active turn. */
  assistantId?: string
  /** Per-task reasoning depth, independent from other concurrent tasks. */
  reasoningLevel?: ReasoningLevel
}
