<script setup lang="ts">
import {
  Archive,
  ChevronRight,
  FolderClosed,
  FolderOpen,
  Copy,
  FileText,
  GitFork,
  Pencil,
  LoaderCircle,
  MoreHorizontal,
  Pin,
  PinOff,
  Plus,
  Search,
  SlidersHorizontal,
  SquarePen,
  Trash2
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import ContextMenu from './ContextMenu.vue'
import type { ContextMenuAnchor, ContextMenuEntry } from './context-menu'
import logoUrl from '../../../../resources/logo.png?url'
import type { ProjectItem, ThreadItem } from '../types'
import { useSidebarExpansion } from '../sidebar-expansion'

const props = withDefaults(defineProps<{
  projects: ProjectItem[]
  projectsReady: boolean
  threads: ThreadItem[]
  activeThreadId: string
  activeProjectId?: string
  width?: number
}>(), {
  width: 240
})

const emit = defineEmits<{
  newTask: [projectId?: string]
  openWorkspace: []
  selectThread: [id: string]
  taskSettings: [thread: ThreadItem]
  renameTask: [thread: ThreadItem]
  forkTask: [thread: ThreadItem]
  copyTask: [thread: ThreadItem, format: 'text' | 'id']
  showProject: [project: ProjectItem]
  archiveTask: [thread: ThreadItem]
  restoreTask: [thread: ThreadItem]
  deleteTask: [thread: ThreadItem]
  openProjectProperties: [project: ProjectItem]
  deleteProject: [project: ProjectItem]
  archiveProjectTasks: [project: ProjectItem]
  deleteProjectTasks: [project: ProjectItem]
  archiveGlobalTasks: []
  deleteGlobalTasks: []
  resize: [width: number]
  collapse: []
  resetWidth: []
}>()

const MIN_SIDEBAR_WIDTH = 180
const COLLAPSE_THRESHOLD = 140

const resizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-sidebar')
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const delta = event.clientX - resizeStartX
  const nextWidth = resizeStartWidth + delta
  if (nextWidth < COLLAPSE_THRESHOLD) {
    stopResize()
    emit('collapse')
    return
  }
  const maxWidth = Math.min(600, Math.max(MIN_SIDEBAR_WIDTH, Math.floor(window.innerWidth * 0.55)))
  emit('resize', Math.min(maxWidth, Math.max(MIN_SIDEBAR_WIDTH, Math.round(nextWidth))))
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = props.width
  document.body.classList.add('is-resizing-sidebar')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

const PINNED_THREADS_STORAGE_KEY = 'seekclaw-pinned-threads'

const searching = ref(false)
const query = ref('')
const { taskSectionExpanded, projectSectionExpanded, expandedProjects, toggleProject } = useSidebarExpansion({
  projectIds: () => props.projects.map((project) => project.id),
  projectsReady: () => props.projectsReady
})
const menuKey = ref('')
const menuAnchor = shallowRef<ContextMenuAnchor>({ x: 8, y: 8 })
const menuReturnFocus = shallowRef<HTMLElement | null>(null)

function loadPinnedThreadIds(): Set<string> {
  try {
    const raw = localStorage.getItem(PINNED_THREADS_STORAGE_KEY)
    if (!raw) return new Set()
    const parsed = JSON.parse(raw)
    return Array.isArray(parsed) ? new Set(parsed.filter((item): item is string => typeof item === 'string')) : new Set()
  } catch {
    return new Set()
  }
}

function savePinnedThreadIds(ids: Set<string>): void {
  try {
    localStorage.setItem(PINNED_THREADS_STORAGE_KEY, JSON.stringify(Array.from(ids)))
  } catch {
    // ignore
  }
}

const pinnedThreadIds = ref<Set<string>>(loadPinnedThreadIds())

function isPinned(thread: ThreadItem): boolean {
  return thread.pinned === true || pinnedThreadIds.value.has(thread.id)
}

function togglePin(thread: ThreadItem): void {
  const next = new Set(pinnedThreadIds.value)
  if (isPinned(thread)) {
    next.delete(thread.id)
    thread.pinned = false
  } else {
    next.add(thread.id)
    thread.pinned = true
  }
  pinnedThreadIds.value = next
  savePinnedThreadIds(next)
}

watch(() => props.threads, (items) => {
  let changed = false
  const next = new Set(pinnedThreadIds.value)
  items.forEach((thread) => {
    if (next.has(thread.id)) {
      thread.pinned = true
    } else if (thread.pinned) {
      next.add(thread.id)
      changed = true
    }
  })
  if (changed) {
    pinnedThreadIds.value = next
    savePinnedThreadIds(next)
  }
}, { immediate: true })

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())

