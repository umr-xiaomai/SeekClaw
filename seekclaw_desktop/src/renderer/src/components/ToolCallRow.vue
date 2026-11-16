<script setup lang="ts">
import { Check, ChevronRight, CircleAlert, FileText, Globe2, Image, Layers, LoaderCircle, Pencil, Search, SquareTerminal, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ToolActivity } from '../types'
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
</script>

<template>
  <SubAgentActivityList v-if="subAgents.length" :agents="subAgents" />
  <details v-else class="tool-call" :class="{ 'is-running': tool.state === 'running', 'is-error': tool.state === 'error' }" @toggle="expanded = ($event.target as HTMLDetailsElement).open">
    <summary :title="target || tool.name">
      <LoaderCircle v-if="tool.state === 'running'" :size="16" class="spin activity-icon" />
      <CircleAlert v-else-if="tool.state === 'error'" :size="16" class="activity-icon" />
      <component :is="icon" v-else :size="16" class="activity-icon" />
      <span class="activity-verb">{{ toolLabel(tool) }}</span>
      <span v-if="target" class="activity-target">{{ target.replace(/\s+/g, ' ') }}</span>
      <span v-else-if="kind === 'shell'">命令</span>
      <span v-else-if="kind === 'other'">{{ tool.name }}</span>
      <ChevronRight :size="14" class="activity-chevron" />
    </summary>
    <div v-if="expanded" class="tool-call-body">
      <div class="tool-call-title">{{ kind === 'shell' ? 'Shell' : tool.name }}</div>
      <pre v-if="input" class="tool-call-input">{{ kind === 'shell' ? '$ ' + input : input }}</pre>
      <McpToolContent :tool="tool" embedded />
      <button v-if="tool.filePath && tool.diff" class="tool-diff-link" type="button" @click="emit('openDiff', tool.filePath, tool.diff)">
        查看文件差异 <ChevronRight :size="13" />
      </button>
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
.tool-call { min-width: 0; color: var(--text-muted); font-size: 14px; }
.tool-call summary { display: flex; align-items: center; gap: 8px; min-width: 0; min-height: 32px; padding: 3px 0; cursor: pointer; list-style: none; }
.tool-call summary::-webkit-details-marker { display: none; }
.tool-call summary:hover, .tool-call[open] summary { color: var(--text); }
.activity-icon, .activity-verb, .activity-chevron { flex: none; }
.activity-target { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.activity-chevron { transition: transform 140ms ease; }
.tool-call[open] .activity-chevron { transform: rotate(90deg); }
.is-running summary { color: var(--text-secondary); }
.is-error summary, .is-error .tool-call-status { color: var(--danger); }
.tool-call-body { margin: 5px 0 12px; padding: 10px 12px 8px; border: 1px solid var(--border); border-radius: 12px; background: var(--surface-hover); overflow: hidden; }
.tool-call-title { margin-bottom: 10px; color: var(--text-secondary); font-size: 12px; }
.tool-call-input { margin: 0 0 10px; max-height: 140px; overflow: auto; font: 12.5px/1.65 var(--font-mono); white-space: pre-wrap; overflow-wrap: anywhere; color: var(--text-secondary); }
.tool-call-status { display: flex; align-items: center; justify-content: flex-end; gap: 5px; padding-top: 8px; font-size: 12px; }
.tool-diff-link { display: inline-flex; align-items: center; gap: 4px; background: transparent; color: var(--text); padding: 6px 0; font-size: 12px; }
</style>
