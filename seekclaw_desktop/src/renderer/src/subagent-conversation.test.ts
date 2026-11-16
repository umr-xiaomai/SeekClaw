import { afterEach, describe, expect, it, vi } from 'vitest'
import type { DaemonMessage } from '../../shared/ipc'
import type { RuntimeSession } from './app-helpers'
import { createSubAgentConversationSource, emptySubAgentConversation, type SubAgentConversationState } from './subagent-conversation'

const saved = (id = 'child-session'): RuntimeSession => ({
  id, createdAt: '2026-11-16T00:00:00Z', updatedAt: '2026-11-16T00:00:00Z',
  messages: [{ role: 'user', text: 'Inspect the renderer' }]
})

function setup() {
  let state: SubAgentConversationState = emptySubAgentConversation()
  let listener: (event: DaemonMessage) => void = () => undefined
  let session = saved()
  let status = 'running'
  const unsubscribe = vi.fn()
  const request = vi.fn(async (method: string, parameters: Record<string, unknown>) => ({ data: JSON.stringify(method === 'subagent.status'
    ? { id: parameters.taskId, parent: parameters.sessionId, sessionId: parameters.taskId === 'other' ? 'other-session' : 'child-session', status }
    : session) }))
  const source = createSubAgentConversationSource({ request, onEvent: (handler) => { listener = handler; return unsubscribe } }, (next) => { state = next })
  const select = (taskId = 'child', workspace?: string) => source.select({ agent: { id: taskId, role: 'coder', status: 'running' }, parentSessionId: 'parent', workspace })
  const event = (kind: string, delta = '', overrides: Partial<DaemonMessage> = {}) => listener({
    id: 8, event: 'subagent_progress', data: 'Working', sessionId: 'parent',
    details: { subAgentId: 'child', childSessionId: 'child-session', kind, delta }, ...overrides
  })
  return { source, request, select, event, unsubscribe, state: () => state,
    save: (next: RuntimeSession) => { session = next }, status: (value: string) => { status = value } }
}

afterEach(() => vi.useRealTimers())

