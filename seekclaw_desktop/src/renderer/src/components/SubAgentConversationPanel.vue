<script setup lang="ts">
import { ArrowLeft, Bot, Check, ChevronRight, CircleAlert, LoaderCircle, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import type { SubAgentActivity, ToolActivity } from '../types'
import { formatModelName, formatRelativeTime } from '../app-helpers'
import { buildConversationProjection } from '../conversation-process'
import type { ToolDiffPreview } from '../conversation-diffs'
import { subAgentName, subAgentStatusLabel, type ThreadSubAgentEntry } from '../subagent-activity'
import { createSubAgentConversationSource, emptySubAgentConversation } from '../subagent-conversation'
import { collectSubAgentEmblemIndices, subAgentEmblem } from '../subagent-emblems'
import ConversationProcess from './ConversationProcess.vue'

const DEFAULT_PANEL_WIDTH = 520
const MIN_PANEL_WIDTH = 320
const MAX_PANEL_WIDTH = 1400

const props = withDefaults(defineProps<{
  agent: SubAgentActivity
  agents?: ThreadSubAgentEntry[]
  sessionId: string
  workspace?: string
  width?: number
}>(), {
  agents: () => [],
  width: DEFAULT_PANEL_WIDTH
})

const emit = defineEmits<{
  close: []
  openDiffPreview: [preview: ToolDiffPreview, trigger: HTMLButtonElement]
  openDiff: [path: string, diff: string]
  openBrowserResult: [tool: ToolActivity, sessionId?: string]
  resize: [width: number]
  selectAgent: [agent: SubAgentActivity]
}>()

const viewMode = ref<'detail' | 'list'>('detail')
const snapshot = shallowRef(emptySubAgentConversation())
const panelRef = ref<HTMLElement | null>(null)
const scroller = ref<HTMLElement | null>(null)
const followOutput = ref(true)
const resizing = ref(false)
const nowTick = ref(Date.now())

let resizeStartX = 0
let resizeStartWidth = props.width
let pendingWidth = props.width
let relativeTimer: ReturnType<typeof setInterval> | undefined

const currentAgent = computed<SubAgentActivity>(() => ({
  ...props.agent,
  status: snapshot.value.task?.status ?? props.agent.status
}))
const running = computed(() => ['starting', 'running'].includes(currentAgent.value.status))
const output = computed(() => snapshot.value.task?.output || props.agent.output)
const failure = computed(() => snapshot.value.task?.error)
const childSessionId = computed(() => snapshot.value.task?.sessionId || currentAgent.value.childSessionId)
const childWorkspace = computed(() => {
  const task = snapshot.value.task
  if (task?.sessionGlobal) return undefined
  return task?.sessionWorkspace || (task?.workspace && props.workspace ? task.workspace : props.workspace)
})
const rawModel = computed(() => snapshot.value.model || currentAgent.value.model || '')
const displayModel = computed(() => (rawModel.value ? formatModelName(rawModel.value) : ''))

const effectiveMessages = computed(() => {
  if (snapshot.value.messages.length > 0) return snapshot.value.messages
  if (!snapshot.value.loading && output.value) {
    return [
      ...(currentAgent.value.task
        ? [{ id: `subagent-fallback-user:${currentAgent.value.id}`, role: 'user' as const, content: currentAgent.value.task, createdAt: 0 }]
        : []),
      { id: `subagent-fallback-output:${currentAgent.value.id}`, role: 'assistant' as const, content: output.value, state: 'done' as const, createdAt: 0 }
    ]
  }
  return []
})

const streamingAssistantId = computed(() => {
  if (!running.value) return undefined
  const lastAssistant = [...effectiveMessages.value].reverse().find((m) => m.role === 'assistant')
  return lastAssistant?.id
})

const projection = computed(() => buildConversationProjection(effectiveMessages.value, {
  running: running.value,
  assistantId: streamingAssistantId.value
}))

const overviewEntries = computed<ThreadSubAgentEntry[]>(() => {
  if (props.agents.length > 0) {
    return props.agents.map((entry) =>
      entry.agent.id === currentAgent.value.id
        ? { ...entry, agent: currentAgent.value, active: running.value }
        : entry
    )
  }
  return [{ agent: currentAgent.value, updatedAt: Date.now(), active: running.value }]
})

const activeEntries = computed(() => overviewEntries.value.filter((entry) => entry.active))
const settledEntries = computed(() => overviewEntries.value.filter((entry) => !entry.active))

const emblemIndexById = computed(() => {
  const orderedAgents = overviewEntries.value.map((entry) => entry.agent)
  if (!orderedAgents.some((a) => a.id === currentAgent.value.id)) {
    orderedAgents.unshift(currentAgent.value)
  }
  return collectSubAgentEmblemIndices(orderedAgents)
})

const currentEmblem = computed(() => subAgentEmblem(currentAgent.value.id, emblemIndexById.value))
const panelStyle = computed(() => ({
  '--subagent-panel-width': `${props.width}px`
}))

const source = createSubAgentConversationSource(window.seekclaw.daemon, (state) => {
  snapshot.value = state
  if (followOutput.value && viewMode.value === 'detail') {
    void nextTick(() => {
      if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
    })
  }
})

watch(() => [props.agent.id, props.agent.runId, props.agent.requestId, props.sessionId, props.workspace] as const, () => {
  viewMode.value = 'detail'
  followOutput.value = true
  void source.select({ agent: props.agent, parentSessionId: props.sessionId, workspace: props.workspace })
}, { immediate: true })

watch(viewMode, (mode) => {
  if (relativeTimer) {
    clearInterval(relativeTimer)
    relativeTimer = undefined
  }
  if (mode === 'list') {
    nowTick.value = Date.now()
    relativeTimer = setInterval(() => {
      nowTick.value = Date.now()
    }, 30_000)
  }
})

function openAgentDetail(agent: SubAgentActivity): void {
  viewMode.value = 'detail'
  if (agent.id === props.agent.id && agent.runId === props.agent.runId) {
    followOutput.value = true
    return
  }
  emit('selectAgent', agent)
}

function openNestedSubAgent(child: SubAgentActivity): void {
  const matched = overviewEntries.value.find((entry) => entry.agent.id === child.id)?.agent
  openAgentDetail(matched ?? child)
}

function onScroll(): void {
  const element = scroller.value
  if (element) followOutput.value = element.scrollHeight - element.scrollTop - element.clientHeight < 64
}

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  emit('resize', pendingWidth)
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
  void nextTick(() => {
    if (panelRef.value) {
      panelRef.value.style.setProperty('--subagent-panel-width', `${props.width}px`)
    }
  })
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const viewportWidth = window.innerWidth
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(viewportWidth * 0.85)))
  const nextWidth = Math.round(resizeStartWidth + resizeStartX - event.clientX)
  if (nextWidth <= MIN_PANEL_WIDTH - 40) {
    stopResize()
    emit('close')
    return
  }
  const clampedWidth = Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth))
  pendingWidth = clampedWidth
  if (panelRef.value) {
    panelRef.value.style.setProperty('--subagent-panel-width', `${clampedWidth}px`)
  }
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = panelRef.value?.getBoundingClientRect().width || props.width
  pendingWidth = resizeStartWidth
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

