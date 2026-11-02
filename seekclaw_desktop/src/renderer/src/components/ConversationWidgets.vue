<script setup lang="ts">
import type { ConversationWidget } from '../types'
import '../conversation-widgets.css'
import ScheduleWidgetCard from './ScheduleWidgetCard.vue'
import ArtifactWidgetCard from './ArtifactWidgetCard.vue'

defineProps<{ widgets: ConversationWidget[]; sessionId?: string; workspace?: string }>()
</script>

<template>
  <div v-if="widgets.length" class="conversation-widgets" aria-label="本轮任务与成果">
    <template v-for="widget in widgets" :key="widget.id">
      <ScheduleWidgetCard v-if="widget.kind === 'schedule'" :widget="widget" />
      <ArtifactWidgetCard v-else-if="widget.kind === 'artifact'" :widget="widget" :session-id="sessionId" :workspace="workspace" />
    </template>
  </div>
</template>

<style scoped>
.conversation-widgets { display: grid; gap: 10px; margin: 16px 0 8px; min-width: 0; }
</style>
