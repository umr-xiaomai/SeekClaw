<script setup lang="ts">
import {
  ArrowLeft,
  Bell,
  Blocks,
  Bug,
  Check,
  Database,
  ExternalLink,
  Folder,
  FolderOpen,
  GitBranch,
  Globe,
  Hammer,
  Hexagon,
  Image,
  LoaderCircle,
  MessageSquare,
  Network,
  PackageOpen,
  Palette,
  Plus,
  RefreshCw,
  Search,
  Settings2,
  ShieldCheck,
  Sparkles,
  Telescope,
  Terminal,
  Trash2,
  Upload,
  Users,
  Wrench,
  X
} from '@lucide/vue'
import MarkdownIt from 'markdown-it'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import {
  buildMcpTogglePayload,
  isRemoteTransport,
  mcpStatusText,
  type McpScope,
  type McpServerSummary,
  type McpTransport
} from '../mcp-form'
import McpEditorDialog from './McpEditorDialog.vue'
import OfficialSkillsDialog from './OfficialSkillsDialog.vue'
import TwoPaneLayout from './TwoPaneLayout.vue'

const props = withDefaults(defineProps<{
  open: boolean
  initialTab?: 'plugins' | 'skills'
  daemonConnected?: boolean
}>(), {
  initialTab: 'plugins',
  daemonConnected: true
})

const emit = defineEmits<{
  close: []
}>()

const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true
})

export interface LocalSkillInfo {
  name: string
  description?: string
  version?: string
  enabled: boolean
  directory: string
  scope: 'workspace' | 'global'
}

export interface RemoteSkill {
  id: number
  name: string
  slug: string
  summary: string
  author: string
  version: string
  isOfficial: boolean
  authorUserId: number | null
  authorUsername: string | null
  enabled: boolean
  hasPackage: boolean
  updatedAt: number
  homepage?: string | null
}

export type CustomizationTab = 'plugins' | 'skills'
export type PluginSubTab = 'public' | 'personal'
export type SkillSubTab = 'installed' | 'market'

const activeTab = ref<CustomizationTab>(props.initialTab)
const pluginSubTab = ref<PluginSubTab>('public')
const skillSubTab = ref<SkillSubTab>('installed')
const pluginSearchQuery = ref('')
const skillSearchQuery = ref('')

const mcpServers = ref<McpServerSummary[]>([])
const localSkills = ref<LocalSkillInfo[]>([])
const remoteSkills = ref<RemoteSkill[]>([])

const reloadingMcp = ref(false)
const reloadingSkills = ref(false)
const loadingMarket = ref(false)
const marketError = ref('')
const marketCategory = ref<'all' | 'official' | 'community'>('all')
const installingSlug = ref<string | null>(null)
const savingMcp = ref(false)
const mcpEditorOpen = ref(false)
const editingMcpServer = ref<McpServerSummary | null>(null)
const mcpDialogError = ref('')
const officialMarketOpen = ref(false)

const selectedSkillDetail = ref<{
  name: string
  role?: string
  scope?: string
  version?: string
  description?: string
  directory?: string
  icon?: any
  iconColor?: string
  bg?: string
  sopDetails?: string
} | null>(null)

interface PublicPluginItem {
  id: string
  name: string
  category: '开发' | '通信' | '创意'
  description: string
  tags: string[]
  icon: any
  iconColor: string
  bg: string
  isSystem?: boolean
  defaultTransport?: McpTransport
  defaultCommand?: string
  defaultArgs?: string[]
  defaultUrl?: string
}

const PUBLIC_PLUGINS: PublicPluginItem[] = [
  // 开发
  {
    id: 'sqlite',
    name: 'SQLite',
    category: '开发',
    description: '连接到本地 SQLite 数据库，执行 SQL 查询、分析数据表结构并生成报表',
    tags: ['MCP', 'SQL', '数据库'],
    icon: Database,
    iconColor: '#0284c7',
    bg: '#0284c718',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-sqlite', '--db-path', 'data.db']
  },
  {
    id: 'terminal',
    name: 'Terminal / CLI',
    category: '开发',
    description: '安全沙箱环境内执行系统命令行工具、自动化脚本与编译构建',
    tags: ['系统内置', 'CLI', '终端'],
    icon: Terminal,
    iconColor: '#10b981',
    bg: '#10b98118',
    isSystem: true
  },
  {
    id: 'github',
    name: 'GitHub',
    category: '开发',
    description: '管理代码仓库、拉取 Issue、查看 Commit 历史、差异对比与提交 Pull Request',
    tags: ['MCP', 'VCS', '代码仓库'],
    icon: GitBranch,
    iconColor: '#6366f1',
    bg: '#6366f118',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-github']
  },
  {
    id: 'postgres',
    name: 'PostgreSQL',
    category: '开发',
    description: '企业级关系型数据库连接器，支持执行查询与查看 schema',
    tags: ['MCP', 'SQL', '数据库'],
    icon: Database,
    iconColor: '#3b82f6',
    bg: '#3b82f618',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-postgres', 'postgresql://localhost/mydb']
  },
  {
    id: 'filesystem',
    name: 'Filesystem',
    category: '开发',
    description: '安全限制在指定工作区目录内的多文件检索、深度遍历与读写操作',
    tags: ['MCP', 'IO', '文件管理'],
    icon: Folder,
    iconColor: '#eab308',
    bg: '#eab30818',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-filesystem', '.']
  },
  {
    id: 'fetch',
    name: 'Fetch & Web',
    category: '开发',
    description: '自主联网抓取网页内容、解析 HTML 并提取关键结构化数据',
    tags: ['MCP', 'HTTP', '网页抓取'],
    icon: Globe,
    iconColor: '#06b6d4',
    bg: '#06b6d418',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-fetch']
  },
  // 通信
  {
    id: 'slack',
    name: 'Slack',
    category: '通信',
    description: '向工作空间指定频道发送任务通知、长流程运行报告与交互卡片',
    tags: ['MCP', '协作', '即时消息'],
    icon: MessageSquare,
    iconColor: '#e11d48',
    bg: '#e11d4818',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-slack']
  },
  {
    id: 'webhook',
    name: 'Webhook Alerts',
    category: '通信',
    description: '触发企业内部自动化工作流与长任务完成推送',
    tags: ['系统内置', '通知', '事件'],
    icon: Bell,
    iconColor: '#f97316',
    bg: '#f9731618',
    isSystem: true
  },
  // 创意
  {
    id: 'figma',
    name: 'Figma',
    category: '创意',
    description: '读取与解析 Figma 设计稿、图层属性与 Design Tokens，实现精准设计图还原',
    tags: ['MCP', '设计', 'UI/UX'],
    icon: Palette,
    iconColor: '#a855f7',
    bg: '#a855f718',
    defaultTransport: 'stdio',
    defaultCommand: 'npx',
    defaultArgs: ['-y', '@modelcontextprotocol/server-figma']
  },
  {
    id: 'mermaid',
    name: 'Mermaid Diagrams',
    category: '创意',
    description: '代码化图表渲染，即时生成时序图、架构图、类图与状态流转图',
    tags: ['系统内置', '图表', '流程图'],
    icon: Network,
    iconColor: '#14b8a6',
    bg: '#14b8a618',
    isSystem: true
  }
]

