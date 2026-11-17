<script setup lang="ts">
import { ChevronRight, Pencil, Search, SquareTerminal } from '@lucide/vue'
import { computed } from 'vue'
import type { ToolActivity } from '../types'
import ToolCallRow from './ToolCallRow.vue'
import { toolKind } from './tool-presentation'

const props = withDefaults(defineProps<{ tools: ToolActivity[]; streaming?: boolean }>(), { streaming: false })
const emit = defineEmits<{ openDiff: [path: string, diff: string] }>()
const groups = computed(() => {
  const groups: ToolActivity[][] = []
  let current: ToolActivity[] = []
  for (const tool of props.tools) {
    if (!props.streaming && tool.state === 'done' && ['shell', 'edit', 'read', 'search'].includes(toolKind(tool))) current.push(tool)
    else {
      if (current.length) groups.push(current)
      current = []
      groups.push([tool])
    }
  }
  if (current.length) groups.push(current)
  return groups
})
function label(tools: ToolActivity[]): string {
  const kinds = new Set(tools.map(toolKind))
  return [kinds.has('edit') ? '编辑了文件' : '', kinds.has('read') || kinds.has('search') ? '读取并探索了文件' : '', kinds.has('shell') ? '运行了命令' : ''].filter(Boolean).join('，')
}
</script>

<template>
  <div class="tool-activities">
    <template v-for="group in groups" :key="group[0]!.id">
      <details v-if="group.length > 1" class="tool-group">
        <summary>
          <Pencil v-if="group.some(tool => toolKind(tool) === 'edit')" :size="14" />
          <SquareTerminal v-else-if="group.some(tool => toolKind(tool) === 'shell')" :size="14" />
          <Search v-else :size="14" />
          <span>{{ label(group) }}</span><ChevronRight :size="14" class="group-chevron" />
        </summary>
        <div class="tool-group-items">
          <ToolCallRow v-for="tool in group" :key="tool.id" :tool="tool" @open-diff="(path, diff) => emit('openDiff', path, diff)" />
        </div>
      </details>
      <ToolCallRow v-else :tool="group[0]!" @open-diff="(path, diff) => emit('openDiff', path, diff)" />
    </template>
  </div>
</template>

<style scoped>
.tool-activities { display: grid; gap: 2px; min-width: 0; }
.tool-group summary { display: flex; align-items: center; gap: 7px; min-height: 26px; padding: 2px 0; list-style: none; cursor: pointer; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.tool-group summary::-webkit-details-marker { display: none; }
.tool-group summary:hover, .tool-group[open] > summary { color: var(--text); }
.group-chevron { flex: none; transition: transform 140ms ease; }
.tool-group[open] > summary .group-chevron { transform: rotate(90deg); }
.tool-group-items { display: grid; gap: 2px; padding-left: 20px; margin-top: 2px; }
</style>
