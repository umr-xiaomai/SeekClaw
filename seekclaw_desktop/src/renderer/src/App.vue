<script setup lang="ts">
import {
  Braces,
  Bug,
  Circle,
  Folder,
  FolderOpen,
  Globe2,
  Hammer,
  History,
  MoreHorizontal,
  PanelRight,
  RefreshCw,
  Telescope,
  TerminalSquare
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { AppearanceTheme, AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import AppTitleBar from './components/AppTitleBar.vue'
import ArchivedTasksDialog from './components/ArchivedTasksDialog.vue'
import Composer from './components/Composer.vue'
import ConfirmDialog from './components/ConfirmDialog.vue'
import ConversationMessage from './components/ConversationMessage.vue'
import GitWorkspacePanel from './components/GitWorkspacePanel.vue'
import RuntimeReconnectDialog from './components/RuntimeReconnectDialog.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import TaskSettingsDialog from './components/TaskSettingsDialog.vue'
import { confirmAction } from './confirmation'
import { retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'
import { ReasoningLevel } from './types'
import type { ChatMessage, ImageAttachment, ProjectItem, ThreadItem, ToolActivity } from './types'

const PROJECTS_STORAGE_KEY = 'seekclaw-projects-v2'
const IMPLICIT_DOCUMENTS_MIGRATION_KEY = 'seekclaw-projects-remove-implicit-documents-v1'
const starterPrompts = [
  { label: '探索并理解代码', icon: Telescope, tone: 'blue' },
  { label: '构建新功能、应用或工具', icon: Hammer, tone: 'purple' },
  { label: '审查代码并提出修改建议', icon: RefreshCw, tone: 'green' },
  { label: '修复问题和失败', icon: Bug, tone: 'orange' }
] as const
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
  reasoningLevel?: string
}

interface RuntimeSession extends RuntimeSessionHeader {
  messages: Array<{
    role: 'user' | 'assistant' | 'tool'
    text: string
    images?: ImageAttachment[]
    thinking?: string
    viewedImages?: Array<{ id: string; name: string }>
    toolCalls?: Array<{ id: string; name: string }>
    toolCallId?: string
    toolName?: string
    toolSuccess?: boolean
    toolDiff?: string
    toolFilePath?: string
  }>
}

interface RuntimeWorkspace {
  path: string
  name: string
  mode: string
}

interface RuntimeModelCatalogItem {
  ref: string
  active: boolean
  capabilities?: { vision?: boolean }
}

const appInfo = ref<AppInfo>({
  version: '0.1.0',
  platform: 'win32',
  supportsMica: false,
  defaultWorkspace: '',
  documentsPath: ''
})
const sidebarOpen = ref(true)
const settingsOpen = ref(false)
const archivedTasksOpen = ref(false)
const gitPanelOpen = ref(false)
const gitPanelTab = ref<'diff' | 'history'>('diff')
const toolDiff = ref<{ path: string; diff: string } | null>(null)
const settingsSection = ref<'general' | 'models' | 'mcp' | 'skills' | 'diagnostics'>('general')
const taskSettingsThreadId = ref('')
const storedTheme = localStorage.getItem('seekclaw-theme')
const theme = ref<AppearanceTheme>(
  storedTheme === 'light' || storedTheme === 'dark' || storedTheme === 'system' ? storedTheme : 'system')
const daemonState = ref<DaemonState>({ connected: false, endpoint: '' })
const reconnecting = ref(false)
const reconnectAttempt = ref(0)
const reconnectPrompt = ref<{ startup: boolean; error?: string } | null>(null)
const projects = ref<ProjectItem[]>([])
const threads = ref<ThreadItem[]>([])
const activeThreadId = ref('')
const selectedProjectId = ref('')
const runtimeWorkspacePath = ref('')
const models = ref<string[]>([])
const modelCatalog = ref<RuntimeModelCatalogItem[]>([])
const activeModel = ref('balanced')
const mode = ref('edit')
const busy = computed(() => Boolean(activeThread.value?.running))
const scrollArea = ref<HTMLElement | null>(null)
const composer = ref<InstanceType<typeof Composer> | null>(null)
const autoFollowConversation = ref(true)
const taskNotice = ref<{ threadId: string; title: string; kind: 'done' | 'error' } | null>(null)

const activeThread = computed(() => threads.value.find((thread) => thread.id === activeThreadId.value))
const activeReasoningLevel = computed(() => activeThread.value?.reasoningLevel ?? ReasoningLevel.High)
const activeModelSupportsImages = computed(() =>
  modelCatalog.value.find((model) => model.ref === activeModel.value)?.capabilities?.vision === true)
const activeImageSources = computed<Record<string, string>>(() => {
  const sources: Record<string, string> = {}
  for (const message of activeThread.value?.messages ?? [])
    for (const image of message.images ?? [])
      sources[image.id] = `data:${image.mediaType};base64,${image.data}`
  return sources
})
const activeProject = computed(() => {
  const projectId = activeThread.value ? activeThread.value.projectId : selectedProjectId.value
  return projects.value.find((project) => project.id === projectId)
})
const globalTaskActive = computed(() => activeThread.value ? !activeThread.value.projectId : !selectedProjectId.value)
const settingsThread = computed(() => threads.value.find((thread) => thread.id === taskSettingsThreadId.value))
const settingsProject = computed(() => projects.value.find((project) => project.id === settingsThread.value?.projectId))
const conversationTitle = computed(() =>
  activeThread.value?.title || activeProject.value?.name || '全局任务')
const runtimeConnectionLabel = computed(() => {
  if (reconnecting.value) return `正在重连 ${reconnectAttempt.value}/${RUNTIME_RECONNECT_ATTEMPTS}`
  return 'Runtime 离线'
})

let unsubscribeEvent: (() => void) | undefined
let unsubscribeState: (() => void) | undefined
let reconnectTask: Promise<boolean> | null = null
let automaticReconnectPaused = false
let appReadyForRecovery = false

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

function openGitPanel(tab: 'diff' | 'history'): void {
  if (!activeProject.value) return
  toolDiff.value = null
  gitPanelTab.value = tab
  gitPanelOpen.value = true
}

function openToolDiff(path: string, diff: string): void {
  toolDiff.value = { path, diff }
  gitPanelTab.value = 'diff'
  gitPanelOpen.value = true
}

async function openProjectTerminal(): Promise<void> {
  if (!activeProject.value) return
  await window.seekclaw.project.openTerminal(activeProject.value.path)
}

function applyTheme(value: AppearanceTheme): void {
  theme.value = value
  document.documentElement.dataset.theme = value
  localStorage.setItem('seekclaw-theme', value)
  void window.seekclaw.setTheme(value)
}

function isNearConversationBottom(element: HTMLElement, threshold = 48): boolean {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold
}

function handleConversationScroll(): void {
  const element = scrollArea.value
  if (element) autoFollowConversation.value = isNearConversationBottom(element)
}

async function scrollToBottom(smooth = false, force = false): Promise<void> {
  await nextTick()
  const element = scrollArea.value
  if (!element || (!force && !autoFollowConversation.value)) return
  element.scrollTo({ top: element.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
}

let taskNoticeTimer: number | undefined

function showTaskNotice(thread: ThreadItem, kind: 'done' | 'error'): void {
  taskNotice.value = { threadId: thread.id, title: thread.title, kind }
  if (taskNoticeTimer !== undefined) window.clearTimeout(taskNoticeTimer)
  taskNoticeTimer = window.setTimeout(() => {
    taskNotice.value = null
    taskNoticeTimer = undefined
  }, 4200)
}

function normalizeReasoningLevel(value?: string): ReasoningLevel {
  const normalized = value?.toLocaleLowerCase()
  return Object.values(ReasoningLevel).includes(normalized as ReasoningLevel)
    ? normalized as ReasoningLevel
    : ReasoningLevel.High
}

function openTaskNotice(): void {
  const notice = taskNotice.value
  taskNotice.value = null
  if (notice) void selectThread(notice.threadId)
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
        existing.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
      } else {
        threads.value.push({
          id: `${project.id}:session:${saved.id}`,
          title: saved.title || fallbackTitle,
          projectId: project.id,
          updatedAt: new Date(saved.updatedAt).getTime(),
          messages: [],
          sessionId: saved.id,
          sessionLoaded: false,
          reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
          archived: Boolean(saved.archived)
        })
      }
    }
  } finally {
    project.loaded = true
  }
}

async function refreshGlobalSessions(): Promise<void> {
  if (!daemonState.value.connected) return
  const response = await window.seekclaw.daemon.request('session.list', {
    global: true,
    includeArchived: true
  })
  const savedSessions = JSON.parse(response.data) as RuntimeSessionHeader[]
  const sessionIds = new Set(savedSessions.map((session) => session.id))
  threads.value = threads.value.filter((thread) =>
    thread.projectId || !thread.sessionId || sessionIds.has(thread.sessionId))

  for (const saved of savedSessions) {
    const existing = threads.value.find((thread) => !thread.projectId && thread.sessionId === saved.id)
    const fallbackTitle = `任务 ${new Date(saved.createdAt).toLocaleString()}`
    if (existing) {
      existing.title = saved.title || existing.title || fallbackTitle
      existing.updatedAt = new Date(saved.updatedAt).getTime()
      existing.archived = Boolean(saved.archived)
      existing.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
    } else {
      threads.value.push({
        id: `global:session:${saved.id}`,
        title: saved.title || fallbackTitle,
        updatedAt: new Date(saved.updatedAt).getTime(),
        messages: [],
        sessionId: saved.id,
        sessionLoaded: false,
        reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
        archived: Boolean(saved.archived)
      })
    }
  }
}

async function refreshAllProjectSessions(): Promise<void> {
  await Promise.all([
    refreshGlobalSessions().catch(() => undefined),
    ...projects.value.map((project) => refreshProjectSessions(project).catch(() => undefined))
  ])
}

async function migrateImplicitDocumentsProject(): Promise<void> {
  if (localStorage.getItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY) === '1') return
  if (!appInfo.value.documentsPath) return

  const project = projects.value.find((item) => samePath(item.path, appInfo.value.documentsPath))
  if (!project) {
    localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
    return
  }

  // Only remove the legacy entry after the Runtime has confirmed that it owns no sessions.
  // A failed request leaves the migration pending so a later reconnect can retry safely.
  try {
    await refreshProjectSessions(project)
  } catch {
    return
  }

  if (!threads.value.some((thread) => thread.projectId === project.id)) {
    projects.value = projects.value.filter((item) => item.id !== project.id)
    if (selectedProjectId.value === project.id) selectedProjectId.value = ''
    if (activeThread.value?.projectId === project.id) activeThreadId.value = ''
  }
  localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
}

