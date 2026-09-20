<script setup lang="ts">
import {
  Blocks,
  ExternalLink,
  Folder,
  FolderCog,
  GitBranch,
  Info,
  LoaderCircle,
  Power,
  RefreshCw,
  Wrench,
  X
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import type { GitOverview } from '../../../shared/ipc'
import type { ProjectItem, ThreadItem } from '../types'

interface McpServerInfo {
  name: string
  scope: 'workspace' | 'global'
  transport: 'stdio' | 'sse' | 'http' | string
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

const props = defineProps<{
  open: boolean
  project?: ProjectItem
  threads?: ThreadItem[]
}>()

const emit = defineEmits<{
  close: []
  initializeWorkspace: [project: ProjectItem]
  openExtensions: [tab: 'mcp' | 'skills']
}>()

const activeTab = ref<'general' | 'mcp' | 'skills'>('general')
const gitOverview = ref<GitOverview | null>(null)
const gitLoading = ref(false)
const mcpServers = ref<McpServerInfo[]>([])
const skills = ref<SkillInfo[]>([])
const loadingData = ref(false)
const error = ref('')

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.seekclaw.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

async function loadProjectData(): Promise<void> {
  if (!props.project) return
  error.value = ''
  loadingData.value = true

  try {
    const [mcpData, skillsData] = await Promise.all([
      requestJson<McpServerInfo[]>('mcp.list').catch(() => [] as McpServerInfo[]),
      requestJson<SkillInfo[]>('skill.list').catch(() => [] as SkillInfo[])
    ])
    mcpServers.value = mcpData
    skills.value = skillsData
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loadingData.value = false
  }

  gitLoading.value = true
  try {
    gitOverview.value = await window.seekclaw.project.gitOverview(props.project.path)
  } catch {
    gitOverview.value = null
  } finally {
    gitLoading.value = false
  }
}

watch(() => [props.open, props.project?.id] as const, ([isOpen, projectId]) => {
  if (isOpen && projectId) {
    activeTab.value = 'general'
    void loadProjectData()
  }
}, { immediate: true })

const projectMcpServers = computed(() =>
  mcpServers.value.filter((s) => s.scope === 'workspace')
)

const projectSkills = computed(() =>
  skills.value.filter((s) => s.scope === 'workspace')
)

const activeTasksCount = computed(() =>
  (props.threads ?? []).filter((t) => t.projectId === props.project?.id && !t.archived).length
)

const archivedTasksCount = computed(() =>
  (props.threads ?? []).filter((t) => t.projectId === props.project?.id && t.archived).length
)

async function toggleMcp(server: McpServerInfo): Promise<void> {
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.upsert', {
      name: server.name,
      scope: 'workspace',
      enabled: !server.enabled
    })
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  }
}

async function toggleSkill(skill: SkillInfo): Promise<void> {
  try {
    skills.value = await requestJson<SkillInfo[]>('skill.toggle', {
      name: skill.name,
      enabled: !skill.enabled
    })
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  }
}