interface SystemSkillItem {
  id: string
  name: string
  role: string
  description: string
  icon: any
  iconColor: string
  bg: string
  sopDetails: string
}

const SYSTEM_SKILLS: SystemSkillItem[] = [
  {
    id: 'review-agent',
    name: 'Review Agent',
    role: '代码审查智能体',
    description: '使用独立审查 Agent 对代码变动执行代码规范、潜在 Bug 与安全风险的多维度审查。',
    icon: ShieldCheck,
    iconColor: '#0284c7',
    bg: '#0284c718',
    sopDetails: `### Review Agent 审查工作流
1. **变更检测**：提取当前任务中所有修改过的文件及 Git Diff 差异。
2. **规范审查**：排查命名规范、类型安全性、未捕获的边界异常与内存泄漏风险。
3. **产出报告**：输出格式化的审查报告，包括严重度等级（Blocker / Warning / Suggestion）与改进补丁。`
  },
  {
    id: 'researcher',
    name: 'Researcher',
    role: '深度调研智能体',
    description: '针对技术方案、第三方库选型、架构设计执行独立沙箱调研并输出报告。',
    icon: Telescope,
    iconColor: '#7c3aed',
    bg: '#7c3aed18',
    sopDetails: `### Researcher 深度调研工作流
1. **需求理解**：解构核心技术难点与待决断的技术决策点。
2. **多维搜索与代码检索**：检索工程内已有实践、官方文档与行业主流成熟模式。
3. **评估报告**：提供对比矩阵（Pros & Cons）、方案依赖成本与分阶段迁移计划。`
  },
  {
    id: 'coder',
    name: 'Coder',
    role: '任务实现智能体',
    description: '自主阅读工程上下文并编写高质量生产级代码，具备代码编辑与验证能力。',
    icon: Hammer,
    iconColor: '#059669',
    bg: '#05966918',
    sopDetails: `### Coder 编码执行工作流
1. **上下文建模**：索引符号定义、引用链与现有测试套件。
2. **最小侵入修改**：按单一职责原则编写模块代码与文档注释。
3. **自主验证**：运行静态分析与编译检查，确保零回归。`
  },
  {
    id: 'tester',
    name: 'Tester',
    role: '质量测试智能体',
    description: '针对代码变更自动生成单元测试、集成测试与边缘用例并运行验证。',
    icon: Bug,
    iconColor: '#d97706',
    bg: '#d9770618',
    sopDetails: `### Tester 质量验证工作流
1. **用例设计**：覆盖常规输入路径、边界极端值与异常捕获逻辑。
2. **测试生成**：基于现有测试框架生成对应测试用例代码。
3. **结果断言**：运行本地测试验证通过状态并报告测试覆盖率。`
  },
  {
    id: 'web-search',
    name: 'Web Search',
    role: '实时联网检索技能',
    description: '自主使用搜索引擎检索权威文档与技术资料并归纳总结。',
    icon: Globe,
    iconColor: '#2563eb',
    bg: '#2563eb18',
    sopDetails: `### Web Search 检索规范
- **权威性保证**：优先索引官方开发文档、RFC 规范与权威开源源码仓库。
- **引用完整**：在给出的分析与解答中，严格附带可靠的源链接。`
  },
  {
    id: 'architect',
    name: 'Architect',
    role: '系统架构设计技能',
    description: '分析多模块系统依赖、设计模式、组件解耦与重构迁移方案。',
    icon: Network,
    iconColor: '#db2777',
    bg: '#db277718',
    sopDetails: `### Architect 架构设计规范
- **领域建模**：划分核心域、支撑域与通信契约。
- **解耦原则**：保证依赖倒置与低耦合度，生成清晰的架构设计图与模块拓扑。`
  }
]

async function requestDaemon<T>(method: string, params: Record<string, unknown> = {}, timeoutMs?: number): Promise<T> {
  const response = await window.seekclaw.daemon.request(method, params, timeoutMs ? { timeoutMs } : undefined)
  return JSON.parse(response.data) as T
}

async function loadMcpServers(): Promise<void> {
  reloadingMcp.value = true
  try {
    const list = await requestDaemon<McpServerSummary[]>('mcp.list')
    mcpServers.value = Array.isArray(list) ? list : []
  } catch (err) {
    console.error('Failed to load MCP servers:', err)
  } finally {
    reloadingMcp.value = false
  }
}

async function loadLocalSkills(): Promise<void> {
  reloadingSkills.value = true
  try {
    const list = await requestDaemon<LocalSkillInfo[]>('skill.list')
    localSkills.value = Array.isArray(list) ? list : []
  } catch (err) {
    console.error('Failed to load skills:', err)
  } finally {
    reloadingSkills.value = false
  }
}

async function reloadMcp(): Promise<void> {
  reloadingMcp.value = true
  try {
    mcpServers.value = await requestDaemon<McpServerSummary[]>('mcp.reload', {}, 120_000)
  } catch (err) {
    alert(`重载 MCP 失败: ${err instanceof Error ? err.message : String(err)}`)
  } finally {
    reloadingMcp.value = false
  }
}

function isPluginConfigured(item: PublicPluginItem): boolean {
  if (item.isSystem) return true
  const lower = item.id.toLowerCase()
  return mcpServers.value.some((s) => s.name.toLowerCase() === lower || s.name.toLowerCase().includes(lower))
}

function getExistingMcp(item: PublicPluginItem): McpServerSummary | undefined {
  const lower = item.id.toLowerCase()
  return mcpServers.value.find((s) => s.name.toLowerCase() === lower || s.name.toLowerCase().includes(lower))
}

function openAddMcp(): void {
  editingMcpServer.value = null
  mcpDialogError.value = ''
  mcpEditorOpen.value = true
}

function addPublicPlugin(item: PublicPluginItem): void {
  if (item.isSystem) return
  const existing = getExistingMcp(item)
  if (existing) {
    editServer(existing)
    return
  }
  editingMcpServer.value = {
    name: item.id,
    scope: 'workspace',
    transport: item.defaultTransport || 'stdio',
    enabled: true,
    command: item.defaultCommand || '',
    args: item.defaultArgs ? [...item.defaultArgs] : [],
    url: item.defaultUrl || '',
    envKeys: [],
    connected: false,
    toolCount: 0
  }
  mcpDialogError.value = ''
  mcpEditorOpen.value = true
}

function editServer(server: McpServerSummary): void {
  editingMcpServer.value = server
  mcpDialogError.value = ''
  mcpEditorOpen.value = true
}

function closeMcpEditor(): void {
  mcpEditorOpen.value = false
  editingMcpServer.value = null
  mcpDialogError.value = ''
}

async function saveMcpServer(payload: { name: string; scope: McpScope; server: Record<string, unknown> }): Promise<void> {
  savingMcp.value = true
  mcpDialogError.value = ''
  try {
    mcpServers.value = await requestDaemon<McpServerSummary[]>('mcp.upsert', payload, 120_000)
    closeMcpEditor()
  } catch (err) {
    mcpDialogError.value = err instanceof Error ? err.message : String(err)
  } finally {
    savingMcp.value = false
  }
}

