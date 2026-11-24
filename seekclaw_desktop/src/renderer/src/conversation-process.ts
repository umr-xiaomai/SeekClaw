import type { ChatMessage, ConversationWidget, ToolActivity } from './types'
import { isHiddenSystemUserMessage, isInternalUserMessage } from './internal-messages'
import { widgetsAtTurnTails } from './conversation-widgets'

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
  // A tool-bearing step remains in the progress stream even after its last call
  // settles. The next model invocation will create its own message for the answer.
  return !running || !message.tools?.length && !message.preparingTool
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
    if (isHiddenSystemUserMessage(item.message)) continue
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

/** Collect base64 image data URIs referenced across a conversation's messages. */
export function collectMessageImageSources(messages: readonly ChatMessage[]): Record<string, string> {
  const sources: Record<string, string> = {}
  for (const message of messages) {
    for (const image of message.images ?? []) {
      sources[image.id] = `data:${image.mediaType};base64,${image.data}`
    }
  }
  return sources
}

/**
 * Shared projection converting raw chat messages into intermediate/tail items with widgets.
 * Used identically by both the main conversation view and sub-agent side panel.
 */
export function projectConversationItems(
  rawMessages: readonly ChatMessage[],
  options: { running?: boolean; activeTurnStartId?: string } = {}
): ConversationProcessItem[] {
  const messages = rawMessages.filter((message) => !isHiddenSystemUserMessage(message))
  const isRunning = options.running === true
  const lastUserIndex = messages.findLastIndex((m) => m.role === 'user' && !isInternalUserMessage(m))
  const turnWidgets = widgetsAtTurnTails(messages, {
    running: isRunning,
    startMessageId: options.activeTurnStartId
  })

  return messages.map((message, index) => {
    let showFooter = false
    let isIntermediate = false
    if (message.role === 'assistant') {
      const inActiveRunningTurn = isRunning && (lastUserIndex < 0 || index >= lastUserIndex)
      let hasLaterAssistant = false
      for (let j = index + 1; j < messages.length; j++) {
        const next = messages[j]
        if (!next || next.role === 'user') break
        if (next.role === 'assistant') {
          hasLaterAssistant = true
          break
        }
      }
      if (hasLaterAssistant) {
        isIntermediate = true
      }
      if (!inActiveRunningTurn && (Boolean(message.content?.trim()) || Boolean(message.errorMessage?.trim()))) {
        showFooter = !hasLaterAssistant
      }
    }
    return { message, showFooter, isIntermediate, widgets: turnWidgets.get(message.id) ?? [] }
  })
}

export interface ConversationProjection {
  items: ConversationProcessItem[]
  turns: ConversationTurnProjection[]
  imageSources: Record<string, string>
}

/**
 * Unified turn projection for both the main chat window and sub-agent side panel,
 * ensuring intermediate work collapsing, widgets, and image sources stay identical.
 */
export function buildConversationProjection(
  messages: readonly ChatMessage[],
  options: { running?: boolean; assistantId?: string; activeTurnStartId?: string } = {}
): ConversationProjection {
  const items = projectConversationItems(messages, options)
  const turns = projectConversationTurns(items, {
    running: options.running,
    assistantId: options.assistantId
  })
  return {
    items,
    turns,
    imageSources: collectMessageImageSources(messages)
  }
}
