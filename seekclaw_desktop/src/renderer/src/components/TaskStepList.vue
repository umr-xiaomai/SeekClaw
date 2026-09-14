<script setup lang="ts">
import {
  Check,
  ChevronDown,
  Circle,
  ListTodo,
  LoaderCircle,
  X
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import type { TaskStep } from '../task-planner'

export type { TaskStep }

const props = defineProps<{
  steps: TaskStep[]
  running?: boolean
  phase?: string
}>()

// Collapsed by default: the plan is background context, the header already shows
// which step is running, and users can expand it when they want the full list.
const collapsed = ref(true)
const dismissed = ref(false)

// A brand-new plan (the Agent called update_plan again in a later turn) brings the
// card back, while a dismissed card stays dismissed as the same plan progresses.
const planSignature = computed(() => props.steps.map((step) => `${step.id}:${step.title}`).join('|'))
watch(planSignature, (signature, previous) => {
  if (signature && signature !== previous) dismissed.value = false
})

const completedCount = computed(() => props.steps.filter((s) => s.state === 'done').length)
const runningStep = computed(() => props.steps.find((s) => s.state === 'running'))

const summaryText = computed(() => {
  if (props.running) {
    if (runningStep.value) {
      return `正在执行：${runningStep.value.title}`
    }
    return props.phase ? `正在执行 · ${props.phase}` : '正在规划与执行…'
  }
  return `已完成全部 ${props.steps.length} 项任务计划`
})
</script>

<template>
  <Transition name="task-step-list">
    <section v-if="!dismissed && steps.length > 0" class="task-step-list-card">
      <header class="task-step-header" @click="collapsed = !collapsed">
        <div class="task-step-header-left">
          <span class="task-step-icon">
            <LoaderCircle v-if="running" :size="16" class="spin accent-spin" />
            <ListTodo v-else :size="16" class="accent-icon" />
          </span>
          <span class="task-step-title">任务计划</span>
          <span class="task-step-badge" :class="{ 'is-running': running }">
            {{ completedCount }} / {{ steps.length }}
          </span>
          <span class="task-step-summary">{{ summaryText }}</span>
        </div>

        <div class="task-step-header-actions" @click.stop>
          <button type="button" class="icon-button compact" :title="collapsed ? '展开任务计划' : '收起任务计划'"
            @click="collapsed = !collapsed">
            <ChevronDown :size="14" class="task-step-chevron" :class="{ rotated: !collapsed }" />
          </button>
          <button v-if="!running" type="button" class="icon-button compact" title="关闭" @click="dismissed = true">
            <X :size="14" />
          </button>
        </div>
      </header>

      <div class="task-step-accordion" :class="{ 'is-open': !collapsed }">
        <div class="task-step-accordion-inner">
          <div class="task-step-body">
            <div class="task-step-items">
              <div v-for="step in steps" :key="step.id" class="task-step-item" :class="`state-${step.state}`">
                <div class="task-step-status">
                  <LoaderCircle v-if="step.state === 'running'" :size="16" class="spin status-running-icon" />
                  <span v-else-if="step.state === 'done'" class="status-done-icon">
                    <Check :size="11" />
                  </span>
                  <span v-else-if="step.state === 'error'" class="status-error-icon">
                    <X :size="11" />
                  </span>
                  <Circle v-else :size="15" class="status-pending-icon" />
                </div>

                <div class="task-step-content">
                  <div class="task-step-main">
                    <span class="task-step-name">{{ step.title }}</span>
                  </div>
                  <div v-if="step.detail" class="task-step-detail" :title="step.detail">
                    {{ step.detail }}
                  </div>
                </div>

                <div class="task-step-state-tag" :class="`tag-${step.state}`">
                  <span v-if="step.state === 'running'" class="running-dot" />
                  {{ step.state === 'running' ? '进行中' : step.state === 'done' ? '已完成' : step.state === 'error' ? '异常' : '待执行' }}
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  </Transition>
</template>

<style scoped>
.task-step-list-card {
  width: min(100%, 940px);
  margin: 0 auto 10px;
  overflow: hidden;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 14px;
  box-shadow: 0 4px 18px rgb(0 0 0 / 4%);
}

.task-step-header {
  display: flex;
  min-height: 40px;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 8px 14px;
  cursor: pointer;
  user-select: none;
  background: var(--surface);
  transition: background-color 140ms ease;
}

.task-step-header:hover {
  background: var(--surface-hover);
}

.task-step-header-left {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 9px;
}

.task-step-icon {
  display: grid;
  place-items: center;
}

.accent-spin {
  color: var(--accent);
}

.accent-icon {
  color: var(--accent);
}

.task-step-title {
  color: var(--text);
  font-size: 13px;
  font-weight: 650;
  white-space: nowrap;
}

.task-step-badge {
  padding: 2px 8px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  background: var(--surface-hover);
  border: 1px solid var(--border);
  border-radius: 999px;
  white-space: nowrap;
}

.task-step-badge.is-running {
  color: var(--accent);
  background: var(--accent-soft);
  border-color: color-mix(in srgb, var(--accent) 30%, transparent);
}

.task-step-summary {
  overflow: hidden;
  color: var(--text-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.task-step-header-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}

.task-step-chevron {
  transition: transform 220ms ease;
}

.task-step-chevron.rotated {
  transform: rotate(180deg);
}

.task-step-accordion {
  display: grid;
  grid-template-rows: 0fr;
  transition: grid-template-rows 240ms cubic-bezier(0.4, 0, 0.2, 1);
}

.task-step-accordion.is-open {
  grid-template-rows: 1fr;
}

.task-step-accordion-inner {
  overflow: hidden;
  border-top: 1px solid var(--border);
  background: var(--surface);
  transition: opacity 200ms ease, transform 200ms ease;
  opacity: 0;
  transform: translateY(-4px);
}

.task-step-accordion.is-open .task-step-accordion-inner {
  opacity: 1;
  transform: translateY(0);
}

.task-step-body {
  max-height: 240px;
  overflow-y: auto;
  scrollbar-width: thin;
}

.task-step-items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 12px;
}

.task-step-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 10px;
  border-radius: 9px;
  transition: background-color 140ms ease;
}

