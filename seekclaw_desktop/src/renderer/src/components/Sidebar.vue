<script setup lang="ts">
import {
  Archive,
  Blocks,
  CalendarClock,
  ChevronDown,
  ChevronRight,
  CircleHelp,
  Folder,
  FolderCog,
  LoaderCircle,
  MoreHorizontal,
  Plus,
  Search,
  Settings2,
  SlidersHorizontal,
  SquarePen,
  Store,
  Trash2
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import logoUrl from '../../../../resources/logo.png?url'
import type { ProjectItem, ThreadItem } from '../types'

const props = defineProps<{
  projects: ProjectItem[]
  threads: ThreadItem[]
  activeThreadId: string
  activeProjectId?: string
  version: string
}>()

const emit = defineEmits<{
  newTask: [projectId?: string]
  openWorkspace: []
  selectThread: [id: string]
  taskSettings: [thread: ThreadItem]
  archiveTask: [thread: ThreadItem]
  restoreTask: [thread: ThreadItem]
  deleteTask: [thread: ThreadItem]
  deleteProject: [project: ProjectItem]
  initializeProjectWorkspace: [project: ProjectItem]
  archiveProjectTasks: [project: ProjectItem]
  deleteProjectTasks: [project: ProjectItem]
  archiveGlobalTasks: []
  deleteGlobalTasks: []
  openArchived: []
  openScheduledTasks: []
  openExtensions: []
  openOfficialSkills: []
  openSettings: []
}>()

const searching = ref(false)
const query = ref('')
const taskSectionExpanded = ref(true)
const expandedProjects = ref(new Set<string>())
const menuKey = ref('')
const menuPlacement = ref<'up' | 'down'>('down')
const knownThreadIds = new Set<string>()

watch(() => props.projects, (items) => {
  const next = new Set(expandedProjects.value)
  items.forEach((project) => next.add(project.id))
  expandedProjects.value = next
}, { immediate: true, deep: true })

watch(() => props.threads.map((thread) => thread.id), (ids) => {
  const current = new Set(ids)
  for (const id of ids) {
    if (knownThreadIds.has(id)) continue
    const thread = props.threads.find((item) => item.id === id)
    if (!thread) continue
    if (thread.projectId) {
      const next = new Set(expandedProjects.value)
      next.add(thread.projectId)
      expandedProjects.value = next
    } else {
      taskSectionExpanded.value = true
    }
  }
  knownThreadIds.clear()
  current.forEach((id) => knownThreadIds.add(id))
}, { immediate: true })

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())

function visibleThreads(projectId: string): ThreadItem[] {
  return props.threads
    .filter((thread) => thread.projectId === projectId && !thread.archived)
    .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
    .sort((left, right) => right.updatedAt - left.updatedAt)
}

function projectHasRunningTask(projectId: string): boolean {
  return props.threads.some((thread) => thread.projectId === projectId && !thread.archived && thread.running)
}

function createTask(projectId?: string): void {
  if (projectId) {
    const next = new Set(expandedProjects.value)
    next.add(projectId)
    expandedProjects.value = next
  } else {
    taskSectionExpanded.value = true
  }
  emit('newTask', projectId)
}

const visibleGlobalThreads = computed(() => props.threads
  .filter((thread) => !thread.projectId && !thread.archived)
  .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
  .sort((left, right) => right.updatedAt - left.updatedAt))

function toggleProject(project: ProjectItem): void {
  const next = new Set(expandedProjects.value)
  if (next.has(project.id)) next.delete(project.id)
  else next.add(project.id)
  expandedProjects.value = next
}

function toggleMenu(key: string, event?: MouseEvent): void {
  if (menuKey.value === key) {
    menuKey.value = ''
    return
  }

  const trigger = event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
  const row = trigger?.closest<HTMLElement>('.thread-heading-row, .project-heading-row')
  const section = trigger?.closest<HTMLElement>('.sidebar-scroll')
  const triggerRect = trigger?.getBoundingClientRect()
  const rowRect = row?.getBoundingClientRect()
  const sectionRect = section?.getBoundingClientRect()
  const menuHeight = 190
  const spaceBelow = sectionRect && triggerRect ? sectionRect.bottom - triggerRect.bottom : Number.POSITIVE_INFINITY
  const spaceAbove = sectionRect && rowRect ? rowRect.top - sectionRect.top : Number.POSITIVE_INFINITY
  menuPlacement.value = spaceBelow < menuHeight && spaceAbove > spaceBelow ? 'up' : 'down'
  menuKey.value = key
}

