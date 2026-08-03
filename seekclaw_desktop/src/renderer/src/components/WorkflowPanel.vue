<script setup lang="ts">
import {
  Brain,
  Bug,
  Check,
  ChevronRight,
  Gavel,
  Hammer,
  Layers,
  Play,
  Sparkles,
  Wrench,
  X
} from '@lucide/vue'
import { nextTick, ref, watch } from 'vue'
import type { WorkflowKind, WorkflowNode, WorkflowState } from '../types'

const props = defineProps<{
  workflow?: WorkflowState
  open: boolean
}>()

const emit = defineEmits<{ close: [] }>()

const flow = ref<HTMLElement | null>(null)

const kindIcon: Record<WorkflowKind, unknown> = {
  start: Play,
  think: Brain,
  tool: Wrench,
  verify: Hammer,
  repair: Bug,
  compact: Layers,
  review: Gavel,
  done: Check,
  error: X
}

const kindLabel: Record<WorkflowKind, string> = {
  start: '开始',
  think: '思考',
  tool: '工具',
  verify: '验证',
  repair: '修复',
  compact: '压缩',
  review: '评审',
  done: '完成',
  error: '失败'
}

function iconFor(kind: WorkflowKind): unknown {
  return kindIcon[kind] ?? Sparkles
}

watch(() => props.workflow?.activeId, (id) => {
  if (!id || !props.open) return
  void nextTick(() => {
    const node = flow.value?.querySelector<HTMLElement>(`[data-node-id="${id}"]`)
    node?.scrollIntoView({ inline: 'center', behavior: 'smooth', block: 'nearest' })
  })
})

function nodeTitle(node: WorkflowNode): string {
  return `${kindLabel[node.kind] ?? node.kind}${node.step > 0 ? ` · 步骤 ${node.step}` : ''}${node.detail ? `\n${node.detail}` : ''}`
}
</script>

<template>
  <Transition name="workflow-panel">
    <section v-if="open && workflow && workflow.nodes.length > 0" class="workflow-panel">
      <header class="workflow-header">
        <div class="workflow-title">
          <Sparkles :size="15" />
          <strong>执行流程图</strong>
          <span class="workflow-count">{{ workflow.nodes.length }} 个节点</span>
        </div>
        <button class="icon-button compact" title="收起流程图" @click="emit('close')"><X :size="15" /></button>
      </header>
      <div ref="flow" class="workflow-flow">
        <template v-for="(node, index) in workflow.nodes" :key="node.id">
          <div class="workflow-node" :class="`workflow-${node.kind}`" :data-node-id="node.id" :title="nodeTitle(node)">
            <span class="workflow-node-icon"><component :is="iconFor(node.kind)" :size="16" /></span>
            <span class="workflow-node-label">{{ node.label }}</span>
            <span v-if="node.state === 'running'" class="workflow-node-active" aria-label="进行中" />
            <span v-else-if="node.state === 'done'" class="workflow-node-done"><Check :size="11" /></span>
            <span v-else-if="node.state === 'error'" class="workflow-node-failed"><X :size="11" /></span>
          </div>
          <ChevronRight v-if="index < workflow.nodes.length - 1" :size="14" class="workflow-arrow" />
        </template>
      </div>
    </section>
  </Transition>
</template>

<style scoped>
.workflow-panel {
  margin: 0 0 12px;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 14px;
}

.workflow-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
}

.workflow-title {
  display: flex;
  align-items: center;
  gap: 7px;
  color: var(--accent);
  font-size: 12px;
  font-weight: 650;
}

.workflow-count {
  color: var(--text-muted);
  font-size: 10.5px;
  font-weight: 500;
}

.workflow-flow {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 10px 12px;
  overflow-x: auto;
  scrollbar-width: thin;
}

.workflow-node {
  display: inline-flex;
  flex: none;
  height: 34px;
  align-items: center;
  gap: 6px;
  padding: 0 11px;
  color: var(--text-secondary);
  font-size: 11.5px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
  opacity: .78;
}

.workflow-node-icon {
  display: grid;
  place-items: center;
  color: var(--text-muted);
}

.workflow-node-label {
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workflow-node.running {
  color: var(--text);
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent) 16%, transparent);
  opacity: 1;
}

.workflow-node.running .workflow-node-icon {
  color: var(--accent);
}

.workflow-node.done {
  opacity: 1;
}

.workflow-node.done .workflow-node-icon {
  color: var(--accent);
}

.workflow-node.error {
  color: var(--danger);
  border-color: color-mix(in srgb, var(--danger) 55%, var(--border));
  opacity: 1;
}

.workflow-node.error .workflow-node-icon {
  color: var(--danger);
}

.workflow-node-active {
  width: 7px;
  height: 7px;
  flex: none;
  background: var(--accent);
  border-radius: 50%;
  animation: workflow-pulse 1s ease-in-out infinite;
}

.workflow-node-done,
.workflow-node-failed {
  display: grid;
  width: 16px;
  height: 16px;
  flex: none;
  place-items: center;
  color: #fff;
  background: var(--accent);
  border-radius: 50%;
}

.workflow-node-failed {
  background: var(--danger);
}

.workflow-arrow {
  flex: none;
  color: var(--text-muted);
  opacity: .55;
}

@keyframes workflow-pulse {
  0%, 100% { opacity: 1; transform: scale(1); }
  50% { opacity: .35; transform: scale(.75); }
}

.workflow-panel-enter-active,
.workflow-panel-leave-active {
  transition: opacity 180ms ease, transform 200ms ease;
}

.workflow-panel-enter-from,
.workflow-panel-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
