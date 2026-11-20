<script setup lang="ts">
import { Check, ChevronRight, CircleAlert, LoaderCircle } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { messageMatches } from '../app-helpers'
import { summarizeConversationProcess, type ConversationProcessItem, type ConversationTurnProjection } from '../conversation-process'
import { animateReverseLineCollapse, captureCollapseSnapshot, type CollapseSnapshot, type ReverseCollapseController } from '../reverse-collapse'
import { isSubAgentTool, messageSubAgents } from '../subagent-activity'
import type { ChatMessage, SubAgentActivity } from '../types'
import ConversationMessage from './ConversationMessage.vue'
import SubAgentActivityList from './SubAgentActivityList.vue'
import { phaseStatusText, preparingToolLabel, toolLabel, toolTarget } from './tool-presentation'

const props = withDefaults(defineProps<{
  turn: ConversationTurnProjection
  assistantId?: string
  sessionId?: string
  workspace?: string
  phase?: string
  imageSources?: Record<string, string>
  query?: string
  /** Optional controlled state, retained by the virtualized conversation view. */
  expanded?: boolean
  /** Read-only rendering (e.g. sub-agent view) keeps copy actions while hiding mutations. */
  readOnly?: boolean
  /** Render the user turn prompt as a compact collapsible disclosure. */
  compactTaskPrompt?: boolean
}>(), { phase: '', imageSources: () => ({}), query: '', expanded: undefined, readOnly: false, compactTaskPrompt: false })

const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
  branch: [message: ChatMessage]
  retry: [message: ChatMessage]
  edit: [message: ChatMessage]
  openSubAgent: [activity: SubAgentActivity]
  'update:expanded': [expanded: boolean]
}>()

const uncontrolledExpanded = ref(false)
const isExpanded = computed(() => Boolean(props.query.trim()) || (props.expanded ?? uncontrolledExpanded.value))
const summary = computed(() => summarizeConversationProcess(props.turn.process))
function isSettledStatus(status: SubAgentActivity['status']): boolean {
  return status === 'completed' || status === 'failed' || status === 'cancelled' || status === 'interrupted'
}

function mergeLatestAgent(latest: Map<string, SubAgentActivity>, agent: SubAgentActivity): void {
  const existing = latest.get(agent.id)
  if (existing && isSettledStatus(existing.status)
    && !agent.live && (agent.status === 'running' || agent.status === 'starting')) {
    return
  }
  latest.set(agent.id, agent)
}

function visibleNonSubAgentTools(message: ChatMessage) {
  const represented = new Set([
    ...(message.subAgents ?? []),
    ...messageSubAgents(message)
  ].map((agent) => agent.sourceToolId).filter(Boolean))
  return (message.tools ?? []).filter((tool) => !represented.has(tool.id))
}

function isBareStep(item: ConversationProcessItem): boolean {
  const msg = item.message
  return !msg.content.trim()
    && !msg.thinking?.trim()
    && !msg.errorMessage?.trim()
    && !(msg.viewedImages?.length)
    && !(msg.images?.length)
    && !(msg.files?.length)
    && item.widgets.length === 0
}

function isPureToolStep(item: ConversationProcessItem, includeEvents: boolean): boolean {
  return isBareStep(item) && !(includeEvents && item.message.subAgentEvents?.length)
}

function isPureSubAgentStep(item: ConversationProcessItem, includeEvents: boolean): boolean {
  return isBareStep(item)
    && includeEvents
    && visibleNonSubAgentTools(item.message).length === 0
    && Boolean(item.message.subAgentEvents?.length)
}

