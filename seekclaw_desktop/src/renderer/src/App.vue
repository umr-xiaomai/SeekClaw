<script setup lang="ts">
import {
  Circle,
  Folder,
  FolderOpen,
  MoreHorizontal,
  PanelRight,
  RefreshCw
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import AppTitleBar from './components/AppTitleBar.vue'
import Composer from './components/Composer.vue'
import ConversationMessage from './components/ConversationMessage.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import TaskSettingsDialog from './components/TaskSettingsDialog.vue'
import type { ChatMessage, ProjectItem, ThreadItem } from './types'

const PROJECTS_STORAGE_KEY = 'seekclaw-projects-v2'
const makeId = (): string => crypto.randomUUID()
const pathName = (path: string): string => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path
const normalizePath = (path: string): string => path.replace(/\\/g, '/').replace(/\/$/, '').toLocaleLowerCase()
const samePath = (left?: string, right?: string): boolean => Boolean(left && right && normalizePath(left) === normalizePath(right))

interface RuntimeSessionHeader {
  id: string
  title?: string
  workspace?: string
  archived?: boolean
  createdAt: string
  updatedAt: string
}

interface RuntimeSession extends RuntimeSessionHeader {
  messages: Array<{
    role: 'user' | 'assistant' | 'tool'
    text: string
    thinking?: string
    toolCalls?: Array<{ id: string; name: string }>
    toolName?: string
    toolSuccess?: boolean
  }>
}

interface RuntimeWorkspace {
  path: string
  name: string
  mode: string
}

const appInfo = ref<AppInfo>({ version: '0.1.0', platform: 'win32', defaultWorkspace: '' })
const sidebarOpen = ref(true)
const settingsOpen = ref(false)
const settingsSection = ref<'general' | 'models' | 'mcp' | 'skills' | 'diagnostics'>('general')
const taskSettingsThreadId = ref('')
const theme = ref<'light' | 'dark'>((localStorage.getItem('seekclaw-theme') as 'light' | 'dark') || 'light')
const daemonState = ref<DaemonState>({ connected: false, endpoint: '' })
const projects = ref<ProjectItem[]>([])
const threads = ref<ThreadItem[]>([])
const activeThreadId = ref('')
const selectedProjectId = ref('')
const runtimeWorkspacePath = ref('')
const models = ref<string[]>([])
const activeModel = ref('balanced')
const mode = ref('edit')
const busy = ref(false)
const activeAssistantId = ref<string | null>(null)
const scrollArea = ref<HTMLElement | null>(null)
const composer = ref<InstanceType<typeof Composer> | null>(null)

const activeThread = computed(() => threads.value.find((thread) => thread.id === activeThreadId.value))
const activeProject = computed(() => {
  const projectId = activeThread.value?.projectId || selectedProjectId.value
  return projects.value.find((project) => project.id === projectId)
})
const settingsThread = computed(() => threads.value.find((thread) => thread.id === taskSettingsThreadId.value))
const settingsProject = computed(() => projects.value.find((project) => project.id === settingsThread.value?.projectId))
const conversationTitle = computed(() => activeThread.value?.title || '新任务')

let unsubscribeEvent: (() => void) | undefined
let unsubscribeState: (() => void) | undefined

function loadStoredProjects(): ProjectItem[] {
  try {
    const saved = JSON.parse(localStorage.getItem(PROJECTS_STORAGE_KEY) ?? '[]') as ProjectItem[]
    return saved.filter((project) => project?.id && project?.path).map((project) => ({
      id: project.id,
      name: project.name || pathName(project.path),
      path: project.path,
      loaded: false
    }))
  } catch {
    return []
  }
}

function persistProjects(): void {
  const saved = projects.value.map(({ id, name, path }) => ({ id, name, path }))
  localStorage.setItem(PROJECTS_STORAGE_KEY, JSON.stringify(saved))
}

function ensureProject(path: string, name?: string): ProjectItem {
  const existing = projects.value.find((project) => samePath(project.path, path))
  if (existing) {
    if (name) existing.name = name
    return existing
  }
  const project: ProjectItem = { id: makeId(), name: name || pathName(path), path, loaded: false }
  projects.value.push(project)
  return project
}

function showActiveProject(): void {
  if (activeProject.value) void window.seekclaw.showItemInFolder(activeProject.value.path)
}

function applyTheme(value: 'light' | 'dark'): void {
  theme.value = value
  document.documentElement.dataset.theme = value
  localStorage.setItem('seekclaw-theme', value)
}

async function scrollToBottom(smooth = false): Promise<void> {
  await nextTick()
  scrollArea.value?.scrollTo({ top: scrollArea.value.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
}

async function refreshProjectSessions(project: ProjectItem): Promise<void> {
  if (!daemonState.value.connected) return
  project.loaded = false
  try {
    const response = await window.seekclaw.daemon.request('session.list', {
      workspace: project.path,
      includeArchived: true
    })
    const savedSessions = JSON.parse(response.data) as RuntimeSessionHeader[]
    const sessionIds = new Set(savedSessions.map((session) => session.id))
    threads.value = threads.value.filter((thread) =>
      thread.projectId !== project.id || !thread.sessionId || sessionIds.has(thread.sessionId))

    for (const saved of savedSessions) {
      const existing = threads.value.find((thread) =>
        thread.projectId === project.id && thread.sessionId === saved.id)
      const fallbackTitle = `任务 ${new Date(saved.createdAt).toLocaleString()}`
      if (existing) {
        existing.title = saved.title || existing.title || fallbackTitle
        existing.updatedAt = new Date(saved.updatedAt).getTime()
        existing.archived = Boolean(saved.archived)
      } else {
        threads.value.push({
          id: `${project.id}:session:${saved.id}`,
          title: saved.title || fallbackTitle,
          projectId: project.id,
          updatedAt: new Date(saved.updatedAt).getTime(),
          messages: [],
          sessionId: saved.id,
          sessionLoaded: false,
          archived: Boolean(saved.archived)
        })
      }
    }
  } finally {
    project.loaded = true
  }
}

async function refreshAllProjectSessions(): Promise<void> {
  await Promise.all(projects.value.map((project) => refreshProjectSessions(project).catch(() => undefined)))
}

async function connectDaemon(): Promise<void> {
  daemonState.value = await window.seekclaw.daemon.connect()
  if (!daemonState.value.connected) return
  try {
    const [modelResponse, workspaceResponse, modeResponse, catalogResponse] = await Promise.all([
      window.seekclaw.daemon.request('model.list'),
      window.seekclaw.daemon.request('workspace.get'),
      window.seekclaw.daemon.request('agent.mode.get'),
      window.seekclaw.daemon.request('model.catalog')
    ])
    const available = JSON.parse(modelResponse.data) as string[]
    const catalog = JSON.parse(catalogResponse.data) as Array<{ ref: string; active: boolean }>
    const workspace = JSON.parse(workspaceResponse.data) as RuntimeWorkspace
    const currentProject = ensureProject(workspace.path, workspace.name)
    runtimeWorkspacePath.value = workspace.path
    selectedProjectId.value ||= currentProject.id
    models.value = available
    activeModel.value = catalog.find((model) => model.active)?.ref
      ?? (available.includes(activeModel.value) ? activeModel.value : available[0] ?? 'balanced')
    mode.value = modeResponse.data
    await refreshAllProjectSessions()

    if (!activeThread.value) {
      const recent = threads.value
        .filter((thread) => !thread.archived)
        .sort((left, right) => right.updatedAt - left.updatedAt)[0]
      if (recent) {
        activeThreadId.value = recent.id
        selectedProjectId.value = recent.projectId
      } else {
        newTask(currentProject.id)
      }
    }
  } catch {
    models.value = []
  }
}

async function reconnectDaemon(): Promise<void> {
  await window.seekclaw.daemon.disconnect()
  await connectDaemon()
}

async function openWorkspace(): Promise<void> {
  const path = await window.seekclaw.selectWorkspace()
  if (!path) return
  const project = ensureProject(path)
  selectedProjectId.value = project.id
  await refreshProjectSessions(project).catch(() => undefined)
  if (!threads.value.some((thread) => thread.projectId === project.id && !thread.archived)) newTask(project.id)
}

async function activateProject(project: ProjectItem): Promise<void> {
  selectedProjectId.value = project.id
  if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
}

function openSettings(section: typeof settingsSection.value = 'general'): void {
  settingsSection.value = section
  settingsOpen.value = true
}

function newTask(projectId?: string): void {
  const project = projects.value.find((item) => item.id === projectId)
    ?? projects.value.find((item) => item.id === selectedProjectId.value)
    ?? projects.value[0]
  if (!project) return
  const thread: ThreadItem = {
    id: makeId(),
    title: '新任务',
    projectId: project.id,
    updatedAt: Date.now(),
    messages: [],
    archived: false
  }
  threads.value.unshift(thread)
  selectedProjectId.value = project.id
  activeThreadId.value = thread.id
  void nextTick(() => composer.value?.focus())
}

async function ensureRuntimeProject(project: ProjectItem): Promise<void> {
  if (samePath(runtimeWorkspacePath.value, project.path)) return
  const response = await window.seekclaw.daemon.request('workspace.open', { path: project.path })
  const opened = JSON.parse(response.data) as RuntimeWorkspace
  project.path = opened.path
  project.name = opened.name || project.name
  runtimeWorkspacePath.value = opened.path
  mode.value = opened.mode
}

function hydrateMessages(saved: RuntimeSession): ChatMessage[] {
  const messages: ChatMessage[] = []
  saved.messages.forEach((item, index) => {
    if (item.role === 'tool') {
      const assistant = messages.findLast((message) => message.role === 'assistant')
      if (assistant && item.toolName) {
        assistant.tools ??= []
        assistant.tools.push({
          id: `${saved.id}:tool:${index}`,
          name: item.toolName,
          state: item.toolSuccess === false ? 'error' : 'done',
          detail: item.text
        })
      }
      return
    }
    messages.push({
      id: `${saved.id}:${index}`,
      role: item.role,
      content: item.text,
      thinking: item.thinking,
      tools: item.toolCalls?.map((call) => ({ id: call.id, name: call.name, state: 'done' })),
      state: item.role === 'assistant' ? 'done' : undefined,
      createdAt: new Date(saved.createdAt).getTime() + index
    })
  })
  return messages
}

async function selectThread(id: string): Promise<void> {
  const thread = threads.value.find((item) => item.id === id)
  if (!thread || busy.value) return
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!project) return
  activeThreadId.value = id
  selectedProjectId.value = project.id
  try {
    await ensureRuntimeProject(project)
    if (thread.sessionId && !thread.archived)
      await window.seekclaw.daemon.request('session.resume', { id: thread.sessionId })
    if (thread.sessionId && !thread.sessionLoaded) {
      const response = await window.seekclaw.daemon.request('session.get', {
        id: thread.sessionId,
        workspace: project.path
      })
      const saved = JSON.parse(response.data) as RuntimeSession
      thread.messages = hydrateMessages(saved)
      thread.sessionLoaded = true
      thread.title = saved.title || thread.title
      thread.archived = Boolean(saved.archived)
    }
  } catch {
    thread.sessionLoaded = false
  }
  await scrollToBottom()
}

function updateThreadTitle(thread: ThreadItem, prompt: string): boolean {
  if (thread.title !== '新任务') return false
  thread.title = prompt.length > 42 ? `${prompt.slice(0, 42)}…` : prompt
  return true
}

async function sendMessage(content: string): Promise<void> {
  const thread = activeThread.value
  const project = activeProject.value
  if (!thread || !project || thread.archived || busy.value) return

  const userMessage: ChatMessage = { id: makeId(), role: 'user', content, createdAt: Date.now() }
  const assistant: ChatMessage = {
    id: makeId(), role: 'assistant', content: '', thinking: '', tools: [], state: 'thinking', createdAt: Date.now()
  }
  thread.messages.push(userMessage, assistant)
  thread.updatedAt = Date.now()
  const titleChanged = updateThreadTitle(thread, content)
  activeAssistantId.value = assistant.id
  busy.value = true
  await scrollToBottom(true)

  try {
    await ensureRuntimeProject(project)
    let sessionCreated = false
    if (!thread.sessionId) {
      const sessionResponse = await window.seekclaw.daemon.request('session.new')
      thread.sessionId = sessionResponse.data
      thread.sessionLoaded = true
      sessionCreated = true
    } else {
      await window.seekclaw.daemon.request('session.resume', { id: thread.sessionId })
    }
    if (sessionCreated || titleChanged) {
      await window.seekclaw.daemon.request('session.update', {
        id: thread.sessionId,
        workspace: project.path,
        title: thread.title
      })
    }
    await window.seekclaw.daemon.request('chat', { message: content })
    assistant.state = 'done'
  } catch (error) {
    assistant.state = 'error'
    if (!assistant.content) {
      const detail = error instanceof Error ? error.message : String(error)
      assistant.content = `无法连接 SeekClaw Daemon。\n\n\`\`\`text\n${detail}\n\`\`\``
    }
  } finally {
    busy.value = false
    activeAssistantId.value = null
    await scrollToBottom(true)
  }
}

function openTaskSettings(thread: ThreadItem = activeThread.value!): void {
  if (thread) taskSettingsThreadId.value = thread.id
}

async function saveTaskTitle(title: string): Promise<void> {
  const thread = settingsThread.value
  const project = settingsProject.value
  if (!thread || !project) return
  thread.title = title
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.update', {
      id: thread.sessionId,
      workspace: project.path,
      title
    })
  }
  taskSettingsThreadId.value = ''
}

