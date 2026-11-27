<script setup lang="ts">
import { ArrowUpRight, Check, ChevronRight, Circle, CircleAlert, CircleDot, FileOutput, FileText, Image, ListChecks, PanelTop, Pencil, Plug, Search, ShieldAlert, SquareTerminal, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import { parseUnifiedDiff } from '../diff-parser'
import { subAgentsFromTool } from '../subagent-activity'
import type { SubAgentActivity, ToolActivity } from '../types'
import McpToolContent from './McpToolContent.vue'
import SubAgentActivityList from './SubAgentActivityList.vue'
import { commandResult, toolDiffStats, toolDisplayState, toolInput, toolKind, toolLabel, toolPlan, toolTarget } from './tool-presentation'

const props = withDefaults(defineProps<{
  tool: ToolActivity
  expanded?: boolean
  awaitingApproval?: boolean
}>(), { expanded: undefined, awaitingApproval: false })
const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
  openBrowserResult: [tool: ToolActivity]
  openSubAgent: [activity: SubAgentActivity]
  'update:expanded': [expanded: boolean]
}>()

const localExpanded = ref(false)
const isExpanded = computed(() => props.expanded ?? localExpanded.value)
const state = computed(() => toolDisplayState(props.tool))
const kind = computed(() => toolKind(props.tool))
const subAgents = computed(() => subAgentsFromTool(props.tool))
const icons = { shell: SquareTerminal, edit: Pencil, read: FileText, search: Search, image: Image, browser: PanelTop, plan: ListChecks, mcp: Plug, artifact: FileOutput, other: Wrench }
const isWaiting = computed(() => props.awaitingApproval && state.value === 'running')
const icon = computed(() => {
  if (isWaiting.value) return ShieldAlert
  if (state.value === 'error' || state.value === 'interrupted') return CircleAlert
  return icons[kind.value]
})
const target = computed(() => toolTarget(props.tool) || (kind.value === 'other' ? props.tool.name : ''))
const summaryTarget = computed(() => target.value.replace(/\s+/g, ' '))
const label = computed(() => isWaiting.value ? '等待批准' : toolLabel(props.tool, state.value))
const input = computed(() => toolInput(props.tool))
const plan = computed(() => toolPlan(props.tool))
const planStatus = {
  pending: { icon: Circle, label: '待处理' },
  running: { icon: CircleDot, label: '进行中' },
  done: { icon: Check, label: '已完成' }
}
const command = computed(() => commandResult(props.tool))
const diffStats = computed(() => toolDiffStats(props.tool))
const hasDiffBadge = computed(() => diffStats.value.addedLines > 0 || diffStats.value.removedLines > 0)
const hasBrowserResult = computed(() => kind.value === 'browser' && state.value !== 'running'
  && Boolean(props.tool.output || props.tool.content?.length || props.tool.structuredContent != null))

const MAX_INLINE_DIFF_LINES = 60
const inlineDiffPreview = computed(() => {
  if (!isExpanded.value || !props.tool.diff) return null
  const files = parseUnifiedDiff(props.tool.diff, props.tool.filePath || target.value || 'patch')
  if (files.length === 0) return null
  let remaining = MAX_INLINE_DIFF_LINES
  let hiddenLines = 0
  const preview = files.flatMap((file) => {
    const lines = file.hunks.flatMap((hunk) => hunk.lines)
    const visible = lines.slice(0, remaining)
    remaining -= visible.length
    hiddenLines += lines.length - visible.length
    return visible.length ? [{ path: file.displayPath, lines: visible }] : []
  })
  return preview.length ? { files: preview, hiddenLines } : null
})

function onToggle(event: Event): void {
  if (event.target !== event.currentTarget) return
  const open = (event.currentTarget as HTMLDetailsElement).open
  if (open === isExpanded.value) return
  localExpanded.value = open
  emit('update:expanded', open)
}

function openFullDiff(): void {
  if (props.tool.diff) emit('openDiff', props.tool.filePath || target.value || '变更预览', props.tool.diff)
}
</script>