const agents = computed(() => {
  const latest = new Map<string, SubAgentActivity>()
  const items = [...props.turn.process, ...(props.turn.response ? [props.turn.response] : [])]
  for (const item of items) {
    for (const agent of messageSubAgents(item.message)) mergeLatestAgent(latest, agent)
  }
  // A later status tool may contain an older launch snapshot. Actual lifecycle
  // events remain authoritative across the whole turn, as they are within a message.
  for (const item of items) {
    for (const agent of item.message.subAgents ?? []) mergeLatestAgent(latest, agent)
  }
  const representedTools = new Set([...latest.values()].filter((agent) => !agent.id.startsWith('tool:'))
    .map((agent) => agent.sourceToolId).filter(Boolean))
  for (const [id, agent] of latest) {
    if (id.startsWith('tool:') && agent.sourceToolId && representedTools.has(agent.sourceToolId)) latest.delete(id)
  }
  return [...latest.values()]
})
const allEvents = computed(() => {
  const seen = new Set<string>()
  const items = [...props.turn.process, ...(props.turn.response ? [props.turn.response] : [])]
  return items.flatMap((item) => item.message.subAgentEvents ?? []).filter((event) => {
    if (seen.has(event.id)) return false
    seen.add(event.id)
    return true
  })
})
function withFoldedResponseThinking(
  items: ConversationProcessItem[],
  response: ConversationProcessItem | null,
  running: boolean,
  hasProcess: boolean
): ConversationProcessItem[] {
  const thinking = response?.message.thinking?.trim()
  if (running || !hasProcess || !response || !thinking) return items
  return [
    ...items,
    {
      ...response,
      message: {
        id: `${response.message.id}:thinking`,
        role: 'assistant',
        content: '',
        thinking: response.message.thinking,
        createdAt: response.message.createdAt,
        state: 'done',
        thinkingActive: false
      },
      widgets: [],
      showFooter: false,
      isIntermediate: true
    }
  ]
}

