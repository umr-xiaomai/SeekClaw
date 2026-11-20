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
  input?: string
  output?: string
  filePath?: string
  diff?: string
  addedLines?: number
  removedLines?: number
  state: 'running' | 'done' | 'error'
  content?: Array<Record<string, unknown>>
  structuredContent?: unknown
  metadata?: Record<string, unknown>
  widgets?: ConversationWidget[]
}

/** Display state from the existing sub-agent lifecycle events and task records. */
export interface SubAgentActivity {
  id: string
  role: string
  name?: string
  status: 'starting' | 'running' | 'completed' | 'failed' | 'cancelled' | 'interrupted' | 'unknown'
  task?: string
  progress?: string
  detail?: string
  model?: string
  elapsedMs?: number
  output?: string
  sourceToolId?: string
  /** Real persisted child session, when supplied by the runtime. */
  childSessionId?: string
  workspace?: string
  /** UI identity for one instruction to a persistent child; never a child task id. */
  runId?: string
  /** Parent request that emitted this instruction's lifecycle. */
  requestId?: number
  /** Historical running snapshots establish that work started, not that it is still live. */
  live?: boolean
}

/** Immutable lifecycle entries retained where the parent received the event. */
export interface SubAgentLifecycleEvent {
  id: string
  agentId: string
  kind: 'started' | 'completed' | 'failed' | 'cancelled' | 'interrupted'
  agent: SubAgentActivity
  createdAt: number
  contentOffset: number
}

export interface ImageAttachment {
  id: string
  name: string
  mediaType: string
  data: string
  sizeBytes: number
  path?: string
}

export interface FileAttachment {
  id: string
  name: string
  path: string
  sizeBytes: number
  extension: string
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
  files?: FileAttachment[]
  thinking?: string
  /** "provider/model" that produced this assistant message. */
  modelRef?: string
  viewedImages?: ImageReference[]
  state?: 'thinking' | 'streaming' | 'done' | 'error'
  /** Transient runtime flag while thinking deltas are actively arriving on this step. */
  thinkingActive?: boolean
  /** Transient tool name while tool-call JSON arguments are streaming before tool_start fires. */
  preparingTool?: string
  tools?: ToolActivity[]
  subAgents?: SubAgentActivity[]
  subAgentEvents?: SubAgentLifecycleEvent[]
  createdAt: number
  errorMessage?: string
}

export interface ScheduledTaskInfo {
  id: string
  name: string
  workspace?: string
  prompt: string
  cron: string
  enabled: boolean
  sessionId?: string
  runInWorktree?: boolean
  notificationPolicy?: 'changes' | 'failures' | 'always'
  lastRunAt?: string
  nextRunAt?: string
  lastStatus?: string
  lastError?: string
  lastOutput?: string
  createdAt: string
  updatedAt: string
}

interface ConversationWidgetBase {
  version: 1
  id: string
  title: string
}

export interface ScheduleWidget extends ConversationWidgetBase {
  kind: 'schedule'
  task: ScheduledTaskInfo
  deleted?: boolean
}

export interface ArtifactWidget extends ConversationWidgetBase {
  kind: 'artifact'
  path: string
  mediaType: string
  description?: string
}

export type ConversationWidget = ScheduleWidget | ArtifactWidget

export interface QueuedMessage {
  id: string
  content: string
  images: ImageAttachment[]
  files?: FileAttachment[]
  createdAt: number
  errorMessage?: string
}

/** Per-session runtime statistics accumulated from daemon events. */
export interface ThreadStats {
  llmRounds?: number
  executionSteps?: number
  inputTokens?: number
  outputTokens?: number
  totalInputTokens?: number
  cachedInputTokens?: number
  outputElapsedMs?: number
}

export interface ThreadItem {
  id: string
  title: string
  titleSource?: 'pending' | 'input' | 'generated' | 'manual'
  titleGenerating?: boolean
  projectId?: string
  updatedAt: number
  messages: ChatMessage[]
  sessionId?: string
  sessionLoaded?: boolean
  executionWorkspace?: string
  worktree?: { path: string; status: string; baseCommit?: string }
  archived?: boolean
  pinned?: boolean
  /** True while this task has an agent turn running, independent of the selected task. */
  running?: boolean
  /** Local request id used to cancel this task without affecting other tasks. */
  requestId?: number
  /** Request ids whose terminal events have already been applied. Used to ignore
   * delayed events from a previous turn when the next queued turn has started. */
  finishedRequestIds?: number[]
  /** Local generation marker so an older request cannot clean up a newer turn. */
  activeTurnToken?: string
  /** First user bubble of the active turn, retained across mid-turn guidance. */
  activeTurnStartId?: string
  /** Current work phase shown in the header (thinking/tool/verify…). */
  phase?: string
  /** Custom high-level plan steps emitted explicitly by update_plan. */
  customPlan?: Array<{ id: string; step: number; title: string; detail?: string; state: 'running' | 'done' | 'error' | 'pending' }>
  /** Assistant placeholder receiving streamed output for the active turn. */
  assistantId?: string
  /** Per-task reasoning depth, independent from other concurrent tasks. */
  reasoningLevel?: ReasoningLevel
  modelRef?: string
  /** Per-task "联网" toggle; controls web_search + web_fetch together. */
  networkEnabled?: boolean
  /** Messages waiting for the current agent turn to finish. */
  queuedMessages?: QueuedMessage[]
  /** Prevents multiple queued messages from starting at the same time. */
  queueDraining?: boolean
  /** Locally displayed guidance messages waiting to be persisted by the Agent. */
  pendingGuidance?: number
  /** Live per-session model/token statistics, populated from daemon events. */
  stats?: ThreadStats
  /** Tracks the highest execution step seen in the current turn (workflow events). */
  turnStepHighWater?: number
}


