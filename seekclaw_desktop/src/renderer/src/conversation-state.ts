import type { ChatMessage } from './types'

export type TerminalKind = 'done' | 'cancelled' | 'error'

/**
 * After a turn's terminal event no assistant bubble may remain in a generating state.
 * The terminal handler normally finalizes the bubble pointed to by thread.assistantId,
 * but that pointer can miss the real bubble (a steer created a fresh bubble, or the
 * message list was replaced while a request was in flight), which left the "..." dots
 * on screen forever even though the turn had already ended. This walks every message
 * and finalizes any bubble that is still thinking/streaming.
 */
export function finalizeAssistantBubbles(
  messages: ChatMessage[],
  kind: TerminalKind
): void {
  const terminalState = kind === 'error' ? 'error' : 'done'
  for (const item of messages) {
    if (item.role !== 'assistant') continue
    if (item.state !== 'thinking' && item.state !== 'streaming') continue
    item.state = terminalState
  }
}
