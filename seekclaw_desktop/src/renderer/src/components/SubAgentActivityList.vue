<script setup lang="ts">
import { computed } from 'vue'
import type { SubAgentActivity, SubAgentLifecycleEvent } from '../types'
import { phaseLabel } from '../app-helpers'
import {
  subAgentLifecycleLabel,
  subAgentName,
  subAgentSummary
} from '../subagent-activity'
import { collectSubAgentEmblemIndices, subAgentEmblem, type SubAgentEmblemSpec } from '../subagent-emblems'

const props = withDefaults(defineProps<{ agents: SubAgentActivity[]; events?: SubAgentLifecycleEvent[]; showSummary?: boolean }>(), {
  events: () => [],
  showSummary: true
})
const emit = defineEmits<{ openSubAgent: [activity: SubAgentActivity] }>()

interface LifecycleWave {
  key: string
  started: SubAgentLifecycleEvent[]
  terminals: SubAgentLifecycleEvent[]
}

const emblemIndexById = computed(() => collectSubAgentEmblemIndices(props.agents, props.events))

function emblemIndex(agentId: string): number {
  return emblemIndexById.value.get(agentId) ?? 0
}

function emblemOf(agentId: string): SubAgentEmblemSpec {
  return subAgentEmblem(agentId, emblemIndexById.value)
}

const waves = computed<LifecycleWave[]>(() => {
  const result: LifecycleWave[] = []
  let current: LifecycleWave | null = null
  let startedIds = new Set<string>()

  for (const event of props.events) {
    if (event.kind === 'started') {
      if (!current || startedIds.has(event.agentId) || current.terminals.length > 0) {
        current = { key: event.id, started: [], terminals: [] }
        startedIds = new Set<string>()
        result.push(current)
      }
      current.started.push(event)
      startedIds.add(event.agentId)
    } else {
      if (!current) {
        current = { key: event.id, started: [], terminals: [] }
        result.push(current)
      }
      current.terminals.push(event)
    }
  }
  return result
})

const summary = computed(() => subAgentSummary(props.agents))
const running = computed(() => props.agents.some((agent) => agent.status === 'starting' || (agent.status === 'running' && agent.live)))
const liveProgress = computed(() => props.agents.findLast((agent) => agent.status === 'running' && agent.live && agent.progress))

function latestActivity(event: SubAgentLifecycleEvent): SubAgentActivity {
  return props.agents.find((agent) => agent.id === event.agentId) ?? event.agent
}

function canOpen(agent: SubAgentActivity): boolean {
  return !agent.id.startsWith('tool:')
}

function progress(agent: SubAgentActivity): string {
  return agent.progress?.startsWith('Using ')
    ? `正在使用 ${agent.progress.slice(6)}`
    : phaseLabel(agent.progress ?? '')
}
</script>