async function loadRuntimeState(): Promise<void> {
  try {
    const [modelResponse, workspaceResponse, modeResponse, catalogResponse] = await Promise.all([
      window.seekclaw.daemon.request('model.list'),
      window.seekclaw.daemon.request('workspace.get'),
      window.seekclaw.daemon.request('agent.mode.get'),
      window.seekclaw.daemon.request('model.catalog')
    ])
    const available = JSON.parse(modelResponse.data) as string[]
    const catalog = JSON.parse(catalogResponse.data) as RuntimeModelCatalogItem[]
    const workspace = JSON.parse(workspaceResponse.data) as RuntimeWorkspace
    const currentProject = projects.value.find((project) => samePath(project.path, workspace.path))
    if (currentProject && workspace.name) currentProject.name = workspace.name
    runtimeWorkspacePath.value = workspace.path
    selectedProjectId.value ||= currentProject?.id ?? ''
    models.value = available
    modelCatalog.value = catalog
    activeModel.value = catalog.find((model) => model.active)?.ref
      ?? (available.includes(activeModel.value) ? activeModel.value : available[0] ?? 'balanced')
    mode.value = modeResponse.data
    await refreshAllProjectSessions()
    await migrateImplicitDocumentsProject()

    if (!activeThread.value) {
      const recent = threads.value
        .filter((thread) => !thread.archived)
        .sort((left, right) => right.updatedAt - left.updatedAt)[0]
      if (recent) {
        activeThreadId.value = recent.id
        selectedProjectId.value = recent.projectId ?? ''
      }
    }
  } catch {
    models.value = []
    modelCatalog.value = []
  }
}

