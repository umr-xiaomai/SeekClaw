<script setup lang="ts">
import {
  ArrowLeft,
  CalendarClock,
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

const tasks = ref<ScheduledTaskInfo[]>([])
const loading = ref(false)
let unsubscribeDaemonEvent: (() => void) | undefined
const error = ref('')
const notice = ref('')
const action = ref('')
const editingId = ref<string | null>(null)
const editorOpen = ref(false)
const customCron = ref(false)

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
    const task = await requestJson<ScheduledTaskInfo>(editingId.value ? 'schedule.update' : 'schedule.create', {
      id: editingId.value ?? undefined,
      name: form.name.trim(),
      workspace: form.workspace || undefined,
      prompt: form.prompt.trim(),
      cron: form.cron.trim(),
      enabled: form.enabled
    })
    editorOpen.value = false
    notice.value = '已保存计划任务'
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
    await requestJson<ScheduledTaskInfo>('schedule.toggle', { id: task.id, enabled: !task.enabled })
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
  <section
    v-if="open"
    class="scheduled-tasks-dialog embedded-page"
    role="region"
    aria-label="计划任务"
  >
    <header class="scheduled-tasks-header">
      <div class="scheduled-tasks-heading">
        <button class="page-back-button" type="button" @click="emit('close')">
          <ArrowLeft :size="18" />
          <span>返回应用</span>
        </button>
        <div>
          <h2>计划任务</h2>
          <span class="scheduled-tasks-subtitle">按 Cron 定时执行智能体任务</span>
        </div>
      </div>
      <div class="scheduled-tasks-actions">
        <button class="secondary-button" :disabled="loading" @click="newTask"><Plus :size="15" /> 新建</button>
      </div>
    </header>

          <div class="scheduled-tasks-body">
            <div v-if="notice" class="scheduled-tasks-notice">{{ notice }}</div>
            <div v-if="error" class="scheduled-tasks-error">{{ error }}</div>

            <div v-if="loading" class="scheduled-tasks-loading"><LoaderCircle class="spin" :size="18" /> 正在加载</div>

            <template v-else-if="!editorOpen">
              <div v-if="tasks.length === 0" class="scheduled-tasks-empty">
                <CalendarClock :size="28" />
                <p>还没有计划任务</p>
                <small>创建后由守护进程在后台按时自动执行</small>
              </div>
              <div v-else class="scheduled-task-list">
                <div v-for="task in tasks" :key="task.id" class="scheduled-task-row">
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
                      <input
                        class="sr-only"
                        type="checkbox"
                        :checked="task.enabled"
                        :disabled="action === `schedule.toggle:${task.id}`"
                        @change="toggleTask(task)"
                      />
                      <span class="toggle-switch" aria-hidden="true"><span /></span>
                    </label>
                    <button class="icon-button compact" title="立即运行" :disabled="action === `schedule.run:${task.id}`" @click="runTask(task)">
                      <LoaderCircle v-if="action === `schedule.run:${task.id}`" class="spin" :size="14" />
                      <Play v-else :size="14" />
                    </button>
                    <button class="icon-button compact" title="编辑" @click="editTask(task)"><Pencil :size="14" /></button>
                    <button class="icon-button compact danger-icon" title="删除" @click="removeTask(task)"><Trash2 :size="14" /></button>
                  </div>
                </div>
              </div>
            </template>

            <form v-else class="scheduled-task-editor" @submit.prevent="saveTask">
              <div class="scheduled-editor-heading">
                <strong>{{ editingId ? '编辑计划任务' : '新建计划任务' }}</strong>
                <button type="button" class="icon-button compact" @click="editorOpen = false"><X :size="15" /></button>
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
                    <input v-if="customCron" v-model="form.cron" class="scheduled-cron-input" placeholder="分 时 日 月 周，如 0 9 * * 1" spellcheck="false" />
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
  </section>
</template>

<style scoped>
.scheduled-tasks-dialog.embedded-page {
  width: 100%;
  max-height: none;
  height: 100%;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: var(--bg);
}

.scheduled-tasks-dialog {
  display: flex;
  flex-direction: column;
  width: min(760px, calc(100vw - 48px));
  max-height: min(720px, calc(100vh - 64px));
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 16px;
  box-shadow: 0 24px 64px rgb(0 0 0 / 28%);
}

.scheduled-tasks-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 62px;
  padding: 10px 22px;
  border-bottom: 1px solid var(--border);
}

