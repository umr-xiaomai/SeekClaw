import type { ProjectItem } from '../types'

export interface CrossSessionMemoryItem {
  id: string
  content: string
  sourceWorkspace?: string | null
  sourceSessionId?: string | null
  createdAt: string
}

export interface MemoryGroup {
  key: string
  title: string
  path?: string
  isGlobal: boolean
  items: CrossSessionMemoryItem[]
}

export const MEMORY_COLLAPSED_GROUPS_KEY = 'seekclaw-memory-collapsed-groups-v1'
export const MEMORY_ACTIVE_GROUP_KEY = 'seekclaw-memory-active-group-v1'

export function normalizePathForComparison(path?: string | null): string {
  if (!path) return ''
  return path.replace(/[/\\]+/g, '/').replace(/\/$/, '').toLowerCase()
}

export function resolveProjectInfo(
  sourceWorkspace: string | null | undefined,
  projects: ProjectItem[] = []
): { name: string; path?: string; isGlobal: boolean } {
  if (!sourceWorkspace || !sourceWorkspace.trim()) {
    return { name: '全局对话', isGlobal: true }
  }
  const normalized = normalizePathForComparison(sourceWorkspace)
  const matched = projects.find((p) => normalizePathForComparison(p.path) === normalized)
  if (matched) {
    return { name: matched.name, path: matched.path, isGlobal: false }
  }
  const segments = sourceWorkspace.split(/[/\\]/).filter(Boolean)
  const fallbackName = segments.length > 0 ? segments[segments.length - 1]! : sourceWorkspace
  return { name: fallbackName, path: sourceWorkspace, isGlobal: false }
}

export function groupMemories(
  memories: CrossSessionMemoryItem[],
  projects: ProjectItem[] = []
): MemoryGroup[] {
  const groupsMap = new Map<string, MemoryGroup>()

  for (const item of memories) {
    const isGlobal = !item.sourceWorkspace || !item.sourceWorkspace.trim()
    const groupKey = isGlobal ? '__global__' : normalizePathForComparison(item.sourceWorkspace)

    let group = groupsMap.get(groupKey)
    if (!group) {
      const info = resolveProjectInfo(item.sourceWorkspace, projects)
      group = {
        key: groupKey,
        title: info.name,
        path: info.path,
        isGlobal: info.isGlobal,
        items: []
      }
      groupsMap.set(groupKey, group)
    }
    group.items.push(item)
  }

  const list = Array.from(groupsMap.values())
  list.sort((a, b) => {
    if (a.isGlobal && !b.isGlobal) return -1
    if (!a.isGlobal && b.isGlobal) return 1
    const aTime = a.items[0]?.createdAt ?? ''
    const bTime = b.items[0]?.createdAt ?? ''
    return bTime.localeCompare(aTime)
  })

  return list
}

export function filterMemories(
  items: CrossSessionMemoryItem[],
  query: string
): CrossSessionMemoryItem[] {
  const q = query.trim().toLowerCase()
  if (!q) return items
  return items.filter((item) =>
    item.content.toLowerCase().includes(q) ||
    (item.sourceWorkspace ? item.sourceWorkspace.toLowerCase().includes(q) : false)
  )
}

export function loadActiveMemoryGroupKey(): string {
  if (typeof localStorage === 'undefined') return '__all__'
  try {
    return localStorage.getItem(MEMORY_ACTIVE_GROUP_KEY) || '__all__'
  } catch {
    return '__all__'
  }
}

export function saveActiveMemoryGroupKey(key: string): void {
  if (typeof localStorage === 'undefined') return
  try {
    localStorage.setItem(MEMORY_ACTIVE_GROUP_KEY, key)
  } catch {
    // Ignore storage quota errors
  }
}

export function loadCollapsedGroups(): Set<string> {
  if (typeof localStorage === 'undefined') return new Set()
  try {
    const raw = localStorage.getItem(MEMORY_COLLAPSED_GROUPS_KEY)
    if (!raw) return new Set()
    const parsed = JSON.parse(raw)
    return Array.isArray(parsed) ? new Set(parsed.filter((x): x is string => typeof x === 'string')) : new Set()
  } catch {
    return new Set()
  }
}

export function saveCollapsedGroups(keys: Set<string>): void {
  if (typeof localStorage === 'undefined') return
  try {
    localStorage.setItem(MEMORY_COLLAPSED_GROUPS_KEY, JSON.stringify(Array.from(keys)))
  } catch {
    // Ignore storage quota errors
  }
}
