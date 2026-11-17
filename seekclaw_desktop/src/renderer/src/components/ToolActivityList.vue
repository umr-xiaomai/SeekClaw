<script setup lang="ts">
import { ChevronRight, Pencil, Search, SquareTerminal } from '@lucide/vue'
import { computed } from 'vue'
import type { ToolActivity } from '../types'
import { countDiffLines } from '../diff-parser'
import ToolCallRow from './ToolCallRow.vue'
import { toolKind, toolTarget } from './tool-presentation'

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
  const edits = tools.filter((t) => toolKind(t) === 'edit')
  const readsAndSearches = tools.filter((t) => {
    const k = toolKind(t)
    return k === 'read' || k === 'search'
  })
  const shells = tools.filter((t) => toolKind(t) === 'shell')

  const editTargets = new Set(edits.map((t) => t.filePath || toolTarget(t) || t.id))
  const parts = [
    edits.length ? `已编辑 ${editTargets.size} 个文件` : '',
    readsAndSearches.length ? `读取并探索了 ${readsAndSearches.length} 项` : '',
    shells.length ? `运行了 ${shells.length} 条命令` : ''
  ].filter(Boolean)
  return parts.join('，')
}

function groupDiffStats(tools: ToolActivity[]): { addedLines: number; removedLines: number } {
  let addedLines = 0
  let removedLines = 0
  for (const tool of tools) {
    if (typeof tool.addedLines === 'number' || typeof tool.removedLines === 'number') {
      addedLines += tool.addedLines ?? 0
      removedLines += tool.removedLines ?? 0
    } else if (tool.diff) {
      const counts = countDiffLines(tool.diff)
      addedLines += counts.addedLines
      removedLines += counts.removedLines
    }
  }
  return { addedLines, removedLines }
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
          <span>{{ label(group) }}</span>
          <span
            v-if="groupDiffStats(group).addedLines > 0 || groupDiffStats(group).removedLines > 0"
            class="tool-group-diff-stats"
          >
            <span v-if="groupDiffStats(group).addedLines > 0" class="stat-add">+{{ groupDiffStats(group).addedLines }}</span>
            <span v-if="groupDiffStats(group).removedLines > 0" class="stat-del">-{{ groupDiffStats(group).removedLines }}</span>
          </span>
          <ChevronRight :size="14" class="group-chevron" />
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
.tool-group-diff-stats { display: inline-flex; align-items: center; gap: 5px; font-family: var(--font-mono); font-size: 11.5px; font-weight: 500; }
.tool-group-diff-stats .stat-add { color: light-dark(#1a7f37, #3fb950); }
.tool-group-diff-stats .stat-del { color: light-dark(#cf222e, #f85149); }
.group-chevron { flex: none; transition: transform 160ms ease; }
.tool-group[open] > summary .group-chevron { transform: rotate(90deg); }
.tool-group-items { display: grid; gap: 2px; padding-left: 20px; margin-top: 2px; }
</style>
