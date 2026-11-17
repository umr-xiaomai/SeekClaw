<script setup lang="ts">
import { Check, ChevronDown, ChevronRight, Globe, Pencil, ShieldAlert, SquareTerminal, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  describeSessionApprovalRule,
  formatApprovalArguments,
  type PendingToolApproval
} from '../tool-approval'

const props = withDefaults(defineProps<{
  approval: PendingToolApproval | null
  queueCount?: number
  backgroundPending?: Array<{ threadId: string; title: string; count: number }>
}>(), {
  queueCount: 0,
  backgroundPending: () => []
})

const emit = defineEmits<{
  decide: [payload: { approvalId: string; approved: boolean; rememberSession?: boolean; reason?: string }]
  switchThread: [threadId: string]
}>()

const cardRef = ref<HTMLElement | null>(null)
const denyMode = ref(false)
const denyReason = ref('')
const argsExpanded = ref(false)
const denyInputRef = ref<HTMLInputElement | null>(null)

const parsedArgs = computed(() => {
  if (!props.approval) {
    return { primaryTarget: '', formattedArgs: '', isShell: false, toolCategory: 'other' as const }
  }
  return formatApprovalArguments(props.approval.argumentsText, props.approval.summary)
})

const ruleDescriptor = computed(() => {
  if (!props.approval) return { signature: '', buttonLabel: '本会话始终允许此类操作' }
  return describeSessionApprovalRule(props.approval)
})

const previewText = computed(() => {
  if (!props.approval) return ''
  return parsedArgs.value.primaryTarget || parsedArgs.value.formattedArgs || props.approval.toolName
})

const isLongArgs = computed(() => parsedArgs.value.formattedArgs.split('\n').length > 4 || parsedArgs.value.formattedArgs.length > 220)

watch(() => props.approval?.approvalId, () => {
  denyMode.value = false
  denyReason.value = ''
  argsExpanded.value = false
})

function allowOnce(): void {
  if (!props.approval) return
  emit('decide', { approvalId: props.approval.approvalId, approved: true })
}

function allowSession(): void {
  if (!props.approval) return
  emit('decide', { approvalId: props.approval.approvalId, approved: true, rememberSession: true })
}

function submitDeny(): void {
  if (!props.approval) return
  const reason = denyReason.value.trim()
  emit('decide', {
    approvalId: props.approval.approvalId,
    approved: false,
    ...(reason ? { reason } : {})
  })
  denyMode.value = false
  denyReason.value = ''
}

function openDenyReason(): void {
  denyMode.value = true
  requestAnimationFrame(() => denyInputRef.value?.focus())
}

function handleWindowKeydown(event: KeyboardEvent): void {
  if (!props.approval || event.isComposing) return
  const active = document.activeElement
  const insideCard = Boolean(active && cardRef.value?.contains(active))
  const inEditable = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement

  if (event.key === 'Escape') {
    if (denyMode.value && insideCard) {
      event.preventDefault()
      event.stopPropagation()
      denyMode.value = false
      return
    }
    if (!inEditable || insideCard) {
      event.preventDefault()
      event.stopPropagation()
      submitDeny()
    }
    return
  }

  if (event.key === 'Enter' && !event.shiftKey) {
    if (denyMode.value && active === denyInputRef.value) {
      event.preventDefault()
      submitDeny()
      return
    }
    // Only trigger Allow via Enter when user isn't typing in composer/other inputs
    if (!inEditable && !(active instanceof HTMLButtonElement)) {
      event.preventDefault()
      allowOnce()
    }
  }
}