function navigateToExtensions(tab: 'mcp' | 'skills'): void {
  emit('close')
  emit('openExtensions', tab)
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open && project" class="modal-backdrop project-properties-backdrop" @mousedown.self="emit('close')">
      <section class="project-properties-dialog" role="dialog" aria-modal="true"
        aria-labelledby="project-properties-title">
        <!-- Minimal Header -->
        <header class="properties-header">
          <div class="header-title">
            <span class="project-icon">
              <Folder :size="20" />
            </span>
            <h2 id="project-properties-title">{{ project.name }}</h2>
          </div>
          <div class="header-actions">
            <button type="button" class="icon-button" title="刷新数据" :disabled="loadingData" @click="loadProjectData">
              <RefreshCw :size="14" :class="{ spin: loadingData }" />
            </button>
            <button type="button" class="icon-button" title="关闭" @click="emit('close')">
              <X :size="16" />
            </button>
          </div>
        </header>

        <!-- Clean Underline Tabs -->
        <nav class="properties-tabs">
          <button type="button" class="tab-item" :class="{ active: activeTab === 'general' }"
            @click="activeTab = 'general'">
            <Info :size="14" />
            <span>概览属性</span>
          </button>
          <button type="button" class="tab-item" :class="{ active: activeTab === 'mcp' }" @click="activeTab = 'mcp'">
            <Blocks :size="14" />
            <span>项目MCP</span>
            <span v-if="projectMcpServers.length > 0" class="tab-badge">{{ projectMcpServers.length }}</span>
          </button>
          <button type="button" class="tab-item" :class="{ active: activeTab === 'skills' }"
            @click="activeTab = 'skills'">
            <Wrench :size="14" />
            <span>项目Skills</span>
            <span v-if="projectSkills.length > 0" class="tab-badge">{{ projectSkills.length }}</span>
          </button>
        </nav>

        <div v-if="error" class="properties-error">
          {{ error }}
        </div>

        <!-- Body Content -->
        <div class="properties-body">
          <!-- 1. Minimal Property List -->
          <div v-if="activeTab === 'general'" class="property-list">
            <!-- Row: Path -->
            <div class="property-row">
              <span class="row-label">项目路径</span>
              <div class="row-value">
                <span class="path-text" :title="project.path">{{ project.path }}</span>
              </div>
            </div>

            <!-- Row: Git -->
            <div class="property-row">
              <span class="row-label">Git 状态</span>
              <div class="row-value">
                <div v-if="gitLoading" class="inline-loading">
                  <LoaderCircle :size="13" class="spin" /> 检查中…
                </div>
                <div v-else-if="gitOverview?.isRepository" class="git-status-inline">
                  <span class="branch-pill">
                    <GitBranch :size="12" />
                    {{ gitOverview.branch }}
                  </span>
                  <span :class="gitOverview.status.length > 0 ? 'status-text warning' : 'status-text clean'">
                    {{ gitOverview.status.length > 0 ? `${gitOverview.status.length} 个未提交更改` : '工作区整洁' }}
                  </span>
                </div>
                <span v-else class="text-muted">未检测到 Git 仓库</span>
              </div>
            </div>

            <!-- Row: Tasks -->
            <div class="property-row">
              <span class="row-label">关联任务</span>
              <div class="row-value">
                <span class="tasks-inline">
                  <strong>{{ activeTasksCount }}</strong> 个活跃
                  <span class="dot-sep">·</span>
                  <span class="text-muted">{{ archivedTasksCount }} 个已归档</span>
                </span>
              </div>
            </div>

            <!-- Row: Environment -->
            <div class="property-row">
              <span class="row-label">工作区环境</span>
              <div class="row-value row-actions-between">
                <span class="text-muted">.seekclaw/ 专属配置目录</span>
                <button type="button" class="secondary-button compact" @click="emit('initializeWorkspace', project)">
                  <FolderCog :size="13" /> 初始化环境
                </button>
              </div>
            </div>
          </div>

          <!-- 2. Project MCP Tab -->
          <div v-else-if="activeTab === 'mcp'" class="tab-content">
            <div class="tab-toolbar">
              <span class="tab-desc">仅对当前项目生效的 Model Context Protocol 扩展。</span>
              <button type="button" class="secondary-button compact" @click="navigateToExtensions('mcp')">
                <ExternalLink :size="12" /> MCP 管理
              </button>
            </div>

            <div v-if="projectMcpServers.length === 0" class="empty-hint">
              暂无项目专属 MCP 服务，可在项目根目录配置 <code>mcp/servers.json</code>。
            </div>

            <div v-else class="settings-list">
              <div v-for="server in projectMcpServers" :key="server.name" class="settings-list-row">
                <div class="row-main">
                  <div class="row-title">
                    <span class="name">{{ server.name }}</span>
                    <span class="transport-tag">{{ server.transport.toUpperCase() }}</span>
                    <span class="status-indicator" :class="{
                      'is-connected': server.connected,
                      'is-error': Boolean(server.error),
                      'is-disabled': !server.enabled
                    }">
                      <span class="dot" />
                      {{ !server.enabled ? '已禁用' : server.connected ? '已连接' : server.error ? '连接异常' : '未连接' }}
                    </span>
                  </div>
                  <div class="row-subtitle">
                    <span v-if="server.command"><code>{{ server.command }} {{ server.args.join(' ') }}</code></span>
                    <span v-else-if="server.url"><code>{{ server.url }}</code></span>
                    <span v-if="server.toolCount > 0" class="text-muted">· {{ server.toolCount }} 个工具</span>
                  </div>
                  <div v-if="server.error" class="row-error">{{ server.error }}</div>
                </div>

                <button type="button" class="icon-button" :class="{ 'is-active': server.enabled }"
                  :title="server.enabled ? '禁用服务' : '启用服务'" @click="toggleMcp(server)">
                  <Power :size="15" />
                </button>
              </div>
            </div>
          </div>

          <!-- 3. Project Skills Tab -->
          <div v-else-if="activeTab === 'skills'" class="tab-content">
            <div class="tab-toolbar">
              <span class="tab-desc">仅对当前项目生效的提示词技能。</span>
              <button type="button" class="secondary-button compact" @click="navigateToExtensions('skills')">
                <ExternalLink :size="12" /> 技能管理
              </button>
            </div>

            <div v-if="projectSkills.length === 0" class="empty-hint">
              暂无项目专属技能，可在项目根目录创建 <code>skills/&lt;技能名&gt;/prompt.txt</code>。
            </div>

            <div v-else class="settings-list">
              <div v-for="skill in projectSkills" :key="skill.name" class="settings-list-row">
                <div class="row-main">
                  <div class="row-title">
                    <span class="name">{{ skill.name }}</span>
                    <span v-if="skill.version" class="version-tag">v{{ skill.version }}</span>
                  </div>
                  <p v-if="skill.description" class="row-desc">{{ skill.description }}</p>
                  <span class="row-meta" :title="skill.directory">{{ skill.directory }}</span>
                </div>

                <button type="button" class="icon-button" :class="{ 'is-active': skill.enabled }"
                  :title="skill.enabled ? '禁用技能' : '启用技能'" @click="toggleSkill(skill)">
                  <Power :size="15" />
                </button>
              </div>
            </div>
          </div>
        </div>

        <!-- Footer -->
        <footer class="properties-footer">
          <button type="button" class="secondary-button primary-action" @click="emit('close')">
            关闭
          </button>
        </footer>
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.project-properties-backdrop {
  position: fixed;
  z-index: 120;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 24px;
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(8px);
}