async function toggleServer(server: McpServerSummary): Promise<void> {
  try {
    mcpServers.value = await requestDaemon<McpServerSummary[]>('mcp.upsert', {
      name: server.name,
      scope: server.scope,
      server: buildMcpTogglePayload(server)
    }, 120_000)
  } catch (err) {
    alert(`切换插件状态失败: ${err instanceof Error ? err.message : String(err)}`)
  }
}

async function deleteServer(server: McpServerSummary): Promise<void> {
  if (!await confirmAction({
    title: '删除插件连接',
    message: `确定要删除插件服务器 “${server.name}” 吗？`,
    confirmLabel: '删除',
    danger: true
  })) return

  try {
    mcpServers.value = await requestDaemon<McpServerSummary[]>('mcp.remove', {
      name: server.name,
      scope: server.scope
    }, 120_000)
  } catch (err) {
    alert(`删除插件失败: ${err instanceof Error ? err.message : String(err)}`)
  }
}

async function toggleSkill(skill: LocalSkillInfo): Promise<void> {
  try {
    localSkills.value = await requestDaemon<LocalSkillInfo[]>('skill.toggle', {
      name: skill.name,
      enabled: !skill.enabled
    })
  } catch (err) {
    alert(`切换技能状态失败: ${err instanceof Error ? err.message : String(err)}`)
  }
}

async function importSkills(): Promise<void> {
  const selection = await window.seekclaw.selectSkillFiles()
  if (!selection || selection.paths.length === 0) return
  try {
    for (const path of selection.paths) {
      localSkills.value = await requestDaemon<LocalSkillInfo[]>('skill.import', { path })
    }
  } catch (err) {
    alert(`导入技能失败: ${err instanceof Error ? err.message : String(err)}`)
  }
}

function showPath(path: string): void {
  void window.seekclaw.showItemInFolder(path)
}

function openSkillDetail(skill: LocalSkillInfo): void {
  selectedSkillDetail.value = {
    name: skill.name,
    scope: skill.scope === 'workspace' ? '工作区技能' : '全局技能',
    version: skill.version,
    description: skill.description || '无附加描述',
    directory: skill.directory,
    icon: Wrench,
    iconColor: '#0284c7',
    bg: '#0284c718',
    sopDetails: `### 技能信息
- **技能名称**：\`${skill.name}\`
- **作用域**：${skill.scope === 'workspace' ? '当前工作区专享' : '全局生效'}
- **存储路径**：\`${skill.directory}\`

> 该技能的 Prompt 指南与工具规则会在对话执行时由 Agent 自动加载。`
  }
}

function openSystemSkillDetail(skill: SystemSkillItem): void {
  selectedSkillDetail.value = {
    name: skill.name,
    role: skill.role,
    description: skill.description,
    icon: skill.icon,
    iconColor: skill.iconColor,
    bg: skill.bg,
    sopDetails: skill.sopDetails
  }
}

function renderMarkdown(content?: string): string {
  if (!content) return ''
  return md.render(content)
}

async function handleOfficialSkillsClose(): Promise<void> {
  officialMarketOpen.value = false
  await loadLocalSkills()
}

async function loadRemoteCatalog(): Promise<void> {
  loadingMarket.value = true
  marketError.value = ''
  try {
    const res = await fetch('https://seekclaw.hoilai.com/api/skills')
    if (!res.ok) {
      throw new Error(`技能市场响应异常 (${res.status})`)
    }
    const data = (await res.json()) as RemoteSkill[]
    remoteSkills.value = Array.isArray(data) ? data : []
  } catch (err) {
    marketError.value = err instanceof Error ? err.message : '无法连接到官方技能市场，请检查网络连接'
  } finally {
    loadingMarket.value = false
  }
}

function switchToMarketTab(): void {
  skillSubTab.value = 'market'
  if (remoteSkills.value.length === 0 && !loadingMarket.value) {
    void loadRemoteCatalog()
  }
}

function isRemoteSkillInstalled(skill: RemoteSkill): boolean {
  const slugLower = skill.slug.toLowerCase()
  const nameLower = skill.name.toLowerCase()
  return localSkills.value.some((local) => {
    const localLower = local.name.toLowerCase()
    return localLower === slugLower || localLower === nameLower || localLower.includes(slugLower)
  })
}

async function installRemoteSkill(skill: RemoteSkill): Promise<void> {
  installingSlug.value = skill.slug
  try {
    const downloadUrl = `https://seekclaw.hoilai.com/api/skills/${encodeURIComponent(skill.slug)}/download`
    const updated = await requestDaemon<LocalSkillInfo[]>('skill.import', {
      path: downloadUrl,
      overwrite: true
    })
    localSkills.value = Array.isArray(updated) ? updated : localSkills.value
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    alert(`安装技能失败: ${message}`)
  } finally {
    installingSlug.value = null
  }
}

async function openRemoteSkillDetail(skill: RemoteSkill): Promise<void> {
  selectedSkillDetail.value = {
    name: skill.name,
    role: skill.isOfficial ? '官方精选技能' : '社区生态技能',
    scope: `作者: ${skill.author || skill.authorUsername || 'SeekClaw 社区'}`,
    version: skill.version,
    description: skill.summary,
    icon: Sparkles,
    iconColor: '#0284c7',
    bg: '#0284c718',
    sopDetails: `正在从服务器加载详细规范文档…`
  }
  try {
    const res = await fetch(`https://seekclaw.hoilai.com/api/skills/${encodeURIComponent(skill.slug)}`)
    if (res.ok) {
      const detail = (await res.json()) as { readmeMarkdown?: string; summary?: string }
      if (selectedSkillDetail.value && selectedSkillDetail.value.name === skill.name) {
        selectedSkillDetail.value.sopDetails = detail.readmeMarkdown || detail.summary || '暂无详细介绍'
      }
    }
  } catch (err) {
    console.error('Failed to load remote skill detail:', err)
  }
}

// Filtered Lists
const filteredPublicCategories = computed(() => {
  const q = pluginSearchQuery.value.trim().toLowerCase()
  const categories: Array<{ name: string; plugins: PublicPluginItem[] }> = [
    { name: '开发', plugins: [] },
    { name: '通信', plugins: [] },
    { name: '创意', plugins: [] }
  ]

  for (const item of PUBLIC_PLUGINS) {
    if (q) {
      const match = item.name.toLowerCase().includes(q) ||
        item.description.toLowerCase().includes(q) ||
        item.tags.some((t) => t.toLowerCase().includes(q))
      if (!match) continue
    }
    const cat = categories.find((c) => c.name === item.category)
    cat?.plugins.push(item)
  }

  return categories.filter((c) => c.plugins.length > 0)
})

const filteredPersonalServers = computed(() => {
  const q = pluginSearchQuery.value.trim().toLowerCase()
  if (!q) return mcpServers.value
  return mcpServers.value.filter((server) =>
    server.name.toLowerCase().includes(q) ||
    server.transport.toLowerCase().includes(q) ||
    (server.command && server.command.toLowerCase().includes(q))
  )
})

const filteredInstalledSkills = computed(() => {
  const q = skillSearchQuery.value.trim().toLowerCase()
  if (!q) return localSkills.value
  return localSkills.value.filter((s) =>
    s.name.toLowerCase().includes(q) ||
    (s.description && s.description.toLowerCase().includes(q)) ||
    s.directory.toLowerCase().includes(q)
  )
})

