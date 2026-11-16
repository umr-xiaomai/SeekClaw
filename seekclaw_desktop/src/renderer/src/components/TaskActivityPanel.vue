<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Bot, ChevronRight, RefreshCw, Terminal } from '@lucide/vue'

const props = defineProps<{ sessionId: string; workspace?: string }>()
interface CommandInfo { sessionId: string; command: string; status: string }
interface CommandOutput { cursor: number; output: string; status: string; truncated: boolean }
interface ChildInfo { id: string; role?: string; status: string; output?: string; error?: string }
const commands = ref<CommandInfo[]>([])
const children = ref<ChildInfo[]>([])
const error = ref('')
const busy = ref(false)
const selected = ref('')
const output = ref('')
const input = ref('')
let cursor = 0
let unsubscribe: (() => void) | undefined
let generation = 0
const scope = (): Record<string, unknown> => ({ sessionId: props.sessionId, workspace: props.workspace, global: !props.workspace })

async function request<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.seekclaw.daemon.request(method, { ...scope(), ...params })
  return JSON.parse(response.data) as T
}

async function refresh(): Promise<void> {
  const version = ++generation
  try {
    const [processes, agents] = await Promise.all([request<CommandInfo[]>('command.list'), request<ChildInfo[]>('subagent.list')])
    if (version !== generation) return
    commands.value = processes
    children.value = agents
  } catch (reason) { if (version === generation) error.value = String(reason) }
}

async function act(operation: () => Promise<void>): Promise<void> {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try { await operation(); await refresh() }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { busy.value = false }
}

async function readCommand(id: string): Promise<void> {
  if (selected.value !== id) { selected.value = id; output.value = ''; cursor = 0 }
  const result = await request<CommandOutput>('command.read', { commandId: id, cursor, waitMs: 1000 })
  cursor = result.cursor
  output.value = (output.value + (result.truncated ? '\n[较早输出已截断]\n' : '') + result.output).slice(-32768)
}

async function writeCommand(closeInput = false): Promise<void> {
  await request<CommandOutput>('command.write', { commandId: selected.value, text: input.value ? input.value.replace(/\n?$/, '\n') : '', closeInput })
  input.value = ''
  await readCommand(selected.value)
}

watch(() => [props.sessionId, props.workspace], () => {
  selected.value = ''; output.value = ''; cursor = 0
  void refresh()
}, { immediate: true })
onMounted(() => {
  unsubscribe = window.seekclaw.daemon.onEvent((event) => {
    if (event.event === 'subagent.updated' && event.details?.parentSessionId === props.sessionId) void refresh()
  })
})
onBeforeUnmount(() => { generation++; unsubscribe?.() })
</script>

<template>
  <details class="task-activity" :aria-busy="busy">
    <summary class="activity-summary"><ChevronRight :size="14" class="activity-chevron" /><span>命令会话与子任务</span><span class="activity-count">{{ commands.length + children.length }}</span></summary>
    <div class="activity-content">
      <div class="activity-toolbar"><span>当前会话的运行记录</span><button class="secondary-button" :disabled="busy" @click="refresh"><RefreshCw :size="13" />刷新</button></div>
      <p v-if="error" class="activity-error" role="alert">{{ error }}</p>
      <p v-if="!commands.length && !children.length && !error" class="activity-empty">暂无命令会话或子任务</p>
      <div v-for="command in commands" :key="command.sessionId" class="activity-row" :class="{ 'is-selected': selected === command.sessionId }">
        <div class="activity-heading"><Terminal :size="14" /><span>命令会话</span><small class="activity-status" :class="`status-${command.status}`">{{ command.status }}</small></div>
        <code>{{ command.command }}</code>
        <div class="activity-actions"><button class="secondary-button" :disabled="busy" @click="act(() => readCommand(command.sessionId))">读取输出</button><button class="secondary-button" :disabled="busy" @click="act(async () => { await request('command.terminate', { commandId: command.sessionId }) })">停止</button></div>
      </div>
      <div v-if="selected" class="command-console">
        <div class="activity-heading"><Terminal :size="14" /><span>命令输出</span></div>
        <pre tabindex="0" aria-label="命令输出">{{ output || '尚无输出' }}</pre>
        <textarea v-model="input" rows="2" aria-label="命令输入" placeholder="输入文本，发送时自动添加换行" />
        <div class="activity-actions"><button class="secondary-button" :disabled="busy || !input" @click="act(() => writeCommand())">发送输入</button><button class="secondary-button" :disabled="busy" @click="act(() => writeCommand(true))">关闭输入</button></div>
      </div>
      <details v-for="child in children" :key="child.id" class="activity-row child-activity">
        <summary><ChevronRight :size="13" class="activity-chevron" /><Bot :size="14" /><span>{{ child.role || '子任务' }}</span><small class="activity-status" :class="`status-${child.status}`">{{ child.status }}</small></summary>
        <pre tabindex="0" :aria-label="`${child.role || '子任务'}的输出`" :class="{ 'activity-error': child.error }">{{ child.error || child.output?.slice(-20000) || '尚无结果' }}</pre>
        <div v-if="child.status === 'running'" class="activity-actions"><button class="secondary-button" :disabled="busy" @click="act(async () => { await request('subagent.cancel', { taskId: child.id }) })">取消子任务</button></div>
      </details>
    </div>
  </details>