.scheduled-tasks-heading {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 12px;
}

.scheduled-tasks-header h2 {
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  letter-spacing: -.01em;
}

.scheduled-tasks-subtitle {
  display: block;
  margin-top: 2px;
  color: var(--text-muted);
  font-size: 11px;
}

.scheduled-tasks-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.scheduled-tasks-body {
  flex: 1;
  overflow-y: auto;
  padding: 18px 22px 22px;
}

.scheduled-tasks-loading,
.scheduled-tasks-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 48px 0;
  color: var(--text-muted);
}

.scheduled-tasks-empty p {
  margin: 6px 0 0;
  font-weight: 600;
  color: var(--text);
}

.scheduled-tasks-empty small {
  font-size: 11px;
}

.scheduled-tasks-notice {
  margin-bottom: 10px;
  padding: 8px 10px;
  color: var(--accent);
  font-size: 12px;
  background: color-mix(in srgb, var(--accent) 9%, transparent);
  border-radius: 7px;
}

.scheduled-tasks-error {
  margin-bottom: 10px;
  padding: 8px 10px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border-radius: 7px;
  white-space: pre-wrap;
}

.scheduled-task-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.scheduled-task-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 11px 12px;
  background: color-mix(in srgb, var(--surface-hover) 55%, transparent);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.scheduled-task-main {
  min-width: 0;
}

.scheduled-task-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.scheduled-task-cron {
  padding: 1px 6px;
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  background: var(--surface-hover);
  border-radius: 5px;
}

.scheduled-task-meta {
  display: block;
  margin-top: 3px;
  color: var(--text-muted);
  font-size: 11px;
}

.scheduled-task-prompt {
  display: -webkit-box;
  margin: 6px 0 0;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 12px;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.scheduled-task-controls {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 6px;
}

.scheduled-task-status {
  margin-right: 4px;
  padding: 2px 7px;
  font-size: 11px;
  border-radius: 999px;
}

.scheduled-task-status.status-success {
  color: var(--success);
  background: color-mix(in srgb, var(--success) 12%, transparent);
}

.scheduled-task-status.status-error {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 12%, transparent);
}

.scheduled-task-status.status-idle {
  color: var(--text-muted);
  background: var(--surface-hover);
}

.scheduled-task-toggle {
  display: inline-flex;
  align-items: center;
  cursor: pointer;
}

.scheduled-task-toggle .toggle-switch {
  width: 34px;
  height: 20px;
}

.scheduled-task-toggle .toggle-switch > span {
  width: 16px;
  height: 16px;
}

.scheduled-task-toggle input:checked + .toggle-switch {
  background: var(--accent);
}

.scheduled-task-toggle input:checked + .toggle-switch > span {
  transform: translateX(14px);
}

.scheduled-task-editor {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.scheduled-editor-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.scheduled-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.scheduled-form-grid label {
  display: flex;
  flex-direction: column;
  gap: 5px;
  font-size: 12px;
  color: var(--text-secondary);
}

.scheduled-form-grid input,
.scheduled-form-grid textarea {
  width: 100%;
  padding: 8px 10px;
  color: var(--text);
  font-size: 13px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
}

.scheduled-form-grid textarea {
  resize: vertical;
}

.scheduled-cron-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.scheduled-cron-input {
  flex: 1;
  font-family: var(--font-mono, monospace);
}

.scheduled-enabled-row {
  display: flex;
  flex-direction: row !important;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px;
  background: color-mix(in srgb, var(--surface-hover) 55%, transparent);
  border-radius: 8px;
  cursor: pointer;
}

.scheduled-enabled-row small {
  color: var(--text-muted);
  font-size: 11px;
}

/* The editor toggle mirrors the list-row switch so toggling gives visible feedback. */
.scheduled-enabled-row input:checked + .toggle-switch {
  background: var(--accent);
}

.scheduled-enabled-row input:checked + .toggle-switch > span {
  transform: translateX(16px);
}

.scheduled-enabled-row input:focus-visible + .toggle-switch {
  outline: 2px solid color-mix(in srgb, var(--accent) 58%, transparent);
  outline-offset: 2px;
}

.span-2 {
  grid-column: span 2;
}

.scheduled-editor-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 4px;
}

.scheduled-editor-hint {
  margin-right: auto;
  color: var(--text-muted);
  font-size: 11px;
}
</style>