function sortThreads(threads: ThreadItem[]): ThreadItem[] {
  return threads.sort((left, right) => {
    const leftPinned = isPinned(left) ? 1 : 0
    const rightPinned = isPinned(right) ? 1 : 0
    if (leftPinned !== rightPinned) return rightPinned - leftPinned
    return right.updatedAt - left.updatedAt
  })
}

function isRealThread(thread: ThreadItem): boolean {
  return Boolean(thread.sessionId || thread.messages.length > 0 || thread.running)
}

function visibleThreads(projectId: string): ThreadItem[] {
  const filtered = props.threads
    .filter((thread) => thread.projectId === projectId && !thread.archived && isRealThread(thread))
    .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
  return sortThreads(filtered)
}

function projectHasRunningTask(projectId: string): boolean {
  return props.threads.some((thread) => thread.projectId === projectId && !thread.archived && thread.running)
}

function createTask(projectId?: string): void {
  emit('newTask', projectId)
}

const visibleGlobalThreads = computed(() => {
  const filtered = props.threads
    .filter((thread) => !thread.projectId && !thread.archived && isRealThread(thread))
    .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
  return sortThreads(filtered)
})

function toggleMenu(key: string, event: MouseEvent): void {
  if (menuKey.value === key) { menuKey.value = ''; return }
  const trigger = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
  if (!trigger) return
  menuReturnFocus.value = trigger
  menuAnchor.value = { rect: trigger.getBoundingClientRect(), align: 'end' }
  menuKey.value = key
}

function openContextMenu(key: string, event: MouseEvent): void {
  const row = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
  menuReturnFocus.value = row?.querySelector<HTMLElement>('button') ?? null
  menuAnchor.value = { x: event.clientX, y: event.clientY }
  menuKey.value = key
}

const menuThread = computed(() => menuKey.value.startsWith('thread:')
  ? props.threads.find((thread) => thread.id === menuKey.value.slice(7)) : undefined)
const menuProject = computed(() => menuKey.value.startsWith('project:')
  ? props.projects.find((project) => project.id === menuKey.value.slice(8)) : undefined)
const menuItems = computed<ContextMenuEntry[]>(() => {
  const thread = menuThread.value
  if (thread) return [
    { id: 'pin', label: isPinned(thread) ? '取消置顶' : '置顶任务', icon: isPinned(thread) ? PinOff : Pin },
    { id: 'rename', label: '重命名…', icon: Pencil },
    { id: 'settings', label: '任务设置…', icon: SlidersHorizontal },
    { type: 'separator' },
    { id: 'fork', label: '分叉任务', icon: GitFork, disabled: !thread.sessionId || thread.running },
    { id: 'copy', label: '复制', icon: Copy, children: [
      { id: 'copy-text', label: '复制会话内容', icon: FileText, disabled: !thread.sessionId && !thread.messages.length },
      { id: 'copy-id', label: '复制会话 ID', icon: Copy, disabled: !thread.sessionId }
    ] },
    { type: 'separator' },
    { id: 'archive', label: '归档任务', icon: Archive, disabled: thread.running },
    { id: 'delete', label: '永久删除任务…', icon: Trash2, danger: true, disabled: thread.running }
  ]
  const project = menuProject.value
  if (project) {
    const items = props.threads.filter((thread) => thread.projectId === project.id)
    const running = items.some((thread) => thread.running)
    return [
      { id: 'new', label: '新建任务', icon: SquarePen },
      { id: 'edit', label: '编辑项目…', icon: Pencil },
      { type: 'separator' },
      { id: 'show', label: '在资源管理器中显示', icon: FolderOpen },
      { type: 'separator' },
      { id: 'archive-all', label: '归档全部任务…', icon: Archive, disabled: running || (!items.length && project.loaded) },
      { type: 'separator' },
      { id: 'delete-all', label: '永久删除全部任务…', icon: Trash2, danger: true, disabled: running || (!items.length && project.loaded) },
      { id: 'delete-project', label: '删除项目和会话…', icon: Trash2, danger: true, disabled: running }
    ]
  }
  const items = props.threads.filter((thread) => !thread.projectId)
  return [
    { id: 'new', label: '新建任务', icon: SquarePen },
    { type: 'separator' },
    { id: 'archive-all', label: '归档全部任务…', icon: Archive, disabled: !items.length || items.some((thread) => thread.running) },
    { id: 'delete-all', label: '永久删除全部任务…', icon: Trash2, danger: true, disabled: !items.length || items.some((thread) => thread.running) }
  ]
})

