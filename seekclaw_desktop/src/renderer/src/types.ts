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
  /** "provider/model" that produced this assistant message. */
  modelRef?: string
  viewedImages?: ImageReference[]
  state?: 'thinking' | 'streaming' | 'done' | 'error'
  tools?: ToolActivity[]
  createdAt: number
}

export interface PanelReviewItem {
  ref: string
  status: 'reviewing' | 'passed' | 'issues' | 'failed'
  issueCount?: number
  summary?: string
}

export type WorkflowKind = 'start' | 'think' | 'tool' | 'verify' | 'repair' | 'compact' | 'review' | 'done' | 'error'

export interface WorkflowNode {
  id: string
  step: number
  kind: WorkflowKind
  label: string
  detail?: string
  state: 'running' | 'done' | 'error'
}

export interface WorkflowState {
  nodes: WorkflowNode[]
  activeId: string | null
}

export interface PanelReviewState {
  round?: number
  running: boolean
  reviews: PanelReviewItem[]
}

export interface QueuedMessage {
  id: string
  content: string
  images: ImageAttachment[]
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
  /** Request ids whose terminal events have already been applied. Used to ignore
   * delayed events from a previous turn when the next queued turn has started. */
  finishedRequestIds?: number[]
  /** Local generation marker so an older request cannot clean up a newer turn. */
  activeTurnToken?: string
  /** Current work phase shown in the header (thinking/tool/verify/review…). */
  phase?: string
  /** Live execution flowchart fed by daemon workflow events. */
  workflow?: WorkflowState
  /** Assistant placeholder receiving streamed output for the active turn. */
  assistantId?: string
  /** Per-task reasoning depth, independent from other concurrent tasks. */
  reasoningLevel?: ReasoningLevel
  /** Per-task "联网" toggle; controls web_search + web_fetch together. */
  networkEnabled?: boolean
  /** Per-task "评审团" toggle; cross-vendor adversarial review runs after each turn. */
  panelEnabled?: boolean
  /** Per-task review panel models ("provider/model"); undefined/empty = auto-pick. */
  panelModels?: string[]
  /** Active panel review state, fed by daemon panel events. */
  panel?: PanelReviewState
  /** Messages waiting for the current agent turn to finish. */
  queuedMessages?: QueuedMessage[]
  /** Prevents multiple queued messages from starting at the same time. */
  queueDraining?: boolean
  /** Locally displayed guidance messages waiting to be persisted by the Agent. */
  pendingGuidance?: number
}