const filteredSystemSkills = computed(() => {
  const q = skillSearchQuery.value.trim().toLowerCase()
  if (!q) return SYSTEM_SKILLS
  return SYSTEM_SKILLS.filter((s) =>
    s.name.toLowerCase().includes(q) ||
    s.role.toLowerCase().includes(q) ||
    s.description.toLowerCase().includes(q)
  )
})

const filteredRemoteSkills = computed(() => {
  let list = remoteSkills.value
  if (marketCategory.value === 'official') {
    list = list.filter((s) => s.isOfficial)
  } else if (marketCategory.value === 'community') {
    list = list.filter((s) => !s.isOfficial)
  }

  const q = skillSearchQuery.value.trim().toLowerCase()
  if (!q) return list

  return list.filter((s) =>
    s.name.toLowerCase().includes(q) ||
    s.slug.toLowerCase().includes(q) ||
    s.summary.toLowerCase().includes(q) ||
    s.author.toLowerCase().includes(q)
  )
})

function getServerStatusClass(server: McpServerSummary): string {
  if (server.connected) return 'status-online'
  if (server.connecting) return 'status-connecting'
  if (!server.enabled) return 'status-disabled'
  return 'status-error'
}

let unsubscribeMcpEvents: (() => void) | null = null

onMounted(() => {
  unsubscribeMcpEvents = window.seekclaw.daemon.onEvent((message) => {
    if (message.event !== 'mcp.updated') return
    void loadMcpServers()
  })
})

onBeforeUnmount(() => {
  unsubscribeMcpEvents?.()
  unsubscribeMcpEvents = null
})

watch(() => props.open, (open) => {
  if (open) {
    if (props.initialTab) activeTab.value = props.initialTab
    void loadMcpServers()
    void loadLocalSkills()
  }
}, { immediate: true })

watch(() => props.initialTab, (tab) => {
  if (tab) activeTab.value = tab
})

function handleKeydown(event: KeyboardEvent): void {
  if (!props.open) return
  if (event.key === 'Escape') {
    if (selectedSkillDetail.value) {
      selectedSkillDetail.value = null
      event.stopPropagation()
      return
    }
    if (mcpEditorOpen.value) {
      mcpEditorOpen.value = false
      event.stopPropagation()
      return
    }
    if (officialMarketOpen.value) {
      officialMarketOpen.value = false
      event.stopPropagation()
      return
    }
    emit('close')
  }
}

