import { effectScope, nextTick, ref, type EffectScope } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useSidebarExpansion } from './sidebar-expansion'

const PREFERENCES = 'seekclaw-sidebar-expansion-v2'
const EXPANDED = 'seekclaw-sidebar-expanded-projects-v1'
const KNOWN = 'seekclaw-sidebar-known-projects-v1'
const TASKS = 'seekclaw-sidebar-task-section-expanded-v1'
const scopes: EffectScope[] = []
let storage: Map<string, string>

beforeEach(() => {
  storage = new Map()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => storage.get(key) ?? null,
    setItem: (key: string, value: string) => storage.set(key, value)
  })
})

afterEach(() => {
  scopes.splice(0).forEach((scope) => scope.stop())
  vi.unstubAllGlobals()
})

function mount(ids: string[] = [], ready = false) {
  const projectIds = ref(ids)
  const projectsReady = ref(ready)
  const scope = effectScope()
  scopes.push(scope)
  const state = scope.run(() => useSidebarExpansion({
    projectIds: () => projectIds.value,
    projectsReady: () => projectsReady.value
  }))!
  return { ...state, projectIds, projectsReady, unmount: () => scope.stop() }
}

describe('sidebar collapse preferences', () => {
  it('starts empty project and global task groups collapsed', () => {
    const state = mount(['p1', 'p2'], true)
    expect([...state.expandedProjects.value]).toEqual([])
    expect(state.taskSectionExpanded.value).toBe(false)
    expect(storage.has(PREFERENCES)).toBe(false)
  })

  it('keeps new groups closed when the project list is refreshed or extended', async () => {
    const state = mount(['p1'], true)
    state.projectIds.value = ['p1', 'p2']
    await nextTick()
    expect([...state.expandedProjects.value]).toEqual([])
    expect(state.taskSectionExpanded.value).toBe(false)
    expect(storage.has(PREFERENCES)).toBe(false)
  })

  it('preserves manual choices through empty startup and partial caches', async () => {
    storage.set(PREFERENCES, JSON.stringify({ projects: { collapsed: false, expanded: true, removed: false }, tasks: false }))
    const state = mount()
    state.projectIds.value = ['collapsed']
    await nextTick()
    expect(JSON.parse(storage.get(PREFERENCES)!).projects).toHaveProperty('removed')

    state.projectIds.value = ['collapsed', 'expanded', 'new']
    state.projectsReady.value = true
    await nextTick()
    expect([...state.expandedProjects.value]).toEqual(['expanded'])
    expect(state.taskSectionExpanded.value).toBe(false)
    expect(JSON.parse(storage.get(PREFERENCES)!).projects).toEqual({ collapsed: false, expanded: true })
  })

  it('restores both manual expansion and manual collapse after remount', () => {
    const original = mount(['p1'], true)
    original.toggleProject('p1')
    original.taskSectionExpanded.value = true
    original.unmount()
    const reopened = mount(['p1'], true)
    expect(reopened.expandedProjects.value.has('p1')).toBe(true)
    expect(reopened.taskSectionExpanded.value).toBe(true)

    reopened.toggleProject('p1')
    reopened.taskSectionExpanded.value = false
    reopened.unmount()
    const restarted = mount(['p1'], true)
    expect(restarted.expandedProjects.value.has('p1')).toBe(false)
    expect(restarted.taskSectionExpanded.value).toBe(false)
  })

  it('keeps manual expansion when other projects are added without expanding them', async () => {
    const state = mount(['p1'], true)
    state.toggleProject('p1')
    state.taskSectionExpanded.value = true
    state.projectIds.value.push('p2')
    await nextTick()
    expect(state.expandedProjects.value.has('p1')).toBe(true)
    expect(state.expandedProjects.value.has('p2')).toBe(false)
    expect(state.taskSectionExpanded.value).toBe(true)
    expect(JSON.parse(storage.get(PREFERENCES)!).projects).toEqual({ p1: true })
  })

  it('cleans up preferences only after confirmed removal and defaults readded projects to collapsed', async () => {
    const state = mount(['p1'], true)
    state.toggleProject('p1')
    // Requesting or canceling deletion does not change the authoritative list.
    state.projectIds.value = ['p1']
    await nextTick()
    expect(state.expandedProjects.value.has('p1')).toBe(true)
    expect(JSON.parse(storage.get(PREFERENCES)!).projects).toEqual({ p1: true })

    state.projectIds.value = []
    await nextTick()
    expect(JSON.parse(storage.get(PREFERENCES)!).projects).toEqual({})
    state.projectIds.value = ['p1']
    await nextTick()
    expect(state.expandedProjects.value.has('p1')).toBe(false)
  })

  it('recognizes a successfully loaded empty list as authoritative', async () => {
    storage.set(PREFERENCES, '{"projects":{"removed":true},"tasks":true}')
    const state = mount()
    state.projectsReady.value = true
    await nextTick()
    expect(JSON.parse(storage.get(PREFERENCES)!)).toEqual({ projects: {}, tasks: true })
  })

  it('restores both expanded and collapsed legacy choices and preserves subsequent changes', () => {
    storage.set(EXPANDED, '["p1"]')
    storage.set(KNOWN, '["p1","p2"]')
    storage.set(TASKS, 'true')
    const state = mount(['p1', 'p2'], true)
    expect([...state.expandedProjects.value]).toEqual(['p1'])
    expect(state.taskSectionExpanded.value).toBe(true)
    state.toggleProject('p1')
    state.taskSectionExpanded.value = false
    state.unmount()
    const restored = mount(['p1', 'p2'], true)
    expect([...restored.expandedProjects.value]).toEqual([])
    expect(restored.taskSectionExpanded.value).toBe(false)
  })

  it('preserves a legacy collapsed global task group', () => {
    storage.set(TASKS, 'false')
    const state = mount(['p1'], true)
    expect(state.taskSectionExpanded.value).toBe(false)
  })

  it('retains manual choices made before project loading completes', async () => {
    const state = mount(['p1'])
    state.toggleProject('p1')
    state.toggleProject('p1')
    state.projectsReady.value = true
    await nextTick()
    expect(state.expandedProjects.value.has('p1')).toBe(false)
  })

  it('restores legacy expanded records even if the legacy known-project list is missing', () => {
    storage.set(EXPANDED, '["p1"]')
    const state = mount(['p1'], true)
    expect(state.expandedProjects.value.has('p1')).toBe(true)
  })

  it('falls back to collapsed empty groups for corrupt preferences and ignores invalid field types', () => {
    storage.set(PREFERENCES, '{bad json')
    storage.set(KNOWN, '{}')
    expect(mount(['p1'], true).expandedProjects.value.size).toBe(0)
    storage.set(PREFERENCES, '{"projects":{"p1":"true","p2":true},"tasks":"true"}')
    const state = mount(['p1', 'p2'], true)
    expect([...state.expandedProjects.value]).toEqual(['p2'])
    expect(state.taskSectionExpanded.value).toBe(false)
  })

  it('keeps manual toggles usable when storage is unavailable', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('Storage unavailable') },
      setItem: () => { throw new Error('Storage unavailable') }
    })
    const state = mount(['p1'], true)
    expect(state.taskSectionExpanded.value).toBe(false)
    state.toggleProject('p1')
    state.taskSectionExpanded.value = true
    expect(state.expandedProjects.value.has('p1')).toBe(true)
    expect(state.taskSectionExpanded.value).toBe(true)
  })
})
