import { ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import { createDaemonEventHandler } from './daemon-event-handler'
import type { DaemonMessage } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'

describe('createDaemonEventHandler', () => {
  it('routes generated titles to their owning session without changing the active chat request', () => {
    const active: ThreadItem = { id: 'active', title: 'Active', sessionId: 'active-session', messages: [], updatedAt: 0, running: true, requestId: 7 }
    const owner: ThreadItem = { id: 'owner', title: 'Input', titleSource: 'input', sessionId: 'owner-session', messages: [], updatedAt: 0 }
    const handler = createDaemonEventHandler({
      threads: ref([active, owner]), activeThreadId: ref(active.id), projects: ref([]),
      handleScheduleUpdated: vi.fn(), scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession: vi.fn(), rememberFinishedRequest: vi.fn(), isFinishedRequest: () => false,
      scheduleQueuedDrain: vi.fn(), reloadBackgroundThreadIfIdle: vi.fn()
    })
    handler({ id: 0, event: 'session.title', data: '', details: { sessionId: 'owner-session', generating: true } })
    expect(owner.titleGenerating).toBe(true)
    handler({ id: 0, event: 'session.title', data: '', details: { sessionId: 'owner-session', generating: false, title: '  Generated title  ' } })
    expect(owner).toMatchObject({ title: 'Generated title', titleSource: 'generated', titleGenerating: false })
    owner.title = 'My title'
    owner.titleSource = 'manual'
    handler({ id: 0, event: 'session.title', data: '', details: { sessionId: 'owner-session', generating: true } })
    handler({ id: 0, event: 'session.title', data: '', details: { sessionId: 'owner-session', title: 'Late generated title' } })
    expect(owner).toMatchObject({ title: 'My title', titleSource: 'manual', titleGenerating: false })
    expect(active).toMatchObject({ title: 'Active', running: true, requestId: 7 })
  })

  it('does not force a late guidance acknowledgement to pull the reader to the bottom', () => {
    const thread: ThreadItem = {
      id: 'thread', title: 'Guidance', sessionId: 'session', requestId: 10, assistantId: 'current',
      running: true, pendingGuidance: 1, updatedAt: 0,
      messages: [{ id: 'current', role: 'assistant', content: 'Earlier answer', state: 'streaming', createdAt: 0 }]
    }
    const scrollToBottom = vi.fn().mockResolvedValue(undefined)
    const handler = createDaemonEventHandler({
      threads: ref([thread]), activeThreadId: ref(thread.id), projects: ref([]),
      handleScheduleUpdated: vi.fn(), scrollToBottom,
      reloadThreadSession: vi.fn().mockResolvedValue(undefined), rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false, scheduleQueuedDrain: vi.fn(), reloadBackgroundThreadIfIdle: vi.fn()
    })

    handler({ id: 10, event: 'steer', data: 'Guidance accepted', requestMethod: 'chat', sessionId: 'session' })

    expect(thread.pendingGuidance).toBe(0)
    expect(thread.messages[0]!.state).toBe('done')
    expect(thread.messages).toHaveLength(2)
    expect(thread.assistantId).toBe(thread.messages[1]!.id)
    expect(scrollToBottom).toHaveBeenCalledExactlyOnceWith()
  })

  it.each(['thinking', 'delta'] as const)('keeps completed tools and clears the retry phase after %s resumes', (resumeEvent) => {
    const thread: ThreadItem = {
      id: 'thread', title: 'Retry', sessionId: 'session', requestId: 10, assistantId: 'current',
      running: true, updatedAt: 0,
      messages: [
        { id: 'previous', role: 'assistant', content: 'completed', state: 'done', createdAt: 0,
          tools: [{ id: 'first', name: 'write_file', state: 'done' }] },
        { id: 'current', role: 'assistant', content: 'prefix partial', thinking: 'earlier thought new thought',
          state: 'streaming', createdAt: 0, tools: [{ id: 'saved', name: 'read_file', state: 'done' }] }
      ]
    }
    const scheduleQueuedDrain = vi.fn()
    const handler = createDaemonEventHandler({
      threads: ref([thread]), activeThreadId: ref(thread.id), projects: ref([]),
      handleScheduleUpdated: vi.fn(), scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession: vi.fn().mockResolvedValue(undefined), rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false, scheduleQueuedDrain, reloadBackgroundThreadIfIdle: vi.fn()
    })
    handler({ id: 10, event: 'provider_retry', data: 'ResponseEnded', requestMethod: 'chat', sessionId: 'session',
      details: { attempt: 1, delayMs: 1000 } })
    expect(thread.running).toBe(true)
    expect(thread.phase).toContain('重试')
    handler({ id: 10, event: 'stream_reset', data: 'test/model', requestMethod: 'chat', sessionId: 'session',
      details: { textLength: 7, thinkingLength: 12 } })
    expect(thread.messages[1]).toMatchObject({ content: 'prefix ', thinking: 'earlier thought', state: 'thinking' })
    expect(thread.phase).toContain('重试')
    handler({ id: 10, event: resumeEvent, data: 'replacement', requestMethod: 'chat', sessionId: 'session' })
    expect(thread.phase).toBeUndefined()
    expect(thread.messages[1]).toMatchObject(resumeEvent === 'thinking'
      ? { content: 'prefix ', thinking: 'earlier thoughtreplacement', thinkingActive: true }
      : { content: 'prefix replacement', thinkingActive: false })
    expect(thread.messages[1]!.tools).toHaveLength(1)
    expect(thread.messages[0]).toMatchObject({ content: 'completed', state: 'done' })
    expect(thread.messages[0]!.tools).toHaveLength(1)
    expect(scheduleQueuedDrain).not.toHaveBeenCalled()
  })

  it.each([null, false, 0])('maps live tool output and structured value %j to the correct call', (structuredContent) => {
    const thread: ThreadItem = {
      id: 'thread', title: 'Tools', sessionId: 'session', requestId: 10, assistantId: 'a1',
      running: true, updatedAt: 0,
      messages: [{ id: 'a1', role: 'assistant', content: '', createdAt: 0, tools: [
        { id: 'call-1', callId: 'call-1', name: 'bash', detail: 'original command', state: 'running' },
        { id: 'call-2', callId: 'call-2', name: 'bash', state: 'running' }
      ] }]
    }
    const handler = createDaemonEventHandler({
      threads: ref([thread]), activeThreadId: ref(thread.id), projects: ref([]),
      handleScheduleUpdated: vi.fn(), scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession: vi.fn().mockResolvedValue(undefined), rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false, scheduleQueuedDrain: vi.fn(), reloadBackgroundThreadIfIdle: vi.fn()
    })
    handler({ id: 10, event: 'tool_done', data: 'summary', requestMethod: 'chat', sessionId: 'session',
      details: { callId: 'call-1', success: true, output: 'first line\nsecond line', content: null,
        structuredContent, metadata: null } })
    const tools = thread.messages[0]!.tools!
    expect(tools[0]).toMatchObject({ state: 'done', detail: 'original command', output: 'first line\nsecond line' })
    expect(tools[0]!.structuredContent).toBe(structuredContent ?? undefined)
    expect(tools[0]!.content).toBeUndefined()
    expect(tools[0]!.metadata).toBeUndefined()
    expect(tools[1]!.state).toBe('running')

    const content = [{ type: 'text', text: 'remote result' }]
    const widget = { version: 1, kind: 'artifact', id: 'artifact:demo', title: 'Demo', path: 'E:\\demo.html', mediaType: 'text/html' }
    handler({ id: 10, event: 'tool_done', data: 'remote summary', requestMethod: 'chat', sessionId: 'session',
      details: { callId: 'call-2', success: false, output: 'remote result', content,
        structuredContent: { error: 'remote failure' }, metadata: { vendor: 'test' }, widgets: [widget] } })
    expect(tools[1]).toMatchObject({ state: 'error', output: 'remote result', content,
      structuredContent: { error: 'remote failure' }, metadata: { vendor: 'test' }, widgets: [widget] })
  })

  it('uses the summary as output when connected to an older daemon', () => {
    const thread: ThreadItem = {
      id: 'thread', title: 'Tools', sessionId: 'session', requestId: 10, assistantId: 'a1',
      running: true, updatedAt: 0,
      messages: [{ id: 'a1', role: 'assistant', content: '', createdAt: 0, tools: [
        { id: 'call', name: 'bash', state: 'running' }
      ] }]
    }
    const handler = createDaemonEventHandler({
      threads: ref([thread]), activeThreadId: ref(thread.id), projects: ref([]),
      handleScheduleUpdated: vi.fn(), scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession: vi.fn().mockResolvedValue(undefined), rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false, scheduleQueuedDrain: vi.fn(), reloadBackgroundThreadIfIdle: vi.fn()
    })
    handler({ id: 10, event: 'tool_done', data: 'command failed', requestMethod: 'chat',
      sessionId: 'session', details: { callId: 'call', success: false } })
    expect(thread.messages[0]!.tools![0]).toMatchObject({ state: 'error', output: 'command failed' })
  })

  it('does not prematurely clear running state or trigger queue drain when activeTurnToken is set', () => {
    const thread: ThreadItem = {
      id: 'thread-1',
      title: 'Active Thread',
      messages: [
        { id: 'u1', role: 'user', content: 'hello', createdAt: 0 },
        { id: 'a1', role: 'assistant', content: '', state: 'thinking', createdAt: 0 }
      ],
      running: true,
      activeTurnToken: 'turn-token-123',
      assistantId: 'a1',
      requestId: 10,
      sessionId: 'sess-1',
      updatedAt: 0
    }

    const scheduleQueuedDrain = vi.fn()
    const reloadThreadSession = vi.fn().mockResolvedValue(undefined)
    const rememberFinishedRequest = vi.fn()

    const handler = createDaemonEventHandler({
      threads: ref([thread]),
      activeThreadId: ref(thread.id),
      projects: ref<ProjectItem[]>([]),
      handleScheduleUpdated: vi.fn(),
      scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession,
      rememberFinishedRequest,
      isFinishedRequest: () => false,
      scheduleQueuedDrain,
      reloadBackgroundThreadIfIdle: vi.fn()
    })

    const doneEvent: DaemonMessage = {
      id: 10,
      event: 'done',
      data: 'final reply',
      requestMethod: 'chat',
      sessionId: 'sess-1'
    }

    handler(doneEvent)

    // Bubble updated to done
    expect(thread.messages[1]!.state).toBe('done')
    expect(thread.messages[1]!.content).toBe('final reply')

    // But lifecycle remains owned by the active turn runner
    expect(thread.running).toBe(true)
    expect(thread.activeTurnToken).toBe('turn-token-123')
    expect(scheduleQueuedDrain).not.toHaveBeenCalled()
    expect(reloadThreadSession).not.toHaveBeenCalled()
    expect(rememberFinishedRequest).toHaveBeenCalledWith(thread, 10)
  })

  it('clears running state and drains queue on terminal event when activeTurnToken is not set', () => {
    const thread: ThreadItem = {
      id: 'thread-bg',
      title: 'Background Thread',
      messages: [
        { id: 'a1', role: 'assistant', content: '', state: 'thinking', createdAt: 0 }
      ],
      running: true,
      activeTurnToken: undefined,
      assistantId: 'a1',
      requestId: 20,
      sessionId: 'sess-2',
      updatedAt: 0
    }

    const scheduleQueuedDrain = vi.fn()
    const reloadThreadSession = vi.fn().mockResolvedValue(undefined)

    const handler = createDaemonEventHandler({
      threads: ref([thread]),
      activeThreadId: ref('other-thread'),
      projects: ref<ProjectItem[]>([]),
      handleScheduleUpdated: vi.fn(),
      scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession,
      rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false,
      scheduleQueuedDrain,
      reloadBackgroundThreadIfIdle: vi.fn()
    })

    const doneEvent: DaemonMessage = {
      id: 20,
      event: 'done',
      data: '',
      requestMethod: 'chat',
      sessionId: 'sess-2'
    }

    handler(doneEvent)

    expect(thread.running).toBe(false)
    expect(thread.activeTurnToken).toBeUndefined()
    expect(scheduleQueuedDrain).toHaveBeenCalledWith(thread)
  })
  it('stores error in errorMessage instead of polluting message.content with code blocks', () => {
    const thread: ThreadItem = {
      id: 'thread-err',
      title: 'Error Thread',
      messages: [
        { id: 'u1', role: 'user', content: 'test', createdAt: 0 },
        { id: 'a1', role: 'assistant', content: 'Partial output', state: 'streaming', createdAt: 0 }
      ],
      running: true,
      activeTurnToken: undefined,
      assistantId: 'a1',
      requestId: 30,
      sessionId: 'sess-3',
      updatedAt: 0
    }

    const handler = createDaemonEventHandler({
      threads: ref([thread]),
      activeThreadId: ref(thread.id),
      projects: ref<ProjectItem[]>([]),
      handleScheduleUpdated: vi.fn(),
      scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession: vi.fn().mockResolvedValue(undefined),
      rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false,
      scheduleQueuedDrain: vi.fn(),
      reloadBackgroundThreadIfIdle: vi.fn()
    })

    const errorEvent: DaemonMessage = {
      id: 30,
      event: 'error',
      data: 'EasyGemini returned HTTP 503: auth_unavailable: no auth available',
      requestMethod: 'chat',
      sessionId: 'sess-3'
    }

    handler(errorEvent)

    const assistant = thread.messages.find((m) => m.id === 'a1')
    expect(assistant?.state).toBe('error')
    expect(assistant?.errorMessage).toBe('EasyGemini returned HTTP 503: auth_unavailable: no auth available')
    expect(assistant?.content).toBe('Partial output')
    expect(assistant?.content).not.toContain('```text')
  })

  it('tracks preparing tool call details and thinking_done transitions cleanly', () => {
    const thread: ThreadItem = {
      id: 'thread-prep',
      title: 'Prep Thread',
      messages: [
        { id: 'u1', role: 'user', content: 'edit file', createdAt: 0 },
        { id: 'a1', role: 'assistant', content: '', thinking: '', state: 'thinking', thinkingActive: false, createdAt: 0 }
      ],
      running: true,
      assistantId: 'a1',
      requestId: 40,
      sessionId: 'sess-4',
      updatedAt: 0
    }

    const handler = createDaemonEventHandler({
      threads: ref([thread]),
      activeThreadId: ref(thread.id),
      projects: ref<ProjectItem[]>([]),
      handleScheduleUpdated: vi.fn(),
      scrollToBottom: vi.fn().mockResolvedValue(undefined),
      reloadThreadSession: vi.fn().mockResolvedValue(undefined),
      rememberFinishedRequest: vi.fn(),
      isFinishedRequest: () => false,
      scheduleQueuedDrain: vi.fn(),
      reloadBackgroundThreadIfIdle: vi.fn()
    })

    // Status "Thinking" before any thinking token arrives must not mark thinkingActive.
    handler({ id: 40, event: 'status', data: 'Thinking', requestMethod: 'chat', sessionId: 'sess-4' })
    expect(thread.messages[1]!.thinkingActive).toBe(false)

    // Real thinking delta marks thinkingActive true.
    handler({ id: 40, event: 'thinking', data: 'Need to edit', requestMethod: 'chat', sessionId: 'sess-4' })
    expect(thread.messages[1]!.thinkingActive).toBe(true)

    // Commentary followed by Preparing tool call surfaces target tool detail.
    handler({ id: 40, event: 'delta', data: 'Switching to HTML.', requestMethod: 'chat', sessionId: 'sess-4' })
    expect(thread.messages[1]!.thinkingActive).toBe(false)
    handler({ id: 40, event: 'status', data: 'Preparing tool call', requestMethod: 'chat', sessionId: 'sess-4', details: { detail: 'edit_file' } })
    expect(thread.messages[1]!.preparingTool).toBe('edit_file')

    // tool_start clears preparingTool.
    handler({ id: 40, event: 'tool_start', data: 'edit_file', requestMethod: 'chat', sessionId: 'sess-4', details: { callId: 'c1' } })
    expect(thread.messages[1]!.preparingTool).toBeUndefined()

    // thinking_done ends active thinking when a step transitions without prose delta.
    handler({ id: 40, event: 'thinking', data: 'More thought', requestMethod: 'chat', sessionId: 'sess-4' })
    expect(thread.messages[1]!.thinkingActive).toBe(true)
    handler({ id: 40, event: 'thinking_done', data: '', requestMethod: 'chat', sessionId: 'sess-4' })
    expect(thread.messages[1]!.thinkingActive).toBe(false)
    expect(thread.messages[1]!.state).toBe('streaming')
  })
})