onMounted(() => {
  window.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <div v-if="open" class="customization-workbench embedded-page" role="region" aria-labelledby="customization-title">
    <TwoPaneLayout storage-key="seekclaw-customization-sidebar-width" :default-width="240" :min-width="190"
      :max-width="360" aria-label="自定义功能导航">
      <template #sidebar>
        <nav class="customization-nav">
          <div class="customization-nav-header">
            <button class="page-back-button" title="返回" @click="emit('close')">
              <ArrowLeft :size="16" />
              <span>返回</span>
            </button>
          </div>

          <div class="customization-title-row">
            <h2 id="customization-title" class="customization-title">自定义</h2>
          </div>

          <div class="customization-nav-list">
            <button class="customization-nav-item" :class="{ active: activeTab === 'plugins' }"
              @click="activeTab = 'plugins'">
              <div class="nav-icon-wrapper">
                <span class="at-symbol">@</span>
              </div>
              <div class="nav-item-body">
                <span class="nav-item-title">插件</span>
                <span class="nav-item-subtitle">{{ mcpServers.length > 0 ? `${mcpServers.length} 项已配置` : '扩展与连接'
                  }}</span>
              </div>
            </button>

            <button class="customization-nav-item" :class="{ active: activeTab === 'skills' }"
              @click="activeTab = 'skills'">
              <div class="nav-icon-wrapper">
                <Hexagon :size="16" />
              </div>
              <div class="nav-item-body">
                <span class="nav-item-title">技能</span>
                <span class="nav-item-subtitle">{{ `${localSkills.length + 6} 项能力` }}</span>
              </div>
            </button>
          </div>

          <div class="customization-nav-footer">
            <div class="customization-tip-box">
              <span class="tip-title">关于自定义</span>
              <p class="tip-text">
                <strong>插件</strong>连接外部数据库、CLI 与第三方服务；<strong>技能</strong>赋予 Agent SOP 规范与子智能体协作流程。
              </p>
            </div>
          </div>
        </nav>
      </template>

      <!-- Main Content Area -->
      <div class="customization-content">
        <!-- PLUGINS VIEW -->
        <section v-if="activeTab === 'plugins'" class="tab-view-container">
          <header class="view-header">
            <div class="header-main-row">
              <div class="custom-search-box">
                <Search :size="15" />
                <input v-model="pluginSearchQuery" placeholder="搜索插件..." aria-label="搜索插件" />
                <button v-if="pluginSearchQuery" class="clear-search-button" title="清空" @click="pluginSearchQuery = ''">
                  <X :size="13" />
                </button>
              </div>

              <div class="header-action-group">
                <button class="icon-button" title="刷新 MCP 插件" :disabled="reloadingMcp" @click="loadMcpServers">
                  <RefreshCw :size="16" :class="{ spin: reloadingMcp }" />
                </button>

                <button class="primary-button add-btn" @click="openAddMcp">
                  <Plus :size="15" />
                  <span>添加</span>
                </button>
              </div>
            </div>

            <!-- Segmented Switch: 公开 / 个人 -->
            <div class="segmented-control-bar">
              <button class="segmented-btn" :class="{ active: pluginSubTab === 'public' }"
                @click="pluginSubTab = 'public'">
                公开
              </button>
              <button class="segmented-btn" :class="{ active: pluginSubTab === 'personal' }"
                @click="pluginSubTab = 'personal'">
                个人
                <span v-if="mcpServers.length > 0" class="badge-count">{{ mcpServers.length }}</span>
              </button>
            </div>
          </header>

          <!-- VIEW: PUBLIC PLUGINS -->
          <div v-if="pluginSubTab === 'public'" class="view-scroll-body">
            <div v-if="filteredPublicCategories.length === 0" class="empty-state">
              <Search :size="28" class="empty-icon" />
              <p>未找到匹配 “{{ pluginSearchQuery }}” 的公开插件</p>
            </div>

            <div v-for="cat in filteredPublicCategories" :key="cat.name" class="category-group">
              <h3 class="category-title">{{ cat.name }}</h3>

              <div class="cards-grid">
                <article v-for="item in cat.plugins" :key="item.id" class="card plugin-card"
                  :class="{ configured: isPluginConfigured(item) }">
                  <div class="card-top">
                    <div class="card-icon-container" :style="{ backgroundColor: item.bg }">
                      <component :is="item.icon" :size="20" :style="{ color: item.iconColor }" />
                    </div>

                    <div class="card-action">
                      <button v-if="!isPluginConfigured(item)" class="action-add-btn" title="添加并配置此插件"
                        @click="addPublicPlugin(item)">
                        <Plus :size="16" />
                      </button>
                      <span v-else class="configured-badge" title="已配置并在使用中">
                        <Check :size="13" /> 已添加
                      </span>
                    </div>
                  </div>

                  <div class="card-middle">
                    <h4 class="card-name">{{ item.name }}</h4>
                    <p class="card-desc">{{ item.description }}</p>
                  </div>

                  <div class="card-bottom">
                    <span v-for="tag in item.tags" :key="tag" class="tag-pill">{{ tag }}</span>
                  </div>
                </article>
              </div>
            </div>
          </div>

          <!-- VIEW: PERSONAL PLUGINS -->
          <div v-else class="view-scroll-body">
            <div v-if="filteredPersonalServers.length === 0" class="empty-state">
              <div v-if="pluginSearchQuery">未找到匹配 “{{ pluginSearchQuery }}” 的个人插件</div>
              <div v-else class="empty-personal-placeholder">
                <Blocks :size="34" class="empty-icon" />
                <h4>尚未配置个人插件</h4>
                <p>你可以从「公开」插件库中一键添加常用插件（如 SQLite、Git、Slack），或点击右上角「+ 添加」自定义配置 MCP 服务器。</p>
                <button class="secondary-button" @click="pluginSubTab = 'public'">
                  浏览公开插件库
                </button>
              </div>
            </div>

            <div v-else class="personal-list">
              <article v-for="server in filteredPersonalServers" :key="`${server.scope}:${server.name}`"
                class="personal-row-card">
                <div class="personal-left">
                  <div class="server-avatar">
                    <Blocks :size="18" />
                  </div>
                  <div class="server-detail">
                    <div class="server-title-line">
                      <strong class="server-name">{{ server.name }}</strong>
                      <span class="badge-scope">{{ server.scope === 'workspace' ? '工作区' : '全局' }}</span>
                      <span class="badge-transport">{{ server.transport }}</span>
                    </div>
                    <div class="server-status-line">
                      <span class="status-indicator-dot" :class="getServerStatusClass(server)" />
                      <span class="status-desc-text">{{ mcpStatusText(server) }}</span>
                      <span v-if="server.command" class="cmd-text"
                        :title="server.command + ' ' + (server.args || []).join(' ')">
                        {{ server.command }}
                      </span>
                    </div>
                  </div>
                </div>

                <div class="personal-actions">
                  <button class="switch-control" :class="{ active: server.enabled, pending: server.connecting }"
                    :disabled="server.connecting" :title="server.enabled ? '禁用插件' : '启用插件'"
                    @click="toggleServer(server)">
                    <LoaderCircle v-if="server.connecting" class="spin" :size="12" />
                    <span v-else />
                  </button>

                  <button class="icon-button compact" title="编辑配置" @click="editServer(server)">
                    <Settings2 :size="15" />
                  </button>

                  <button class="icon-button compact danger-icon" title="删除插件" @click="deleteServer(server)">
                    <Trash2 :size="15" />
                  </button>
                </div>
              </article>
            </div>
          </div>
        </section>

        <!-- SKILLS VIEW -->
        <section v-else class="tab-view-container">
          <header class="view-header">
            <div class="header-main-row">
              <div class="custom-search-box">
                <Search :size="15" />
                <input
                  v-model="skillSearchQuery"
                  :placeholder="skillSubTab === 'market' ? '搜索官方与社区技能...' : '搜索已安装或系统技能...'"
                  aria-label="搜索技能"
                />
                <button
                  v-if="skillSearchQuery"
                  class="clear-search-button"
                  title="清空"
                  @click="skillSearchQuery = ''"
                >
                  <X :size="13" />
                </button>
              </div>

              <div class="header-action-group">
                <button
                  class="icon-button"
                  :title="skillSubTab === 'market' ? '刷新市场' : '刷新技能'"
                  :disabled="reloadingSkills || loadingMarket"
                  @click="skillSubTab === 'market' ? loadRemoteCatalog() : loadLocalSkills()"
                >
                  <RefreshCw :size="16" :class="{ spin: reloadingSkills || loadingMarket }" />
                </button>

                <button
                  v-if="skillSubTab === 'installed'"
                  class="secondary-button"
                  title="浏览官方精选技能市场"
                  @click="switchToMarketTab"
                >
                  <Sparkles :size="15" />
                  <span>官方市场</span>
                </button>
                <button
                  v-else
                  class="secondary-button"
                  title="查看已安装技能"
                  @click="skillSubTab = 'installed'"
                >
                  <Check :size="15" />
                  <span>已安装</span>
                </button>

                <button class="primary-button add-btn" title="导入本地技能文件 (.md, .zip)" @click="importSkills">
                  <Plus :size="15" />
                  <span>导入</span>
                </button>
              </div>
            </div>

            <!-- Segmented Switch: 已安装 / 官方市场 -->
            <div class="segmented-control-bar">
              <button
                class="segmented-btn"
                :class="{ active: skillSubTab === 'installed' }"
                @click="skillSubTab = 'installed'"
              >
                已安装
                <span class="badge-count">{{ localSkills.length + filteredSystemSkills.length }}</span>
              </button>
              <button
                class="segmented-btn"
                :class="{ active: skillSubTab === 'market' }"
                @click="switchToMarketTab"
              >
                官方市场
                <span v-if="remoteSkills.length > 0" class="badge-count">{{ remoteSkills.length }}</span>
              </button>
            </div>
          </header>

          <!-- VIEW: INSTALLED SKILLS -->
          <div v-if="skillSubTab === 'installed'" class="view-scroll-body">
            <!-- SECTION 1: 已安装技能 -->
            <div class="category-group">
              <div class="section-heading-row">
                <h3 class="category-title">已安装</h3>
                <span class="count-tag">{{ filteredInstalledSkills.length }}</span>
              </div>

              <div v-if="filteredInstalledSkills.length === 0" class="empty-installed-hint">
                <div v-if="skillSearchQuery">未找到匹配 “{{ skillSearchQuery }}” 的已安装技能</div>
                <div v-else class="empty-box">
                  <Wrench :size="24" class="empty-icon-sm" />
                  <div>
                    <p>尚未安装自定义技能。你可以点击右上角「导入」本地 .md / .zip 文件，或在「官方市场」一键安装。</p>
                    <button class="secondary-button compact mt-2" @click="switchToMarketTab">
                      <Sparkles :size="13" /> 前往官方市场
                    </button>
                  </div>
                </div>
              </div>

              <div v-else class="cards-grid">
                <article
                  v-for="skill in filteredInstalledSkills"
                  :key="skill.name"
                  class="card skill-card clickable"
                  @click="openSkillDetail(skill)"
                >
                  <div class="card-top">
                    <div class="card-icon-container default-skill">
                      <Wrench :size="18" />
                    </div>
                    <div class="card-action">
                      <button
                        class="switch-control compact"
                        :class="{ active: skill.enabled }"
                        :title="skill.enabled ? '禁用技能' : '启用技能'"
                        @click.stop="toggleSkill(skill)"
                      >
                        <span />
                      </button>
                    </div>
                  </div>

                  <div class="card-middle">
                    <div class="title-with-tags">
                      <h4 class="card-name">{{ skill.name }}</h4>
                      <span v-if="skill.version" class="version-tag">v{{ skill.version }}</span>
                      <span class="scope-tag">{{ skill.scope === 'workspace' ? '工作区' : '全局' }}</span>
                    </div>
                    <p class="card-desc">{{ skill.description || skill.directory }}</p>
                  </div>

                  <div class="card-bottom">
                    <button
                      class="dir-link-btn"
                      title="在系统文件管理器中打开"
                      @click.stop="showPath(skill.directory)"
                    >
                      <FolderOpen :size="13" />
                      <span>打开位置</span>
                    </button>
                  </div>
                </article>
              </div>
            </div>

            <!-- SECTION 2: 系统内置能力 (Codex Built-in Skills & Sub-Agents) -->
            <div class="category-group system-category-margin">
              <div class="section-heading-row">
                <h3 class="category-title">系统</h3>
                <span class="count-tag">{{ filteredSystemSkills.length }}</span>
              </div>

              <div class="cards-grid">
                <article
                  v-for="item in filteredSystemSkills"
                  :key="item.id"
                  class="card skill-card system-card clickable"
                  @click="openSystemSkillDetail(item)"
                >
                  <div class="card-top">
                    <div class="card-icon-container" :style="{ backgroundColor: item.bg }">
                      <component :is="item.icon" :size="18" :style="{ color: item.iconColor }" />
                    </div>
                    <div class="card-action">
                      <span class="check-indicator" title="系统核心常驻能力">
                        <Check :size="14" />
                      </span>
                    </div>
                  </div>

                  <div class="card-middle">
                    <div class="title-with-tags">
                      <h4 class="card-name">{{ item.name }}</h4>
                      <span class="role-pill">{{ item.role }}</span>
                    </div>
                    <p class="card-desc">{{ item.description }}</p>
                  </div>

                  <div class="card-bottom">
                    <span class="system-tag">系统内置</span>
                  </div>
                </article>
              </div>
            </div>
          </div>

          <!-- VIEW: OFFICIAL REMOTE MARKETPLACE -->
          <div v-else class="view-scroll-body">
            <!-- Market Category Chips -->
            <div class="market-chips-bar">
              <button
                class="category-chip"
                :class="{ active: marketCategory === 'all' }"
                @click="marketCategory = 'all'"
              >
                全部技能
                <span v-if="remoteSkills.length > 0" class="chip-count">{{ remoteSkills.length }}</span>
              </button>
              <button
                class="category-chip"
                :class="{ active: marketCategory === 'official' }"
                @click="marketCategory = 'official'"
              >
                官方精选
                <span v-if="remoteSkills.length > 0" class="chip-count">{{ remoteSkills.filter((s) => s.isOfficial).length }}</span>
              </button>
              <button
                class="category-chip"
                :class="{ active: marketCategory === 'community' }"
                @click="marketCategory = 'community'"
              >
                社区生态
                <span v-if="remoteSkills.length > 0" class="chip-count">{{ remoteSkills.filter((s) => !s.isOfficial).length }}</span>
              </button>

              <button
                class="full-market-link"
                title="以完整双栏视图浏览技能市场"
                @click="officialMarketOpen = true"
              >
                <ExternalLink :size="13" />
                <span>完整市场视图</span>
              </button>
            </div>

            <!-- Loading State -->
            <div v-if="loadingMarket && remoteSkills.length === 0" class="market-loading-box">
              <LoaderCircle class="spin" :size="30" />
              <p>正在连接官方技能市场…</p>
            </div>

            <!-- Error State -->
            <div v-else-if="marketError && remoteSkills.length === 0" class="empty-state error">
              <Search :size="30" class="empty-icon" />
              <p>{{ marketError }}</p>
              <button class="secondary-button" @click="loadRemoteCatalog">重试连接</button>
            </div>

            <!-- Empty Search State -->
            <div v-else-if="filteredRemoteSkills.length === 0" class="empty-state">
              <Search :size="28" class="empty-icon" />
              <p>未找到匹配 “{{ skillSearchQuery }}” 的技能</p>
            </div>

            <!-- Remote Skills Cards Grid -->
            <div v-else class="cards-grid">
              <article
                v-for="skill in filteredRemoteSkills"
                :key="skill.id"
                class="card skill-card clickable"
                @click="openRemoteSkillDetail(skill)"
              >
                <div class="card-top">
                  <div
                    class="card-icon-container"
                    :style="{ backgroundColor: skill.isOfficial ? '#0284c718' : '#8b5cf618' }"
                  >
                    <ShieldCheck v-if="skill.isOfficial" :size="20" style="color: #0284c7;" />
                    <Sparkles v-else :size="20" style="color: #8b5cf6;" />
                  </div>

                  <div class="card-action">
                    <span v-if="isRemoteSkillInstalled(skill)" class="configured-badge" title="该技能已安装">
                      <Check :size="13" /> 已安装
                    </span>
                    <button
                      v-else
                      class="card-install-btn"
                      :disabled="installingSlug === skill.slug"
                      title="下载并安装此技能"
                      @click.stop="installRemoteSkill(skill)"
                    >
                      <LoaderCircle v-if="installingSlug === skill.slug" class="spin" :size="13" />
                      <Plus v-else :size="14" />
                      <span>安装</span>
                    </button>
                  </div>
                </div>

                <div class="card-middle">
                  <div class="title-with-tags">
                    <h4 class="card-name">{{ skill.name }}</h4>
                    <span v-if="skill.version" class="version-tag">v{{ skill.version }}</span>
                    <span v-if="skill.isOfficial" class="role-pill">官方精选</span>
                  </div>
                  <p class="card-desc">{{ skill.summary }}</p>
                </div>

                <div class="card-bottom">
                  <span class="author-label">作者: {{ skill.author || skill.authorUsername || 'SeekClaw' }}</span>
                  <button class="dir-link-btn" title="查看技能说明文档" @click.stop="openRemoteSkillDetail(skill)">
                    <ExternalLink :size="12" />
                    <span>查看介绍</span>
                  </button>
                </div>
              </article>
            </div>
          </div>
        </section>
      </div>
    </TwoPaneLayout>

    <!-- Skill Detail Inspection Modal -->
    <div v-if="selectedSkillDetail" class="detail-modal-overlay" @click.self="selectedSkillDetail = null">
      <div class="detail-modal-box">
        <header class="detail-modal-header">
          <div class="modal-header-left">
            <div class="modal-icon-wrap" :style="{ backgroundColor: selectedSkillDetail.bg || 'var(--surface-hover)' }">
              <component :is="selectedSkillDetail.icon || Sparkles" :size="22"
                :style="{ color: selectedSkillDetail.iconColor || 'var(--accent)' }" />
            </div>
            <div class="modal-header-titles">
              <h3>{{ selectedSkillDetail.name }}</h3>
              <div class="modal-pills">
                <span v-if="selectedSkillDetail.role" class="meta-tag">{{ selectedSkillDetail.role }}</span>
                <span v-if="selectedSkillDetail.scope" class="meta-tag">{{ selectedSkillDetail.scope }}</span>
                <span v-if="selectedSkillDetail.version" class="meta-tag">v{{ selectedSkillDetail.version }}</span>
              </div>
            </div>
          </div>

          <button class="icon-button" title="关闭" @click="selectedSkillDetail = null">
            <X :size="18" />
          </button>
        </header>

        <div class="detail-modal-content">
          <p class="modal-summary">{{ selectedSkillDetail.description }}</p>

          <div v-if="selectedSkillDetail.sopDetails" class="sop-markdown-wrapper">
            <div class="sop-html" v-html="renderMarkdown(selectedSkillDetail.sopDetails)" />
          </div>

          <div v-if="selectedSkillDetail.directory" class="dir-info-card">
            <div class="dir-info-text">
              <span class="dir-label">存储路径：</span>
              <code class="dir-code">{{ selectedSkillDetail.directory }}</code>
            </div>
            <button class="secondary-button compact" @click="showPath(selectedSkillDetail.directory)">
              <FolderOpen :size="13" />
              <span>打开文件夹</span>
            </button>
          </div>
        </div>

        <footer class="detail-modal-footer">
          <button class="secondary-button" @click="selectedSkillDetail = null">关闭</button>
        </footer>
      </div>
    </div>

    <!-- MCP Server Editor Modal -->
    <McpEditorDialog :open="mcpEditorOpen" :server="editingMcpServer" :saving="savingMcp" :error="mcpDialogError"
      @close="closeMcpEditor" @save="saveMcpServer" />

    <!-- Official Remote Skills Market Dialog Overlay -->
    <div v-if="officialMarketOpen" class="official-market-modal-overlay">
      <OfficialSkillsDialog :open="officialMarketOpen" @close="handleOfficialSkillsClose" />
    </div>
  </div>
</template>

<style scoped>
.customization-workbench.embedded-page {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: var(--bg);
  overflow: hidden;
  position: relative;
}

/* Sidebar Nav */
.customization-nav {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: 12px 14px 16px;
  overflow-y: auto;
  user-select: none;
  box-sizing: border-box;
}

.customization-nav-header {
  margin-bottom: 6px;
}

.page-back-button {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  height: 34px;
  padding: 0 10px;
  color: var(--text-secondary);
  font-size: 13px;
  font-weight: 500;
  background: transparent;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.page-back-button:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 76%, transparent);
}

.customization-title-row {
  padding: 8px 10px 10px;
}

.customization-title {
  margin: 0;
  font-size: 19px;
  font-weight: 700;
  color: var(--text);
  letter-spacing: -0.01em;
}

.customization-nav-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex: 1 1 auto;
}