function handleDaemonState(state: DaemonState): void {
  daemonState.value = state
  if (state.connected) {
    automaticReconnectPaused = false
    reconnectPrompt.value = null
    return
  }
  if (!appReadyForRecovery || reconnecting.value || reconnectTask || automaticReconnectPaused || reconnectPrompt.value) return
  void runReconnectCycle(false)
}

async function runReconnectCycle(startup: boolean): Promise<boolean> {
  if (reconnectTask) return reconnectTask
  reconnectTask = (async () => {
    reconnecting.value = true
    reconnectPrompt.value = null
    const state = await retryRuntimeConnection(
      () => window.seekclaw.daemon.connect(),
      { onAttempt: (attempt) => { reconnectAttempt.value = attempt } })
    daemonState.value = state
    if (state.connected) {
      automaticReconnectPaused = false
      await loadRuntimeState()
      return true
    }
    reconnectPrompt.value = { startup, error: state.error }
    return false
  })().finally(() => {
    reconnecting.value = false
    reconnectAttempt.value = 0
    reconnectTask = null
  })
  return reconnectTask
}

async function reconnectDaemon(): Promise<void> {
  automaticReconnectPaused = false
  reconnectPrompt.value = null
  if (daemonState.value.connected) {
    await loadRuntimeState()
    return
  }
  await runReconnectCycle(false)
}

async function refreshRuntimeState(): Promise<void> {
  if (daemonState.value.connected) await loadRuntimeState()
  else await reconnectDaemon()
}

function continueRuntimeReconnect(): void {
  const startup = reconnectPrompt.value?.startup ?? false
  reconnectPrompt.value = null
  automaticReconnectPaused = false
  void runReconnectCycle(startup)
}

function cancelRuntimeReconnect(): void {
  if (reconnectPrompt.value?.startup) {
    void window.seekclaw.closeApp()
    return
  }
  reconnectPrompt.value = null
  automaticReconnectPaused = true
}

async function openWorkspace(): Promise<void> {
  const path = await window.seekclaw.selectWorkspace()
  if (!path) return
  const project = ensureProject(path)
  selectedProjectId.value = project.id
  activeThreadId.value = ''
  await refreshProjectSessions(project).catch(() => undefined)
}

function openSettings(section: typeof settingsSection.value = 'general'): void {
  settingsSection.value = section
  settingsOpen.value = true
}

function openArchivedTasks(): void {
  archivedTasksOpen.value = true
}

function selectArchivedThread(id: string): void {
  archivedTasksOpen.value = false
  void selectThread(id)
}