<template>
  <SubAgentActivityList v-if="subAgents.length" :agents="subAgents" @open-sub-agent="emit('openSubAgent', $event)" />
  <details v-else class="tool-call" :class="{
    'is-running': state === 'running', 'is-error': state === 'error',
    'is-interrupted': state === 'interrupted', 'is-waiting': isWaiting
  }"
    :open="isExpanded" @toggle="onToggle">
    <summary :title="target || tool.name">
      <component :is="icon" :size="14" class="activity-icon" aria-hidden="true" />
      <span class="activity-label">
        <span>{{ label }}</span>
        <code v-if="summaryTarget && kind === 'shell'" class="activity-command">{{ summaryTarget }}</code>
        <span v-else-if="summaryTarget" class="activity-target">{{ summaryTarget }}</span>
      </span>
      <span v-if="hasDiffBadge" class="tool-diff-stats" :aria-label="`新增 ${diffStats.addedLines} 行，删除 ${diffStats.removedLines} 行`">
        <span class="stat-add">+{{ diffStats.addedLines }}</span>
        <span class="stat-del">-{{ diffStats.removedLines }}</span>
      </span>
      <span v-if="state === 'running' && !isWaiting" class="run-dot" aria-hidden="true" />
      <ChevronRight :size="12" class="activity-chevron" aria-hidden="true" />
    </summary>
    <div v-if="isExpanded" class="tool-call-body">
      <div class="tool-call-meta">
        <span>{{ tool.name }}</span>
        <span v-if="command?.exitCode !== undefined" :class="{ 'command-error': command.exitCode !== 0 }">退出码 {{ command.exitCode }}</span>
        <span v-else-if="command?.status === 'running'">命令会话仍在运行</span>
      </div>
      <template v-if="plan">
        <p v-if="plan.explanation" class="tool-plan-explanation">{{ plan.explanation }}</p>
        <ol class="tool-plan-steps" role="list" aria-label="计划步骤">
          <li v-for="(step, index) in plan.steps" :key="index" class="tool-plan-step" :class="`step-${step.status}`">
            <component :is="planStatus[step.status].icon" :size="13" class="tool-plan-status" aria-hidden="true" />
            <div>
              <span>{{ step.title }}</span>
              <span class="sr-only">，{{ planStatus[step.status].label }}</span>
              <p v-if="step.detail" class="tool-plan-detail">{{ step.detail }}</p>
            </div>
          </li>
        </ol>
      </template>
      <pre v-else-if="input" class="tool-call-code" aria-label="工具参数" tabindex="0">{{ input }}</pre>
      <template v-if="inlineDiffPreview">
        <section v-for="file in inlineDiffPreview.files" :key="file.path" class="tool-inline-diff" :aria-label="`差异预览 ${file.path}`">
          <div class="tool-diff-path" :title="file.path">{{ file.path }}</div>
          <div v-for="(line, index) in file.lines" :key="index" class="tool-diff-line" :class="`diff-${line.type}`">
            <span class="tool-diff-gutter" aria-hidden="true">{{ line.newLineNumber ?? line.oldLineNumber ?? '' }}</span>
            <span class="tool-diff-text"><span aria-hidden="true">{{ line.type === 'addition' ? '+' : line.type === 'deletion' ? '−' : ' ' }} </span>{{ line.content }}</span>
          </div>
        </section>
        <p v-if="inlineDiffPreview.hiddenLines" class="tool-diff-more">另有 {{ inlineDiffPreview.hiddenLines }} 行未显示</p>
      </template>
      <pre v-else-if="tool.diff" class="tool-call-code" aria-label="差异预览" tabindex="0">{{ tool.diff }}</pre>
      <McpToolContent v-if="kind === 'mcp' || tool.content?.length || tool.structuredContent != null" :tool="tool" embedded />
      <pre v-else-if="command ? command.output : tool.output" class="tool-call-code" aria-label="工具输出" tabindex="0">{{ command ? command.output : tool.output }}</pre>
      <div v-if="tool.diff || hasBrowserResult" class="tool-call-actions">
        <button v-if="tool.diff" class="tool-action" type="button" @click="openFullDiff">
          在右侧查看差异 <ArrowUpRight :size="12" aria-hidden="true" />
        </button>
        <button v-if="hasBrowserResult" class="tool-action" type="button" @click="emit('openBrowserResult', tool)">
          在右侧查看浏览结果 <ArrowUpRight :size="12" aria-hidden="true" />
        </button>
      </div>
    </div>
  </details>
</template>