function runAction(action: () => void): void {
  menuKey.value = ''
  action()
}

function closeMenuWhenFocusLeaves(event: Event): void {
  const target = event.target
  if (!(target instanceof Element) || !target.closest('.sidebar-context-menu, .row-menu-button'))
    menuKey.value = ''
}

function closeMenuOnEscape(event: KeyboardEvent): void {
  if (event.key === 'Escape') menuKey.value = ''
}

onMounted(() => {
  document.addEventListener('pointerdown', closeMenuWhenFocusLeaves)
  document.addEventListener('focusin', closeMenuWhenFocusLeaves)
  document.addEventListener('keydown', closeMenuOnEscape)
  window.addEventListener('blur', closeMenuWhenFocusLeaves)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', closeMenuWhenFocusLeaves)
  document.removeEventListener('focusin', closeMenuWhenFocusLeaves)
  document.removeEventListener('keydown', closeMenuOnEscape)
  window.removeEventListener('blur', closeMenuWhenFocusLeaves)
})

</script>

<template>
  <aside class="sidebar">
    <div class="sidebar-brand-row">
      <div class="brand-lockup">
        <img :src="logoUrl" alt="" />
        <span>SeekClaw</span>
      </div>
      <button class="icon-button" title="搜索任务" @click="searching = !searching">
        <Search :size="18" />
      </button>
    </div>

    <Transition name="collapse">
      <div v-if="searching" class="sidebar-search">
        <Search :size="15" />
        <input v-model="query" autofocus placeholder="搜索任务" />
      </div>
    </Transition>

    <nav class="primary-nav">
      <button class="nav-item is-primary" @click="createTask(activeProjectId)">
        <SquarePen :size="18" />
        <span>新建任务</span>
      </button>
      <button class="nav-item" @click="emit('openArchived')">
        <Archive :size="18" />
        <span>已归档</span>
      </button>
      <button class="nav-item" @click="emit('openScheduledTasks')">
        <CalendarClock :size="18" />
        <span>计划任务</span>
      </button>
      <button class="nav-item" @click="emit('openExtensions')">
        <Blocks :size="18" />
        <span>MCP 与技能</span>
      </button>
      <button class="nav-item" @click="emit('openOfficialSkills')">
        <Store :size="18" />
        <span>官方技能</span>
      </button>
    </nav>

    <div class="sidebar-scroll" :class="{ 'menu-open': menuKey }">
      <section class="sidebar-section tasks-section">
        <div class="section-heading task-section-heading">
          <button style="padding-left: unset;" class="task-section-toggle" type="button"
            :aria-expanded="taskSectionExpanded" @click="taskSectionExpanded = !taskSectionExpanded">
            <span>任务</span>
            <ChevronDown v-if="taskSectionExpanded" :size="15" />
            <ChevronRight v-else :size="15" />

          </button>
          <div class="section-heading-actions">
          <button class="icon-button compact" title="新建任务" @click="createTask()">
              <Plus :size="15" />
            </button>
            <button class="icon-button compact" aria-label="任务菜单" @click.stop="toggleMenu('global', $event)">
              <MoreHorizontal :size="16" />
            </button>
          </div>
          <Transition name="context-menu">
            <div v-if="menuKey === 'global'" class="sidebar-context-menu project-menu"
              :class="{ 'menu-up': menuPlacement === 'up' }">
              <button @click="runAction(() => createTask())">
                <SquarePen :size="15" />新建任务
              </button>
              <button @click="runAction(() => emit('archiveGlobalTasks'))">
                <Archive :size="15" />归档全部任务
              </button>
              <button class="danger" @click="runAction(() => emit('deleteGlobalTasks'))">
                <Trash2 :size="15" />删除全部任务
              </button>
            </div>
          </Transition>
        </div>

        <div v-if="taskSectionExpanded" class="task-list">
          <div v-for="thread in visibleGlobalThreads" :key="thread.id" class="thread-heading-row"
            :class="{ active: thread.id === activeThreadId }">
            <button class="thread-row" @click="emit('selectThread', thread.id)">
              <span>{{ thread.title }}</span>
              <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
            </button>
            <button class="icon-button compact row-menu-button" :aria-label="`${thread.title} 任务菜单`"
              @click.stop="toggleMenu(`thread:${thread.id}`, $event)">
              <MoreHorizontal :size="15" />
            </button>
            <Transition name="context-menu">
              <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu"
                :class="{ 'menu-up': menuPlacement === 'up' }">
                <button @click="runAction(() => emit('taskSettings', thread))">
                  <SlidersHorizontal :size="15" />任务设置
                </button>
                <button @click="runAction(() => emit('archiveTask', thread))">
                  <Archive :size="15" />归档任务
                </button>
                <button class="danger" @click="runAction(() => emit('deleteTask', thread))">
                  <Trash2 :size="15" />删除任务
                </button>
              </div>
            </Transition>
          </div>
          <p v-if="visibleGlobalThreads.length === 0" class="project-empty">无任务</p>
        </div>
      </section>

      <section class="sidebar-section project-section">
        <div class="section-heading">
          <span>项目</span>
          <button class="icon-button compact" title="添加项目" @click="emit('openWorkspace')">
            <Plus :size="15" />
          </button>
        </div>

        <div v-for="project in projects" :key="project.id" class="project-group">
          <div class="project-heading-row">
            <button class="project-row" :title="project.path" @click="toggleProject(project)">
              <Folder :size="18" />
              <span>{{ project.name }}</span>
              <LoaderCircle v-if="!expandedProjects.has(project.id) && projectHasRunningTask(project.id)"
                class="thread-running-spinner" :size="15" aria-label="项目中有任务正在运行" />
            </button>
            <button class="icon-button compact row-menu-button" :aria-label="`${project.name} 项目菜单`"
              @click.stop="toggleMenu(`project:${project.id}`, $event)">
              <MoreHorizontal :size="16" />
            </button>
            <Transition name="context-menu">
              <div v-if="menuKey === `project:${project.id}`" class="sidebar-context-menu project-menu"
                :class="{ 'menu-up': menuPlacement === 'up' }">
                <button @click="runAction(() => createTask(project.id))">
                  <SquarePen :size="15" />新建任务
                </button>
                <button @click="runAction(() => emit('initializeProjectWorkspace', project))">
                  <FolderCog :size="15" />初始化工作区元数据
                </button>
                <button @click="runAction(() => emit('archiveProjectTasks', project))">
                  <Archive :size="15" />归档全部任务
                </button>
                <button class="danger" @click="runAction(() => emit('deleteProjectTasks', project))">
                  <Trash2 :size="15" />删除全部任务
                </button>
                <button class="danger" @click="runAction(() => emit('deleteProject', project))">
                  <Trash2 :size="15" />删除项目
                </button>
              </div>
            </Transition>
          </div>

          <Transition name="project-collapse">
            <div v-if="expandedProjects.has(project.id)" class="project-tasks">
              <div v-for="thread in visibleThreads(project.id)" :key="thread.id" class="thread-heading-row"
                :class="{ active: thread.id === activeThreadId }">
                <button class="thread-row" @click="emit('selectThread', thread.id)">
                  <span>{{ thread.title }}</span>
                  <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
                </button>
                <button class="icon-button compact row-menu-button" :aria-label="`${thread.title} 任务菜单`"
                  @click.stop="toggleMenu(`thread:${thread.id}`, $event)">
                  <MoreHorizontal :size="15" />
                </button>
                <Transition name="context-menu">
                  <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu"
                    :class="{ 'menu-up': menuPlacement === 'up' }">
                    <button @click="runAction(() => emit('taskSettings', thread))">
                      <SlidersHorizontal :size="15" />任务设置
                    </button>
                    <button @click="runAction(() => emit('archiveTask', thread))">
                      <Archive :size="15" />归档任务
                    </button>
                    <button class="danger" @click="runAction(() => emit('deleteTask', thread))">
                      <Trash2 :size="15" />删除任务
                    </button>
                  </div>
                </Transition>
              </div>
              <p v-if="visibleThreads(project.id).length === 0" class="project-empty">
                {{ project.loaded === false ? '正在读取任务…' : '无任务' }}
              </p>
            </div>
          </Transition>
        </div>

        <p v-if="projects.length === 0" class="empty-recent">添加一个项目以开始任务</p>
      </section>
    </div>

    <footer class="sidebar-footer">
      <button class="account-row" @click="emit('openSettings')">
        <span class="account-copy">
          <strong>SeekClaw Desktop</strong>
          <small>v{{ version }}</small>
        </span>
        <Settings2 :size="17" />
      </button>
      <a class="icon-button" href="https://seekclaw.hoilai.com/doc/" target="_blank" title="帮助" rel="noreferrer">
        <CircleHelp :size="18" />
      </a>
    </footer>
  </aside>
</template>