function chooseAfterRemoval(projectId: string): void {
  const fallback = threads.value
    .filter((thread) => thread.projectId === projectId && !thread.archived)
    .sort((left, right) => right.updatedAt - left.updatedAt)[0]
  if (fallback) {
    activeThreadId.value = fallback.id
    return
  }
  newTask(projectId)
}

async function archiveTask(thread: ThreadItem): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!project || busy.value) return
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.archive', {
      id: thread.sessionId,
      workspace: project.path,
      archived: true
    })
    thread.archived = true
  } else {
    threads.value = threads.value.filter((item) => item.id !== thread.id)
  }
  taskSettingsThreadId.value = ''
  if (activeThreadId.value === thread.id) chooseAfterRemoval(project.id)
}

async function restoreTask(thread: ThreadItem): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!project || !thread.sessionId || busy.value) return
  await window.seekclaw.daemon.request('session.archive', {
    id: thread.sessionId,
    workspace: project.path,
    archived: false
  })
  thread.archived = false
  thread.updatedAt = Date.now()
  taskSettingsThreadId.value = ''
}

async function archiveProjectTasks(project: ProjectItem): Promise<void> {
  if (busy.value) return
  if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
  const targets = threads.value.filter((thread) => thread.projectId === project.id && !thread.archived)
  if (targets.length === 0) return
  if (!window.confirm(`归档项目“${project.name}”的全部 ${targets.length} 个任务？`)) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  busy.value = true
  try {
    for (const thread of targets) {
      if (thread.sessionId) {
        await window.seekclaw.daemon.request('session.archive', {
          id: thread.sessionId,
          workspace: project.path,
          archived: true
        })
        thread.archived = true
      } else {
        threads.value = threads.value.filter((item) => item.id !== thread.id)
      }
    }
    if (activeAffected) chooseAfterRemoval(project.id)
  } finally {
    busy.value = false
  }
}

