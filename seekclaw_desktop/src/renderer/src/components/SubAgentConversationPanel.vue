<script setup lang="ts">
import { Bot, Check, ChevronRight, CircleAlert, Code2, LoaderCircle, RefreshCw, Search, TestTube2, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import type { SubAgentActivity } from '../types'
import { subAgentName, subAgentStatusLabel } from '../subagent-activity'
import { createSubAgentConversationSource, emptySubAgentConversation } from '../subagent-conversation'
import MarkdownMessage from './MarkdownMessage.vue'
import ToolActivityList from './ToolActivityList.vue'

const props = defineProps<{ agent: SubAgentActivity; sessionId: string; workspace?: string }>()
const emit = defineEmits<{ close: []; openDiff: [path: string, diff: string] }>()
const snapshot = shallowRef(emptySubAgentConversation())
const scroller = ref<HTMLElement | null>(null)
const followOutput = ref(true)
const roleIcon = computed(() => props.agent.role === 'researcher' ? Search : props.agent.role === 'coder' ? Code2 : props.agent.role === 'tester' ? TestTube2 : Bot)
const currentAgent = computed(() => ({ ...props.agent, status: snapshot.value.task?.status ?? props.agent.status }))
const running = computed(() => ['starting', 'running'].includes(currentAgent.value.status))
const output = computed(() => snapshot.value.task?.output || props.agent.output)
const failure = computed(() => snapshot.value.task?.error)
const progress = computed(() => snapshot.value.progress || props.agent.progress)
const source = createSubAgentConversationSource(window.seekclaw.daemon, (state) => {
  snapshot.value = state
  if (followOutput.value) void nextTick(() => {
    if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
  })
})

watch(() => [props.agent.id, props.agent.runId, props.agent.requestId, props.sessionId, props.workspace] as const, () => {
  followOutput.value = true
  void source.select({ agent: props.agent, parentSessionId: props.sessionId, workspace: props.workspace })
}, { immediate: true })
onBeforeUnmount(() => source.dispose())

function onScroll(): void {
  const element = scroller.value
  if (element) followOutput.value = element.scrollHeight - element.scrollTop - element.clientHeight < 64
}
</script>

<template>
  <aside class="subagent-conversation-panel" aria-label="子 Agent 工作过程">
    <header class="child-panel-header">
      <component :is="roleIcon" :size="18" class="child-role-icon" aria-hidden="true" />
      <div class="child-heading">
        <h2>{{ subAgentName(agent) }}</h2>
        <span class="child-status" :class="{ failed: currentAgent.status === 'failed' }">
          <LoaderCircle v-if="running" :size="12" class="child-spin" />
          <Check v-else-if="currentAgent.status === 'completed'" :size="12" />
          <CircleAlert v-else-if="currentAgent.status === 'failed'" :size="12" />
          {{ subAgentStatusLabel(currentAgent) }}
        </span>
      </div>
      <button class="child-icon-button" type="button" :disabled="snapshot.loading" title="刷新工作过程" aria-label="刷新工作过程" @click="source.refresh">
        <RefreshCw :size="15" :class="{ 'child-spin': snapshot.loading }" />
      </button>
      <button class="child-icon-button" type="button" title="关闭子 Agent 面板" aria-label="关闭子 Agent 面板" @click="emit('close')"><X :size="17" /></button>
    </header>

    <div v-if="snapshot.model" class="child-model" :title="snapshot.model"><Bot :size="13" aria-hidden="true" /><span>{{ snapshot.model }}</span></div>

    <div ref="scroller" class="child-conversation" :aria-busy="snapshot.loading" @scroll="onScroll">
      <div v-if="snapshot.loading && !snapshot.messages.length" class="child-empty" role="status"><LoaderCircle :size="20" class="child-spin" /><span>正在加载工作过程…</span></div>
      <div v-if="snapshot.error" class="child-load-error" role="alert">
        <CircleAlert :size="16" /><div><strong>暂时无法加载对话</strong><p>{{ snapshot.error }}</p><button type="button" @click="source.refresh">重试</button></div>
      </div>

      <template v-if="snapshot.messages.length">
        <article v-for="message in snapshot.messages" :key="message.id" class="child-message" :class="`child-message-${message.role}`">
          <details v-if="message.role === 'user'" class="child-task" :open="snapshot.messages.length === 1">
            <summary><ChevronRight :size="14" /><span>任务指令</span></summary>
            <div class="child-task-text">{{ message.content }}</div>
            <div v-if="message.images?.length" class="child-images"><img v-for="image in message.images" :key="image.id" :src="`data:${image.mediaType};base64,${image.data}`" :alt="image.name" loading="lazy"></div>
          </details>
          <template v-else>
            <details v-if="message.thinking" class="child-thinking">
              <summary><Check v-if="message.state !== 'thinking'" :size="13" /><span :class="{ 'thinking-shimmer': message.state === 'thinking' }">{{ message.state === 'thinking' ? '正在思考' : '思考过程' }}</span><ChevronRight :size="13" /></summary>
              <div>{{ message.thinking }}</div>
            </details>
            <div v-else-if="message.state === 'thinking' && !message.content && !message.tools?.length" class="child-thinking">
              <span class="thinking-shimmer">正在思考</span>
            </div>
            <MarkdownMessage v-if="message.content" :content="message.content" :streaming="message.state === 'streaming'" />
            <ToolActivityList v-if="message.tools?.length" :tools="message.tools" :streaming="running" @open-diff="(path, diff) => emit('openDiff', path, diff)" />
            <div v-if="message.errorMessage" class="child-message-error" role="alert"><CircleAlert :size="15" /><span>{{ message.errorMessage }}</span></div>
          </template>
        </article>
      </template>
      <template v-else-if="!snapshot.loading">
        <details v-if="agent.task" class="child-task" open><summary><ChevronRight :size="14" /><span>任务指令</span></summary><div class="child-task-text">{{ agent.task }}</div></details>
        <MarkdownMessage v-if="output" :content="output" />
        <div v-else-if="!snapshot.error" class="child-empty"><component :is="roleIcon" :size="24" /><span>{{ running ? '正在开始工作…' : '此子 Agent 暂无对话记录' }}</span></div>
      </template>
      <div v-if="failure && !snapshot.messages.some(message => message.errorMessage === failure)" class="child-message-error" role="alert"><CircleAlert :size="15" /><span>{{ failure }}</span></div>
    </div>

    <footer class="child-panel-footer">
      <span v-if="running" class="child-live-dot" aria-hidden="true" />
      <span>{{ running ? (progress || '正在执行任务') : subAgentStatusLabel(currentAgent) }}</span>
      <span class="child-readonly">只读</span>
    </footer>
  </aside>
</template>

<style scoped>
.subagent-conversation-panel { display: flex; flex-direction: column; min-width: 0; width: 100%; height: 100%; overflow: hidden; background: var(--bg); color: var(--text); }
.child-panel-header { display: flex; flex: none; align-items: center; gap: 10px; min-height: 57px; padding: 12px 16px; border-bottom: 1px solid var(--border); }
.child-role-icon { flex: none; color: var(--text-secondary); }
.child-heading { display: flex; flex: 1; min-width: 0; align-items: center; flex-wrap: wrap; gap: 6px 10px; }
.child-heading h2 { margin: 0; overflow: hidden; font-size: 14px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.child-status { display: inline-flex; align-items: center; gap: 5px; font-size: 11px; color: var(--text-muted); }
.child-status.failed, .child-message-error { color: var(--danger); }
.child-icon-button { display: grid; place-items: center; flex: none; width: 28px; height: 28px; padding: 0; border: 0; border-radius: 6px; background: transparent; color: var(--text-muted); cursor: pointer; }
.child-icon-button:hover { background: var(--surface-hover); color: var(--text); }
.child-icon-button:disabled { opacity: .45; cursor: wait; }
.child-icon-button:focus-visible, .child-task summary:focus-visible, .child-thinking summary:focus-visible, .child-load-error button:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.child-model { display: flex; flex: none; gap: 6px; align-items: center; padding: 10px 19px 0; color: var(--text-muted); font-size: 11px; }
.child-model span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.child-conversation { flex: 1; min-height: 0; overflow: auto; overscroll-behavior: contain; padding: 20px; scrollbar-gutter: stable; }
.child-message { min-width: 0; margin-bottom: 23px; font-size: 13px; overflow-wrap: anywhere; }
.child-message :deep(.markdown-message) { font-size: 13px; }
.child-message :deep(pre) { max-width: 100%; }
.child-message :deep(.tool-activities) { margin-top: 14px; }
.child-task { margin-bottom: 18px; color: var(--text-secondary); font-size: 12px; }
.child-task summary, .child-thinking summary { display: flex; align-items: center; gap: 6px; list-style: none; cursor: pointer; }
.child-task summary::-webkit-details-marker, .child-thinking summary::-webkit-details-marker { display: none; }
.child-task[open] > summary > svg:first-child, .child-thinking[open] > summary > svg:last-child { transform: rotate(90deg); }
.child-task-text { margin: 10px 0 0 20px; white-space: pre-wrap; line-height: 1.7; overflow-wrap: anywhere; }
.child-thinking { margin-bottom: 13px; color: var(--text-muted); font-size: 12px; }
.child-thinking > div { margin-top: 10px; padding-left: 19px; line-height: 1.75; white-space: pre-wrap; }
.child-thinking summary > svg:last-child { margin-left: 2px; }
.child-images { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; }
.child-images img { width: 82px; height: 65px; object-fit: cover; border: 1px solid var(--border); border-radius: 6px; }
.child-empty { display: flex; min-height: 140px; align-items: center; justify-content: center; flex-direction: column; gap: 12px; color: var(--text-muted); font-size: 12px; }
.child-load-error { display: flex; gap: 9px; margin-bottom: 18px; color: var(--text-secondary); font-size: 12px; line-height: 1.65; }
.child-load-error > svg { flex: none; margin-top: 2px; color: var(--danger); }
.child-load-error strong { color: var(--text); font-weight: 500; }
.child-load-error p { margin: 3px 0 7px; overflow-wrap: anywhere; }
.child-load-error button { padding: 0; border: 0; background: transparent; color: var(--text); text-decoration: underline; text-underline-offset: 3px; cursor: pointer; }
.child-message-error { display: flex; align-items: flex-start; gap: 8px; margin-top: 12px; font-size: 12px; line-height: 1.65; overflow-wrap: anywhere; }
.child-message-error > svg { flex: none; margin-top: 3px; }
.child-panel-footer { display: flex; align-items: center; flex: none; gap: 7px; min-height: 37px; padding: 9px 17px; border-top: 1px solid var(--border); color: var(--text-muted); font-size: 11px; }
.child-panel-footer > span:nth-last-child(2) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.child-readonly { margin-left: auto; flex: none; }
.child-live-dot { width: 5px; height: 5px; flex: none; border-radius: 50%; background: var(--text-secondary); }
.child-spin { animation: child-spin 1s linear infinite; }
@keyframes child-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .child-spin { animation: none; } }
@media (max-width: 520px) { .child-conversation { padding: 16px; } .child-panel-header { padding-inline: 13px; } }
</style>
