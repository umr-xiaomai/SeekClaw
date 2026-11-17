<script setup lang="ts">
import type { ConversationWidget } from '../types'
import '../conversation-widgets.css'
import ScheduleWidgetCard from './ScheduleWidgetCard.vue'
import ArtifactWidgetCard from './ArtifactWidgetCard.vue'

defineProps<{ widgets: ConversationWidget[]; sessionId?: string; workspace?: string }>()
</script>

<template>
  <div v-if="widgets.length" class="conversation-widgets" :class="{ 'is-single': widgets.length === 1 }" aria-label="本轮任务与成果">
    <template v-for="widget in widgets" :key="widget.id">
      <ScheduleWidgetCard v-if="widget.kind === 'schedule'" :widget="widget" />
      <ArtifactWidgetCard v-else-if="widget.kind === 'artifact'" :widget="widget" :session-id="sessionId" :workspace="workspace" />
    </template>
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
.conversation-widgets.is-single > :deep(.artifact-widget) {
  max-width: 380px;
}
</style>
