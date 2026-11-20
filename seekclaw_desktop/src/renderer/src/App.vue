<script setup lang="ts">
import {
  ArrowDown,
  ArrowUp,
  Bug,
  CornerDownLeft,
  Folder,
  Hammer,
  LoaderCircle,
  Pencil,
  RefreshCw,
  Telescope,
  Trash2
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowReactive, watch } from 'vue'
import type { AppearanceTheme, AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import { finishesQuestionTurn, parseQuestionRequest, parseQuestionResolved, type UserQuestion } from '../../shared/user-questions'
import AppTitleBar from './components/AppTitleBar.vue'
import ActivityBar from './components/ActivityBar.vue'
import AboutDialog from './components/AboutDialog.vue'
import ArchivedTasksDialog from './components/ArchivedTasksDialog.vue'
import ScheduledTasksDialog from './components/ScheduledTasksDialog.vue'
import Composer from './components/Composer.vue'
import ComposerProjectSelector from './components/ComposerProjectSelector.vue'
import ConfigAnomalyDialog from './components/ConfigAnomalyDialog.vue'
import InlineApprovalCard from './components/InlineApprovalCard.vue'
import InlineQuestionCard from './components/InlineQuestionCard.vue'

import ConfirmDialog from './components/ConfirmDialog.vue'
import ConversationProcess from './components/ConversationProcess.vue'
import SubAgentConversationPanel from './components/SubAgentConversationPanel.vue'
import ConversationBrowserPanel from './components/ConversationBrowserPanel.vue'
import ConversationToolbar from './components/ConversationToolbar.vue'
import type { ConversationBrowserState } from '../../shared/browser'
import CustomizationPage from './components/CustomizationPage.vue'
import EditMessageDialog from './components/EditMessageDialog.vue'
import GitWorkspacePanel from './components/GitWorkspacePanel.vue'
import ConversationTimeline from './components/ConversationTimeline.vue'
import { extractConversationTurns, type ConversationTurn } from './components/conversation-turns'

import ProjectPropertiesDialog from './components/ProjectPropertiesDialog.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import TaskSettingsDialog from './components/TaskSettingsDialog.vue'
import TaskStepList from './components/TaskStepList.vue'
import { computeTurnTaskSteps, type TaskStep } from './task-planner'
import { confirmAction } from './confirmation'
import { finalizeAssistantBubbles } from './conversation-state'
import { isHiddenSystemUserMessage, isInternalUserMessage } from './internal-messages'
import { collectThreadSubAgents, type ThreadSubAgentEntry } from './subagent-activity'
import { isForbiddenProjectPath } from './project-paths'
import { retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'
import { ReasoningLevel } from './types'
import type { ChatMessage, ConversationWidget, FileAttachment, ImageAttachment, ProjectItem, QueuedMessage, SubAgentActivity, ThreadItem, ThreadStats, ToolActivity } from './types'
import {
  DEFAULT_SIDEBAR_WIDTH,
  formatPromptWithFiles,
  hydrateMessages,
  loadStoredSidebarWidth,
  makeId,
  MIN_SIDEBAR_WIDTH,
  normalizePath,
  normalizeReasoningLevel,
  pathName,
  plainImages,
  queuedImageUrl,
  queuedMessageAttachmentSummary,
  queuedMessagePreview,
  samePath,
  sessionScope,
  sessionStats,
  SIDEBAR_WIDTH_STORAGE_KEY
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
import type { ComposerCommandId } from './composer-triggers'
import {
  describeSessionApprovalRule,
  enqueuePendingApproval,
  matchesSessionApprovalRule,
  removeFinishedApprovals,
  removePendingApprovalById,
  type PendingToolApproval
} from './tool-approval'
import { applySessionTitle } from './session-title'
import { useSidebarVisibility } from './sidebar-expansion'
import { createConversationScroller } from './conversation-scroll'
import {
  collectMessageImageSources,
  projectConversationItems,
  projectConversationTurns
} from './conversation-process'
import { computeConversationVirtualWindow, correctConversationMeasurement } from './conversation-virtual'

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
type AppPage = 'main' | 'settings' | 'customization' | 'extensions' | 'archived' | 'scheduled'

const sidebarOpen = useSidebarVisibility()
const activePage = ref<AppPage>('main')

const currentActivityItem = computed<'chat' | 'archived' | 'scheduled' | 'customization' | 'settings'>(() => {
  switch (activePage.value) {
    case 'archived': return 'archived'
    case 'scheduled': return 'scheduled'
    case 'customization':
    case 'extensions':
      return 'customization'
    case 'settings': return 'settings'
    case 'main':
    default:
      return 'chat'
  }
})

function handleToggleSidebar(): void {
  sidebarOpen.value = !sidebarOpen.value
  if (sidebarOpen.value && sidebarWidth.value < MIN_SIDEBAR_WIDTH) {
    sidebarWidth.value = DEFAULT_SIDEBAR_WIDTH
    localStorage.setItem(SIDEBAR_WIDTH_STORAGE_KEY, String(DEFAULT_SIDEBAR_WIDTH))
  }
}

function handleActivityChat(): void {
  if (activePage.value !== 'main') {
    activePage.value = 'main'
    sidebarOpen.value = true
    if (sidebarWidth.value < MIN_SIDEBAR_WIDTH) {
      sidebarWidth.value = DEFAULT_SIDEBAR_WIDTH
      localStorage.setItem(SIDEBAR_WIDTH_STORAGE_KEY, String(DEFAULT_SIDEBAR_WIDTH))
    }
  } else {
    handleToggleSidebar()
  }
}



const sidebarWidth = ref(loadStoredSidebarWidth())

function handleSidebarResize(width: number): void {
  sidebarWidth.value = width
  localStorage.setItem(SIDEBAR_WIDTH_STORAGE_KEY, String(width))
}

function handleSidebarResetWidth(): void {
  sidebarWidth.value = DEFAULT_SIDEBAR_WIDTH
  localStorage.setItem(SIDEBAR_WIDTH_STORAGE_KEY, String(DEFAULT_SIDEBAR_WIDTH))
}
const aboutOpen = ref(false)
const gitPanelOpen = ref(false)
const browserPanelOpen = ref(false)
const DEFAULT_BROWSER_PANEL_WIDTH = 520
const MIN_BROWSER_PANEL_WIDTH = 320
const MAX_BROWSER_PANEL_WIDTH = 1400
const BROWSER_PANEL_WIDTH_STORAGE_KEY = 'seekclaw-browser-panel-width-v1'

function loadStoredBrowserPanelWidth(): number {
  try {
    const raw = localStorage.getItem(BROWSER_PANEL_WIDTH_STORAGE_KEY)
    if (!raw) return DEFAULT_BROWSER_PANEL_WIDTH
    const val = Number(raw)
    if (Number.isFinite(val) && val >= MIN_BROWSER_PANEL_WIDTH && val <= MAX_BROWSER_PANEL_WIDTH) {
      return val
    }
    return DEFAULT_BROWSER_PANEL_WIDTH
  } catch {
    return DEFAULT_BROWSER_PANEL_WIDTH
  }
}

const browserPanelWidth = ref(loadStoredBrowserPanelWidth())

function resizeBrowserPanel(width: number): void {
  if (!Number.isFinite(width)) return
  const maxWidth = Math.min(MAX_BROWSER_PANEL_WIDTH, Math.max(MIN_BROWSER_PANEL_WIDTH, Math.floor(window.innerWidth * 0.85)))
  const nextWidth = Math.min(maxWidth, Math.max(MIN_BROWSER_PANEL_WIDTH, Math.round(width)))
  browserPanelWidth.value = nextWidth
  localStorage.setItem(BROWSER_PANEL_WIDTH_STORAGE_KEY, String(nextWidth))
}

const selectedSubAgent = ref<{ agent: SubAgentActivity; threadId: string } | null>(null)
const browserState = ref<ConversationBrowserState | null>(null)
const browserError = ref('')
let unsubscribeBrowser: (() => void) | undefined
const browserAutoOpened = new Set<string>()
let browserStateSequence = 0
function handleBrowserState(state: ConversationBrowserState): void {
  if (state.sessionId !== activeThread.value?.sessionId) return
  browserState.value = state
  if (state.tabs.length === 0) {
    browserAutoOpened.delete(state.scope)
    if (browserPanelOpen.value && !state.busy) {
      browserPanelOpen.value = false
    }
  }
  if (state.busy && !browserAutoOpened.has(state.scope)) {
    browserAutoOpened.add(state.scope)
    window.dispatchEvent(new CustomEvent('seekclaw:side-panel-open'))
    browserPanelOpen.value = true
    gitPanelOpen.value = false
    selectedSubAgent.value = null
  }
}
async function openConversationBrowser(): Promise<void> {
  const thread = activeThread.value
  if (!thread) return
  browserError.value = ''
  try {
    const project = projects.value.find(project => project.id === thread.projectId)
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    if (!thread.sessionId) {
      const modelRef = thread.modelRef ?? (defaultModel.value || undefined)
      const response = await window.seekclaw.daemon.request('session.new', { ...scope, modelRef, reasoningLevel: thread.reasoningLevel, networkEnabled: thread.networkEnabled ?? true })
      thread.sessionId = response.data
      thread.modelRef = modelRef
      thread.sessionLoaded = true
      if (thread.titleSource === 'manual')
        await window.seekclaw.daemon.request('session.update', { ...scope, id: thread.sessionId, title: thread.title })
    }
    const state = await window.seekclaw.browser.open({ ...scope, sessionId: thread.sessionId })
    if (thread !== activeThread.value) return
    browserState.value = state
    browserAutoOpened.add(state.scope)
    window.dispatchEvent(new CustomEvent('seekclaw:side-panel-open'))
    browserPanelOpen.value = true
    gitPanelOpen.value = false
    selectedSubAgent.value = null
  } catch (reason) { browserError.value = reason instanceof Error ? reason.message : String(reason) }
}

const gitPanelTab = ref<'diff' | 'history'>('diff')

const DEFAULT_GIT_PANEL_WIDTH = 240
const MIN_GIT_PANEL_WIDTH = 180
const MAX_GIT_PANEL_WIDTH = 1400
const GIT_PANEL_WIDTH_STORAGE_KEY = 'seekclaw-git-panel-width-v2'

function loadStoredGitPanelWidth(): number {
  try {
    const raw = localStorage.getItem(GIT_PANEL_WIDTH_STORAGE_KEY)
    if (!raw) return DEFAULT_GIT_PANEL_WIDTH
    const val = Number(raw)
    if (Number.isFinite(val) && val >= MIN_GIT_PANEL_WIDTH && val <= 800) {
      return val
    }
    return DEFAULT_GIT_PANEL_WIDTH
  } catch {
    return DEFAULT_GIT_PANEL_WIDTH
  }
}

const gitPanelWidth = ref(loadStoredGitPanelWidth())
const toolDiff = ref<{ path: string; diff: string } | null>(null)

const settingsSection = ref<'general' | 'appearance' | 'models' | 'mcp' | 'skills' | 'diagnostics' | 'advanced'>('general')
const extensionsSection = ref<'mcp' | 'skills'>('mcp')
const taskSettingsThreadId = ref('')
const worktreeBusy = ref(false)
const worktreeError = ref('')
const activePropertiesProject = ref<ProjectItem | null>(null)
const projectNameSaving = ref(false)
const projectNameError = ref('')
const storedAccentColor = localStorage.getItem('seekclaw-accent-color') || 'default'
const accentColor = ref<string>(storedAccentColor)
const storedFontFamily = localStorage.getItem('seekclaw-font-family') || ''
const fontFamily = ref<string>(storedFontFamily)
const storedMonoFontFamily = localStorage.getItem('seekclaw-mono-font-family') || ''
const monoFontFamily = ref<string>(storedMonoFontFamily)
const storedZoom = localStorage.getItem('seekclaw-zoom-factor')
const zoomFactor = ref<number>(
  storedZoom ? Math.max(0.75, Math.min(1.5, parseFloat(storedZoom) || 1.0)) : 1.0
)
const storedTheme = localStorage.getItem('seekclaw-theme')
const theme = ref<AppearanceTheme>(
  storedTheme === 'light' || storedTheme === 'dark' || storedTheme === 'system' ? storedTheme : 'system')
const daemonState = ref<DaemonState>({ connected: false, endpoint: '' })
const reconnecting = ref(false)
const reconnectAttempt = ref(0)
const configAnomaly = ref<{
  hasAnomaly: boolean
  detail?: string
  configFile?: string
  backupFile?: string
} | null>(null)
const rebuildingConfig = ref(false)
const projects = ref<ProjectItem[]>([])
const projectsReady = ref(false)
const threads = ref<ThreadItem[]>([])
const activeThreadId = ref('')
const selectedProjectId = ref('')
const runtimeWorkspacePath = ref('')
const models = ref<string[]>([])
const modelCatalog = ref<RuntimeModelCatalogItem[]>([])
const defaultModel = ref('')
const pendingModelChanges = reactive(new Set<string>())
const mode = ref('guardrail')
const busy = computed(() => Boolean(activeThread.value?.running))
const scrollArea = ref<HTMLElement | null>(null)
const conversationContent = ref<HTMLElement | null>(null)
let conversationResizeObserver: ResizeObserver | undefined
const composer = ref<InstanceType<typeof Composer> | null>(null)
const autoFollowConversation = ref(true)
const conversationLoading = ref(false)
const conversationLoadError = ref('')
const conversationQuery = ref('')
const turnHeights = shallowReactive(new Map<string, number>())
const expandedProcesses = shallowReactive(new Map<string, boolean>())

function turnViewKey(turnId: string): string {
  return `${activeThreadId.value}:${turnId}`
}
const conversationScrollTop = ref(0)
const conversationViewportHeight = ref(600)
const conversationContentOffset = ref(0)
const conversationSelectionToken = { value: 0 }

const activeThread = computed(() => threads.value.find((thread) => thread.id === activeThreadId.value))
const activeSubAgent = computed(() => {
  const selected = selectedSubAgent.value
  if (!selected || selected.threadId !== activeThread.value?.id) return null
  return activeThread.value.messages.flatMap((message) => message.subAgents ?? [])
    .findLast((agent) => agent.id === selected.agent.id) ?? selected.agent
})
const activeThreadSubAgents = computed<ThreadSubAgentEntry[]>(() => {
  return collectThreadSubAgents(activeThread.value?.messages ?? [])
})

const DEFAULT_SUBAGENT_PANEL_WIDTH = 520
const MIN_SUBAGENT_PANEL_WIDTH = 320
const MAX_SUBAGENT_PANEL_WIDTH = 1400
const SUBAGENT_PANEL_WIDTH_STORAGE_KEY = 'seekclaw-subagent-panel-width-v1'

function loadStoredSubagentPanelWidth(): number {
  try {
    const raw = localStorage.getItem(SUBAGENT_PANEL_WIDTH_STORAGE_KEY)
    if (!raw) return DEFAULT_SUBAGENT_PANEL_WIDTH
    const val = Number(raw)
    if (Number.isFinite(val) && val >= MIN_SUBAGENT_PANEL_WIDTH && val <= MAX_SUBAGENT_PANEL_WIDTH) {
      return val
    }
    return DEFAULT_SUBAGENT_PANEL_WIDTH
  } catch {
    return DEFAULT_SUBAGENT_PANEL_WIDTH
  }
}

const subagentPanelWidth = ref(loadStoredSubagentPanelWidth())

function resizeSubagentPanel(width: number): void {
  if (!Number.isFinite(width)) return
  const maxWidth = Math.min(MAX_SUBAGENT_PANEL_WIDTH, Math.max(MIN_SUBAGENT_PANEL_WIDTH, Math.floor(window.innerWidth * 0.85)))
  const nextWidth = Math.min(maxWidth, Math.max(MIN_SUBAGENT_PANEL_WIDTH, Math.round(width)))
  if (nextWidth === subagentPanelWidth.value) return
  subagentPanelWidth.value = nextWidth
  localStorage.setItem(SUBAGENT_PANEL_WIDTH_STORAGE_KEY, String(nextWidth))
}

watch(activeThreadId, () => { selectedSubAgent.value = null })

function openSubAgent(agent: SubAgentActivity): void {
  if (!activeThread.value?.sessionId) return
  window.dispatchEvent(new CustomEvent('seekclaw:side-panel-open'))
  selectedSubAgent.value = { agent, threadId: activeThread.value.id }
  gitPanelOpen.value = false
  browserPanelOpen.value = false
}
watch(() => activeThread.value?.sessionId, async id => {
  const sequence = ++browserStateSequence
  browserState.value = null
  browserError.value = ''
  if (id) {
    const state = await window.seekclaw.browser.state(id).catch(() => null)
    if (sequence === browserStateSequence) browserState.value = state
  }
})
const activeModel = computed(() => activeThread.value?.modelRef ?? defaultModel.value)
const activeReasoningLevel = computed(() => activeThread.value?.reasoningLevel ?? ReasoningLevel.High)
const activeModelCatalogItem = computed(() =>
  modelCatalog.value.find((model) => model.ref === activeModel.value)
)
const composerModels = computed(() => {
  const separator = defaultModel.value.indexOf('/')
  if (separator < 1) return []
  const providerPrefix = defaultModel.value.slice(0, separator + 1)
  return models.value.filter((model) => model.startsWith(providerPrefix))
})
const activeModelSupportsImages = computed(() =>
  activeModelCatalogItem.value?.capabilities?.vision === true)
const activeModelMaxReasoning = computed<ReasoningLevel>(() => {
  const cap = activeModelCatalogItem.value?.capabilities
  if (cap?.maxReasoningLevel) {
    return normalizeReasoningLevel(cap.maxReasoningLevel)
  }
  return ReasoningLevel.Max
})
const activeModelSupportsReasoning = computed<boolean>(() => {
  const cap = activeModelCatalogItem.value?.capabilities
  if (!cap) return true
  if (cap.maxReasoningLevel === 'none') return false
  if (cap.thinking === false && cap.reasoning === false && !cap.maxReasoningLevel) return false
  return true
})
const activeImageSources = computed<Record<string, string>>(() =>
  collectMessageImageSources(activeThread.value?.messages ?? [])
)
const activeProject = computed(() => {
  const projectId = activeThread.value ? activeThread.value.projectId : selectedProjectId.value
  return projects.value.find((project) => project.id === projectId)
})
let cachedGitPanelProject: { source?: ProjectItem; workspace?: string; resolved?: ProjectItem } = {}
const gitPanelProject = computed<ProjectItem | undefined>(() => {
  const project = activeProject.value
  const workspace = activeThread.value?.executionWorkspace || ''
  if (!project || !workspace || project.path === workspace) {
    cachedGitPanelProject = {}
    return project
  }
  if (cachedGitPanelProject.source === project && cachedGitPanelProject.workspace === workspace && cachedGitPanelProject.resolved) {
    return cachedGitPanelProject.resolved
  }
  const resolved = { ...project, path: workspace }
  cachedGitPanelProject = { source: project, workspace, resolved }
  return resolved
})
const globalTaskActive = computed(() => activeThread.value ? !activeThread.value.projectId : !selectedProjectId.value)
const currentWorkspacePath = computed(() => activeProject.value?.path || runtimeWorkspacePath.value || '')

const isGlobalExplicit = ref(false)

const showComposerProjectSelector = computed(() => {
  if (activePage.value !== 'main') return false
  if (conversationLoading.value) return false
  if (!activeThread.value) return true
  return activeThread.value.messages.length === 0 && !activeThread.value.running
})

function onSelectProjectFromComposer(project: ProjectItem): void {
  isGlobalExplicit.value = false
  selectedProjectId.value = project.id
  if (activeThread.value && activeThread.value.messages.length === 0) {
    activeThread.value.projectId = project.id
  } else if (!activeThread.value) {
    newTask(project.id)
  }
}

function onSelectGlobalFromComposer(): void {
  isGlobalExplicit.value = true
  selectedProjectId.value = ''
  if (activeThread.value && activeThread.value.messages.length === 0) {
    activeThread.value.projectId = undefined
  } else if (!activeThread.value) {
    newTask(undefined)
  }
}

async function handleCreateProjectFromComposer(): Promise<void> {
  const path = await window.seekclaw.selectWorkspace()
  if (!path) return
  if (isForbiddenProjectPath(path, appInfo.value.userProfilePath)) {
    window.alert('不能把用户主目录或 SeekClaw 数据目录添加为项目，请选择具体的项目文件夹。')
    return
  }
  const project = await saveProject(ensureProject(path))
  isGlobalExplicit.value = false
  selectedProjectId.value = project.id
  if (activeThread.value && activeThread.value.messages.length === 0) {
    activeThread.value.projectId = project.id
  } else if (!activeThread.value) {
    newTask(project.id)
  }
  await refreshProjectSessions(project).catch(() => undefined)
}

const activeBranch = ref('')
let branchLookupSeq = 0

const activeWorkspaceForBranch = computed(() =>
  activeThread.value?.executionWorkspace || activeProject.value?.path || ''
)

watch(activeWorkspaceForBranch, async (workspacePath) => {
  const seq = ++branchLookupSeq
  if (!workspacePath || !window.seekclaw?.project?.gitOverview) {
    activeBranch.value = ''
    return
  }
  try {
    const overview = await window.seekclaw.project.gitOverview(workspacePath)
    if (seq !== branchLookupSeq) return
    activeBranch.value = overview.isRepository && overview.branch ? overview.branch : ''
  } catch {
    if (seq === branchLookupSeq) activeBranch.value = ''
  }
}, { immediate: true })

const composerCaption = computed(() => {
  if (conversationLoading.value) return '正在读取会话历史…'
  if (!activeThread.value) return '选择一个任务，或新建任务开始。'
  if (activeThread.value.archived) return '此任务已归档，恢复后可继续。'
  const stats = activeThread.value.stats
  const rounds = stats?.llmRounds ?? 0
  const steps = stats?.executionSteps ?? 0
  const inputTokens = stats?.inputTokens ?? 0
  const outputTokens = stats?.outputTokens ?? 0
  const totalInputTokens = stats?.totalInputTokens ?? 0
  const cachedInputTokens = stats?.cachedInputTokens ?? 0
  const outputElapsedMs = stats?.outputElapsedMs ?? 0

  const parts: string[] = []
  if (activeBranch.value) parts.push(`分支 ${activeBranch.value}`)

  const hasUsage = rounds > 0 || inputTokens > 0 || outputTokens > 0
  if (!hasUsage) {
    parts.push('输入 @ 引用文件，/ 使用快捷命令')
    return parts.join(' · ')
  }

  parts.push(steps > rounds ? `${rounds.toLocaleString()} 轮 · ${steps.toLocaleString()} 步` : `${rounds.toLocaleString()} 轮`)
  if (totalInputTokens > 0) {
    const cacheRate = Math.min(100, Math.round(cachedInputTokens / totalInputTokens * 100))
    parts.push(`缓存命中 ${cacheRate}%`)
  }
  if (outputTokens > 0 && outputElapsedMs > 0) {
    parts.push(`${(outputTokens / (outputElapsedMs / 1000)).toFixed(1)} tok/s`)
  }
  if (inputTokens > 0 || outputTokens > 0) {
    parts.push(`输入 ${inputTokens.toLocaleString()} · 输出 ${outputTokens.toLocaleString()} tok`)
  }
  return parts.join(' | ')
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

async function saveProject(project: ProjectItem, name = project.name): Promise<ProjectItem> {
  const oldId = project.id
  const response = await window.seekclaw.daemon.request('project.upsert', {
    id: project.id,
    path: project.path,
    name
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
  const path = activeThread.value?.executionWorkspace || activeProject.value?.path
  if (path) void window.seekclaw.showItemInFolder(path)
}

function openGitPanel(tab: 'diff' | 'history'): void {
  if (!activeProject.value) return
  window.dispatchEvent(new CustomEvent('seekclaw:side-panel-open'))
  toolDiff.value = null
  gitPanelTab.value = tab
  browserPanelOpen.value = false
  selectedSubAgent.value = null
  gitPanelOpen.value = true
}

function openDevTools(): void {
  void window.seekclaw.openDevTools()
}

function openToolDiff(path: string, diff: string): void {
  window.dispatchEvent(new CustomEvent('seekclaw:side-panel-open'))
  toolDiff.value = { path, diff }
  gitPanelTab.value = 'diff'
  browserPanelOpen.value = false
  selectedSubAgent.value = null
  gitPanelOpen.value = true
}

function closeGitPanel(): void {
  gitPanelOpen.value = false
  toolDiff.value = null
}

function resizeGitPanel(width: number): void {
  if (!Number.isFinite(width)) return
  const maxWidth = Math.min(MAX_GIT_PANEL_WIDTH, Math.max(MIN_GIT_PANEL_WIDTH, Math.floor(window.innerWidth * 0.9)))
  const nextWidth = Math.min(maxWidth, Math.max(MIN_GIT_PANEL_WIDTH, Math.round(width)))
  if (nextWidth === gitPanelWidth.value) return
  gitPanelWidth.value = nextWidth
  localStorage.setItem(GIT_PANEL_WIDTH_STORAGE_KEY, String(nextWidth))
}

async function openProjectTerminal(): Promise<void> {
  const targetPath = activeThread.value?.executionWorkspace || activeProject.value?.path || runtimeWorkspacePath.value || ''
  if (!targetPath) {
    await window.seekclaw.notify('无法打开终端', '当前未选定有效的工作区或项目目录')
    return
  }
  try {
    await window.seekclaw.project.openTerminal(targetPath)
  } catch (error) {
    await window.seekclaw.notify('打开终端失败', error instanceof Error ? error.message : String(error))
  }
}

function applyTheme(value: AppearanceTheme): void {
  theme.value = value
  document.documentElement.dataset.theme = value
  localStorage.setItem('seekclaw-theme', value)
  void window.seekclaw.setTheme(value)
  applyAccentColor(accentColor.value)
}

function applyAccentColor(color: string): void {
  accentColor.value = color
  localStorage.setItem('seekclaw-accent-color', color)
  if (!color || color === 'default') {
    document.documentElement.style.removeProperty('--accent')
    document.documentElement.style.removeProperty('--accent-soft')
  } else {
    document.documentElement.style.setProperty('--accent', color)
    document.documentElement.style.setProperty(
      '--accent-soft',
      `color-mix(in srgb, ${color} 16%, var(--surface-raised))`
    )
  }
}

function applyFontFamily(font: string): void {
  fontFamily.value = font
  localStorage.setItem('seekclaw-font-family', font)
  if (!font) {
    document.documentElement.style.removeProperty('--font-sans')
  } else {
    document.documentElement.style.setProperty('--font-sans', font)
  }
}

function applyMonoFontFamily(font: string): void {
  monoFontFamily.value = font
  localStorage.setItem('seekclaw-mono-font-family', font)
  if (!font) {
    document.documentElement.style.removeProperty('--font-mono')
  } else {
    document.documentElement.style.setProperty('--font-mono', font)
  }
}

function applyZoomFactor(factor: number): void {
  const clamped = Math.max(0.75, Math.min(1.5, Math.round(factor * 100) / 100))
  zoomFactor.value = clamped
  localStorage.setItem('seekclaw-zoom-factor', String(clamped))
  if (window.seekclaw?.setZoomFactor) {
    window.seekclaw.setZoomFactor(clamped)
  } else {
    (document.documentElement.style as unknown as { zoom: string }).zoom = String(clamped)
  }
}

function isNearConversationBottom(element: HTMLElement, threshold = 48): boolean {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold
}

let viewportMeasureRafId: number | null = null

function measureConversationViewport(): void {
  if (scrollArea.value) conversationViewportHeight.value = scrollArea.value.clientHeight
  if (scrollArea.value && conversationContent.value) {
    conversationContentOffset.value = conversationContent.value.getBoundingClientRect().top
      - scrollArea.value.getBoundingClientRect().top + scrollArea.value.scrollTop
      + (parseFloat(getComputedStyle(conversationContent.value).paddingTop) || 0)
  }
}

function scheduleConversationViewportMeasure(): void {
  if (viewportMeasureRafId !== null) return
  viewportMeasureRafId = requestAnimationFrame(() => {
    viewportMeasureRafId = null
    measureConversationViewport()
  })
}

const userTurns = computed<ConversationTurn[]>(() => {
  return extractConversationTurns(activeThread.value?.messages ?? [])
})

const activeTurnId = ref<string>('')

function updateActiveTurn(): void {
  const container = scrollArea.value
  if (!container || userTurns.value.length === 0) return

  if (isNearConversationBottom(container, 80)) {
    activeTurnId.value = userTurns.value[userTurns.value.length - 1]?.id ?? ''
    return
  }

  const containerRect = container.getBoundingClientRect()
  const triggerY = containerRect.top + 160

  let currentId = userTurns.value[0]?.id ?? ''
  for (const turn of userTurns.value) {
    const el = document.getElementById('message-' + turn.id)
    if (el) {
      const rect = el.getBoundingClientRect()
      if (rect.top <= triggerY) {
        currentId = turn.id
      } else {
        break
      }
    }
  }
  activeTurnId.value = currentId
}

watch(
  () => userTurns.value.map((t) => t.id).join(','),
  () => {
    void nextTick(() => updateActiveTurn())
  },
  { immediate: true }
)

function triggerMessagePulse(el: HTMLElement): void {
  el.classList.remove('target-highlight-flash')
  void el.offsetWidth
  el.classList.add('target-highlight-flash')
  setTimeout(() => {
    el.classList.remove('target-highlight-flash')
  }, 1600)
}

function handleTimelineNavigate(turn: ConversationTurn): void {
  const container = scrollArea.value
  if (!container) return

  activeTurnId.value = turn.id
  autoFollowConversation.value = false

  const targetEl = document.getElementById('message-' + turn.id)
  if (targetEl) {
    targetEl.scrollIntoView({ behavior: 'smooth', block: 'start' })
    triggerMessagePulse(targetEl)
    return
  }

  const index = conversationTurns.value.findIndex((item) => item.user?.message.id === turn.id)
  if (index >= 0) {
    let top = conversationContentOffset.value
    for (let i = 0; i < index; i++) {
      const id = conversationTurns.value[i]?.id ?? ''
      top += turnHeights.get(turnViewKey(id)) ?? VIRTUAL_ESTIMATED_HEIGHT
    }
    container.scrollTo({ top, behavior: 'smooth' })
    setTimeout(() => {
      const el = document.getElementById('message-' + turn.id)
      if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'start' })
        triggerMessagePulse(el)
      }
    }, 150)
  }
}

function handleConversationScroll(): void {
  if (scrollArea.value) conversationScrollTop.value = scrollArea.value.scrollTop
  const element = scrollArea.value
  if (element) autoFollowConversation.value = isNearConversationBottom(element)
  updateActiveTurn()
}

const conversationScroller = createConversationScroller({
  element: () => scrollArea.value,
  shouldFollow: () => autoFollowConversation.value,
  afterRender: nextTick,
  requestFrame: (callback) => requestAnimationFrame(callback),
  cancelFrame: (handle) => cancelAnimationFrame(handle)
})
const scrollToBottom = conversationScroller.scrollToBottom

watch([scrollArea, conversationContent], () => {
  conversationResizeObserver?.disconnect()
  conversationResizeObserver = new ResizeObserver(() => {
    scheduleConversationViewportMeasure()
    if (autoFollowConversation.value) void scrollToBottom()
  })
  if (scrollArea.value) conversationResizeObserver.observe(scrollArea.value)
  if (conversationContent.value) conversationResizeObserver.observe(conversationContent.value)
}, { flush: 'post' })

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
    projectsReady.value = true
    await removeInvalidProjectRows()
    const available = JSON.parse(modelResponse.data) as string[]
    const catalog = JSON.parse(catalogResponse.data) as RuntimeModelCatalogItem[]
    const workspace = JSON.parse(workspaceResponse.data) as RuntimeWorkspace
    const currentProject = projects.value.find((project) => samePath(project.path, workspace.path))
    runtimeWorkspacePath.value = workspace.path
    if (!projects.value.some((project) => project.id === selectedProjectId.value))
      selectedProjectId.value = currentProject?.id ?? ''
    models.value = available
    modelCatalog.value = catalog
    defaultModel.value = catalog.find((model) => model.active)?.ref
      ?? (available.includes(defaultModel.value) ? defaultModel.value : available[0] ?? '')
    mode.value = modeResponse.data
    await refreshAllProjectSessions()
    await migrateImplicitDocumentsProject()

    // 清理可能由于先前未加锁保存残留的无消息未启动草稿
    threads.value = threads.value.filter((thread) =>
      thread.sessionId || thread.messages.length > 0 || thread.running
    )

    if (!activeThread.value) {
      const recent = threads.value
        .filter((thread) => !thread.archived && Boolean(thread.sessionId || thread.messages.length > 0 || thread.running))
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
  if (!state.connected) threads.value.forEach((thread) => { thread.titleGenerating = false })
  if (state.connected) {
    return
  }
  pendingQuestions.value = []
  if (conversationLoading.value) {
    conversationLoading.value = false
    conversationLoadError.value = '已与 SeekClaw Runtime 断开连接，正在尝试重连…'
  }
  if (!appReadyForRecovery || reconnecting.value || reconnectTask) return
  void runReconnectCycle()
}

async function runReconnectCycle(): Promise<boolean> {
  if (reconnectTask) return reconnectTask
  reconnectTask = (async () => {
    reconnecting.value = true
    const state = await retryRuntimeConnection(
      () => window.seekclaw.daemon.connect(),
      { onAttempt: (attempt) => { reconnectAttempt.value = attempt } })
    daemonState.value = state
    if (state.connected) {
        await loadRuntimeState()
      return true
    }
    await window.seekclaw.exitApp()
    return false
  })().finally(() => {
    reconnecting.value = false
    reconnectAttempt.value = 0
    reconnectTask = null
  })
  return reconnectTask
}

async function reconnectDaemon(): Promise<void> {
  if (daemonState.value.connected) {
    await loadRuntimeState()
    return
  }
  await runReconnectCycle()
}

async function refreshRuntimeState(): Promise<void> {
  if (daemonState.value.connected) await loadRuntimeState()
  else await reconnectDaemon()
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

const customizationTab = ref<'plugins' | 'skills'>('plugins')

function openCustomization(tab: 'plugins' | 'skills' = 'plugins'): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  customizationTab.value = tab
  activePage.value = 'customization'
}

function openExtensions(section: 'mcp' | 'skills' = 'mcp'): void {
  openCustomization(section === 'skills' ? 'skills' : 'plugins')
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

  // 1. 如果当前已经在未开启的空会话中，不重复新建，直接复用当前会话页面并更新作用域
  const current = activeThread.value
  if (current && !current.sessionId && current.messages.length === 0 && !current.running) {
    current.projectId = project?.id
    selectedProjectId.value = project?.id ?? ''
    isGlobalExplicit.value = !project
    conversationSelectionToken.value++
    conversationLoading.value = false
    conversationLoadError.value = ''
    void nextTick(() => composer.value?.focus())
    return
  }

  // 2. 清理之前遗留的未发送消息的空会话草稿，避免内存中堆积
  threads.value = threads.value.filter((t) => t.sessionId || t.messages.length > 0 || t.running)

  // 3. 创建单例的空会话草稿
  const thread: ThreadItem = {
    id: makeId(),
    title: '新任务',
    projectId: project?.id,
    updatedAt: Date.now(),
    messages: [],
    modelRef: defaultModel.value || undefined,
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
  isGlobalExplicit.value = !project
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
    modelRef: currentThread.modelRef,
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
  await saveProject(project)
  runtimeWorkspacePath.value = opened.path
  mode.value = opened.mode
}

async function reloadThreadSession(thread: ThreadItem, project?: ProjectItem): Promise<void> {
  await reloadThreadSessionFromStore(sessionSyncContext, thread, project)
}

async function selectThread(id: string): Promise<void> {
  // 切换离开未开始的空草稿会话时，自动清理该草稿
  const current = activeThread.value
  if (current && current.id !== id && !current.sessionId && current.messages.length === 0 && !current.running) {
    threads.value = threads.value.filter((t) => t.id !== current.id)
  }

  const thread = threads.value.find((item) => item.id === id)
  if (!thread) return
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const selectionToken = ++conversationSelectionToken.value
  const needsLoad = Boolean(thread.sessionId && (!thread.sessionLoaded || !thread.running))
  activeThreadId.value = id
  isGlobalExplicit.value = false
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
      thread.messages = hydrateMessages(saved, thread.messages)
      thread.sessionLoaded = true
      thread.executionWorkspace = saved.executionWorkspace
      thread.worktree = saved.worktree
      applySessionTitle(thread, saved)
      thread.archived = Boolean(saved.archived)
      thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
      thread.modelRef = saved.modelRef ?? undefined
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
  await scrollToBottom(false, true)
}

const conversationItems = computed(() => {
  const thread = activeThread.value
  return projectConversationItems(thread?.messages ?? [], {
    running: thread?.running === true,
    activeTurnStartId: thread?.activeTurnStartId
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
  const lastUserIndex = messages.findLastIndex((m) => m.role === 'user' && !isInternalUserMessage(m))
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

const conversationTurns = computed(() => projectConversationTurns(conversationItems.value, {
  running: activeThread.value?.running,
  assistantId: activeThread.value?.assistantId
}))

/** Measure the same turn units that are rendered, including their disclosure state. */
const virtualWindow = computed(() => computeConversationVirtualWindow({
  items: conversationTurns.value,
  getHeight: (turn) => turnHeights.get(turnViewKey(turn.id)),
  scrollTop: conversationScrollTop.value,
  viewportHeight: conversationViewportHeight.value,
  contentOffsetTop: conversationContentOffset.value,
  enabled: !conversationQuery.value.trim(),
  threshold: VIRTUAL_THRESHOLD,
  overscan: VIRTUAL_OVERSCAN,
  estimatedHeight: VIRTUAL_ESTIMATED_HEIGHT
}))

const vMeasure = {
  mounted(el: HTMLElement & { __heightObserver?: ResizeObserver; __heightFrame?: number }, binding: { value: string }): void {
    const report = (): void => {
      const height = el.getBoundingClientRect().height
      const prev = turnHeights.get(binding.value)
      if (height <= 0 || (prev !== undefined && Math.abs(prev - height) < 0.5)) return
      const container = scrollArea.value
      if (container && virtualWindow.value.active) {
        const top = correctConversationMeasurement({
          items: conversationTurns.value,
          getHeight: (turn) => turnHeights.get(turnViewKey(turn.id)),
          scrollTop: conversationScrollTop.value,
          following: autoFollowConversation.value,
          measuredIndex: conversationTurns.value.findIndex((turn) => turnViewKey(turn.id) === binding.value),
          height,
          contentOffsetTop: conversationContentOffset.value,
          estimatedHeight: VIRTUAL_ESTIMATED_HEIGHT
        })
        // Publish the corrected viewport before the new height can change its window.
        if (top !== conversationScrollTop.value) {
          conversationScrollTop.value = top
          container.scrollTop = top
        }
      }
      turnHeights.set(binding.value, height)
    }
    report()
    const scheduleReport = (): void => {
      if (el.__heightFrame !== undefined) return
      el.__heightFrame = requestAnimationFrame(() => {
        el.__heightFrame = undefined
        report()
      })
    }
    const observer = new ResizeObserver(scheduleReport)
    el.__heightObserver = observer
    observer.observe(el)
  },
  unmounted(el: HTMLElement & { __heightObserver?: ResizeObserver; __heightFrame?: number }): void {
    if (el.__heightFrame !== undefined) {
      cancelAnimationFrame(el.__heightFrame)
      el.__heightFrame = undefined
    }
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
  // Locate the originating user prompt so retrying replaces only its response cleanly without duplicating the prompt.
  let promptIndex = -1
  for (let i = index; i >= 0; i--) {
    const candidate = thread.messages[i]
    if (candidate?.role === 'user' && !isInternalUserMessage(candidate)) {
      promptIndex = i
      break
    }
  }
  if (promptIndex < 0) return
  const promptMessage = thread.messages[promptIndex]!
  let keepCount = promptIndex
  const idMatch = /^.*:(\d+)$/.exec(promptMessage.id)
  if (idMatch?.[1]) {
    keepCount = parseInt(idMatch[1], 10)
  }
  const project = projects.value.find((item) => item.id === thread.projectId)
  try {
    await window.seekclaw.daemon.request('session.truncate', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      keepCount
    })
  } catch {
    return
  }
  thread.messages = thread.messages.slice(0, promptIndex)
  thread.phase = undefined
  await sendMessage(promptMessage.content, promptMessage.images ?? [], promptMessage.files)
}

async function sendMessage(content: string, images: ImageAttachment[], files?: FileAttachment[]): Promise<void> {
  const thread = activeThread.value
  const project = thread ? projects.value.find((item) => item.id === thread.projectId) : undefined
  if (!thread || (thread.projectId && !project) || thread.archived || activeQuestion.value) return
  if (!content.trim() && images.length === 0 && (!files || files.length === 0)) return
  if (thread.running || thread.queueDraining) {
    thread.queuedMessages ??= []
    thread.queuedMessages.push({ id: makeId(), content, images, files, createdAt: Date.now() })
    return
  }
  composer.value?.clearDraft(thread.id)
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

interface PendingUserQuestion extends UserQuestion {
  draft: string
  submitting: boolean
  submitted: boolean
  error: string
}

const pendingQuestions = ref<PendingUserQuestion[]>([])
const activeQuestion = computed(() => pendingQuestions.value.find((item) => item.sessionId === activeThread.value?.sessionId))

async function submitQuestionAnswer(question: PendingUserQuestion | undefined, answer: string): Promise<void> {
  if (!question || question !== activeQuestion.value || question.submitting || question.submitted || !answer.trim()) return
  question.submitting = true
  question.error = ''
  try {
    const response = await window.seekclaw.daemon.request('agent.answer', { questionId: question.questionId, answer })
    if (response.event === 'cancelled') throw new Error('发送已取消')
    question.submitted = true
  } catch (error) {
    question.error = `发送回答失败：${error instanceof Error ? error.message : String(error)}`
  } finally {
    question.submitting = false
  }
}

const pendingApprovals = ref<PendingToolApproval[]>([])
const sessionApprovalRules = new Map<string, Set<string>>()

const activeThreadApprovals = computed(() =>
  pendingApprovals.value.filter((item) => item.threadId === activeThreadId.value)
)
const activePendingApproval = computed(() => activeThreadApprovals.value[0] ?? null)
const backgroundPendingApprovals = computed(() => {
  const counts = new Map<string, { threadId: string; title: string; count: number }>()
  for (const item of pendingApprovals.value) {
    if (item.threadId === activeThreadId.value) continue
    const owner = threads.value.find((t) => t.id === item.threadId)
    const existing = counts.get(item.threadId)
    if (existing) {
      existing.count += 1
    } else {
      counts.set(item.threadId, {
        threadId: item.threadId,
        title: owner?.title || '未命名任务',
        count: 1
      })
    }
  }
  return Array.from(counts.values())
})

async function submitApprovalDecision(
  approvalId: string,
  approved: boolean,
  reason?: string
): Promise<void> {
  try {
    await window.seekclaw.daemon.request('agent.approve', {
      approvalId,
      approved,
      ...(reason ? { reason } : {})
    })
  } catch { /* The runtime rejects approvals that already timed out. */ }
}

async function resolveToolApproval(payload: {
  approvalId: string
  approved: boolean
  rememberSession?: boolean
  reason?: string
}): Promise<void> {
  const target = pendingApprovals.value.find((item) => item.approvalId === payload.approvalId)
  pendingApprovals.value = removePendingApprovalById(pendingApprovals.value, payload.approvalId)

  await submitApprovalDecision(payload.approvalId, payload.approved, payload.reason)

  if (payload.approved && payload.rememberSession && target) {
    let rules = sessionApprovalRules.get(target.threadId)
    if (!rules) {
      rules = new Set<string>()
      sessionApprovalRules.set(target.threadId, rules)
    }
    const descriptor = describeSessionApprovalRule(target)
    rules.add(descriptor.signature)

    // Auto-approve any other queued approvals in the same thread matching the new rule.
    const autoMatched = pendingApprovals.value.filter((item) =>
      item.threadId === target.threadId && matchesSessionApprovalRule(rules, item)
    )
    for (const item of autoMatched) {
      pendingApprovals.value = removePendingApprovalById(pendingApprovals.value, item.approvalId)
      void submitApprovalDecision(item.approvalId, true)
    }
  }
}

async function handleApprovalRequest(event: DaemonMessage): Promise<void> {
  const approvalId = typeof event.details?.approvalId === 'string' ? event.details.approvalId : ''
  if (!approvalId) return

  const thread = (event.sessionId
    ? threads.value.find((item) => item.sessionId === event.sessionId)
    : undefined)
    ?? threads.value.find((item) => item.requestId === event.id)
  if (thread && event.details?.background !== true && isFinishedRequest(thread, event.id)) {
    // The originating turn timed out or was cancelled while the approval was in
    // flight. Deny it immediately instead of leaving the runtime blocked.
    await submitApprovalDecision(approvalId, false)
    return
  }

  const targetThreadId = thread?.id || activeThreadId.value
  const toolName = typeof event.details?.toolName === 'string' ? event.details.toolName : event.data
  const summary = typeof event.details?.summary === 'string' ? event.details.summary : ''
  const rawArguments = typeof event.details?.arguments === 'string' ? event.details.arguments : ''
  const requestRecord: PendingToolApproval = {
    approvalId,
    callId: typeof event.details?.callId === 'string' ? event.details.callId : approvalId,
    sessionId: event.sessionId || thread?.sessionId,
    threadId: targetThreadId,
    requestId: typeof event.details?.requestId === 'number' ? event.details.requestId : event.id,
    toolName: toolName || 'unknown',
    summary,
    argumentsText: rawArguments,
    mode: typeof event.details?.mode === 'string' ? event.details.mode : undefined,
    mutating: event.details?.mutating === true,
    requiresNetwork: event.details?.requiresNetwork === true,
    isMcp: event.details?.isMcp === true,
    background: event.details?.background === true,
    childSessionId: typeof event.details?.childSessionId === 'string' ? event.details.childSessionId : undefined,
    createdAt: Date.now()
  }

  const rules = sessionApprovalRules.get(targetThreadId)
  if (matchesSessionApprovalRule(rules, requestRecord)) {
    await submitApprovalDecision(approvalId, true)
    return
  }

  pendingApprovals.value = enqueuePendingApproval(pendingApprovals.value, requestRecord)

  // Notify cleanly without modal hijack when a background task waits on approval.
  if (targetThreadId && targetThreadId !== activeThreadId.value) {
    const title = thread?.title || '后台任务'
    void window.seekclaw?.notify?.(`任务「${title}」需要批准`, `工具 ${requestRecord.toolName} 等待你的确认。`)
  }
}

function scheduleQueuedDrain(thread: ThreadItem): void {
  if (!thread.running && !thread.queueDraining && !thread.archived && !pendingModelChanges.has(thread.id) && thread.queuedMessages?.length)
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

function moveQueuedMessage(thread: ThreadItem, id: string, delta: -1 | 1): void {
  const list = thread.queuedMessages
  if (!list || list.length < 2) return
  const idx = list.findIndex((item) => item.id === id)
  const targetIdx = idx + delta
  if (idx < 0 || targetIdx < 0 || targetIdx >= list.length) return
  const [moved] = list.splice(idx, 1)
  if (moved) list.splice(targetIdx, 0, moved)
}

function editQueuedMessage(thread: ThreadItem, queued: QueuedMessage): void {
  removeQueuedMessage(thread, queued.id)
  composer.value?.populate(queued.content, queued.images, queued.files)
}

async function steerQueuedMessage(thread: ThreadItem, queued: QueuedMessage): Promise<void> {
  if (!thread.running || !thread.sessionId || !daemonState.value.connected
    || pendingQuestions.value.some((question) => question.sessionId === thread.sessionId)) return
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
  if (thread.id === activeThreadId.value) void scrollToBottom(false, true)
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
  const turnStartIndex = thread.messages.length

  const userMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content,
    images,
    files,
    createdAt: Date.now()
  }
  const assistant: ChatMessage = {
    id: makeId(), role: 'assistant', content: '', thinking: '', tools: [], state: 'thinking', thinkingActive: false, createdAt: Date.now()
  }
  thread.messages.push(userMessage, assistant)
  thread.updatedAt = Date.now()
  if (thread.titleSource === 'pending' || thread.titleSource === 'input'
    || (turnStartIndex === 0 && thread.title === '新任务')) {
    thread.titleSource = 'pending'
    thread.title = '未命名任务'
    thread.titleGenerating = true
  }
  thread.running = true
  thread.activeTurnToken = turnToken
  thread.activeTurnStartId = userMessage.id
  thread.assistantId = assistant.id
  thread.requestId = undefined
  let terminalAssistant = assistant
  autoFollowConversation.value = true
  await scrollToBottom(false, true)

  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    let sessionCreated = false
    if (!thread.sessionId) {
      const modelRef = thread.modelRef ?? (defaultModel.value || undefined)
      const sessionResponse = await window.seekclaw.daemon.request('session.new', {
        ...scope,
        modelRef,
        reasoningLevel,
        networkEnabled: thread.networkEnabled ?? true
      })
      thread.sessionId = sessionResponse.data
      thread.modelRef = modelRef
      thread.sessionLoaded = true
      sessionCreated = true
    }
    if (sessionCreated && thread.titleSource !== 'pending') {
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
      supportsUserQuestions: true,
      ...scope
    }, { timeoutMs: CHAT_FIRST_EVENT_TIMEOUT_MS })
    pendingQuestions.value = pendingQuestions.value.filter((question) => !finishesQuestionTurn(response, question))
    rememberFinishedRequest(thread, response.id)
    const turnAssistantBubbles = thread.messages.slice(turnStartIndex).filter((item) => item.role === 'assistant')
    const finalAssistant = turnAssistantBubbles[turnAssistantBubbles.length - 1] ?? assistant
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
    thread.titleGenerating = false
    const requestId = error instanceof Error
      ? (error as Error & { requestId?: unknown }).requestId
      : undefined
    if (typeof requestId === 'number' && Number.isFinite(requestId))
      rememberFinishedRequest(thread, requestId)
    const turnAssistantBubbles = thread.messages.slice(turnStartIndex).filter((item) => item.role === 'assistant')
    const failedAssistant = turnAssistantBubbles[turnAssistantBubbles.length - 1] ?? assistant
    terminalAssistant = failedAssistant
    failedAssistant.state = 'error'
    if (!failedAssistant.content && !failedAssistant.errorMessage) {
      const detail = error instanceof Error ? error.message : String(error)
      failedAssistant.errorMessage = `无法连接 SeekClaw Daemon: ${detail}`
    }
  } finally {
    // A terminal event may already have started the next queued turn. Do not let
    // this older request clear that newer turn's state.
    if (thread.activeTurnToken !== turnToken) return
    if (thread.requestId !== undefined) {
      pendingQuestions.value = pendingQuestions.value.filter((question) =>
        question.turnId !== thread.requestId || question.sessionId !== thread.sessionId)
    }
    // Belt-and-braces: finalize every leftover "..." placeholder when this turn
    // settles. The captured assistant can be a stale bubble (a mid-turn steer
    // created a fresh one) and the terminal event itself may have been dropped
    // by the finished-request guard, so walk all messages of this turn.
    const turnAssistantBubbles = thread.messages.slice(turnStartIndex).filter((item) => item.role === 'assistant')
    finalizeAssistantBubbles(turnAssistantBubbles, terminalAssistant.state === 'error' ? 'error' : 'done')
    thread.activeTurnToken = undefined
    thread.activeTurnStartId = undefined
    thread.running = false
    thread.requestId = undefined
    thread.assistantId = undefined
    thread.phase = undefined
    if (thread.sessionId && terminalAssistant.state !== 'error') {
      await reloadThreadSession(thread, project).catch(() => undefined)
    }
    if (thread.id === activeThreadId.value) await scrollToBottom()
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
  taskRenameOnly.value = false
  worktreeError.value = ''
  if (thread) taskSettingsThreadId.value = thread.id
}

const taskRenameOnly = ref(false)
const sidebarActionError = ref('')

function showProject(project: ProjectItem): void {
  void runSidebarAction(() => window.seekclaw.showItemInFolder(project.path))
}

function renameTask(thread: ThreadItem): void {
  openTaskSettings(thread)
  taskRenameOnly.value = true
}

async function runSidebarAction(action: () => Promise<unknown>): Promise<void> {
  sidebarActionError.value = ''
  try { await action() }
  catch (error) { sidebarActionError.value = error instanceof Error ? error.message : String(error) }
}

async function copyTask(thread: ThreadItem, format: 'text' | 'id'): Promise<void> {
  await runSidebarAction(async () => {
    if (format === 'id') {
      if (thread.sessionId) await navigator.clipboard.writeText(thread.sessionId)
      return
    }
    const project = projects.value.find((item) => item.id === thread.projectId)
    if (thread.projectId && !project) return
    let messages = thread.messages
    if (thread.sessionId && !thread.sessionLoaded) {
      const response = await window.seekclaw.daemon.request('session.get', {
        id: thread.sessionId, ...sessionScope(thread, project)
      })
      messages = hydrateMessages(JSON.parse(response.data) as RuntimeSession)
    }
    await navigator.clipboard.writeText(messages.filter((message) => !isHiddenSystemUserMessage(message) && message.content.trim())
      .map((message) => `${message.role === 'user' ? '用户' : 'SeekClaw'}\n${message.content}`).join('\n\n'))
  })
}

async function forkTask(thread: ThreadItem): Promise<void> {
  if (!thread.sessionId || thread.running) return
  await runSidebarAction(async () => {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if (thread.projectId && !project) return
    const response = await window.seekclaw.daemon.request('session.fork', {
      id: thread.sessionId, ...sessionScope(thread, project), keepCount: 0,
      title: `${thread.title} (分支)`
    })
    const saved = JSON.parse(response.data) as { id: string }
    if (project) await refreshProjectSessions(project)
    else await refreshGlobalSessions()
    const fork = threads.value.find((item) => item.sessionId === saved.id && item.projectId === thread.projectId)
    if (fork) await selectThread(fork.id)
  })
}

async function manageTaskWorktree(action: 'create' | 'archive' | 'restore', baseRef: string): Promise<void> {
  const thread = settingsThread.value
  const project = settingsProject.value
  if (!thread || !project || thread.running || worktreeBusy.value) return
  worktreeBusy.value = true
  worktreeError.value = ''
  try {
    const scope = sessionScope(thread, project)
    if (!thread.sessionId) {
      const modelRef = thread.modelRef ?? (defaultModel.value || undefined)
      const response = await window.seekclaw.daemon.request('session.new', { ...scope, modelRef, reasoningLevel: thread.reasoningLevel, networkEnabled: thread.networkEnabled ?? true })
      thread.sessionId = response.data
      thread.modelRef = modelRef
      if (thread.titleSource === 'manual')
        await window.seekclaw.daemon.request('session.update', { ...scope, id: thread.sessionId, title: thread.title })
    }
    await window.seekclaw.daemon.request(`worktree.${action}`, { ...scope, sessionId: thread.sessionId, baseRef }, { timeoutMs: 150_000 })
    await reloadThreadSession(thread, project)
  } catch (reason) {
    worktreeError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    worktreeBusy.value = false
  }
}

function openProjectProperties(project: ProjectItem): void {
  projectNameError.value = ''
  activePropertiesProject.value = project
}

async function saveProjectName(name: string): Promise<void> {
  const project = activePropertiesProject.value
  const value = name.trim()
  if (!project || !value || projectNameSaving.value) return
  projectNameSaving.value = true
  projectNameError.value = ''
  try {
    await saveProject(project, value)
    activePropertiesProject.value = null
  } catch (reason) {
    projectNameError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    projectNameSaving.value = false
  }
}

async function saveTaskTitle(title: string): Promise<void> {
  const thread = settingsThread.value
  const project = settingsProject.value
  if (!thread || (thread.projectId && !project)) return
  if (thread.sessionId) {
    await window.seekclaw.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      title
    })
  }
  thread.title = title
  thread.titleSource = 'manual'
  thread.titleGenerating = false
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

const handleDaemonEvent = createDaemonEventHandler({
  threads,
  activeThreadId,
  projects,
  readSubAgentStatus: async (thread, taskId) => {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if (!thread.sessionId || (thread.projectId && !project)) throw new Error('找不到父会话')
    const response = await window.seekclaw.daemon.request('subagent.status', {
      ...sessionScope(thread, project), sessionId: thread.sessionId, taskId
    })
    const record = JSON.parse(response.data) as { status: SubAgentActivity['status']; output?: string; error?: string; sessionId?: string }
    return { status: record.status, output: record.error || record.output, childSessionId: record.sessionId }
  },
  handleScheduleUpdated,
  scrollToBottom,
  reloadThreadSession,
  rememberFinishedRequest,
  isFinishedRequest,
  scheduleQueuedDrain,
  reloadBackgroundThreadIfIdle
})

function handleDaemonMessage(event: DaemonMessage): void {
  if (event.event === 'question_request') {
    const question = parseQuestionRequest(event)
    if (!question || !daemonState.value.connected) return
    const thread = threads.value.find((item) => item.sessionId === question.sessionId)
    if (!thread?.running || isFinishedRequest(thread, question.turnId)
      || (thread.requestId !== undefined && thread.requestId !== question.turnId)) return
    thread.requestId = question.turnId
    if (!pendingQuestions.value.some((item) => item.sessionId === question.sessionId && item.questionId === question.questionId)) {
      pendingQuestions.value.push({ ...question, draft: '', submitting: false, submitted: false, error: '' })
    }
    return
  }
  if (event.event === 'question_resolved') {
    const resolved = parseQuestionResolved(event)
    if (resolved) {
      pendingQuestions.value = pendingQuestions.value.filter((item) =>
        item.sessionId !== resolved.sessionId || item.questionId !== resolved.questionId)
    }
    return
  }
  if (event.requestMethod === 'agent.answer') return
  if (event.event === 'done' || event.event === 'cancelled' || event.event === 'error' || event.event === 'result') {
    pendingQuestions.value = pendingQuestions.value.filter((question) => !finishesQuestionTurn(event, question))
  }
  if (event.event === 'approval_request') {
    void handleApprovalRequest(event)
    return
  }
  if (event.event === 'done' || event.event === 'cancelled' || event.event === 'error') {
    pendingApprovals.value = removeFinishedApprovals(pendingApprovals.value, {
      sessionId: event.sessionId,
      requestId: event.id
    })
  }
  handleDaemonEvent(event)
}

async function stopTurn(): Promise<void> {
  const thread = activeThread.value
  if (!thread?.running) return
  const params = thread.requestId
    ? { requestId: thread.requestId }
    : thread.sessionId
      ? { sessionId: thread.sessionId }
      : null
  if (!params) return
  try {
    await window.seekclaw.daemon.request('agent.cancel', params)
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
  const thread = activeThread.value
  if (!thread || thread.archived || thread.running || !model || pendingModelChanges.has(thread.id)) return
  if (!thread.sessionId) {
    thread.modelRef = model
    return
  }
  const project = projects.value.find((item) => item.id === thread.projectId)
  pendingModelChanges.add(thread.id)
  try {
    await window.seekclaw.daemon.request('session.update', {
      ...sessionScope(thread, project), id: thread.sessionId, modelRef: model
    })
    thread.modelRef = model
  } catch (error) {
    await window.seekclaw.notify('切换会话模型失败', error instanceof Error ? error.message : String(error))
  } finally {
    pendingModelChanges.delete(thread.id)
    scheduleQueuedDrain(thread)
  }
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
  if (!thread || thread.archived) return
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

function handleComposerCommand(commandId: ComposerCommandId): void {
  switch (commandId) {
    case 'new':
      newTask(selectedProjectId.value || undefined)
      break
    case 'diff':
      openGitPanel('diff')
      break
    case 'history':
      openGitPanel('history')
      break
    case 'fork':
      if (activeThread.value) void forkTask(activeThread.value)
      break
    case 'settings':
      openSettings('general')
      break
    case 'mode:manual':
      void changeMode('manual')
      break
    case 'mode:guardrail':
      void changeMode('guardrail')
      break
    case 'mode:full':
      void changeMode('full')
      break
    default:
      break
  }
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

function handleArtifactPreviewOpen(): void {
  gitPanelOpen.value = false
  browserPanelOpen.value = false
  selectedSubAgent.value = null
}

onMounted(async () => {
  applyTheme(theme.value)
  applyAccentColor(accentColor.value)
  applyFontFamily(fontFamily.value)
  applyMonoFontFamily(monoFontFamily.value)
  applyZoomFactor(zoomFactor.value)
  appInfo.value = await window.seekclaw.getAppInfo()
  document.documentElement.dataset.platform = appInfo.value.platform
  document.documentElement.dataset.material = appInfo.value.supportsMica ? 'mica' : 'solid'
  projects.value = loadStoredProjects()
  window.addEventListener('seekclaw:artifact-preview-open', handleArtifactPreviewOpen)
  unsubscribeBrowser = window.seekclaw.browser.onChanged(handleBrowserState)
  unsubscribeEvent = window.seekclaw.daemon.onEvent(handleDaemonMessage)
  unsubscribeState = window.seekclaw.daemon.onState(handleDaemonState)
  appReadyForRecovery = true
  await runReconnectCycle()
  measureConversationViewport()
  window.addEventListener('resize', scheduleConversationViewportMeasure)
  if (!daemonState.value.connected) projects.value.forEach((project) => { project.loaded = true })
  if (activeThread.value) composer.value?.focus()
})

onBeforeUnmount(() => {
  appReadyForRecovery = false
  if (viewportMeasureRafId !== null) {
    cancelAnimationFrame(viewportMeasureRafId)
    viewportMeasureRafId = null
  }
  conversationScroller.dispose()
  conversationResizeObserver?.disconnect()
  window.removeEventListener('resize', scheduleConversationViewportMeasure)
  window.removeEventListener('seekclaw:artifact-preview-open', handleArtifactPreviewOpen)
  unsubscribeBrowser?.()
  unsubscribeEvent?.()
  unsubscribeState?.()
})

watch(theme, applyTheme)
</script>

<template>
  <div class="app-shell">
    <AppTitleBar :sidebar-open="sidebarOpen" :project-path="globalTaskActive ? undefined : activeProject?.path"
      @toggle-sidebar="handleToggleSidebar" @new-task="newTask(selectedProjectId || undefined)"
      @open-workspace="openWorkspace" @show-project="showActiveProject" @open-settings="openSettings('general')"
      @focus-composer="composer?.focus()" @open-terminal="openProjectTerminal" @open-git-changes="openGitPanel('diff')"
      @open-git-history="openGitPanel('history')" @open-diagnostics="openSettings('diagnostics')"
      @open-dev-tools="openDevTools" @open-about="aboutOpen = true" />

    <div class="app-main-layout">
      <ActivityBar
        :active-item="currentActivityItem"
        :sidebar-open="sidebarOpen"
        @open-chat="handleActivityChat"
        @open-archived="openArchivedTasks"
        @open-scheduled-tasks="openScheduledTasks"
        @open-customization="openCustomization('plugins')"
        @open-settings="openSettings('general')"
      />

      <div class="app-workspace-container">
        <div
          class="app-body"
          v-show="activePage === 'main'"
          :class="{ 'sidebar-collapsed': !sidebarOpen }"
          :style="{ '--sidebar-width': `${sidebarWidth}px` }"
        >
          <Transition name="sidebar-slide">
            <Sidebar
              v-if="sidebarOpen"
              :projects="projects"
              :projects-ready="projectsReady"
              :threads="threads"
              :active-thread-id="activeThreadId"
              :active-project-id="selectedProjectId"
              :width="sidebarWidth"
              @new-task="newTask"
              @open-workspace="openWorkspace"
              @select-thread="selectThread"
              @task-settings="openTaskSettings"
              @rename-task="renameTask"
              @copy-task="copyTask"
              @fork-task="forkTask"
              @show-project="showProject"
              @archive-task="archiveTask"
              @restore-task="restoreTask"
              @delete-task="deleteTask"
              @delete-project="deleteProject"
              @archive-project-tasks="archiveProjectTasks"
              @open-project-properties="openProjectProperties"
              @delete-project-tasks="deleteProjectTasks"
              @archive-global-tasks="archiveGlobalTasks"
              @delete-global-tasks="deleteGlobalTasks"
              @resize="handleSidebarResize"
              @collapse="sidebarOpen = false"
              @reset-width="handleSidebarResetWidth"
            />
          </Transition>
      <Transition name="scrim-fade">
        <button v-if="sidebarOpen" class="sidebar-scrim" title="关闭侧栏" @click="sidebarOpen = false" />
      </Transition>

      <div class="workspace-content">
        <main class="workspace-main" v-show="activePage === 'main'">
          <header class="conversation-header">
            <div class="conversation-title">
              <span v-if="activeThread?.titleGenerating" class="task-title-skeleton" role="status" aria-label="正在生成任务标题" />
              <strong v-else>{{ conversationTitle }}</strong>
              <!--  <small v-if="activeThread">{{ activeProject?.name || '任务' }}</small>-->
            </div>
            <ConversationToolbar
              v-model="conversationQuery"
              :context-key="`${activePage}:${activeThreadId}:${activeProject?.id || ''}`"
              :has-project="Boolean(activeProject)"
              :has-workspace="Boolean(activeProject || runtimeWorkspacePath)"
              :has-thread="Boolean(activeThread)"
              :connected="daemonState.connected"
              :reconnecting="reconnecting"
              :connection-title="daemonState.error || daemonState.endpoint"
              :connection-label="runtimeConnectionLabel"
              :changes-open="gitPanelOpen && gitPanelTab === 'diff'"
              :history-open="gitPanelOpen && gitPanelTab === 'history'"
              :browser-open="browserPanelOpen"
              :sidebar-open="sidebarOpen"
              @show-project="showActiveProject"
              @open-terminal="openProjectTerminal"
              @open-changes="openGitPanel('diff')"
              @open-history="openGitPanel('history')"
              @open-browser="openConversationBrowser"
              @open-task-settings="openTaskSettings()"
              @toggle-sidebar="handleToggleSidebar"
              @reconnect="reconnectDaemon"
            />
          </header>

          <p v-if="browserError" class="inline-error" role="alert">{{ browserError }}</p>
          <p v-if="sidebarActionError" class="inline-error" role="alert">{{ sidebarActionError }}</p>
          <ConversationTimeline
            v-if="userTurns.length > 0"
            :turns="userTurns"
            :active-turn-id="activeTurnId"
            @select-turn="handleTimelineNavigate"
          />

          <section ref="scrollArea" class="conversation-scroll" :class="{ following: autoFollowConversation, virtualized: virtualWindow.active }" @scroll="handleConversationScroll">
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
            <div v-else-if="activeThread && activeThread.messages.length > 0" ref="conversationContent" class="conversation-content">
              <div v-if="virtualWindow.active" class="virtual-pad" :style="{ height: `${virtualWindow.topPad}px` }" />
              <div v-for="turn in virtualWindow.items" :key="turnViewKey(turn.id)" v-measure="turnViewKey(turn.id)" class="virtual-message">
                <ConversationProcess :turn="turn" :image-sources="activeImageSources"
                  :session-id="activeThread.sessionId" :workspace="activeProject?.path"
                  :phase="activeThread.phase"
                  :assistant-id="activeThread.assistantId" :query="conversationQuery"
                  :expanded="expandedProcesses.get(turnViewKey(turn.id)) ?? false"
                  @update:expanded="expandedProcesses.set(turnViewKey(turn.id), $event)"
                  @open-sub-agent="openSubAgent"
                  @open-diff="openToolDiff" @branch="branchFromMessage" @retry="regenerateMessage" @edit="onEditUserMessage" />
              </div>
              <div v-if="virtualWindow.active" class="virtual-pad" :style="{ height: `${virtualWindow.bottomPad}px` }" />
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
            <div class="sr-only" role="status">{{ activeQuestion ? '有问题等待回答' : '' }}</div>
            <InlineApprovalCard
              :approval="activePendingApproval"
              :queue-count="activeThreadApprovals.length"
              :background-pending="backgroundPendingApprovals"
              @decide="resolveToolApproval"
              @switch-thread="selectThread"
            />
            <TransitionGroup
              v-if="activeThread?.queuedMessages?.length"
              name="queue-card"
              tag="div"
              class="pending-message-stack"
              aria-label="等待发送的消息"
            >
              <div v-for="(queued, qIdx) in activeThread.queuedMessages" :key="queued.id" class="pending-message-card">
                <div class="pending-message-main">
                  <CornerDownLeft :size="15" aria-hidden="true" />
                  <img v-if="queued.images.length" class="pending-message-image" :src="queuedImageUrl(queued.images[0])"
                    :alt="queued.images[0]?.name || '图片'">
                  <span class="pending-message-text">{{ queuedMessagePreview(queued) }}</span>
                  <span v-if="queuedMessageAttachmentSummary(queued)" class="pending-message-badge">
                    {{ queuedMessageAttachmentSummary(queued) }}
                  </span>
                </div>
                <div class="pending-message-actions">
                  <button
                    v-if="(activeThread.queuedMessages?.length ?? 0) > 1"
                    type="button"
                    class="pending-message-action icon-only"
                    :disabled="qIdx === 0"
                    title="上移队列顺序"
                    aria-label="上移等待中的消息"
                    @click="moveQueuedMessage(activeThread, queued.id, -1)"
                  >
                    <ArrowUp :size="13" />
                  </button>
                  <button
                    v-if="(activeThread.queuedMessages?.length ?? 0) > 1"
                    type="button"
                    class="pending-message-action icon-only"
                    :disabled="qIdx === (activeThread.queuedMessages?.length ?? 0) - 1"
                    title="下移队列顺序"
                    aria-label="下移等待中的消息"
                    @click="moveQueuedMessage(activeThread, queued.id, 1)"
                  >
                    <ArrowDown :size="13" />
                  </button>
                  <button
                    type="button"
                    class="pending-message-action icon-only"
                    title="放回输入框编辑"
                    aria-label="编辑等待中的消息"
                    @click="editQueuedMessage(activeThread, queued)"
                  >
                    <Pencil :size="13" />
                  </button>
                  <button type="button" class="pending-message-action"
                    :disabled="!activeThread.running || !activeThread.sessionId || !daemonState.connected"
                    title="作为附加指导发送，不打断当前 AI 回合" @click="steerQueuedMessage(activeThread, queued)">
                    <CornerDownLeft :size="14" /> 引导
                  </button>
                  <button type="button" class="pending-message-action icon-only" title="删除等待中的消息"
                    aria-label="删除等待中的消息" @click="removeQueuedMessage(activeThread, queued.id)">
                    <Trash2 :size="14" />
                  </button>
                </div>
              </div>
            </TransitionGroup>

            <TaskStepList :steps="activeThreadTurnSteps" :running="activeThread?.running"
              :phase="activeThread?.phase" />
            <InlineQuestionCard
              v-if="activeQuestion"
              class="composer-frame"
              :key="activeQuestion.turnId + ':' + activeQuestion.questionId"
              v-model:draft="activeQuestion.draft"
              :request="activeQuestion"
              :submitting="activeQuestion.submitting"
              :submitted="activeQuestion.submitted"
              :error="activeQuestion.error"
              @answer="submitQuestionAnswer(activeQuestion, $event)"
              @cancel="stopTurn"
            />
            <Composer ref="composer" :busy="busy" :obscured="Boolean(activeQuestion)"
              :disabled="!activeThread || activeThread.archived || conversationLoading || pendingModelChanges.has(activeThread.id)"
              :disabled-placeholder="!activeThread ? '请选择或新建一个任务开始对话' : conversationLoading ? '正在加载会话…' : '恢复任务后可继续对话'"
              :workspace-path="activeThread?.executionWorkspace || activeProject?.path"
              :has-active-session="Boolean(activeThread?.sessionId)"
              :model="activeModel"
              :models="composerModels" :mode="mode" :task-id="activeThread?.id" :supports-images="activeModelSupportsImages"
              :reasoning-level="activeReasoningLevel" :max-reasoning-level="activeModelMaxReasoning"
              :supports-reasoning="activeModelSupportsReasoning" :network-enabled="activeThread?.networkEnabled ?? true"
              :optimize-prompt="optimizePrompt" @send="sendMessage" @stop="stopTurn" @change-model="changeModel"
              @change-mode="changeMode" @change-reasoning-level="changeReasoningLevel"
              @change-network="changeNetwork" @command="handleComposerCommand">
              <template v-if="showComposerProjectSelector" #context>
                <ComposerProjectSelector
                  :projects="projects"
                  :active-project="activeProject"
                  :is-global-explicit="isGlobalExplicit"
                  :disabled="activeThread?.archived"
                  @select-project="onSelectProjectFromComposer"
                  @select-global="onSelectGlobalFromComposer"
                  @create-project="handleCreateProjectFromComposer"
                />
              </template>
            </Composer>
            <p class="composer-caption">{{ composerCaption }}</p>
          </footer>
        </main>

        <Transition name="side-panel">
          <SubAgentConversationPanel
            v-if="activePage === 'main' && activeSubAgent && activeThread?.sessionId"
            :agent="activeSubAgent"
            :agents="activeThreadSubAgents"
            :session-id="activeThread.sessionId"
            :workspace="activeProject?.path"
            :width="subagentPanelWidth"
            @resize="resizeSubagentPanel"
            @select-agent="selectedSubAgent = { agent: $event, threadId: activeThread!.id }"
            @close="selectedSubAgent = null"
            @open-diff="openToolDiff"
          />
        </Transition>
        <Transition name="side-panel">
          <ConversationBrowserPanel v-if="activePage === 'main' && browserPanelOpen && browserState && browserState.sessionId === activeThread?.sessionId"
            :state="browserState" :width="browserPanelWidth" @changed="handleBrowserState" @resize="resizeBrowserPanel" @close="browserPanelOpen = false" />
        </Transition>
        <GitWorkspacePanel v-show="activePage === 'main'" :open="gitPanelOpen" :project="gitPanelProject"
          :initial-tab="gitPanelTab" :diff-override="toolDiff" :width="gitPanelWidth" @close="closeGitPanel"
          @resize="resizeGitPanel" @open-terminal="openProjectTerminal" />
      </div>
    </div>


    <CustomizationPage
      :open="activePage === 'customization'"
      :initial-tab="customizationTab"
      :daemon-connected="daemonState.connected"
      :workspace-path="currentWorkspacePath"
      @close="closePage"
    />

    <SettingsDialog :open="activePage === 'settings' || activePage === 'extensions'"
      :page="activePage === 'extensions' ? 'extensions' : 'settings'" :theme="theme"
      :accent-color="accentColor"
      :font-family="fontFamily"
      :mono-font-family="monoFontFamily"
      :zoom-factor="zoomFactor"
      :daemon-connected="daemonState.connected"
      :initial-section="activePage === 'settings' ? settingsSection : extensionsSection"
      :workspace-path="currentWorkspacePath"
      :projects="projects"
      @close="closePage"
      @change-theme="applyTheme"
      @change-accent-color="applyAccentColor"
      @change-font-family="applyFontFamily"
      @change-mono-font-family="applyMonoFontFamily"
      @change-zoom-factor="applyZoomFactor"
       @open-workspace="openWorkspace"
      @runtime-changed="refreshRuntimeState" />

    <ScheduledTasksDialog :open="activePage === 'scheduled'" :projects="projects" :threads="threads" @close="closePage" />

    <ArchivedTasksDialog :open="activePage === 'archived'" :projects="projects" :threads="threads" @close="closePage"
      @select-thread="selectArchivedThread" @restore-task="restoreTask" @delete-task="deleteTask"
      @delete-all="deleteArchivedTasks" />
      </div>
    </div>

    <AboutDialog :open="aboutOpen" :app-info="appInfo" @close="aboutOpen = false" />


    <TaskSettingsDialog :open="Boolean(taskSettingsThreadId)" :thread="settingsThread" :project="settingsProject"
      :rename-only="taskRenameOnly"
      :worktree-busy="worktreeBusy" :worktree-error="worktreeError" @worktree="manageTaskWorktree"
      @close="taskSettingsThreadId = ''" @save-title="saveTaskTitle"
      @archive="settingsThread && archiveTask(settingsThread)" @restore="settingsThread && restoreTask(settingsThread)"
      @delete="settingsThread && deleteTask(settingsThread)" />

    <ProjectPropertiesDialog :open="Boolean(activePropertiesProject)" :project="activePropertiesProject ?? undefined"
      :threads="threads" :saving="projectNameSaving" :save-error="projectNameError"
      @close="activePropertiesProject = null" @save-name="saveProjectName"
      @open-extensions="openExtensions" />



    <ConfigAnomalyDialog v-if="configAnomaly && configAnomaly.hasAnomaly" :open="true" :detail="configAnomaly.detail"
      :config-file="configAnomaly.configFile" :backup-file="configAnomaly.backupFile" :rebuilding="rebuildingConfig"
      @close="configAnomaly = null" @rebuild="handleRebuildConfig" />

    <EditMessageDialog :open="editMessageDialogOpen" :message="editingUserMessage"
      :modified-files="editMessageModifiedFiles" @close="editMessageDialogOpen = false"
      @confirm="handleEditConfirm" />

    <ConfirmDialog />
  </div>
</template>
