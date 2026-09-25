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
  X
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { AppearanceTheme, AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import AppTitleBar from './components/AppTitleBar.vue'
import AboutDialog from './components/AboutDialog.vue'
import ArchivedTasksDialog from './components/ArchivedTasksDialog.vue'
import ScheduledTasksDialog from './components/ScheduledTasksDialog.vue'
import Composer from './components/Composer.vue'
import ConfigAnomalyDialog from './components/ConfigAnomalyDialog.vue'

import ConfirmDialog from './components/ConfirmDialog.vue'
import ConversationMessage from './components/ConversationMessage.vue'
import EditMessageDialog from './components/EditMessageDialog.vue'
import GitWorkspacePanel from './components/GitWorkspacePanel.vue'

import OfficialSkillsDialog from './components/OfficialSkillsDialog.vue'
import ProjectPropertiesDialog from './components/ProjectPropertiesDialog.vue'
import RuntimeReconnectDialog from './components/RuntimeReconnectDialog.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import TaskSettingsDialog from './components/TaskSettingsDialog.vue'
import TaskStepList from './components/TaskStepList.vue'
import { computeTurnTaskSteps, type TaskStep } from './task-planner'
import { confirmAction } from './confirmation'
import { finalizeAssistantBubbles } from './conversation-state'
import { isForbiddenProjectPath } from './project-paths'
import { retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'
import { ReasoningLevel } from './types'
import type { ChatMessage, FileAttachment, ImageAttachment, ProjectItem, QueuedMessage, ThreadItem, ThreadStats, ToolActivity } from './types'
import {
  formatPromptWithFiles,
  hydrateMessages,
  makeId,
  messageMatches,
  normalizePath,
  normalizeReasoningLevel,
  pathName,
  phaseLabel,
  plainImages,
  queuedImageUrl,
  queuedMessagePreview,
  samePath,
  sessionScope,
  sessionStats,
  updateThreadTitle
} from './app-helpers'
import type { RuntimeModelCatalogItem, RuntimeProject, RuntimeSession, RuntimeWorkspace } from './app-helpers'
import {
  refreshAllProjectSessions as refreshAllProjectSessionsFromStore,
  refreshGlobalSessions as refreshGlobalSessionsFromStore,
  refreshProjectSessions as refreshProjectSessionsFromStore,
  reloadThreadSession as reloadThreadSessionFromStore
} from './session-sync'
import { createThreadActions } from './thread-actions'
import { createDaemonEventHandler } from './daemon-event-handler'

const PROJECTS_STORAGE_KEY = 'seekclaw-projects-v2'
const IMPLICIT_DOCUMENTS_MIGRATION_KEY = 'seekclaw-projects-remove-implicit-documents-v2'
// The daemon starts a chat turn within milliseconds of receiving the request,
// so a request that produces no event at all within this window is stuck (for
// example the payload never reached the daemon). Reject it instead of leaving
// the task loading forever; once the first event arrives the turn is confirmed
// running and may legitimately take minutes.
const CHAT_FIRST_EVENT_TIMEOUT_MS = 30_000
const starterPrompts = [
  { label: '探索并理解代码', icon: Telescope, tone: 'blue' },
  { label: '构建新功能、应用或工具', icon: Hammer, tone: 'purple' },
  { label: '审查代码并提出修改建议', icon: RefreshCw, tone: 'green' },
  { label: '修复问题和失败', icon: Bug, tone: 'orange' }
] as const
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
const gitPanelOpen = ref(false)
const gitPanelTab = ref<'diff' | 'history'>('diff')
const gitPanelWidth = ref(560)
const toolDiff = ref<{ path: string; diff: string } | null>(null)

const settingsSection = ref<'general' | 'models' | 'mcp' | 'skills' | 'diagnostics' | 'advanced'>('general')
const extensionsSection = ref<'mcp' | 'skills'>('mcp')
const taskSettingsThreadId = ref('')
const activePropertiesProject = ref<ProjectItem | null>(null)
const storedTheme = localStorage.getItem('seekclaw-theme')
const theme = ref<AppearanceTheme>(
  storedTheme === 'light' || storedTheme === 'dark' || storedTheme === 'system' ? storedTheme : 'system')
const daemonState = ref<DaemonState>({ connected: false, endpoint: '' })
const reconnecting = ref(false)
const reconnectAttempt = ref(0)
const reconnectPrompt = ref<{ startup: boolean; error?: string } | null>(null)
const configAnomaly = ref<{
  hasAnomaly: boolean
  detail?: string
  configFile?: string
  backupFile?: string
} | null>(null)
const rebuildingConfig = ref(false)
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
const conversationSelectionToken = { value: 0 }

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

const sessionSyncContext = { daemonState, threads }

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

async function refreshProjectSessions(project: ProjectItem): Promise<void> {
  await refreshProjectSessionsFromStore(sessionSyncContext, project)
}

async function refreshGlobalSessions(): Promise<void> {
  await refreshGlobalSessionsFromStore(sessionSyncContext)
}

async function refreshAllProjectSessions(): Promise<void> {
  await refreshAllProjectSessionsFromStore(sessionSyncContext, projects.value)
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
    const [projectResponse, modelResponse, workspaceResponse, modeResponse, catalogResponse, configStatusResponse] = await Promise.all([
      window.seekclaw.daemon.request('project.list'),
      window.seekclaw.daemon.request('model.list'),
      window.seekclaw.daemon.request('workspace.get'),
      window.seekclaw.daemon.request('agent.mode.get'),
      window.seekclaw.daemon.request('model.catalog'),
      window.seekclaw.daemon.request('config.status').catch(() => null)
    ])
    if (configStatusResponse) {
      try {
        const status = JSON.parse(configStatusResponse.data) as {
          hasAnomaly: boolean
          detail?: string
          configFile?: string
          backupFile?: string
        }
        if (status.hasAnomaly) {
          configAnomaly.value = status
        } else {
          configAnomaly.value = null
        }
      } catch { /* ignore parse error */ }
    }
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
    } else if (activeThread.value && !activeThread.value.sessionLoaded) {
      await selectThread(activeThread.value.id)
    }
  } catch {
    models.value = []
    modelCatalog.value = []
  }
}

