<script setup lang="ts">
import { computed, ref } from 'vue'
import { ChevronDown, Pause, Play, Target } from '@lucide/vue'
import type { SessionGoal } from '../session-goal'

const props = defineProps<{ goal: SessionGoal; running: boolean; disabled?: boolean }>()
const emit = defineEmits<{ pause: []; resume: [] }>()
const expanded = ref(false)
const status = computed(() => {
  if (props.goal.status === 'complete') return '已完成'
  if (props.goal.status === 'paused') return '已暂停'
  return props.running ? '执行中' : '待继续'
})
</script>

<template>
  <section class="goal-card" aria-label="会话目标">
    <div class="goal-row">
      <button type="button" class="goal-disclosure" :aria-expanded="expanded" :title="goal.objective"
        @click="expanded = !expanded">
        <Target :size="15" aria-hidden="true" />
        <span class="goal-title">{{ goal.objective }}</span>
        <span class="goal-status">{{ status }}</span>
        <ChevronDown :size="14" class="goal-chevron" aria-hidden="true" />
      </button>
      <button v-if="goal.status === 'active'" type="button" class="goal-action" aria-label="暂停目标" title="暂停目标"
        :disabled="disabled" @click="emit('pause')">
        <Pause :size="14" />
      </button>
      <button v-if="goal.status !== 'complete' && !running" type="button" class="goal-action" aria-label="继续目标"
        title="继续目标" :disabled="disabled" @click="emit('resume')">
        <Play :size="14" />
      </button>
    </div>
    <dl v-if="expanded" class="goal-details">
      <dt>目标</dt><dd>{{ goal.objective }}</dd>
      <dt>完成条件</dt><dd>{{ goal.criteria }}</dd>
      <template v-if="goal.progress">
        <dt>{{ goal.status === 'complete' ? '完成依据' : '已保存进度' }}</dt><dd>{{ goal.progress }}</dd>
      </template>
      <template v-if="goal.pauseReason">
        <dt>暂停原因</dt><dd>{{ goal.pauseReason }}</dd>
      </template>
    </dl>
    <p v-else-if="goal.pauseReason" class="goal-reason" :title="goal.pauseReason">{{ goal.pauseReason }}</p>
  </section>
</template>

<style scoped>
.goal-card {
  width: min(100%, 800px);
  margin: 0 auto 8px;
  padding: 3px 5px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--surface);
  font-size: 12px;
}
.goal-row { display: flex; align-items: center; gap: 3px; }
.goal-disclosure, .goal-action {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 28px;
  padding: 4px 7px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  cursor: pointer;
}
.goal-disclosure { flex: 1; min-width: 0; text-align: left; }
.goal-disclosure:hover, .goal-action:hover:not(:disabled) { background: var(--surface-hover); }
.goal-disclosure > svg, .goal-action, .goal-status { flex-shrink: 0; }
.goal-action:disabled { opacity: .45; cursor: default; }
.goal-title, .goal-reason { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.goal-status, .goal-chevron, .goal-reason, .goal-details dt { color: var(--text-secondary); }
.goal-status { margin-left: auto; }
.goal-disclosure[aria-expanded="true"] .goal-chevron { transform: rotate(180deg); }
.goal-reason { margin: 1px 8px 5px 30px; }
.goal-details {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 8px 14px;
  max-height: 200px;
  overflow: auto;
  margin: 4px 6px 3px;
  padding: 8px 4px 4px;
  border-top: 1px solid var(--border);
}
.goal-details dd { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
