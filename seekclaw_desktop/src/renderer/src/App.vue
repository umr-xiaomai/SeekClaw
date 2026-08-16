<script setup lang="ts">
import {
  Braces,
  Bug,
  Circle,
  CornerDownLeft,
  Folder,
  FolderOpen,
  Globe2,
  Hammer,
  History,
  LoaderCircle,
  MoreHorizontal,
  PanelRight,
  RefreshCw,
  Search,
  Telescope,
  TerminalSquare,
  Trash2,
  Workflow,
  X
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { AppearanceTheme, AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import AppTitleBar from './components/AppTitleBar.vue'
import AboutDialog from './components/AboutDialog.vue'
import ArchivedTasksDialog from './components/ArchivedTasksDialog.vue'
import ScheduledTasksDialog from './components/ScheduledTasksDialog.vue'
import Composer from './components/Composer.vue'
import ConfirmDialog from './components/ConfirmDialog.vue'
import ConversationMessage from './components/ConversationMessage.vue'
import GitWorkspacePanel from './components/GitWorkspacePanel.vue'
import OfficialSkillsDialog from './components/OfficialSkillsDialog.vue'
import RuntimeReconnectDialog from './components/RuntimeReconnectDialog.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import TaskSettingsDialog from './components/TaskSettingsDialog.vue'
import WorkflowPanel from './components/WorkflowPanel.vue'
import { confirmAction } from './confirmation'
import { finalizeAssistantBubbles } from './conversation-state'
import { isForbiddenProjectPath } from './project-paths'
import { retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'
import { ReasoningLevel } from './types'
import type { ChatMessage, ImageAttachment, ProjectItem, QueuedMessage, ThreadItem, ThreadStats, ToolActivity, WorkflowKind } from './types'

const PROJECTS_STORAGE_KEY = 'seekclaw-projects-v2'
const IMPLICIT_DOCUMENTS_MIGRATION_KEY = 'seekclaw-projects-remove-implicit-documents-v2'
// The daemon starts a chat turn within milliseconds of receiving the request,
// so a request that produces no event at all within this window is stuck (for
// example the payload never reached the daemon). Reject it instead of leaving
// the task loading forever; once the first event arrives the turn is confirmed
// running and may legitimately take minutes.
const CHAT_FIRST_EVENT_TIMEOUT_MS = 30_000
// Vue reactive proxies cannot be structured-cloned by Electron IPC; copy the
// attachments into plain objects at the IPC boundary so requests reach the
// daemon instead of failing with a DataCloneError.
function plainImages(images?: ImageAttachment[]): ImageAttachment[] {
  return (images ?? []).map((image) => ({
    id: image.id,
    name: image.name,
    mediaType: image.mediaType,
    data: image.data,
    sizeBytes: image.sizeBytes
  }))
}
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
  networkEnabled?: boolean
  llmRounds?: number
  executionSteps?: number
  inputTokens?: number
  totalInputTokens?: number
  cachedInputTokens?: number
  outputTokens?: number
  outputElapsedMs?: number
}

interface RuntimeSession extends RuntimeSessionHeader {
  messages: Array<{
    role: 'user' | 'assistant' | 'tool'
    text: string
    images?: ImageAttachment[]
    thinking?: string
    modelRef?: string
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

interface RuntimeProject {
  id: string
  path: string
  name: string
  createdAt: string
  updatedAt: string
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
  documentsPath: '',
  userProfilePath: ''
})
type AppPage = 'main' | 'settings' | 'extensions' | 'archived' | 'scheduled' | 'official-skills'

const sidebarOpen = ref(true)
const activePage = ref<AppPage>('main')
const aboutOpen = ref(false)
const workflowOpen = ref(false)
const gitPanelOpen = ref(false)
const gitPanelTab = ref<'diff' | 'history'>('diff')
const gitPanelWidth = ref(560)
const toolDiff = ref<{ path: string; diff: string } | null>(null)
const settingsSection = ref<'general' | 'models' | 'mcp' | 'skills' | 'diagnostics'>('general')
const extensionsSection = ref<'mcp' | 'skills'>('mcp')
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
const activeModel = ref('')
const mode = ref('edit')
const busy = computed(() => Boolean(activeThread.value?.running))
const scrollArea = ref<HTMLElement | null>(null)
const composer = ref<InstanceType<typeof Composer> | null>(null)
const autoFollowConversation = ref(true)
const conversationLoading = ref(false)
const conversationLoadError = ref('')
const conversationQuery = ref('')
const messageHeights = new Map<string, number>()
const conversationScrollTop = ref(0)
const conversationViewportHeight = ref(600)
/** Per-task composer drafts, kept across task switches. */
const composerDrafts = new Map<string, string>()
let conversationSelectionToken = 0

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
const composerCaption = computed(() => {
  if (conversationLoading.value) return '正在读取会话历史…'
  if (!activeThread.value) return '选择一个任务，或新建任务开始。'
  if (activeThread.value.archived) return '此任务已归档，恢复后可继续。'
  const stats = activeThread.value.stats
  const number = (value?: number): string =>
    typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString() : '—'
  const totalInputTokens = stats?.totalInputTokens ?? 0
  const cachedInputTokens = stats?.cachedInputTokens ?? 0
  const cacheRate = totalInputTokens > 0
    ? `${Math.min(100, Math.round(cachedInputTokens / totalInputTokens * 100))}%`
    : '—'
  const outputTokens = stats?.outputTokens ?? 0
  const outputElapsedMs = stats?.outputElapsedMs ?? 0
  const speed = outputTokens > 0 && outputElapsedMs > 0
    ? (outputTokens / (outputElapsedMs / 1000)).toFixed(1)
    : '—'
  return `${number(stats?.llmRounds)} 轮 · ${number(stats?.executionSteps)} 步 | 缓存命中 ${cacheRate} | ${speed} tok/s | 输入 ${number(stats?.inputTokens)} tok · 输出 ${number(stats?.outputTokens)} tok`
})
const settingsThread = computed(() => threads.value.find((thread) => thread.id === taskSettingsThreadId.value))
const settingsProject = computed(() => projects.value.find((project) => project.id === settingsThread.value?.projectId))
const conversationTitle = computed(() =>
  activeThread.value?.title || activeProject.value?.name || '任务')
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

async function saveProject(project: ProjectItem): Promise<ProjectItem> {
  const oldId = project.id
  const response = await window.seekclaw.daemon.request('project.upsert', {
    id: project.id,
    path: project.path,
    name: project.name
  })
  const saved = JSON.parse(response.data) as RuntimeProject
  const duplicate = projects.value.find((item) => item !== project && item.id === saved.id)
  if (duplicate) {
    duplicate.path = saved.path
    duplicate.name = saved.name
    threads.value.forEach((thread) => {
      if (thread.projectId === oldId) thread.projectId = duplicate.id
    })
    if (selectedProjectId.value === oldId) selectedProjectId.value = duplicate.id
    projects.value = projects.value.filter((item) => item !== project)
    return duplicate
  }
  project.id = saved.id
  project.path = saved.path
  project.name = saved.name
  if (oldId !== saved.id) {
    threads.value.forEach((thread) => {
      if (thread.projectId === oldId) thread.projectId = saved.id
    })
    if (selectedProjectId.value === oldId) selectedProjectId.value = saved.id
  }
  return project
}

async function migrateStoredProjects(): Promise<void> {
  if (localStorage.getItem(PROJECTS_STORAGE_KEY) === null) return
  const stored = loadStoredProjects()
  for (const project of stored) {
    if (isForbiddenProjectPath(project.path, appInfo.value.userProfilePath)) continue
    await window.seekclaw.daemon.request('project.upsert', {
      id: project.id,
      path: project.path,
      name: project.name
    })
  }
  localStorage.removeItem(PROJECTS_STORAGE_KEY)
}

async function removeInvalidProjectRows(): Promise<void> {
  // Older builds could register the user profile (or ~/.seekclaw) as a project, which
  // made every plain folder under the profile share one session scope. Drop those rows
  // now; sessions are preserved in the database instead of being deleted with them.
  const invalid = projects.value.filter((project) =>
    isForbiddenProjectPath(project.path, appInfo.value.userProfilePath))
  for (const project of invalid) {
    try {
      await window.seekclaw.daemon.request('project.remove', { id: project.id, keepSessions: true })
    } catch {
      continue // leave the row in place; the next launch retries the cleanup
    }
    projects.value = projects.value.filter((item) => item.id !== project.id)
    if (selectedProjectId.value === project.id) selectedProjectId.value = ''
  }
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

function openDevTools(): void {
  void window.seekclaw.openDevTools()
}

function openToolDiff(path: string, diff: string): void {
  toolDiff.value = { path, diff }
  gitPanelTab.value = 'diff'
  gitPanelOpen.value = true
}

function closeGitPanel(): void {
  gitPanelOpen.value = false
  toolDiff.value = null
}

function resizeGitPanel(width: number): void {
  if (!Number.isFinite(width)) return
  const maxWidth = Math.min(720, Math.max(280, Math.floor(window.innerWidth * 0.66)))
  gitPanelWidth.value = Math.min(maxWidth, Math.max(280, Math.round(width)))
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

function measureConversationViewport(): void {
  if (scrollArea.value) conversationViewportHeight.value = scrollArea.value.clientHeight
}

function handleConversationScroll(): void {
  if (scrollArea.value) conversationScrollTop.value = scrollArea.value.scrollTop
  const element = scrollArea.value
  if (element) autoFollowConversation.value = isNearConversationBottom(element)
}

async function scrollToBottom(smooth = false, force = false): Promise<void> {
  await nextTick()
  const element = scrollArea.value
  if (!element || (!force && !autoFollowConversation.value)) return
  element.scrollTo({ top: element.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
}

async function handleScheduleUpdated(): Promise<void> {
  if (!daemonState.value.connected) return
  await refreshAllProjectSessions().catch(() => undefined)
}

function normalizeReasoningLevel(value?: string): ReasoningLevel {
  const normalized = value?.toLocaleLowerCase()
  return Object.values(ReasoningLevel).includes(normalized as ReasoningLevel)
    ? normalized as ReasoningLevel
    : ReasoningLevel.High
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
        existing.networkEnabled = saved.networkEnabled ?? true
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
          networkEnabled: saved.networkEnabled ?? true,
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
      existing.networkEnabled = saved.networkEnabled ?? true
    } else {
      threads.value.push({
        id: `global:session:${saved.id}`,
        title: saved.title || fallbackTitle,
        updatedAt: new Date(saved.updatedAt).getTime(),
        messages: [],
        sessionId: saved.id,
        sessionLoaded: false,
        reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
        networkEnabled: saved.networkEnabled ?? true,
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
    await window.seekclaw.daemon.request('project.remove', { id: project.id })
    projects.value = projects.value.filter((item) => item.id !== project.id)
    if (selectedProjectId.value === project.id) selectedProjectId.value = ''
    if (activeThread.value?.projectId === project.id) activeThreadId.value = ''
  }
  localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
}

async function loadRuntimeState(): Promise<void> {
  try {
    await migrateStoredProjects()
    const [projectResponse, modelResponse, workspaceResponse, modeResponse, catalogResponse] = await Promise.all([
      window.seekclaw.daemon.request('project.list'),
      window.seekclaw.daemon.request('model.list'),
      window.seekclaw.daemon.request('workspace.get'),
      window.seekclaw.daemon.request('agent.mode.get'),
      window.seekclaw.daemon.request('model.catalog')
    ])
    projects.value = (JSON.parse(projectResponse.data) as RuntimeProject[]).map((project) => ({
      id: project.id,
      name: project.name || pathName(project.path),
      path: project.path,
      loaded: false
    }))
    await removeInvalidProjectRows()
    const available = JSON.parse(modelResponse.data) as string[]
    const catalog = JSON.parse(catalogResponse.data) as RuntimeModelCatalogItem[]
    const workspace = JSON.parse(workspaceResponse.data) as RuntimeWorkspace
    const currentProject = projects.value.find((project) => samePath(project.path, workspace.path))
    if (currentProject && workspace.name) currentProject.name = workspace.name
    runtimeWorkspacePath.value = workspace.path
    if (!projects.value.some((project) => project.id === selectedProjectId.value))
      selectedProjectId.value = currentProject?.id ?? ''
    models.value = available
    modelCatalog.value = catalog
    activeModel.value = catalog.find((model) => model.active)?.ref
      ?? (available.length > 0
        ? (available.includes(activeModel.value) ? activeModel.value : available[0] ?? '')
        : '')
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
        await selectThread(recent.id)
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
  if (isForbiddenProjectPath(path, appInfo.value.userProfilePath)) {
    window.alert('不能把用户主目录或 SeekClaw 数据目录添加为项目，请选择具体的项目文件夹。')
    return
  }
  const project = await saveProject(ensureProject(path))
  selectedProjectId.value = project.id
  activeThreadId.value = ''
  conversationSelectionToken++
  conversationLoading.value = false
  conversationLoadError.value = ''
  await refreshProjectSessions(project).catch(() => undefined)
}

function openSettings(section: typeof settingsSection.value = 'general'): void {
  settingsSection.value = section
  activePage.value = 'settings'
}

function openExtensions(section: 'mcp' | 'skills' = 'mcp'): void {
  extensionsSection.value = section
  activePage.value = 'extensions'
}

function openArchivedTasks(): void {
  activePage.value = 'archived'
}

function openScheduledTasks(): void {
  activePage.value = 'scheduled'
}

function openOfficialSkills(): void {
  activePage.value = 'official-skills'
}

function closePage(): void {
  activePage.value = 'main'
}

function selectArchivedThread(id: string): void {
  activePage.value = 'main'
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
    networkEnabled: true,
    archived: false,
    stats: {
      llmRounds: 0,
      executionSteps: 0,
      inputTokens: 0,
      outputTokens: 0,
      totalInputTokens: 0,
      cachedInputTokens: 0,
      outputElapsedMs: 0
    }
  }
  threads.value.unshift(thread)
  selectedProjectId.value = project?.id ?? ''
  activeThreadId.value = thread.id
  // Invalidate an in-flight session read for the previously selected task.
  conversationSelectionToken++
  conversationLoading.value = false
  conversationLoadError.value = ''
  void nextTick(() => composer.value?.focus())
}

async function ensureRuntimeProject(project: ProjectItem): Promise<void> {
  if (samePath(runtimeWorkspacePath.value, project.path)) return
  const response = await window.seekclaw.daemon.request('workspace.open', { path: project.path })
  const opened = JSON.parse(response.data) as RuntimeWorkspace
  project.path = opened.path
  project.name = opened.name || project.name
  await saveProject(project)
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
      modelRef: item.modelRef,
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

function sessionStats(saved: RuntimeSession): ThreadStats {
  const assistantCount = saved.messages.filter((item) => item.role === 'assistant').length
  return {
    llmRounds: saved.llmRounds || assistantCount,
    executionSteps: saved.executionSteps || assistantCount,
    inputTokens: saved.inputTokens ?? 0,
    totalInputTokens: saved.totalInputTokens ?? 0,
    cachedInputTokens: saved.cachedInputTokens ?? 0,
    outputTokens: saved.outputTokens ?? 0,
    outputElapsedMs: saved.outputElapsedMs ?? 0
  }
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
    thread.stats = sessionStats(saved)
  } catch {
    thread.sessionLoaded = false
  }
}

async function selectThread(id: string): Promise<void> {
  const thread = threads.value.find((item) => item.id === id)
  if (!thread) return
  if (activeThread.value && composer.value)
    composerDrafts.set(activeThread.value.id, composer.value.getValue())
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const selectionToken = ++conversationSelectionToken
  const needsLoad = Boolean(thread.sessionId && (!thread.sessionLoaded || !thread.running))
  activeThreadId.value = id
  selectedProjectId.value = project?.id ?? ''
  conversationLoadError.value = ''
  conversationLoading.value = needsLoad
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
      thread.networkEnabled = saved.networkEnabled ?? true
      thread.stats = sessionStats(saved)
    }
  } catch {
    thread.sessionLoaded = false
    if (selectionToken === conversationSelectionToken)
      conversationLoadError.value = '无法读取此会话，请检查 Runtime 连接后重试。'
  }
  if (selectionToken !== conversationSelectionToken) return
  conversationLoading.value = false
  autoFollowConversation.value = true
  const draft = composerDrafts.get(thread.id)
  if (draft) composer.value?.setValue(draft)
  await scrollToBottom(false, true)
}

function updateThreadTitle(thread: ThreadItem, prompt: string): boolean {
  if (thread.title !== '新任务') return false
  thread.title = prompt.length > 42 ? `${prompt.slice(0, 42)}…` : prompt
  return true
}

interface ConversationItem { message: ChatMessage; step?: number }

/** Adds "步骤 N" markers before assistant messages that follow tool results. */
const conversationItems = computed<ConversationItem[]>(() => {
  const messages = activeThread.value?.messages ?? []
  const items: ConversationItem[] = []
  let step = 0
  let lastWasTool = false
  for (const message of messages) {
    if (message.role === 'user') {
      step = 0
      lastWasTool = false
    } else {
      // Tool results are folded into assistant.tools, so an assistant message that
      // carries tools followed by another assistant message begins a new step.
      if (lastWasTool) step++
      lastWasTool = (message.tools?.length ?? 0) > 0
      items.push({ message, step: step > 1 ? step : undefined })
      continue
    }
    items.push({ message })
  }
  return items
})

const VIRTUAL_THRESHOLD = 60
const VIRTUAL_OVERSCAN = 12
const VIRTUAL_ESTIMATED_HEIGHT = 120

/** Windowed rendering for very long conversations: only messages near the viewport are mounted. */
const virtualWindow = computed(() => {
  const items = conversationItems.value
  const total = items.length
  if (total <= VIRTUAL_THRESHOLD || conversationQuery.value.trim()) {
    return { active: false, start: 0, end: total, topPad: 0, bottomPad: 0, items }
  }
  const heightOf = (index: number): number =>
    messageHeights.get(items[index]?.message.id ?? '') ?? VIRTUAL_ESTIMATED_HEIGHT

  const targetTop = Math.max(0, conversationScrollTop.value - VIRTUAL_OVERSCAN * VIRTUAL_ESTIMATED_HEIGHT)
  let topPad = 0
  let start = 0
  for (; start < total; start++) {
    const height = heightOf(start)
    if (topPad + height >= targetTop) break
    topPad += height
  }

  const targetBottom = conversationScrollTop.value + conversationViewportHeight.value
    + VIRTUAL_OVERSCAN * VIRTUAL_ESTIMATED_HEIGHT
  let end = start
  let acc = topPad
  while (end < total && acc < targetBottom) {
    acc += heightOf(end)
    end++
  }

  let bottomPad = 0
  for (let i = end; i < total; i++) bottomPad += heightOf(i)
  return { active: true, start, end, topPad, bottomPad, items: items.slice(start, end) }
})

const vMeasure = {
  mounted(el: HTMLElement, binding: { value: string }): void {
    const report = (): void => {
      const height = el.getBoundingClientRect().height
      if (height > 0) messageHeights.set(binding.value, height)
    }
    report()
    const observer = new ResizeObserver(report)
      ; (el as HTMLElement & { __heightObserver?: ResizeObserver }).__heightObserver = observer
    observer.observe(el)
  },
  unmounted(el: HTMLElement & { __heightObserver?: ResizeObserver }): void {
    el.__heightObserver?.disconnect()
  }
}

function messageMatches(message: ChatMessage, query: string): boolean {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  return message.content.toLocaleLowerCase().includes(normalized)
}

function phaseLabel(status: string): string {
  const s = status.toLocaleLowerCase()
  if (s.includes('compacting')) return '压缩记忆'
  if (s.includes('verifying')) return '构建验证'
  if (s.includes('truncated')) return '自动续写'
  if (s.includes('thinking')) return '思考中'
  return status
}

function continueAssistant(): void {
  const thread = activeThread.value
  if (!thread || thread.running || thread.archived) return
  void sendMessage('继续', [])
}

async function regenerateMessage(message: ChatMessage): Promise<void> {
  const thread = activeThread.value
  if (!thread || thread.running || thread.archived || !thread.sessionId) return
  const index = thread.messages.findIndex((item) => item.id === message.id)
  if (index < 0) return
  // Re-run the turn from its user prompt: keep history through that prompt, then resend it.
  let promptIndex = -1
  for (let i = index; i >= 0; i--) {
    if (thread.messages[i]?.role === 'user') { promptIndex = i; break }
  }
  if (promptIndex < 0) return
  const prompt = thread.messages[promptIndex]?.content ?? ''
  const project = projects.value.find((item) => item.id === thread.projectId)
  try {
    await window.seekclaw.daemon.request('session.truncate', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      keepCount: promptIndex + 1
    })
  } catch {
    return
  }
  thread.messages = thread.messages.slice(0, promptIndex + 1)
  thread.phase = undefined
  await sendMessage(prompt, [])
}

async function sendMessage(content: string, images: ImageAttachment[]): Promise<void> {
  const thread = activeThread.value
  const project = thread ? projects.value.find((item) => item.id === thread.projectId) : undefined
  if (!thread || (thread.projectId && !project) || thread.archived) return
  if (!content.trim() && images.length === 0) return
  if (thread.running || thread.queueDraining) {
    thread.queuedMessages ??= []
    thread.queuedMessages.push({ id: makeId(), content, images, createdAt: Date.now() })
    return
  }
  composerDrafts.delete(thread.id)
  await runMessageTurn(thread, content, images)
}

function rememberFinishedRequest(thread: ThreadItem, requestId: number): void {
  if (!Number.isFinite(requestId)) return
  thread.finishedRequestIds ??= []
  if (thread.finishedRequestIds.includes(requestId)) return
  thread.finishedRequestIds.push(requestId)
  // Keep this bounded; it only protects against delayed events from recent turns.
  if (thread.finishedRequestIds.length > 12) thread.finishedRequestIds.splice(0, thread.finishedRequestIds.length - 12)
}

function isFinishedRequest(thread: ThreadItem, requestId: number): boolean {
  return thread.finishedRequestIds?.includes(requestId) === true
}

function scheduleQueuedDrain(thread: ThreadItem): void {
  if (!thread.running && !thread.queueDraining && !thread.archived && thread.queuedMessages?.length)
    void drainQueuedMessages(thread)
}

async function drainQueuedMessages(thread: ThreadItem): Promise<void> {
  if (thread.running || thread.queueDraining || thread.archived) return
  const next = thread.queuedMessages?.shift()
  if (!next) return
  thread.queueDraining = true
  try {
    await runMessageTurn(thread, next.content, next.images)
  } finally {
    thread.queueDraining = false
    scheduleQueuedDrain(thread)
    reloadBackgroundThreadIfIdle(thread)
  }
}

function removeQueuedMessage(thread: ThreadItem, id: string): void {
  if (!thread.queuedMessages) return
  thread.queuedMessages = thread.queuedMessages.filter((item) => item.id !== id)
}

function queuedMessagePreview(message: QueuedMessage): string {
  const text = message.content.trim().replace(/\s+/g, ' ')
  if (text) return text.length > 120 ? `${text.slice(0, 120)}…` : text
  return message.images.length > 1 ? `发送 ${message.images.length} 张图片` : '发送图片'
}

function queuedImageUrl(image?: ImageAttachment): string {
  return image ? `data:${image.mediaType};base64,${image.data}` : ''
}

async function steerQueuedMessage(thread: ThreadItem, queued: QueuedMessage): Promise<void> {
  if (!thread.running || !thread.sessionId || !daemonState.value.connected) return
  const index = thread.queuedMessages?.findIndex((item) => item.id === queued.id) ?? -1
  if (index < 0) return
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const guidanceMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content: queued.content,
    images: queued.images,
    createdAt: Date.now()
  }
  // Reflect the steer immediately. The daemon request is still awaited below so
  // a rejection can put the item back into the normal queue without losing it.
  thread.queuedMessages?.splice(index, 1)
  thread.messages.push(guidanceMessage)
  thread.updatedAt = Date.now()
  thread.pendingGuidance = (thread.pendingGuidance ?? 0) + 1
  if (thread.id === activeThreadId.value) void scrollToBottom(true, true)
  try {
    await window.seekclaw.daemon.request('agent.steer', {
      message: queued.content,
      images: plainImages(queued.images),
      sessionId: thread.sessionId,
      requestId: thread.requestId,
      ...sessionScope(thread, project)
    }, { timeoutMs: CHAT_FIRST_EVENT_TIMEOUT_MS })
  } catch {
    // Keep the message queued when the active turn has just finished or the Runtime rejects it.
    const messageIndex = thread.messages.findIndex((item) => item.id === guidanceMessage.id)
    if (messageIndex >= 0) thread.messages.splice(messageIndex, 1)
    thread.queuedMessages ??= []
    if (!thread.queuedMessages.some((item) => item.id === queued.id))
      thread.queuedMessages.splice(Math.min(index, thread.queuedMessages.length), 0, queued)
    thread.pendingGuidance = Math.max(0, (thread.pendingGuidance ?? 1) - 1)
  }
}

async function runMessageTurn(thread: ThreadItem, content: string, images: ImageAttachment[]): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || thread.archived || thread.running) return
  const reasoningLevel = thread.reasoningLevel ?? ReasoningLevel.High
  const turnToken = makeId()

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
  thread.activeTurnToken = turnToken
  thread.assistantId = assistant.id
  thread.requestId = undefined
  let terminalAssistant = assistant
  autoFollowConversation.value = true
  await scrollToBottom(true, true)

  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    let sessionCreated = false
    if (!thread.sessionId) {
      const sessionResponse = await window.seekclaw.daemon.request('session.new', {
        ...scope,
        reasoningLevel,
        networkEnabled: thread.networkEnabled ?? true
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
    const response = await window.seekclaw.daemon.request('chat', {
      message: content,
      images: plainImages(images),
      sessionId: thread.sessionId,
      reasoningLevel,
      ...scope
    }, { timeoutMs: CHAT_FIRST_EVENT_TIMEOUT_MS })
    rememberFinishedRequest(thread, response.id)
    const finalAssistant = thread.messages.findLast((item) => item.role === 'assistant') ?? assistant
    terminalAssistant = finalAssistant
    if (finalAssistant.state !== 'error') {
      finalAssistant.state = 'done'
      // Streamed delta events can lose the IPC race to the request's terminal
      // response, leaving the bubble empty even though the daemon finished.
      // Fall back to the latest assistant bubble: a mid-turn model step or steer
      // may have superseded the original placeholder captured above.
      if (!finalAssistant.content && response.data) finalAssistant.content = response.data
    }
  } catch (error) {
    const failedAssistant = thread.messages.findLast((item) => item.role === 'assistant') ?? assistant
    terminalAssistant = failedAssistant
    failedAssistant.state = 'error'
    if (!failedAssistant.content) {
      const detail = error instanceof Error ? error.message : String(error)
      failedAssistant.content = `无法连接 SeekClaw Daemon。\n\n\`\`\`text\n${detail}\n\`\`\``
    }
  } finally {
    // A terminal event may already have started the next queued turn. Do not let
    // this older request clear that newer turn's state.
    if (thread.activeTurnToken !== turnToken) return
    // Belt-and-braces: finalize every leftover "..." placeholder when this turn
    // settles. The captured assistant can be a stale bubble (a mid-turn steer
    // created a fresh one) and the terminal event itself may have been dropped
    // by the finished-request guard, so walk all messages instead of one.
    finalizeAssistantBubbles(thread.messages, terminalAssistant.state === 'error' ? 'error' : 'done')
    thread.activeTurnToken = undefined
    thread.running = false
    thread.requestId = undefined
    thread.assistantId = undefined
    if (thread.id === activeThreadId.value) await scrollToBottom(true)
    scheduleQueuedDrain(thread)
  }
}

function reloadBackgroundThreadIfIdle(thread: ThreadItem): void {
  if (thread.id === activeThreadId.value
    || thread.running
    || thread.queueDraining
    || thread.queuedMessages?.length
    || thread.pendingGuidance)
    return
  void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
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
  // The selected task is about to change; stale session reads must not update
  // the loading state for the replacement task.
  conversationSelectionToken++
  conversationLoading.value = false
  conversationLoadError.value = ''
  const fallback = threads.value
    .filter((thread) => thread.projectId === projectId && !thread.archived)
    .sort((left, right) => right.updatedAt - left.updatedAt)[0]
  if (fallback) {
    void selectThread(fallback.id)
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

async function initializeProjectWorkspace(project: ProjectItem): Promise<void> {
  if (!daemonState.value.connected) {
    await reconnectDaemon()
    if (!daemonState.value.connected) return
  }
  try {
    await ensureRuntimeProject(project)
    const response = await window.seekclaw.daemon.request('workspace.init')
    const result = JSON.parse(response.data) as { created: string[] }
    await window.seekclaw.notify(
      '工作区元数据已初始化',
      result.created.length > 0 ? `已创建 ${result.created.length} 项元数据` : '工作区元数据已就绪'
    )
  } catch (reason) {
    await window.seekclaw.notify('工作区元数据初始化失败', reason instanceof Error ? reason.message : String(reason))
  }
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
    title: '归档任务',
    message: `归档全部 ${targets.length} 个任务？`,
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
    title: '删除全部任务',
    message: `永久删除全部 ${targets.length} 个任务？此操作无法撤销。`,
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
    await window.seekclaw.daemon.request('project.remove', { id: project.id })
  } catch {
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
  if (event.event === 'schedule.upcoming') return
  if (event.event === 'schedule.updated') {
    void handleScheduleUpdated()
    return
  }
  const isChatRequest = event.requestMethod === 'chat'
    || event.requestMethod === 'agent.runTurn'
    || event.requestMethod === 'agent/runTurn'
  // The steer request only acknowledges enqueueing (or reports that the turn
  // just finished). Its error/result must never be interpreted as the active
  // chat turn's terminal state.
  if (event.requestMethod === 'agent.steer') return
  if (!isChatRequest && !event.sessionId) return
  const thread = (event.sessionId
    ? threads.value.find((item) => item.sessionId === event.sessionId)
    : undefined)
    ?? threads.value.find((item) => item.requestId === event.id)
    ?? (!event.sessionId && activeThread.value?.running ? activeThread.value : undefined)
  if (!thread) return

  // Guidance is drained by the Agent and forwarded under the chat request id, so it
  // can arrive after the turn's terminal response already resolved (IPC race). It
  // must be applied before the stale-request guards below: the optimistic guidance
  // message is already rendered, and its pending counter always needs releasing.
  if (event.event === 'steer') {
    thread.pendingGuidance = Math.max(0, (thread.pendingGuidance ?? 1) - 1)
    const currentAssistant = thread.messages.find((item) => item.id === thread.assistantId)
    // The guidance user message is rendered immediately after the current
    // assistant placeholder. Always start a fresh assistant bubble so the next
    // streamed answer cannot appear above the guidance message when the previous
    // answer was still blank/thinking.
    if (currentAssistant) {
      currentAssistant.state = 'done'
      const nextAssistant: ChatMessage = {
        id: makeId(),
        role: 'assistant',
        content: '',
        thinking: '',
        tools: [],
        state: 'thinking',
        createdAt: Date.now()
      }
      thread.messages.push(nextAssistant)
      thread.assistantId = nextAssistant.id
      if (thread.id === activeThreadId.value) void scrollToBottom(true, true)
    } else if (!thread.running && thread.sessionId) {
      // The turn ended before the guidance bubble could be created (for example it
      // was cancelled while a steer was still in flight). Reload the persisted
      // session so the optimistic copy is replaced by the real guidance/reply pair.
      void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
    }
    // A steer may be the very first event of a turn; keep its chat request id so
    // cancellation still targets this task instead of every turn on the connection.
    if (isChatRequest) thread.requestId ??= event.id
    return
  }

  // A terminal response can reach the request continuation before its renderer
  // event callback. If the next queued turn has already started, ignore all
  // delayed events belonging to the completed request — but still finalize any
  // leftover "..." placeholder bubbles, otherwise a dropped terminal event
  // leaves them on screen forever.
  if (isChatRequest && isFinishedRequest(thread, event.id)) {
    if (event.event === 'done' || event.event === 'cancelled')
      finalizeAssistantBubbles(thread.messages, 'done')
    else if (event.event === 'error')
      finalizeAssistantBubbles(thread.messages, 'error')
    return
  }
  if (isChatRequest && thread.requestId !== undefined && thread.requestId !== event.id) return
  // Steering acknowledgements have their own request id and must never replace
  // the id of the active chat turn (used by cancellation and stale-event checks).
  if (isChatRequest) thread.requestId ??= event.id
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
    case 'status': {
      if (message && event.data.toLocaleLowerCase().includes('thinking')) message.state = 'thinking'
      thread.phase = phaseLabel(event.data)
      break
    }
    case 'image_view': {
      if (!message) break
      const imageId = typeof event.details?.imageId === 'string' ? event.details.imageId : ''
      if (!imageId) break
      message.viewedImages ??= []
      if (!message.viewedImages.some((image) => image.id === imageId))
        message.viewedImages.push({ id: imageId, name: event.data || '图片' })
      break
    }
    case 'tool_start': {
      if (!message) break
      thread.phase = '执行工具'
      message.tools ??= []
      message.tools.push({
        id: eventCallId ?? `${event.id}-${message.tools.length}`,
        callId: eventCallId,
        name: event.data,
        detail: typeof event.details?.summary === 'string' ? event.details.summary : undefined,
        state: 'running'
      })
      break
    }
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
    case 'model_start': {
      thread.stats ??= {}
      thread.stats.llmRounds = (thread.stats.llmRounds ?? 0) + 1
      const step = Number(event.details?.step) || 0
      const previousHasOutput = Boolean(
        message?.content || message?.thinking || (message?.tools?.length ?? 0))
      if (message && step > 1 && previousHasOutput) {
        message.state = 'done'
        const nextAssistant: ChatMessage = {
          id: makeId(),
          role: 'assistant',
          content: '',
          thinking: '',
          tools: [],
          state: 'thinking',
          createdAt: Date.now()
        }
        thread.messages.push(nextAssistant)
        thread.assistantId = nextAssistant.id
      }
      break
    }
    case 'usage': {
      thread.stats ??= {}
      const inputTokens = Number(event.details?.inputTokens) || 0
      const outputTokens = Number(event.details?.outputTokens) || 0
      const totalInputTokens = Number(event.details?.totalInputTokens) || inputTokens
      const cachedInputTokens = Number(event.details?.cachedInputTokens) || 0
      const elapsedMs = Number(event.details?.elapsedMs) || 0
      thread.stats.inputTokens = (thread.stats.inputTokens ?? 0) + inputTokens
      thread.stats.outputTokens = (thread.stats.outputTokens ?? 0) + outputTokens
      thread.stats.totalInputTokens = (thread.stats.totalInputTokens ?? 0) + totalInputTokens
      thread.stats.cachedInputTokens = (thread.stats.cachedInputTokens ?? 0) + cachedInputTokens
      thread.stats.outputElapsedMs = (thread.stats.outputElapsedMs ?? 0) + elapsedMs
      break
    }
    case 'workflow': {
      const kind = String(event.details?.kind ?? '')
      const step = Number(event.details?.step) || 0
      const label = event.data || String(event.details?.label ?? '')
      const detail = typeof event.details?.detail === 'string' ? event.details.detail : undefined
      if (kind === 'start') {
        thread.workflow = { nodes: [], activeId: null }
        thread.turnStepHighWater = 0
      } else if (step > (thread.turnStepHighWater ?? 0)) {
        thread.stats ??= {}
        thread.stats.executionSteps = (thread.stats.executionSteps ?? 0)
          + step - (thread.turnStepHighWater ?? 0)
        thread.turnStepHighWater = step
      }
      thread.workflow ??= { nodes: [], activeId: null }
      if (thread.workflow.activeId) {
        const previous = thread.workflow.nodes.find((node) => node.id === thread.workflow?.activeId)
        if (previous && previous.state === 'running') previous.state = 'done'
      }
      const nodeKind = (['start', 'think', 'tool', 'verify', 'repair', 'compact', 'done', 'error'] as const)
        .includes(kind as never) ? kind as WorkflowKind : 'think'
      const node = {
        id: `${event.id}:${thread.workflow.nodes.length}:${kind}`,
        step,
        kind: nodeKind,
        label,
        detail,
        state: (kind === 'done' || kind === 'error' ? kind : 'running') as 'running' | 'done' | 'error'
      }
      thread.workflow.nodes.push(node)
      thread.workflow.activeId = node.id
      // Once the turn moves to build verification the visible answer is
      // complete; stop showing the "..." placeholder until repair continues the bubble.
      if (kind === 'verify'
        && message?.content
        && (message.state === 'thinking' || message.state === 'streaming')) {
        message.state = 'done'
      }
      break
    }
    case 'done':
    case 'cancelled':
      if (message) {
        message.state = 'done'
        if (!message.content && event.data) message.content = event.data
      }
      // Safety net: the assistantId pointer can miss the real bubble (e.g. a steer
      // created a fresh bubble or the message list was replaced mid-flight), which
      // used to leave the "..." placeholder on screen forever after the turn ended.
      finalizeAssistantBubbles(thread.messages, 'done')
      if (isChatRequest) rememberFinishedRequest(thread, event.id)
      thread.activeTurnToken = undefined
      thread.pendingGuidance = 0
      thread.running = false
      thread.requestId = undefined
      thread.assistantId = undefined
      thread.phase = undefined
      if (thread.workflow?.activeId) {
        const last = thread.workflow.nodes.find((node) => node.id === thread.workflow?.activeId)
        if (last && last.state === 'running') last.state = 'done'
      }
      scheduleQueuedDrain(thread)
      if (isBackgroundThread) {
        void window.seekclaw.notify('后台任务完成', `「${thread.title}」已完成`)
      }
      if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
      break
    case 'error':
      if (message) {
        message.state = 'error'
        appendModelError(message, event.data)
      }
      finalizeAssistantBubbles(thread.messages, 'error')
      if (isChatRequest) rememberFinishedRequest(thread, event.id)
      thread.activeTurnToken = undefined
      thread.pendingGuidance = 0
      thread.running = false
      thread.requestId = undefined
      thread.assistantId = undefined
      thread.phase = undefined
      if (thread.workflow?.activeId) {
        const last = thread.workflow.nodes.find((node) => node.id === thread.workflow?.activeId)
        if (last && last.state === 'running') last.state = 'error'
      }
      scheduleQueuedDrain(thread)
      if (isBackgroundThread) {
        void window.seekclaw.notify('后台任务执行失败', `「${thread.title}」执行失败`)
      }
      if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
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

async function changeNetwork(enabled: boolean): Promise<void> {
  const thread = activeThread.value
  if (!thread || thread.archived) return
  thread.networkEnabled = enabled
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!thread.sessionId || !daemonState.value.connected || (thread.projectId && !project)) return
  try {
    await window.seekclaw.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      networkEnabled: enabled
    })
  } catch { /* The in-memory toggle is still applied to the next turn. */ }
}

async function changeModel(model: string): Promise<void> {
  const previousModel = activeModel.value
  activeModel.value = model
  if (!daemonState.value.connected || !model) return
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

async function optimizePrompt(text: string): Promise<string> {
  if (!daemonState.value.connected) throw new Error('Runtime 未连接，无法优化提示词。')
  if (!activeModel.value) throw new Error('尚未配置模型，请先在设置中新建 Provider 和模型。')
  const params: Record<string, unknown> = { text }
  params.model = activeModel.value
  const response = await window.seekclaw.daemon.request('prompt.optimize', params)
  return response.data
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
  measureConversationViewport()
  window.addEventListener('resize', measureConversationViewport)
  if (!daemonState.value.connected) projects.value.forEach((project) => { project.loaded = true })
  if (activeThread.value) composer.value?.focus()
})

onBeforeUnmount(() => {
  appReadyForRecovery = false
  window.removeEventListener('resize', measureConversationViewport)
  unsubscribeEvent?.()
  unsubscribeState?.()
})

watch(theme, applyTheme)
</script>

<template>
  <div class="app-shell">
    <AppTitleBar :sidebar-open="sidebarOpen" :project-path="globalTaskActive ? undefined : activeProject?.path"
      @toggle-sidebar="sidebarOpen = !sidebarOpen" @new-task="newTask(selectedProjectId || undefined)"
      @open-workspace="openWorkspace" @show-project="showActiveProject" @open-settings="openSettings('general')"
      @focus-composer="composer?.focus()" @open-terminal="openProjectTerminal" @open-git-changes="openGitPanel('diff')"
      @open-git-history="openGitPanel('history')" @open-diagnostics="openSettings('diagnostics')"
      @open-dev-tools="openDevTools"
      @open-about="aboutOpen = true" />

    <div class="app-body" v-show="activePage === 'main'" :class="{ 'sidebar-collapsed': !sidebarOpen }">
      <Transition name="sidebar-slide">
        <Sidebar v-if="sidebarOpen" :projects="projects" :threads="threads" :active-thread-id="activeThreadId"
          :active-project-id="selectedProjectId" :version="appInfo.version" @new-task="newTask"
          @open-workspace="openWorkspace" @select-thread="selectThread" @task-settings="openTaskSettings"
          @archive-task="archiveTask" @restore-task="restoreTask" @delete-task="deleteTask"
          @delete-project="deleteProject" @archive-project-tasks="archiveProjectTasks"
          @initialize-project-workspace="initializeProjectWorkspace"
          @delete-project-tasks="deleteProjectTasks" @archive-global-tasks="archiveGlobalTasks"
          @delete-global-tasks="deleteGlobalTasks" @open-archived="openArchivedTasks"
          @open-scheduled-tasks="openScheduledTasks" @open-extensions="openExtensions('mcp')"
          @open-official-skills="openOfficialSkills" @open-settings="openSettings('general')" />
      </Transition>
      <Transition name="scrim-fade">
        <button v-if="sidebarOpen" class="sidebar-scrim" title="关闭侧栏" @click="sidebarOpen = false" />
      </Transition>

      <div class="workspace-content">
        <main class="workspace-main" v-show="activePage === 'main'">
          <header class="conversation-header">
            <div class="conversation-title">
              <Globe2 v-if="globalTaskActive" :size="20" />
              <Folder v-else :size="20" />
              <strong>{{ conversationTitle }}</strong>
              <!--  <small v-if="activeThread">{{ activeProject?.name || '任务' }}</small>-->
              <span v-if="activeThread?.running && activeThread?.phase" class="task-phase-chip">
                <span class="phase-dot" />{{ activeThread.phase }}
              </span>
            </div>
            <div class="conversation-actions">
              <label class="conversation-search" :class="{ active: Boolean(conversationQuery.trim()) }">
                <Search :size="15" />
                <input v-model="conversationQuery" placeholder="搜索对话" aria-label="搜索对话" />
                <button v-if="conversationQuery" type="button" class="conversation-search-clear" title="清除搜索"
                  @click="conversationQuery = ''">
                  <X :size="13" />
                </button>
              </label>
              <button v-if="!daemonState.connected" class="connection-button"
                :title="daemonState.error || daemonState.endpoint" :disabled="reconnecting" @click="reconnectDaemon">
                <Circle :size="9" fill="currentColor" />
                {{ runtimeConnectionLabel }}
                <RefreshCw :class="{ spin: reconnecting }" :size="14" />
              </button>
              <button v-if="activeProject" class="open-location-button" @click="showActiveProject">
                <FolderOpen :size="17" />
                <span>打开位置</span>
              </button>
              <button v-if="activeProject" class="icon-button project-tool-button" title="在项目目录打开终端"
                @click="openProjectTerminal">
                <TerminalSquare :size="18" />
              </button>
              <button v-if="activeProject" class="icon-button project-tool-button" title="查看代码更改"
                @click="openGitPanel('diff')">
                <Braces :size="18" />
              </button>
              <button v-if="activeProject" class="icon-button project-tool-button" title="查看 Git 提交记录"
                @click="openGitPanel('history')">
                <History :size="18" />
              </button>
              <button class="icon-button project-tool-button" :class="{ active: workflowOpen }" title="实时执行流程图"
                @click="workflowOpen = !workflowOpen">
                <Workflow :size="18" />
              </button>
              <button class="icon-button" title="任务设置" :disabled="!activeThread" @click="openTaskSettings()">
                <MoreHorizontal :size="18" />
              </button>
              <button class="icon-button" title="切换侧栏" @click="sidebarOpen = !sidebarOpen">
                <PanelRight :size="18" />
              </button>
            </div>
          </header>

          <section ref="scrollArea" class="conversation-scroll" @scroll="handleConversationScroll">
            <div v-if="conversationLoading" class="conversation-loading" role="status" aria-live="polite">
              <LoaderCircle :size="20" class="spin" />
              <span>正在加载会话…</span>
            </div>
            <div v-else-if="conversationLoadError" class="empty-state conversation-load-error">
              <h1>会话加载失败</h1>
              <p>{{ conversationLoadError }}</p>
              <button class="secondary-button empty-state-action" @click="selectThread(activeThreadId)">重新加载</button>
            </div>
            <div v-else-if="activeThread && activeThread.messages.length > 0" class="conversation-content">
              <template v-if="virtualWindow.active">
                <div class="virtual-pad" :style="{ height: `${virtualWindow.topPad}px` }" />
                <template v-for="item in virtualWindow.items" :key="item.message.id">
                  <div v-if="item.step" class="step-divider"><span>步骤 {{ item.step }}</span></div>
                  <div v-measure="item.message.id" class="virtual-message">
                    <ConversationMessage :message="item.message" :image-sources="activeImageSources"
                      :streaming="item.message.id === activeThread?.assistantId && activeThread?.running === true"
                      :dimmed="Boolean(conversationQuery.trim()) && !messageMatches(item.message, conversationQuery)"
                      @open-diff="openToolDiff" @continue="continueAssistant" @regenerate="regenerateMessage" />
                  </div>
                </template>
                <div class="virtual-pad" :style="{ height: `${virtualWindow.bottomPad}px` }" />
              </template>
              <template v-else>
                <template v-for="item in conversationItems" :key="item.message.id">
                  <div v-if="item.step" class="step-divider"><span>步骤 {{ item.step }}</span></div>
                  <ConversationMessage :message="item.message" :image-sources="activeImageSources"
                    :streaming="item.message.id === activeThread?.assistantId && activeThread?.running === true"
                    :dimmed="Boolean(conversationQuery.trim()) && !messageMatches(item.message, conversationQuery)"
                    @open-diff="openToolDiff" @continue="continueAssistant" @regenerate="regenerateMessage" />
                </template>
              </template>
            </div>
            <div v-else-if="activeThread" class="empty-state">
              <h1>今天从哪里开始？</h1>
              <p>{{ activeProject?.name || '任务 · 无工作目录' }}</p>
              <div v-if="!activeThread?.archived" class="starter-prompts" aria-label="快速开始">
                <button v-for="prompt in starterPrompts" :key="prompt.label" type="button" class="starter-prompt-card"
                  :data-tone="prompt.tone" @click="useStarterPrompt(prompt.label)">
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

          <footer class="composer-region">
            <div v-if="activeThread?.queuedMessages?.length" class="pending-message-stack" aria-label="等待发送的消息">
              <div v-for="queued in activeThread.queuedMessages" :key="queued.id" class="pending-message-card">
                <div class="pending-message-main">
                  <CornerDownLeft :size="15" aria-hidden="true" />
                  <img v-if="queued.images.length" class="pending-message-image" :src="queuedImageUrl(queued.images[0])"
                    :alt="queued.images[0]?.name || '图片'">
                  <span>{{ queuedMessagePreview(queued) }}</span>
                </div>
                <div class="pending-message-actions">
                  <button type="button" class="pending-message-action"
                    :disabled="!activeThread.running || !activeThread.sessionId || !daemonState.connected"
                    title="作为附加指导发送，不打断当前 AI 回合" @click="steerQueuedMessage(activeThread, queued)">
                    <CornerDownLeft :size="14" /> 引导
                  </button>
                  <button type="button" class="pending-message-action icon-only" title="删除等待中的消息"
                    @click="removeQueuedMessage(activeThread, queued.id)">
                    <Trash2 :size="14" />
                  </button>
                </div>
              </div>
            </div>

            <WorkflowPanel :workflow="activeThread?.workflow" :open="workflowOpen" @close="workflowOpen = false" />
            <Composer ref="composer" :busy="busy"
              :disabled="!activeThread || activeThread.archived || conversationLoading" :model="activeModel"
              :models="models" :mode="mode" :task-id="activeThread?.id" :supports-images="activeModelSupportsImages"
              :reasoning-level="activeReasoningLevel" :network-enabled="activeThread?.networkEnabled ?? true"
              :optimize-prompt="optimizePrompt"
              @send="sendMessage" @stop="stopTurn" @change-model="changeModel" @change-mode="changeMode"
              @change-reasoning-level="changeReasoningLevel" @change-network="changeNetwork" />
            <p class="composer-caption">{{ composerCaption }}</p>
          </footer>
        </main>

        <GitWorkspacePanel v-show="activePage === 'main'" :open="gitPanelOpen" :project="activeProject"
          :initial-tab="gitPanelTab" :diff-override="toolDiff" :width="gitPanelWidth" @close="closeGitPanel"
          @resize="resizeGitPanel" @open-terminal="openProjectTerminal" />
      </div>
    </div>

    <SettingsDialog :open="activePage === 'settings' || activePage === 'extensions'"
      :page="activePage === 'extensions' ? 'extensions' : 'settings'" :theme="theme"
      :daemon-connected="daemonState.connected" :daemon-endpoint="daemonState.endpoint"
      :initial-section="activePage === 'settings' ? settingsSection : extensionsSection" @close="closePage"
      @change-theme="applyTheme" @reconnect="reconnectDaemon" @open-workspace="openWorkspace"
      @open-official-skills="openOfficialSkills" @runtime-changed="refreshRuntimeState" />

    <OfficialSkillsDialog :open="activePage === 'official-skills'" @close="closePage" />

    <ScheduledTasksDialog :open="activePage === 'scheduled'" :projects="projects" @close="closePage" />

    <ArchivedTasksDialog :open="activePage === 'archived'" :projects="projects" :threads="threads" @close="closePage"
      @select-thread="selectArchivedThread" @restore-task="restoreTask" @delete-task="deleteTask"
      @delete-all="deleteArchivedTasks" />

    <AboutDialog :open="aboutOpen" :app-info="appInfo" @close="aboutOpen = false" />


    <TaskSettingsDialog :open="Boolean(taskSettingsThreadId)" :thread="settingsThread" :project="settingsProject"
      @close="taskSettingsThreadId = ''" @save-title="saveTaskTitle"
      @archive="settingsThread && archiveTask(settingsThread)" @restore="settingsThread && restoreTask(settingsThread)"
      @delete="settingsThread && deleteTask(settingsThread)" />

    <RuntimeReconnectDialog :open="Boolean(reconnectPrompt)" :startup="reconnectPrompt?.startup ?? false"
      :endpoint="daemonState.endpoint" :error="reconnectPrompt?.error" @retry="continueRuntimeReconnect"
      @cancel="cancelRuntimeReconnect" />

    <ConfirmDialog />
  </div>
</template>
