<script setup lang="ts">
import {
  Activity,
  Blocks,
  Bot,
  Check,
  Circle,
  FolderCog,
  FolderOpen,
  Gauge,
  KeyRound,
  LoaderCircle,
  Moon,
  Monitor,
  Plus,
  RefreshCw,
  Save,
  Search,
  Settings2,
  Store,
  Sun,
  Trash2,
  Wrench,
  X
} from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import ProviderEditorDialog from './ProviderEditorDialog.vue'
import SelectMenu from './SelectMenu.vue'

type SettingsSection = 'general' | 'models' | 'mcp' | 'skills' | 'diagnostics'

interface ProfileInfo {
  name: string
  active: boolean
  provider?: string
  model?: string
  strategy?: string
  temperature?: number
  mode?: string
}

interface ProviderInfo {
  id: string
  name: string
  kind: 'openai' | 'anthropic'
  baseUrl: string
  modelListUrl?: string
  apiKey?: string
  apiKeyConfigured: boolean
  models: string[]
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy?: string
  promptCaching: boolean
  active: boolean
}

interface ProviderFormValue {
  id: string
  name: string
  kind: 'openai' | 'anthropic'
  baseUrl: string
  modelListUrl: string
  apiKey: string
  models: string
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy: string
  promptCaching: boolean
}

interface ModelInfo {
  ref: string
  active: boolean
  provider: string
  providerEnabled: boolean
  id: string
  alias?: string
  contextWindow: number
  maxOutput: number
  tags: string[]
  capabilities: Record<string, boolean | string>
}

interface McpServerInfo {
  name: string
  scope: 'workspace' | 'global'
  transport: 'stdio' | 'sse'
  command?: string
  args: string[]
  url?: string
  envKeys: string[]
  enabled: boolean
  connected: boolean
  toolCount: number
  error?: string
}

interface SkillInfo {
  name: string
  description?: string
  version?: string
  enabled: boolean
  directory: string
  scope: 'workspace' | 'global'
}

interface HealthCheck {
  name: string
  ok: boolean
  detail: string
  kind: 'runtime' | 'provider'
}

interface UsageInfo {
  provider: string
  model: string
  calls: number
  failures: number
  inputTokens: number
  totalInputTokens?: number
  cachedInputTokens?: number
  cacheCreationInputTokens?: number
  outputTokens: number
  cost: number
  avgLatencyMs: number
  successRate: number
}

const props = defineProps<{
  open: boolean
  theme: 'system' | 'light' | 'dark'
  daemonConnected: boolean
  daemonEndpoint: string
  workspacePath: string
  initialSection?: SettingsSection
}>()

const emit = defineEmits<{
  close: []
  changeTheme: [theme: 'system' | 'light' | 'dark']
  reconnect: []
  openWorkspace: []
  openOfficialSkills: []
  runtimeChanged: []
}>()

const section = ref<SettingsSection>('general')
const loading = ref(false)
const failoverEnabled = ref(true)
const action = ref('')
const error = ref('')
const notice = ref('')
const profiles = ref<ProfileInfo[]>([])
const providers = ref<ProviderInfo[]>([])
const models = ref<ModelInfo[]>([])
const mcpServers = ref<McpServerInfo[]>([])
const skills = ref<SkillInfo[]>([])
const checks = ref<HealthCheck[]>([])
const usage = ref<UsageInfo[]>([])
const selectedModel = ref('')
const modelQuery = ref('')
const modelEditorOpen = ref(false)
const providerEditorOpen = ref(false)
const profileEditorOpen = ref(false)
const mcpEditorOpen = ref(false)
const editingProviderId = ref<string | null>(null)
const editingMcpName = ref<string | null>(null)

const providerForm = reactive({
  id: '', name: '', kind: 'openai' as 'openai' | 'anthropic', baseUrl: '',
  apiKey: '', models: '', enabled: true, priority: 0,
  modelListUrl: '', timeoutSeconds: 120, proxy: '', promptCaching: true
})
const modelForm = reactive({
  provider: '', id: '', alias: '', contextWindow: 128000, maxOutput: 8192, vision: false
})
const profileForm = reactive({
  name: '', provider: '', model: '', strategy: 'balanced', temperature: ''
})
const mcpForm = reactive({
  name: '', scope: 'workspace' as 'workspace' | 'global', transport: 'stdio' as 'stdio' | 'sse',
  command: '', args: '', url: '', env: '', enabled: true
})

