<script setup lang="ts">
import {
  ArrowLeft,
  CalendarClock,
  Clock,
  LoaderCircle,
  Pencil,
  Play,
  Plus,
  Power,
  Save,
  Trash2,
  X
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type { ProjectItem, ScheduledTaskInfo } from '../types'
import SelectMenu from './SelectMenu.vue'
import TwoPaneLayout from './TwoPaneLayout.vue'

const props = defineProps<{
  open: boolean
  projects: ProjectItem[]
}>()

const emit = defineEmits<{ close: [] }>()

interface ScheduleForm {
  name: string
  workspace: string
  prompt: string
  cron: string
  enabled: boolean
}

type ScheduleFilter = 'all' | 'enabled' | 'disabled'

const tasks = ref<ScheduledTaskInfo[]>([])
const loading = ref(false)
let unsubscribeDaemonEvent: (() => void) | undefined
const error = ref('')
const notice = ref('')
const action = ref('')
const editingId = ref<string | null>(null)
const editorOpen = ref(false)
const customCron = ref(false)
const statusFilter = ref<ScheduleFilter>('all')

const cronPresets = [
  { value: '*/30 * * * *', label: '每 30 分钟', description: '每 30 分钟' },
  { value: '0 * * * *', label: '每小时', description: '每小时' },
  { value: '0 9 * * *', label: '每天 09:00', description: '每天 09:00' },
  { value: '0 18 * * *', label: '每天 18:00', description: '每天 18:00' },
  { value: '0 9 * * 1', label: '每周一 09:00', description: '每周一 09:00' },
  { value: '__custom__', label: '自定义 Cron', description: '自定义 5 段表达式' }
]

const form = reactive<ScheduleForm>({ name: '', workspace: '', prompt: '', cron: '0 9 * * *', enabled: true })

const workspaceOptions = computed(() => [
  { value: '', label: '不绑定项目', description: '无固定工作目录' },
  ...props.projects.map((project) => ({ value: project.path, label: project.name, description: project.path }))
])

const selectedPreset = computed({
  get: () => cronPresets.some((preset) => preset.value === form.cron) ? form.cron : '__custom__',
  set: (value: string) => {
    customCron.value = value === '__custom__'
    if (value !== '__custom__') form.cron = value
  }
})

const filteredTasks = computed(() => {
  if (statusFilter.value === 'enabled') return tasks.value.filter((t) => t.enabled)
  if (statusFilter.value === 'disabled') return tasks.value.filter((t) => !t.enabled)
  return tasks.value
})

const enabledCount = computed(() => tasks.value.filter((t) => t.enabled).length)
const disabledCount = computed(() => tasks.value.filter((t) => !t.enabled).length)

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

function formatTime(value?: string): string {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '—'
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function statusLabel(task: ScheduledTaskInfo): string {
  switch (task.lastStatus) {
    case 'success': return '上次成功'
    case 'error': return '上次失败'
    case 'cancelled': return '已取消'
    case 'skipped': return '已跳过'
    default: return '尚未运行'
  }
}

function statusClass(task: ScheduledTaskInfo): string {
  return task.lastStatus === 'success' ? 'status-success' : task.lastStatus === 'error' ? 'status-error' : 'status-idle'
}

function taskWorkspaceName(task: ScheduledTaskInfo): string {
  if (!task.workspace) return '不绑定项目'
  const project = props.projects.find((item) => item.path === task.workspace)
  return project?.name ?? task.workspace
}

async function loadTasks(): Promise<void> {
  if (!props.open) return
  loading.value = true
  error.value = ''
  try {
    tasks.value = await requestJson<ScheduledTaskInfo[]>('schedule.list')
  } catch (reason) {
    fail(reason)
  } finally {
    loading.value = false
  }
}

function newTask(): void {
  error.value = ''
  editingId.value = null
  Object.assign(form, { name: '', workspace: '', prompt: '', cron: '0 9 * * *', enabled: true })
  customCron.value = false
  editorOpen.value = true
}

function editTask(task: ScheduledTaskInfo): void {
  error.value = ''
  editingId.value = task.id
  Object.assign(form, {
    name: task.name,
    workspace: task.workspace ?? '',
    prompt: task.prompt,
    cron: task.cron,
    enabled: task.enabled
  })
  customCron.value = !cronPresets.some((preset) => preset.value === task.cron)
  editorOpen.value = true
}

async function saveTask(): Promise<void> {
  if (!form.name.trim() || !form.prompt.trim()) {
    error.value = '请填写任务名称和提示词'
    return
  }
  beginAction('schedule.save')
  try {
    if (editingId.value) {
      await window.seekclaw.daemon.request('schedule.update', {
        id: editingId.value,
        name: form.name.trim(),
        workspace: form.workspace || undefined,
        prompt: form.prompt.trim(),
        cron: form.cron.trim(),
        enabled: form.enabled
      })
      notice.value = `已更新「${form.name}」`
    } else {
      await window.seekclaw.daemon.request('schedule.create', {
        name: form.name.trim(),
        workspace: form.workspace || undefined,
        prompt: form.prompt.trim(),
        cron: form.cron.trim(),
        enabled: form.enabled
      })
      notice.value = `已创建「${form.name}」`
    }
    editorOpen.value = false
    await loadTasks()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function toggleTask(task: ScheduledTaskInfo): Promise<void> {
  beginAction(`schedule.toggle:${task.id}`)
  try {
    await window.seekclaw.daemon.request('schedule.toggle', { id: task.id, enabled: !task.enabled })
    await loadTasks()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function runTask(task: ScheduledTaskInfo): Promise<void> {
  beginAction(`schedule.run:${task.id}`)
  try {
    await window.seekclaw.daemon.request('schedule.run', { id: task.id })
    notice.value = `已触发「${task.name}」`
    await loadTasks()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function removeTask(task: ScheduledTaskInfo): Promise<void> {
  beginAction(`schedule.delete:${task.id}`)
  try {
    await window.seekclaw.daemon.request('schedule.delete', { id: task.id })
    notice.value = `已删除「${task.name}」`
    if (editingId.value === task.id) editorOpen.value = false
    await loadTasks()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

function closeOnEscape(event: KeyboardEvent): void {
  if (props.open && event.key === 'Escape') emit('close')
}

watch(() => props.open, (open) => {
  if (open) {
    error.value = ''
    notice.value = ''
    editorOpen.value = false
    statusFilter.value = 'all'
    void loadTasks()
  }
})

onMounted(() => {
  document.addEventListener('keydown', closeOnEscape)
  unsubscribeDaemonEvent = window.seekclaw.daemon.onEvent((message) => {
    if (message.event === 'schedule.updated') void loadTasks()
  })
})

onBeforeUnmount(() => {
  document.removeEventListener('keydown', closeOnEscape)
  unsubscribeDaemonEvent?.()
})
</script>

<template>
  <div v-if="open" class="scheduled-tasks-workbench embedded-page" role="region" aria-label="计划任务">
    <TwoPaneLayout
      storage-key="seekclaw-scheduled-sidebar-width"
      :default-width="260"
      :min-width="200"
      :max-width="480"
      :can-collapse="false"
      aria-label="计划任务导航"
    >
      <template #sidebar>
        <div class="scheduled-nav">
          <div class="scheduled-nav-header">
            <button class="page-back-button" type="button" title="返回应用" @click="emit('close')">
              <ArrowLeft :size="16" />
              <span>返回应用</span>
            </button>
          </div>

          <div class="scheduled-nav-group-title">
            计划任务
          </div>

          <div class="scheduled-nav-action-row">
            <button class="scheduled-create-btn" :disabled="loading" @click="newTask">
              <Plus :size="16" />
              <span>新建计划任务</span>
            </button>
          </div>

          <div class="scheduled-nav-list">
            <button
              class="scheduled-nav-item"
              :class="{ active: statusFilter === 'all' }"
              @click="statusFilter = 'all'"
            >
              <CalendarClock :size="16" />
              <span class="nav-item-title">全部任务</span>
              <span class="nav-item-badge">{{ tasks.length }}</span>
            </button>

            <button
              class="scheduled-nav-item"
              :class="{ active: statusFilter === 'enabled' }"
              @click="statusFilter = 'enabled'"
            >
              <Clock :size="16" />
              <span class="nav-item-title">运行中</span>
              <span class="nav-item-badge">{{ enabledCount }}</span>
            </button>

            <button
              class="scheduled-nav-item"
              :class="{ active: statusFilter === 'disabled' }"
              @click="statusFilter = 'disabled'"
            >
              <Power :size="16" />
              <span class="nav-item-title">已暂停</span>
              <span class="nav-item-badge">{{ disabledCount }}</span>
            </button>
          </div>

          <template v-if="filteredTasks.length > 0">
            <div class="scheduled-nav-section-label">
              快捷列表
            </div>

            <div class="scheduled-quick-list">
              <button
                v-for="task in filteredTasks"
                :key="task.id"
                class="scheduled-quick-item"
                :class="{ active: editorOpen && editingId === task.id }"
                @click="editTask(task)"
              >
                <span class="quick-status-dot" :class="{ enabled: task.enabled }" />
                <span class="quick-task-name" :title="task.name">{{ task.name }}</span>
                <span class="quick-task-cron">{{ task.cron }}</span>
              </button>
            </div>
          </template>
        </div>
      </template>

      <div class="scheduled-main">
        <header class="scheduled-content-header">
          <div class="scheduled-content-title">
            <h3>{{ editorOpen ? (editingId ? '编辑计划任务' : '新建计划任务') : '计划任务列表' }}</h3>
            <span v-if="!editorOpen" class="scheduled-count-tag">{{ filteredTasks.length }} 个任务</span>
          </div>

          <div class="scheduled-header-actions">
            <button v-if="!editorOpen" class="secondary-button compact" :disabled="loading" @click="newTask">
              <Plus :size="14" /> 新建任务
            </button>
          </div>
        </header>

        <div class="scheduled-main-scroll">
          <div v-if="notice" class="scheduled-tasks-notice">{{ notice }}</div>
          <div v-if="error" class="scheduled-tasks-error">{{ error }}</div>

          <div v-if="loading" class="scheduled-tasks-loading">
            <LoaderCircle class="spin" :size="20" /> 正在加载
          </div>

          <template v-else-if="!editorOpen">
            <div v-if="tasks.length === 0" class="scheduled-tasks-empty">
              <CalendarClock :size="32" />
              <p>还没有计划任务</p>
              <small>创建后由运行时在后台按时自动执行</small>
              <button class="secondary-button" style="margin-top: 12px;" @click="newTask">
                <Plus :size="15" /> 创建第一个任务
              </button>
            </div>

            <div v-else-if="filteredTasks.length === 0" class="scheduled-tasks-empty">
              <CalendarClock :size="32" />
              <p>没有符合筛选条件的任务</p>
            </div>

            <div v-else class="scheduled-task-list">
              <div v-for="task in filteredTasks" :key="task.id" class="scheduled-task-row">
                <div class="scheduled-task-main">
                  <div class="scheduled-task-title">
                    <strong>{{ task.name }}</strong>
                    <span class="scheduled-task-cron">{{ task.cron }}</span>
                  </div>
                  <small class="scheduled-task-meta">
                    {{ taskWorkspaceName(task) }} · 下次运行 {{ formatTime(task.nextRunAt) }} · {{ statusLabel(task) }}
                  </small>
                  <p v-if="task.prompt" class="scheduled-task-prompt" :title="task.prompt">{{ task.prompt }}</p>
                </div>
                <div class="scheduled-task-controls">
                  <span class="scheduled-task-status" :class="statusClass(task)">{{ statusLabel(task) }}</span>
                  <label class="scheduled-task-toggle" :title="task.enabled ? '暂停' : '启用'">
                    <input class="sr-only" type="checkbox" :checked="task.enabled"
                      :disabled="action === `schedule.toggle:${task.id}`" @change="toggleTask(task)" />
                    <span class="toggle-switch" aria-hidden="true"><span /></span>
                  </label>
                  <button class="icon-button compact" title="立即运行" :disabled="action === `schedule.run:${task.id}`"
                    @click="runTask(task)">
                    <LoaderCircle v-if="action === `schedule.run:${task.id}`" class="spin" :size="14" />
                    <Play v-else :size="14" />
                  </button>
                  <button class="icon-button compact" title="编辑" @click="editTask(task)">
                    <Pencil :size="14" />
                  </button>
                  <button class="icon-button compact danger-icon" title="删除" @click="removeTask(task)">
                    <Trash2 :size="14" />
                  </button>
                </div>
              </div>
            </div>
          </template>

          <form v-else class="scheduled-task-editor" @submit.prevent="saveTask">
            <div class="scheduled-editor-heading">
              <strong>{{ editingId ? '编辑计划任务' : '新建计划任务' }}</strong>
              <button type="button" class="icon-button compact" @click="editorOpen = false">
                <X :size="15" />
              </button>
            </div>
            <div class="scheduled-form-grid">
              <label class="span-2">
                <span>任务名称</span>
                <input v-model="form.name" placeholder="例如：每日代码检查" autocomplete="off" />
              </label>
              <label class="span-2">
                <span>提示词</span>
                <textarea v-model="form.prompt" rows="4" placeholder="例如：检查当前项目是否有未提交的改动并生成日报" />
              </label>
              <label class="span-2">
                <span>执行位置</span>
                <SelectMenu v-model="form.workspace" :options="workspaceOptions" label="执行位置" :menu-min-width="300" />
              </label>
              <label class="span-2">
                <span>频率</span>
                <div class="scheduled-cron-row">
                  <SelectMenu v-model="selectedPreset" :options="cronPresets" label="频率" :menu-min-width="260" />
                  <input v-if="customCron" v-model="form.cron" class="scheduled-cron-input"
                    placeholder="分 时 日 月 周，如 0 9 * * 1" spellcheck="false" />
                </div>
              </label>
              <label class="scheduled-enabled-row span-2">
                <span><strong>启用</strong><small>关闭后保留任务但不再自动执行</small></span>
                <input v-model="form.enabled" class="sr-only" type="checkbox" />
                <span class="toggle-switch" aria-hidden="true"><span /></span>
              </label>
            </div>
            <div class="scheduled-editor-actions">
              <span class="scheduled-editor-hint">5 段 Cron：分 时 日 月 周（本地时区）</span>
              <button type="button" class="secondary-button" @click="editorOpen = false">取消</button>
              <button type="submit" class="secondary-button primary-action" :disabled="action === 'schedule.save'">
                <LoaderCircle v-if="action === 'schedule.save'" class="spin" :size="15" />
                <Save v-else :size="15" /> 保存
              </button>
            </div>
          </form>
        </div>
      </div>
    </TwoPaneLayout>
  </div>
</template>

<style scoped>
.scheduled-tasks-workbench.embedded-page {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: transparent;
  overflow: hidden;
}

.scheduled-nav {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: 10px 12px 14px;
  overflow-y: auto;
  user-select: none;
  box-sizing: border-box;
}

.scheduled-nav-header {
  margin-bottom: 8px;
}

.scheduled-nav .page-back-button {
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
  box-sizing: border-box;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.scheduled-nav .page-back-button:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 76%, transparent);
}

.scheduled-nav-group-title {
  padding: 8px 10px 6px;
  color: var(--text-muted);
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.scheduled-nav-action-row {
  margin: 4px 0 10px;
}

.scheduled-create-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  width: 100%;
  height: 36px;
  padding: 0 12px;
  color: var(--text);
  font-size: 13px;
  font-weight: 550;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: 0 1px 2px rgb(0 0 0 / 4%);
  cursor: pointer;
  transition: background-color 140ms ease, border-color 140ms ease;
}

.scheduled-create-btn:hover {
  background: var(--surface-hover);
  border-color: var(--border-strong);
}

.scheduled-nav-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.scheduled-nav-item {
  display: flex;
  align-items: center;
  width: 100%;
  min-height: 36px;
  gap: 10px;
  padding: 0 10px;
  color: var(--text-secondary);
  font-size: 13.5px;
  font-weight: 450;
  text-align: left;
  background: transparent;
  border: none;
  border-radius: 8px;
  box-sizing: border-box;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.scheduled-nav-item:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 75%, transparent);
}

.scheduled-nav-item.active {
  color: var(--text);
  background: var(--surface-raised);
  box-shadow: 0 1px 2px rgb(0 0 0 / 4%);
}

.nav-item-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.nav-item-badge {
  flex: 0 0 auto;
  padding: 2px 7px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 550;
}

.scheduled-nav-section-label {
  padding: 14px 10px 4px;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.scheduled-quick-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
  overflow-y: auto;
  flex: 1 1 auto;
}

.scheduled-quick-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 32px;
  padding: 4px 10px;
  color: var(--text-secondary);
  font-size: 12.5px;
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  transition: background-color 140ms ease, color 140ms ease;
}

.scheduled-quick-item:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 70%, transparent);
}

.scheduled-quick-item.active {
  color: var(--text);
  background: var(--surface-raised);
  box-shadow: 0 1px 2px rgb(0 0 0 / 4%);
}

.quick-status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--text-muted);
  flex-shrink: 0;
}

.quick-status-dot.enabled {
  background: #10b981;
}

.quick-task-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.quick-task-cron {
  font-size: 11px;
  color: var(--text-muted);
  font-family: monospace;
}

.scheduled-main {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: var(--surface);
}

.scheduled-content-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 28px;
  border-bottom: 1px solid var(--border);
}

.scheduled-content-title {
  display: flex;
  align-items: center;
  gap: 12px;
}

.scheduled-content-title h3 {
  margin: 0;
  font-size: 17px;
  font-weight: 650;
  letter-spacing: -0.01em;
}

.scheduled-count-tag {
  padding: 3px 8px;
  border-radius: 12px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text-secondary);
  font-size: 12px;
}

.scheduled-header-actions .compact {
  height: 32px;
  padding: 0 12px;
  font-size: 13px;
}

.scheduled-main-scroll {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  padding: 20px 28px 40px;
}

.scheduled-tasks-notice,
.scheduled-tasks-error {
  padding: 10px 14px;
  margin-bottom: 16px;
  border-radius: 8px;
  font-size: 13px;
}

.scheduled-tasks-notice {
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  color: var(--accent);
  border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
}

.scheduled-tasks-error {
  background: color-mix(in srgb, var(--danger) 12%, transparent);
  color: var(--danger);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
}

.scheduled-tasks-loading,
.scheduled-tasks-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 60px 0;
  color: var(--text-muted);
}