const expandedProcessSteps = computed(() => {
  if (props.query.trim()) {
    return withFoldedResponseThinking(props.turn.process, props.turn.response, props.turn.running, props.turn.process.length > 0)
  }
  const includeEvents = allEvents.value.length > 0
  const result: ConversationProcessItem[] = []
  let lastKind: 'none' | 'tool' | 'subagent' = 'none'
  for (const item of props.turn.process) {
    const activeStep = isStreaming(item.message) || Boolean(item.message.preparingTool)
    if (isPureToolStep(item, includeEvents) && !activeStep) {
      const tools = visibleNonSubAgentTools(item.message)
      if (tools.length === 0) continue
      if (lastKind === 'tool' && result.length > 0) {
        const prev = result[result.length - 1]!
        result[result.length - 1] = {
          ...prev,
          message: {
            ...prev.message,
            tools: [...(prev.message.tools ?? []), ...tools],
            subAgents: undefined,
            subAgentEvents: undefined
          }
        }
      } else {
        result.push({
          ...item,
          message: {
            ...item.message,
            tools,
            subAgents: undefined,
            subAgentEvents: undefined
          }
        })
        lastKind = 'tool'
      }
    } else if (isPureSubAgentStep(item, includeEvents) && !activeStep) {
      const events = item.message.subAgentEvents ?? []
      if (lastKind === 'subagent' && result.length > 0) {
        const prev = result[result.length - 1]!
        const mergedEvents = [...(prev.message.subAgentEvents ?? [])]
        const seen = new Set(mergedEvents.map((e) => e.id))
        for (const ev of events) {
          if (!seen.has(ev.id)) {
            seen.add(ev.id)
            mergedEvents.push(ev)
          }
        }
        mergedEvents.sort((a, b) => a.createdAt - b.createdAt)
        const mergedAgents = new Map((prev.message.subAgents ?? []).map((a) => [a.id, a]))
        for (const a of item.message.subAgents ?? []) mergedAgents.set(a.id, a)
        result[result.length - 1] = {
          ...prev,
          message: {
            ...prev.message,
            subAgents: [...mergedAgents.values()],
            subAgentEvents: mergedEvents
          }
        }
      } else {
        result.push({
          ...item,
          message: {
            ...item.message,
            tools: []
          }
        })
        lastKind = 'subagent'
      }
    } else {
      result.push(item)
      lastKind = 'none'
    }
  }
  return withFoldedResponseThinking(result, props.turn.response, props.turn.running, props.turn.process.length > 0)
})
const responseEvents = computed(() => {
  if (!props.turn.response) return []
  const seen = new Set(props.turn.process.flatMap((item) => (item.message.subAgentEvents ?? []).map((event) => event.id)))
  return (props.turn.response.message.subAgentEvents ?? []).filter((event) => {
    if (seen.has(event.id)) return false
    seen.add(event.id)
    return true
  })
})
const hasLiveSubAgents = computed(() =>
  agents.value.some((agent) => agent.status === 'starting' || (agent.status === 'running' && agent.live))
)
const showSubAgentsOutside = computed(() =>
  !isExpanded.value
  && (!props.turn.running || props.turn.process.length === 0)
  && (props.turn.running || hasLiveSubAgents.value)
  && (agents.value.length > 0 || allEvents.value.length > 0)
)
const completedAt = ref<number | null>(null)
function formatTurnDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 500) return ''
  const totalSeconds = Math.max(1, Math.round(ms / 1000))
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  if (hours > 0) return `用时 ${hours}h ${minutes}m ${seconds}s`
  if (minutes > 0) return `用时 ${minutes}m ${seconds}s`
  return `用时 ${seconds}s`
}
const turnDurationLabel = computed(() => {
  if (props.turn.running) return ''
  const timestamps: number[] = []
  const pushTs = (value?: number | null) => {
    if (typeof value === 'number' && Number.isFinite(value) && value > 0) timestamps.push(value)
  }
  pushTs(props.turn.user?.message.createdAt)
  for (const item of props.turn.process) pushTs(item.message.createdAt)
  pushTs(props.turn.response?.message.createdAt)
  for (const ev of allEvents.value) pushTs(ev.createdAt)
  if (completedAt.value && timestamps.length > 0) pushTs(completedAt.value)
  if (timestamps.length < 2) return ''
  return formatTurnDuration(Math.max(...timestamps) - Math.min(...timestamps))
})
const status = computed(() => {
  const process = summary.value
  if (props.turn.running) {
    if (process.activeTool && !isSubAgentTool(process.activeTool)) {
      return `${toolLabel(process.activeTool)} ${toolTarget(process.activeTool)}`.trim()
    }
    const latest = process.latestMessage
    if (latest?.preparingTool) {
      return preparingToolLabel(latest.preparingTool).replace(/…$/, '')
    }
    if (props.turn.response) {
      return props.turn.response.message.preparingTool
        ? preparingToolLabel(props.turn.response.message.preparingTool).replace(/…$/, '')
        : '正在生成回答'
    }
    const latestThinking = Boolean(
      latest?.state === 'thinking'
      && (latest.thinkingActive ?? true)
    )
    if (latestThinking || hasLiveSubAgents.value) {
      return '正在思考'
    }
    const p = phaseStatusText(props.phase)
    if (p) return p
    return '正在等待模型响应'
  }
  return turnDurationLabel.value
})
const isThinkingStatus = computed(() => props.turn.running && status.value === '正在思考')
const expandedStepsHaveThinkingShimmer = computed(() =>
  expandedProcessSteps.value.some((item) => {
    const msg = item.message
    return isStreaming(msg)
      && msg.state === 'thinking'
      && !msg.preparingTool
      && (msg.thinkingActive ?? true)
  })
)
const hasLiveStepIndicator = computed(() =>
  expandedProcessSteps.value.some((item) => {
    const msg = item.message
    if (msg.preparingTool) return true
    if ((msg.tools ?? []).some((tool) => !isSubAgentTool(tool) && tool.state === 'running')) return true
    if (isStreaming(msg) && (msg.state === 'thinking' || msg.state === 'streaming') && !msg.content.trim()) return true
    return false
  })
)
const tailWaitingLabel = computed(() => {
  const p = phaseStatusText(props.phase)
  return p ? (p.endsWith('…') ? p : `${p}…`) : '正在等待模型响应…'
})