async function deleteTask(thread: ThreadItem): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!project || busy.value) return
  if (!window.confirm(`永久删除任务“${thread.title}”？此操作无法撤销。`)) return
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.delete', {
      id: thread.sessionId,
      workspace: project.path
    })
  }
  threads.value = threads.value.filter((item) => item.id !== thread.id)
  taskSettingsThreadId.value = ''
  if (activeThreadId.value === thread.id) chooseAfterRemoval(project.id)
}

async function deleteProjectTasks(project: ProjectItem): Promise<void> {
  if (busy.value) return
  if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
  const targets = threads.value.filter((thread) => thread.projectId === project.id)
  if (targets.length === 0) return
  if (!window.confirm(`永久删除项目“${project.name}”的全部 ${targets.length} 个任务？此操作无法撤销。`)) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  busy.value = true
  try {
    for (const thread of targets) {
      if (thread.sessionId) {
        await window.seekclaw.daemon.request('session.delete', {
          id: thread.sessionId,
          workspace: project.path
        })
      }
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeAffected) chooseAfterRemoval(project.id)
  } finally {
    busy.value = false
  }
}

async function deleteArchivedTasks(): Promise<void> {
  if (busy.value) return
  const targets = threads.value.filter((thread) => thread.archived)
  if (targets.length === 0) return
  if (!window.confirm(`永久删除全部 ${targets.length} 个已归档任务？此操作无法撤销。`)) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  busy.value = true
  try {
    for (const thread of targets) {
      const project = projects.value.find((item) => item.id === thread.projectId)
      if (thread.sessionId && project) {
        await window.seekclaw.daemon.request('session.delete', {
          id: thread.sessionId,
          workspace: project.path
        })
      }
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeAffected) {
      activeThreadId.value = ''
      const project = projects.value.find((item) => item.id === selectedProjectId.value) ?? projects.value[0]
      if (project) chooseAfterRemoval(project.id)
    }
  } finally {
    busy.value = false
  }
}