.scheduled-task-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.scheduled-task-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  transition: border-color 140ms ease;
}

.scheduled-task-row:hover {
  border-color: var(--border-strong);
}

.scheduled-task-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.scheduled-task-title {
  display: flex;
  align-items: center;
  gap: 10px;
}

.scheduled-task-title strong {
  font-size: 15px;
  color: var(--text);
}

.scheduled-task-cron {
  padding: 2px 7px;
  background: var(--surface-hover);
  border: 1px solid var(--border);
  border-radius: 5px;
  font-family: monospace;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.scheduled-task-meta {
  color: var(--text-muted);
  font-size: 12px;
}

.scheduled-task-prompt {
  margin: 4px 0 0;
  color: var(--text-secondary);
  font-size: 13px;
  line-height: 1.45;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.scheduled-task-controls {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.scheduled-task-status {
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 11.5px;
}

.status-success {
  background: color-mix(in srgb, #10b981 14%, transparent);
  color: #10b981;
}

.status-error {
  background: color-mix(in srgb, var(--danger) 14%, transparent);
  color: var(--danger);
}

.status-idle {
  background: color-mix(in srgb, var(--text-muted) 14%, transparent);
  color: var(--text-muted);
}

.danger-icon:hover {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 10%, transparent);
}

/* Editor Form */
.scheduled-task-editor {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 20px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.scheduled-editor-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--border);
  font-size: 15px;
  color: var(--text);
}

.scheduled-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 14px;
}

.scheduled-form-grid .span-2 {
  grid-column: span 2;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.scheduled-form-grid span {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.scheduled-form-grid input,
.scheduled-form-grid textarea {
  padding: 8px 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 7px;
  color: var(--text);
  font-size: 13.5px;
  outline: none;
  transition: border-color 140ms ease;
}

.scheduled-form-grid input:focus,
.scheduled-form-grid textarea:focus {
  border-color: var(--accent);
}

.scheduled-cron-row {
  display: flex;
  gap: 10px;
  align-items: center;
}

.scheduled-cron-input {
  flex: 1;
  font-family: monospace;
}

.scheduled-enabled-row {
  display: flex;
  flex-direction: row !important;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px;
  background: color-mix(in srgb, var(--surface-hover) 40%, transparent);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.scheduled-enabled-row span {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.scheduled-enabled-row small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.scheduled-editor-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 10px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.scheduled-editor-hint {
  margin-right: auto;
  color: var(--text-muted);
  font-size: 12px;
}

.primary-action {
  background: var(--accent);
  color: #fff;
  border-color: var(--accent);
}

.primary-action:hover {
  filter: brightness(1.08);
}
</style>
