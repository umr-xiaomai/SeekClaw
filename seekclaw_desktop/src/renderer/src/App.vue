<script setup lang="ts">
import {
  Circle,
  Folder,
  FolderOpen,
  PanelRight,
  RefreshCw
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import logoUrl from '../../../resources/logo.svg?url'
import type { AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import AppTitleBar from './components/AppTitleBar.vue'
import Composer from './components/Composer.vue'
import ConversationMessage from './components/ConversationMessage.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import type { ChatMessage, ProjectItem, ThreadItem } from './types'

const makeId = (): string => crypto.randomUUID()
const pathName = (path: string): string => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path

const appInfo = ref<AppInfo>({ version: '0.1.0', platform: 'win32', defaultWorkspace: '' })
const sidebarOpen = ref(true)
const settingsOpen = ref(false)
const theme = ref<'light' | 'dark'>((localStorage.getItem('seekclaw-theme') as 'light' | 'dark') || 'light')
const daemonState = ref<DaemonState>({ connected: false, endpoint: '' })
const projects = ref<ProjectItem[]>([])
const threads = ref<ThreadItem[]>([
  { id: makeId(), title: '新任务', projectId: 'default', updatedAt: Date.now(), messages: [] }
])
const activeThreadId = ref(threads.value[0]!.id)
const models = ref<string[]>([])
const activeModel = ref('balanced')
const mode = ref('edit')
const busy = ref(false)
const activeAssistantId = ref<string | null>(null)
const scrollArea = ref<HTMLElement | null>(null)
const composer = ref<InstanceType<typeof Composer> | null>(null)

const activeThread = computed(() => threads.value.find((thread) => thread.id === activeThreadId.value) ?? threads.value[0])
const activeProject = computed(() => projects.value.find((project) => project.id === activeThread.value?.projectId) ?? projects.value[0])
const conversationTitle = computed(() => activeThread.value?.title || '新任务')

function showActiveProject(): void {
  if (activeProject.value) void window.seekclaw.showItemInFolder(activeProject.value.path)
}

let unsubscribeEvent: (() => void) | undefined
let unsubscribeState: (() => void) | undefined

function applyTheme(value: 'light' | 'dark'): void {
  theme.value = value
  document.documentElement.dataset.theme = value
  localStorage.setItem('seekclaw-theme', value)
}

async function scrollToBottom(smooth = false): Promise<void> {
  await nextTick()
  scrollArea.value?.scrollTo({ top: scrollArea.value.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
}

async function connectDaemon(): Promise<void> {
  daemonState.value = await window.seekclaw.daemon.connect()
  if (!daemonState.value.connected) return
  try {
    const response = await window.seekclaw.daemon.request('model.list')
    const available = JSON.parse(response.data) as string[]
    models.value = available
    if (available.length > 0 && !available.includes(activeModel.value)) activeModel.value = available[0]!
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
  if (!path || projects.value.some((project) => project.path === path)) return
  const project: ProjectItem = { id: makeId(), name: pathName(path), path }
  projects.value.push(project)
  if (activeThread.value) activeThread.value.projectId = project.id
}

function newTask(): void {
  const thread: ThreadItem = {
    id: makeId(),
    title: '新任务',
    projectId: activeProject.value?.id ?? 'default',
    updatedAt: Date.now(),
    messages: []
  }
  threads.value.unshift(thread)
  activeThreadId.value = thread.id
  void nextTick(() => composer.value?.focus())
}

function selectThread(id: string): void {
  activeThreadId.value = id
  void scrollToBottom()
}

function updateThreadTitle(thread: ThreadItem, prompt: string): void {
  if (thread.title !== '新任务') return
  thread.title = prompt.length > 28 ? `${prompt.slice(0, 28)}…` : prompt
}

async function sendMessage(content: string): Promise<void> {
  const thread = activeThread.value
  if (!thread || busy.value) return

  const userMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content,
    createdAt: Date.now()
  }
  const assistant: ChatMessage = {
    id: makeId(),
    role: 'assistant',
    content: '',
    thinking: '',
    tools: [],
    state: 'thinking',
    createdAt: Date.now()
  }
  thread.messages.push(userMessage, assistant)
  thread.updatedAt = Date.now()
  updateThreadTitle(thread, content)
  activeAssistantId.value = assistant.id
  busy.value = true
  await scrollToBottom(true)

  try {
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

function handleDaemonEvent(event: DaemonMessage): void {
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

function stopTurn(): void {
  const message = activeThread.value?.messages.find((item) => item.id === activeAssistantId.value)
  if (message) message.state = 'done'
  busy.value = false
  activeAssistantId.value = null
  void window.seekclaw.daemon.disconnect()
}

async function changeModel(model: string): Promise<void> {
  activeModel.value = model
  if (!daemonState.value.connected || model === 'balanced') return
  try {
    await window.seekclaw.daemon.request('model.switch', { model })
  } catch {
    daemonState.value = { ...daemonState.value, connected: false }
  }
}

onMounted(async () => {
  applyTheme(theme.value)
  appInfo.value = await window.seekclaw.getAppInfo()
  const workspace = appInfo.value.defaultWorkspace
  projects.value = [{ id: 'default', name: pathName(workspace), path: workspace }]
  unsubscribeEvent = window.seekclaw.daemon.onEvent(handleDaemonEvent)
  unsubscribeState = window.seekclaw.daemon.onState((state) => { daemonState.value = state })
  await connectDaemon()
  composer.value?.focus()
})

onBeforeUnmount(() => {
  unsubscribeEvent?.()
  unsubscribeState?.()
})

watch(theme, applyTheme)
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
        :version="appInfo.version"
        @new-task="newTask"
        @open-workspace="openWorkspace"
        @select-thread="selectThread"
        @open-settings="settingsOpen = true"
      />
      <button v-if="sidebarOpen" class="sidebar-scrim" title="关闭侧栏" @click="sidebarOpen = false" />

      <main class="workspace-main">
        <header class="conversation-header">
          <div class="conversation-title">
            <Folder :size="20" />
            <strong>{{ conversationTitle }}</strong>
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
            <button
              class="open-location-button"
              :disabled="!activeProject"
              @click="showActiveProject"
            >
              <FolderOpen :size="17" />
              <span>打开位置</span>
            </button>
            <button class="icon-button" title="切换侧栏" @click="sidebarOpen = !sidebarOpen"><PanelRight :size="18" /></button>
          </div>
        </header>

        <section ref="scrollArea" class="conversation-scroll">
          <div v-if="activeThread && activeThread.messages.length > 0" class="conversation-content">
            <ConversationMessage
              v-for="message in activeThread.messages"
              :key="message.id"
              :message="message"
            />
          </div>
          <div v-else class="empty-state">
            <img :src="logoUrl" alt="SeekClaw" />
            <h1>今天从哪里开始？</h1>
            <p>{{ activeProject?.name }}</p>
          </div>
        </section>

        <footer class="composer-region">
          <Composer
            ref="composer"
            :busy="busy"
            :model="activeModel"
            :models="models"
            :mode="mode"
            @send="sendMessage"
            @stop="stopTurn"
            @attach="openWorkspace"
            @change-model="changeModel"
            @change-mode="mode = $event"
          />
          <p class="composer-caption">SeekClaw 可能会出错，请检查生成的代码和命令。</p>
        </footer>
      </main>
    </div>

    <SettingsDialog
      :open="settingsOpen"
      :theme="theme"
      :daemon-connected="daemonState.connected"
      :daemon-endpoint="daemonState.endpoint"
      @close="settingsOpen = false"
      @change-theme="applyTheme"
      @reconnect="reconnectDaemon"
    />
  </div>
</template>