function deleteProject(project: ProjectItem): void {
  if (busy.value) return
  if (!window.confirm(`从 SeekClaw 中移除项目“${project.name}”？项目文件和任务记录不会从磁盘删除。`)) return
  projects.value = projects.value.filter((item) => item.id !== project.id)
  threads.value = threads.value.filter((thread) => thread.projectId !== project.id)
  if (selectedProjectId.value === project.id) selectedProjectId.value = projects.value[0]?.id ?? ''
  if (!activeThread.value) {
    activeThreadId.value = ''
    if (selectedProjectId.value) chooseAfterRemoval(selectedProjectId.value)
  }
}

function handleDaemonEvent(event: DaemonMessage): void {
  if (event.requestMethod !== 'chat'
      && event.requestMethod !== 'agent.runTurn'
      && event.requestMethod !== 'agent/runTurn') return
  if (!activeAssistantId.value) return
  const message = activeThread.value?.messages.find((item) => item.id === activeAssistantId.value)
  if (!message) return

  switch (event.event) {
    case 'thinking':
      message.thinking = (message.thinking ?? '') + event.data
      message.state = 'thinking'
      break
    case 'delta':
      message.content += event.data
      message.state = 'streaming'
      break
    case 'status':
      if (event.data.toLocaleLowerCase().includes('thinking')) message.state = 'thinking'
      break
    case 'tool_start':
      message.tools?.push({ id: `${event.id}-${message.tools.length}`, name: event.data, state: 'running' })
      break
    case 'tool_done': {
      const running = message.tools?.findLast((tool) => tool.state === 'running')
      if (running) {
        running.state = 'done'
        running.detail = event.data
      }
      break
    }
    case 'done':
    case 'cancelled':
      message.state = 'done'
      if (!message.content && event.data) message.content = event.data
      break
    case 'error':
      message.state = 'error'
      if (!message.content) message.content = event.data
      break
  }
  void scrollToBottom()
}

