<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'

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
  <details class="task-activity">
    <summary>命令会话与子任务（{{ commands.length + children.length }}）</summary>
    <button class="secondary-button" :disabled="busy" @click="refresh">刷新</button>
    <p v-if="error" role="alert">{{ error }}</p>
    <div v-for="command in commands" :key="command.sessionId" class="activity-row">
      <code>{{ command.command }}</code><small>{{ command.status }}</small>
      <button class="secondary-button" :disabled="busy" @click="act(() => readCommand(command.sessionId))">读取输出</button>
      <button class="secondary-button" :disabled="busy" @click="act(async () => { await request('command.terminate', { commandId: command.sessionId }) })">停止</button>
    </div>
    <div v-if="selected" class="command-console">
      <pre>{{ output || '尚无输出' }}</pre>
      <textarea v-model="input" rows="2" aria-label="命令输入" placeholder="输入文本，发送时自动添加换行" />
      <button class="secondary-button" :disabled="busy || !input" @click="act(() => writeCommand())">发送输入</button>
      <button class="secondary-button" :disabled="busy" @click="act(() => writeCommand(true))">关闭输入</button>
    </div>
    <details v-for="child in children" :key="child.id" class="activity-row">
      <summary>{{ child.role || '子任务' }} · {{ child.status }}</summary>
      <pre>{{ child.error || child.output?.slice(-20000) || '尚无结果' }}</pre>
      <button v-if="child.status === 'running'" class="secondary-button" :disabled="busy" @click="act(async () => { await request('subagent.cancel', { taskId: child.id }) })">取消子任务</button>
    </details>
  </details>
</template>

<style scoped>
.task-activity { display: grid; gap: 8px; font-size: 12px; }
.task-activity summary { cursor: pointer; padding: 6px 0; }
.activity-row { margin: 8px 0; padding: 8px; border: 1px solid var(--border); border-radius: 6px; }
.activity-row code { display: block; overflow-wrap: anywhere; max-height: 60px; overflow: auto; }
.activity-row small { display: block; margin: 4px 0; }
.task-activity pre { max-height: 180px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; }
.task-activity textarea { width: 100%; }
.task-activity button { margin: 4px 4px 4px 0; }
</style>