.customization-nav-item {
  display: flex;
  align-items: center;
  width: 100%;
  min-height: 48px;
  gap: 12px;
  padding: 8px 12px;
  color: var(--text-secondary);
  background: transparent;
  border: 1px solid transparent;
  border-radius: 9px;
  cursor: pointer;
  text-align: left;
  transition: background-color 140ms ease, color 140ms ease, border-color 140ms ease;
}

.customization-nav-item:hover {
  background: color-mix(in srgb, var(--surface-hover) 76%, transparent);
  color: var(--text);
}

.customization-nav-item.active {
  background: var(--surface);
  color: var(--text);
  border-color: color-mix(in srgb, var(--border) 80%, transparent);
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.04);
}

.nav-icon-wrapper {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text);
}

.customization-nav-item.active .nav-icon-wrapper {
  background: color-mix(in srgb, var(--accent) 15%, transparent);
  color: var(--accent);
}

.at-symbol {
  font-size: 15px;
  font-weight: 700;
  line-height: 1;
}

.nav-item-body {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.nav-item-title {
  font-size: 13.5px;
  font-weight: 600;
  line-height: 1.25;
}

.nav-item-subtitle {
  font-size: 11.5px;
  color: var(--text-muted);
  margin-top: 2px;
}

.customization-nav-footer {
  margin-top: auto;
  padding-top: 14px;
}

.customization-tip-box {
  padding: 10px 12px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--surface) 60%, transparent);
  border: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
}

