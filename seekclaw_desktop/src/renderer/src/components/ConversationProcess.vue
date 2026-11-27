<script setup lang="ts">
import { Check, ChevronRight, CircleAlert, ScrollText } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { messageMatches } from '../app-helpers'
import { collectTurnDiff, type ToolDiffPreview } from '../conversation-diffs'
import { type ConversationProcessItem, type ConversationTurnProjection } from '../conversation-process'
import { animateReverseLineCollapse, captureCollapseSnapshot, type CollapseSnapshot, type ReverseCollapseController } from '../reverse-collapse'
import { messageSubAgents } from '../subagent-activity'
import { extractThinkingHeading } from '../thinking-status'
import type { ChatMessage, ImageReference, SubAgentActivity, SubAgentLifecycleEvent, ToolActivity } from '../types'
import ConversationMessage from './ConversationMessage.vue'
import ConversationWidgets from './ConversationWidgets.vue'
import ProcessActivityGroup from './ProcessActivityGroup.vue'
import SubAgentActivityList from './SubAgentActivityList.vue'
import ToolCallRow from './ToolCallRow.vue'
import ViewedImagesActivity from './ViewedImagesActivity.vue'
import { phaseStatusText, preparingToolLabel, toolDisplayState } from './tool-presentation'

const props = withDefaults(defineProps<{
  turn: ConversationTurnProjection
  assistantId?: string
  sessionId?: string
  workspace?: string
  phase?: string
  /** Approval and clarification panels already indicate that the turn is paused. */
  awaitingUser?: boolean
  approvalCallIds?: ReadonlySet<string>
  activeApprovalCallId?: string
  imageSources?: Record<string, string>
  query?: string
  /** Optional controlled state, retained by the virtualized conversation view. */
  expanded?: boolean
  /** Open activity disclosures, retained when a virtualized turn leaves the viewport. */
  activityDisclosures?: ReadonlySet<string>
  /** Read-only rendering (e.g. sub-agent view) keeps copy actions while hiding mutations. */
  readOnly?: boolean
  /** Render the user turn prompt as a compact collapsible disclosure. */
  compactTaskPrompt?: boolean
}>(), { phase: '', imageSources: () => ({}), query: '', expanded: undefined, readOnly: false, compactTaskPrompt: false })

const emit = defineEmits<{
  openDiffPreview: [preview: ToolDiffPreview, trigger: HTMLButtonElement]
  openDiff: [filePath: string, diff: string]
  branch: [message: ChatMessage]
  retry: [message: ChatMessage]
  edit: [message: ChatMessage]
  openSubAgent: [activity: SubAgentActivity]
  openBrowserResult: [tool: ToolActivity]
  'update:expanded': [expanded: boolean]
  setActivityDisclosure: [id: string, expanded: boolean]
}>()

const uncontrolledExpanded = ref(false)
const localActivityDisclosures = ref(new Set<string>())
const activityDisclosures = computed(() => props.activityDisclosures ?? localActivityDisclosures.value)
function setActivityDisclosure(id: string, expanded: boolean): void {
  if (props.activityDisclosures === undefined) {
    if (expanded) localActivityDisclosures.value.add(id)
    else localActivityDisclosures.value.delete(id)
  }
  emit('setActivityDisclosure', id, expanded)
}
const isExpanded = computed(() => Boolean(props.query.trim()) || (props.expanded ?? uncontrolledExpanded.value))
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

const turnDiff = computed(() => {
  if (props.turn.running) return null
  const items = props.turn.response ? [...props.turn.process, props.turn.response] : props.turn.process
  return collectTurnDiff(items.flatMap(({ message }) => message.tools ?? []))
})

const responseTools = computed(() => props.turn.response ? visibleNonSubAgentTools(props.turn.response.message) : [])
const hasProcessError = computed(() => props.turn.process.some(({ message }) =>
  Boolean(message.errorMessage?.trim()) || message.tools?.some((tool) => toolDisplayState(tool) === 'error')
) || responseTools.value.some((tool) => toolDisplayState(tool) === 'error'))