onMounted(() => window.addEventListener('keydown', handleWindowKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', handleWindowKeydown))
</script>

<template>
  <Transition name="queue-card" mode="out-in">
    <section
      v-if="approval"
      ref="cardRef"
      :key="approval.approvalId"
      class="inline-approval-card"
      role="region"
      aria-label="待批准工具调用"
    >
      <header class="inline-approval-header">
        <div class="inline-approval-title-row">
          <span class="inline-approval-badge">
            <SquareTerminal v-if="parsedArgs.isShell" :size="14" aria-hidden="true" />
            <Globe v-else-if="approval.requiresNetwork" :size="14" aria-hidden="true" />
            <Pencil v-else-if="approval.mutating" :size="14" aria-hidden="true" />
            <ShieldAlert v-else :size="14" aria-hidden="true" />
          </span>
          <strong class="inline-approval-title">
            {{ approval.background ? '子智能体请求批准' : '请求批准执行工具' }}：<code>{{ approval.toolName }}</code>
          </strong>
          <span v-if="approval.requiresNetwork" class="inline-approval-tag">需联网</span>
          <span v-if="approval.isMcp" class="inline-approval-tag">MCP</span>
          <span v-if="queueCount > 1" class="inline-approval-queue-count">
            还有 {{ queueCount - 1 }} 项待处理
          </span>
        </div>
        <button
          v-if="isLongArgs && parsedArgs.formattedArgs"
          type="button"
          class="inline-approval-expand"
          @click="argsExpanded = !argsExpanded"
        >
          <component :is="argsExpanded ? ChevronDown : ChevronRight" :size="13" />
          <span>{{ argsExpanded ? '收起参数' : '完整参数' }}</span>
        </button>
      </header>

      <div v-if="previewText" class="inline-approval-preview-wrap">
        <pre class="inline-approval-preview">{{ argsExpanded && parsedArgs.formattedArgs ? parsedArgs.formattedArgs : previewText }}</pre>
      </div>

      <div v-if="denyMode" class="inline-approval-reason-row">
        <input
          ref="denyInputRef"
          v-model="denyReason"
          type="text"
          class="inline-approval-reason-input"
          placeholder="可选：说明拒绝原因并反馈给 AI（Enter 提交拒绝，Esc 返回）"
          aria-label="拒绝原因"
        />
        <button type="button" class="inline-approval-btn danger" @click="submitDeny">
          确认拒绝
        </button>
        <button type="button" class="inline-approval-btn subtle" @click="denyMode = false">
          取消
        </button>
      </div>

      <footer v-else class="inline-approval-actions">
        <div class="inline-approval-actions-left">
          <button
            type="button"
            class="inline-approval-btn subtle"
            title="拒绝此次操作 (Esc)"
            @click="submitDeny"
          >
            <X :size="14" aria-hidden="true" />
            <span>拒绝</span>
            <kbd class="approval-kbd">Esc</kbd>
          </button>
          <button
            type="button"
            class="inline-approval-btn subtle"
            title="拒绝并附带原因指导 AI"
            @click="openDenyReason"
          >
            拒绝并说明…
          </button>
        </div>

        <div class="inline-approval-actions-right">
          <button
            type="button"
            class="inline-approval-btn secondary"
            :title="ruleDescriptor.buttonLabel"
            @click="allowSession"
          >
            {{ ruleDescriptor.buttonLabel }}
          </button>
          <button
            type="button"
            class="inline-approval-btn primary"
            title="仅允许本次调用 (Enter)"
            @click="allowOnce"
          >
            <Check :size="14" aria-hidden="true" />
            <span>允许此操作</span>
            <kbd class="approval-kbd">Enter</kbd>
          </button>
        </div>
      </footer>
    </section>

    <div
      v-else-if="backgroundPending.length > 0"
      class="inline-approval-bg-notice"
      role="status"
    >
      <ShieldAlert :size="14" aria-hidden="true" />
      <span>
        后台任务「{{ backgroundPending[0]!.title }}」有 {{ backgroundPending[0]!.count }} 项工具调用等待批准
      </span>
      <button
        type="button"
        class="inline-approval-bg-switch"
        @click="emit('switchThread', backgroundPending[0]!.threadId)"
      >
        查看并处理
      </button>
    </div>
  </Transition>
</template>