<template>
  <div v-if="events.length" class="agent-lifecycle" aria-label="子智能体工作动态">
    <template v-for="wave in waves" :key="wave.key">
      <button
        v-if="wave.started.length === 1"
        type="button"
        class="agent-lifecycle-row is-started"
        :data-subagent-event-id="wave.started[0]!.id"
        :data-agent-id="wave.started[0]!.agentId"
        :disabled="!canOpen(latestActivity(wave.started[0]!))"
        :aria-label="`${subAgentLifecycleLabel(wave.started[0]!)}，打开子智能体会话`"
        @click="emit('openSubAgent', latestActivity(wave.started[0]!))"
      >
        <svg
          class="agent-emblem"
          :data-emblem-index="emblemIndex(wave.started[0]!.agentId)"
          viewBox="0 0 16 16"
          width="16"
          height="16"
          aria-hidden="true"
        >
          <path :d="emblemOf(wave.started[0]!.agentId).path" :fill="emblemOf(wave.started[0]!.agentId).color" fill-rule="evenodd" />
        </svg>
        <span class="agent-lifecycle-text">{{ subAgentLifecycleLabel(wave.started[0]!) }}</span>
      </button>

      <div v-else-if="wave.started.length > 1" class="agent-lifecycle-group">
        <span class="agent-emblems" aria-hidden="true">
          <svg
            v-for="event in wave.started"
            :key="`icon-${event.id}`"
            class="agent-emblem"
            :data-emblem-index="emblemIndex(event.agentId)"
            viewBox="0 0 16 16"
            width="16"
            height="16"
          >
            <path :d="emblemOf(event.agentId).path" :fill="emblemOf(event.agentId).color" fill-rule="evenodd" />
          </svg>
        </span>
        <span class="agent-lifecycle-group-text">
          <template v-for="(event, index) in wave.started" :key="event.id">
            <span v-if="index > 0" class="agent-lifecycle-sep">{{ wave.started.length === 2 ? '和' : '、' }}</span>
            <button
              type="button"
              class="agent-lifecycle-inline-btn"
              :data-subagent-event-id="event.id"
              :data-agent-id="event.agentId"
              :disabled="!canOpen(latestActivity(event))"
              :aria-label="`${subAgentName(event.agent)}，打开子智能体会话`"
              @click="emit('openSubAgent', latestActivity(event))"
            >{{ subAgentName(event.agent) }}</button>
          </template>
          <span class="agent-lifecycle-suffix">{{ wave.started.length > 2 ? `等 ${wave.started.length} 个子智能体开始工作` : '开始工作' }}</span>
        </span>
      </div>

      <button
        v-for="event in wave.terminals"
        :key="event.id"
        type="button"
        class="agent-lifecycle-row"
        :class="`is-${event.kind}`"
        :data-subagent-event-id="event.id"
        :data-agent-id="event.agentId"
        :disabled="!canOpen(latestActivity(event))"
        :aria-label="`${subAgentLifecycleLabel(event)}，打开子智能体会话`"
        @click="emit('openSubAgent', latestActivity(event))"
      >
        <svg
          class="agent-emblem"
          :data-emblem-index="emblemIndex(event.agentId)"
          viewBox="0 0 16 16"
          width="16"
          height="16"
          aria-hidden="true"
        >
          <path :d="emblemOf(event.agentId).path" :fill="emblemOf(event.agentId).color" fill-rule="evenodd" />
        </svg>
        <span class="agent-lifecycle-text">{{ subAgentLifecycleLabel(event) }}</span>
      </button>
    </template>

    <div v-if="running && liveProgress" class="agent-inline-progress-row">
      <span class="agent-inline-progress" :title="progress(liveProgress)">{{ progress(liveProgress) }}</span>
    </div>
  </div>

  <div v-else-if="showSummary && agents.length" class="agent-lifecycle" :class="{ 'is-working': running }">
    <div class="agent-lifecycle-group">
      <span class="agent-emblems" aria-hidden="true">
        <svg
          v-for="agent in agents.slice(0, 3)"
          :key="agent.id"
          class="agent-emblem"
          :data-emblem-index="emblemIndex(agent.id)"
          viewBox="0 0 16 16"
          width="16"
          height="16"
        >
          <path :d="emblemOf(agent.id).path" :fill="emblemOf(agent.id).color" fill-rule="evenodd" />
        </svg>
      </span>
      <button
        type="button"
        class="agent-lifecycle-inline-btn agent-summary-btn"
        :disabled="!canOpen(agents[0]!)"
        :title="summary"
        @click="emit('openSubAgent', agents[0]!)"
      >
        <span class="agent-summary">{{ summary }}</span>
      </button>
      <span v-if="liveProgress" class="agent-inline-progress" :title="progress(liveProgress)">{{ progress(liveProgress) }}</span>
    </div>
  </div>
</template>

<style scoped>
.agent-lifecycle {
  display: grid;
  gap: 2px;
  min-width: 0;
  margin: 2px 0;
}

.agent-lifecycle-row {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  width: fit-content;
  max-width: 100%;
  min-height: 26px;
  padding: 2px 0;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 13px;
  line-height: 1.45;
  text-align: left;
}

.agent-lifecycle-row:not(:disabled):hover,
.agent-lifecycle-inline-btn:not(:disabled):hover {
  color: var(--text);
}

.agent-lifecycle-row:disabled,
.agent-lifecycle-inline-btn:disabled {
  cursor: default;
}

.agent-lifecycle-group {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 7px;
  min-width: 0;
  min-height: 26px;
  padding: 2px 0;
  color: var(--text-secondary);
  font-size: 13px;
  line-height: 1.45;
}

.agent-emblems {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.agent-emblem {
  display: block;
  width: 15px;
  height: 15px;
  flex-shrink: 0;
}

.agent-lifecycle-text,
.agent-summary {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-lifecycle-group-text {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  min-width: 0;
}

.agent-lifecycle-inline-btn {
  display: inline-flex;
  align-items: center;
  min-height: 24px;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  line-height: inherit;
  text-align: left;
  border-radius: 4px;
}

.agent-lifecycle-sep,
.agent-lifecycle-suffix {
  color: inherit;
}

.agent-lifecycle-row.is-failed .agent-lifecycle-text,
.agent-lifecycle-row.is-interrupted .agent-lifecycle-text {
  color: var(--danger);
}

.agent-lifecycle-row:focus-visible,
.agent-lifecycle-inline-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
  border-radius: 4px;
}

.agent-inline-progress-row {
  padding-left: 24px;
}

.agent-inline-progress {
  min-width: 0;
  max-width: 280px;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
