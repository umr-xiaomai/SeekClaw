import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { refreshGlobalSessions, refreshProjectSessions } from './session-sync'
import type { RuntimeSessionHeader } from './app-helpers'
import type { ThreadItem } from './types'

afterEach(() => vi.unstubAllGlobals())

const saved = (id: string, parentSessionId?: string): RuntimeSessionHeader => ({
  id, title: id === 'user' ? '只读审查 Godot 渲染' : id,
  parentSessionId, createdAt: '2026-11-16T08:00:00Z', updatedAt: '2026-11-16T08:10:00Z'
})

describe('sidebar session synchronization', () => {
  it('filters internal project sessions and removes child rows cached by older versions without title guessing', async () => {
    const request = vi.fn().mockResolvedValue({ data: JSON.stringify([saved('user'), saved('scheduled'), saved('child', 'user')]) })
    vi.stubGlobal('window', { seekclaw: { daemon: { request } } })
    const threads = ref<ThreadItem[]>([
      { id: 'old-child', sessionId: 'child', projectId: 'project', title: 'Child', messages: [], updatedAt: 0 },
      { id: 'old-record-child', sessionId: 'legacy-child', projectId: 'project', title: 'Legacy child', messages: [], updatedAt: 0 },
      { id: 'draft', projectId: 'project', title: 'Unsaved', messages: [], updatedAt: 0 },
      { id: 'other', sessionId: 'other', projectId: 'other-project', title: 'Other', messages: [], updatedAt: 0 }
    ])
    await refreshProjectSessions({ threads, daemonState: ref({ connected: true, endpoint: 'test' }) }, { id: 'project', name: 'Project', path: 'E:\\project' })
    expect(threads.value.map((thread) => thread.id)).not.toContain('old-child')
    expect(threads.value.map((thread) => thread.id)).not.toContain('old-record-child')
    expect(threads.value.map((thread) => thread.sessionId)).toContain('user')
    expect(threads.value.map((thread) => thread.sessionId)).toContain('scheduled')
    expect(threads.value.map((thread) => thread.id)).toContain('draft')
    expect(threads.value.map((thread) => thread.id)).toContain('other')
  })

  it('applies the same ownership rule to global sessions and leaves project threads intact', async () => {
    vi.stubGlobal('window', { seekclaw: { daemon: { request: vi.fn().mockResolvedValue({ data: JSON.stringify([saved('user'), saved('child', 'user')]) }) } } })
    const threads = ref<ThreadItem[]>([
      { id: 'old-child', sessionId: 'child', title: 'Child', messages: [], updatedAt: 0 },
      { id: 'old-legacy', sessionId: 'legacy-child', title: 'Legacy child', messages: [], updatedAt: 0 },
      { id: 'project', sessionId: 'project-session', projectId: 'project', title: 'Project', messages: [], updatedAt: 0 }
    ])
    await refreshGlobalSessions({ threads, daemonState: ref({ connected: true, endpoint: 'test' }) })
    expect(threads.value.map((thread) => thread.sessionId)).toEqual(['project-session', 'user'])
  })
})
