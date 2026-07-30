<script setup lang="ts">
import {
  Archive,
  Blocks,
  ChevronDown,
  ChevronRight,
  CircleHelp,
  Folder,
  MoreHorizontal,
  Plus,
  RotateCcw,
  Search,
  Settings2,
  SlidersHorizontal,
  SquarePen,
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
  selectProject: [project: ProjectItem]
  taskSettings: [thread: ThreadItem]
  archiveTask: [thread: ThreadItem]
  restoreTask: [thread: ThreadItem]
  deleteTask: [thread: ThreadItem]
  deleteProject: [project: ProjectItem]
  archiveProjectTasks: [project: ProjectItem]
  deleteProjectTasks: [project: ProjectItem]
  deleteArchivedTasks: []
  openExtensions: []
  openSettings: []
}>()

const searching = ref(false)
const query = ref('')
const showArchived = ref(false)
const expandedProjects = ref(new Set<string>())
const menuKey = ref('')

watch(() => props.projects, (items) => {
  const next = new Set(expandedProjects.value)
  items.forEach((project) => next.add(project.id))
  expandedProjects.value = next
}, { immediate: true, deep: true })

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())

function visibleThreads(projectId: string): ThreadItem[] {
  return props.threads
    .filter((thread) => thread.projectId === projectId && Boolean(thread.archived) === showArchived.value)
    .filter((thread) => !normalizedQuery.value || thread.title.toLocaleLowerCase().includes(normalizedQuery.value))
    .sort((left, right) => right.updatedAt - left.updatedAt)
}

function toggleProject(project: ProjectItem): void {
  const next = new Set(expandedProjects.value)
  if (next.has(project.id)) next.delete(project.id)
  else next.add(project.id)
  expandedProjects.value = next
  emit('selectProject', project)
}

function toggleMenu(key: string): void {
  menuKey.value = menuKey.value === key ? '' : key
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
  <aside class="sidebar" @click.self="menuKey = ''">
    <div class="sidebar-brand-row">
      <div class="brand-lockup">
        <img :src="logoUrl" alt="" />
        <span>SeekClaw</span>
      </div>
      <button class="icon-button" title="搜索任务" @click="searching = !searching">
        <Search :size="18" />
      </button>
    </div>

    <div v-if="searching" class="sidebar-search">
      <Search :size="15" />
      <input v-model="query" autofocus placeholder="搜索所有项目中的任务" />
    </div>

    <nav class="primary-nav">
      <button class="nav-item is-primary" @click="emit('newTask', activeProjectId)">
        <SquarePen :size="19" />
        <span>新建任务</span>
      </button>
      <button class="nav-item" :class="{ active: showArchived }" @click="showArchived = !showArchived">
        <Archive :size="19" />
        <span>{{ showArchived ? '返回任务' : '已归档' }}</span>
      </button>
      <button class="nav-item" @click="emit('openExtensions')">
        <Blocks :size="19" />
        <span>MCP 与技能</span>
      </button>
    </nav>

    <section class="sidebar-section project-section">
      <div class="section-heading">
        <span>{{ showArchived ? '已归档任务' : '项目' }}</span>
        <button
          v-if="showArchived"
          class="icon-button compact"
          title="删除全部归档任务"
          :disabled="!threads.some((thread) => thread.archived)"
          @click="emit('deleteArchivedTasks')"
        >
          <Trash2 :size="15" />
        </button>
        <button v-else class="icon-button compact" title="添加项目" @click="emit('openWorkspace')">
          <Plus :size="15" />
        </button>
      </div>

      <div v-for="project in projects" :key="project.id" class="project-group">
        <div class="project-heading-row" :class="{ active: project.id === activeProjectId }">
          <button class="project-row" :title="project.path" @click="toggleProject(project)">
            <ChevronDown v-if="expandedProjects.has(project.id)" :size="15" class="project-chevron" />
            <ChevronRight v-else :size="15" class="project-chevron" />
            <Folder :size="18" />
            <span>{{ project.name }}</span>
          </button>
          <button
            class="icon-button compact row-menu-button"
            :aria-label="`${project.name} 项目菜单`"
            @click.stop="toggleMenu(`project:${project.id}`)"
          >
            <MoreHorizontal :size="16" />
          </button>
          <div v-if="menuKey === `project:${project.id}`" class="sidebar-context-menu project-menu">
            <button @click="runAction(() => emit('newTask', project.id))"><SquarePen :size="15" />新建任务</button>
            <button
              v-if="!showArchived"
              @click="runAction(() => emit('archiveProjectTasks', project))"
            ><Archive :size="15" />归档全部任务</button>
            <button class="danger" @click="runAction(() => emit('deleteProjectTasks', project))"><Trash2 :size="15" />删除全部任务</button>
            <button @click="runAction(() => emit('deleteProject', project))"><Trash2 :size="15" />移除项目</button>
          </div>
        </div>

        <div v-if="expandedProjects.has(project.id)" class="project-tasks">
          <div
            v-for="thread in visibleThreads(project.id)"
            :key="thread.id"
            class="thread-heading-row"
            :class="{ active: thread.id === activeThreadId }"
          >
            <button class="thread-row" @click="emit('selectThread', thread.id)">
              <span>{{ thread.title }}</span>
            </button>
            <button
              class="icon-button compact row-menu-button"
              :aria-label="`${thread.title} 任务菜单`"
              @click.stop="toggleMenu(`thread:${thread.id}`)"
            >
              <MoreHorizontal :size="15" />
            </button>
            <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu">
              <button @click="runAction(() => emit('taskSettings', thread))"><SlidersHorizontal :size="15" />任务设置</button>
              <button v-if="thread.archived" @click="runAction(() => emit('restoreTask', thread))"><RotateCcw :size="15" />恢复任务</button>
              <button v-else @click="runAction(() => emit('archiveTask', thread))"><Archive :size="15" />归档任务</button>
              <button class="danger" @click="runAction(() => emit('deleteTask', thread))"><Trash2 :size="15" />删除任务</button>
            </div>
          </div>
          <p v-if="visibleThreads(project.id).length === 0" class="project-empty">
            {{ project.loaded === false ? '正在读取任务…' : '无任务' }}
          </p>
        </div>
      </div>

      <p v-if="projects.length === 0" class="empty-recent">添加一个项目以开始任务</p>
    </section>

    <footer class="sidebar-footer">
      <button class="account-row" @click="emit('openSettings')">
        <span class="account-avatar">S</span>
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
