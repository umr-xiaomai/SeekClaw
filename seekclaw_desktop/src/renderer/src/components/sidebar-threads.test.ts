import { describe, expect, it } from 'vitest'
import type { ThreadItem } from '../types'

function sortThreadsWithPin(
  threads: ThreadItem[],
  isPinned: (t: ThreadItem) => boolean,
  query = ''
): ThreadItem[] {
  const normalized = query.trim().toLocaleLowerCase()
  return threads
    .filter((t) => !normalized || t.title.toLocaleLowerCase().includes(normalized))
    .sort((left, right) => {
      const leftPinned = isPinned(left) ? 1 : 0
      const rightPinned = isPinned(right) ? 1 : 0
      if (leftPinned !== rightPinned) return rightPinned - leftPinned
      return right.updatedAt - left.updatedAt
    })
}

describe('sidebar threads pinning and sorting', () => {
  const thread1: ThreadItem = {
    id: 't1',
    title: 'Older thread',
    updatedAt: 1000,
    messages: []
  }
  const thread2: ThreadItem = {
    id: 't2',
    title: 'Newer thread',
    updatedAt: 2000,
    messages: []
  }
  const thread3: ThreadItem = {
    id: 't3',
    title: 'Middle thread',
    updatedAt: 1500,
    messages: []
  }

  it('sorts unpinned threads by updatedAt descending', () => {
    const sorted = sortThreadsWithPin([thread1, thread2, thread3], () => false)
    expect(sorted.map((t) => t.id)).toEqual(['t2', 't3', 't1'])
  })

  it('places pinned threads at the top regardless of updatedAt', () => {
    const pinnedSet = new Set(['t1'])
    const sorted = sortThreadsWithPin([thread1, thread2, thread3], (t) => pinnedSet.has(t.id))
    expect(sorted.map((t) => t.id)).toEqual(['t1', 't2', 't3'])
  })

  it('sorts multiple pinned threads among themselves by updatedAt descending', () => {
    const pinnedSet = new Set(['t1', 't3'])
    const sorted = sortThreadsWithPin([thread1, thread2, thread3], (t) => pinnedSet.has(t.id))
    expect(sorted.map((t) => t.id)).toEqual(['t3', 't1', 't2'])
  })

  it('filters by query while maintaining pinned precedence', () => {
    const pinnedSet = new Set(['t1'])
    const sorted = sortThreadsWithPin([thread1, thread2, thread3], (t) => pinnedSet.has(t.id), 'thread')
    expect(sorted.map((t) => t.id)).toEqual(['t1', 't2', 't3'])

    const filtered = sortThreadsWithPin([thread1, thread2, thread3], (t) => pinnedSet.has(t.id), 'Older')
    expect(filtered.map((t) => t.id)).toEqual(['t1'])
  })

  it('filters out unstarted draft threads from sidebar display', () => {
    function isRealThread(thread: ThreadItem): boolean {
      return Boolean(thread.sessionId || thread.messages.length > 0 || thread.running)
    }

    const draftThread: ThreadItem = {
      id: 'draft',
      title: '新任务',
      updatedAt: 3000,
      messages: []
    }
    const realSessionThread: ThreadItem = {
      id: 's1',
      title: 'Existing session',
      sessionId: 'sess-123',
      updatedAt: 2500,
      messages: []
    }
    const threadWithMessages: ThreadItem = {
      id: 'm1',
      title: 'Active conversation',
      updatedAt: 2000,
      messages: [{ id: 'msg-1', role: 'user', content: 'hello', createdAt: 2000 }]
    }
    const runningDraftThread: ThreadItem = {
      id: 'r1',
      title: 'Running turn',
      running: true,
      updatedAt: 2200,
      messages: []
    }

    expect(isRealThread(draftThread)).toBe(false)
    expect(isRealThread(realSessionThread)).toBe(true)
    expect(isRealThread(threadWithMessages)).toBe(true)
    expect(isRealThread(runningDraftThread)).toBe(true)

    const allThreads = [draftThread, realSessionThread, threadWithMessages, runningDraftThread]
    const visible = allThreads.filter(isRealThread)
    expect(visible.map((t) => t.id)).toEqual(['s1', 'm1', 'r1'])
  })

  it('manages project expansion state and prunes deleted projects', () => {
    function syncProjectState(
      currentProjects: Array<{ id: string }>,
      expanded: Set<string>,
      known: Set<string>
    ): { nextExpanded: Set<string>; nextKnown: Set<string> } {
      const currentIds = new Set(currentProjects.map((p) => p.id))
      const nextExpanded = new Set(expanded)
      const nextKnown = new Set(known)

      // 1. Remove deleted projects
      for (const id of Array.from(nextExpanded)) {
        if (!currentIds.has(id)) nextExpanded.delete(id)
      }
      for (const id of Array.from(nextKnown)) {
        if (!currentIds.has(id)) nextKnown.delete(id)
      }

      // 2. Newly added projects default to expanded
      for (const project of currentProjects) {
        if (!nextKnown.has(project.id)) {
          nextKnown.add(project.id)
          nextExpanded.add(project.id)
        }
      }

      return { nextExpanded, nextKnown }
    }

    // Step 1: Initial load with 2 projects
    const p1 = { id: 'p1' }
    const p2 = { id: 'p2' }
    const expanded = new Set<string>()
    const known = new Set<string>()

    let state = syncProjectState([p1, p2], expanded, known)
    expect(state.nextExpanded.has('p1')).toBe(true)
    expect(state.nextExpanded.has('p2')).toBe(true)

    // Step 2: User collapses p2
    state.nextExpanded.delete('p2')
    expect(state.nextExpanded.has('p2')).toBe(false)

    // Step 3: Re-syncing existing projects preserves collapsed state (does not force expand)
    state = syncProjectState([p1, p2], state.nextExpanded, state.nextKnown)
    expect(state.nextExpanded.has('p1')).toBe(true)
    expect(state.nextExpanded.has('p2')).toBe(false)

    // Step 4: Delete p2 -> p2 is pruned from both expanded and known
    state = syncProjectState([p1], state.nextExpanded, state.nextKnown)
    expect(state.nextExpanded.has('p2')).toBe(false)
    expect(state.nextKnown.has('p2')).toBe(false)

    // Step 5: Re-add p2 later -> it is treated as a newly added project and starts fresh (expanded)
    state = syncProjectState([p1, p2], state.nextExpanded, state.nextKnown)
    expect(state.nextExpanded.has('p2')).toBe(true)
    expect(state.nextKnown.has('p2')).toBe(true)
  })
})