const activeProfile = computed(() => profiles.value.find((profile) => profile.active))
const activeModel = computed(() => models.value.find((model) => model.active))
const profileOptions = computed(() => profiles.value.map((profile) => ({
  value: profile.name,
  label: profile.name,
  description: profile.strategy ? `Strategy · ${profile.strategy}` : undefined
})))
const modelOptions = computed(() => models.value.map((model) => ({
  value: model.ref,
  label: model.ref,
  description: `${model.contextWindow.toLocaleString()} context · ${model.provider}`,
  disabled: !model.providerEnabled
})))
const providerOptions = computed(() => [
  { value: '', label: '自动选择', description: 'Automatic routing' },
  ...providers.value.map((provider) => ({ value: provider.id, label: provider.name, description: provider.id }))
])
const strategyOptions = [
  { value: 'balanced', label: 'Balanced', description: '平衡质量、速度和成本' },
  { value: 'fast', label: 'Fast', description: '优先选择响应更快的模型' },
  { value: 'quality', label: 'Quality', description: '优先选择能力更强的模型' },
  { value: 'cheap', label: 'Cheap', description: '优先降低调用成本' },
  { value: 'offline', label: 'Offline', description: '仅使用离线模型' }
]
const mcpScopeOptions = [
  { value: 'workspace', label: '当前工作区', description: '仅在此工作区生效' },
  { value: 'global', label: '全局', description: '在所有工作区中生效' }
]
const mcpTransportOptions = [
  { value: 'stdio', label: 'stdio', description: '通过本地子进程通信' },
  { value: 'sse', label: 'SSE', description: '连接远程 HTTP 服务' }
]
const filteredModels = computed(() => {
  const query = modelQuery.value.trim().toLocaleLowerCase()
  if (!query) return models.value
  return models.value.filter((model) =>
    model.ref.toLocaleLowerCase().includes(query)
    || model.alias?.toLocaleLowerCase().includes(query)
    || model.tags.some((tag) => tag.toLocaleLowerCase().includes(query)))
})
const totalUsage = computed(() => usage.value.reduce((total, item) => ({
  calls: total.calls + item.calls,
  tokens: total.tokens + promptInputTokens(item) + item.outputTokens,
  cost: total.cost + item.cost
}), { calls: 0, tokens: 0, cost: 0 }))

function promptInputTokens(item: UsageInfo): number {
  return item.totalInputTokens && item.totalInputTokens > 0 ? item.totalInputTokens : item.inputTokens
}

function cacheHitRate(item: UsageInfo): number {
  const cached = item.cachedInputTokens ?? 0
  const total = promptInputTokens(item)
  return total > 0 ? Math.min(100, Math.round(cached / total * 100)) : 0
}

const sections: Array<{ id: SettingsSection; label: string; icon: typeof Settings2 }> = [
  { id: 'general', label: '常规', icon: Settings2 },
  { id: 'models', label: '模型与 Provider', icon: Bot },
  { id: 'mcp', label: 'MCP', icon: Blocks },
  { id: 'skills', label: 'Skills', icon: Wrench },
  { id: 'diagnostics', label: '诊断与用量', icon: Activity }
]

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.seekclaw.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

function beginAction(name: string): void {
  action.value = name
  error.value = ''
  notice.value = ''
}

function endAction(): void {
  action.value = ''
}

function fail(reason: unknown): void {
  error.value = reason instanceof Error ? reason.message : String(reason)
}

function showPath(path: string): void {
  void window.seekclaw.showItemInFolder(path)
}

async function loadCurrentSection(): Promise<void> {
  if (!props.open || !props.daemonConnected) return
  loading.value = true
  error.value = ''
  notice.value = ''
  try {
    if (section.value === 'general') await loadGeneral()
    if (section.value === 'models') await loadModels()
    if (section.value === 'mcp') mcpServers.value = await requestJson<McpServerInfo[]>('mcp.list')
    if (section.value === 'skills') skills.value = await requestJson<SkillInfo[]>('skill.list')
    if (section.value === 'diagnostics') await loadDiagnostics()
  } catch (reason) {
    fail(reason)
  } finally {
    loading.value = false
  }
}

async function loadGeneral(): Promise<void> {
  const routing = await requestJson<{ failoverEnabled: boolean }>('routing.get')
  failoverEnabled.value = routing.failoverEnabled
}

async function toggleFailover(): Promise<void> {
  beginAction('routing.set')
  try {
    const routing = await requestJson<{ failoverEnabled: boolean }>('routing.set', {
      failoverEnabled: failoverEnabled.value
    })
    failoverEnabled.value = routing.failoverEnabled
    notice.value = failoverEnabled.value ? '已开启自动切换其他模型' : '已关闭自动切换，失败即停止'
  } catch (reason) {
    fail(reason)
    try {
      const routing = await requestJson<{ failoverEnabled: boolean }>('routing.get')
      failoverEnabled.value = routing.failoverEnabled
    } catch { /* keep the last known state */ }
  } finally {
    endAction()
  }
}

async function loadModels(): Promise<void> {
  const [profileData, providerData, modelData] = await Promise.all([
    requestJson<ProfileInfo[]>('profile.list'),
    requestJson<ProviderInfo[]>('provider.list'),
    requestJson<ModelInfo[]>('model.catalog')
  ])
  profiles.value = profileData
  providers.value = providerData
  models.value = modelData
  selectedModel.value = modelData.find((model) => model.active)?.ref ?? modelData[0]?.ref ?? ''
}

