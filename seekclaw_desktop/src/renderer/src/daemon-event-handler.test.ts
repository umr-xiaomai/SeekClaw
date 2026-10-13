import { ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import { createDaemonEventHandler } from './daemon-event-handler'
import type { DaemonMessage } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'

describe('createDaemonEventHandler', () => {
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
})
