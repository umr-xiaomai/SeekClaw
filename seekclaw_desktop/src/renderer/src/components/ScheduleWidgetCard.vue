<script setup lang="ts">
import { CalendarClock, ChevronDown, Clock3, LoaderCircle, Pause, Pencil, Play, RefreshCw, Save, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type { ScheduledTaskInfo, ScheduleWidget } from '../types'
import { describeCron } from '../conversation-widgets'

const props = defineProps<{ widget: ScheduleWidget }>()
const task = ref<ScheduledTaskInfo>(props.widget.task)
const deleted = ref(props.widget.deleted === true)
const busy = ref('')
const error = ref('')
const notice = ref('')
const editing = ref(false)
const customTime = ref(false)
const cronPresets = [
  { value: '*/10 * * * *', label: '每 10 分钟' },
  { value: '*/30 * * * *', label: '每 30 分钟' },
  { value: '0 * * * *', label: '每小时' },
  { value: '0 9 * * *', label: '每天 09:00' },
  { value: '0 18 * * *', label: '每天 18:00' },
  { value: '0 9 * * 1', label: '每周一 09:00' }
]
const draft = reactive({ name: '', prompt: '', cron: '', notificationPolicy: 'changes' })
const timePreset = computed({
  get: () => customTime.value ? '__custom__' : draft.cron,
  set: (value: string) => { customTime.value = value === '__custom__'; if (!customTime.value) draft.cron = value }
})
let unsubscribe: (() => void) | undefined
let generation = 0
let disposed = false

const status = computed(() => deleted.value ? '已删除' : task.value.enabled ? '已启用' : '已暂停')
const notification = computed(() => ({ changes: '有变化时通知', failures: '仅失败时通知', always: '每次通知' }[task.value.notificationPolicy ?? 'changes']))
function time(value?: string): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }).format(date)
}
async function request<T>(method: string, params: Record<string, unknown>): Promise<T> {
  const response = await window.seekclaw.daemon.request(method, params)
  return JSON.parse(response.data) as T
}
async function refresh(): Promise<void> {
  const version = ++generation
  try {
    const latest = await request<ScheduledTaskInfo | null>('schedule.get', { id: props.widget.task.id })
    if (disposed || version !== generation) return
    deleted.value = latest === null
    if (latest) task.value = latest
    error.value = ''
  } catch (reason) {
    if (!disposed && version === generation) error.value = `无法刷新状态：${reason instanceof Error ? reason.message : String(reason)}`
  }
}
async function act(action: string, operation: () => Promise<void>): Promise<void> {
  if (busy.value || deleted.value) return
  busy.value = action; error.value = ''; notice.value = ''
  generation++
  try { await operation() }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { if (!disposed) busy.value = '' }
}
function edit(): void {
  Object.assign(draft, { name: task.value.name, prompt: task.value.prompt, cron: task.value.cron, notificationPolicy: task.value.notificationPolicy ?? 'changes' })
  customTime.value = !cronPresets.some((preset) => preset.value === draft.cron)
  editing.value = true
}
async function save(): Promise<void> {
  if (!draft.name.trim() || !draft.prompt.trim() || !draft.cron.trim()) { error.value = '请填写名称、任务指令和执行时间'; return }
  await act('save', async () => {
    const current = task.value
    task.value = await request<ScheduledTaskInfo>('schedule.update', {
      id: current.id, name: draft.name.trim(), prompt: draft.prompt.trim(), cron: draft.cron.trim(),
      workspace: current.workspace ?? null, enabled: current.enabled, sessionId: current.sessionId ?? null,
      runInWorktree: current.runInWorktree ?? false, notificationPolicy: draft.notificationPolicy
    })
    editing.value = false; notice.value = '任务已更新'
  })
}
async function toggle(): Promise<void> {
  await act('toggle', async () => {
    task.value = await request<ScheduledTaskInfo>('schedule.toggle', { id: task.value.id, enabled: !task.value.enabled })
    notice.value = task.value.enabled ? '任务已恢复' : '任务已暂停'
  })
}
async function run(): Promise<void> {
  await act('run', async () => {
    await window.seekclaw.daemon.request('schedule.run', { id: task.value.id })
    notice.value = '已提交运行，可继续当前对话'
  })
}
watch(() => props.widget, (widget) => {
  task.value = widget.task
  deleted.value = widget.deleted === true
  // A history reload contains the original tool snapshot; reconcile it with
  // the live task so an edited/paused/deleted schedule never reverts visually.
  void refresh()
})
onMounted(() => {
  void refresh()
  unsubscribe = window.seekclaw.daemon.onEvent((event) => {
    if (event.event === 'schedule.changed' || event.event === 'schedule.updated') void refresh()
  })
})
onBeforeUnmount(() => { disposed = true; generation++; unsubscribe?.() })
</script>

