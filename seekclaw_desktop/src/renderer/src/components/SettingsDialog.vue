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
  Plus,
  RefreshCw,
  Save,
  Search,
  Settings2,
  Sun,
  Trash2,
  Wrench,
  X
} from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'

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
  apiKeyConfigured: boolean
  apiKeyEnv?: string
  models: string[]
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy?: string
  active: boolean
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
  capabilities: Record<string, boolean>
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
  outputTokens: number
  cost: number
  avgLatencyMs: number
  successRate: number
}

const props = defineProps<{
  open: boolean
  theme: 'light' | 'dark'
  daemonConnected: boolean
  daemonEndpoint: string
  workspacePath: string
  initialSection?: SettingsSection
}>()

const emit = defineEmits<{
  close: []
  changeTheme: [theme: 'light' | 'dark']
  reconnect: []
  openWorkspace: []
  runtimeChanged: []
}>()

const section = ref<SettingsSection>('general')
const loading = ref(false)
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
const providerEditorOpen = ref(false)
const profileEditorOpen = ref(false)
const mcpEditorOpen = ref(false)
const editingProviderId = ref<string | null>(null)
const editingMcpName = ref<string | null>(null)

const providerForm = reactive({
  id: '', name: '', kind: 'openai' as 'openai' | 'anthropic', baseUrl: '',
  apiKey: '', apiKeyEnv: '', models: '', enabled: true, priority: 0,
  timeoutSeconds: 120, proxy: ''
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
  tokens: total.tokens + item.inputTokens + item.outputTokens,
  cost: total.cost + item.cost
}), { calls: 0, tokens: 0, cost: 0 }))

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
  if (!window.confirm(`删除 Profile “${profile.name}”？`)) return
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
  editingProviderId.value = null
  Object.assign(providerForm, {
    id: '', name: '', kind: 'openai', baseUrl: '', apiKey: '', apiKeyEnv: '',
    models: '', enabled: true, priority: 0, timeoutSeconds: 120, proxy: ''
  })
  providerEditorOpen.value = true
}

function editProvider(provider: ProviderInfo): void {
  editingProviderId.value = provider.id
  Object.assign(providerForm, {
    id: provider.id,
    name: provider.name === provider.id ? '' : provider.name,
    kind: provider.kind,
    baseUrl: provider.baseUrl,
    apiKey: '',
    apiKeyEnv: provider.apiKeyEnv ?? '',
    models: provider.models.join('\n'),
    enabled: provider.enabled,
    priority: provider.priority,
    timeoutSeconds: provider.timeoutSeconds,
    proxy: provider.proxy ?? ''
  })
  providerEditorOpen.value = true
}

