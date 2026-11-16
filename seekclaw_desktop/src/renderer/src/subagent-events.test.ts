import { ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import { createDaemonEventHandler, type DaemonEventContext } from './daemon-event-handler'
import type { ThreadItem } from './types'

function setup(readSubAgentStatus?: DaemonEventContext['readSubAgentStatus']) {
  const owner: ThreadItem = {
    id: 'owner', title: 'Parent', sessionId: 'parent-session', requestId: 10, assistantId: 'assistant', running: true, updatedAt: 0,
    messages: [{ id: 'assistant', role: 'assistant', content: '', state: 'thinking', createdAt: 0,
      tools: [{ id: 'spawn', name: 'subagent', input: '{"action":"spawn","role":"coder"}', state: 'running' }] }]
  }
  const unrelated: ThreadItem = { id: 'unrelated', title: 'Other', sessionId: 'other-session', messages: [], updatedAt: 0 }
  const finished = new Set<number>()
  const scroll = vi.fn().mockResolvedValue(undefined)
  const drain = vi.fn()
  const handler = createDaemonEventHandler({
    threads: ref([owner, unrelated]), activeThreadId: ref(owner.id), projects: ref([]),
    handleScheduleUpdated: vi.fn(), scrollToBottom: scroll,
    reloadThreadSession: vi.fn().mockResolvedValue(undefined), rememberFinishedRequest: vi.fn(),
    isFinishedRequest: (_thread, id) => finished.has(id), scheduleQueuedDrain: drain, reloadBackgroundThreadIfIdle: vi.fn(), readSubAgentStatus
  })
  return { owner, unrelated, finished, handler, scroll, drain }
}

describe('sub-agent lifecycle event routing', () => {
  it('shows a child while its parent is still thinking without ending the parent turn', () => {
    const { owner, handler, drain } = setup()
    handler({ id: 10, event: 'subagent_start', data: 'Polish the composer', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    handler({ id: 10, event: 'subagent_progress', data: 'Using edit_file', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder', detail: 'Composer.vue' } })
    expect(owner.messages[0]?.subAgents?.[0]).toMatchObject({ status: 'running', sourceToolId: 'spawn', progress: 'Using edit_file', detail: 'Composer.vue' })
    expect(owner.messages[0]?.state).toBe('thinking')
    expect(owner.running).toBe(true)
    expect(drain).not.toHaveBeenCalled()
  })

  it('routes an idle child completion broadcast using its parent session', () => {
    const { owner, unrelated, handler } = setup()
    const tool = owner.messages[0]!.tools![0]!
    tool.state = 'done'
    tool.output = '{"id":"child","role":"coder","status":"running"}'
    owner.running = false
    owner.assistantId = undefined
    owner.requestId = undefined
    handler({ id: 0, event: 'subagent.updated', data: 'child', details: { parentSessionId: 'parent-session', taskId: 'child', status: 'cancelled' } })
    expect(owner.messages[0]?.subAgents?.[0]).toMatchObject({ id: 'child', status: 'cancelled' })
    expect(unrelated.messages).toEqual([])
    handler({ id: 0, event: 'subagent.updated', data: 'child', details: { parentSessionId: 'other-session', taskId: 'child', status: 'failed' } })
    expect(owner.messages[0]?.subAgents?.[0]?.status).toBe('cancelled')
  })

  it('accepts completion for a known child after parent completion and ignores stale starts', () => {
    const { owner, finished, handler } = setup()
    handler({ id: 10, event: 'subagent_start', data: 'Task', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    finished.add(10)
    handler({ id: 10, event: 'subagent_done', data: 'Provider failed', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder', success: false } })
    handler({ id: 10, event: 'subagent_start', data: 'Late start', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'unknown', role: 'tester' } })
    expect(owner.messages[0]?.subAgents).toHaveLength(1)
    expect(owner.messages[0]?.subAgents?.[0]).toMatchObject({ status: 'failed', output: 'Provider failed' })
  })

  it('retains completed events in the later parent bubble without replaying an earlier launch', () => {
    const { owner, handler } = setup()
    const start = { id: 10, event: 'subagent_start' as const, data: 'Task', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } }
    handler(start)
    owner.messages.push({ id: 'next', role: 'assistant', content: 'Later response', state: 'streaming', createdAt: 1 })
    owner.assistantId = 'next'
    const done = { ...start, event: 'subagent_done' as const, data: 'Done', details: { subAgentId: 'child', role: 'coder', success: true } }
    handler(done)
    handler(done)
    handler({ id: 0, event: 'subagent.updated', data: 'child', details: { parentSessionId: 'parent-session', taskId: 'child', status: 'completed' } })
    expect(owner.messages[0]?.subAgentEvents?.map((event) => event.kind)).toEqual(['started'])
    expect(owner.messages[1]?.subAgentEvents?.map((event) => event.kind)).toEqual(['completed'])
    expect(owner.messages[0]?.subAgentEvents?.[0]?.agent.status).toBe('running')
    expect(owner.running).toBe(true)
  })

  it('updates only the previous instruction when an old completion arrives after a new instruction starts', () => {
    const { owner, finished, handler } = setup()
    handler({ id: 10, event: 'subagent_start', data: 'First', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    handler({ id: 10, event: 'subagent_done', data: 'First done', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', success: true } })
    finished.add(10)
    owner.requestId = 11
    owner.assistantId = 'next'
    owner.messages.push({ id: 'next', role: 'assistant', content: '', createdAt: 1, tools: [
      { id: 'follow-up', name: 'subagent', input: '{"action":"send","id":"child","role":"coder"}', state: 'running' }
    ] })
    handler({ id: 11, event: 'subagent_start', data: 'Follow up', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    handler({ id: 10, event: 'subagent_done', data: 'Late first done', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', success: true } })
    expect(owner.messages[1]?.subAgents?.[0]).toMatchObject({ status: 'running', task: 'Follow up', requestId: 11 })
    expect(owner.messages[1]?.subAgentEvents?.map((event) => event.kind)).toEqual(['started'])
  })

  it('verifies an old terminal broadcast against the current running send', async () => {
    const read = vi.fn().mockResolvedValue({ status: 'running' })
    const { owner, handler } = setup(read)
    handler({ id: 10, event: 'subagent_start', data: 'New instruction', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    handler({ id: 0, event: 'subagent.updated', data: 'child', details: { parentSessionId: 'parent-session', taskId: 'child', status: 'completed' } })
    await vi.waitFor(() => expect(read).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ sessionId: 'parent-session' }), 'child'))
    expect(owner.messages[0]?.subAgents?.[0]).toMatchObject({ status: 'running', live: true })
    expect(owner.messages[0]?.subAgentEvents?.map((event) => event.kind)).toEqual(['started'])
  })

  it('applies verified durable completion after the parent has ended', async () => {
    const read = vi.fn().mockResolvedValue({ status: 'completed', output: 'Actual child result', childSessionId: 'durable-child-session' })
    const { owner, finished, handler } = setup(read)
    handler({ id: 10, event: 'subagent_start', data: 'Task', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    owner.running = false
    owner.assistantId = undefined
    finished.add(10)
    handler({ id: 0, event: 'subagent.updated', data: 'child', details: { parentSessionId: 'parent-session', taskId: 'child', status: 'failed' } })
    await vi.waitFor(() => expect(owner.messages[0]?.subAgents?.[0]?.status).toBe('completed'))
    expect(owner.messages[0]?.subAgents?.[0]).toMatchObject({ output: 'Actual child result', childSessionId: 'durable-child-session', live: false })
    expect(owner.messages[0]?.subAgentEvents?.map((event) => event.kind)).toEqual(['started', 'completed'])
  })

  it('drops a verified result when another send starts while the status read is pending', async () => {
    let resolve: (value: { status: 'completed' }) => void = () => undefined
    const read = vi.fn(() => new Promise<{ status: 'completed' }>((done) => { resolve = done }))
    const { owner, handler } = setup(read)
    handler({ id: 10, event: 'subagent_start', data: 'First', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    handler({ id: 0, event: 'subagent.updated', data: 'child', details: { parentSessionId: 'parent-session', taskId: 'child', status: 'completed' } })
    handler({ id: 10, event: 'subagent_done', data: 'First done', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', success: true } })
    owner.requestId = 11
    owner.assistantId = 'next'
    owner.messages.push({ id: 'next', role: 'assistant', content: '', createdAt: 1, tools: [
      { id: 'send', name: 'subagent', input: '{"action":"send","id":"child","role":"coder"}', state: 'running' }
    ] })
    handler({ id: 11, event: 'subagent_start', data: 'Next', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } })
    resolve({ status: 'completed' })
    await Promise.resolve()
    expect(owner.messages[1]?.subAgents?.[0]).toMatchObject({ status: 'running', task: 'Next', requestId: 11 })
    expect(owner.messages[1]?.subAgentEvents?.map((event) => event.kind)).toEqual(['started'])
  })

  it('keeps live state if status verification fails or is unavailable', async () => {
    const read = vi.fn().mockRejectedValue(new Error('Disconnected'))
    const { owner, handler } = setup(read)
    const start = { id: 10, event: 'subagent_start' as const, data: 'Task', requestMethod: 'chat', sessionId: 'parent-session', details: { subAgentId: 'child', role: 'coder' } }
    const broadcast = { id: 0, event: 'subagent.updated' as const, data: 'child', details: { parentSessionId: 'parent-session', taskId: 'child', status: 'completed' } }
    handler(start)
    handler(broadcast)
    await Promise.resolve()
    expect(owner.messages[0]?.subAgents?.[0]?.status).toBe('running')
    const legacy = setup()
    legacy.handler(start)
    legacy.handler(broadcast)
    expect(legacy.owner.messages[0]?.subAgents?.[0]?.status).toBe('running')
  })
})