async function stopTurn(): Promise<void> {
  try { await window.seekclaw.daemon.request('agent.cancel') } catch { /* sendMessage owns the final state */ }
}

async function changeModel(model: string): Promise<void> {
  activeModel.value = model
  if (!daemonState.value.connected || model === 'balanced') return
  try { await window.seekclaw.daemon.request('model.switch', { model }) }
  catch { daemonState.value = { ...daemonState.value, connected: false } }
}

async function changeMode(nextMode: string): Promise<void> {
  if (nextMode === mode.value) return
  try {
    const response = await window.seekclaw.daemon.request('agent.mode.switch', { mode: nextMode })
    mode.value = response.data
  } catch { /* Keep showing the active Runtime mode. */ }
}

onMounted(async () => {
  applyTheme(theme.value)
  appInfo.value = await window.seekclaw.getAppInfo()
  projects.value = loadStoredProjects()
  const defaultProject = ensureProject(appInfo.value.defaultWorkspace)
  selectedProjectId.value = defaultProject.id
  unsubscribeEvent = window.seekclaw.daemon.onEvent(handleDaemonEvent)
  unsubscribeState = window.seekclaw.daemon.onState((state) => { daemonState.value = state })
  await connectDaemon()
  if (!daemonState.value.connected) projects.value.forEach((project) => { project.loaded = true })
  if (!activeThread.value) newTask(defaultProject.id)
  composer.value?.focus()
})

onBeforeUnmount(() => {
  unsubscribeEvent?.()
  unsubscribeState?.()
})

watch(theme, applyTheme)
watch(projects, persistProjects, { deep: true })
</script>