.project-properties-dialog {
  width: min(100%, 580px);
  max-height: 80vh;
  display: flex;
  flex-direction: column;
  background: var(--surface-raised, #ffffff);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.22), 0 2px 10px rgba(0, 0, 0, 0.08);
  overflow: hidden;
}

/* ================= Header ================= */
.properties-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 16px 20px 14px;
  background: var(--surface-raised);
}

.header-title {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.project-icon {
  display: grid;
  place-items: center;
  color: var(--accent);
}

.header-title h2 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex: none;
}

/* ================= Tabs ================= */
.properties-tabs {
  display: flex;
  gap: 18px;
  padding: 0 20px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border);
}

.tab-item {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 8px 2px 10px;
  font-size: 13px;
  font-weight: 500;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  cursor: pointer;
  transition: color 140ms ease;
}

.tab-item:hover {
  color: var(--text);
}

.tab-item.active {
  color: var(--accent);
  font-weight: 600;
}

.tab-item.active::after {
  content: '';
  position: absolute;
  bottom: -1px;
  left: 0;
  right: 0;
  height: 2px;
  background: var(--accent);
  border-radius: 2px 2px 0 0;
}

.tab-badge {
  display: inline-block;
  padding: 0 5px;
  font-size: 10px;
  font-weight: 600;
  background: var(--accent-soft);
  color: var(--accent);
  border-radius: 999px;
  line-height: 1.4;
}