const liveProcessRef = ref<HTMLElement | null>(null)
const wipeHostRef = ref<HTMLElement | null>(null)
const detailsEntering = ref(false)
let activeWipe: ReverseCollapseController | null = null
let enteringTimer: ReturnType<typeof setTimeout> | undefined
let mounted = false

interface TurnVisualSignature {
  hasDetails: boolean
  isRunning: boolean
  isExpanded: boolean
  responseId: string | null
  processMessageIds: string[]
}

function currentVisualSignature(): TurnVisualSignature {
  return {
    hasDetails: props.turn.process.length > 0,
    isRunning: props.turn.running,
    isExpanded: isExpanded.value,
    responseId: props.turn.response?.message.id ?? null,
    processMessageIds: props.turn.process.map((item) => item.message.id)
  }
}

let lastSignature: TurnVisualSignature | null = null

function cancelActiveWipe(): void {
  activeWipe?.cancel()
  activeWipe = null
}

onMounted(() => {
  mounted = true
  lastSignature = currentVisualSignature()
})

onBeforeUnmount(() => {
  mounted = false
  cancelActiveWipe()
  if (enteringTimer) clearTimeout(enteringTimer)
})

watch(() => props.turn.running, (running, prevRunning) => {
  if (running) {
    completedAt.value = null
  } else if (prevRunning) {
    completedAt.value = Date.now()
  }
}, { flush: 'sync' })

watch(currentVisualSignature, (nextSig) => {
  const prevSig = lastSignature
  lastSignature = nextSig
  if (!mounted || !prevSig) return

  if (nextSig.isExpanded || nextSig.isRunning) {
    cancelActiveWipe()
    return
  }

  if (prevSig.isRunning && !nextSig.isRunning && nextSig.hasDetails) {
    const snapshot: CollapseSnapshot | null = captureCollapseSnapshot(liveProcessRef.value)
    if (!snapshot) return

    detailsEntering.value = true
    if (enteringTimer) clearTimeout(enteringTimer)
    enteringTimer = setTimeout(() => {
      detailsEntering.value = false
    }, 260)

    void nextTick(() => {
      if (!mounted || isExpanded.value || props.turn.running) return
      cancelActiveWipe()
      activeWipe = animateReverseLineCollapse(wipeHostRef.value, snapshot, 500)
    })
  }
}, { flush: 'pre' })

function isDimmed(message: ChatMessage): boolean {
  return Boolean(props.query.trim()) && !messageMatches(message, props.query)
}
function isStreaming(message: ChatMessage): boolean {
  return props.turn.running && message.id === props.assistantId
}
function onToggle(event: Event): void {
  const details = event.currentTarget as HTMLDetailsElement
  // Search reveals history temporarily without overwriting the user's preference.
  if (props.query.trim()) {
    if (!details.open) details.open = true
    return
  }
  if (details.open === (props.expanded ?? uncontrolledExpanded.value)) return
  uncontrolledExpanded.value = details.open
  emit('update:expanded', details.open)
}
</script>