function newTask(projectId?: string): void {
  const project = projectId ? projects.value.find((item) => item.id === projectId) : undefined
  if (projectId && !project) return
  const thread: ThreadItem = {
    id: makeId(),
    title: '新任务',
    projectId: project?.id,
    updatedAt: Date.now(),
    messages: [],
    reasoningLevel: ReasoningLevel.High,
    archived: false
  }
  threads.value.unshift(thread)
  selectedProjectId.value = project?.id ?? ''
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
        const tool = assistant.tools.find((activity) =>
          Boolean(item.toolCallId) && (activity.callId === item.toolCallId || activity.id === item.toolCallId))
        const hydrated: ToolActivity = tool ?? {
          id: item.toolCallId ?? `${saved.id}:tool:${index}`,
          callId: item.toolCallId,
          name: item.toolName,
          state: 'done'
        }
        hydrated.state = item.toolSuccess === false ? 'error' : 'done'
        hydrated.detail = item.text
        hydrated.filePath = item.toolFilePath
        hydrated.diff = item.toolDiff
        if (!tool) assistant.tools.push(hydrated)
      }
      return
    }
    messages.push({
      id: `${saved.id}:${index}`,
      role: item.role,
      content: item.text,
      images: item.images,
      thinking: item.thinking,
      viewedImages: item.viewedImages,
      tools: item.toolCalls?.map((call) => ({
        id: call.id,
        callId: call.id,
        name: call.name,
        state: 'done'
      })),
      state: item.role === 'assistant' ? 'done' : undefined,
      createdAt: new Date(saved.createdAt).getTime() + index
    })
  })
  return messages
}

function sessionScope(thread: ThreadItem, project?: ProjectItem): Record<string, unknown> {
  return thread.projectId && project ? { workspace: project.path } : { global: true }
}

async function reloadThreadSession(thread: ThreadItem, project?: ProjectItem): Promise<void> {
  if (!thread.sessionId || thread.running || !daemonState.value.connected) return
  try {
    const response = await window.seekclaw.daemon.request('session.get', {
      id: thread.sessionId,
      ...sessionScope(thread, project)
    })
    const saved = JSON.parse(response.data) as RuntimeSession
    thread.messages = hydrateMessages(saved)
    thread.sessionLoaded = true
    thread.title = saved.title || thread.title
    thread.archived = Boolean(saved.archived)
    thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
  } catch {
    thread.sessionLoaded = false
  }
}

async function selectThread(id: string): Promise<void> {
  const thread = threads.value.find((item) => item.id === id)
  if (!thread) return
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  activeThreadId.value = id
  selectedProjectId.value = project?.id ?? ''
  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    if (thread.sessionId && (!thread.sessionLoaded || !thread.running)) {
      const response = await window.seekclaw.daemon.request('session.get', {
        id: thread.sessionId,
        ...scope
      })
      const saved = JSON.parse(response.data) as RuntimeSession
      thread.messages = hydrateMessages(saved)
      thread.sessionLoaded = true
      thread.title = saved.title || thread.title
      thread.archived = Boolean(saved.archived)
      thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
    }
  } catch {
    thread.sessionLoaded = false
  }
  autoFollowConversation.value = true
  await scrollToBottom(false, true)
}

function updateThreadTitle(thread: ThreadItem, prompt: string): boolean {
  if (thread.title !== '新任务') return false
  thread.title = prompt.length > 42 ? `${prompt.slice(0, 42)}…` : prompt
  return true
}

async function sendMessage(content: string, images: ImageAttachment[]): Promise<void> {
  const thread = activeThread.value
  const project = activeProject.value
  if (!thread || (thread.projectId && !project) || thread.archived || thread.running) return
  if (!content.trim() && images.length === 0) return
  const reasoningLevel = thread.reasoningLevel ?? ReasoningLevel.High

  const userMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content,
    images,
    createdAt: Date.now()
  }
  const assistant: ChatMessage = {
    id: makeId(), role: 'assistant', content: '', thinking: '', tools: [], state: 'thinking', createdAt: Date.now()
  }
  thread.messages.push(userMessage, assistant)
  thread.updatedAt = Date.now()
  const titlePrompt = content.trim() || `查看图片：${images.map((image) => image.name).join('、')}`
  const titleChanged = updateThreadTitle(thread, titlePrompt)
  thread.running = true
  thread.assistantId = assistant.id
  thread.requestId = undefined
  autoFollowConversation.value = true
  await scrollToBottom(true, true)

  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    let sessionCreated = false
    if (!thread.sessionId) {
      const sessionResponse = await window.seekclaw.daemon.request('session.new', {
        ...scope,
        reasoningLevel
      })
      thread.sessionId = sessionResponse.data
      thread.sessionLoaded = true
      sessionCreated = true
    }
    if (sessionCreated || titleChanged) {
      await window.seekclaw.daemon.request('session.update', {
        id: thread.sessionId,
        ...scope,
        title: thread.title
      })
    }
    await window.seekclaw.daemon.request('chat', {
      message: content,
      images,
      sessionId: thread.sessionId,
      reasoningLevel,
      ...scope
    })
    if (assistant.state !== 'error') assistant.state = 'done'
  } catch (error) {
    assistant.state = 'error'
    if (!assistant.content) {
      const detail = error instanceof Error ? error.message : String(error)
      assistant.content = `无法连接 SeekClaw Daemon。\n\n\`\`\`text\n${detail}\n\`\`\``
    }
  } finally {
    thread.running = false
    thread.requestId = undefined
    thread.assistantId = undefined
    if (thread.id === activeThreadId.value) await scrollToBottom(true)
  }
}

function openTaskSettings(thread: ThreadItem = activeThread.value!): void {
  if (thread) taskSettingsThreadId.value = thread.id
}

