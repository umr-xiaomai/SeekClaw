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
})
