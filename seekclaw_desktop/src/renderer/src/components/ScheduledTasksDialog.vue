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
import type { ProjectItem, ScheduledTaskInfo, ThreadItem } from '../types'
import SelectMenu from './SelectMenu.vue'
import TwoPaneLayout from './TwoPaneLayout.vue'

const props = defineProps<{
  open: boolean
  projects: ProjectItem[]
  threads?: ThreadItem[]
}>()

const emit = defineEmits<{ close: [] }>()

interface ScheduleForm {
  name: string
  workspace: string
  prompt: string
  cron: string
  enabled: boolean
  sessionId: string
  runInWorktree: boolean
  notificationPolicy: 'changes' | 'failures' | 'always'
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

const defaults = (): ScheduleForm => ({ name: '', workspace: '', prompt: '', cron: '0 9 * * *', enabled: true, sessionId: '', runInWorktree: false, notificationPolicy: 'changes' })
const form = reactive<ScheduleForm>(defaults())
const notificationOptions = [
  { value: 'changes', label: '有变化时通知', description: '无变化时保持安静' },
  { value: 'failures', label: '仅失败时通知', description: '成功时保持安静' },
  { value: 'always', label: '每次通知', description: '报告每次运行结果' }
]

const workspaceOptions = computed(() => [
  { value: '', label: '不绑定项目', description: '无固定工作目录' },
  ...props.projects.map((project) => ({ value: project.path, label: project.name, description: project.path }))
])

const conversationOptions = computed(() => {
  const project = props.projects.find((item) => item.path === form.workspace)
  const matches = (props.threads ?? []).filter((thread) => thread.sessionId && !thread.archived
    && (form.workspace ? thread.projectId === project?.id : !thread.projectId))
  const options = [
    { value: '', label: '每次创建新对话', description: '独立执行' },
    ...matches.map((thread) => ({ value: thread.sessionId!, label: thread.title, description: '延续此对话的上下文' }))
  ]
  if (form.sessionId && !options.some((option) => option.value === form.sessionId))
    options.push({ value: form.sessionId, label: '已绑定的对话', description: form.sessionId })
  return options
})

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
  Object.assign(form, defaults())
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
    enabled: task.enabled,
    sessionId: task.sessionId ?? '',
    runInWorktree: task.runInWorktree ?? false,
    notificationPolicy: task.notificationPolicy ?? 'always'
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
        enabled: form.enabled,
        sessionId: form.sessionId.trim() || null,
        runInWorktree: form.runInWorktree,
        notificationPolicy: form.notificationPolicy
      })
      notice.value = `已更新「${form.name}」`
    } else {
      await window.seekclaw.daemon.request('schedule.create', {
        name: form.name.trim(),
        workspace: form.workspace || undefined,
        prompt: form.prompt.trim(),
        cron: form.cron.trim(),
        enabled: form.enabled,
        sessionId: form.sessionId.trim() || null,
        runInWorktree: form.runInWorktree,
        notificationPolicy: form.notificationPolicy
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
    if (message.event === 'schedule.updated' || message.event === 'schedule.changed') void loadTasks()
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
            <button class="page-back-button" type="button" title="返回应用" aria-label="返回应用" @click="emit('close')">
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
              :class="{ active: statusFilter === 'all' }" :aria-pressed="statusFilter === 'all'"
              @click="statusFilter = 'all'"
            >
              <CalendarClock :size="16" />
              <span class="nav-item-title">全部任务</span>
              <span class="nav-item-badge">{{ tasks.length }}</span>
            </button>

            <button
              class="scheduled-nav-item"
              :class="{ active: statusFilter === 'enabled' }" :aria-pressed="statusFilter === 'enabled'"
              @click="statusFilter = 'enabled'"
            >
              <Clock :size="16" />
              <span class="nav-item-title">运行中</span>
              <span class="nav-item-badge">{{ enabledCount }}</span>
            </button>

            <button
              class="scheduled-nav-item"
              :class="{ active: statusFilter === 'disabled' }" :aria-pressed="statusFilter === 'disabled'"
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
          <div v-if="notice" class="scheduled-tasks-notice" role="status">{{ notice }}</div>
          <div v-if="error" class="scheduled-tasks-error" role="alert">{{ error }}</div>

          <div v-if="loading" class="scheduled-tasks-loading" role="status">
            <LoaderCircle class="spin" :size="20" /> 正在加载
          </div>

          <template v-else-if="!editorOpen">
            <div v-if="tasks.length === 0" class="scheduled-tasks-empty">
              <CalendarClock :size="32" />
              <p>还没有计划任务</p>
              <small>创建后由运行时在后台按时自动执行</small>
              <button class="secondary-button" @click="newTask">
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
                    <strong :title="task.name">{{ task.name }}</strong>
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
                    <input class="sr-only" type="checkbox" :checked="task.enabled" :aria-label="`${task.enabled ? '暂停' : '启用'} ${task.name}`"
                      :disabled="action === `schedule.toggle:${task.id}`" @change="toggleTask(task)" />
                    <span class="toggle-switch" aria-hidden="true"><span /></span>
                  </label>
                  <button class="icon-button compact" :title="`立即运行 ${task.name}`" :aria-label="`立即运行 ${task.name}`" :disabled="action === `schedule.run:${task.id}`"
                    @click="runTask(task)">
                    <LoaderCircle v-if="action === `schedule.run:${task.id}`" class="spin" :size="14" />
                    <Play v-else :size="14" />
                  </button>
                  <button class="icon-button compact" :title="`编辑 ${task.name}`" :aria-label="`编辑 ${task.name}`" @click="editTask(task)">
                    <Pencil :size="14" />
                  </button>
                  <button class="icon-button compact danger-icon" :title="`删除 ${task.name}`" :aria-label="`删除 ${task.name}`" @click="removeTask(task)">
                    <Trash2 :size="14" />
                  </button>
                </div>
              </div>
            </div>
          </template>

          <form v-else class="scheduled-task-editor" @submit.prevent="saveTask">
            <div class="scheduled-editor-heading">
              <strong>{{ editingId ? '编辑计划任务' : '新建计划任务' }}</strong>
              <button type="button" class="icon-button compact" title="关闭编辑器" aria-label="关闭编辑器" @click="editorOpen = false">
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
                <span><strong>隔离工作目录</strong><small>在专属 Git worktree 中执行</small></span>
                <input v-model="form.runInWorktree" type="checkbox" :disabled="!form.workspace" />
              </label>
              <label class="span-2">
                <span>对话上下文</span>
                <SelectMenu v-model="form.sessionId" :options="conversationOptions" label="对话上下文" />
              </label>
              <label class="span-2">
                <span>通知</span>
                <SelectMenu v-model="form.notificationPolicy" :options="notificationOptions" label="通知策略" />
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
  padding: 12px 12px 16px;
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
  font-size: 13px;
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
  background: var(--surface-active);
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
  background: var(--surface-active);
}

.quick-status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--text-muted);
  flex-shrink: 0;
}