async function saveTaskTitle(title: string): Promise<void> {
  const thread = settingsThread.value
  const project = settingsProject.value
  if (!thread || (thread.projectId && !project)) return
  thread.title = title
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      title
    })
  }
  taskSettingsThreadId.value = ''
}

function chooseAfterRemoval(projectId?: string): void {
  const fallback = threads.value
    .filter((thread) => thread.projectId === projectId && !thread.archived)
    .sort((left, right) => right.updatedAt - left.updatedAt)[0]
  if (fallback) {
    activeThreadId.value = fallback.id
    selectedProjectId.value = projectId ?? ''
    return
  }
  activeThreadId.value = ''
  selectedProjectId.value = projectId ?? ''
}

async function archiveTask(thread: ThreadItem): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || thread.running) return
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.archive', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      archived: true
    })
    thread.archived = true
  } else {
    threads.value = threads.value.filter((item) => item.id !== thread.id)
  }
  taskSettingsThreadId.value = ''
  if (activeThreadId.value === thread.id) chooseAfterRemoval(project?.id)
}

async function restoreTask(thread: ThreadItem): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || !thread.sessionId || thread.running) return
  await window.seekclaw.daemon.request('session.archive', {
    id: thread.sessionId,
    ...sessionScope(thread, project),
    archived: false
  })
  thread.archived = false
  thread.updatedAt = Date.now()
  taskSettingsThreadId.value = ''
}

async function archiveProjectTasks(project: ProjectItem): Promise<void> {
  if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
  const targets = threads.value.filter((thread) => thread.projectId === project.id && !thread.archived)
  if (targets.length === 0 || targets.some((thread) => thread.running)) return
  if (!await confirmAction({
    title: '归档项目任务',
    message: `归档项目“${project.name}”的全部 ${targets.length} 个任务？`,
    confirmLabel: '全部归档'
  })) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
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
}

async function archiveGlobalTasks(): Promise<void> {
  const targets = threads.value.filter((thread) => !thread.projectId && !thread.archived)
  if (targets.length === 0 || targets.some((thread) => thread.running)) return
  if (!await confirmAction({
    title: '归档全局任务',
    message: `归档全部 ${targets.length} 个全局任务？`,
    confirmLabel: '全部归档'
  })) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  for (const thread of targets) {
    if (thread.sessionId) {
      await window.seekclaw.daemon.request('session.archive', {
        id: thread.sessionId,
        global: true,
        archived: true
      })
      thread.archived = true
    } else {
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
  }
  if (activeAffected) chooseAfterRemoval()
}

async function deleteTask(thread: ThreadItem): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || thread.running) return
  if (!await confirmAction({
    title: '删除任务',
    message: `永久删除任务“${thread.title}”？此操作无法撤销。`,
    confirmLabel: '永久删除',
    danger: true
  })) return
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.delete', {
      id: thread.sessionId,
      ...sessionScope(thread, project)
    })
  }
  threads.value = threads.value.filter((item) => item.id !== thread.id)
  taskSettingsThreadId.value = ''
  if (activeThreadId.value === thread.id) chooseAfterRemoval(project?.id)
}

async function deleteGlobalTasks(): Promise<void> {
  const targets = threads.value.filter((thread) => !thread.projectId)
  if (targets.length === 0 || targets.some((thread) => thread.running)) return
  if (!await confirmAction({
    title: '删除全部全局任务',
    message: `永久删除全部 ${targets.length} 个全局任务？此操作无法撤销。`,
    confirmLabel: '全部删除',
    danger: true
  })) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  for (const thread of targets) {
    if (thread.sessionId) {
      await window.seekclaw.daemon.request('session.delete', { id: thread.sessionId, global: true })
    }
    threads.value = threads.value.filter((item) => item.id !== thread.id)
  }
  taskSettingsThreadId.value = ''
  if (activeAffected) chooseAfterRemoval()
}

async function deleteProjectTasks(project: ProjectItem): Promise<void> {
  if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
  const targets = threads.value.filter((thread) => thread.projectId === project.id)
  if (targets.length === 0 || targets.some((thread) => thread.running)) return
  if (!await confirmAction({
    title: '删除项目全部任务',
    message: `永久删除项目“${project.name}”的全部 ${targets.length} 个任务？此操作无法撤销。`,
    confirmLabel: '全部删除',
    danger: true
  })) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
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
}

async function deleteArchivedTasks(): Promise<void> {
  const targets = threads.value.filter((thread) => thread.archived)
  if (targets.length === 0 || targets.some((thread) => thread.running)) return
  if (!await confirmAction({
    title: '清空已归档任务',
    message: `永久删除全部 ${targets.length} 个已归档任务？此操作无法撤销。`,
    confirmLabel: '全部删除',
    danger: true
  })) return

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  for (const thread of targets) {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if (thread.sessionId && (project || !thread.projectId)) {
      await window.seekclaw.daemon.request('session.delete', {
        id: thread.sessionId,
        ...sessionScope(thread, project)
      })
      }
    threads.value = threads.value.filter((item) => item.id !== thread.id)
  }
  taskSettingsThreadId.value = ''
  if (activeAffected) {
    activeThreadId.value = ''
    const project = projects.value.find((project) => project.id === selectedProjectId.value)
    chooseAfterRemoval(project?.id)
  }
}