async function loadDiagnostics(): Promise<void> {
  const [healthData, usageData] = await Promise.all([
    requestJson<HealthCheck[]>('doctor.run'),
    requestJson<UsageInfo[]>('usage.get')
  ])
  checks.value = healthData
  usage.value = usageData
}

async function initializeWorkspace(): Promise<void> {
  beginAction('workspace.init')
  try {
    const result = await requestJson<{ created: string[] }>('workspace.init')
    notice.value = result.created.length > 0 ? `已创建 ${result.created.length} 个工作区项目` : '工作区已经初始化'
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function switchProfile(name: string): Promise<void> {
  beginAction('profile.use')
  try {
    await window.seekclaw.daemon.request('profile.use', { name })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

function newProfile(): void {
  Object.assign(profileForm, { name: '', provider: '', model: '', strategy: 'balanced', temperature: '' })
  profileEditorOpen.value = true
}

function editProfile(profile: ProfileInfo): void {
  Object.assign(profileForm, {
    name: profile.name,
    provider: profile.provider ?? '',
    model: profile.model ?? '',
    strategy: profile.strategy ?? 'balanced',
    temperature: profile.temperature?.toString() ?? ''
  })
  profileEditorOpen.value = true
}

async function saveProfile(): Promise<void> {
  beginAction('profile.save')
  try {
    await window.seekclaw.daemon.request('profile.upsert', {
      name: profileForm.name,
      provider: profileForm.provider,
      model: profileForm.model,
      strategy: profileForm.strategy,
      temperature: profileForm.temperature === '' ? null : Number(profileForm.temperature)
    })
    profileEditorOpen.value = false
    await loadModels()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function removeProfile(profile: ProfileInfo): Promise<void> {
  if (!await confirmAction({
    title: '删除 Profile', message: `删除 Profile “${profile.name}”？`, confirmLabel: '删除', danger: true
  })) return
  beginAction('profile.remove')
  try {
    await window.seekclaw.daemon.request('profile.remove', { name: profile.name })
    await loadModels()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

function newProvider(): void {
  error.value = ''
  editingProviderId.value = null
  Object.assign(providerForm, {
    id: '', name: '', kind: 'openai', baseUrl: '', apiKey: '',
    models: '', enabled: true, priority: 0, modelListUrl: '', timeoutSeconds: 120, proxy: '', promptCaching: true
  })
  providerEditorOpen.value = true
}

function editProvider(provider: ProviderInfo): void {
  error.value = ''
  editingProviderId.value = provider.id
  Object.assign(providerForm, {
    id: provider.id,
    name: provider.name === provider.id ? '' : provider.name,
    kind: provider.kind,
    baseUrl: provider.baseUrl,
    modelListUrl: provider.modelListUrl ?? '',
    apiKey: provider.apiKey ?? '',
    models: provider.models.join('\n'),
    enabled: provider.enabled,
    priority: provider.priority,
    timeoutSeconds: provider.timeoutSeconds,
    proxy: provider.proxy ?? '',
    promptCaching: provider.promptCaching ?? true
  })
  providerEditorOpen.value = true
}

async function saveProvider(value: ProviderFormValue): Promise<void> {
  beginAction('provider.save')
  try {
    const { apiKey, ...provider } = value
    const parameters: Record<string, unknown> = {
      ...provider,
      models: value.models.split(/\r?\n|,/).map((model) => model.trim()).filter(Boolean)
    }
    if (apiKey.trim()) parameters.apiKey = apiKey.trim()
    else if (editingProviderId.value) parameters.clearApiKey = true
    await window.seekclaw.daemon.request('provider.upsert', parameters)
    providerEditorOpen.value = false
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function fetchProviderModels(provider: ProviderInfo): Promise<void> {
  beginAction(`provider.models.fetch:${provider.id}`)
  try {
    const ids = await requestJson<string[]>('provider.models.fetch', { id: provider.id })
    notice.value = `${provider.id} 已获取 ${ids.length} 个模型`
    await loadModels()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

function editModel(model: ModelInfo): void {
  Object.assign(modelForm, {
    provider: model.provider,
    id: model.id,
    alias: model.alias ?? '',
    contextWindow: model.contextWindow,
    maxOutput: model.maxOutput,
    vision: model.capabilities.vision === true
  })
  modelEditorOpen.value = true
}

async function saveModel(): Promise<void> {
  beginAction('model.update')
  try {
    await window.seekclaw.daemon.request('model.update', {
      provider: modelForm.provider,
      id: modelForm.id,
      alias: modelForm.alias.trim() || null,
      contextWindow: Number(modelForm.contextWindow),
      maxOutput: Number(modelForm.maxOutput),
      vision: modelForm.vision
    })
    modelEditorOpen.value = false
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

function updateMcpScope(value: string): void {
  if (value === 'workspace' || value === 'global') mcpForm.scope = value
}

function updateMcpTransport(value: string): void {
  if (value === 'stdio' || value === 'sse') mcpForm.transport = value
}

async function useProvider(provider: ProviderInfo): Promise<void> {
  beginAction(`provider.use:${provider.id}`)
  try {
    await window.seekclaw.daemon.request('provider.use', { id: provider.id })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function testProvider(provider: ProviderInfo): Promise<void> {
  beginAction(`provider.test:${provider.id}`)
  try {
    const [result] = await requestJson<Array<{ online: boolean; latencyMs: number; detail: string }>>('provider.test', { id: provider.id })
    const message = result
      ? `${provider.id}: ${result.online ? '在线' : '离线'} · ${Math.round(result.latencyMs)} ms · ${result.detail}`
      : 'Provider 测试没有返回结果'
    if (result?.online) notice.value = message
    else error.value = message
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function removeProvider(provider: ProviderInfo): Promise<void> {
  if (!await confirmAction({
    title: '删除 Provider', message: `删除 Provider “${provider.id}”？`, confirmLabel: '删除', danger: true
  })) return
  beginAction('provider.remove')
  try {
    await window.seekclaw.daemon.request('provider.remove', { id: provider.id })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function switchModel(): Promise<void> {
  if (!selectedModel.value) return
  beginAction('model.switch')
  try {
    await window.seekclaw.daemon.request('model.switch', { model: selectedModel.value })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function testModel(): Promise<void> {
  if (!selectedModel.value) return
  beginAction('model.test')
  try {
    const result = await requestJson<{ success: boolean; detail: string; latencyMs: number }>('model.test', { model: selectedModel.value })
    const message = `${result.success ? '模型可用' : '模型不可用'} · ${Math.round(result.latencyMs)} ms · ${result.detail}`
    if (result.success) notice.value = message
    else error.value = message
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function useModelReference(reference: string): Promise<void> {
  selectedModel.value = reference
  await switchModel()
}

async function testModelReference(reference: string): Promise<void> {
  selectedModel.value = reference
  await testModel()
}

function newMcpServer(): void {
  editingMcpName.value = null
  Object.assign(mcpForm, {
    name: '', scope: 'workspace', transport: 'stdio', command: '', args: '', url: '', env: '', enabled: true
  })
  mcpEditorOpen.value = true
}

function editMcpServer(server: McpServerInfo): void {
  editingMcpName.value = server.name
  Object.assign(mcpForm, {
    name: server.name,
    scope: server.scope,
    transport: server.transport,
    command: server.command ?? '',
    args: server.args.join('\n'),
    url: server.url ?? '',
    env: '',
    enabled: server.enabled
  })
  mcpEditorOpen.value = true
}

function parseEnv(value: string): Record<string, string> | undefined {
  const entries = value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  if (entries.length === 0) return undefined
  return Object.fromEntries(entries.map((line) => {
    const index = line.indexOf('=')
    return index < 0 ? [line, ''] : [line.slice(0, index).trim(), line.slice(index + 1)]
  }))
}

async function saveMcpServer(): Promise<void> {
  beginAction('mcp.save')
  try {
    const server: Record<string, unknown> = {
      transport: mcpForm.transport,
      command: mcpForm.command,
      args: mcpForm.args.split(/\r?\n/).map((value) => value.trim()).filter(Boolean),
      url: mcpForm.url,
      enabled: mcpForm.enabled
    }
    const env = parseEnv(mcpForm.env)
    if (env) server.env = env
    const response = await requestJson<McpServerInfo[]>('mcp.upsert', {
      name: mcpForm.name,
      scope: mcpForm.scope,
      server
    })
    mcpServers.value = response
    mcpEditorOpen.value = false
    notice.value = 'MCP 配置已保存并重载'
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function toggleMcp(server: McpServerInfo): Promise<void> {
  beginAction(`mcp.toggle:${server.name}`)
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.upsert', {
      name: server.name,
      scope: server.scope,
      server: {
        transport: server.transport,
        command: server.command,
        args: server.args,
        url: server.url,
        enabled: !server.enabled
      }
    })
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function removeMcp(server: McpServerInfo): Promise<void> {
  if (!await confirmAction({
    title: '删除 MCP Server', message: `删除 MCP Server “${server.name}”？`, confirmLabel: '删除', danger: true
  })) return
  beginAction('mcp.remove')
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.remove', { name: server.name, scope: server.scope })
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function reloadMcp(): Promise<void> {
  beginAction('mcp.reload')
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.reload')
    notice.value = 'MCP 已重新加载'
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function toggleSkill(skill: SkillInfo): Promise<void> {
  beginAction(`skill.toggle:${skill.name}`)
  try {
    skills.value = await requestJson<SkillInfo[]>('skill.toggle', { name: skill.name, enabled: !skill.enabled })
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

watch(() => props.open, (open) => {
  if (!open) return
  section.value = props.initialSection ?? 'general'
  void loadCurrentSection()
})
watch(() => props.initialSection, (value) => {
  if (!props.open || !value) return
  section.value = value
  void loadCurrentSection()
})
watch(section, () => { void loadCurrentSection() })
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open" class="modal-backdrop" @mousedown.self="emit('close')">
      <section class="settings-dialog settings-workbench" role="dialog" aria-modal="true" aria-label="设置">
      <header class="settings-header">
        <div>
          <h2>设置</h2>
          <span class="settings-connection" :class="{ online: daemonConnected }">
            <Circle :size="8" fill="currentColor" />
            {{ daemonConnected ? 'Runtime 已连接' : 'Runtime 离线' }}
          </span>
        </div>
        <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
      </header>

      <div class="settings-layout">
        <nav class="settings-nav" aria-label="设置分区">
          <button
            v-for="item in sections"
            :key="item.id"
            :class="{ active: section === item.id }"
            @click="section = item.id"
          >
            <component :is="item.icon" :size="17" />
            <span>{{ item.label }}</span>
          </button>
        </nav>

        <main class="settings-content">
          <div v-if="loading" class="settings-loading"><LoaderCircle class="spin" :size="20" /> 正在加载</div>

          <template v-else-if="section === 'general'">
            <div class="settings-section-heading">
              <div><h3>常规</h3><p>桌面外观与当前 Runtime</p></div>
            </div>

            <section class="settings-group">
              <div class="settings-row">
                <div><strong>工作目录</strong><small :title="workspacePath">{{ workspacePath }}</small></div>
                <div class="row-actions">
                  <button class="icon-button" title="打开位置" @click="showPath(workspacePath)"><FolderOpen :size="17" /></button>
                  <button class="secondary-button" @click="emit('openWorkspace')"><FolderCog :size="16" /> 选择</button>
                </div>
              </div>
              <div class="settings-row">
                <div><strong>工作区元数据</strong><small>.seekclaw、sessions、skills、mcp、logs</small></div>
                <button class="secondary-button" :disabled="action === 'workspace.init'" @click="initializeWorkspace">
                  <LoaderCircle v-if="action === 'workspace.init'" class="spin" :size="15" />
                  <Save v-else :size="15" /> 初始化
                </button>
              </div>
            </section>

            <section class="settings-group">
              <div class="settings-row">
                <div><strong>外观</strong><small>界面主题</small></div>
                <div class="segmented-control">
                  <button :class="{ active: theme === 'system' }" title="跟随系统" @click="emit('changeTheme', 'system')"><Monitor :size="16" /></button>
                  <button :class="{ active: theme === 'light' }" title="浅色" @click="emit('changeTheme', 'light')"><Sun :size="16" /></button>
                  <button :class="{ active: theme === 'dark' }" title="深色" @click="emit('changeTheme', 'dark')"><Moon :size="16" /></button>
                </div>
              </div>
              <div class="settings-row">
                <div><strong>Daemon</strong><small>{{ daemonEndpoint }}</small></div>
                <button class="secondary-button" @click="emit('reconnect')"><RefreshCw :size="15" /> 重新连接</button>
              </div>
            </section>

            <section class="settings-group">
              <label class="provider-enabled-row">
                <span>
                  <strong>自动切换其他模型</strong>
                  <small>激活模型请求失败时自动尝试路由链中的其他模型；关闭后失败即停止，并显示真实错误</small>
                </span>
                <input
                  v-model="failoverEnabled"
                  class="sr-only"
                  type="checkbox"
                  :disabled="action === 'routing.set'"
                  @change="toggleFailover"
                />
                <span class="toggle-switch" aria-hidden="true"><span /></span>
              </label>
            </section>
          </template>

          <template v-else-if="section === 'models'">
            <div class="settings-section-heading">
              <div><h3>模型与 Provider</h3><p>{{ activeModel?.ref || '未选择模型' }}</p></div>
              <button class="secondary-button" @click="newProvider"><Plus :size="15" /> Provider</button>
            </div>

            <section class="settings-group compact-group">
              <div class="settings-row">
                <div><strong>活动 Profile</strong><small>{{ activeProfile?.strategy || '未设置路由策略' }}</small></div>
                <div class="row-actions">
                  <SelectMenu
                    class="settings-select"
                    :model-value="activeProfile?.name ?? ''"
                    :options="profileOptions"
                    label="活动 Profile"
                    :menu-min-width="240"
                    @update:model-value="switchProfile"
                  />
                  <button class="icon-button" title="编辑 Profile" @click="activeProfile && editProfile(activeProfile)"><Settings2 :size="16" /></button>
                  <button class="icon-button" title="新增 Profile" @click="newProfile"><Plus :size="16" /></button>
                </div>
              </div>
              <div class="settings-row">
                <div><strong>活动模型</strong><small>{{ activeModel ? `${activeModel.contextWindow.toLocaleString()} context` : '无可用模型' }}</small></div>
                <div class="row-actions model-actions">
                  <SelectMenu v-model="selectedModel" class="settings-select model-select" :options="modelOptions" label="活动模型" :menu-min-width="330" />
                  <button class="secondary-button" :disabled="!selectedModel" @click="testModel">测试</button>
                  <button class="secondary-button primary-action" :disabled="!selectedModel" @click="switchModel">使用</button>
                </div>
              </div>
            </section>

            <section v-if="profileEditorOpen" class="settings-editor">
              <div class="editor-heading"><strong>Profile</strong><button class="icon-button compact" @click="profileEditorOpen = false"><X :size="15" /></button></div>
              <div class="form-grid">
                <label><span>名称</span><input v-model="profileForm.name" :disabled="profiles.some((item) => item.name === profileForm.name)" /></label>
                <label><span>Provider</span><SelectMenu v-model="profileForm.provider" :options="providerOptions" label="Profile Provider" :menu-min-width="240" /></label>
                <label><span>Model</span><input v-model="profileForm.model" /></label>
                <label><span>Strategy</span><SelectMenu v-model="profileForm.strategy" :options="strategyOptions" label="Profile Strategy" :menu-min-width="250" /></label>
                <label><span>Temperature</span><input v-model="profileForm.temperature" type="number" min="0" max="2" step="0.1" /></label>
              </div>
              <div class="editor-actions">
                <button v-if="profiles.some((item) => item.name === profileForm.name && !item.active)" class="danger-button" @click="removeProfile(profiles.find((item) => item.name === profileForm.name)!)"><Trash2 :size="15" /> 删除</button>
                <span class="toolbar-spacer" />
                <button class="secondary-button" @click="profileEditorOpen = false">取消</button>
                <button class="secondary-button primary-action" @click="saveProfile"><Save :size="15" /> 保存</button>
              </div>
            </section>

            <section class="settings-list" aria-label="Provider 列表">
              <div v-if="providers.length === 0" class="empty-settings">尚未配置 Provider</div>
              <div v-for="provider in providers" :key="provider.id" class="settings-list-row">
                <span class="status-dot" :class="{ online: provider.enabled }" />
                <div class="list-main">
                  <div><strong>{{ provider.name }}</strong><span v-if="provider.active" class="inline-badge">活动</span></div>
                  <small>{{ provider.kind }} · {{ provider.models.length }} models · {{ provider.baseUrl }}</small>
                </div>
                <KeyRound :size="15" :class="provider.apiKeyConfigured ? 'key-set' : 'key-missing'" />
                <button class="secondary-button compact-button" :disabled="action === `provider.test:${provider.id}`" @click="testProvider(provider)">测试</button>
                <button class="secondary-button compact-button" :disabled="action === `provider.models.fetch:${provider.id}`" @click="fetchProviderModels(provider)">
                  <RefreshCw :size="13" :class="{ spin: action === `provider.models.fetch:${provider.id}` }" /> 获取模型
                </button>
                <button v-if="!provider.active" class="secondary-button compact-button" @click="useProvider(provider)">使用</button>
                <button class="icon-button compact" title="编辑" @click="editProvider(provider)"><Settings2 :size="15" /></button>
                <button class="icon-button compact danger-icon" title="删除" @click="removeProvider(provider)"><Trash2 :size="15" /></button>
              </div>
            </section>

            <div class="catalog-heading">
              <div><strong>模型目录</strong><small>{{ filteredModels.length }} / {{ models.length }}</small></div>
              <label class="settings-search"><Search :size="15" /><input v-model="modelQuery" placeholder="搜索模型、别名或标签" /></label>
            </div>
            <section v-if="modelEditorOpen" class="settings-editor model-editor">
              <div class="editor-heading">
                <div><strong>模型能力与上下文</strong><small>{{ modelForm.provider }}/{{ modelForm.id }}</small></div>
                <button class="icon-button compact" title="关闭" @click="modelEditorOpen = false"><X :size="15" /></button>
              </div>
              <div class="form-grid">
                <label><span>显示别名</span><input v-model="modelForm.alias" placeholder="可选" /></label>
                <label><span>上下文长度（tokens）</span><input v-model.number="modelForm.contextWindow" type="number" min="1024" max="10000000" step="1024" /></label>
                <label><span>最大输出（tokens）</span><input v-model.number="modelForm.maxOutput" type="number" min="128" max="1000000" step="128" /></label>
                <fieldset class="model-capability-fieldset">
                  <legend>视觉 / 多模态输入</legend>
                  <label class="radio-option"><input v-model="modelForm.vision" type="radio" :value="true" name="model-vision" /><span>支持</span></label>
                  <label class="radio-option"><input v-model="modelForm.vision" type="radio" :value="false" name="model-vision" /><span>不支持</span></label>
                  <small>声明后，上传图片时会优先使用支持视觉的模型。</small>
                </fieldset>
              </div>
              <small class="model-context-hint">当会话估算 token 接近该上下文长度时，Runtime 会自动压缩较早的历史消息。</small>
              <div class="editor-actions"><span class="toolbar-spacer" /><button class="secondary-button" @click="modelEditorOpen = false">取消</button><button class="secondary-button primary-action" @click="saveModel"><Save :size="15" /> 保存模型</button></div>
            </section>
            <section class="settings-list model-catalog" aria-label="模型目录">
              <div v-if="filteredModels.length === 0" class="empty-settings">没有匹配的模型</div>
              <div v-for="model in filteredModels" :key="model.ref" class="settings-list-row">
                <span class="status-dot" :class="{ online: model.providerEnabled }" />
                <div class="list-main">
                  <div><strong>{{ model.ref }}</strong><span v-if="model.active" class="inline-badge">活动</span><span v-if="model.alias" class="version-text">{{ model.alias }}</span></div>
                  <small>{{ model.contextWindow.toLocaleString() }} context · {{ model.maxOutput.toLocaleString() }} output · {{ Object.entries(model.capabilities).filter(([, enabled]) => enabled).map(([name]) => name).join(', ') }}</small>
                </div>
                <button class="secondary-button compact-button" @click="testModelReference(model.ref)">测试</button>
                <button class="icon-button compact" title="编辑模型能力与上下文" @click="editModel(model)"><Settings2 :size="15" /></button>
                <button v-if="!model.active && model.providerEnabled" class="secondary-button compact-button" @click="useModelReference(model.ref)">使用</button>
              </div>
            </section>
          </template>

          <template v-else-if="section === 'mcp'">
            <div class="settings-section-heading">
              <div><h3>MCP Servers</h3><p>{{ mcpServers.filter((server) => server.connected).length }} connected · {{ mcpServers.reduce((sum, server) => sum + server.toolCount, 0) }} tools</p></div>
              <div class="row-actions">
                <button class="icon-button" title="重新加载" :disabled="action === 'mcp.reload'" @click="reloadMcp"><RefreshCw :class="{ spin: action === 'mcp.reload' }" :size="17" /></button>
                <button class="secondary-button" @click="newMcpServer"><Plus :size="15" /> Server</button>
              </div>
            </div>

            <section v-if="mcpEditorOpen" class="settings-editor">
              <div class="editor-heading"><strong>{{ editingMcpName ? '编辑 MCP Server' : '新增 MCP Server' }}</strong><button class="icon-button compact" @click="mcpEditorOpen = false"><X :size="15" /></button></div>
              <div class="form-grid">
                <label><span>名称</span><input v-model="mcpForm.name" :disabled="!!editingMcpName" placeholder="filesystem" /></label>
                <label><span>范围</span><SelectMenu :model-value="mcpForm.scope" :options="mcpScopeOptions" label="MCP 范围" @update:model-value="updateMcpScope" /></label>
                <label><span>Transport</span><SelectMenu :model-value="mcpForm.transport" :options="mcpTransportOptions" label="MCP Transport" @update:model-value="updateMcpTransport" /></label>
                <label v-if="mcpForm.transport === 'stdio'" class="span-2"><span>Command</span><input v-model="mcpForm.command" placeholder="npx" /></label>
                <label v-if="mcpForm.transport === 'stdio'" class="span-2"><span>Args</span><textarea v-model="mcpForm.args" rows="3" placeholder="-y\n@modelcontextprotocol/server-filesystem" /></label>
                <label v-else class="span-2"><span>URL</span><input v-model="mcpForm.url" placeholder="https://example.com/sse" /></label>
                <label class="span-2"><span>Environment</span><textarea v-model="mcpForm.env" rows="2" placeholder="TOKEN=..." /></label>
                <label class="check-label"><input v-model="mcpForm.enabled" type="checkbox" /><span>启用</span></label>
              </div>
              <div class="editor-actions"><span class="toolbar-spacer" /><button class="secondary-button" @click="mcpEditorOpen = false">取消</button><button class="secondary-button primary-action" @click="saveMcpServer"><Save :size="15" /> 保存并重载</button></div>
            </section>

            <section class="settings-list">
              <div v-if="mcpServers.length === 0" class="empty-settings">尚未配置 MCP Server</div>
              <div v-for="server in mcpServers" :key="`${server.scope}:${server.name}`" class="settings-list-row">
                <span class="status-dot" :class="{ online: server.connected }" />
                <div class="list-main">
                  <div><strong>{{ server.name }}</strong><span class="inline-badge">{{ server.scope === 'workspace' ? '工作区' : '全局' }}</span></div>
                  <small :title="server.error">{{ server.connected ? `${server.toolCount} tools` : server.error || (server.enabled ? '未连接' : '已禁用') }} · {{ server.transport }}</small>
                </div>
                <button class="switch-control" :class="{ active: server.enabled }" :aria-label="server.enabled ? '禁用' : '启用'" @click="toggleMcp(server)"><span /></button>
                <button class="icon-button compact" title="编辑" @click="editMcpServer(server)"><Settings2 :size="15" /></button>
                <button class="icon-button compact danger-icon" title="删除" @click="removeMcp(server)"><Trash2 :size="15" /></button>
              </div>
            </section>
          </template>

          <template v-else-if="section === 'skills'">
            <div class="settings-section-heading">
              <div><h3>Skills</h3><p>{{ skills.filter((skill) => skill.enabled).length }} enabled</p></div>
              <button class="secondary-button" @click="emit('openOfficialSkills')"><Store :size="15" />官方技能市场</button>
              <button class="icon-button" title="刷新" @click="loadCurrentSection"><RefreshCw :size="17" /></button>
            </div>
            <section class="settings-list">
              <div v-if="skills.length === 0" class="empty-settings">当前工作区没有发现 Skill</div>
              <div v-for="skill in skills" :key="skill.name" class="settings-list-row skill-row">
                <Wrench :size="17" />
                <div class="list-main">
                  <div><strong>{{ skill.name }}</strong><span class="inline-badge">{{ skill.scope === 'workspace' ? '工作区' : '全局' }}</span><span v-if="skill.version" class="version-text">v{{ skill.version }}</span></div>
                  <small>{{ skill.description || skill.directory }}</small>
                </div>
                <button class="icon-button compact" title="打开位置" @click="showPath(skill.directory)"><FolderOpen :size="15" /></button>
                <button class="switch-control" :class="{ active: skill.enabled }" :aria-label="skill.enabled ? '禁用' : '启用'" @click="toggleSkill(skill)"><span /></button>
              </div>
            </section>
          </template>

          <template v-else>
            <div class="settings-section-heading">
              <div><h3>诊断与用量</h3><p>Runtime、Provider 和模型调用</p></div>
              <button class="secondary-button" @click="loadCurrentSection"><RefreshCw :size="15" /> 重新检查</button>
            </div>

            <section class="usage-summary">
              <div><Gauge :size="17" /><span>调用</span><strong>{{ totalUsage.calls.toLocaleString() }}</strong></div>
              <div><Activity :size="17" /><span>Tokens</span><strong>{{ totalUsage.tokens.toLocaleString() }}</strong></div>
              <div><Bot :size="17" /><span>成本</span><strong>${{ totalUsage.cost.toFixed(4) }}</strong></div>
            </section>

            <section class="settings-group diagnostic-list">
              <div v-for="check in checks" :key="check.name" class="settings-row">
                <div><strong>{{ check.name }}</strong><small>{{ check.detail }}</small></div>
                <span class="check-status" :class="{ ok: check.ok }"><Check v-if="check.ok" :size="15" /><X v-else :size="15" /> {{ check.ok ? '正常' : '异常' }}</span>
              </div>
            </section>

            <section v-if="usage.length > 0" class="usage-table-wrap">
              <table class="usage-table">
                <thead><tr><th>模型</th><th>调用</th><th>成功率</th><th>缓存命中</th><th>Tokens</th><th>平均延迟</th><th>成本</th></tr></thead>
                <tbody><tr v-for="item in usage" :key="`${item.provider}/${item.model}`"><td><strong>{{ item.provider }}/{{ item.model }}</strong></td><td>{{ item.calls }}</td><td>{{ Math.round(item.successRate * 100) }}%</td><td><span class="cache-rate">{{ cacheHitRate(item) }}%</span><small v-if="item.cachedInputTokens">命中 {{ item.cachedInputTokens.toLocaleString() }}<template v-if="item.cacheCreationInputTokens"> · 写入 {{ item.cacheCreationInputTokens.toLocaleString() }}</template></small></td><td>{{ (promptInputTokens(item) + item.outputTokens).toLocaleString() }}</td><td>{{ Math.round(item.avgLatencyMs) }} ms</td><td>${{ item.cost.toFixed(4) }}</td></tr></tbody>
              </table>
            </section>
          </template>

        </main>
      </div>
      </section>
    </div>
  </Transition>
  <Teleport to="body">
    <Transition name="global-toast">
      <div
        v-if="open && (error || notice)"
        class="global-toast-layer"
        role="status"
        :aria-live="error ? 'assertive' : 'polite'"
      >
        <div class="global-toast" :class="{ error: error, success: !error && notice }">
          <X v-if="error" :size="15" />
          <Check v-else :size="15" />
          <span>{{ error || notice }}</span>
        </div>
      </div>
    </Transition>
  </Teleport>
  <ProviderEditorDialog
    :open="providerEditorOpen"
    :editing-id="editingProviderId"
    :value="providerForm"
    :saving="action === 'provider.save'"
    :error="providerEditorOpen ? error : ''"
    @close="providerEditorOpen = false"
    @save="saveProvider"
  />
</template>