function selectMenuAction(id: string): void {
  const thread = menuThread.value
  const project = menuProject.value
  menuKey.value = ''
  if (thread) {
    if (id === 'pin') togglePin(thread)
    else if (id === 'rename') emit('renameTask', thread)
    else if (id === 'settings') emit('taskSettings', thread)
    else if (id === 'fork') emit('forkTask', thread)
    else if (id === 'copy-text' || id === 'copy-id') emit('copyTask', thread, id === 'copy-id' ? 'id' : 'text')
    else if (id === 'archive') emit('archiveTask', thread)
    else if (id === 'delete') emit('deleteTask', thread)
  } else if (project) {
    if (id === 'new') createTask(project.id)
    else if (id === 'edit') emit('openProjectProperties', project)
    else if (id === 'show') emit('showProject', project)
    else if (id === 'archive-all') emit('archiveProjectTasks', project)
    else if (id === 'delete-all') emit('deleteProjectTasks', project)
    else if (id === 'delete-project') emit('deleteProject', project)
  } else {
    if (id === 'new') createTask()
    else if (id === 'archive-all') emit('archiveGlobalTasks')
    else if (id === 'delete-all') emit('deleteGlobalTasks')
  }
}

onBeforeUnmount(stopResize)

</script>

<template>
  <aside class="sidebar" aria-label="任务与项目">
    <div class="sidebar-brand-row">
      <div class="brand-lockup">
        <img :src="logoUrl" alt="" />
        <span>SeekClaw</span>
      </div>
      <button class="icon-button" title="搜索任务" aria-label="搜索任务" :aria-expanded="searching" @click="searching = !searching">
        <Search :size="18" />
      </button>
    </div>

    <Transition name="collapse">
      <div v-if="searching" class="sidebar-search">
        <Search :size="15" />
        <input v-model="query" autofocus aria-label="搜索任务" placeholder="搜索任务" />
      </div>
    </Transition>

    <nav class="primary-nav">
      <button class="nav-item is-primary" @click="createTask(activeProjectId)">
        <SquarePen :size="18" />
        <span>新建任务</span>
      </button>
    </nav>

    <div class="sidebar-scroll">
      <section class="sidebar-section tasks-section">
        <div class="section-heading task-section-heading">
          <button class="task-section-toggle" type="button"
            aria-controls="sidebar-global-tasks" :aria-expanded="taskSectionExpanded" @click="taskSectionExpanded = !taskSectionExpanded">
            <ChevronRight class="section-disclosure" :class="{ expanded: taskSectionExpanded }" :size="14" />
            <span>任务</span>
          </button>
          <div class="section-heading-actions">
            <button class="icon-button compact" title="新建任务" @click="createTask()">
              <Plus :size="15" />
            </button>
            <button class="icon-button compact" data-sidebar-menu-trigger aria-label="任务菜单" :aria-expanded="menuKey === 'global'" @click.stop="toggleMenu('global', $event)">
              <MoreHorizontal :size="16" />
            </button>
          </div>
        </div>

        <div v-if="taskSectionExpanded" id="sidebar-global-tasks" class="task-list">
          <div v-for="thread in visibleGlobalThreads" :key="thread.id" class="thread-heading-row"
            :class="{ active: thread.id === activeThreadId, 'has-menu': menuKey === `thread:${thread.id}` }"
            @contextmenu.prevent="openContextMenu(`thread:${thread.id}`, $event)">
            <button class="thread-row" :title="thread.titleGenerating ? '正在生成任务标题' : thread.title"
              :aria-busy="thread.titleGenerating || undefined" :aria-current="thread.id === activeThreadId ? 'page' : undefined"
              @click="emit('selectThread', thread.id)">
              <span v-if="thread.titleGenerating" class="task-title-skeleton" role="status" aria-label="正在生成任务标题" />
              <span v-else>{{ thread.title }}</span>
              <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
            </button>
            <div class="thread-row-actions">
              <button class="icon-button compact thread-row-action"
                :class="{ 'pinned-active': isPinned(thread) }"
                :title="isPinned(thread) ? '取消置顶' : '置顶'"
                :aria-label="isPinned(thread) ? '取消置顶' : '置顶'"
                :aria-pressed="isPinned(thread)"
                @click.stop="togglePin(thread)">
                <Pin :size="14" />
              </button>
              <button class="icon-button compact thread-row-action"
                title="归档任务"
                aria-label="归档任务"
                @click.stop="emit('archiveTask', thread)">
                <Archive :size="14" />
              </button>
            </div>
          </div>
          <p v-if="visibleGlobalThreads.length === 0" class="project-empty">无任务</p>
        </div>
      </section>

      <section class="sidebar-section project-section">
        <div class="section-heading">
          <button class="task-section-toggle" type="button" aria-controls="sidebar-projects"
            :aria-expanded="projectSectionExpanded" @click="projectSectionExpanded = !projectSectionExpanded">
            <ChevronRight class="section-disclosure" :class="{ expanded: projectSectionExpanded }" :size="14" />
            <span>项目</span>
          </button>
          <button class="icon-button compact" title="添加项目" @click="emit('openWorkspace')">
            <Plus :size="15" />
          </button>
        </div>

        <div v-if="projectSectionExpanded" id="sidebar-projects" class="project-list">
          <div v-for="project in projects" :key="project.id" class="project-group">
            <div class="project-heading-row" :class="{ 'has-menu': menuKey === `project:${project.id}` }"
              @contextmenu.prevent="openContextMenu(`project:${project.id}`, $event)">
              <button class="project-row" :title="project.path" :aria-expanded="expandedProjects.has(project.id)" :aria-controls="`project-tasks-${project.id}`"
                @click="toggleProject(project.id)">
                <span class="project-row-icon" aria-hidden="true">
                  <FolderOpen v-if="expandedProjects.has(project.id)" :size="17" />
                  <FolderClosed v-else :size="17" />
                </span>
                <span>{{ project.name }}</span>
                <LoaderCircle v-if="!expandedProjects.has(project.id) && projectHasRunningTask(project.id)"
                  class="thread-running-spinner" :size="15" aria-label="项目中有任务正在运行" />
              </button>
              <button class="icon-button compact row-menu-button" :aria-label="`${project.name} 项目菜单`"
                data-sidebar-menu-trigger
                :aria-expanded="menuKey === `project:${project.id}`"
                @click.stop="toggleMenu(`project:${project.id}`, $event)">
                <MoreHorizontal :size="16" />
              </button>
            </div>

            <Transition name="project-collapse">
              <div v-if="expandedProjects.has(project.id)" :id="`project-tasks-${project.id}`" class="project-tasks">
                <div v-for="thread in visibleThreads(project.id)" :key="thread.id" class="thread-heading-row"
                  :class="{ active: thread.id === activeThreadId, 'has-menu': menuKey === `thread:${thread.id}` }"
                  @contextmenu.prevent="openContextMenu(`thread:${thread.id}`, $event)">
                  <button class="thread-row" :title="thread.titleGenerating ? '正在生成任务标题' : thread.title"
                    :aria-busy="thread.titleGenerating || undefined" :aria-current="thread.id === activeThreadId ? 'page' : undefined"
                    @click="emit('selectThread', thread.id)">
                    <span v-if="thread.titleGenerating" class="task-title-skeleton" role="status" aria-label="正在生成任务标题" />
                    <span v-else>{{ thread.title }}</span>
                    <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
                  </button>
                  <div class="thread-row-actions">
                    <button class="icon-button compact thread-row-action"
                      :class="{ 'pinned-active': isPinned(thread) }"
                      :title="isPinned(thread) ? '取消置顶' : '置顶'"
                      :aria-label="isPinned(thread) ? '取消置顶' : '置顶'"
                      :aria-pressed="isPinned(thread)"
                      @click.stop="togglePin(thread)">
                      <Pin :size="14" />
                    </button>
                    <button class="icon-button compact thread-row-action"
                      title="归档任务"
                      aria-label="归档任务"
                      @click.stop="emit('archiveTask', thread)">
                      <Archive :size="14" />
                    </button>
                  </div>
                </div>
                <p v-if="visibleThreads(project.id).length === 0" class="project-empty">
                  {{ project.loaded === false ? '正在读取任务…' : '无任务' }}
                </p>
              </div>
            </Transition>
          </div>

          <p v-if="projects.length === 0" class="empty-recent">添加一个项目以开始任务</p>
        </div>
      </section>
    </div>

    <div
      class="sidebar-resize-handle"
      role="separator"
      aria-label="调整侧边栏宽度"
      aria-orientation="vertical"
      title="拖拽调节侧边栏宽度，拖至最小时隐藏，双击重置"
      @pointerdown="startResize"
      @dblclick="emit('resetWidth')"
    />
    <ContextMenu v-if="menuKey" :key="menuKey" :items="menuItems" :anchor="menuAnchor"
      :return-focus="menuReturnFocus" :label="menuThread ? '任务菜单' : menuProject ? '项目菜单' : '任务分组菜单'"
      @select="selectMenuAction" @close="menuKey = ''" />
  </aside>
</template>