async function deleteProject(project: ProjectItem): Promise<void> {
  if (threads.value.some((thread) => thread.projectId === project.id && thread.running)) return
  if (!daemonState.value.connected) {
    await reconnectDaemon()
    if (!daemonState.value.connected) return
  }
  try {
    // Always refresh so sessions created or archived by another client are included.
    await refreshProjectSessions(project)
  } catch {
    return
  }
  const targets = threads.value.filter((thread) => thread.projectId === project.id)
  if (targets.some((thread) => thread.running)) return
  if (!await confirmAction({
    title: '删除项目',
    message: `删除项目“${project.name}”并永久删除其下全部 ${targets.length} 个会话？本地项目文件不会删除，此操作无法撤销。`,
    confirmLabel: '删除项目和会话',
    danger: true
  })) return

  try {
    await Promise.all(targets
      .filter((thread) => thread.sessionId)
      .map((thread) => window.seekclaw.daemon.request('session.delete', {
        id: thread.sessionId,
        workspace: project.path
      })))
  } catch {
    // Some concurrent deletions may already have completed. Reconcile the project and keep it
    // visible so the user can retry instead of hiding sessions that remain on disk.
    await refreshProjectSessions(project).catch(() => undefined)
    return
  }

  const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
  projects.value = projects.value.filter((item) => item.id !== project.id)
  threads.value = threads.value.filter((thread) => thread.projectId !== project.id)
  if (targets.some((thread) => thread.id === taskSettingsThreadId.value)) taskSettingsThreadId.value = ''
  if (activeAffected) activeThreadId.value = ''
  if (selectedProjectId.value === project.id) selectedProjectId.value = projects.value[0]?.id ?? ''
  if (!activeThread.value) {
    activeThreadId.value = ''
    chooseAfterRemoval(selectedProjectId.value || undefined)
  }
}

function handleDaemonEvent(event: DaemonMessage): void {
  const isChatRequest = event.requestMethod === 'chat'
    || event.requestMethod === 'agent.runTurn'
    || event.requestMethod === 'agent/runTurn'
  if (!isChatRequest && !event.sessionId) return
  const thread = (event.sessionId
    ? threads.value.find((item) => item.sessionId === event.sessionId)
    : undefined)
    ?? threads.value.find((item) => item.requestId === event.id)
    ?? (!event.sessionId && activeThread.value?.running ? activeThread.value : undefined)
  if (!thread) return
  thread.requestId ??= event.id
  const message = thread.messages.find((item) => item.id === thread.assistantId)
  const terminalEvent = event.event === 'done' || event.event === 'cancelled' || event.event === 'error'
  if (!message && !terminalEvent) return
  const isBackgroundThread = thread.id !== activeThreadId.value
  const eventCallId = typeof event.details?.callId === 'string' ? event.details.callId : undefined
  const findTool = () => eventCallId
    ? message?.tools?.find((tool) => tool.callId === eventCallId || tool.id === eventCallId)
    : message?.tools?.findLast((tool) => tool.state === 'running')

  switch (event.event) {
    case 'thinking':
      if (!message) break
      message.thinking = (message.thinking ?? '') + event.data
      message.state = 'thinking'
      break
    case 'delta':
      if (!message) break
      message.content += event.data
      message.state = 'streaming'
      break
    case 'status':
      if (message && event.data.toLocaleLowerCase().includes('thinking')) message.state = 'thinking'
      break
    case 'image_view': {
      if (!message) break
      const imageId = typeof event.details?.imageId === 'string' ? event.details.imageId : ''
      if (!imageId) break
      message.viewedImages ??= []
      if (!message.viewedImages.some((image) => image.id === imageId))
        message.viewedImages.push({ id: imageId, name: event.data || '图片' })
      break
    }
    case 'tool_start':
      if (!message) break
      message.tools ??= []
      message.tools.push({
        id: eventCallId ?? `${event.id}-${message.tools.length}`,
        callId: eventCallId,
        name: event.data,
        detail: typeof event.details?.summary === 'string' ? event.details.summary : undefined,
        state: 'running'
      })
      break
    case 'tool_done': {
      if (!message) break
      const running = findTool()
      if (running) {
        running.state = event.details?.success === false ? 'error' : 'done'
        running.detail = event.data || running.detail
      }
      break
    }
    case 'file_diff': {
      if (!message) break
      const tool = findTool()
      if (tool) {
        const diff = typeof event.details?.diff === 'string' ? event.details.diff : ''
        tool.filePath = event.data
        tool.diff = diff
        tool.addedLines = diff.split(/\r?\n/).filter((line) => line.startsWith('+') && !line.startsWith('+++')).length
        tool.removedLines = diff.split(/\r?\n/).filter((line) => line.startsWith('-') && !line.startsWith('---')).length
      }
      break
    }
    case 'done':
    case 'cancelled':
      if (message) {
        message.state = 'done'
        if (!message.content && event.data) message.content = event.data
      }
      thread.running = false
      thread.requestId = undefined
      thread.assistantId = undefined
      if (isBackgroundThread) {
        showTaskNotice(thread, 'done')
      }
      if (!message || isBackgroundThread) {
        void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
      }
      break
    case 'error':
      if (message) {
        message.state = 'error'
        appendModelError(message, event.data)
      }
      thread.running = false
      thread.requestId = undefined
      thread.assistantId = undefined
      if (isBackgroundThread) {
        showTaskNotice(thread, 'error')
      }
      if (!message || isBackgroundThread) {
        void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
      }
      break
  }
  if (thread.id === activeThreadId.value) void scrollToBottom()
}