function resetPanelWidth(): void {
  pendingWidth = DEFAULT_PANEL_WIDTH
  emit('resize', DEFAULT_PANEL_WIDTH)
  void nextTick(() => {
    if (panelRef.value) {
      panelRef.value.style.setProperty('--subagent-panel-width', `${props.width}px`)
    }
  })
}

onBeforeUnmount(() => {
  stopResize()
  if (relativeTimer) clearInterval(relativeTimer)
  source.dispose()
})
</script>

<template>
  <aside
    ref="panelRef"
    class="subagent-conversation-panel"
    :class="{ 'is-resizing': resizing }"
    :style="panelStyle"
    aria-label="子智能体工作过程"
  >
    <div
      class="subagent-panel-resize-handle"
      role="separator"
      aria-label="调整子智能体面板宽度"
      aria-orientation="vertical"
      @pointerdown="startResize"
      @dblclick="resetPanelWidth"
    />

    <div class="subagent-tab-strip">
      <div class="subagent-tab-pill">
        <span class="subagent-tab-main">
          <Bot :size="15" class="subagent-tab-icon" aria-hidden="true" />
          <span class="subagent-tab-label">子智能体</span>
        </span>
        <button
          type="button"
          class="subagent-tab-close"
          title="关闭子智能体"
          aria-label="关闭子智能体"
          @click.stop="emit('close')"
        >
          <X :size="13" aria-hidden="true" />
        </button>
      </div>
    </div>

    <template v-if="viewMode === 'list'">
      <div class="subagent-overview-scroll">
        <section class="subagent-overview-section" aria-label="已开启的子代理">
          <h3 class="subagent-overview-heading">已开启 · {{ activeEntries.length }}</h3>
          <p v-if="activeEntries.length === 0" class="subagent-overview-empty">没有已开启的子代理</p>
          <div v-else class="subagent-overview-list">
            <button
              v-for="entry in activeEntries"
              :key="entry.agent.id"
              type="button"
              class="subagent-overview-row"
              @click="openAgentDetail(entry.agent)"
            >
              <svg class="agent-emblem" viewBox="0 0 16 16" width="18" height="18" aria-hidden="true">
                <path :d="subAgentEmblem(entry.agent.id, emblemIndexById).path" :fill="subAgentEmblem(entry.agent.id, emblemIndexById).color" fill-rule="evenodd" />
              </svg>
              <span class="subagent-overview-name">{{ subAgentName(entry.agent) }}</span>
              <span class="subagent-overview-status">
                <LoaderCircle :size="12" class="child-spin" aria-hidden="true" />
                <span>{{ subAgentStatusLabel(entry.agent) }}</span>
              </span>
            </button>
          </div>
        </section>

        <section class="subagent-overview-section" aria-label="已完成的子代理">
          <h3 class="subagent-overview-heading">完成 · {{ settledEntries.length }}</h3>
          <p v-if="settledEntries.length === 0" class="subagent-overview-empty">没有已完成的子代理</p>
          <div v-else class="subagent-overview-list">
            <button
              v-for="entry in settledEntries"
              :key="entry.agent.id"
              type="button"
              class="subagent-overview-row"
              @click="openAgentDetail(entry.agent)"
            >
              <svg class="agent-emblem" viewBox="0 0 16 16" width="18" height="18" aria-hidden="true">
                <path :d="subAgentEmblem(entry.agent.id, emblemIndexById).path" :fill="subAgentEmblem(entry.agent.id, emblemIndexById).color" fill-rule="evenodd" />
              </svg>
              <span class="subagent-overview-name">{{ subAgentName(entry.agent) }}</span>
              <span
                v-if="entry.agent.status !== 'completed'"
                class="subagent-overview-badge"
                :class="{ failed: entry.agent.status === 'failed' }"
              >
                {{ subAgentStatusLabel(entry.agent) }}
              </span>
              <time
                v-if="entry.updatedAt"
                class="subagent-overview-time"
                :datetime="new Date(entry.updatedAt).toISOString()"
              >
                {{ formatRelativeTime(entry.updatedAt, nowTick) }}
              </time>
            </button>
          </div>
        </section>
      </div>
    </template>

    <template v-else>
      <header class="subagent-detail-header">
        <div class="subagent-detail-title">
          <button
            type="button"
            class="subagent-back-btn"
            title="返回子智能体列表"
            aria-label="返回子智能体列表"
            @click="viewMode = 'list'"
          >
            <ArrowLeft :size="16" aria-hidden="true" />
          </button>
          <svg class="agent-emblem" viewBox="0 0 16 16" width="18" height="18" aria-hidden="true">
            <path :d="currentEmblem.path" :fill="currentEmblem.color" fill-rule="evenodd" />
          </svg>
          <h2 class="subagent-detail-name" :title="subAgentName(currentAgent)">{{ subAgentName(currentAgent) }}</h2>
          <span
            v-if="currentAgent.status !== 'unknown'"
            class="subagent-detail-status"
            :class="{ failed: currentAgent.status === 'failed', completed: currentAgent.status === 'completed' }"
          >
            <LoaderCircle v-if="running" :size="12" class="child-spin" aria-hidden="true" />
            <Check v-else-if="currentAgent.status === 'completed'" :size="12" aria-hidden="true" />
            <CircleAlert v-else :size="12" aria-hidden="true" />
            <span>{{ subAgentStatusLabel(currentAgent) }}</span>
          </span>
        </div>
        <span v-if="displayModel" class="subagent-detail-model" :title="rawModel">{{ displayModel }}</span>
      </header>

      <div ref="scroller" class="subagent-detail-scroll" :aria-busy="snapshot.loading" @scroll="onScroll">
        <div class="conversation-content subagent-conversation-content">
          <div v-if="snapshot.loading && !snapshot.messages.length" class="child-empty" role="status">
            <LoaderCircle :size="20" class="child-spin" aria-hidden="true" />
            <span>正在加载工作过程…</span>
          </div>

          <div v-if="snapshot.error" class="child-load-error" role="alert">
            <CircleAlert :size="16" aria-hidden="true" />
            <div>
              <strong>暂时无法加载对话</strong>
              <p>{{ snapshot.error }}</p>
              <button type="button" @click="source.refresh">重试</button>
            </div>
          </div>

          <template v-if="projection.turns.length">
            <ConversationProcess
              v-for="turn in projection.turns"
              :key="turn.id"
              :turn="turn"
              :image-sources="projection.imageSources"
              :session-id="childSessionId"
              :workspace="childWorkspace"
              :assistant-id="streamingAssistantId"
              :read-only="true"
              :compact-task-prompt="true"
              @open-diff="(path, diff) => emit('openDiff', path, diff)"
              @open-diff-preview="(preview, trigger) => emit('openDiffPreview', preview, trigger)"
              @open-browser-result="emit('openBrowserResult', $event, childSessionId)"
              @open-sub-agent="openNestedSubAgent"
            />
          </template>
          <template v-else-if="!snapshot.loading">
            <details v-if="currentAgent.task" class="subagent-fallback-task">
              <summary>
                <ChevronRight :size="14" class="subagent-fallback-chevron" aria-hidden="true" />
                <span>任务指令</span>
              </summary>
              <div class="subagent-fallback-text">{{ currentAgent.task }}</div>
            </details>
            <div v-if="!snapshot.error" class="child-empty">
              <svg class="agent-emblem" viewBox="0 0 16 16" width="24" height="24" aria-hidden="true">
                <path :d="currentEmblem.path" :fill="currentEmblem.color" fill-rule="evenodd" />
              </svg>
              <span>{{ running ? '正在开始工作…' : '此子智能体暂无对话记录' }}</span>
            </div>
          </template>

          <div
            v-if="failure && !snapshot.messages.some(message => message.errorMessage === failure)"
            class="child-message-error"
            role="alert"
          >
            <CircleAlert :size="15" aria-hidden="true" />
            <span>{{ failure }}</span>
          </div>
        </div>
      </div>
    </template>
  </aside>