/* ================= Body ================= */
.properties-body {
  flex: 1;
  overflow-y: auto;
  padding: 16px 20px;
  background: var(--surface-raised);
}

.properties-error {
  margin: 10px 20px 0;
  padding: 8px 12px;
  font-size: 12px;
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 12%, transparent);
  border-radius: 6px;
}

/* ================= Minimal Property Rows ================= */
.property-list {
  display: flex;
  flex-direction: column;
}

.property-row {
  display: flex;
  align-items: center;
  min-height: 48px;
  padding: 8px 0;
  border-bottom: 1px solid var(--border);
}

.property-row:last-child {
  border-bottom: none;
}

.row-label {
  width: 96px;
  flex: none;
  font-size: 13px;
  font-weight: 500;
  color: var(--text-muted);
}

.row-value {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  font-size: 13px;
  color: var(--text);
}

.row-actions-between {
  justify-content: space-between;
  gap: 12px;
}

.path-value {
  justify-content: space-between;
  gap: 10px;
}

.path-text {
  font-family: var(--font-mono, monospace);
  font-size: 12.5px;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Git inline */
.git-status-inline {
  display: flex;
  align-items: center;
  gap: 10px;
}

.branch-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 7px;
  font-size: 11.5px;
  font-weight: 550;
  border-radius: 4px;
  background: var(--accent-soft);
  color: var(--accent);
}

.status-text {
  font-size: 12.5px;
}

.status-text.clean {
  color: #10b981;
}

.status-text.warning {
  color: #f59e0b;
}

.inline-loading {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-muted);
}

.text-muted {
  color: var(--text-muted);
  font-size: 12.5px;
}

/* Tasks inline */
.tasks-inline {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 13px;
}

.tasks-inline strong {
  font-weight: 600;
  color: var(--text);
}

.dot-sep {
  margin: 0 4px;
  color: var(--text-muted);
}

/* ================= Tab Content (MCP / Skills) ================= */
.tab-content {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.tab-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.tab-desc {
  font-size: 12px;
  color: var(--text-muted);
}

.empty-hint {
  padding: 24px 12px;
  text-align: center;
  font-size: 12.5px;
  color: var(--text-muted);
  background: var(--surface-hover);
  border-radius: 8px;
}

.empty-hint code {
  font-family: var(--font-mono, monospace);
  font-size: 11.5px;
  background: var(--surface-raised);
  padding: 1px 4px;
  border-radius: 4px;
}

.settings-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.settings-list-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  background: var(--surface-hover);
  border-radius: 8px;
}

.row-main {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  flex: 1;
}

.row-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.row-title .name {
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.transport-tag,
.version-tag {
  font-size: 10px;
  font-weight: 600;
  padding: 1px 5px;
  border-radius: 3px;
  background: var(--surface-raised);
  color: var(--text-secondary);
}

.status-indicator {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--text-muted);
}

.status-indicator .dot {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--text-muted);
}

.status-indicator.is-connected {
  color: #10b981;
}

.status-indicator.is-connected .dot {
  background: #10b981;
}

.status-indicator.is-error {
  color: var(--danger);
}

.status-indicator.is-error .dot {
  background: var(--danger);
}

.row-subtitle {
  font-size: 11.5px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-subtitle code {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
}

.row-desc {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.row-meta {
  font-size: 11px;
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-error {
  font-size: 11px;
  color: var(--danger);
}

/* ================= Footer ================= */
.properties-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 10px 20px;
  background: var(--surface-raised);
  border-top: 1px solid var(--border);
}
</style>
