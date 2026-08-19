import { ReasoningLevel } from './types'
import type {
  ChatMessage,
  ImageAttachment,
  ProjectItem,
  QueuedMessage,
  ThreadItem,
  ThreadStats,
  ToolActivity
} from './types'

// Vue reactive proxies cannot be structured-cloned by Electron IPC; copy the
// attachments into plain objects at the IPC boundary so requests reach the
// daemon instead of failing with a DataCloneError.
export function plainImages(images?: ImageAttachment[]): ImageAttachment[] {
  return (images ?? []).map((image) => ({
    id: image.id,
    name: image.name,
    mediaType: image.mediaType,
    data: image.data,
    sizeBytes: image.sizeBytes
  }))
}

export const makeId = (): string => crypto.randomUUID()
export const pathName = (path: string): string => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path
export const normalizePath = (path: string): string => path.replace(/\\/g, '/').replace(/\/$/, '').toLocaleLowerCase()
export const samePath = (left?: string, right?: string): boolean => Boolean(left && right && normalizePath(left) === normalizePath(right))

export interface RuntimeSessionHeader {
  id: string
  title?: string
  workspace?: string
  archived?: boolean
  createdAt: string
  updatedAt: string
  reasoningLevel?: string
  networkEnabled?: boolean
  llmRounds?: number
  executionSteps?: number
  inputTokens?: number
  totalInputTokens?: number
  cachedInputTokens?: number
  outputTokens?: number
  outputElapsedMs?: number
}

export interface RuntimeSession extends RuntimeSessionHeader {
  messages: Array<{
    role: 'user' | 'assistant' | 'tool'
    text: string
    images?: ImageAttachment[]
    thinking?: string
    modelRef?: string
    viewedImages?: Array<{ id: string; name: string }>
    toolCalls?: Array<{ id: string; name: string }>
    toolCallId?: string
    toolName?: string
    toolSuccess?: boolean
    toolDiff?: string
    toolFilePath?: string
  }>
}

export interface RuntimeWorkspace {
  path: string
  name: string
  mode: string
}

export interface RuntimeProject {
  id: string
  path: string
  name: string
  createdAt: string
  updatedAt: string
}

export interface RuntimeModelCatalogItem {
  ref: string
  active: boolean
  capabilities?: { vision?: boolean }
}

export function normalizeReasoningLevel(value?: string): ReasoningLevel {
  const normalized = value?.toLocaleLowerCase()
  return Object.values(ReasoningLevel).includes(normalized as ReasoningLevel)
    ? normalized as ReasoningLevel
    : ReasoningLevel.High
}

export function hydrateMessages(saved: RuntimeSession): ChatMessage[] {
  const messages: ChatMessage[] = []
  saved.messages.forEach((item, index) => {
    if (item.role === 'tool') {
      const assistant = messages.findLast((message) => message.role === 'assistant')
      if (assistant && item.toolName) {
        assistant.tools ??= []
        const tool = assistant.tools.find((activity) =>
          Boolean(item.toolCallId) && (activity.callId === item.toolCallId || activity.id === item.toolCallId))
        const hydrated: ToolActivity = tool ?? {
          id: item.toolCallId ?? `${saved.id}:tool:${index}`,
          callId: item.toolCallId,
          name: item.toolName,
          state: 'done'
        }
        hydrated.state = item.toolSuccess === false ? 'error' : 'done'
        hydrated.detail = item.text
        hydrated.filePath = item.toolFilePath
        hydrated.diff = item.toolDiff
        if (!tool) assistant.tools.push(hydrated)
      }
      return
    }
    messages.push({
      id: `${saved.id}:${index}`,
      role: item.role,
      content: item.text,
      images: item.images,
      thinking: item.thinking,
      modelRef: item.modelRef,
      viewedImages: item.viewedImages,
      tools: item.toolCalls?.map((call) => ({
        id: call.id,
        callId: call.id,
        name: call.name,
        state: 'done'
      })),
      state: item.role === 'assistant' ? 'done' : undefined,
      createdAt: new Date(saved.createdAt).getTime() + index
    })
  })
  return messages
}

export function sessionStats(saved: RuntimeSession): ThreadStats {
  const assistantCount = saved.messages.filter((item) => item.role === 'assistant').length
  return {
    llmRounds: saved.llmRounds || assistantCount,
    executionSteps: saved.executionSteps || assistantCount,
    inputTokens: saved.inputTokens ?? 0,
    totalInputTokens: saved.totalInputTokens ?? 0,
    cachedInputTokens: saved.cachedInputTokens ?? 0,
    outputTokens: saved.outputTokens ?? 0,
    outputElapsedMs: saved.outputElapsedMs ?? 0
  }
}

export function sessionScope(thread: ThreadItem, project?: ProjectItem): Record<string, unknown> {
  return thread.projectId && project ? { workspace: project.path } : { global: true }
}

export function messageMatches(message: ChatMessage, query: string): boolean {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  return message.content.toLocaleLowerCase().includes(normalized)
}

export function phaseLabel(status: string): string {
  const s = status.toLocaleLowerCase()
  if (s.includes('compacting')) return '压缩记忆'
  if (s.includes('verifying')) return '构建验证'
  if (s.includes('truncated')) return '自动续写'
  if (s.includes('thinking')) return '思考中'
  return status
}

export function queuedMessagePreview(message: QueuedMessage): string {
  const text = message.content.trim().replace(/\s+/g, ' ')
  if (text) return text.length > 120 ? `${text.slice(0, 120)}…` : text
  return message.images.length > 1 ? `发送 ${message.images.length} 张图片` : '发送图片'
}

export function queuedImageUrl(image?: ImageAttachment): string {
  return image ? `data:${image.mediaType};base64,${image.data}` : ''
}

export function updateThreadTitle(thread: ThreadItem, prompt: string): boolean {
  if (thread.title !== '新任务') return false
  thread.title = prompt.length > 42 ? `${prompt.slice(0, 42)}…` : prompt
  return true
}
