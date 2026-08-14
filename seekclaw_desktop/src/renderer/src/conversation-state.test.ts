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

  it('finalizes a mid-turn steer bubble even when the captured assistant is the older one', () => {
    // A steer closes the current bubble and opens a fresh one; when the turn
    // settles the continuation only knows the old bubble, so the walk must
    // finalize both and not leave the fresh "..." placeholder on screen.
    const messages = [
      assistant({ id: 'captured', state: 'done', content: 'answer so far' }),
      assistant({ id: 'steer-bubble', state: 'streaming', content: 'steered answer' })
    ]
    finalizeAssistantBubbles(messages, 'done')
    expect(messages[1]!.state).toBe('done')
  })
})