async function handleRebuildConfig(): Promise<void> {
  rebuildingConfig.value = true
  try {
    await window.seekclaw.daemon.request('config.rebuild')
    configAnomaly.value = null
    await loadRuntimeState()
  } catch (err) {
    console.error('Failed to rebuild config:', err)
  } finally {
    rebuildingConfig.value = false
  }
}

function handleDaemonState(state: DaemonState): void {
  daemonState.value = state
  if (state.connected) {
    automaticReconnectPaused = false
    reconnectPrompt.value = null
    return
  }
  if (conversationLoading.value) {
    conversationLoading.value = false
    conversationLoadError.value = '已与 SeekClaw Runtime 断开连接，正在尝试重连…'
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
  conversationSelectionToken.value++
  conversationLoading.value = false
  conversationLoadError.value = ''
  await refreshProjectSessions(project).catch(() => undefined)
}

function openSettings(section: typeof settingsSection.value = 'general'): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  settingsSection.value = section
  activePage.value = 'settings'
}

function openExtensions(section: 'mcp' | 'skills' = 'mcp'): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  extensionsSection.value = section
  activePage.value = 'extensions'
}

function openArchivedTasks(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  activePage.value = 'archived'
}

function openScheduledTasks(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  activePage.value = 'scheduled'
}

function openOfficialSkills(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
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
  conversationSelectionToken.value++
  conversationLoading.value = false
  conversationLoadError.value = ''
  void nextTick(() => composer.value?.focus())
}

