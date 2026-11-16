<script setup lang="ts">
import { Check, ChevronRight, CircleAlert, LoaderCircle } from '@lucide/vue'
import { computed, ref } from 'vue'
import { messageMatches } from '../app-helpers'
import { summarizeConversationProcess, type ConversationTurnProjection } from '../conversation-process'
import { messageSubAgents } from '../subagent-activity'
import type { ChatMessage, SubAgentActivity } from '../types'
import ConversationMessage from './ConversationMessage.vue'
import SubAgentActivityList from './SubAgentActivityList.vue'
import { toolLabel, toolTarget } from './tool-presentation'

const props = withDefaults(defineProps<{
  turn: ConversationTurnProjection
  assistantId?: string
  sessionId?: string
  workspace?: string
  imageSources?: Record<string, string>
  query?: string
  /** Optional controlled state, retained by the virtualized conversation view. */
  expanded?: boolean
}>(), { imageSources: () => ({}), query: '', expanded: undefined })

const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
  branch: [message: ChatMessage]
  edit: [message: ChatMessage]
  openSubAgent: [activity: SubAgentActivity]
  'update:expanded': [expanded: boolean]
}>()

const uncontrolledExpanded = ref(false)
const isExpanded = computed(() => Boolean(props.query.trim()) || (props.expanded ?? uncontrolledExpanded.value))
const summary = computed(() => summarizeConversationProcess(props.turn.process))
const agents = computed(() => {
  const latest = new Map<string, SubAgentActivity>()
  const items = [...props.turn.process, ...(props.turn.response ? [props.turn.response] : [])]
  for (const item of items) {
    for (const agent of messageSubAgents(item.message)) latest.set(agent.id, agent)
  }
  // A later status tool may contain an older launch snapshot. Actual lifecycle
  // events remain authoritative across the whole turn, as they are within a message.
  for (const item of items) {
    for (const agent of item.message.subAgents ?? []) latest.set(agent.id, agent)
  }
  const representedTools = new Set([...latest.values()].filter((agent) => !agent.id.startsWith('tool:'))
    .map((agent) => agent.sourceToolId).filter(Boolean))
  for (const [id, agent] of latest) {
    if (id.startsWith('tool:') && agent.sourceToolId && representedTools.has(agent.sourceToolId)) latest.delete(id)
  }
  return [...latest.values()]
})
const processEvents = computed(() => {
  const seen = new Set<string>()
  return props.turn.process.flatMap((item) => item.message.subAgentEvents ?? []).filter((event) => {
    if (seen.has(event.id)) return false
    seen.add(event.id)
    return true
  })
})
const status = computed(() => {
  const process = summary.value
  if (props.turn.running) {
    if (process.activeTool) return `${toolLabel(process.activeTool)} ${toolTarget(process.activeTool)}`.trim()
    return props.turn.response ? '正在生成回答' : process.latestMessage?.state === 'thinking' ? '正在思考' : '正在处理'
  }
  const total = process.toolCount ? `${process.toolCount} 项操作` : `${process.stepCount} 个步骤`
  return process.errorCount ? `${total} · ${process.errorCount} 项异常` : total
})

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
      :show-footer="turn.user.showFooter" :is-intermediate="turn.user.isIntermediate" :dimmed="isDimmed(turn.user.message)"
      @open-diff="(path, diff) => emit('openDiff', path, diff)" @branch="emit('branch', $event)" @edit="emit('edit', $event)" />

    <details v-if="turn.process.length" class="conversation-process" :open="isExpanded" @toggle="onToggle">
      <summary :title="status">
        <ChevronRight :size="14" class="process-chevron" aria-hidden="true" />
        <LoaderCircle v-if="turn.running" :size="14" class="process-state spin" aria-label="正在工作" />
        <CircleAlert v-else-if="summary.errorCount" :size="14" class="process-state is-error" aria-label="工作过程包含异常" />
        <Check v-else :size="14" class="process-state" aria-hidden="true" />
        <span class="process-title">{{ turn.running || summary.errorCount ? '工作过程' : '已完成工作' }}</span>
        <span class="process-description">{{ status }}</span>
      </summary>
      <div v-if="isExpanded" class="process-history">
        <div v-for="item in turn.process" :key="item.message.id" class="process-step">
          <ConversationMessage :message="item.message" :image-sources="imageSources" :widgets="item.widgets"
            :session-id="sessionId" :workspace="workspace" :streaming="isStreaming(item.message)"
            :smooth-output="!query.trim()"
            :show-footer="item.showFooter" :is-intermediate="item.isIntermediate" :hide-sub-agents="true"
            :dimmed="isDimmed(item.message)" @open-diff="(path, diff) => emit('openDiff', path, diff)"
            @branch="emit('branch', $event)" @edit="emit('edit', $event)" @open-sub-agent="emit('openSubAgent', $event)" />
        </div>
      </div>
    </details>

    <SubAgentActivityList v-if="agents.length || processEvents.length" :agents="agents" :events="isExpanded ? [] : processEvents"
      @open-sub-agent="emit('openSubAgent', $event)" />

    <ConversationMessage v-if="turn.response" :message="turn.response.message" :image-sources="imageSources"
      :widgets="turn.response.widgets" :session-id="sessionId" :workspace="workspace"
      :streaming="isStreaming(turn.response.message)" :show-footer="turn.response.showFooter"
      :smooth-output="!query.trim()"
      :is-intermediate="turn.response.isIntermediate" :hide-sub-agents="true" :dimmed="isDimmed(turn.response.message)"
      @open-diff="(path, diff) => emit('openDiff', path, diff)" @branch="emit('branch', $event)" @edit="emit('edit', $event)"
      @open-sub-agent="emit('openSubAgent', $event)" />
  </div>
</template>

<style scoped>
.conversation-turn { display: flow-root; min-width: 0; }
.conversation-process { min-width: 0; margin: 0 0 12px; color: var(--text-muted); }
.conversation-process > summary { display: flex; align-items: center; gap: 7px; min-height: 30px; max-width: 100%; padding: 3px 0; border-radius: 6px; cursor: pointer; list-style: none; font-size: 12px; line-height: 1.5; }
.conversation-process > summary::-webkit-details-marker { display: none; }
.conversation-process > summary:hover, .conversation-process[open] > summary { color: var(--text-secondary); }
.conversation-process > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.process-chevron, .process-state { flex-shrink: 0; }
.process-chevron { transition: transform 120ms ease; }
.conversation-process[open] > summary .process-chevron { transform: rotate(90deg); }
.process-title { flex-shrink: 0; }
.process-state.is-error { color: var(--danger); }
.process-description { min-width: 0; overflow: hidden; color: var(--text-muted); text-overflow: ellipsis; white-space: nowrap; font-size: 11px; }
.process-history { display: grid; gap: 8px; min-width: 0; margin: 6px 0 4px 6px; padding: 2px 0 2px 16px; border-left: 1px solid var(--border); }
.process-step { display: flow-root; min-width: 0; }
.process-step :deep(.message) { margin-bottom: 0; }
.process-step :deep(.tool-list) { margin: 0; }
.process-step :deep(.markdown-body + .tool-list) { margin-top: 6px; }
@media (prefers-reduced-motion: reduce) { .process-chevron { transition: none; } }
</style>
