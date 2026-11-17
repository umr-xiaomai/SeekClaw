<script setup lang="ts">
import { Check, ChevronRight, CircleAlert, ExternalLink, FileText, Globe2, Image, Layers, LoaderCircle, Pencil, Search, SquareTerminal, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ToolActivity } from '../types'
import { countDiffLines, parseUnifiedDiff } from '../diff-parser'
import McpToolContent from './McpToolContent.vue'
import { toolKind, toolLabel, toolTarget } from './tool-presentation'
import { subAgentsFromTool } from '../subagent-activity'
import SubAgentActivityList from './SubAgentActivityList.vue'

const props = defineProps<{ tool: ToolActivity }>()
const emit = defineEmits<{ openDiff: [filePath: string, diff: string] }>()
const expanded = ref(false)
const kind = computed(() => toolKind(props.tool))
const subAgents = computed(() => subAgentsFromTool(props.tool))
const icon = computed(() => ({ shell: SquareTerminal, edit: Pencil, read: FileText, search: Search, image: Image, browser: Globe2, plan: Layers, other: Wrench })[kind.value])
const target = computed(() => toolTarget(props.tool))
const input = computed(() => {
  if (kind.value === 'shell' && props.tool.input) {
    try { const args = JSON.parse(props.tool.input); return args.command || args.cmd || props.tool.input } catch { /* Legacy text. */ }
  }
  return props.tool.input || props.tool.detail
})

const diffStats = computed(() => {
  if (typeof props.tool.addedLines === 'number' || typeof props.tool.removedLines === 'number') {
    return {
      addedLines: props.tool.addedLines ?? 0,
      removedLines: props.tool.removedLines ?? 0
    }
  }
  if (props.tool.diff) return countDiffLines(props.tool.diff)
  return { addedLines: 0, removedLines: 0 }
})

const hasDiffBadge = computed(() => diffStats.value.addedLines > 0 || diffStats.value.removedLines > 0)

const MAX_INLINE_DIFF_LINES = 60
const inlineDiffPreview = computed(() => {
  if (!expanded.value || !props.tool.diff) return null
  const files = parseUnifiedDiff(props.tool.diff, props.tool.filePath || target.value || 'patch')
  if (files.length === 0) return null
  const allLines = files.flatMap((file) => file.hunks.flatMap((hunk) => hunk.lines))
  const truncatedCount = Math.max(0, allLines.length - MAX_INLINE_DIFF_LINES)
  return {
    file: files[0]!,
    lines: allLines.slice(0, MAX_INLINE_DIFF_LINES),
    truncatedCount
  }
})

function openFullDiff(): void {
  if (!props.tool.diff) return
  emit('openDiff', props.tool.filePath || target.value || '变更预览', props.tool.diff)
}
</script>

<template>
  <SubAgentActivityList v-if="subAgents.length" :agents="subAgents" />
  <details v-else class="tool-call" :class="{ 'is-running': tool.state === 'running', 'is-error': tool.state === 'error' }" @toggle="expanded = ($event.target as HTMLDetailsElement).open">
    <summary :title="target || tool.name">
      <LoaderCircle v-if="tool.state === 'running'" :size="14" class="spin activity-icon" />
      <CircleAlert v-else-if="tool.state === 'error'" :size="14" class="activity-icon" />
      <component :is="icon" v-else :size="14" class="activity-icon" />
      <span class="activity-verb">{{ toolLabel(tool) }}</span>
      <span v-if="target" class="activity-target">{{ target.replace(/\s+/g, ' ') }}</span>
      <span v-else-if="kind === 'shell'">命令</span>
      <span v-else-if="kind === 'other'">{{ tool.name }}</span>
      <span v-if="hasDiffBadge" class="tool-diff-stats" aria-label="代码变更统计">
        <span v-if="diffStats.addedLines > 0" class="stat-add">+{{ diffStats.addedLines }}</span>
        <span v-if="diffStats.removedLines > 0" class="stat-del">-{{ diffStats.removedLines }}</span>
      </span>
      <ChevronRight :size="14" class="activity-chevron" />
    </summary>
    <div v-if="expanded" class="tool-call-body">
      <div class="tool-call-header">
        <div class="tool-call-title">{{ kind === 'shell' ? 'Shell' : tool.name }}</div>
        <button
          v-if="tool.diff"
          class="tool-diff-link"
          type="button"
          @click="openFullDiff"
        >
          <span>在右侧完整对比</span>
          <ExternalLink :size="12" />
        </button>
      </div>
      <div v-if="inlineDiffPreview" class="tool-inline-diff" aria-label="内联差异预览">
        <div
          v-for="(line, lineIdx) in inlineDiffPreview.lines"
          :key="lineIdx"
          class="tool-diff-line"
          :class="'diff-' + line.type"
        >
          <span class="tool-diff-gutter">{{ line.newLineNumber || line.oldLineNumber || '' }}</span>
          <span class="tool-diff-sign">{{ line.type === 'addition' ? '+' : line.type === 'deletion' ? '-' : ' ' }}</span>
          <span class="tool-diff-text">{{ line.content }}</span>
        </div>
        <div v-if="inlineDiffPreview.truncatedCount > 0" class="tool-diff-more">
          <span>还有 {{ inlineDiffPreview.truncatedCount }} 行变更，</span>
          <button type="button" class="tool-diff-more-btn" @click="openFullDiff">在右侧查看完整对比</button>
        </div>
      </div>
      <pre v-else-if="input" class="tool-call-input">{{ kind === 'shell' ? '$ ' + input : input }}</pre>
      <McpToolContent :tool="tool" embedded />
      <div class="tool-call-status" role="status">
        <LoaderCircle v-if="tool.state === 'running'" :size="13" class="spin" />
        <CircleAlert v-else-if="tool.state === 'error'" :size="13" />
        <Check v-else :size="13" />
        {{ tool.state === 'running' ? '执行中' : tool.state === 'error' ? '失败' : '成功' }}
      </div>
    </div>
  </details>