function appendModelError(message: ChatMessage, detail: string): void {
  const normalized = detail.trim() || 'Unknown model error'
  if (message.content.includes(normalized)) return
  const indentedDetail = normalized.split(/\r?\n/).map((line) => `    ${line}`).join('\n')
  const errorBlock = `**模型请求失败**\n\n${indentedDetail}`
  message.content = message.content.trim()
    ? `${message.content}\n\n---\n\n${errorBlock}`
    : errorBlock
}

async function stopTurn(): Promise<void> {
  const thread = activeThread.value
  if (!thread?.running) return
  try {
    await window.seekclaw.daemon.request('agent.cancel',
      thread.requestId ? { requestId: thread.requestId } : {})
  } catch { /* sendMessage owns the final state */ }
}

async function changeModel(model: string): Promise<void> {
  const previousModel = activeModel.value
  activeModel.value = model
  if (!daemonState.value.connected || model === 'balanced') return
  try { await window.seekclaw.daemon.request('model.switch', { model }) }
  catch { activeModel.value = previousModel }
}

async function changeMode(nextMode: string): Promise<void> {
  if (nextMode === mode.value) return
  try {
    const response = await window.seekclaw.daemon.request('agent.mode.switch', { mode: nextMode })
    mode.value = response.data
  } catch { /* Keep showing the active Runtime mode. */ }
}

async function changeReasoningLevel(level: ReasoningLevel): Promise<void> {
  const thread = activeThread.value
  if (!thread || thread.running || thread.archived) return
  thread.reasoningLevel = level
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!thread.sessionId || !daemonState.value.connected || (thread.projectId && !project)) return
  try {
    await window.seekclaw.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      reasoningLevel: level
    })
  } catch { /* The selected level is still sent with the next turn. */ }
}

function useStarterPrompt(prompt: string): void {
  composer.value?.setValue(prompt)
}

onMounted(async () => {
  applyTheme(theme.value)
  appInfo.value = await window.seekclaw.getAppInfo()
  document.documentElement.dataset.platform = appInfo.value.platform
  document.documentElement.dataset.material = appInfo.value.supportsMica ? 'mica' : 'solid'
  projects.value = loadStoredProjects()
  unsubscribeEvent = window.seekclaw.daemon.onEvent(handleDaemonEvent)
  unsubscribeState = window.seekclaw.daemon.onState(handleDaemonState)
  appReadyForRecovery = true
  await runReconnectCycle(true)
  if (!daemonState.value.connected) projects.value.forEach((project) => { project.loaded = true })
  if (activeThread.value) composer.value?.focus()
})

