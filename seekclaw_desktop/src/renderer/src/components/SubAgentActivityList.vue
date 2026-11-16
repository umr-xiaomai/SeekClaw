<script setup lang="ts">
import { Bot, Check, ChevronRight, CircleAlert, CodeXml, FlaskConical, LoaderCircle, Search, Square } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { SubAgentActivity, SubAgentLifecycleEvent } from '../types'
import { phaseLabel } from '../app-helpers'
import { subAgentLifecycleLabel, subAgentName, subAgentRoleKind, subAgentStatusLabel, subAgentSummary } from '../subagent-activity'

const props = withDefaults(defineProps<{ agents: SubAgentActivity[]; events?: SubAgentLifecycleEvent[]; showSummary?: boolean }>(), {
  events: () => [], showSummary: true
})
const emit = defineEmits<{ openSubAgent: [activity: SubAgentActivity] }>()
const expanded = ref(false)
const summary = computed(() => subAgentSummary(props.agents))
const running = computed(() => props.agents.some((agent) => agent.status === 'starting' || (agent.status === 'running' && agent.live)))
const liveProgress = computed(() => props.agents.findLast((agent) => agent.status === 'running' && agent.live && agent.progress))

function roleIcon(agent: SubAgentActivity) {
  return { researcher: Search, coder: CodeXml, tester: FlaskConical, general: Bot }[subAgentRoleKind(agent)]
}

function latestActivity(event: SubAgentLifecycleEvent): SubAgentActivity {
  return props.agents.find((agent) => agent.id === event.agentId) ?? event.agent
}

function name(agent: SubAgentActivity): string {
  const label = subAgentName(agent)
  return props.agents.filter((item) => subAgentName(item) === label).length > 1 && !agent.id.startsWith('tool:')
    ? `${label} · ${agent.id.slice(0, 6)}` : label
}

function progress(agent: SubAgentActivity): string {
  return agent.progress?.startsWith('Using ')
    ? `正在使用 ${agent.progress.slice(6)}` : phaseLabel(agent.progress ?? '')
}

function elapsed(agent: SubAgentActivity): string {
  if (agent.elapsedMs === undefined) return ''
  const seconds = agent.elapsedMs / 1000
  return seconds < 60 ? `${seconds.toFixed(1)} 秒` : `${Math.floor(seconds / 60)} 分 ${Math.round(seconds % 60)} 秒`
}
</script>

<template>
  <details v-if="showSummary && agents.length" class="subagent-activity" :class="{ 'is-working': running }"
    @toggle="expanded = ($event.target as HTMLDetailsElement).open">
    <summary :title="summary">
      <span class="agent-avatars" aria-hidden="true">
        <span v-for="agent in agents.slice(0, 3)" :key="agent.id" class="agent-avatar"
          :class="{ failed: agent.status === 'failed', completed: agent.status === 'completed' }"><component :is="roleIcon(agent)" :size="13" /></span>
      </span>
      <span class="agent-summary" aria-live="polite" aria-atomic="true">{{ summary }}</span>
      <span v-if="liveProgress" class="agent-inline-progress" :title="progress(liveProgress)">{{ progress(liveProgress) }}</span>
      <LoaderCircle v-if="running" :size="13" class="agent-summary-status spin" aria-label="子智能体正在执行" />
      <ChevronRight :size="14" class="agent-chevron" />
    </summary>
    <div v-if="expanded" class="agent-details">
      <section v-for="agent in agents" :key="agent.id" class="agent-detail" :class="{ failed: agent.status === 'failed' }">
        <header class="agent-heading">
          <button type="button" class="agent-open" :disabled="agent.id.startsWith('tool:')"
            :title="agent.id.startsWith('tool:') ? '正在等待子智能体标识' : `打开 ${name(agent)} 的会话`"
            :aria-label="`打开 ${name(agent)} 的会话`" @click="emit('openSubAgent', agent)">
            <component :is="roleIcon(agent)" :size="14" aria-hidden="true" />
            <strong>{{ name(agent) }}</strong>
            <ChevronRight :size="12" aria-hidden="true" />
          </button>
          <span class="agent-state">
            <LoaderCircle v-if="agent.status === 'starting' || (agent.status === 'running' && agent.live)" :size="12" class="spin" />
            <Check v-else-if="agent.status === 'completed'" :size="12" />
            <CircleAlert v-else-if="agent.status === 'failed' || agent.status === 'interrupted'" :size="12" />
            <Square v-else-if="agent.status === 'cancelled'" :size="11" />
            {{ subAgentStatusLabel(agent) }}
          </span>
          <small v-if="elapsed(agent)" class="agent-elapsed">{{ elapsed(agent) }}</small>
        </header>
        <p v-if="agent.progress && agent.status === 'running' && agent.live" class="agent-progress">{{ progress(agent) }}</p>
        <p v-if="agent.detail && agent.status === 'running' && agent.live" class="agent-progress-detail">{{ agent.detail }}</p>
        <p v-if="agent.task" class="agent-task">{{ agent.task }}</p>
        <pre v-if="agent.output" class="agent-output" tabindex="0" :aria-label="`${name(agent)}的结果`">{{ agent.output }}</pre>
        <small v-if="agent.model" class="agent-model">{{ agent.model }}</small>
      </section>
    </div>
  </details>
  <div v-if="events.length" class="agent-lifecycle" aria-label="子智能体工作动态">
    <button v-for="event in events" :key="event.id" type="button" class="agent-lifecycle-row"
      :class="`is-${event.kind}`" :data-subagent-event-id="event.id" :data-agent-id="event.agentId"
      :aria-label="`${subAgentLifecycleLabel(event)}，打开子智能体会话`"
      @click="emit('openSubAgent', latestActivity(event))">
      <component :is="roleIcon(event.agent)" :size="14" class="lifecycle-role" aria-hidden="true" />
      <span>{{ subAgentLifecycleLabel(event) }}</span>
      <Check v-if="event.kind === 'completed'" :size="12" class="lifecycle-state" aria-hidden="true" />
      <CircleAlert v-else-if="event.kind === 'failed' || event.kind === 'interrupted'" :size="12" class="lifecycle-state" aria-hidden="true" />
      <Square v-else-if="event.kind === 'cancelled'" :size="10" class="lifecycle-state" aria-hidden="true" />
      <ChevronRight :size="12" class="lifecycle-chevron" aria-hidden="true" />
    </button>
  </div>