.quick-status-dot.enabled {
  background: light-dark(#18743b, #79c996);
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
  font-family: var(--font-mono);
}

.scheduled-main {
  min-width: 0;
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
  flex-wrap: wrap;
  gap: 12px 16px;
  padding: 20px 28px;
  border-bottom: 1px solid var(--border);
}

.scheduled-content-title {
  min-width: 0;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
}

.scheduled-content-title h3 {
  min-width: 0;
  overflow-wrap: anywhere;
  margin: 0;
  font-size: 18px;
  font-weight: 600;
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
  background: transparent;
  color: var(--text-secondary);
  border: 1px solid var(--border);
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
  min-height: 260px;
  padding: 48px 16px;
  color: var(--text-secondary);
  text-align: center;
  line-height: 1.6;
}

.scheduled-task-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.scheduled-task-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 16px;
  background: transparent;
  border: 1px solid var(--border);
  border-radius: var(--radius-popover, 12px);
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
  gap: 6px 10px;
  flex-wrap: wrap;
}

.scheduled-task-title strong {
  min-width: 0;
  max-width: 100%;
  overflow-wrap: anywhere;
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.scheduled-task-cron {
  padding: 2px 7px;
  background: var(--surface-hover);
  border: 1px solid var(--border);
  border-radius: 5px;
  font-family: var(--font-mono);
  font-size: 11.5px;
  overflow-wrap: anywhere;
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
  line-height: 1.6;
  overflow-wrap: anywhere;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.scheduled-task-controls {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
  flex-wrap: wrap;
  justify-content: flex-end;
  max-width: 100%;
}

.scheduled-task-status {
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 11.5px;
}

.status-success {
  background: color-mix(in srgb, light-dark(#18743b, #79c996) 10%, transparent);
  color: light-dark(#18743b, #79c996);
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
  background: transparent;
  border: 1px solid var(--border);
  border-radius: var(--radius-popover, 12px);
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
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}

.scheduled-form-grid .span-2 {
  grid-column: 1 / -1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.scheduled-form-grid label > span:first-child {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
}

.scheduled-form-grid input:not([type="checkbox"]),
.scheduled-form-grid textarea {
  min-width: 0;
  width: 100%;
  padding: 9px 12px;
  background: transparent;
  border: 1px solid var(--border);
  border-radius: 7px;
  color: var(--text);
  font-size: 13.5px;
  outline: none;
  transition: border-color 140ms ease;
}

.scheduled-form-grid input:not([type="checkbox"]):focus,
.scheduled-form-grid textarea:focus {
  border-color: var(--accent);
  box-shadow: 0 0 0 2px var(--accent-soft);
}

.scheduled-cron-row {
  min-width: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
}

.scheduled-cron-input {
  flex: 1 1 220px;
  font-family: var(--font-mono);
}

.scheduled-form-grid .scheduled-enabled-row {
  display: flex;
  flex-direction: row;
  gap: 16px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 0;
  background: transparent;
}

.scheduled-enabled-row > span:first-child {
  min-width: 0;
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
  gap: 8px;
  flex-wrap: wrap;
  padding-top: 16px;
  border-top: 1px solid var(--border);
}

.scheduled-editor-hint {
  margin-right: auto;
  color: var(--text-muted);
  font-size: 12px;
}


.scheduled-nav-item > svg { flex-shrink: 0; }
.quick-task-cron { flex-shrink: 0; max-width: 48%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.scheduled-content-title { gap: 6px 12px; }
.scheduled-count-tag { white-space: nowrap; }
.scheduled-task-meta { overflow-wrap: anywhere; line-height: 1.6; }
.scheduled-tasks-empty p { margin: 6px 0 0; color: var(--text); font-size: 15px; font-weight: 600; }
.scheduled-tasks-empty > .secondary-button { margin-top: 10px; }
.scheduled-cron-row > :deep(.custom-select) { flex: 1 1 180px; min-width: 0; }
.scheduled-form-grid textarea { resize: vertical; line-height: 1.6; }
.scheduled-enabled-row > input[type="checkbox"]:not(.sr-only) {
  flex: 0 0 auto;
  width: 16px;
  height: 16px;
  margin: 0;
  accent-color: var(--accent);
}

.scheduled-enabled-row input:focus-visible + .toggle-switch,
.scheduled-task-toggle input:focus-visible + .toggle-switch {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
}

@container workbench-content (max-width: 620px) {
  .scheduled-content-header { padding: 18px; }
  .scheduled-main-scroll { padding: 18px 18px 32px; }
  .scheduled-task-row { flex-direction: column; gap: 14px; }
  .scheduled-task-controls { align-self: flex-end; }
  .scheduled-form-grid { grid-template-columns: minmax(0, 1fr); }
  .scheduled-task-editor { padding: 16px; }
  .scheduled-editor-hint { flex-basis: 100%; line-height: 1.6; }
}

@container workbench-layout (max-width: 580px) {
  .scheduled-nav { padding-inline: 8px; }
  .scheduled-nav-item { gap: 6px; padding-inline: 8px; }
  .nav-item-badge { padding-inline: 5px; }
  .scheduled-create-btn { gap: 5px; padding-inline: 6px; font-size: 12px; }
  .quick-task-cron { max-width: 36%; }
}
</style>
