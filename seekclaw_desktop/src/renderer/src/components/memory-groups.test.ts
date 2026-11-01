import { beforeEach, describe, expect, it } from 'vitest'
import type { ProjectItem } from '../types'
import {
  MEMORY_COLLAPSED_GROUPS_KEY,
  filterMemories,
  groupMemories,
  loadActiveMemoryGroupKey,
  loadCollapsedGroups,
  resolveProjectInfo,
  saveActiveMemoryGroupKey,
  saveCollapsedGroups,
  type CrossSessionMemoryItem
} from './memory-groups'

describe('memory-groups', () => {
  const sampleProjects: ProjectItem[] = [
    {
      id: 'p1',
      name: 'SeekClaw',
      path: 'E:\\Project\\SeekClaw',
      loaded: true
    },
    {
      id: 'p2',
      name: 'StarLife AI',
      path: 'E:\\Project\\StarLife',
      loaded: true
    }
  ]

  const storage = new Map<string, string>()
  const mockLocalStorage = {
    getItem: (key: string) => storage.get(key) ?? null,
    setItem: (key: string, val: string) => storage.set(key, val),
    removeItem: (key: string) => storage.delete(key),
    clear: () => storage.clear()
  }

  beforeEach(() => {
    // @ts-expect-error test mock
    globalThis.localStorage = mockLocalStorage
    storage.clear()
  })

  it('resolves global conversation when workspace is empty or null', () => {
    expect(resolveProjectInfo(null)).toEqual({ name: '全局对话', isGlobal: true })
    expect(resolveProjectInfo(undefined)).toEqual({ name: '全局对话', isGlobal: true })
    expect(resolveProjectInfo('')).toEqual({ name: '全局对话', isGlobal: true })
  })

  it('resolves project name by path case-insensitively with slashes normalized', () => {
    const info1 = resolveProjectInfo('e:/project/seekclaw', sampleProjects)
    expect(info1.name).toBe('SeekClaw')
    expect(info1.isGlobal).toBe(false)

    const info2 = resolveProjectInfo('E:\\Project\\StarLife\\', sampleProjects)
    expect(info2.name).toBe('StarLife AI')
  })

  it('falls back to folder name if project is not in registered list', () => {
    const info = resolveProjectInfo('D:\\Work\\UnknownApp', sampleProjects)
    expect(info.name).toBe('UnknownApp')
    expect(info.path).toBe('D:\\Work\\UnknownApp')
    expect(info.isGlobal).toBe(false)
  })

  it('groups memories by workspace with global first and project groups sorted by recency', () => {
    const memories: CrossSessionMemoryItem[] = [
      {
        id: 'm1',
        content: 'StarLife 训练损失为 focal CE',
        sourceWorkspace: 'E:\\Project\\StarLife',
        createdAt: '2026-11-01T10:00:00Z'
      },
      {
        id: 'm2',
        content: '全局偏好：中文回复',
        sourceWorkspace: null,
        createdAt: '2026-11-01T09:00:00Z'
      },
      {
        id: 'm3',
        content: 'SeekClaw 采用 C# .NET 10',
        sourceWorkspace: 'E:\\Project\\SeekClaw',
        createdAt: '2026-11-01T12:00:00Z'
      },
      {
        id: 'm4',
        content: 'StarLife 比赛已晋级复赛',
        sourceWorkspace: 'E:/Project/StarLife/',
        createdAt: '2026-11-01T11:00:00Z'
      }
    ]

    const groups = groupMemories(memories, sampleProjects)

    expect(groups.length).toBe(3)
    // Global is first
    expect(groups[0]?.isGlobal).toBe(true)
    expect(groups[0]?.title).toBe('全局对话')
    expect(groups[0]?.items.length).toBe(1)

    // SeekClaw has latest memory (12:00) vs StarLife (11:00)
    expect(groups[1]?.title).toBe('SeekClaw')
    expect(groups[1]?.items.length).toBe(1)

    // StarLife has 2 memories (slashes normalized into same group)
    expect(groups[2]?.title).toBe('StarLife AI')
    expect(groups[2]?.items.length).toBe(2)
  })

  it('persists and restores collapsed groups in localStorage', () => {
    expect(loadCollapsedGroups().size).toBe(0)

    const set = new Set(['__global__', 'e:/project/seekclaw'])
    saveCollapsedGroups(set)

    const restored = loadCollapsedGroups()
    expect(restored.has('__global__')).toBe(true)
    expect(restored.has('e:/project/seekclaw')).toBe(true)
    expect(restored.size).toBe(2)
  })

  it('filters memories by keyword across content and source workspace', () => {
    const items: CrossSessionMemoryItem[] = [
      { id: '1', content: 'Focal loss 2.0 with smooth l1', sourceWorkspace: 'E:\\Project\\StarLife', createdAt: '' },
      { id: '2', content: 'Always reply in Chinese', sourceWorkspace: null, createdAt: '' },
      { id: '3', content: 'C# 10 architecture review', sourceWorkspace: 'E:\\Project\\SeekClaw', createdAt: '' }
    ]

    expect(filterMemories(items, '')).toHaveLength(3)
    expect(filterMemories(items, 'focal')).toHaveLength(1)
    expect(filterMemories(items, 'FOCAL')[0]?.id).toBe('1')
    expect(filterMemories(items, 'Chinese')[0]?.id).toBe('2')
    expect(filterMemories(items, 'SeekClaw')[0]?.id).toBe('3')
    expect(filterMemories(items, 'nonexistent')).toHaveLength(0)
  })

  it('persists and restores active selected memory group key in localStorage', () => {
    expect(loadActiveMemoryGroupKey()).toBe('__all__')

    saveActiveMemoryGroupKey('e:/project/seekclaw')
    expect(loadActiveMemoryGroupKey()).toBe('e:/project/seekclaw')

    saveActiveMemoryGroupKey('__global__')
    expect(loadActiveMemoryGroupKey()).toBe('__global__')
  })
})