.tip-title {
  display: block;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  margin-bottom: 4px;
}

.tip-text {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-muted);
}

/* Content Area */
.customization-content {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-width: 0;
  background: var(--bg);
}

.tab-view-container {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-width: 0;
  overflow: hidden;
}

/* Header */
.view-header {
  padding: 14px 24px 10px;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--bg) 95%, transparent);
}

.header-main-row {
  display: flex;
  align-items: center;
  gap: 14px;
}

.custom-search-box {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
  max-width: 480px;
  height: 36px;
  padding: 0 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
  color: var(--text-secondary);
  transition: border-color 140ms ease;
}

.custom-search-box:focus-within {
  border-color: var(--accent);
}

.custom-search-box input {
  flex: 1;
  min-width: 0;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  outline: none;
}

.clear-search-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  border: none;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  border-radius: 50%;
}

.clear-search-button:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--text) 10%, transparent);
}

.header-action-group {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.add-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 34px;
  padding: 0 14px;
  border-radius: 8px;
  font-size: 13px;
  font-weight: 550;
}

/* Segmented Control Bar */
.segmented-control-bar {
  display: inline-flex;
  padding: 3px;
  margin-top: 12px;
  background: color-mix(in srgb, var(--text) 6%, transparent);
  border-radius: 8px;
}

.segmented-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 16px;
  font-size: 12.5px;
  font-weight: 500;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease, box-shadow 140ms ease;
}

.segmented-btn:hover:not(.active) {
  color: var(--text);
}

.segmented-btn.active {
  background: var(--surface);
  color: var(--text);
  font-weight: 600;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08);
}

.badge-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  font-size: 11px;
  border-radius: 9px;
  background: color-mix(in srgb, var(--text) 10%, transparent);
  color: var(--text);
}

/* View Scroll Body */
.view-scroll-body {
  flex: 1;
  min-height: 0;
  padding: 20px 24px 40px;
  overflow-y: auto;
}

.category-group {
  margin-bottom: 28px;
}

.system-category-margin {
  margin-top: 32px;
}

.category-title {

  font-size: 14.5px;
  font-weight: 650;
  color: var(--text);
  letter-spacing: -0.01em;
}

.section-heading-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 14px;
}

.count-tag {
  font-size: 11px;
  font-weight: 600;
  padding: 1px 7px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text-secondary);
}

/* Cards Grid */
.cards-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 14px;
}

.card {
  display: flex;
  flex-direction: column;
  padding: 14px 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 11px;
  box-sizing: border-box;
  transition: background-color 150ms ease, border-color 150ms ease, box-shadow 150ms ease;
}

.card:hover {
  background: var(--surface-hover);
  border-color: color-mix(in srgb, var(--accent) 30%, var(--border));
}

.card.clickable {
  cursor: pointer;
}

.card-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

.card-icon-container {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 9px;
  background: color-mix(in srgb, var(--accent) 15%, transparent);
}

.card-icon-container.default-skill {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

.action-add-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text-secondary);
  cursor: pointer;
  transition: all 140ms ease;
}

.action-add-btn:hover {
  border-color: var(--accent);
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 10%, transparent);
}

.configured-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11.5px;
  font-weight: 550;
  color: #10b981;
  padding: 3px 8px;
  border-radius: 6px;
  background: #10b98118;
}

.check-indicator {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 15%, transparent);
}

.card-middle {
  flex: 1;
  min-height: 0;
  margin-bottom: 12px;
}