async function saveProvider(): Promise<void> {
  beginAction('provider.save')
  try {
    await window.seekclaw.daemon.request('provider.upsert', {
      ...providerForm,
      models: providerForm.models.split(/\r?\n|,/).map((value) => value.trim()).filter(Boolean)
    })
    providerEditorOpen.value = false
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
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
    notice.value = result ? `${provider.id}: ${result.online ? '在线' : '离线'} · ${Math.round(result.latencyMs)} ms · ${result.detail}` : ''
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function removeProvider(provider: ProviderInfo): Promise<void> {
  if (!window.confirm(`删除 Provider “${provider.id}”？`)) return
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
    notice.value = `${result.success ? '模型可用' : '模型不可用'} · ${Math.round(result.latencyMs)} ms · ${result.detail}`
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
  if (!window.confirm(`删除 MCP Server “${server.name}”？`)) return
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
                  <button :class="{ active: theme === 'light' }" title="浅色" @click="emit('changeTheme', 'light')"><Sun :size="16" /></button>
                  <button :class="{ active: theme === 'dark' }" title="深色" @click="emit('changeTheme', 'dark')"><Moon :size="16" /></button>
                </div>
              </div>
              <div class="settings-row">
                <div><strong>Daemon</strong><small>{{ daemonEndpoint }}</small></div>
                <button class="secondary-button" @click="emit('reconnect')"><RefreshCw :size="15" /> 重新连接</button>
              </div>
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
                  <select :value="activeProfile?.name" @change="switchProfile(($event.target as HTMLSelectElement).value)">
                    <option v-for="profile in profiles" :key="profile.name" :value="profile.name">{{ profile.name }}</option>
                  </select>
                  <button class="icon-button" title="编辑 Profile" @click="activeProfile && editProfile(activeProfile)"><Settings2 :size="16" /></button>
                  <button class="icon-button" title="新增 Profile" @click="newProfile"><Plus :size="16" /></button>
                </div>
              </div>
              <div class="settings-row">
                <div><strong>活动模型</strong><small>{{ activeModel ? `${activeModel.contextWindow.toLocaleString()} context` : '无可用模型' }}</small></div>
                <div class="row-actions model-actions">
                  <select v-model="selectedModel">
                    <option v-for="model in models" :key="model.ref" :value="model.ref" :disabled="!model.providerEnabled">{{ model.ref }}</option>
                  </select>
                  <button class="secondary-button" :disabled="!selectedModel" @click="testModel">测试</button>
                  <button class="secondary-button primary-action" :disabled="!selectedModel" @click="switchModel">使用</button>
                </div>
              </div>
            </section>

            <section v-if="profileEditorOpen" class="settings-editor">
              <div class="editor-heading"><strong>Profile</strong><button class="icon-button compact" @click="profileEditorOpen = false"><X :size="15" /></button></div>
              <div class="form-grid">
                <label><span>名称</span><input v-model="profileForm.name" :disabled="profiles.some((item) => item.name === profileForm.name)" /></label>
                <label><span>Provider</span><select v-model="profileForm.provider"><option value="">自动</option><option v-for="provider in providers" :key="provider.id" :value="provider.id">{{ provider.id }}</option></select></label>
                <label><span>Model</span><input v-model="profileForm.model" /></label>
                <label><span>Strategy</span><select v-model="profileForm.strategy"><option v-for="value in ['balanced','fast','quality','cheap','offline']" :key="value">{{ value }}</option></select></label>
                <label><span>Temperature</span><input v-model="profileForm.temperature" type="number" min="0" max="2" step="0.1" /></label>
              </div>
              <div class="editor-actions">
                <button v-if="profiles.some((item) => item.name === profileForm.name && !item.active)" class="danger-button" @click="removeProfile(profiles.find((item) => item.name === profileForm.name)!)"><Trash2 :size="15" /> 删除</button>
                <span class="toolbar-spacer" />
                <button class="secondary-button" @click="profileEditorOpen = false">取消</button>
                <button class="secondary-button primary-action" @click="saveProfile"><Save :size="15" /> 保存</button>
              </div>
            </section>

            <section v-if="providerEditorOpen" class="settings-editor">
              <div class="editor-heading"><strong>{{ editingProviderId ? '编辑 Provider' : '新增 Provider' }}</strong><button class="icon-button compact" @click="providerEditorOpen = false"><X :size="15" /></button></div>
              <div class="form-grid">
                <label><span>ID</span><input v-model="providerForm.id" :disabled="!!editingProviderId" placeholder="openai" /></label>
                <label><span>名称</span><input v-model="providerForm.name" /></label>
                <label><span>协议</span><select v-model="providerForm.kind"><option value="openai">OpenAI-compatible</option><option value="anthropic">Anthropic</option></select></label>
                <label class="span-2"><span>Base URL</span><input v-model="providerForm.baseUrl" placeholder="https://api.openai.com/v1" /></label>
                <label><span>API Key</span><input v-model="providerForm.apiKey" type="password" :placeholder="editingProviderId ? '••••••••' : ''" /></label>
                <label><span>Key 环境变量</span><input v-model="providerForm.apiKeyEnv" placeholder="OPENAI_API_KEY" /></label>
                <label class="span-2"><span>Models</span><textarea v-model="providerForm.models" rows="3" placeholder="gpt-5\ngpt-5-mini" /></label>
                <label><span>超时（秒）</span><input v-model.number="providerForm.timeoutSeconds" type="number" min="5" /></label>
                <label><span>优先级</span><input v-model.number="providerForm.priority" type="number" /></label>
                <label class="span-2"><span>Proxy</span><input v-model="providerForm.proxy" placeholder="http://127.0.0.1:7890" /></label>
                <label class="check-label"><input v-model="providerForm.enabled" type="checkbox" /><span>启用</span></label>
              </div>
              <div class="editor-actions">
                <span class="toolbar-spacer" />
                <button class="secondary-button" @click="providerEditorOpen = false">取消</button>
                <button class="secondary-button primary-action" @click="saveProvider"><Save :size="15" /> 保存</button>
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
                <button v-if="!provider.active" class="secondary-button compact-button" @click="useProvider(provider)">使用</button>
                <button class="icon-button compact" title="编辑" @click="editProvider(provider)"><Settings2 :size="15" /></button>
                <button class="icon-button compact danger-icon" title="删除" @click="removeProvider(provider)"><Trash2 :size="15" /></button>
              </div>
            </section>

            <div class="catalog-heading">
              <div><strong>模型目录</strong><small>{{ filteredModels.length }} / {{ models.length }}</small></div>
              <label class="settings-search"><Search :size="15" /><input v-model="modelQuery" placeholder="搜索模型、别名或标签" /></label>
            </div>
            <section class="settings-list model-catalog" aria-label="模型目录">
              <div v-if="filteredModels.length === 0" class="empty-settings">没有匹配的模型</div>
              <div v-for="model in filteredModels" :key="model.ref" class="settings-list-row">
                <span class="status-dot" :class="{ online: model.providerEnabled }" />
                <div class="list-main">
                  <div><strong>{{ model.ref }}</strong><span v-if="model.active" class="inline-badge">活动</span><span v-if="model.alias" class="version-text">{{ model.alias }}</span></div>
                  <small>{{ model.contextWindow.toLocaleString() }} context · {{ model.maxOutput.toLocaleString() }} output · {{ Object.entries(model.capabilities).filter(([, enabled]) => enabled).map(([name]) => name).join(', ') }}</small>
                </div>
                <button class="secondary-button compact-button" @click="testModelReference(model.ref)">测试</button>
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
                <label><span>范围</span><select v-model="mcpForm.scope"><option value="workspace">当前工作区</option><option value="global">全局</option></select></label>
                <label><span>Transport</span><select v-model="mcpForm.transport"><option value="stdio">stdio</option><option value="sse">SSE</option></select></label>
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
                <thead><tr><th>模型</th><th>调用</th><th>成功率</th><th>Tokens</th><th>平均延迟</th><th>成本</th></tr></thead>
                <tbody><tr v-for="item in usage" :key="`${item.provider}/${item.model}`"><td><strong>{{ item.provider }}/{{ item.model }}</strong></td><td>{{ item.calls }}</td><td>{{ Math.round(item.successRate * 100) }}%</td><td>{{ (item.inputTokens + item.outputTokens).toLocaleString() }}</td><td>{{ Math.round(item.avgLatencyMs) }} ms</td><td>${{ item.cost.toFixed(4) }}</td></tr></tbody>
              </table>
            </section>
          </template>

          <div v-if="error" class="settings-feedback error"><X :size="15" />{{ error }}</div>
          <div v-else-if="notice" class="settings-feedback success"><Check :size="15" />{{ notice }}</div>
        </main>
      </div>
    </section>
  </div>
</template>