onBeforeUnmount(() => {
  appReadyForRecovery = false
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
      :project-path="globalTaskActive ? undefined : activeProject?.path"
      @toggle-sidebar="sidebarOpen = !sidebarOpen"
      @new-task="newTask(selectedProjectId || undefined)"
      @open-workspace="openWorkspace"
      @show-project="showActiveProject"
      @open-settings="openSettings('general')"
      @focus-composer="composer?.focus()"
      @open-terminal="openProjectTerminal"
      @open-git-changes="openGitPanel('diff')"
      @open-git-history="openGitPanel('history')"
      @open-diagnostics="openSettings('diagnostics')"
    />

    <div class="app-body" :class="{ 'sidebar-collapsed': !sidebarOpen }">
      <Transition name="sidebar-slide">
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
        @task-settings="openTaskSettings"
        @archive-task="archiveTask"
        @restore-task="restoreTask"
        @delete-task="deleteTask"
        @delete-project="deleteProject"
        @archive-project-tasks="archiveProjectTasks"
        @delete-project-tasks="deleteProjectTasks"
        @archive-global-tasks="archiveGlobalTasks"
        @delete-global-tasks="deleteGlobalTasks"
        @open-archived="openArchivedTasks"
        @open-extensions="openSettings('mcp')"
        @open-settings="openSettings('general')"
        />
      </Transition>
      <Transition name="scrim-fade">
        <button v-if="sidebarOpen" class="sidebar-scrim" title="关闭侧栏" @click="sidebarOpen = false" />
      </Transition>

      <main class="workspace-main">
        <header class="conversation-header">
          <div class="conversation-title">
            <Globe2 v-if="globalTaskActive" :size="20" />
            <Folder v-else :size="20" />
            <strong>{{ conversationTitle }}</strong>
            <small v-if="activeThread">{{ activeProject?.name || '全局任务' }}</small>
          </div>
          <div class="conversation-actions">
            <button
              v-if="!daemonState.connected"
              class="connection-button"
              :title="daemonState.error || daemonState.endpoint"
              :disabled="reconnecting"
              @click="reconnectDaemon"
            >
              <Circle :size="9" fill="currentColor" />
              {{ runtimeConnectionLabel }}
              <RefreshCw :class="{ spin: reconnecting }" :size="14" />
            </button>
            <button v-if="activeProject" class="open-location-button" @click="showActiveProject">
              <FolderOpen :size="17" />
              <span>打开位置</span>
            </button>
            <button v-if="activeProject" class="icon-button project-tool-button" title="在项目目录打开终端" @click="openProjectTerminal">
              <TerminalSquare :size="18" />
            </button>
            <button v-if="activeProject" class="icon-button project-tool-button" title="查看代码更改" @click="openGitPanel('diff')">
              <Braces :size="18" />
            </button>
            <button v-if="activeProject" class="icon-button project-tool-button" title="查看 Git 提交记录" @click="openGitPanel('history')">
              <History :size="18" />
            </button>
            <button class="icon-button" title="任务设置" :disabled="!activeThread" @click="openTaskSettings()">
              <MoreHorizontal :size="18" />
            </button>
            <button class="icon-button" title="切换侧栏" @click="sidebarOpen = !sidebarOpen"><PanelRight :size="18" /></button>
          </div>
        </header>

        <section ref="scrollArea" class="conversation-scroll" @scroll="handleConversationScroll">
          <div v-if="activeThread && activeThread.messages.length > 0" class="conversation-content">
            <ConversationMessage
              v-for="message in activeThread.messages"
              :key="message.id"
              :message="message"
              :image-sources="activeImageSources"
              @open-diff="openToolDiff"
            />
          </div>
          <div v-else-if="activeThread" class="empty-state">
            <h1>今天从哪里开始？</h1>
            <p>{{ activeProject?.name || '全局任务 · 无工作目录' }}</p>
            <div v-if="!activeThread?.archived" class="starter-prompts" aria-label="快速开始">
              <button
                v-for="prompt in starterPrompts"
                :key="prompt.label"
                type="button"
                class="starter-prompt-card"
                :data-tone="prompt.tone"
                @click="useStarterPrompt(prompt.label)"
              >
                <component :is="prompt.icon" :size="20" aria-hidden="true" />
                <span>{{ prompt.label }}</span>
              </button>
            </div>
          </div>
          <div v-else class="empty-state no-task-state">
            <h1>还没有任务</h1>
            <p>新建一个任务以开始使用 SeekClaw</p>
            <button class="secondary-button empty-state-action" @click="newTask(selectedProjectId || undefined)">
              新建任务
            </button>
          </div>
        </section>

        <Transition name="task-notice">
          <button
            v-if="taskNotice"
            type="button"
            class="task-notice"
            :class="{ error: taskNotice.kind === 'error' }"
            @click="openTaskNotice"
          >
            <span>{{ taskNotice.kind === 'error' ? '任务执行失败' : '后台任务已完成' }}</span>
            <small>{{ taskNotice.title }}</small>
          </button>
        </Transition>

        <footer class="composer-region">
          <Composer
            ref="composer"
            :busy="busy"
            :disabled="!activeThread || activeThread.archived"
            :model="activeModel"
            :models="models"
            :mode="mode"
            :task-id="activeThread?.id"
            :supports-images="activeModelSupportsImages"
            :reasoning-level="activeReasoningLevel"
            @send="sendMessage"
            @stop="stopTurn"
            @change-model="changeModel"
            @change-mode="changeMode"
            @change-reasoning-level="changeReasoningLevel"
          />
          <p class="composer-caption">
            {{ !activeThread
              ? '选择一个任务，或新建任务开始。'
              : activeThread.archived
              ? '此任务已归档，恢复后可继续。'
              : globalTaskActive
                ? '全局任务不使用本地文件、终端或 Git 工具。'
                : 'SeekClaw 可能会出错，请检查生成的代码和命令。' }}
          </p>
        </footer>
      </main>
    </div>

    <SettingsDialog
      :open="settingsOpen"
      :theme="theme"
      :daemon-connected="daemonState.connected"
      :daemon-endpoint="daemonState.endpoint"
      :workspace-path="activeProject?.path || runtimeWorkspacePath || appInfo.defaultWorkspace"
      :initial-section="settingsSection"
      @close="settingsOpen = false"
      @change-theme="applyTheme"
      @reconnect="reconnectDaemon"
      @open-workspace="openWorkspace"
      @runtime-changed="refreshRuntimeState"
    />

    <ArchivedTasksDialog
      :open="archivedTasksOpen"
      :projects="projects"
      :threads="threads"
      @close="archivedTasksOpen = false"
      @select-thread="selectArchivedThread"
      @restore-task="restoreTask"
      @delete-task="deleteTask"
      @delete-all="deleteArchivedTasks"
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

    <RuntimeReconnectDialog
      :open="Boolean(reconnectPrompt)"
      :startup="reconnectPrompt?.startup ?? false"
      :endpoint="daemonState.endpoint"
      :error="reconnectPrompt?.error"
      @retry="continueRuntimeReconnect"
      @cancel="cancelRuntimeReconnect"
    />

    <GitWorkspacePanel
      :open="gitPanelOpen"
      :project="activeProject"
      :initial-tab="gitPanelTab"
      :diff-override="toolDiff"
      @close="gitPanelOpen = false; toolDiff = null"
      @open-terminal="openProjectTerminal"
    />

    <ConfirmDialog />
  </div>
</template>