<style scoped>
.tool-call { min-width: 0; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.tool-call > summary {
  display: flex;
  align-items: center;
  gap: 8px;
  width: fit-content;
  min-width: 0;
  max-width: 100%;
  min-height: 29px;
  padding: 2px 0;
  border-radius: 5px;
  cursor: pointer;
  list-style: none;
}
.tool-call > summary::-webkit-details-marker { display: none; }
.tool-call > summary:hover, .tool-call[open] > summary { color: var(--text); }
.tool-call > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.activity-icon, .activity-chevron { flex: none; }
.activity-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.activity-command, .activity-target { margin-left: 5px; color: var(--text-secondary); }
.activity-command { font: 12px/1.5 var(--font-mono); }
.activity-chevron { transition: transform 130ms ease; }
.tool-call[open] > summary .activity-chevron { transform: rotate(90deg); }
.tool-diff-stats { display: inline-flex; align-items: center; gap: 5px; flex: none; font: 11px/1.4 var(--font-mono); }
.stat-add { color: var(--success); }
.stat-del, .is-error > summary, .is-error .activity-command, .is-error .activity-target, .command-error { color: var(--danger); }
.is-running > summary { color: var(--text-secondary); }
.is-running .activity-command { color: var(--text); }
.is-waiting > summary, .is-interrupted > summary { color: var(--warning); }
.run-dot { flex: none; width: 6px; height: 6px; border-radius: 50%; background: var(--accent); animation: tool-pulse 1.7s ease-in-out infinite; }
@keyframes tool-pulse { 50% { opacity: .35; } }
.tool-call-body { min-width: 0; margin: 2px 0 3px 7px; padding: 4px 0 7px 13px; border-left: 1px solid var(--border); }
.tool-call-meta { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 4px 10px; margin: 0 0 4px; font-size: 11px; overflow-wrap: anywhere; }
.tool-plan-explanation { margin: 0 0 4px; color: var(--text-secondary); overflow-wrap: anywhere; }
.tool-plan-steps { display: grid; gap: 4px; margin: 0 0 7px; padding: 0; list-style: none; }
.tool-plan-step { display: flex; align-items: flex-start; gap: 8px; color: var(--text-secondary); overflow-wrap: anywhere; }
.tool-plan-step.step-pending { color: var(--text-muted); }
.tool-plan-status { flex: none; margin-top: 3px; }
.step-done .tool-plan-status { color: var(--success); }
.step-running .tool-plan-status { color: var(--accent); }
.tool-plan-detail { margin: 0; color: var(--text-muted); font-size: 12px; }
.tool-call-code, .tool-call-body :deep(.mcp-result-body) {
  min-width: 0;
  max-height: 260px;
  overflow: auto;
  margin: 3px 0 6px;
  padding: 9px 10px;
  border-radius: 8px;
  background: var(--surface-subtle);
  color: var(--text-secondary);
  font: 12px/1.62 var(--font-mono);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.tool-call-body :deep(.mcp-result-body pre) { white-space: pre-wrap; overflow-wrap: anywhere; }
.tool-inline-diff { min-width: 0; max-height: 240px; overflow: auto; margin: 6px 0 10px; border: 1px solid var(--border); border-radius: 8px; font: 12px/1.65 var(--font-mono); }
.tool-diff-path { overflow: hidden; padding: 7px 9px; border-bottom: 1px solid var(--border); background: var(--surface-subtle); color: var(--text-secondary); text-overflow: ellipsis; white-space: nowrap; }
.tool-diff-line { display: grid; grid-template-columns: 34px minmax(0, 1fr); padding: 1px 7px 1px 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.tool-diff-line.diff-addition { background: color-mix(in srgb, var(--success) 12%, transparent); color: var(--success); }
.tool-diff-line.diff-deletion { background: color-mix(in srgb, var(--danger) 12%, transparent); color: var(--danger); }
.tool-diff-gutter { padding: 0 6px; text-align: right; color: var(--text-muted); user-select: none; }
.tool-diff-text { min-width: 0; }
.tool-diff-more { margin: 0 0 4px; font-size: 11px; }
.tool-call-actions { display: flex; flex-wrap: wrap; gap: 4px 8px; }
.tool-action { display: inline-flex; align-items: center; gap: 4px; min-height: 26px; padding: 2px 5px; border: 0; border-radius: 5px; color: var(--text-secondary); background: transparent; font-size: 11.5px; }
.tool-action:hover { background: var(--surface-hover); color: var(--text); }
@media (prefers-reduced-motion: reduce) {
  .activity-chevron { transition: none; }
  .run-dot { animation: none; }
}
</style>