async function branchFromMessage(message: ChatMessage): Promise<void> {
  const currentThread = activeThread.value
  if (!currentThread) return
  const messageIndex = currentThread.messages.findIndex((m) => m.id === message.id)
  if (messageIndex < 0) return

  const project = projects.value.find((item) => item.id === currentThread.projectId)
  const slicedMessages = currentThread.messages.slice(0, messageIndex + 1)
  const branchTitle = currentThread.title.includes('分支')
    ? currentThread.title
    : `${currentThread.title} (分支)`

  let newSessionId: string | undefined
  if (currentThread.sessionId && daemonState.value.connected) {
    try {
      const match = /^.*:(\d+)$/.exec(message.id)
      const keepCount = match && match[1] ? parseInt(match[1], 10) + 1 : 0
      const response = await window.seekclaw.daemon.request('session.fork', {
        id: currentThread.sessionId,
        ...sessionScope(currentThread, project),
        keepCount,
        title: branchTitle
      })
      const data = JSON.parse(response.data) as { id: string }
      newSessionId = data.id
    } catch (err) {
      console.error('Failed to fork session on daemon:', err)
    }
  }

  const clonedMessages: ChatMessage[] = slicedMessages.map((m, idx) => ({
    ...m,
    id: newSessionId ? `${newSessionId}:${idx}` : makeId(),
    tools: m.tools?.map((t) => ({ ...t })),
    images: m.images ? [...m.images] : undefined,
    files: m.files ? [...m.files] : undefined,
    viewedImages: m.viewedImages ? [...m.viewedImages] : undefined
  }))

  const newThreadId = newSessionId
    ? (project ? `${project.id}:session:${newSessionId}` : `global:session:${newSessionId}`)
    : makeId()

  const newThread: ThreadItem = {
    id: newThreadId,
    title: branchTitle,
    projectId: project?.id,
    updatedAt: Date.now(),
    messages: clonedMessages,
    sessionId: newSessionId,
    sessionLoaded: true,
    reasoningLevel: currentThread.reasoningLevel ?? ReasoningLevel.High,
    networkEnabled: currentThread.networkEnabled ?? true,
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

  threads.value.unshift(newThread)
  await selectThread(newThread.id)
  void nextTick(() => composer.value?.focus())
}

const editingUserMessage = ref<ChatMessage | null>(null)
const editMessageModifiedFiles = ref<string[]>([])
const editMessagePatches = ref<Array<{ filePath: string; diff: string }>>([])
const editMessageDialogOpen = ref(false)

function onEditUserMessage(message: ChatMessage): void {
  const thread = activeThread.value
  if (!thread || thread.archived) return
  if (thread.running || thread.queueDraining) {
    void window.seekclaw?.notify?.('无法编辑', '当前回合正在执行，请先等待或停止生成后再进行编辑。')
    return
  }

  const promptIndex = thread.messages.findIndex((item) => item.id === message.id)
  if (promptIndex < 0) return

  // Collect subsequent file patches & modified files
  const patches: Array<{ filePath: string; diff: string }> = []
  const filesSet = new Set<string>()

  for (let i = promptIndex + 1; i < thread.messages.length; i++) {
    const msg = thread.messages[i]
    if (!msg) continue
    for (const tool of msg.tools ?? []) {
      if (tool.filePath && tool.diff) {
        patches.push({ filePath: tool.filePath, diff: tool.diff })
        filesSet.add(tool.filePath)
      }
    }
  }

  editingUserMessage.value = message
  editMessageModifiedFiles.value = Array.from(filesSet)
  editMessagePatches.value = patches
  editMessageDialogOpen.value = true
}

async function handleEditConfirm(revertFiles: boolean): Promise<void> {
  const thread = activeThread.value
  const message = editingUserMessage.value
  const patches = editMessagePatches.value
  editMessageDialogOpen.value = false
  if (!thread || !message) return

  const promptIndex = thread.messages.findIndex((item) => item.id === message.id)
  if (promptIndex < 0) return

  const project = projects.value.find((item) => item.id === thread.projectId)
  const workspace = project?.path || appInfo.value.defaultWorkspace

  // Step 1: If reverting file modifications, apply diffs in reverse
  if (revertFiles && patches.length > 0) {
    try {
      const res = await window.seekclaw.project.revertFileDiffs(workspace, patches)
      if (res.failed.length > 0) {
        const failedSummary = res.failed
          .map((f) => `• ${f.filePath} (${f.reason})`)
          .join('\n')
        await confirmAction({
          title: '部分文件撤销失败',
          message: `以下文件未能成功自动撤回（可能已被外部修改或存在冲突），建议您手动检查：\n${failedSummary}`,
          confirmLabel: '我知道了',
          danger: true
        })
      }
    } catch (err) {
      await confirmAction({
        title: '撤销文件修改失败',
        message: `撤回文件修改时出错：${err instanceof Error ? err.message : String(err)}`,
        confirmLabel: '我知道了',
        danger: true
      })
    }
  }

  // Step 2: Truncate backend session in SQLite
  let keepCount: number | null = null
  if (message.id) {
    const match = /^.*:(\d+)$/.exec(message.id)
    if (match && match[1]) {
      keepCount = parseInt(match[1], 10)
    }
  }
  if (keepCount === null) keepCount = promptIndex

  if (thread.sessionId) {
    try {
      await window.seekclaw.daemon.request('session.truncate', {
        id: thread.sessionId,
        ...sessionScope(thread, project),
        keepCount
      })
    } catch (err) {
      console.error('Failed to truncate session:', err)
    }
  }

  // Step 3: Truncate frontend messages
  thread.messages = thread.messages.slice(0, promptIndex)
  thread.phase = undefined
  thread.assistantId = undefined

  // Step 4: Populate Composer and focus
  if (typeof composer.value?.populate === 'function') {
    composer.value.populate(message.content || '', message.images, message.files)
  } else {
    composer.value?.setValue(message.content || '')
    composer.value?.focus()
  }
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

async function reloadThreadSession(thread: ThreadItem, project?: ProjectItem): Promise<void> {
  await reloadThreadSessionFromStore(sessionSyncContext, thread, project)
}

async function selectThread(id: string): Promise<void> {
  const thread = threads.value.find((item) => item.id === id)
  if (!thread) return
  if (activeThread.value && composer.value)
    composerDrafts.set(activeThread.value.id, composer.value.getValue())
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const selectionToken = ++conversationSelectionToken.value
  const needsLoad = Boolean(thread.sessionId && (!thread.sessionLoaded || !thread.running))
  activeThreadId.value = id
  selectedProjectId.value = project?.id ?? ''
  conversationLoadError.value = ''

  if (needsLoad && !daemonState.value.connected) {
    conversationLoading.value = false
    conversationLoadError.value = '未连接到 SeekClaw Runtime，请检查运行时状态或点击重试。'
    return
  }

  conversationLoading.value = needsLoad
  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    if (thread.sessionId && (!thread.sessionLoaded || !thread.running)) {
      const response = await window.seekclaw.daemon.request('session.get', {
        id: thread.sessionId,
        ...scope
      })
      if (selectionToken !== conversationSelectionToken.value) return
      // 让出事件循环，确保骨架屏平滑渲染、侧栏点击与拖拽无卡顿
      await new Promise((resolve) => setTimeout(resolve, 0))
      if (selectionToken !== conversationSelectionToken.value) return

      const saved = JSON.parse(response.data) as RuntimeSession
      thread.messages = hydrateMessages(saved)
      thread.sessionLoaded = true
      thread.title = saved.title || thread.title
      thread.archived = Boolean(saved.archived)
      thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
      thread.networkEnabled = saved.networkEnabled ?? true
      thread.stats = sessionStats(saved)
    }
  } catch (error) {
    thread.sessionLoaded = false
    if (selectionToken === conversationSelectionToken.value) {
      conversationLoadError.value = error instanceof Error
        ? error.message
        : '无法读取此会话，请检查 Runtime 连接后重试。'
    }
  } finally {
    if (selectionToken === conversationSelectionToken.value) {
      conversationLoading.value = false
    }
  }
  if (selectionToken !== conversationSelectionToken.value) return
  autoFollowConversation.value = true
  const draft = composerDrafts.get(thread.id)
  if (draft) composer.value?.setValue(draft)
  await scrollToBottom(false, true)
}

interface ConversationItem {
  message: ChatMessage
  showFooter: boolean
}

const conversationItems = computed<ConversationItem[]>(() => {
  const thread = activeThread.value
  const messages = (thread?.messages ?? []).filter(
    (message) => !message.content?.startsWith('>>> [output truncated]')
  )
  const isThreadRunning = thread?.running === true
  const lastUserIndex = messages.findLastIndex((m) => m.role === 'user')

  return messages.map((message, index) => {
    let showFooter = false
    if (message.role === 'assistant' && Boolean(message.content?.trim())) {
      // If this message belongs to the actively running turn, do not show footer
      const inActiveRunningTurn = isThreadRunning && (lastUserIndex < 0 || index >= lastUserIndex)
      if (!inActiveRunningTurn) {
        // Must be the last assistant message in this turn
        let hasLaterAssistant = false
        for (let j = index + 1; j < messages.length; j++) {
          const next = messages[j]
          if (!next || next.role === 'user') break
          if (next.role === 'assistant') {
            hasLaterAssistant = true
            break
          }
        }
        showFooter = !hasLaterAssistant
      }
    }
    return { message, showFooter }
  })
})

/**
 * The plan shown above the composer, straight from the Agent's own `update_plan`
 * calls. Turns where the Agent did not create a plan render nothing.
 */
const activeThreadTurnSteps = computed<TaskStep[]>(() => {
  const thread = activeThread.value
  if (!thread) return []

  const messages = thread.messages ?? []
  const lastUserIndex = messages.findLastIndex((m) => m.role === 'user')
  const turnAssistants = (lastUserIndex >= 0 ? messages.slice(lastUserIndex + 1) : messages)
    .filter((m) => m.role === 'assistant')

  return computeTurnTaskSteps({
    turnAssistants,
    isTurnRunning: thread.running === true,
    customPlan: thread.customPlan
  })
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

async function sendMessage(content: string, images: ImageAttachment[], files?: FileAttachment[]): Promise<void> {
  const thread = activeThread.value
  const project = thread ? projects.value.find((item) => item.id === thread.projectId) : undefined
  if (!thread || (thread.projectId && !project) || thread.archived) return
  if (!content.trim() && images.length === 0 && (!files || files.length === 0)) return
  if (thread.running || thread.queueDraining) {
    thread.queuedMessages ??= []
    thread.queuedMessages.push({ id: makeId(), content, images, files, createdAt: Date.now() })
    return
  }
  composerDrafts.delete(thread.id)
  await runMessageTurn(thread, content, images, files)
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
    await runMessageTurn(thread, next.content, next.images, next.files)
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
    files: queued.files,
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
      message: formatPromptWithFiles(queued.content, queued.files),
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

async function runMessageTurn(
  thread: ThreadItem,
  content: string,
  images: ImageAttachment[],
  files?: FileAttachment[]
): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || thread.archived || thread.running) return
  const reasoningLevel = thread.reasoningLevel ?? ReasoningLevel.High
  const turnToken = makeId()

  const userMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content,
    images,
    files,
    createdAt: Date.now()
  }
  const assistant: ChatMessage = {
    id: makeId(), role: 'assistant', content: '', thinking: '', tools: [], state: 'thinking', createdAt: Date.now()
  }
  thread.messages.push(userMessage, assistant)
  thread.updatedAt = Date.now()
  const titlePrompt = content.trim()
    || (files?.length ? `处理文件：${files.map((f) => f.name).join('、')}` : '')
    || `查看图片：${images.map((image) => image.name).join('、')}`
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
    const daemonPrompt = formatPromptWithFiles(content, files)
    const response = await window.seekclaw.daemon.request('chat', {
      message: daemonPrompt,
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
    if (thread.sessionId && terminalAssistant.state !== 'error') {
      await reloadThreadSession(thread, project).catch(() => undefined)
    }
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

function openProjectProperties(project: ProjectItem): void {
  activePropertiesProject.value = project
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

const {
  chooseAfterRemoval,
  archiveTask,
  restoreTask,
  archiveProjectTasks,
  archiveGlobalTasks,
  deleteTask,
  deleteGlobalTasks,
  deleteProjectTasks,
  deleteArchivedTasks,
  deleteProject
} = createThreadActions({
  projects,
  threads,
  activeThreadId,
  selectedProjectId,
  taskSettingsThreadId,
  daemonState,
  conversationLoading,
  conversationLoadError,
  conversationSelectionToken,
  selectThread,
  refreshProjectSessions,
  reconnectDaemon
})

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

const handleDaemonEvent = createDaemonEventHandler({
  threads,
  activeThreadId,
  projects,
  handleScheduleUpdated,
  scrollToBottom,
  reloadThreadSession,
  rememberFinishedRequest,
  isFinishedRequest,
  scheduleQueuedDrain,
  reloadBackgroundThreadIfIdle
})

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
      @open-dev-tools="openDevTools" @open-about="aboutOpen = true" />

    <div class="app-body" v-show="activePage === 'main'" :class="{ 'sidebar-collapsed': !sidebarOpen }">
      <Transition name="sidebar-slide">
        <Sidebar v-if="sidebarOpen" :projects="projects" :threads="threads" :active-thread-id="activeThreadId"
          :active-project-id="selectedProjectId" :version="appInfo.version" @new-task="newTask"
          @open-workspace="openWorkspace" @select-thread="selectThread" @task-settings="openTaskSettings"
          @archive-task="archiveTask" @restore-task="restoreTask" @delete-task="deleteTask"
          @delete-project="deleteProject" @archive-project-tasks="archiveProjectTasks"
          @initialize-project-workspace="initializeProjectWorkspace" @open-project-properties="openProjectProperties"
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
              <button v-if="activeProject" class="open-location-button" title="打开项目所在文件夹" @click="showActiveProject">
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
              <button class="icon-button" title="任务设置" :disabled="!activeThread" @click="openTaskSettings()">
                <MoreHorizontal :size="18" />
              </button>
              <button class="icon-button" title="切换侧栏" @click="sidebarOpen = !sidebarOpen">
                <PanelRight :size="18" />
              </button>
            </div>
          </header>

          <section ref="scrollArea" class="conversation-scroll" @scroll="handleConversationScroll">
            <div v-if="conversationLoading" class="conversation-content conversation-skeleton" role="status" aria-label="正在加载会话">
              <div class="skeleton-bubble user" style="width: 130px;" />
              <div class="skeleton-bubble assistant" style="width: 240px;" />
              <div class="skeleton-bubble user" style="width: 85px;" />
              <div class="skeleton-bubble assistant" style="width: 190px;" />
            </div>
            <div v-else-if="conversationLoadError" class="empty-state conversation-load-error">
              <h1>会话加载失败</h1>
              <p>{{ conversationLoadError }}</p>
              <div style="display: flex; gap: 8px; justify-content: center; margin-top: 12px;">
                <button v-if="!daemonState.connected" class="primary-button empty-state-action" @click="reconnectDaemon">连接运行时</button>
                <button class="secondary-button empty-state-action" @click="selectThread(activeThreadId)">重新加载</button>
              </div>
            </div>
            <div v-else-if="activeThread && activeThread.messages.length > 0" class="conversation-content">
              <template v-if="virtualWindow.active">
                <div class="virtual-pad" :style="{ height: `${virtualWindow.topPad}px` }" />
                <template v-for="item in virtualWindow.items" :key="item.message.id">
                  <div v-measure="item.message.id" class="virtual-message">
                    <ConversationMessage :message="item.message" :image-sources="activeImageSources"
                      :streaming="item.message.id === activeThread?.assistantId && activeThread?.running === true"
                      :show-footer="item.showFooter"
                      :dimmed="Boolean(conversationQuery.trim()) && !messageMatches(item.message, conversationQuery)"
                      @open-diff="openToolDiff"
                      @branch="branchFromMessage"
                      @edit="onEditUserMessage" />
                  </div>
                </template>
                <div class="virtual-pad" :style="{ height: `${virtualWindow.bottomPad}px` }" />
              </template>
              <template v-else>
                <template v-for="item in conversationItems" :key="item.message.id">
                  <ConversationMessage :message="item.message" :image-sources="activeImageSources"
                    :streaming="item.message.id === activeThread?.assistantId && activeThread?.running === true"
                    :show-footer="item.showFooter"
                    :dimmed="Boolean(conversationQuery.trim()) && !messageMatches(item.message, conversationQuery)"
                    @open-diff="openToolDiff"
                    @branch="branchFromMessage"
                    @edit="onEditUserMessage" />
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

            <TaskStepList :steps="activeThreadTurnSteps" :running="activeThread?.running"
              :phase="activeThread?.phase" />
            <Composer ref="composer" :busy="busy"
              :disabled="!activeThread || activeThread.archived || conversationLoading" :model="activeModel"
              :models="models" :mode="mode" :task-id="activeThread?.id" :supports-images="activeModelSupportsImages"
              :reasoning-level="activeReasoningLevel" :network-enabled="activeThread?.networkEnabled ?? true"
              :optimize-prompt="optimizePrompt" @send="sendMessage" @stop="stopTurn" @change-model="changeModel"
              @change-mode="changeMode" @change-reasoning-level="changeReasoningLevel"
              @change-network="changeNetwork" />
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

    <ProjectPropertiesDialog :open="Boolean(activePropertiesProject)" :project="activePropertiesProject ?? undefined"
      :threads="threads" @close="activePropertiesProject = null" @initialize-workspace="initializeProjectWorkspace"
      @open-extensions="openExtensions" />

    <RuntimeReconnectDialog :open="Boolean(reconnectPrompt)" :startup="reconnectPrompt?.startup ?? false"
      :endpoint="daemonState.endpoint" :error="reconnectPrompt?.error" @retry="continueRuntimeReconnect"
      @cancel="cancelRuntimeReconnect" />

    <ConfigAnomalyDialog v-if="configAnomaly && configAnomaly.hasAnomaly" :open="true" :detail="configAnomaly.detail"
      :config-file="configAnomaly.configFile" :backup-file="configAnomaly.backupFile" :rebuilding="rebuildingConfig"
      @close="configAnomaly = null" @rebuild="handleRebuildConfig" />

    <EditMessageDialog :open="editMessageDialogOpen" :message="editingUserMessage"
      :modified-files="editMessageModifiedFiles" @close="editMessageDialogOpen = false"
      @confirm="handleEditConfirm" />

    <ConfirmDialog />
  </div>
</template>