<template>
  <div class="conversation-turn" :data-turn-id="turn.id">
    <ConversationMessage v-if="turn.user" :message="turn.user.message" :image-sources="imageSources"
      :widgets="turn.user.widgets" :session-id="sessionId" :workspace="workspace"
      :show-footer="turn.user.showFooter" :is-intermediate="turn.user.isIntermediate"
      :read-only="readOnly" :compact-task-prompt="compactTaskPrompt" :dimmed="isDimmed(turn.user.message)"
      @open-diff="(path, diff) => emit('openDiff', path, diff)" @branch="emit('branch', $event)" @edit="emit('edit', $event)" />

    <template v-if="turn.process.length">
      <div v-if="turn.running" ref="liveProcessRef" class="process-live-timeline">
        <div v-for="item in expandedProcessSteps" :key="item.message.id" class="process-step process-live-step">
          <ConversationMessage :message="item.message" :image-sources="imageSources" :widgets="item.widgets"
            :session-id="sessionId" :workspace="workspace" :phase="phase" :streaming="isStreaming(item.message)"
            :smooth-output="!query.trim()" :show-footer="item.showFooter" :is-intermediate="item.isIntermediate"
            :hide-sub-agents="allEvents.length === 0" :show-sub-agent-summary="false" :read-only="readOnly"
            :dimmed="isDimmed(item.message)" @open-diff="(path, diff) => emit('openDiff', path, diff)"
            @branch="emit('branch', $event)" @retry="emit('retry', $event)" @edit="emit('edit', $event)"
            @open-sub-agent="emit('openSubAgent', $event)" />
        </div>
        <SubAgentActivityList v-if="allEvents.length === 0 && agents.length > 0" :agents="agents"
          @open-sub-agent="emit('openSubAgent', $event)" />
        <SubAgentActivityList v-if="responseEvents.length" :agents="agents" :events="responseEvents" :show-summary="false"
          @open-sub-agent="emit('openSubAgent', $event)" />
        <div v-if="!turn.response && hasLiveSubAgents && !expandedStepsHaveThinkingShimmer" class="process-thinking-row">
          <span class="thinking-shimmer">正在思考</span>
        </div>
        <div v-else-if="!turn.response && !hasLiveSubAgents && !hasLiveStepIndicator"
          class="process-tail-status" role="status" aria-live="polite">
          <LoaderCircle :size="14" class="spin process-state" aria-hidden="true" />
          <span>{{ tailWaitingLabel }}</span>
        </div>
      </div>

      <details v-else class="conversation-process" :class="{ 'is-entering': detailsEntering }" :open="isExpanded" @toggle="onToggle">
        <summary :title="status ? `已完成工作 ${status}` : '已完成工作'">
          <ChevronRight :size="14" class="process-chevron" aria-hidden="true" />
          <CircleAlert v-if="summary.errorCount" :size="14" class="process-state is-error" aria-label="工作过程包含异常" />
          <Check v-else :size="14" class="process-state" aria-hidden="true" />
          <span class="process-title">已完成工作</span>
          <span v-if="status" class="process-description">{{ status }}</span>
        </summary>
        <div v-if="isExpanded" class="process-history">
          <div v-for="item in expandedProcessSteps" :key="item.message.id" class="process-step">
            <ConversationMessage :message="item.message" :image-sources="imageSources" :widgets="item.widgets"
              :session-id="sessionId" :workspace="workspace" :phase="phase" :streaming="isStreaming(item.message)"
              :smooth-output="!query.trim()" :show-footer="item.showFooter" :is-intermediate="item.isIntermediate"
              :hide-sub-agents="allEvents.length === 0" :show-sub-agent-summary="false" :read-only="readOnly"
              :dimmed="isDimmed(item.message)" @open-diff="(path, diff) => emit('openDiff', path, diff)"
              @branch="emit('branch', $event)" @retry="emit('retry', $event)" @edit="emit('edit', $event)"
              @open-sub-agent="emit('openSubAgent', $event)" />
          </div>
          <SubAgentActivityList v-if="allEvents.length === 0 && agents.length > 0" :agents="agents"
            @open-sub-agent="emit('openSubAgent', $event)" />
          <SubAgentActivityList v-if="responseEvents.length" :agents="agents" :events="responseEvents" :show-summary="false"
            @open-sub-agent="emit('openSubAgent', $event)" />
        </div>
      </details>
    </template>

    <div ref="wipeHostRef" class="collapse-wipe-host" aria-hidden="true" />

    <SubAgentActivityList v-if="showSubAgentsOutside" :agents="agents" :events="allEvents"
      @open-sub-agent="emit('openSubAgent', $event)" />

    <div v-if="turn.response" class="turn-response-host">
      <ConversationMessage :message="turn.response.message" :image-sources="imageSources"
        :widgets="turn.response.widgets" :session-id="sessionId" :workspace="workspace" :phase="phase"
        :streaming="isStreaming(turn.response.message)" :show-footer="turn.response.showFooter"
        :smooth-output="!query.trim()" :read-only="readOnly" :hide-thinking="!turn.running && turn.process.length > 0"
        :is-intermediate="turn.response.isIntermediate" :hide-sub-agents="true" :dimmed="isDimmed(turn.response.message)"
        @open-diff="(path, diff) => emit('openDiff', path, diff)" @branch="emit('branch', $event)" @retry="emit('retry', $event)" @edit="emit('edit', $event)"
        @open-sub-agent="emit('openSubAgent', $event)" />
    </div>
  </div>
