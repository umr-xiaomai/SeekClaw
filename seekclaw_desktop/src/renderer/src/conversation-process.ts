import type { ChatMessage, ConversationWidget, ToolActivity } from './types'

/** Presentation metadata already produced by the conversation view. */
export interface ConversationProcessItem {
  message: ChatMessage
  widgets: ConversationWidget[]
  showFooter: boolean
  isIntermediate?: boolean
}

export interface ConversationTurnProjection<T extends ConversationProcessItem = ConversationProcessItem> {
  /** Stable while streamed assistant messages are appended to this user turn. */
  id: string
  user: T | null
  process: T[]
  response: T | null
  running: boolean
}

export interface ConversationProcessOptions {
  running?: boolean
  assistantId?: string
}

function hasResponse(item: ConversationProcessItem, running: boolean): boolean {
  const message = item.message
  // Errors and result cards must stay reachable outside the collapsed history.
  if (message.errorMessage?.trim() || item.widgets.length > 0) return true
  if (!message.content.trim() && !message.images?.length && !message.files?.length && !message.viewedImages?.length) return false
  // A tool-bearing commentary message is history while its operation is still active.
  // Once all operations finish, a streamed answer remains visible as it grows.
  return !running || !(message.tools ?? []).some((tool) => tool.state === 'running')
}

/**
 * Groups the existing message projection without modifying messages, widgets, or
 * execution state. Each user bubble owns its following assistant steps. The last
 * visible answer remains outside the disclosure, including a growing answer.
 */
export function projectConversationTurns<T extends ConversationProcessItem>(
  items: readonly T[],
  options: ConversationProcessOptions = {}
): ConversationTurnProjection<T>[] {
  const turns: Array<{ id: string; user: T | null; assistants: T[] }> = []
  for (const item of items) {
    if (item.message.role === 'user') {
      turns.push({ id: `turn:${item.message.id}`, user: item, assistants: [] })
      continue
    }
    let turn = turns.at(-1)
    if (!turn) {
      turn = { id: `turn:${item.message.id}`, user: null, assistants: [] }
      turns.push(turn)
    }
    turn.assistants.push(item)
  }

  return turns.map((turn, index) => {
    // A stale assistant id can briefly survive the newly appended user bubble.
    // Only the latest user turn can own live work; never relabel its predecessor.
    const running = options.running === true && index === turns.length - 1 && (!options.assistantId
      || turn.assistants.length === 0
      || turn.assistants.some((item) => item.message.id === options.assistantId))
    const tail = turn.assistants.at(-1)
    const response = tail && hasResponse(tail, running) ? tail : null
    return {
      id: turn.id,
      user: turn.user,
      process: response ? turn.assistants.slice(0, -1) : turn.assistants,
      response,
      running
    }
  })
}

export interface ConversationProcessSummary {
  stepCount: number
  toolCount: number
  errorCount: number
  activeTool?: ToolActivity
  latestMessage?: ChatMessage
}

/** Keep summary counts independent of the disclosure's mounted DOM. */
export function summarizeConversationProcess(items: readonly ConversationProcessItem[]): ConversationProcessSummary {
  let toolCount = 0
  let errorCount = 0
  let activeTool: ToolActivity | undefined
  for (const item of items) {
    if (item.message.errorMessage?.trim()) errorCount++
    for (const tool of item.message.tools ?? []) {
      toolCount++
      if (tool.state === 'error') errorCount++
      if (tool.state === 'running') activeTool = tool
    }
  }
  return { stepCount: items.length, toolCount, errorCount, activeTool, latestMessage: items.at(-1)?.message }
}