</template>

<style scoped>
.subagent-conversation-panel {
  position: relative;
  z-index: 6;
  display: flex;
  flex: 0 0 auto;
  width: min(var(--subagent-panel-width, 520px), 100%);
  min-width: 0;
  height: 100%;
  flex-direction: column;
  overflow: hidden;
  border-left: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
  container-type: inline-size;
}

.subagent-conversation-panel.is-resizing,
.subagent-conversation-panel.is-resizing * {
  user-select: none;
  transition: none !important;
}

.subagent-panel-resize-handle {
  position: absolute;
  z-index: 10;
  top: 0;
  bottom: 0;
  left: 0;
  width: 8px;
  cursor: col-resize;
}

.subagent-panel-resize-handle::after {
  position: absolute;
  top: 50%;
  left: 2px;
  width: 2px;
  height: 34px;
  content: '';
  background: var(--border-strong);
  border-radius: 2px;
  opacity: 0;
  transform: translateY(-50%);
  transition: opacity 140ms ease, background-color 140ms ease;
}

.subagent-panel-resize-handle:hover::after,
.subagent-conversation-panel.is-resizing .subagent-panel-resize-handle::after {
  background: var(--accent);
  opacity: 1;
}

.subagent-tab-strip {
  display: flex;
  flex: none;
  align-items: center;
  min-height: 48px;
  padding: 7px 14px;
  border-bottom: 1px solid var(--border);
  background: var(--surface);
}