</template>

<style scoped>
.subagent-activity {
  min-width: 0;
  color: var(--text-secondary);
  font-size: 13px;
}

.subagent-activity > summary {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 32px;
  width: fit-content;
  max-width: 100%;
  padding: 3px 0;
  border-radius: 6px;
  list-style: none;
  cursor: pointer;
}

.subagent-activity > summary::-webkit-details-marker {
  display: none;
}

.subagent-activity > summary:hover,
.subagent-activity[open] > summary {
  color: var(--text);
}

.subagent-activity > summary:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
}

.agent-avatars {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  padding-right: 2px;
}

.agent-avatar {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  color: var(--text-secondary);
  background: var(--surface-subtle, var(--surface));
  border: 1px solid var(--border);
  border-radius: 7px;
}

.agent-avatar + .agent-avatar {
  margin-left: -5px;
}

.agent-avatar.failed,
.agent-detail.failed .agent-state {
  color: var(--danger);
}

.agent-summary {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-inline-progress {
  min-width: 0;
  max-width: 220px;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-summary-status,
.agent-chevron {
  flex-shrink: 0;
  color: var(--text-muted);
}

.agent-chevron {
  transition: transform 140ms ease;
}

.subagent-activity[open] > summary .agent-chevron {
  transform: rotate(90deg);
}

.agent-details {
  display: grid;
  gap: 14px;
  min-width: 0;
  margin: 8px 0 12px 10px;
  padding: 2px 0 2px 18px;
  border-left: 1px solid var(--border);
}

.agent-detail {
  min-width: 0;
}

.agent-heading {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 8px;
}

.agent-heading > svg {
  flex-shrink: 0;
  color: var(--text-muted);
}

.agent-open {
  display: inline-flex;
  min-width: 0;
  align-items: center;
  gap: 6px;
  padding: 2px 0;
  color: var(--text-secondary);
  background: transparent;
  border-radius: 4px;
  text-align: left;
}

.agent-open:not(:disabled):hover {
  color: var(--text);
}

.agent-open > svg {
  flex-shrink: 0;
}

.agent-open:disabled {
  cursor: default;
}

.agent-open:focus-visible,
.agent-lifecycle-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
}

.agent-lifecycle {
  display: grid;
  gap: 2px;
  min-width: 0;
  margin: 4px 0 10px;
}

.agent-lifecycle-row {
  display: flex;
  align-items: center;
  gap: 7px;
  width: fit-content;
  max-width: 100%;
  min-height: 28px;
  padding: 3px 0;
  color: var(--text-secondary);
  background: transparent;
  border-radius: 5px;
  font-size: 12px;
  text-align: left;
}

.agent-lifecycle-row > span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-lifecycle-row:hover {
  color: var(--text);
}

.lifecycle-role,
.lifecycle-state,
.lifecycle-chevron {
  flex-shrink: 0;
}

.lifecycle-state,
.lifecycle-chevron {
  color: var(--text-muted);
}

.agent-lifecycle-row.is-failed .lifecycle-state,
.agent-lifecycle-row.is-interrupted .lifecycle-state {
  color: var(--danger);
}

.agent-heading strong {
  min-width: 0;
  overflow-wrap: anywhere;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.agent-state {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--text-secondary);
}

.agent-elapsed,
.agent-model {
  color: var(--text-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.agent-task,
.agent-progress,
.agent-progress-detail {
  margin: 7px 0 0;
  overflow-wrap: anywhere;
  line-height: 1.6;
}

.agent-task,
.agent-progress-detail {
  color: var(--text-muted);
  font-size: 12px;
  white-space: pre-wrap;
}

.agent-output {
  max-height: 260px;
  overflow: auto;
  margin: 8px 0;
  padding: 0;
  color: var(--text-secondary);
  font: 12px/1.65 var(--font-mono);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

@media (prefers-reduced-motion: reduce) {
  .agent-chevron {
    transition: none;
  }
}
</style>
