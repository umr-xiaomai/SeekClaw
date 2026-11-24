<script setup lang="ts">
import { computed, ref } from 'vue'
import type { SubAgentActivity, ToolActivity } from '../types'
import { isSubAgentTool } from '../subagent-activity'
import ProcessActivityGroup from './ProcessActivityGroup.vue'
import ToolCallRow from './ToolCallRow.vue'
import { toolDisplayState } from './tool-presentation'

const props = withDefaults(defineProps<{
  tools: readonly ToolActivity[]
  streaming?: boolean
}>(), { streaming: false })
const emit = defineEmits<{
  openDiff: [path: string, diff: string]
  openBrowserResult: [tool: ToolActivity]
  openSubAgent: [activity: SubAgentActivity]
}>()
const disclosures = ref(new Set<string>())
const groups = computed(() => {
  const groups: Array<{ id: string; tool?: ToolActivity; tools: ToolActivity[] }> = []
  let group: ToolActivity[] = []
  let anchor = 'start'
  function flush(): void {
    if (!group.length) return
    groups.push({ id: `group:${anchor}`, tools: group })
    group = []
  }
  for (const tool of props.tools) {
    if (isSubAgentTool(tool) || toolDisplayState(tool) !== 'done') {
      flush()
      anchor = `tool:${tool.id}`
      groups.push({ id: anchor, tool, tools: [] })
    } else {
      group.push(tool)
    }
  }
  flush()
  return groups
})

function setDisclosure(id: string, expanded: boolean): void {
  if (expanded) disclosures.value.add(id)
  else disclosures.value.delete(id)
}
</script>

<template>
  <div class="tool-activities">
    <template v-for="group in groups" :key="group.id">
      <ToolCallRow v-if="group.tool" :tool="group.tool" :expanded="disclosures.has(group.id)"
        @update:expanded="setDisclosure(group.id, $event)" @open-diff="(path, diff) => emit('openDiff', path, diff)"
        @open-browser-result="emit('openBrowserResult', $event)" @open-sub-agent="emit('openSubAgent', $event)" />
      <ProcessActivityGroup v-else :tools="group.tools" :expanded="disclosures.has(group.id)" :activity-disclosures="disclosures"
        @update:expanded="setDisclosure(group.id, $event)" @set-activity-disclosure="setDisclosure"
        @open-diff="(path, diff) => emit('openDiff', path, diff)" @open-browser-result="emit('openBrowserResult', $event)" />
    </template>
  </div>
</template>

<style scoped>
.tool-activities { display: grid; grid-template-columns: minmax(0, 1fr); gap: 3px; min-width: 0; }
</style>