</template>

<style scoped>
.task-activity { min-width: 0; color: var(--text-secondary); font-size: 12px; }
.task-activity summary { display: flex; align-items: center; gap: 7px; min-height: 36px; cursor: pointer; list-style: none; }
.task-activity summary::-webkit-details-marker { display: none; }
.activity-summary { width: fit-content; max-width: 100%; border-radius: 7px; }
.activity-summary:hover { color: var(--text); }
.activity-chevron { flex-shrink: 0; color: var(--text-muted); transition: transform 120ms; }
.task-activity[open] > summary .activity-chevron, .child-activity[open] > summary .activity-chevron { transform: rotate(90deg); }
.activity-count { min-width: 20px; padding: 1px 5px; border: 1px solid var(--border); border-radius: 5px; color: var(--text-muted); font-size: 10px; text-align: center; font-variant-numeric: tabular-nums; }
.activity-content { display: grid; gap: 8px; min-width: 0; padding: 4px 0 10px; }
.activity-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 10px; color: var(--text-muted); font-size: 11px; }
.activity-row, .command-console { min-width: 0; padding: 12px; border: 1px solid var(--border); border-radius: 10px; }
.activity-row.is-selected { border-color: var(--border-strong); }
.activity-heading { display: flex; align-items: center; gap: 7px; min-width: 0; margin-bottom: 8px; font-size: 11px; color: var(--text-muted); }
.activity-heading > svg, .child-activity summary > svg { flex-shrink: 0; }
.activity-row code { display: block; overflow-wrap: anywhere; max-height: 60px; overflow: auto; color: var(--text); font: 11px/1.7 var(--font-mono); }
.activity-status { display: inline-flex; align-items: center; gap: 5px; margin-left: auto; font-size: 10px; color: var(--text-muted); }
.activity-status::before { content: ''; width: 5px; height: 5px; border-radius: 50%; background: currentColor; }
.status-running { color: var(--text-secondary); }
.status-failed, .activity-error { color: var(--danger); }
.activity-error { margin: 0; line-height: 1.6; overflow-wrap: anywhere; }
.activity-empty { margin: 0; padding: 24px 12px; color: var(--text-muted); text-align: center; }
.activity-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 4px; margin-top: 8px; }
.task-activity .secondary-button { display: inline-flex; align-items: center; justify-content: center; gap: 6px; min-height: 30px; padding: 4px 9px; border: 1px solid transparent; border-radius: 7px; color: var(--text-secondary); background: transparent; font-size: 11px; }
.task-activity .secondary-button:hover:not(:disabled) { color: var(--text); background: var(--surface-hover); }
.task-activity .secondary-button:disabled { opacity: .45; cursor: default; }
.task-activity pre { max-height: 220px; margin: 0; padding: 10px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; color: var(--text-secondary); background: var(--code-bg); border-radius: 7px; font: 11px/1.7 var(--font-mono); scrollbar-width: thin; }
.task-activity pre.activity-error { color: var(--danger); }
.task-activity textarea { display: block; width: 100%; min-height: 64px; margin-top: 10px; padding: 9px 10px; resize: vertical; color: var(--text); background: var(--surface); border: 1px solid var(--border); border-radius: 8px; font: 12px/1.6 var(--font-sans); }
.task-activity textarea::placeholder { color: var(--text-muted); }
.child-activity > summary { min-height: 24px; }
.child-activity[open] > summary { margin-bottom: 10px; }
.task-activity :is(button, summary, textarea, pre):focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
@media (prefers-reduced-motion: reduce) { .activity-chevron { transition: none; } }
</style>
