<script setup lang="ts">
import { FileDiff } from '@lucide/vue'
import { computed } from 'vue'
import type { ToolDiffPreview } from '../conversation-diffs'
import type { ConversationWidget } from '../types'
import '../conversation-widgets.css'
import ScheduleWidgetCard from './ScheduleWidgetCard.vue'
import ArtifactWidgetCard from './ArtifactWidgetCard.vue'

const props = defineProps<{ widgets: ConversationWidget[]; sessionId?: string; workspace?: string; diff?: ToolDiffPreview | null }>()
const emit = defineEmits<{ openDiffPreview: [preview: ToolDiffPreview, trigger: HTMLButtonElement] }>()
const count = computed(() => props.widgets.length + (props.diff ? 1 : 0))

function openDiff(event: MouseEvent): void {
  if (props.diff && event.currentTarget instanceof HTMLButtonElement) {
    emit('openDiffPreview', props.diff, event.currentTarget)
  }
}
</script>

<template>
  <div v-if="count" class="conversation-widgets" :class="{ 'is-single': count === 1 }" aria-label="本轮任务与成果">
    <template v-for="widget in widgets" :key="widget.id">
      <ScheduleWidgetCard v-if="widget.kind === 'schedule'" :widget="widget" />
      <ArtifactWidgetCard v-else-if="widget.kind === 'artifact'" :widget="widget" :session-id="sessionId" :workspace="workspace" />
    </template>
    <button v-if="diff" class="conversation-widget turn-diff-card" type="button" @click="openDiff">
      <FileDiff :size="16" class="turn-diff-icon" aria-hidden="true" />
      <span class="turn-diff-label">
        <strong>查看代码变更</strong>
        <small>{{ diff.fileCount }} 个文件</small>
      </span>
      <span v-if="diff.additions || diff.deletions" class="turn-diff-stats" :aria-label="`新增 ${diff.additions} 行，删除 ${diff.deletions} 行`">
        <span v-if="diff.additions" class="turn-diff-add" aria-hidden="true">+{{ diff.additions }}</span>
        <span v-if="diff.deletions" class="turn-diff-remove" aria-hidden="true">−{{ diff.deletions }}</span>
      </span>
    </button>
  </div>
</template>

<style scoped>
.conversation-widgets {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 250px), 1fr));
  gap: 10px;
  align-items: stretch;
  margin: 12px 0 6px;
  min-width: 0;
}
.conversation-widgets.is-single > :deep(.artifact-widget),
.conversation-widgets.is-single > .turn-diff-card { max-width: 380px; }
.turn-diff-card {
  display: flex;
  align-self: start;
  align-items: center;
  gap: 9px;
  width: 100%;
  min-height: 46px;
  padding: 7px 10px;
  border-radius: 9px;
  text-align: left;
  cursor: pointer;
}
.turn-diff-card:hover { background: var(--surface-hover); }
.turn-diff-card:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.turn-diff-icon { flex: none; color: var(--text-secondary); }
.turn-diff-label { flex: 1; min-width: 0; }
.turn-diff-label strong { display: block; font-size: 13px; font-weight: 600; line-height: 1.45; }
.turn-diff-label small { display: block; color: var(--text-muted); font-size: 11px; line-height: 1.4; }
.turn-diff-stats { display: flex; gap: 6px; flex-shrink: 0; font: 11px var(--font-mono); }
.turn-diff-add { color: var(--hl-addition-color); }
.turn-diff-remove { color: var(--hl-deletion-color); }
</style>