.card-name {
  margin: 0 0 6px;
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.title-with-tags {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
  flex-wrap: wrap;
}

.version-tag,
.scope-tag,
.role-pill {
  font-size: 10.5px;
  font-weight: 500;
  padding: 1px 6px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text-secondary);
}

.role-pill {
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  color: var(--accent);
}

.card-desc {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.card-bottom {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  padding-top: 8px;
  border-top: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
}

.tag-pill {
  font-size: 10.5px;
  padding: 2px 6px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text-muted);
}

.system-tag {
  font-size: 11px;
  color: var(--text-muted);
}

.dir-link-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 8px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  border-radius: 5px;
  cursor: pointer;
  transition: all 140ms ease;
}

.dir-link-btn:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--text) 8%, transparent);
}

/* Personal Plugins List */
.personal-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.personal-row-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.personal-left {
  display: flex;
  align-items: center;
  gap: 14px;
  min-width: 0;
}

.server-avatar {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 38px;
  height: 38px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent) 15%, transparent);
  color: var(--accent);
  flex-shrink: 0;
}

.server-detail {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.server-title-line {
  display: flex;
  align-items: center;
  gap: 8px;
}

.server-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.badge-scope,
.badge-transport {
  font-size: 10.5px;
  font-weight: 500;
  padding: 1px 6px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text-secondary);
}

.server-status-line {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--text-secondary);
}

.status-indicator-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}

.status-online {
  background-color: #10b981;
}

.status-connecting {
  background-color: #eab308;
}

.status-disabled {
  background-color: #94a3b8;
}

.status-error {
  background-color: #ef4444;
}

.status-desc-text {
  font-size: 11.5px;
}

.cmd-text {
  font-family: monospace;
  font-size: 11px;
  color: var(--text-muted);
  max-width: 320px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.personal-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

/* Empty States */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 60px 20px;
  color: var(--text-muted);
  text-align: center;
}

.empty-icon {
  margin-bottom: 12px;
  opacity: 0.6;
}

.empty-personal-placeholder {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  max-width: 440px;
  margin: 40px auto;
  text-align: center;
}

.empty-personal-placeholder h4 {
  margin: 12px 0 6px;
  font-size: 16px;
  color: var(--text);
}

.empty-personal-placeholder p {
  margin: 0 0 18px;
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.empty-installed-hint {
  padding: 18px;
  border: 1px dashed var(--border);
  border-radius: 10px;
  color: var(--text-secondary);
  font-size: 12.5px;
}

.empty-box {
  display: flex;
  align-items: center;
  gap: 12px;
}

.empty-box p {
  margin: 0;
  line-height: 1.45;
}

.empty-icon-sm {
  opacity: 0.6;
}

/* Detail Modal */
.detail-modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(4px);
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
}

.detail-modal-box {
  display: flex;
  flex-direction: column;
  width: 100%;
  max-width: 580px;
  max-height: 85vh;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 14px;
  box-shadow: 0 16px 36px rgba(0, 0, 0, 0.28);
  overflow: hidden;
}

.detail-modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px;
  border-bottom: 1px solid var(--border);
}

.modal-header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.modal-icon-wrap {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 10px;
}

.modal-header-titles h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
  color: var(--text);
}

.modal-pills {
  display: flex;
  gap: 6px;
  margin-top: 4px;
}

.meta-tag {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--accent) 15%, transparent);
  color: var(--accent);
}

.detail-modal-content {
  flex: 1;
  min-height: 0;
  padding: 20px;
  overflow-y: auto;
}

.modal-summary {
  margin: 0 0 16px;
  font-size: 13.5px;
  line-height: 1.5;
  color: var(--text);
}

.sop-markdown-wrapper {
  padding: 14px 16px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--bg) 60%, transparent);
  border: 1px solid var(--border);
  margin-bottom: 16px;
}

.sop-html :deep(h3) {
  margin: 0 0 10px;
  font-size: 14px;
  font-weight: 650;
  color: var(--text);
}

.sop-html :deep(ol),
.sop-html :deep(ul) {
  margin: 0;
  padding-left: 18px;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--text-secondary);
}

.sop-html :deep(li) {
  margin-bottom: 6px;
}

.sop-html :deep(strong) {
  color: var(--text);
}

.dir-info-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 14px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--text) 4%, transparent);
  border: 1px solid var(--border);
}

.dir-info-text {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.dir-label {
  font-size: 12px;
  color: var(--text-muted);
  flex-shrink: 0;
}

.dir-code {
  font-family: monospace;
  font-size: 11.5px;
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.detail-modal-footer {
  display: flex;
  justify-content: flex-end;
  padding: 12px 20px;
  border-top: 1px solid var(--border);
  background: color-mix(in srgb, var(--surface) 90%, transparent);
}

/* Switch control standard */
.switch-control {
  position: relative;
  width: 38px;
  height: 22px;
  padding: 2px;
  background: color-mix(in srgb, var(--text) 16%, transparent);
  border: none;
  border-radius: 12px;
  cursor: pointer;
  transition: background-color 150ms ease;
  display: flex;
  align-items: center;
}

.switch-control.compact {
  width: 32px;
  height: 18px;
}

.switch-control span {
  display: block;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: #ffffff;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
  transition: transform 150ms ease;
}

.switch-control.compact span {
  width: 14px;
  height: 14px;
}

.switch-control.active {
  background: var(--accent);
}

.switch-control.active span {
  transform: translateX(16px);
}

.switch-control.compact.active span {
  transform: translateX(14px);
}

.switch-control.pending {
  justify-content: center;
  cursor: wait;
}

.spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }

  to {
    transform: rotate(360deg);
  }
}

/* Market Category Chips Bar */
.market-chips-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 20px;
  flex-wrap: wrap;
}

.category-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  border-radius: 20px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text-secondary);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
  transition: all 140ms ease;
}

.category-chip:hover {
  border-color: var(--accent);
  color: var(--text);
  background: var(--surface-hover);
}

.category-chip.active {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  border-color: var(--accent);
  color: var(--accent);
  font-weight: 600;
}

.chip-count {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text-secondary);
}

.category-chip.active .chip-count {
  background: color-mix(in srgb, var(--accent) 20%, transparent);
  color: var(--accent);
}

.full-market-link {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin-left: auto;
  padding: 4px 10px;
  border: none;
  background: transparent;
  color: var(--text-muted);
  font-size: 12px;
  cursor: pointer;
  border-radius: 6px;
  transition: all 140ms ease;
}

.full-market-link:hover {
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 8%, transparent);
}

.market-loading-box {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 60px 20px;
  color: var(--text-muted);
  gap: 12px;
}

.card-install-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 28px;
  padding: 0 10px;
  border-radius: 6px;
  border: 1px solid color-mix(in srgb, var(--accent) 40%, var(--border));
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  color: var(--accent);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: all 140ms ease;
}

.card-install-btn:hover:not(:disabled) {
  background: var(--accent);
  color: #ffffff;
}

.card-install-btn:disabled {
  opacity: 0.65;
  cursor: wait;
}

.author-label {
  font-size: 11px;
  color: var(--text-muted);
}

.mt-2 {
  margin-top: 8px;
}

/* Fullscreen Overlay for OfficialSkillsDialog */
.official-market-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 1000;
  background: var(--bg);
  display: flex;
  flex-direction: column;
}
</style>