describe('read-only sub-agent conversation source', () => {
  it('loads actual child history with the parent global scope and rich tool output', async () => {
    const test = setup()
    const session = saved()
    session.messages.push({ role: 'assistant', text: 'Checking', modelRef: 'provider/model', toolCalls: [{ id: 'call', name: 'read_file', input: '{"path":"app.vue"}' }] },
      { role: 'tool', text: 'file contents', toolCallId: 'call', toolName: 'read_file', toolSuccess: true })
    test.save(session)
    await test.select()
    expect(test.request.mock.calls).toEqual([
      ['subagent.status', { global: true, sessionId: 'parent', taskId: 'child' }],
      ['session.get', { global: true, id: 'child-session' }]
    ])
    expect(test.state().messages[1]?.tools?.[0]).toMatchObject({ name: 'read_file', output: 'file contents', state: 'done' })
    expect(test.state().model).toBe('provider/model')
    test.source.dispose()
  })

  it('preserves the project scope instead of interpreting a child worktree as a project', async () => {
    const test = setup()
    await test.select('child', 'E:\\Project\\SeekClaw')
    expect(test.request.mock.calls[1]?.[1]).toEqual({ workspace: 'E:\\Project\\SeekClaw', id: 'child-session' })
    test.source.dispose()
  })

  it('ignores a previous child response after switching selection', async () => {
    const test = setup()
    let finishOld: (value: { data: string }) => void = () => undefined
    test.request.mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve }))
    const old = test.select()
    test.save(saved('other-session'))
    await test.select('other')
    finishOld({ data: JSON.stringify({ id: 'child', parent: 'parent', sessionId: 'child-session', status: 'completed' }) })
    await old
    expect(test.state().session?.id).toBe('other-session')
    expect(test.request.mock.calls.filter(([method]) => method === 'session.get')).toHaveLength(1)
    test.source.dispose()
  })

  it('streams child tokens immediately, resets retries, and replaces persisted output without duplicates', async () => {
    vi.useFakeTimers()
    const test = setup()
    await test.select()
    test.event('model', 'provider/first')
    test.event('thinking', 'Look at the code')
    test.event('text', 'Discarded partial')
    test.event('stream_reset', 'provider/fallback')
    test.event('text', 'Actual ')
    test.event('text', 'answer')
    test.event('text', 'unrelated', { sessionId: 'another-parent' })
    expect(test.state().messages).toHaveLength(2)
    expect(test.state().messages[1]).toMatchObject({ content: 'Actual answer', modelRef: 'provider/fallback', state: 'streaming' })
    expect(test.request).toHaveBeenCalledTimes(2)
    test.event('message_done', 'Actual answer')
    const session = saved()
    session.messages.push({ role: 'assistant', text: 'Actual answer', modelRef: 'provider/fallback' })
    test.save(session)
    await vi.advanceTimersByTimeAsync(180)
    expect(test.state().messages).toHaveLength(2)
    expect(test.state().messages[1]?.id).toBe('child-session:1')
    expect(test.state().model).toBe('provider/fallback')
    test.source.dispose()
  })

  it('coalesces lifecycle refreshes and disposes the event listener and pending timer', async () => {
    vi.useFakeTimers()
    const test = setup()
    await test.select()
    for (let i = 0; i < 15; i++) test.event('tool_done')
    await vi.advanceTimersByTimeAsync(180)
    expect(test.request).toHaveBeenCalledTimes(4)
    test.event('tool_done')
    test.source.dispose()
    await vi.advanceTimersByTimeAsync(500)
    expect(test.unsubscribe).toHaveBeenCalledOnce()
    expect(test.request).toHaveBeenCalledTimes(4)
  })

  it('rejects mismatched records and exposes an error without fabricating a transcript', async () => {
    const test = setup()
    test.request.mockResolvedValueOnce({ data: JSON.stringify({ id: 'child', parent: 'unrelated-parent', sessionId: 'foreign-session', status: 'running' }) })
    await test.select()
    expect(test.state().error).toContain('不匹配')
    expect(test.state().messages).toEqual([])
    expect(test.request).toHaveBeenCalledTimes(1)
    test.source.dispose()
  })

  it('keeps existing identical history distinct from a new live response', async () => {
    const test = setup()
    const session = saved()
    session.messages.push({ role: 'assistant', text: 'Same answer' })
    test.save(session)
    await test.select()
    test.event('text', 'Same answer')
    await test.source.refresh()
    expect(test.state().messages.filter((message) => message.content === 'Same answer')).toHaveLength(2)
    test.status('cancelled')
    await test.source.refresh()
    expect(test.state().task?.status).toBe('cancelled')
    expect(test.state().messages.filter((message) => message.content === 'Same answer')).toHaveLength(1)
    test.source.dispose()
  })

  it('does not consume two pending responses with the same single saved message', async () => {
    const test = setup()
    await test.select()
    test.event('model', 'provider/model')
    test.event('text', 'Repeated answer')
    test.event('message_done', 'Repeated answer')
    test.event('model', 'provider/model')
    test.event('text', 'Repeated answer')
    const session = saved()
    session.messages.push({ role: 'assistant', text: 'Repeated answer' })
    test.save(session)
    await test.source.refresh()
    expect(test.state().messages.filter((message) => message.content === 'Repeated answer')).toHaveLength(2)
    expect(test.state().messages.at(-1)?.state).toBe('streaming')
    test.source.dispose()
  })

  it('rejects late tokens from the previous instruction while retaining broadcast refreshes', async () => {
    vi.useFakeTimers()
    const test = setup()
    await test.source.select({ agent: { id: 'child', role: 'coder', status: 'running', requestId: 8, runId: 'run-8' }, parentSessionId: 'parent' })
    test.event('text', 'Old instruction')
    expect(test.state().messages.at(-1)?.content).toBe('Old instruction')
    await test.source.select({ agent: { id: 'child', role: 'coder', status: 'running', requestId: 9, runId: 'run-9' }, parentSessionId: 'parent' })
    test.event('text', 'Late old output')
    test.event('', '', { event: 'subagent_done', data: 'Old completion' })
    expect(test.state().messages).toHaveLength(1)
    test.event('text', 'New instruction', { id: 9 })
    expect(test.state().messages.at(-1)).toMatchObject({ content: 'New instruction', state: 'streaming' })
    test.event('', '', { id: 0, event: 'subagent.updated', data: 'child', sessionId: undefined,
      details: { parentSessionId: 'parent', taskId: 'child', status: 'running' } })
    await vi.advanceTimersByTimeAsync(180)
    expect(test.request).toHaveBeenCalledTimes(6)
    expect(test.state().messages.at(-1)?.content).toBe('New instruction')
    test.source.dispose()
  })
})
