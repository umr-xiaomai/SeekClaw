import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import type { ChatMessage, SubAgentLifecycleEvent, ToolActivity } from './types'
import { applySubAgentEvent, messageSubAgents, subAgentsFromTool } from './subagent-activity'
import SubAgentActivityList from './components/SubAgentActivityList.vue'
import ToolCallRow from './components/ToolCallRow.vue'
import { hydrateMessages, mergeSubAgentActivities } from './app-helpers'

const spawn = (id = 'spawn'): ToolActivity => ({ id, name: 'subagent', input: JSON.stringify({ action: 'spawn', role: 'coder', task: 'Improve the composer' }), state: 'running' })
const message = (): ChatMessage => ({ id: 'm', role: 'assistant', content: '', createdAt: 0, tools: [spawn()] })

describe('sub-agent conversation activity', () => {
  it('shows launch, live progress, and completion before the original tool finishes', () => {
    const current = message()
    applySubAgentEvent([current], { id: 1, event: 'subagent_start', data: 'Improve the composer', details: { subAgentId: 'child', role: 'coder' } }, current)
    expect(messageSubAgents(current)).toHaveLength(1)
    expect(current.subAgents?.[0]).toMatchObject({ id: 'child', sourceToolId: 'spawn', task: 'Improve the composer', live: true, status: 'running' })
    const next: ChatMessage = { id: 'next', role: 'assistant', content: '', createdAt: 1 }
    applySubAgentEvent([current, next], { id: 1, event: 'subagent_progress', data: 'Using edit_file', details: { subAgentId: 'child', role: 'coder', detail: 'Composer.vue' } }, next)
    expect(current.subAgents?.[0]).toMatchObject({ progress: 'Using edit_file', detail: 'Composer.vue' })
    expect(next.subAgents).toBeUndefined()
    applySubAgentEvent([current, next], { id: 1, event: 'subagent_done', data: 'Completed in 5.0s', details: { subAgentId: 'child', success: true, elapsedMs: 5000 } }, next)
    expect(current.subAgents?.[0]).toMatchObject({ status: 'completed', live: false, elapsedMs: 5000 })
    applySubAgentEvent([current, next], { id: 1, event: 'subagent_progress', data: 'late progress', details: { subAgentId: 'child' } }, next)
    expect(current.subAgents?.[0]?.status).toBe('completed')
  })

  it('recovers durable spawn output and applies a later completion broadcast', () => {
    const current = message()
    current.tools![0]!.state = 'done'
    current.tools![0]!.output = JSON.stringify({ id: 'child', role: 'coder', status: 'running' })
    expect(messageSubAgents(current)[0]).toMatchObject({ status: 'running', live: false })
    expect(applySubAgentEvent([current], { id: 0, event: 'subagent.updated', data: 'child', details: { taskId: 'child', status: 'failed' } }, undefined, false)).toBe(true)
    expect(messageSubAgents(current)).toHaveLength(1)
    expect(messageSubAgents(current)[0]?.status).toBe('failed')
  })

  it('associates parallel launches once and keeps lifecycle events ahead of later status snapshots', () => {
    const current = message()
    current.tools!.push(spawn('second'))
    applySubAgentEvent([current], { id: 1, event: 'subagent_start', data: 'One', details: { subAgentId: 'one', role: 'coder' } }, current)
    applySubAgentEvent([current], { id: 1, event: 'subagent_start', data: 'Two', details: { subAgentId: 'two', role: 'coder' } }, current)
    expect(new Set(current.subAgents?.map((agent) => agent.sourceToolId)).size).toBe(2)
    expect(messageSubAgents(current)).toHaveLength(2)
    const later: ChatMessage = { id: 'later', role: 'assistant', content: '', createdAt: 1, tools: [
      { id: 'status', name: 'subagent', state: 'done', input: '{"action":"status","id":"one"}', output: '{"id":"one","role":"coder","status":"running"}' }
    ] }
    applySubAgentEvent([current, later], { id: 1, event: 'subagent_done', data: 'Complete', details: { subAgentId: 'one', success: true } }, later)
    expect(current.subAgents?.find((agent) => agent.id === 'one')?.status).toBe('completed')
    expect(later.subAgents).toBeUndefined()
  })

  it('preserves every child from list results and never uses a malformed result as a role', () => {
    const tool: ToolActivity = { id: 'list', name: 'subagent', input: '{"action":"list"}', output: '[{"id":"one","role":"coder","status":"completed"},{"id":"two","role":"tester","status":"failed"}]', state: 'done' }
    const current = message()
    current.tools = [tool]
    expect(messageSubAgents(current).map((agent) => agent.id)).toEqual(['one', 'two'])
    expect(subAgentsFromTool({ ...tool, output: 'invalid' })).toEqual([])
    expect(subAgentsFromTool({ ...tool, name: 'bash' })).toEqual([])
  })

  it('distinguishes starting a child from observing it running and does not create stale activities', () => {
    expect(subAgentsFromTool(spawn())[0]).toMatchObject({ status: 'starting', live: false })
    const current = message()
    expect(applySubAgentEvent([current], { id: 1, event: 'subagent_done', data: 'late failure', details: { subAgentId: 'unknown', success: false } }, undefined, false)).toBe(false)
    expect(current.subAgents).toBeUndefined()
  })

  it('renders compact named lifecycle rows and keeps live tool progress visible', async () => {
    const html = await renderToString(createSSRApp({ render: () => h(SubAgentActivityList, { agents: [
      { id: 'one', name: 'Composer polish', role: 'coder', status: 'running', live: true, progress: 'Using edit_file' },
      { id: 'two', name: 'Cards panels polish', role: 'coder', status: 'running', live: true }
    ] }) }))
    expect(html).toContain('Composer polish和Cards panels polish正在工作')
    expect(html).toContain('正在使用 edit_file')
    expect(html).toContain('agent-avatars')
    const historical = spawn()
    historical.state = 'done'
    historical.output = '{"id":"child","role":"coder","status":"running"}'
    const fallback = await renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool: historical }) }))
    expect(fallback).toContain('代码工程师已启动')
    expect(fallback).not.toContain('正在工作')
    expect(fallback).not.toContain('activity-target')
  })

  it('preserves completed live child state on hydration using persistent call identity', () => {
    const old = message()
    old.tools![0]!.callId = 'call'
    old.subAgents = [{ id: 'child', role: 'coder', status: 'completed', sourceToolId: 'spawn', output: 'Completed in 5.0s', elapsedMs: 5000 }]
    const hydrated = hydrateMessages({
      id: 'session', createdAt: '2026-11-16T00:00:00Z', updatedAt: '2026-11-16T00:00:00Z', messages: [
        { role: 'assistant', text: '', toolCalls: [{ id: 'call', name: 'subagent', input: old.tools![0]!.input }] },
        { role: 'tool', text: '{"id":"child","role":"coder","status":"running"}', toolName: 'subagent', toolCallId: 'call' }
      ]
    }, [old])
    expect(hydrated[0]?.subAgents?.[0]).toMatchObject({ id: 'child', sourceToolId: 'call', status: 'completed', elapsedMs: 5000 })
    expect(messageSubAgents(hydrated[0]!)).toHaveLength(1)
    expect(messageSubAgents(hydrated[0]!)[0]?.status).toBe('completed')
    expect(hydrated[0]?.subAgents?.[0]).not.toBe(old.subAgents[0])
  })

  it('does not attach an unrelated same-role child after reload', () => {
    const old = message()
    old.subAgents = [{ id: 'first', role: 'coder', status: 'failed', sourceToolId: 'first-call' }]
    const replacement: ChatMessage = { id: 'new', role: 'assistant', content: '', createdAt: 1,
      tools: [{ id: 'second-call', name: 'subagent', state: 'done', input: '{"action":"spawn","role":"coder"}' }] }
    expect(mergeSubAgentActivities([replacement], [old])[0]?.subAgents).toBeUndefined()
    const sameMessage = { ...replacement, id: old.id, tools: [] }
    expect(mergeSubAgentActivities([sameMessage], [old])[0]?.subAgents?.[0]?.id).toBe('first')
  })

  it('appends start and completion at their receiving messages and deduplicates repeated notifications', () => {
    const first = message()
    const later: ChatMessage = { id: 'later', role: 'assistant', content: 'Parent continues working', createdAt: 1 }
    const messages = [first, later]
    const start = { id: 1, event: 'subagent_start' as const, data: 'Improve', details: { subAgentId: 'child', role: 'coder' } }
    const done = { id: 1, event: 'subagent_done' as const, data: 'Complete', details: { subAgentId: 'child', success: true } }
    applySubAgentEvent(messages, start, first)
    applySubAgentEvent(messages, done, later)
    applySubAgentEvent(messages, done, later)
    applySubAgentEvent(messages, { id: 0, event: 'subagent.updated', data: 'child', details: { taskId: 'child', status: 'completed' } }, undefined, false)
    applySubAgentEvent(messages, start, first)
    expect(first.subAgentEvents?.map((event) => event.kind)).toEqual(['started'])
    expect(later.subAgentEvents?.map((event) => event.kind)).toEqual(['completed'])
    expect(later.subAgentEvents?.[0]?.contentOffset).toBe(later.content.length)
    expect(first.subAgentEvents?.[0]?.agent.status).toBe('running')
    expect(first.subAgents?.[0]?.status).toBe('completed')
  })

  it('records a new instruction to the same real child without overwriting its earlier lifecycle', () => {
    const first = message()
    applySubAgentEvent([first], { id: 1, event: 'subagent_start', data: 'First', details: { subAgentId: 'child', role: 'coder' } }, first)
    applySubAgentEvent([first], { id: 1, event: 'subagent_done', data: 'First done', details: { subAgentId: 'child', success: true } }, first)
    const later: ChatMessage = { id: 'later', role: 'assistant', content: '', createdAt: 1, tools: [
      { id: 'send-call', callId: 'send-call', name: 'subagent', input: '{"action":"send","id":"child","role":"coder"}', state: 'running' }
    ] }
    const messages = [first, later]
    applySubAgentEvent(messages, { id: 2, event: 'subagent_start', data: 'Follow up', details: { subAgentId: 'child', role: 'coder' } }, later)
    expect(first.subAgentEvents).toHaveLength(2)
    expect(later.subAgentEvents).toHaveLength(1)
    expect(later.subAgentEvents?.[0]?.id).not.toBe(first.subAgentEvents?.[0]?.id)
    expect(later.subAgents?.[0]).toMatchObject({ id: 'child', status: 'running', sourceToolId: 'send-call', task: 'Follow up' })
  })

  it('does not bind a repeated start to another pending same-role launch', () => {
    const current = message()
    const start = { id: 1, event: 'subagent_start' as const, data: 'First', details: { subAgentId: 'first', role: 'coder' } }
    applySubAgentEvent([current], start, current)
    current.tools!.push(spawn('second-call'))
    applySubAgentEvent([current], start, current)
    applySubAgentEvent([current], { ...start, data: 'Second', details: { subAgentId: 'second', role: 'coder' } }, current)
    expect(current.subAgentEvents?.map((event) => event.agentId)).toEqual(['first', 'second'])
    expect(messageSubAgents(current).map((agent) => [agent.id, agent.sourceToolId]))
      .toEqual([['first', 'spawn'], ['second', 'second-call']])
  })

  it('keeps real child session identity without turning token deltas into a progress summary', () => {
    const current = message()
    applySubAgentEvent([current], { id: 1, event: 'subagent_start', data: 'Improve', details: { subAgentId: 'child', role: 'coder' } }, current)
    applySubAgentEvent([current], { id: 1, event: 'subagent_progress', data: 'token', details: { subAgentId: 'child', kind: 'text', delta: 'token', childSessionId: 'child-session' } }, current)
    expect(current.subAgents?.[0]?.childSessionId).toBe('child-session')
    expect(current.subAgents?.[0]?.progress).toBeUndefined()
    applySubAgentEvent([current], { id: 1, event: 'subagent_progress', data: 'Using edit_file', details: { subAgentId: 'child', kind: 'tool_start', detail: 'Composer.vue' } }, current)
    expect(current.subAgents?.[0]).toMatchObject({ progress: 'Using edit_file', detail: 'Composer.vue' })
    applySubAgentEvent([current], { id: 1, event: 'subagent_progress', data: 'Responding', details: { subAgentId: 'child', kind: 'text', delta: 'next token' } }, current)
    expect(current.subAgents?.[0]?.progress).toBe('Using edit_file')
    expect(subAgentsFromTool({ ...spawn(), structuredContent: { id: 'durable-child', sessionId: 'durable-session', workspace: 'E:/project', status: 'running' } })[0])
      .toMatchObject({ id: 'durable-child', childSessionId: 'durable-session', workspace: 'E:/project' })
  })

  it('preserves lifecycle identities and the later completion position across repeated hydration', () => {
    const first = message()
    first.tools![0]!.callId = 'durable-call'
    const later: ChatMessage = { id: 'later', role: 'assistant', content: 'The parent has continued.', createdAt: 1 }
    const old = [first, later]
    applySubAgentEvent(old, { id: 1, event: 'subagent_start', data: 'Improve', details: { subAgentId: 'child', role: 'coder' } }, first)
    applySubAgentEvent(old, { id: 1, event: 'subagent_done', data: 'Done', details: { subAgentId: 'child', success: true } }, later)
    const saved = {
      id: 'session', createdAt: '2026-11-16T00:00:00Z', updatedAt: '2026-11-16T00:00:00Z', messages: [
        { role: 'assistant' as const, text: '', toolCalls: [{ id: 'durable-call', name: 'subagent', input: first.tools![0]!.input }] },
        { role: 'tool' as const, text: '{"id":"child","role":"coder","status":"running"}', toolName: 'subagent', toolCallId: 'durable-call' },
        { role: 'assistant' as const, text: later.content }
      ]
    }
    const hydrated = hydrateMessages(saved, old)
    const repeated = hydrateMessages(saved, hydrated)
    expect(repeated[0]?.subAgentEvents?.map((event) => event.kind)).toEqual(['started'])
    expect(repeated[1]?.subAgentEvents?.map((event) => event.kind)).toEqual(['completed'])
    expect(repeated.flatMap((item) => item.subAgentEvents ?? []).map((event) => event.id))
      .toEqual(old.flatMap((item) => item.subAgentEvents ?? []).map((event) => event.id))
    expect(repeated[0]?.subAgentEvents?.[0]?.agent).not.toBe(first.subAgentEvents?.[0]?.agent)
  })

  it('renders distinct real role icons and immutable lifecycle rows even after the summary settles', async () => {
    const current = message()
    applySubAgentEvent([current], { id: 1, event: 'subagent_start', data: 'Improve', details: { subAgentId: 'child', role: 'coder' } }, current)
    applySubAgentEvent([current], { id: 1, event: 'subagent_done', data: 'Done', details: { subAgentId: 'child', success: true } }, current)
    const events = current.subAgentEvents as SubAgentLifecycleEvent[]
    const html = await renderToString(createSSRApp({ render: () => h(SubAgentActivityList, { agents: [
      ...current.subAgents!, { id: 'researcher', role: 'researcher', status: 'running' }, { id: 'tester', role: 'tester', status: 'completed' }
    ], events }) }))
    expect(html).toContain('lucide-code-xml')
    expect(html).toContain('lucide-search')
    expect(html).toContain('lucide-flask-conical')
    expect(html).toContain('代码工程师开始工作')
    expect(html).toContain('代码工程师已完成工作')
    expect(html.match(/data-subagent-event-id=/g)).toHaveLength(2)
    expect(html).toContain('打开子智能体会话')
  })
})