.task-step-item:hover {
  background: var(--surface-hover);
}

.task-step-item.state-running {
  background: color-mix(in srgb, var(--accent-soft) 40%, transparent);
}

.task-step-status {
  display: grid;
  flex: none;
  width: 22px;
  height: 22px;
  place-items: center;
}

.status-running-icon {
  color: var(--accent);
}

.status-done-icon {
  display: grid;
  width: 19px;
  height: 19px;
  place-items: center;
  color: var(--accent);
  background: var(--accent-soft);
  border: 1.5px solid color-mix(in srgb, var(--accent) 60%, transparent);
  border-radius: 50%;
}

.status-error-icon {
  display: grid;
  width: 19px;
  height: 19px;
  place-items: center;
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 15%, transparent);
  border: 1.5px solid color-mix(in srgb, var(--danger) 60%, transparent);
  border-radius: 50%;
}

.status-pending-icon {
  color: var(--text-muted);
  opacity: .5;
}

.task-step-content {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 2px;
}

.task-step-main {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 8px;
}

.task-step-name {
  overflow: hidden;
  color: var(--text);
  font-size: 13.5px;
  font-weight: 550;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.task-step-detail {
  max-width: 90%;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.task-step-state-tag {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 8px;
  font-size: 11px;
  font-weight: 550;
  border-radius: 6px;
  white-space: nowrap;
}

.task-step-state-tag.tag-running {
  color: var(--accent);
  background: var(--accent-soft);
}

.task-step-state-tag.tag-done {
  color: var(--text-muted);
  background: transparent;
}

.task-step-state-tag.tag-error {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 12%, transparent);
}

.running-dot {
  width: 6px;
  height: 6px;
  background: var(--accent);
  border-radius: 50%;
  animation: step-pulse 1.2s ease-in-out infinite;
}

@keyframes step-pulse {
  0%, 100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: .3;
    transform: scale(.75);
  }
}

.task-step-list-enter-active,
.task-step-list-leave-active {
  transition: opacity 180ms ease, transform 180ms ease;
}

.task-step-list-enter-from,
.task-step-list-leave-to {
  opacity: 0;
  transform: translateY(6px);
}
</style>
