<script setup lang="ts">
import {
  Archive,
  Blocks,
  CalendarClock,
  CircleHelp,
  Folder,
  Globe2,
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
const globalExpanded = ref(true)
const expandedProjects = ref(new Set<string>())
const menuKey = ref('')
const menuPlacement = ref<'up' | 'down'>('down')

watch(() => props.projects, (items) => {
  const next = new Set(expandedProjects.value)
  items.forEach((project) => next.add(project.id))
  expandedProjects.value = next
}, { immediate: true, deep: true })

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())

function visibleThreads(projectId: string): ThreadItem[] {
  return props.threads
    .filter((thread) => thread.projectId === projectId && !thread.archived)
    .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
    .sort((left, right) => right.updatedAt - left.updatedAt)
}

const visibleGlobalThreads = computed(() => props.threads
  .filter((thread) => !thread.projectId && !thread.archived)
  .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
  .sort((left, right) => right.updatedAt - left.updatedAt))

function toggleGlobal(): void {
  globalExpanded.value = !globalExpanded.value
}

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
  const section = trigger?.closest<HTMLElement>('.project-section')
  const triggerRect = trigger?.getBoundingClientRect()
  const rowRect = row?.getBoundingClientRect()
  const sectionRect = section?.getBoundingClientRect()
  const menuHeight = 154
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
        <input v-model="query" autofocus placeholder="搜索所有项目中的任务" />
      </div>
    </Transition>

    <nav class="primary-nav">
      <button class="nav-item is-primary" @click="emit('newTask', activeProjectId)">
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

    <section class="sidebar-section project-section">
      <div class="section-heading">
        <span>项目</span>
        <button class="icon-button compact" title="添加项目" @click="emit('openWorkspace')">
          <Plus :size="15" />
        </button>
      </div>

      <div class="project-group global-task-group">
        <div class="project-heading-row">
          <button class="project-row" title="不绑定工作目录的任务" @click="toggleGlobal">
            <Globe2 :size="17" />
            <span>全局任务</span>
          </button>
          <button
            class="icon-button compact row-menu-button"
            aria-label="全局任务菜单"
            @click.stop="toggleMenu('global', $event)"
          >
            <MoreHorizontal :size="16" />
          </button>
          <Transition name="context-menu">
            <div v-if="menuKey === 'global'" class="sidebar-context-menu project-menu" :class="{ 'menu-up': menuPlacement === 'up' }">
              <button @click="runAction(() => emit('newTask'))"><SquarePen :size="15" />新建全局任务</button>
              <button @click="runAction(() => emit('archiveGlobalTasks'))"><Archive :size="15" />归档全部任务</button>
              <button class="danger" @click="runAction(() => emit('deleteGlobalTasks'))"><Trash2 :size="15" />删除全部任务</button>
            </div>
          </Transition>
        </div>

        <Transition name="project-collapse">
          <div v-if="globalExpanded" class="project-tasks">
            <div
              v-for="thread in visibleGlobalThreads"
              :key="thread.id"
              class="thread-heading-row"
              :class="{ active: thread.id === activeThreadId }"
            >
              <button class="thread-row" @click="emit('selectThread', thread.id)">
                <span>{{ thread.title }}</span>
                <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
              </button>
              <button
                class="icon-button compact row-menu-button"
                :aria-label="`${thread.title} 任务菜单`"
                @click.stop="toggleMenu(`thread:${thread.id}`, $event)"
              ><MoreHorizontal :size="15" /></button>
              <Transition name="context-menu">
                <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu" :class="{ 'menu-up': menuPlacement === 'up' }">
                  <button @click="runAction(() => emit('taskSettings', thread))"><SlidersHorizontal :size="15" />任务设置</button>
                  <button @click="runAction(() => emit('archiveTask', thread))"><Archive :size="15" />归档任务</button>
                  <button class="danger" @click="runAction(() => emit('deleteTask', thread))"><Trash2 :size="15" />删除任务</button>
                </div>
              </Transition>
            </div>
            <p v-if="visibleGlobalThreads.length === 0" class="project-empty">无任务</p>
          </div>
        </Transition>
      </div>

      <div v-for="project in projects" :key="project.id" class="project-group">
        <div class="project-heading-row">
          <button class="project-row" :title="project.path" @click="toggleProject(project)">
            <Folder :size="18" />
            <span>{{ project.name }}</span>
          </button>
          <button
            class="icon-button compact row-menu-button"
            :aria-label="`${project.name} 项目菜单`"
            @click.stop="toggleMenu(`project:${project.id}`, $event)"
          >
            <MoreHorizontal :size="16" />
          </button>
          <Transition name="context-menu">
            <div v-if="menuKey === `project:${project.id}`" class="sidebar-context-menu project-menu" :class="{ 'menu-up': menuPlacement === 'up' }">
              <button @click="runAction(() => emit('newTask', project.id))"><SquarePen :size="15" />新建任务</button>
              <button
                @click="runAction(() => emit('archiveProjectTasks', project))"
              ><Archive :size="15" />归档全部任务</button>
              <button class="danger" @click="runAction(() => emit('deleteProjectTasks', project))"><Trash2 :size="15" />删除全部任务</button>
              <button class="danger" @click="runAction(() => emit('deleteProject', project))"><Trash2 :size="15" />删除项目</button>
            </div>
          </Transition>
        </div>

        <Transition name="project-collapse">
          <div v-if="expandedProjects.has(project.id)" class="project-tasks">
            <div
              v-for="thread in visibleThreads(project.id)"
              :key="thread.id"
              class="thread-heading-row"
              :class="{ active: thread.id === activeThreadId }"
            >
              <button class="thread-row" @click="emit('selectThread', thread.id)">
                <span>{{ thread.title }}</span>
                <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
              </button>
              <button
                class="icon-button compact row-menu-button"
                :aria-label="`${thread.title} 任务菜单`"
                @click.stop="toggleMenu(`thread:${thread.id}`, $event)"
              >
                <MoreHorizontal :size="15" />
              </button>
              <Transition name="context-menu">
                <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu" :class="{ 'menu-up': menuPlacement === 'up' }">
                  <button @click="runAction(() => emit('taskSettings', thread))"><SlidersHorizontal :size="15" />任务设置</button>
                  <button @click="runAction(() => emit('archiveTask', thread))"><Archive :size="15" />归档任务</button>
                  <button class="danger" @click="runAction(() => emit('deleteTask', thread))"><Trash2 :size="15" />删除任务</button>
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