</template>

<style scoped>
.tool-call { min-width: 0; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.tool-call summary { display: flex; align-items: center; gap: 7px; min-width: 0; min-height: 26px; padding: 2px 0; cursor: pointer; list-style: none; }
.tool-call summary::-webkit-details-marker { display: none; }
.tool-call summary:hover, .tool-call[open] summary { color: var(--text); }
.activity-icon, .activity-verb, .activity-chevron { flex: none; }
.activity-target { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tool-diff-stats { display: inline-flex; align-items: center; gap: 5px; flex: none; font-family: var(--font-mono); font-size: 11.5px; font-weight: 500; }
.tool-diff-stats .stat-add { color: light-dark(#1a7f37, #3fb950); }
.tool-diff-stats .stat-del { color: light-dark(#cf222e, #f85149); }
.activity-chevron { transition: transform 160ms ease; }
.tool-call[open] .activity-chevron { transform: rotate(90deg); }
.is-running summary { color: var(--text-secondary); }
.is-error summary, .is-error .tool-call-status { color: var(--danger); }
.tool-call-body {
  margin: 5px 0 12px;
  padding: 10px 12px 8px;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: var(--surface-hover);
  overflow: hidden;
  animation: tool-body-reveal 160ms cubic-bezier(.2, .8, .2, 1);
  transform-origin: top center;
}
@keyframes tool-body-reveal {
  from { opacity: 0; transform: translateY(-4px); }
  to { opacity: 1; transform: translateY(0); }
}
.tool-call-header { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-bottom: 8px; }
.tool-call-title { color: var(--text-secondary); font-size: 12px; font-weight: 500; }
.tool-call-input { margin: 0 0 10px; max-height: 140px; overflow: auto; font: 12.5px/1.65 var(--font-mono); white-space: pre-wrap; overflow-wrap: anywhere; color: var(--text-secondary); }
.tool-inline-diff {
  margin: 0 0 8px;
  max-height: 240px;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
  font: 12px/1.55 var(--font-mono);
}
.tool-diff-line {
  display: grid;
  grid-template-columns: 38px 14px minmax(0, 1fr);
  padding: 0 8px 0 0;
  white-space: pre;
}
.tool-diff-line.diff-addition {
  background: light-dark(rgb(26 127 55 / 10%), rgb(63 185 80 / 13%));
  color: light-dark(#116329, #7ee787);
}
.tool-diff-line.diff-deletion {
  background: light-dark(rgb(207 34 46 / 10%), rgb(248 81 73 / 13%));
  color: light-dark(#a40e26, #ffa198);
}
.tool-diff-gutter {
  padding: 0 6px;
  text-align: right;
  color: var(--text-muted);
  opacity: .75;
  user-select: none;
  border-right: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
}
.tool-diff-sign { text-align: center; user-select: none; opacity: .85; }
.tool-diff-text { min-width: 0; padding-left: 2px; }
.tool-diff-more {
  display: flex;
  align-items: center;
  padding: 6px 10px;
  border-top: 1px solid var(--border);
  font-family: var(--font-sans);
  font-size: 11.5px;
  color: var(--text-muted);
}
.tool-diff-more-btn {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--accent);
  cursor: pointer;
  font-size: 11.5px;
}
.tool-diff-more-btn:hover { text-decoration: underline; }
.tool-call-status { display: flex; align-items: center; justify-content: flex-end; gap: 5px; padding-top: 6px; font-size: 12px; }
.tool-diff-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  background: transparent;
  color: var(--text-secondary);
  padding: 2px 6px;
  border-radius: 6px;
  font-size: 11.5px;
  transition: background 140ms ease, color 140ms ease;
}
.tool-diff-link:hover {
  background: var(--surface);
  color: var(--text);
}
@media (prefers-reduced-motion: reduce) {
  .activity-chevron, .tool-call-body { transition: none; animation: none; }
}
</style>
