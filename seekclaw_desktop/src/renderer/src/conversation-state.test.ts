import { describe, expect, it } from 'vitest'
import { finalizeAssistantBubbles } from './conversation-state'
import type { ChatMessage } from './types'

function assistant(overrides: Partial<ChatMessage> = {}): ChatMessage {
  return { id: overrides.id ?? 'a', role: 'assistant', content: '', createdAt: 0, ...overrides }
}

describe('finalizeAssistantBubbles', () => {
  it('finalizes lingering thinking/streaming bubbles as done and leaves others untouched', () => {
    const messages = [
      assistant({ id: 'a', state: 'thinking' }),
      assistant({ id: 'b', state: 'streaming', content: 'done text' }),
      assistant({ id: 'c', state: 'done' }),
      { id: 'u', role: 'user' as const, content: 'hi', createdAt: 0 }
    ]
    finalizeAssistantBubbles(messages, 'done')
    expect(messages[0]!.state).toBe('done')
    expect(messages[1]!.state).toBe('done')
    expect(messages[2]!.state).toBe('done')
    expect(messages[3]!.state).toBeUndefined()
  })

  it('marks lingering bubbles as error for terminal error events', () => {
    const messages = [assistant({ state: 'streaming' }), assistant({ state: 'thinking' })]
    finalizeAssistantBubbles(messages, 'error')
    expect(messages[0]!.state).toBe('error')
    expect(messages[1]!.state).toBe('error')
  })
})