<template>
  <section class="conversation-widget schedule-widget" aria-label="定时任务" :aria-busy="Boolean(busy)">
    <header class="widget-heading">
      <span class="widget-icon"><CalendarClock :size="19" /></span>
      <div class="widget-heading-text"><span class="widget-eyebrow">定时任务</span><h3>{{ task.name || widget.title }}</h3></div>
      <span class="widget-status" :class="{ enabled: task.enabled && !deleted }"><span />{{ status }}</span>
    </header>
    <div class="widget-body">
      <div class="schedule-time"><Clock3 :size="14" /><strong>{{ describeCron(task.cron) }}</strong><span>本地时间</span></div>
      <p class="widget-description">{{ task.prompt }}</p>
      <div class="widget-meta"><span>{{ task.sessionId ? '延续此对话' : '独立对话' }}</span><span>{{ notification }}</span><span v-if="task.runInWorktree">独立工作树</span></div>
      <p v-if="task.enabled && !deleted" class="widget-next-run">下次运行 · {{ time(task.nextRunAt) }}</p>
      <details v-if="task.lastRunAt || task.lastError" class="widget-details">
        <summary>最近运行 · {{ time(task.lastRunAt) }} <ChevronDown :size="13" /></summary>
        <p v-if="task.lastError" class="widget-error">{{ task.lastError }}</p>
        <pre v-else>{{ task.lastOutput || ({ success: '运行成功', cancelled: '已取消', skipped: '已跳过' }[task.lastStatus ?? ''] || '尚无输出') }}</pre>
      </details>
      <form v-if="editing && !deleted" class="widget-editor" @submit.prevent="save">
        <label>任务名称<input v-model="draft.name" required maxlength="200" :disabled="Boolean(busy)" /></label>
        <label>任务指令<textarea v-model="draft.prompt" required rows="3" :disabled="Boolean(busy)" /></label>
        <div class="widget-editor-row">
          <label>执行时间<select v-model="timePreset" :disabled="Boolean(busy)"><option v-for="preset in cronPresets" :key="preset.value" :value="preset.value">{{ preset.label }}</option><option value="__custom__">自定义时间…</option></select></label>
          <label>通知<select v-model="draft.notificationPolicy" :disabled="Boolean(busy)"><option value="changes">有变化时</option><option value="failures">仅失败时</option><option value="always">每次运行</option></select></label>
        </div>
        <label v-if="customTime">自定义 Cron<input v-model="draft.cron" required placeholder="0 9 * * *" :disabled="Boolean(busy)" /><small>分 时 日 月 周 · 使用本地时区，例如每天 20:00：0 20 * * *</small></label>
        <div class="widget-editor-actions"><button type="button" class="widget-button" :disabled="Boolean(busy)" @click="editing = false"><X :size="13" />取消</button><button type="submit" class="widget-button widget-primary" :disabled="Boolean(busy)"><LoaderCircle v-if="busy === 'save'" :size="13" class="spin" /><Save v-else :size="13" />保存修改</button></div>
      </form>
      <p v-if="error" class="widget-error" role="alert">{{ error }} <button type="button" class="widget-text-button" :disabled="Boolean(busy)" @click="refresh">重试</button></p>
      <p v-if="notice" class="widget-notice" role="status">{{ notice }}</p>
    </div>
    <footer v-if="!editing" class="widget-footer">
      <span class="widget-footnote">{{ deleted ? '任务已移除，保留此次操作记录' : 'SeekClaw 运行时开启时执行' }}</span>
      <div v-if="!deleted" class="widget-actions">
        <button type="button" class="widget-button" :disabled="Boolean(busy)" @click="edit"><Pencil :size="13" />编辑</button>
        <button type="button" class="widget-button" :disabled="Boolean(busy)" @click="toggle"><LoaderCircle v-if="busy === 'toggle'" :size="13" class="spin" /><Pause v-else-if="task.enabled" :size="13" /><Play v-else :size="13" />{{ task.enabled ? '暂停' : '恢复' }}</button>
        <button type="button" class="widget-button widget-primary" :disabled="Boolean(busy)" @click="run"><LoaderCircle v-if="busy === 'run'" :size="13" class="spin" /><Play v-else :size="13" />立即运行</button>
      </div>
      <button v-else type="button" class="widget-button" @click="refresh"><RefreshCw :size="13" />刷新</button>
    </footer>
  </section>
</template>