</template>

<style scoped>
.conversation-turn { display: flow-root; min-width: 0; }
.process-live-timeline { display: grid; gap: 4px; min-width: 0; margin: 0 0 10px; }
.process-thinking-row { margin: 2px 0 4px; font-size: 13.5px; line-height: 1.5; }
.conversation-process { min-width: 0; margin: 0 0 12px; color: var(--text-muted); interpolate-size: allow-keywords; }
.conversation-process:has(+ .collapse-wipe-host:not(:empty)) { margin-bottom: 4px; }
.conversation-process.is-entering { animation: process-header-enter 220ms cubic-bezier(0.22, 1, 0.36, 1) both; }
@keyframes process-header-enter { from { opacity: 0; transform: translateY(3px); } to { opacity: 1; transform: translateY(0); } }
.conversation-process > summary { display: flex; align-items: center; gap: 7px; min-height: 28px; max-width: 100%; padding: 2px 0; border-radius: 6px; cursor: pointer; list-style: none; font-size: 13px; line-height: 1.45; }
.conversation-process > summary::-webkit-details-marker { display: none; }
.conversation-process > summary:hover, .conversation-process[open] > summary { color: var(--text-secondary); }
.conversation-process > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.process-chevron, .process-state { flex-shrink: 0; }
.process-chevron { transition: transform 120ms ease; }
.conversation-process[open] > summary .process-chevron { transform: rotate(90deg); }
.process-title { flex-shrink: 0; }
.process-state.is-error { color: var(--danger); }
.process-description { min-width: 0; overflow: hidden; color: var(--text-muted); text-overflow: ellipsis; white-space: nowrap; font-size: 12.5px; }
.process-history { display: grid; gap: 2px; min-width: 0; margin: 4px 0 8px 6px; padding: 2px 0 2px 14px; border-left: 1px solid var(--border); overflow: clip; block-size: auto; opacity: 1; transition: block-size 220ms cubic-bezier(0.22, 1, 0.36, 1), opacity 180ms ease; }
@starting-style { .conversation-process[open] > .process-history { block-size: 0; opacity: 0; } }
.process-step { display: flow-root; min-width: 0; }
.process-step :deep(.message) { margin-bottom: 0; }
.process-history .process-step :deep(.assistant-message) { font-size: 13.5px; line-height: 1.6; color: var(--text-secondary); }
.process-history .process-step :deep(.markdown-body) { font-size: 13.5px; line-height: 1.6; color: var(--text-secondary); }
.process-step :deep(.markdown-body p) { margin: 2px 0 4px; }
.process-step :deep(.tool-list) { margin: 0; }
.process-step :deep(.markdown-body + .tool-list) { margin-top: 4px; }
.process-tail-status { display: flex; align-items: center; gap: 7px; min-height: 26px; padding: 2px 0; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.turn-response-host { display: flow-root; min-width: 0; }
.collapse-wipe-host { display: flow-root; min-width: 0; }
.collapse-wipe-host :deep(.collapse-wipe-shell) { overflow: hidden; margin-bottom: 8px; pointer-events: none; will-change: height, opacity; }
.collapse-wipe-host :deep(.collapse-wipe-body) { display: flow-root; width: 100%; }
@media (prefers-reduced-motion: reduce) {
  .process-chevron, .process-history { transition: none; }
  .conversation-process.is-entering { animation: none; }
}
</style>