<template>
  <div class="app-shell">
    <AppTitleBar
      :sidebar-open="sidebarOpen"
      @toggle-sidebar="sidebarOpen = !sidebarOpen"
      @open-workspace="openWorkspace"
      @focus-composer="composer?.focus()"
    />

    <div class="app-body" :class="{ 'sidebar-collapsed': !sidebarOpen }">
      <Sidebar
        v-if="sidebarOpen"
        :projects="projects"
        :threads="threads"
        :active-thread-id="activeThreadId"
        :active-project-id="selectedProjectId"
        :version="appInfo.version"
        @new-task="newTask"
        @open-workspace="openWorkspace"
        @select-thread="selectThread"
        @select-project="activateProject"
        @task-settings="openTaskSettings"
        @archive-task="archiveTask"
        @restore-task="restoreTask"
        @delete-task="deleteTask"
        @delete-project="deleteProject"
        @archive-project-tasks="archiveProjectTasks"
        @delete-project-tasks="deleteProjectTasks"
        @delete-archived-tasks="deleteArchivedTasks"
        @open-extensions="openSettings('mcp')"
        @open-settings="openSettings('general')"
      />
      <button v-if="sidebarOpen" class="sidebar-scrim" title="关闭侧栏" @click="sidebarOpen = false" />

      <main class="workspace-main">
        <header class="conversation-header">
          <div class="conversation-title">
            <Folder :size="20" />
            <strong>{{ conversationTitle }}</strong>
            <small v-if="activeProject">{{ activeProject.name }}</small>
          </div>
          <div class="conversation-actions">
            <button
              class="connection-button"
              :class="{ connected: daemonState.connected }"
              :title="daemonState.error || daemonState.endpoint"
              @click="reconnectDaemon"
            >
              <Circle :size="9" fill="currentColor" />
              {{ daemonState.connected ? 'Runtime 已连接' : 'Runtime 离线' }}
              <RefreshCw v-if="!daemonState.connected" :size="14" />
            </button>
            <button class="open-location-button" :disabled="!activeProject" @click="showActiveProject">
              <FolderOpen :size="17" />
              <span>打开位置</span>
            </button>
            <button class="icon-button" title="任务设置" :disabled="!activeThread" @click="openTaskSettings()">
              <MoreHorizontal :size="18" />
            </button>
            <button class="icon-button" title="切换侧栏" @click="sidebarOpen = !sidebarOpen"><PanelRight :size="18" /></button>
          </div>
        </header>

        <section ref="scrollArea" class="conversation-scroll">
          <div v-if="activeThread && activeThread.messages.length > 0" class="conversation-content">
            <ConversationMessage v-for="message in activeThread.messages" :key="message.id" :message="message" />
          </div>
          <div v-else class="empty-state">
            <h1>今天从哪里开始？</h1>
            <p>{{ activeProject?.name }}</p>
          </div>
        </section>

        <footer class="composer-region">
          <Composer
            ref="composer"
            :busy="busy"
            :disabled="activeThread?.archived"
            :model="activeModel"
            :models="models"
            :mode="mode"
            @send="sendMessage"
            @stop="stopTurn"
            @attach="openWorkspace"
            @change-model="changeModel"
            @change-mode="changeMode"
          />
          <p class="composer-caption">
            {{ activeThread?.archived ? '此任务已归档，恢复后可继续。' : 'SeekClaw 可能会出错，请检查生成的代码和命令。' }}
          </p>
        </footer>
      </main>
    </div>

    <SettingsDialog
      :open="settingsOpen"
      :theme="theme"
      :daemon-connected="daemonState.connected"
      :daemon-endpoint="daemonState.endpoint"
      :workspace-path="activeProject?.path || appInfo.defaultWorkspace"
      :initial-section="settingsSection"
      @close="settingsOpen = false"
      @change-theme="applyTheme"
      @reconnect="reconnectDaemon"
      @open-workspace="openWorkspace"
      @runtime-changed="connectDaemon"
    />

    <TaskSettingsDialog
      :open="Boolean(taskSettingsThreadId)"
      :thread="settingsThread"
      :project="settingsProject"
      @close="taskSettingsThreadId = ''"
      @save-title="saveTaskTitle"
      @archive="settingsThread && archiveTask(settingsThread)"
      @restore="settingsThread && restoreTask(settingsThread)"
      @delete="settingsThread && deleteTask(settingsThread)"
    />
  </div>
</template>
