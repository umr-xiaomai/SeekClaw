import { ReasoningLevel } from './types'
import type {
  ChatMessage,
  FileAttachment,
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
    sizeBytes: image.sizeBytes,
    path: image.path
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
    timestamp?: number
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

export function formatPromptWithFiles(content: string, files?: FileAttachment[]): string {
  if (!files || files.length === 0) return content

  const fileLines = files
    .map((file) => `- 文件名: "${file.name}" | 绝对路径: "${file.path}"`)
    .join('\n')

  const header = `[用户已附加以下本地文件。请直接根据任务需求调用相应的工具（如 read_file、bash 等）读取和处理这些文件，无需让用户手动提供文件路径]：\n${fileLines}`

  if (!content.trim()) {
    return `${header}\n\n请直接查看、分析并处理以上附加的文件。`
  }

  return `${header}\n\n用户指示：\n${content.trim()}`
}

export function parseAttachmentsFromText(rawText: string): { content: string; files: FileAttachment[] } {
  if (!rawText || !rawText.startsWith('[用户已附加以下本地文件')) {
    return { content: rawText, files: [] }
  }

  const files: FileAttachment[] = []
  const fileRegex = /- 文件名: "([^"]+)" \| 绝对路径: "([^"]+)"/g
  let match: RegExpExecArray | null
  while ((match = fileRegex.exec(rawText)) !== null) {
    const name = match[1] ?? ''
    const path = match[2] ?? ''
    if (!name || !path) continue
    const dot = name.lastIndexOf('.')
    const extension = dot >= 0 ? name.slice(dot + 1).toLowerCase() : ''
    files.push({
      id: `${name}:${path}`,
      name,
      path,
      sizeBytes: 0,
      extension
    })
  }

  const marker = '\n\n用户指示：\n'
  const defaultMarker = '\n\n请直接查看、分析并处理以上附加的文件。'
  let content = ''
  if (rawText.includes(marker)) {
    content = rawText.slice(rawText.indexOf(marker) + marker.length).trim()
  } else if (!rawText.includes(defaultMarker)) {
    content = ''
  }

  return { content, files }
}

export function getFileExtension(name: string): string {
  const dot = name.lastIndexOf('.')
  if (dot < 0 || dot === name.length - 1) return ''
  return name.slice(dot + 1).toLowerCase()
}

export function fileBadgeText(ext?: string): string {
  if (!ext) return 'FILE'
  const upper = ext.toUpperCase()
  if (upper.length <= 4) return upper
  return upper.slice(0, 3)
}

export function fileExtClass(ext?: string): string {
  const e = (ext || '').toLowerCase()
  if (e === 'pdf') return 'badge-pdf'
  if (['ts', 'js', 'py', 'cs', 'cpp', 'c', 'go', 'rs', 'java', 'vue', 'json', 'html', 'css', 'sql', 'sh', 'bat', 'ps1', 'dart'].includes(e)) return 'badge-code'
  if (['doc', 'docx', 'txt', 'md', 'rtf', 'odt'].includes(e)) return 'badge-doc'
  if (['xls', 'xlsx', 'csv', 'tsv'].includes(e)) return 'badge-sheet'
  if (['ppt', 'pptx'].includes(e)) return 'badge-slide'
  if (['zip', 'rar', '7z', 'tar', 'gz'].includes(e)) return 'badge-archive'
  if (['png', 'jpg', 'jpeg', 'webp', 'gif', 'svg', 'bmp', 'ico'].includes(e)) return 'badge-image'
  return 'badge-default'
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

    let messageContent = item.text
    let messageFiles: FileAttachment[] | undefined
    if (item.role === 'user' && item.text) {
      const parsed = parseAttachmentsFromText(item.text)
      messageContent = parsed.content
      if (parsed.files.length > 0) messageFiles = parsed.files
    }

    messages.push({
      id: `${saved.id}:${index}`,
      role: item.role,
      content: messageContent,
      images: item.images,
      files: messageFiles,
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
      createdAt: typeof item.timestamp === 'number' && item.timestamp > 0
        ? item.timestamp
        : new Date(saved.createdAt).getTime() + index
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
  if (message.files?.length) {
    return `发送 ${message.files.length} 个附件文件`
  }
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

export function formatTokenCount(tokens?: number): string {
  const count = tokens || 1000000
  if (count >= 1_000_000) {
    const inMillions = count / 1_000_000
    const formatted = inMillions % 1 === 0 ? inMillions.toString() : inMillions.toFixed(2).replace(/\.?0+$/, '')
    return `${formatted}M`
  }
  if (count >= 1000) {
    const inThousands = count / 1000
    const formatted = inThousands % 1 === 0 ? inThousands.toString() : Math.round(inThousands).toString()
    return `${formatted}k`
  }
  return count.toString()
}

export function formatMessageTime(timestamp?: number): string {
  if (!timestamp) return ''
  const date = new Date(timestamp)
  if (isNaN(date.getTime())) return ''
  const month = date.getMonth() + 1
  const day = date.getDate()
  const hours = date.getHours()
  const minutes = date.getMinutes().toString().padStart(2, '0')
  return `${month}月${day}日 ${hours}:${minutes}`
}