.subagent-tab-pill {
  display: inline-flex;
  align-items: center;
  min-width: 176px;
  max-width: min(240px, 100%);
  height: 32px;
  padding: 0 4px 0 10px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--bg);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.04);
  color: var(--text);
}

.subagent-tab-main {
  display: inline-flex;
  flex: 1;
  min-width: 0;
  height: 100%;
  align-items: center;
  gap: 7px;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  user-select: none;
}

.subagent-tab-icon {
  flex: none;
  color: var(--text-secondary);
}

.subagent-tab-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.subagent-tab-close {
  display: grid;
  place-items: center;
  flex: none;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  transition: background 140ms ease, color 140ms ease;
}

.subagent-tab-close:hover {
  background: var(--surface-hover);
  color: var(--text);
}

.subagent-detail-header {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 46px;
  padding: 8px 18px;
  border-bottom: 1px solid color-mix(in srgb, var(--border) 75%, transparent);
}

.subagent-detail-title {
  display: flex;
  flex: 1;
  min-width: 0;
  align-items: center;
  gap: 8px;
}

.subagent-back-btn {
  display: grid;
  place-items: center;
  flex: none;
  width: 26px;
  height: 26px;
  margin-left: -4px;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background 140ms ease, color 140ms ease;
}

.subagent-back-btn:hover {
  background: var(--surface-hover);
  color: var(--text);
}

.agent-emblem {
  flex: none;
}

