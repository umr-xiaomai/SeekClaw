<script setup lang="ts">
import { ChevronRight, FileCode, FileText, Globe2, Image, Layers, MessageSquareText, Pencil, Plug, Search, SquareTerminal, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { SubAgentActivity, ToolActivity } from '../types'
import ToolCallRow from './ToolCallRow.vue'
import { toolDiffStats, toolDisplayState, toolKind, toolTarget } from './tool-presentation'

const props = defineProps<{
  tools: readonly ToolActivity[]
  reasoning?: readonly string[]
  expanded: boolean
  activityDisclosures: ReadonlySet<string>
}>()
const emit = defineEmits<{
  openDiff: [path: string, diff: string]
  openBrowserResult: [tool: ToolActivity]
  openSubAgent: [activity: SubAgentActivity]
  'update:expanded': [expanded: boolean]
  setActivityDisclosure: [id: string, expanded: boolean]
}>()

function onToggle(event: Event): void {
  if (event.target !== event.currentTarget) return
  const open = (event.currentTarget as HTMLDetailsElement).open
  if (open !== props.expanded) emit('update:expanded', open)
}
const reasoningExpanded = ref(false)
const hasReasoning = computed(() => props.reasoning?.some((text) => text.trim()))

const icons = {
  edit: Pencil, shell: SquareTerminal, read: FileText, search: Search,
  plan: Layers, browser: Globe2, image: Image, mcp: Plug, artifact: FileCode, other: Wrench
}
const iconOrder = ['edit', 'shell', 'read', 'search', 'plan', 'browser', 'image', 'mcp', 'artifact', 'other'] as const

const summary = computed(() => {
  const counts = { edit: 0, shell: 0, read: 0, search: 0, plan: 0, browser: 0, image: 0, mcp: 0, artifact: 0, other: 0 }
  const editedFiles = new Set<string>()
  let failures = 0
  let addedLines = 0
  let removedLines = 0

  for (const tool of props.tools) {
    const kind = toolKind(tool)
    counts[kind]++
    if (kind === 'edit') editedFiles.add(tool.filePath || toolTarget(tool) || tool.id)
    if (toolDisplayState(tool) === 'error') failures++
    const diff = toolDiffStats(tool)
    addedLines += diff.addedLines
    removedLines += diff.removedLines
  }

  const explorations = counts.read + counts.search
  const parts = [
    counts.edit ? `已编辑 ${editedFiles.size} 个文件` : '',
    explorations ? `读取并探索了 ${explorations} 项` : '',
    counts.shell ? `运行了 ${counts.shell} 条命令` : '',
    counts.plan ? `更新了 ${counts.plan} 次计划` : '',
    counts.browser ? `浏览了 ${counts.browser} 项` : '',
    counts.image ? `查看了 ${counts.image} 次图像` : '',
    counts.mcp ? `调用了 ${counts.mcp} 次 MCP` : '',
    counts.artifact ? `生成了 ${counts.artifact} 个预览` : '',
    counts.other ? `调用了 ${counts.other} 次工具` : ''
  ].filter(Boolean)
  const primaryKind = iconOrder.find((kind) => counts[kind] > 0)

  return {
    label: parts.join('，') || '思考记录',
    icon: primaryKind ? icons[primaryKind] : MessageSquareText,
    failures,
    addedLines,
    removedLines
  }
})
</script>

<template>
  <details class="tool-group" :open="expanded" @toggle="onToggle">
    <summary class="group-summary" :title="summary.label">
      <component :is="summary.icon" :size="14" class="group-icon" aria-hidden="true" />
      <span class="group-label">{{ summary.label }}</span>
      <span v-if="summary.addedLines > 0 || summary.removedLines > 0" class="tool-group-diff-stats">
        <span v-if="summary.addedLines > 0" class="stat-add">+{{ summary.addedLines }}</span>
        <span v-if="summary.removedLines > 0" class="stat-del">-{{ summary.removedLines }}</span>
      </span>
      <span v-if="summary.failures" class="group-failures">{{ summary.failures }} 项失败</span>
      <ChevronRight :size="14" class="group-chevron" aria-hidden="true" />
    </summary>
    <div v-if="expanded" class="tool-group-items">
      <ToolCallRow v-for="tool in tools" :key="tool.id" :tool="tool" :expanded="activityDisclosures.has(`tool:${tool.id}`)"
        @update:expanded="emit('setActivityDisclosure', `tool:${tool.id}`, $event)"
        @open-diff="(path, diff) => emit('openDiff', path, diff)" @open-browser-result="emit('openBrowserResult', $event)"
        @open-sub-agent="emit('openSubAgent', $event)" />
      <details v-if="hasReasoning && tools.length" class="group-reasoning" :open="reasoningExpanded"
        @toggle="reasoningExpanded = ($event.currentTarget as HTMLDetailsElement).open">
        <summary class="group-summary">
          <MessageSquareText :size="14" class="group-icon" aria-hidden="true" />
          <span>思考记录</span>
          <ChevronRight :size="14" class="group-chevron" aria-hidden="true" />
        </summary>
        <div v-if="reasoningExpanded" class="group-reasoning-text">{{ reasoning?.join('\n\n') }}</div>
      </details>
      <div v-else-if="hasReasoning" class="group-reasoning-text">{{ reasoning?.join('\n\n') }}</div>
    </div>
  </details>
</template>

<style scoped>
.tool-group { min-width: 0; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.group-summary { display: flex; align-items: center; gap: 8px; min-height: 27px; width: fit-content; max-width: 100%; padding: 2px 0; border-radius: 5px; list-style: none; cursor: pointer; }
.group-summary::-webkit-details-marker { display: none; }
.group-summary:hover, .group-summary:focus-visible, details[open] > .group-summary { color: var(--text); }
.group-summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.group-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.group-icon, .group-failures, .group-chevron { flex: none; }
.group-failures { color: var(--danger); font-size: 11px; white-space: nowrap; }
.tool-group-diff-stats { display: inline-flex; align-items: center; gap: 5px; flex: none; font-family: var(--font-mono); font-size: 11.5px; font-weight: 500; }
.tool-group-diff-stats .stat-add { color: light-dark(#1a7f37, #3fb950); }
.tool-group-diff-stats .stat-del { color: light-dark(#cf222e, #f85149); }
.group-chevron { transition: transform 160ms ease; }
details[open] > .group-summary .group-chevron { transform: rotate(90deg); }
.tool-group-items { display: grid; gap: 2px; min-width: 0; padding-left: 12px; margin: 2px 0 2px 7px; border-left: 1px solid var(--border); }
.group-reasoning-text { padding: 2px 0 6px; white-space: pre-wrap; overflow-wrap: anywhere; }
.group-reasoning > .group-reasoning-text { margin-left: 21px; }
@media (prefers-reduced-motion: reduce) {
  .group-chevron { transition: none; }
}
</style>
