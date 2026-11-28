<script setup lang="ts">
import { ChevronRight, CircleAlert, FileCode, Globe2, Image, Layers, List, MessageSquareText, Pencil, Plug, Search, ShieldAlert, SquareTerminal, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { SubAgentActivity, ToolActivity } from '../types'
import ToolCallRow from './ToolCallRow.vue'
import { toolDiffStats, toolDisplayState, toolKind, toolLabel, toolTarget } from './tool-presentation'

const props = defineProps<{
  tools: readonly ToolActivity[]
  reasoning?: readonly string[]
  expanded: boolean
  activityDisclosures: ReadonlySet<string>
  approvalCallIds?: ReadonlySet<string>
  activeApprovalCallId?: string
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
  edit: Pencil, shell: SquareTerminal, explore: Search, web: Globe2,
  plan: Layers, browser: Globe2, image: Image, mcp: Plug, artifact: FileCode, other: Wrench
}
type GroupKind = keyof typeof icons
const summaryOrder: readonly GroupKind[] = ['edit', 'explore', 'shell', 'web', 'plan', 'browser', 'image', 'mcp', 'artifact', 'other']

function groupKind(tool: ToolActivity): GroupKind {
  if (tool.name.toLowerCase() === 'web_search') return 'web'
  const kind = toolKind(tool)
  return kind === 'read' || kind === 'search' ? 'explore' : kind
}

const summaryLabel: Record<GroupKind, (count: number) => string> = {
  edit: (count) => `已编辑 ${count} 个文件`,
  shell: (count) => `运行了 ${count} 条命令`,
  explore: (count) => `读取并探索了 ${count} 项`,
  web: (count) => count === 1 ? '已搜索网页' : `搜索了 ${count} 次网页`,
  plan: (count) => `更新了 ${count} 次计划`,
  browser: (count) => `浏览了 ${count} 项`,
  image: (count) => `查看了 ${count} 次图像`,
  mcp: (count) => `调用了 ${count} 次 MCP`,
  artifact: (count) => `生成了 ${count} 个预览`,
  other: (count) => `调用了 ${count} 次工具`
}

function awaitsApproval(tool: ToolActivity): boolean {
  return tool.state === 'running' && props.approvalCallIds?.has(tool.callId ?? tool.id) === true
}

const summary = computed(() => {
  const counts = new Map<GroupKind, number>()
  const editedFiles = new Set<string>()
  let running: ToolActivity | undefined
  let approval: ToolActivity | undefined
  let interrupted = 0
  let addedLines = 0
  let removedLines = 0

  for (const tool of props.tools) {
    const kind = groupKind(tool)
    const state = toolDisplayState(tool)
    if (state === 'interrupted') {
      interrupted++
    } else {
      counts.set(kind, (counts.get(kind) ?? 0) + 1)
      if (kind === 'edit') editedFiles.add(tool.filePath || toolTarget(tool) || tool.id)
    }
    if (state === 'running') running = tool
    if (awaitsApproval(tool) && (!approval || (tool.callId ?? tool.id) === props.activeApprovalCallId)) {
      approval = tool
    }
    const diff = toolDiffStats(tool)
    addedLines += diff.addedLines
    removedLines += diff.removedLines
  }

  const current = approval ?? running
  const badges = { addedLines, removedLines }
  if (current) {
    const kind = groupKind(current)
    const label = approval ? '等待批准' : toolLabel(current, 'running')
    const target = (toolTarget(current) || (kind === 'other' ? current.name : '')).replace(/\s+/g, ' ')
    return {
      label: target ? `${label} ${target}` : label,
      icon: approval ? ShieldAlert : List,
      active: true,
      ...badges
    }
  }

  const parts: string[] = []
  let primaryKind: GroupKind | undefined
  for (const kind of summaryOrder) {
    const count = counts.get(kind)
    if (!count) continue
    primaryKind ??= kind
    parts.push(summaryLabel[kind](kind === 'edit' ? editedFiles.size : count))
  }
  if (interrupted) parts.push(`${interrupted} 项未完成`)
  return {
    label: parts.join('，') || '思考记录',
    icon: primaryKind ? icons[primaryKind] : interrupted ? CircleAlert : MessageSquareText,
    active: false,
    ...badges
  }
})
</script>

<template>
  <details class="tool-group" :open="expanded" @toggle="onToggle">
    <summary class="group-summary" :class="{ 'is-active': summary.active }" :title="summary.label">
      <component :is="summary.icon" :size="14" class="group-icon" aria-hidden="true" />
      <span class="group-label">{{ summary.label }}</span>
      <span v-if="summary.addedLines > 0 || summary.removedLines > 0" class="tool-group-diff-stats"
        :aria-label="`新增 ${summary.addedLines} 行，删除 ${summary.removedLines} 行`">
        <span v-if="summary.addedLines > 0" class="stat-add" aria-hidden="true">+{{ summary.addedLines }}</span>
        <span v-if="summary.removedLines > 0" class="stat-del" aria-hidden="true">-{{ summary.removedLines }}</span>
      </span>
      <ChevronRight :size="14" class="group-chevron" aria-hidden="true" />
    </summary>
    <div v-if="expanded" class="tool-group-items">
      <ToolCallRow v-for="tool in tools" :key="tool.id" :tool="tool" :expanded="activityDisclosures.has(`tool:${tool.id}`)"
        :awaiting-approval="awaitsApproval(tool)" @update:expanded="emit('setActivityDisclosure', `tool:${tool.id}`, $event)"
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
.tool-group > .group-summary.is-active { width: 100%; }
.group-summary.is-active .group-chevron { margin-left: auto; }
.group-summary::-webkit-details-marker { display: none; }
.group-summary:hover, .group-summary:focus-visible, details[open] > .group-summary:not(.is-active) { color: var(--text); }
.group-summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.group-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.group-icon, .group-chevron { flex: none; }
.tool-group-diff-stats { display: inline-flex; align-items: center; gap: 5px; flex: none; font-family: var(--font-mono); font-size: 11.5px; font-weight: 500; }
.tool-group-diff-stats .stat-add { color: var(--hl-addition-color); }
.tool-group-diff-stats .stat-del { color: var(--hl-deletion-color); }
.group-chevron { transition: transform 160ms ease; }
details[open] > .group-summary .group-chevron { transform: rotate(90deg); }
.tool-group-items { display: grid; gap: 2px; min-width: 0; padding-left: 12px; margin: 2px 0 2px 7px; border-left: 1px solid var(--border); }
.group-reasoning-text { padding: 2px 0 6px; white-space: pre-wrap; overflow-wrap: anywhere; }
.group-reasoning > .group-reasoning-text { margin-left: 21px; }
@media (prefers-reduced-motion: reduce) {
  .group-chevron { transition: none; }
}
</style>