.subagent-detail-name {
  margin: 0;
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 14px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.subagent-detail-status {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex: none;
  font-size: 11.5px;
  color: var(--text-muted);
}

.subagent-detail-status.completed {
  color: var(--hl-addition-color);
}

.subagent-detail-status.failed {
  color: var(--danger);
}

.subagent-detail-model {
  flex: none;
  max-width: 42%;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 12.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.subagent-overview-scroll,
.subagent-detail-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
  overscroll-behavior: contain;
  scrollbar-gutter: stable;
}

.subagent-overview-scroll {
  padding: 18px 20px 28px;
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.subagent-overview-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.subagent-overview-heading {
  margin: 0;
  color: var(--text-muted);
  font-size: 13px;
  font-weight: 500;
}

.subagent-overview-empty {
  margin: 2px 0 0;
  color: var(--text-muted);
  font-size: 13.5px;
}

.subagent-overview-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.subagent-overview-row {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 38px;
  padding: 7px 8px;
  margin: 0 -8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text);
  font-size: 13.5px;
  text-align: left;
  cursor: pointer;
  transition: background 140ms ease;
}

.subagent-overview-row:hover {
  background: var(--surface-hover);
}

.subagent-overview-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.subagent-overview-status,
.subagent-overview-time {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex: none;
  color: var(--text-muted);
  font-size: 12.5px;
  white-space: nowrap;
}

.subagent-overview-badge {
  flex: none;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-hover);
  color: var(--text-secondary);
  font-size: 11px;
}

.subagent-overview-badge.failed {
  color: var(--danger);
}

.subagent-detail-scroll {
  padding: 8px 20px 32px;
}

.conversation-content.subagent-conversation-content {
  width: 100%;
  max-width: none;
  min-height: 0;
  margin: 0;
  padding: 0;
}

.subagent-conversation-content :deep(.message) {
  margin-bottom: 14px;
}

.subagent-conversation-content :deep(.message-user.is-compact-prompt) {
  justify-content: flex-start;
  margin-bottom: 10px;
}

.subagent-fallback-task {
  margin-bottom: 14px;
  color: var(--text-muted);
  font-size: 13px;
}

.subagent-fallback-task summary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 28px;
  list-style: none;
  cursor: pointer;
}

.subagent-fallback-task summary::-webkit-details-marker {
  display: none;
}

.subagent-fallback-chevron {
  flex: none;
  transition: transform 120ms ease;
}

.subagent-fallback-task[open] .subagent-fallback-chevron {
  transform: rotate(90deg);
}

.subagent-fallback-text {
  margin: 4px 0 8px 6px;
  padding: 6px 0 6px 14px;
  border-left: 1px solid var(--border);
  color: var(--text-secondary);
  line-height: 1.65;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.child-empty {
  display: flex;
  min-height: 140px;
  align-items: center;
  justify-content: center;
  flex-direction: column;
  gap: 12px;
  color: var(--text-muted);
  font-size: 12.5px;
}

.child-load-error {
  display: flex;
  gap: 9px;
  margin-bottom: 18px;
  color: var(--text-secondary);
  font-size: 12.5px;
  line-height: 1.65;
}

.child-load-error > svg {
  flex: none;
  margin-top: 2px;
  color: var(--danger);
}

.child-load-error strong {
  color: var(--text);
  font-weight: 500;
}

.child-load-error p {
  margin: 3px 0 7px;
  overflow-wrap: anywhere;
}

.child-load-error button {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text);
  text-decoration: underline;
  text-underline-offset: 3px;
  cursor: pointer;
}

.child-message-error {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  margin-top: 12px;
  color: var(--danger);
  font-size: 12.5px;
  line-height: 1.65;
  overflow-wrap: anywhere;
}

.child-message-error > svg {
  flex: none;
  margin-top: 3px;
}

.child-spin {
  animation: child-spin 1s linear infinite;
}

.subagent-conversation-panel :is(button):focus-visible,
.subagent-fallback-task summary:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

@keyframes child-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .child-spin,
  .subagent-fallback-chevron {
    animation: none;
    transition: none;
  }
}

@media (max-width: 760px) {
  .subagent-conversation-panel {
    position: absolute;
    inset: 0 0 0 auto;
    width: min(100%, 480px);
    max-width: 100%;
    z-index: 35;
    box-shadow: -12px 0 30px #00000014;
  }
}

@container (max-width: 360px) {
  .subagent-detail-header {
    padding-inline: 14px;
  }
  .subagent-detail-scroll,
  .subagent-overview-scroll {
    padding-inline: 14px;
  }
}
</style>