function hasVisibleMessage(item: ConversationProcessItem): boolean {
  const message = item.message
  return Boolean(message.content.trim() || message.errorMessage?.trim()
    || message.images?.length || message.files?.length || item.widgets.length)
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
type ActivityStep = { kind: 'activity'; id: string; tools: ToolActivity[]; reasoning: string[] }
type MessageStep = { kind: 'message'; id: string; item: ConversationProcessItem }
type LifecycleStep = { kind: 'lifecycle'; id: string; events: SubAgentLifecycleEvent[] }
type ProcessStep = ActivityStep | MessageStep | LifecycleStep
  | { kind: 'viewed-images'; id: string; images: ImageReference[] }
  | { kind: 'context-compacted'; id: string }

const processSteps = computed(() => {
  const steps: ProcessStep[] = []
  let activity: ActivityStep | undefined
  let lastActivity: ActivityStep | undefined
  let anchor = props.turn.id
  let pendingReasoning: string[] = []
  const seenEvents = new Set<string>()

  function startSegmentAfter(id: string): void {
    anchor = id
    activity = undefined
  }

  function addReasoning(message: ChatMessage): void {
    const reasoning = message.thinking?.trim()
    if (reasoning && (!isStreaming(message) || message.thinkingActive === false)) {
      (activity?.reasoning ?? pendingReasoning).push(reasoning)
    }
  }

  function addTool(tool: ToolActivity): void {
    if (!activity) {
      // Use the section boundary as the key. Updates to a call's result and
      // additional calls in this section keep the same disclosure state.
      activity = { kind: 'activity', id: `activity:${anchor}`, tools: [], reasoning: pendingReasoning }
      pendingReasoning = []
      lastActivity = activity
      steps.push(activity)
    }
    if (!props.turn.running && tool.state === 'running') {
      // A stopped turn can end without a corresponding tool_done event.
      activity.tools.push({ ...tool, state: 'interrupted' })
    } else {
      activity.tools.push(tool)
    }
  }

  function addLifecycle(events: SubAgentLifecycleEvent[] | undefined): void {
    if (!events?.length) return
    const previous = steps.at(-1)
    if (previous?.kind === 'lifecycle') {
      previous.events.push(...events)
      return
    }
    const id = `lifecycle:${events[0]!.id}`
    steps.push({ kind: 'lifecycle', id, events })
    startSegmentAfter(id)
  }

  function addMessageActivities(message: ChatMessage, tools: ToolActivity[], response = false): void {
    addReasoning(message)
    if (!message.subAgentEvents?.length) {
      for (const tool of tools) addTool(tool)
      return
    }

    const allTools = message.tools ?? []
    const visibleTools = new Set(tools)
    const eventsAtTool = new Map<number, SubAgentLifecycleEvent[]>()
    let lastIndex = -1
    for (const event of message.subAgentEvents) {
      if (seenEvents.has(event.id)) continue
      seenEvents.add(event.id)
      const toolId = event.afterToolId === undefined && event.kind === 'started'
        ? event.agent.sourceToolId : event.afterToolId
      const index = toolId ? allTools.findIndex((tool) => tool.id === toolId || tool.callId === toolId) : -1
      // Old responses appended events after their tools without recording an anchor.
      const fallbackIndex = response && event.afterToolId === undefined ? allTools.length - 1 : -1
      lastIndex = Math.max(lastIndex, index < 0 ? fallbackIndex : index)
      const events = eventsAtTool.get(lastIndex) ?? []
      events.push(event)
      eventsAtTool.set(lastIndex, events)
    }
    addLifecycle(eventsAtTool.get(-1))
    allTools.forEach((tool, index) => {
      if (visibleTools.has(tool)) addTool(tool)
      addLifecycle(eventsAtTool.get(index))
    })
  }

  // The runtime records these events immediately before invoking the model.
  function addModelNotices(message: ChatMessage): void {
    if (message.contextCompacted) {
      const id = `context-compacted:${message.id}`
      steps.push({ kind: 'context-compacted', id })
      startSegmentAfter(id)
    }
    if (message.viewedImages?.length) {
      const id = `viewed-images:${message.id}`
      steps.push({ kind: 'viewed-images', id, images: message.viewedImages })
      startSegmentAfter(id)
    }
  }

  for (const item of props.turn.process) {
    addModelNotices(item.message)
    if (hasVisibleMessage(item)) {
      const id = `message:${item.message.id}`
      steps.push({ kind: 'message', id, item })
      startSegmentAfter(id)
    }
    addMessageActivities(item.message, visibleNonSubAgentTools(item.message))
  }

  if (props.turn.response) {
    const message = props.turn.response.message
    addModelNotices(message)
    // Older sessions can persist a final answer in a message that also has tools.
    addMessageActivities(message, responseTools.value, true)
  }

  if (!props.turn.running && pendingReasoning.length) {
    if (lastActivity) {
      lastActivity.reasoning.push(...pendingReasoning)
    } else {
      steps.push({ kind: 'activity', id: `reasoning:${props.turn.id}`, tools: [], reasoning: pendingReasoning })
    }
  }
  return steps
})
function awaitsApproval(tool: ToolActivity): boolean {
  return props.approvalCallIds?.has(tool.callId ?? tool.id) === true
}

function setSingleToolDisclosure(step: ActivityStep, tool: ToolActivity, expanded: boolean): void {
  // Opening a standalone tool also opens its group if another tool joins.
  setActivityDisclosure(step.id, expanded)
  setActivityDisclosure(`tool:${tool.id}`, expanded)
}

const hasHistory = computed(() => processSteps.value.length > 0 || agents.value.length > 0)
const hasLiveSubAgents = computed(() =>
  agents.value.some((agent) => agent.status === 'starting' || (agent.status === 'running' && agent.live))
)
const showSubAgentsOutside = computed(() => !props.turn.running && !isExpanded.value && hasLiveSubAgents.value)
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
const liveStatus = computed(() => {
  if (!props.turn.running || props.awaitingUser) return null
  const messages = [...props.turn.process, ...(props.turn.response ? [props.turn.response] : [])]
    .map((item) => item.message)
  const current = messages.findLast((message) => message.id === props.assistantId) ?? messages.at(-1)
  if (current?.errorMessage?.trim()) return null
  // Active operations already have dedicated rows.
  if (processSteps.value.some((step) => step.kind === 'activity'
    && step.tools.some((tool) => tool.state === 'running'))) return null
  const p = phaseStatusText(props.phase)
  if (p) return { label: p, thinking: false }
  if (current?.preparingTool) return { label: preparingToolLabel(current.preparingTool), thinking: false }
  if (current?.state === 'thinking' && (current.thinkingActive ?? true)) {
    return { label: extractThinkingHeading(current.thinking) ?? '正在思考', thinking: true }
  }
  if (props.turn.response && current?.id === props.turn.response.message.id && current.content.trim()) return null
  return { label: '正在思考', thinking: true }
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
}

function currentVisualSignature(): TurnVisualSignature {
  return {
    hasDetails: hasHistory.value,
    isRunning: props.turn.running,
    isExpanded: isExpanded.value
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
  if (props.turn.running || event.target !== event.currentTarget) return
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

    <details v-if="hasHistory" :open="turn.running || isExpanded"
      :class="turn.running ? 'process-live-timeline' : ['conversation-process', { 'is-entering': detailsEntering }]"
      @toggle="onToggle">
      <summary v-show="!turn.running" :title="turn.running ? undefined : turnDurationLabel || '已完成工作'">
        <template v-if="!turn.running">
          <CircleAlert v-if="hasProcessError" :size="14" class="process-state is-error" aria-label="工作过程包含异常" />
          <Check v-else :size="14" class="process-state" aria-hidden="true" />
          <span class="process-title">已完成工作</span>
          <span v-if="turnDurationLabel" class="process-description">{{ turnDurationLabel }}</span>
          <ChevronRight :size="14" class="process-chevron" aria-hidden="true" />
        </template>
      </summary>
      <div v-if="turn.running || isExpanded" ref="liveProcessRef" class="process-timeline"
        :class="{ 'process-history': !turn.running }">
        <div v-for="step in processSteps" :key="step.id" class="process-step">
          <template v-if="step.kind === 'activity'">
            <ToolCallRow v-if="turn.running && step.tools.length === 1" :tool="step.tools[0]!"
              :awaiting-approval="awaitsApproval(step.tools[0]!)"
              :expanded="activityDisclosures.has(`tool:${step.tools[0]!.id}`)"
              @update:expanded="setSingleToolDisclosure(step, step.tools[0]!, $event)"
              @open-diff="(path, diff) => emit('openDiff', path, diff)" @open-browser-result="emit('openBrowserResult', $event)"
              @open-sub-agent="emit('openSubAgent', $event)" />
            <ProcessActivityGroup v-else :tools="step.tools" :reasoning="turn.running ? undefined : step.reasoning"
              :expanded="activityDisclosures.has(step.id)" :activity-disclosures="activityDisclosures"
              :approval-call-ids="turn.running ? approvalCallIds : undefined" :active-approval-call-id="activeApprovalCallId"
              @update:expanded="setActivityDisclosure(step.id, $event)" @set-activity-disclosure="setActivityDisclosure"
              @open-diff="(path, diff) => emit('openDiff', path, diff)" @open-browser-result="emit('openBrowserResult', $event)"
              @open-sub-agent="emit('openSubAgent', $event)" />
          </template>
          <ViewedImagesActivity v-else-if="step.kind === 'viewed-images'" :images="step.images" :image-sources="imageSources"
            :expanded="activityDisclosures.has(step.id)" @update:expanded="setActivityDisclosure(step.id, $event)" />
          <div v-else-if="step.kind === 'context-compacted'" class="process-notice">
            <ScrollText :size="14" aria-hidden="true" />
            <span>上下文已自动压缩</span>
          </div>
          <SubAgentActivityList v-else-if="step.kind === 'lifecycle'" :agents="agents" :events="step.events"
            :show-summary="false" @open-sub-agent="emit('openSubAgent', $event)" />
          <ConversationMessage v-else :message="step.item.message" :image-sources="imageSources" :widgets="step.item.widgets"
            :session-id="sessionId" :workspace="workspace" :streaming="isStreaming(step.item.message)"
            :smooth-output="!query.trim()" :show-footer="step.item.showFooter" :is-intermediate="step.item.isIntermediate"
            progress hide-tools hide-sub-agents :read-only="readOnly"
            :dimmed="isDimmed(step.item.message)" @open-diff="(path, diff) => emit('openDiff', path, diff)"
            @branch="emit('branch', $event)" @retry="emit('retry', $event)" @edit="emit('edit', $event)"
            @open-sub-agent="emit('openSubAgent', $event)" />
        </div>
        <SubAgentActivityList v-if="allEvents.length === 0 && agents.length > 0" :agents="agents"
          @open-sub-agent="emit('openSubAgent', $event)" />
      </div>
    </details>

    <div ref="wipeHostRef" class="collapse-wipe-host" aria-hidden="true" />

    <SubAgentActivityList v-if="showSubAgentsOutside" :agents="agents" :events="allEvents"
      @open-sub-agent="emit('openSubAgent', $event)" />

    <div v-if="turn.response" class="turn-response-host">
      <ConversationMessage :message="turn.response.message" :image-sources="imageSources"
        :widgets="turn.response.widgets" :turn-diff="turnDiff" :session-id="sessionId" :workspace="workspace"
        :streaming="isStreaming(turn.response.message)" :show-footer="turn.response.showFooter"
        :smooth-output="!query.trim()" :read-only="readOnly"
        hide-tools
        :is-intermediate="turn.response.isIntermediate" :hide-sub-agents="true" :dimmed="isDimmed(turn.response.message)"
        @open-diff="(path, diff) => emit('openDiff', path, diff)" @branch="emit('branch', $event)" @retry="emit('retry', $event)" @edit="emit('edit', $event)"
        @open-diff-preview="(preview, trigger) => emit('openDiffPreview', preview, trigger)"
        @open-sub-agent="emit('openSubAgent', $event)" />
    </div>
    <ConversationWidgets v-else-if="turnDiff" :widgets="[]" :diff="turnDiff"
      @open-diff-preview="(preview, trigger) => emit('openDiffPreview', preview, trigger)" />
    <div v-if="liveStatus" class="process-tail-status" :class="{ 'process-thinking-row': liveStatus.thinking }"
      role="status" aria-live="polite">
      <span :class="{ 'thinking-shimmer': liveStatus.thinking }">{{ liveStatus.label }}</span>
    </div>
  </div>
</template>

<style scoped>
.conversation-turn { display: flow-root; min-width: 0; }
.process-live-timeline { min-width: 0; margin: 0 0 10px; }
.process-timeline { display: grid; grid-template-columns: minmax(0, 1fr); gap: 3px; min-width: 0; }
.conversation-process { min-width: 0; margin: 0 0 12px; color: var(--text-muted); interpolate-size: allow-keywords; }
.conversation-process:has(+ .collapse-wipe-host:not(:empty)) { margin-bottom: 4px; }
.conversation-process.is-entering { animation: process-header-enter 220ms cubic-bezier(0.22, 1, 0.36, 1) both; }
@keyframes process-header-enter { from { opacity: 0; transform: translateY(3px); } to { opacity: 1; transform: translateY(0); } }
.conversation-process > summary { display: flex; align-items: center; gap: 8px; min-height: 27px; width: fit-content; max-width: 100%; padding: 2px 0; border-radius: 5px; cursor: pointer; list-style: none; font-size: 13px; line-height: 1.45; }
.conversation-process > summary::-webkit-details-marker { display: none; }
.conversation-process > summary:hover, .conversation-process[open] > summary { color: var(--text-secondary); }
.conversation-process > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.process-chevron, .process-state { flex-shrink: 0; }
.process-chevron { transition: transform 120ms ease; }
.conversation-process[open] > summary .process-chevron { transform: rotate(90deg); }
.process-title { flex-shrink: 0; font-weight: 550; }
.process-state.is-error { color: var(--danger); }
.process-description { min-width: 0; overflow: hidden; color: var(--text-muted); text-overflow: ellipsis; white-space: nowrap; font-size: 12.5px; }
.process-history { gap: 2px; margin: 4px 0 8px 6px; padding: 2px 0 2px 14px; border-left: 1px solid var(--border); overflow: clip; block-size: auto; opacity: 1; transition: block-size 220ms cubic-bezier(0.22, 1, 0.36, 1), opacity 180ms ease; }
@starting-style { .conversation-process[open] > .process-history { block-size: 0; opacity: 0; } }
.process-step { display: flow-root; min-width: 0; }
.process-notice { display: flex; align-items: center; gap: 8px; min-height: 27px; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.process-tail-status { display: flex; align-items: center; min-height: 27px; padding: 1px 0; margin: 4px 0; color: var(--text-muted); font-size: 14px; line-height: 1.5; }
.process-thinking-row > span { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.turn-response-host { display: flow-root; min-width: 0; }
.collapse-wipe-host { display: flow-root; min-width: 0; }
.collapse-wipe-host :deep(.collapse-wipe-shell) { overflow: hidden; margin-bottom: 8px; pointer-events: none; will-change: height, opacity; }
.collapse-wipe-host :deep(.collapse-wipe-body) { display: flow-root; width: 100%; }
@media (prefers-reduced-motion: reduce) {
  .process-chevron, .process-history { transition: none; }
  .conversation-process.is-entering { animation: none; }
}
</style>
