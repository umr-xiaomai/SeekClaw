import { computed, ref, watch } from 'vue'

const PREFERENCES_STORAGE_KEY = 'seekclaw-sidebar-expansion-v2'
const EXPANDED_PROJECTS_STORAGE_KEY = 'seekclaw-sidebar-expanded-projects-v1'
const KNOWN_PROJECTS_STORAGE_KEY = 'seekclaw-sidebar-known-projects-v1'
const TASK_SECTION_EXPANDED_STORAGE_KEY = 'seekclaw-sidebar-task-section-expanded-v1'

function loadStoredSet(key: string): Set<string> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(key) ?? '[]')
    return Array.isArray(parsed)
      ? new Set(parsed.filter((item): item is string => typeof item === 'string'))
      : new Set()
  } catch {
    return new Set()
  }
}

interface ExpansionPreferences {
  projects: Map<string, boolean>
  tasks?: boolean
}

function loadPreferences(): ExpansionPreferences {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(PREFERENCES_STORAGE_KEY) ?? 'null')
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      const stored = parsed as Record<string, unknown>
      const projects = stored.projects && typeof stored.projects === 'object' && !Array.isArray(stored.projects)
        ? Object.entries(stored.projects).filter((entry): entry is [string, boolean] => typeof entry[1] === 'boolean')
        : []
      return { projects: new Map(projects), tasks: typeof stored.tasks === 'boolean' ? stored.tasks : undefined }
    }
  } catch { /* Fall back to legacy preferences or collapsed defaults. */ }

  // Preserve both states from V1. Session contents cannot tell us which groups
  // the user chose to expand, so migration must not discard saved expansion.
  const expanded = loadStoredSet(EXPANDED_PROJECTS_STORAGE_KEY)
  const known = loadStoredSet(KNOWN_PROJECTS_STORAGE_KEY)
  let tasks: boolean | undefined
  try {
    const raw = localStorage.getItem(TASK_SECTION_EXPANDED_STORAGE_KEY)
    if (raw === 'true' || raw === 'false') tasks = raw === 'true'
  } catch { /* Storage may be unavailable. */ }
  return {
    projects: new Map([...new Set([...known, ...expanded])].map((id) => [id, expanded.has(id)])),
    tasks
  }
}

function save(preferences: ExpansionPreferences): void {
  try {
    localStorage.setItem(PREFERENCES_STORAGE_KEY, JSON.stringify({
      projects: Object.fromEntries(preferences.projects),
      tasks: preferences.tasks
    }))
  } catch {
    // The sidebar remains usable if storage is unavailable.
  }
}

/** Keep user preferences independent of asynchronous project/session hydration. */
export function useSidebarExpansion(options: {
  projectIds: () => string[]
  projectsReady: () => boolean
}) {
  const stored = loadPreferences()
  const projectOverrides = ref(stored.projects)
  const taskOverride = ref(stored.tasks)

  // Loading or changing session history must never change a group's state.
  const expandedProjects = computed(() => new Set(options.projectIds().filter((id) =>
    projectOverrides.value.get(id) === true)))
  const taskSectionExpanded = computed({
    get: () => taskOverride.value ?? false,
    set: (expanded: boolean) => {
      taskOverride.value = expanded
      save({ projects: projectOverrides.value, tasks: expanded })
    }
  })

  watch([() => [...options.projectIds()], options.projectsReady], ([ids, ready]) => {
    // Startup placeholders and legacy caches are not an authoritative project list.
    if (!ready) return
    const currentIds = new Set(ids)
    const next = new Map([...projectOverrides.value].filter(([id]) => currentIds.has(id)))
    if (next.size !== projectOverrides.value.size) {
      projectOverrides.value = next
      save({ projects: next, tasks: taskOverride.value })
    }
  }, { immediate: true })

  function toggleProject(id: string): void {
    const next = new Map(projectOverrides.value)
    next.set(id, !expandedProjects.value.has(id))
    projectOverrides.value = next
    save({ projects: next, tasks: taskOverride.value })
  }

  return { taskSectionExpanded, expandedProjects, toggleProject }
}
