<script setup lang="ts">
import {
  Check,
  ChevronDown,
  ChevronRight,
  CircleAlert,
  Copy,
  Layers,
  Pencil,
  RotateCcw,
  Split
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref } from 'vue'
import type { ChatMessage, ConversationWidget, SubAgentActivity } from '../types'
import { formatMessageTime } from '../app-helpers'
import type { ToolDiffPreview } from '../conversation-diffs'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import MessageAttachments from './MessageAttachments.vue'
import ToolActivityList from './ToolActivityList.vue'
import ConversationWidgets from './ConversationWidgets.vue'
import MarkdownMessage from './MarkdownMessage.vue'
import SubAgentActivityList from './SubAgentActivityList.vue'
import { messageSubAgents } from '../subagent-activity'
import { isHiddenSystemUserMessage, isSystemBannerUserMessage } from '../internal-messages'

const props = withDefaults(defineProps<{
  message: ChatMessage
  widgets?: ConversationWidget[]
  turnDiff?: ToolDiffPreview | null
  sessionId?: string
  workspace?: string
  imageSources?: Record<string, string>
  /** Dimmed while the conversation search does not match this message. */
  dimmed?: boolean
  /** True while this message is receiving text. */
  streaming?: boolean
  /** Search reveals all received text immediately, even in a live response. */
  smoothOutput?: boolean
  /** Only true at the final bubble tail of an assistant turn. */
  showFooter?: boolean
  /** True when this assistant message is an intermediate step in a multi-step turn. */
  isIntermediate?: boolean
  /** Compact message within the sequence of activities. */
  progress?: boolean
  /** A parent process summary can render these activities outside its disclosure. */
  hideSubAgents?: boolean
  /** Control whether a fallback sub-agent summary row renders when events are absent. */
  showSubAgentSummary?: boolean
  /** Read-only panels (e.g. sub-agent detail) omit edit, branch, retry, and timestamps. */
  readOnly?: boolean
  /** Render user message as a compact collapsible task prompt disclosure instead of a bubble. */
  compactTaskPrompt?: boolean
  hideTools?: boolean
}>(), {
  imageSources: () => ({}),
  widgets: () => [],
  showFooter: false,
  isIntermediate: false,
  progress: false,
  smoothOutput: true,
  showSubAgentSummary: true,
  readOnly: false,
  compactTaskPrompt: false,
  hideTools: false
})

const emit = defineEmits<{
  openDiffPreview: [preview: ToolDiffPreview, trigger: HTMLButtonElement]
  openDiff: [filePath: string, diff: string]
  branch: [message: ChatMessage]
  retry: [message: ChatMessage]
  edit: [message: ChatMessage]
  openSubAgent: [activity: SubAgentActivity]
}>()

const systemOpen = ref(false)
const preview = ref<{ src: string; name: string } | null>(null)
const isCopied = ref(false)
let copyTimer: ReturnType<typeof setTimeout> | undefined

onBeforeUnmount(() => {
  if (copyTimer) clearTimeout(copyTimer)
})

async function copyContent(): Promise<void> {
  const textToCopy = displayContent.value
    ? (activeError.value ? `${displayContent.value}\n\n${activeError.value}` : displayContent.value)
    : (activeError.value || '')
  if (!textToCopy) return
  try {
    await navigator.clipboard.writeText(textToCopy)
    isCopied.value = true
    if (copyTimer) clearTimeout(copyTimer)
    copyTimer = setTimeout(() => {
      isCopied.value = false
    }, 2000)
  } catch (err) {
    console.error('Failed to copy to clipboard', err)
  }
}

/** Context compaction summaries render as neutral system cards instead of user bubbles. */
const systemKind = computed<'memory' | null>(() => {
  return isSystemBannerUserMessage(props.message) ? 'memory' : null
})

const systemMeta = computed(() => {
  if (systemKind.value === 'memory') {
    return { title: '记忆压缩', subtitle: '较早的对话已被总结，以保持上下文可容纳' }
  }
  return { title: '', subtitle: '' }
})

const systemBody = computed(() => {
  const content = props.message.content
  const marker = content.indexOf('\n')
  return marker >= 0 ? content.slice(marker + 1).trim() : ''
})

function imageUrl(id: string): string | undefined {
  return props.imageSources[id]
}

function previewImage(id: string, name: string): void {
  const src = imageUrl(id)
  if (src) preview.value = { src, name }
}

interface TextPart {
  type: 'text' | 'link'
  content: string
  url?: string
}

function parseTextParts(text: string): TextPart[] {
  const parts: TextPart[] = []
  const urlRegex = /(https?:\/\/[^\s"'`<>]+)/g
  let lastIndex = 0
  let match: RegExpExecArray | null

  while ((match = urlRegex.exec(text)) !== null) {
    const rawUrl = match[1]
    if (!rawUrl) continue

    let fullUrl = rawUrl
    let trailing = ''
    while (/[.,;:\)"'>\]]$/.test(fullUrl)) {
      trailing = fullUrl.slice(-1) + trailing
      fullUrl = fullUrl.slice(0, -1)
    }

    const start = match.index
    if (start > lastIndex) {
      parts.push({ type: 'text', content: text.slice(lastIndex, start) })
    }
    if (fullUrl) {
      parts.push({ type: 'link', content: fullUrl, url: fullUrl })
    }
    if (trailing) {
      parts.push({ type: 'text', content: trailing })
    }
    lastIndex = start + rawUrl.length
  }

  if (lastIndex < text.length) {
    parts.push({ type: 'text', content: text.slice(lastIndex) })
  }

  return parts
}

const legacyError = computed<{ error: string; cleanContent: string } | null>(() => {
  const content = props.message.content || ''
  const match = content.match(/(?:^|\n\n)(?:模型调用失败：|无法连接 SeekClaw Daemon[：:]?)\n```(?:text)?\n([\s\S]*?)\n```\s*$/)
  if (match && match[1]) {
    const matchIndex = match.index ?? 0
    return {
      error: match[1].replace(/^ {4}/gm, '').trim(),
      cleanContent: content.slice(0, matchIndex).trim()
    }
  }
  return null
})

const activeError = computed<string | null>(() => {
  if (props.message.errorMessage?.trim()) {
    return props.message.errorMessage.trim()
  }
  const legacy = legacyError.value
  if (legacy) {
    return legacy.error
  }
  return null
})

const displayContent = computed(() => {
  if (props.message.errorMessage) {
    return props.message.content || ''
  }
  const legacy = legacyError.value
  if (legacy) {
    return legacy.cleanContent
  }
  return props.message.content || ''
})

const errorParts = computed(() => {
  if (!activeError.value) return []
  return parseTextParts(activeError.value)
})

const hasContent = computed(() => Boolean(displayContent.value?.trim()))
const hasError = computed(() => Boolean(activeError.value))
const subAgents = computed(() => messageSubAgents(props.message))
const visibleTools = computed(() => {
  if (props.hideTools) return []
  const represented = new Set([...(props.message.subAgents ?? []), ...subAgents.value].map((agent) => agent.sourceToolId).filter(Boolean))
  return props.message.tools?.filter((tool) => !represented.has(tool.id)) ?? []
})
const hasImages = computed(() =>
  (props.message.images?.length ?? 0) > 0 ||
  (props.message.files?.length ?? 0) > 0
)

const hasVisibleSubAgents = computed(() =>
  !props.hideSubAgents && (
    (props.showSubAgentSummary && subAgents.value.length > 0)
    || (props.message.subAgentEvents?.length ?? 0) > 0
  )
)

const isVisible = computed(() => {
  if (isHiddenSystemUserMessage(props.message)) return false
  if (props.message.role === 'user') return true
  return hasContent.value || visibleTools.value.length > 0 || hasVisibleSubAgents.value
    || hasImages.value || hasError.value || props.widgets.length > 0 || Boolean(props.turnDiff)
})
</script>

<template>
  <article v-if="isVisible" :id="'message-' + message.id" class="message"
    :class="[systemKind ? 'message-system' : `message-${message.role}`, { dimmed, 'is-intermediate': isIntermediate, 'is-progress': progress, 'is-compact-prompt': compactTaskPrompt && message.role === 'user' && !systemKind }]">
    <!-- user: either a real message or a system-injected card -->
    <template v-if="message.role === 'user'">
      <div v-if="systemKind" class="system-card" :class="`system-${systemKind}`">
        <button class="system-card-header" type="button" @click="systemOpen = !systemOpen">
          <span class="system-card-icon">
            <Layers :size="15" />
          </span>
          <div class="system-card-title">
            <strong>{{ systemMeta.title }}</strong>
            <small>{{ systemMeta.subtitle }}</small>
          </div>
          <ChevronDown :size="15" :class="{ rotated: systemOpen }" />
        </button>
        <div v-if="systemOpen" class="system-card-body">
          <pre class="system-card-text">{{ systemBody }}</pre>
        </div>
      </div>
      <details v-else-if="compactTaskPrompt" class="task-prompt-disclosure">
        <summary>
          <ChevronRight :size="14" class="task-prompt-chevron" aria-hidden="true" />
          <span>任务指令</span>
        </summary>
        <div class="task-prompt-body">
          <MessageAttachments :files="message.files" :images="message.images" :image-sources="imageSources"
            @preview-image="previewImage" />
          <div v-if="message.content" class="task-prompt-text">{{ message.content }}</div>
        </div>
      </details>
      <div v-else class="user-message-stack">
        <MessageAttachments :files="message.files" :images="message.images" :image-sources="imageSources"
          @preview-image="previewImage" />
        <div v-if="message.content" class="user-bubble">{{ message.content }}</div>
        <footer v-if="!streaming && !readOnly" class="user-footer" :class="{ active: isCopied }">
          <span v-if="message.createdAt" class="message-time">
            {{ formatMessageTime(message.createdAt) }}
          </span>
          <div class="user-actions">
            <button v-if="message.content" type="button" class="action-btn" :class="{ success: isCopied }"
              :title="isCopied ? '已复制' : '复制'" aria-label="复制" @click="copyContent">
              <Check v-if="isCopied" :size="13" class="action-icon success-icon" />
              <Copy v-else :size="13" class="action-icon" />
            </button>
            <button type="button" class="action-btn" title="编辑此消息" aria-label="编辑此消息" @click="emit('edit', message)">
              <Pencil :size="13" class="action-icon" />
            </button>
          </div>
        </footer>
      </div>
    </template>

    <div v-else class="assistant-message">
      <MarkdownMessage v-if="hasContent" :key="message.id" :content="displayContent"
        :streaming="smoothOutput && streaming && message.state === 'streaming' && !hasError" />
      <SubAgentActivityList v-if="!hideSubAgents" :agents="subAgents" :events="message.subAgentEvents"
        :show-summary="showSubAgentSummary" @open-sub-agent="emit('openSubAgent', $event)" />
      <div v-if="visibleTools.length" class="tool-list">
        <ToolActivityList :tools="visibleTools" :streaming="streaming" @open-diff="(path, diff) => emit('openDiff', path, diff)" />
      </div>
      <ConversationWidgets :widgets="widgets" :session-id="sessionId" :workspace="workspace" :diff="turnDiff"
        @open-diff-preview="(preview, trigger) => emit('openDiffPreview', preview, trigger)" />
      <div v-if="activeError" class="model-error-card" role="alert">
        <CircleAlert :size="16" class="model-error-icon" />
        <div class="model-error-body">
          <span v-for="(part, idx) in errorParts" :key="idx">
            <a v-if="part.type === 'link'" :href="part.url" target="_blank" rel="noreferrer" class="model-error-link">{{ part.content }}</a>
            <template v-else>{{ part.content }}</template>
          </span>
        </div>
      </div>

      <footer v-if="showFooter && !streaming && (displayContent || activeError)" class="assistant-footer">
        <div class="assistant-actions">
          <button type="button" class="action-btn" :class="{ success: isCopied }" :title="isCopied ? '已复制' : '复制回答'"
            aria-label="复制回答" @click="copyContent">
            <Check v-if="isCopied" :size="13" class="action-icon success-icon" />
            <Copy v-else :size="13" class="action-icon" />
          </button>
          <button v-if="!readOnly" type="button" class="action-btn" title="在此分叉新任务" aria-label="在此分叉新任务"
            @click="emit('branch', message)">
            <Split :size="13" class="action-icon" />
          </button>
          <button v-if="!readOnly" type="button" class="action-btn" title="重新生成" aria-label="重新生成"
            @click="emit('retry', message)">
            <RotateCcw :size="13" class="action-icon" />
          </button>
        </div>
        <span v-if="!readOnly && message.createdAt" class="message-time">
          {{ formatMessageTime(message.createdAt) }}
        </span>
      </footer>
    </div>
  </article>

  <ImagePreviewDialog :src="preview?.src" :name="preview?.name" @close="preview = null" />
</template>

<style scoped>
.dimmed {
  opacity: .32;
  transition: opacity 160ms ease;
}

.message-system {
  justify-content: center;
}

.system-card {
  width: min(100%, 670px);
  margin: 2px 0 10px;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: 12px;
}

.system-card-header {
  display: flex;
  width: 100%;
  min-height: 44px;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  color: var(--text);
  text-align: left;
  background: var(--surface);
}

.system-card-header:hover {
  background: var(--surface-hover);
}

.system-card-icon {
  display: grid;
  width: 28px;
  height: 28px;
  flex: none;
  place-items: center;
  border-radius: 8px;
}

.system-memory .system-card-icon {
  color: var(--accent);
  background: var(--accent-soft);
}

.system-card-title {
  min-width: 0;
  flex: 1;
}

.system-card-title strong,
.system-card-title small {
  display: block;
}

.system-card-title strong {
  font-size: 13px;
}

.system-card-title small {
  margin-top: 2px;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.system-card-header>.lucide-chevron-down {
  flex: none;
  color: var(--text-muted);
  transition: transform 160ms ease;
}

.system-card-header>.lucide-chevron-down.rotated {
  transform: rotate(180deg);
}

.system-card-body {
  padding: 10px 14px;
  background: var(--surface-raised);
  border-top: 1px solid var(--border);
}

.system-card-text {
  margin: 0;
  color: var(--text-secondary);
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 11.5px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.user-footer {
  position: absolute;
  top: 100%;
  display: flex;
  align-items: center;
  gap: 8px;
  padding-top: 2px;
  user-select: none;
  opacity: 0;
  pointer-events: none;
  transform: translateY(-2px);
  transition: opacity 160ms ease, transform 160ms ease;
}

.assistant-footer {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  user-select: none;
}

.user-footer {
  right: 0;
}

.message:hover .user-footer,
.user-message-stack:hover .user-footer,
.user-footer:focus-within,
.user-footer.active {
  opacity: 1;
  pointer-events: auto;
  transform: translateY(0);
}

.task-prompt-disclosure {
  width: 100%;
  min-width: 0;
  color: var(--text-muted);
}

.task-prompt-disclosure > summary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 28px;
  padding: 2px 0;
  font-size: 13px;
  line-height: 1.45;
  list-style: none;
  cursor: pointer;
}

.task-prompt-disclosure > summary::-webkit-details-marker {
  display: none;
}

.task-prompt-disclosure > summary:hover,
.task-prompt-disclosure[open] > summary {
  color: var(--text-secondary);
}

.task-prompt-chevron {
  flex: none;
  transition: transform 120ms ease;
}

.task-prompt-disclosure[open] .task-prompt-chevron {
  transform: rotate(90deg);
}

.task-prompt-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 4px 0 8px 6px;
  padding: 6px 0 6px 14px;
  border-left: 1px solid var(--border);
}

.task-prompt-text {
  color: var(--text-secondary);
  font-size: 13.5px;
  line-height: 1.65;
  white-space: pre-wrap;
  word-break: break-word;
  overflow-wrap: anywhere;
  user-select: text;
}

.assistant-actions,
.user-actions {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.action-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  transition: background 140ms ease, color 140ms ease;
}

.action-btn:hover {
  background: var(--surface-hover);
  color: var(--text);
}

.action-btn.success {
  color: var(--accent, #10b981);
}

.action-icon {
  flex: none;
}

.message-time {
  font-size: 11px;
  color: var(--text-muted);
  margin-left: 2px;
}

.tool-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 4px 0;
}

.message.is-intermediate { margin-bottom: 16px; }
.message.is-progress { margin: 3px 0; }
.message.is-progress:has(.markdown-body) { margin: 8px 0 4px; }
.is-progress .assistant-message { font-size: 14px; line-height: 1.68; }

.model-error-card {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  margin: 8px 0 10px;
  padding: 12px 16px;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: var(--surface);
  color: var(--text);
  font-size: 13.5px;
  line-height: 1.55;
  user-select: text;
}

.model-error-icon {
  flex: none;
  margin-top: 2px;
  color: var(--text-secondary);
}

.model-error-body {
  flex: 1;
  min-width: 0;
  white-space: pre-wrap;
  word-break: break-word;
  overflow-wrap: anywhere;
}

.model-error-link {
  color: var(--accent);
  text-decoration: underline;
  text-underline-offset: 2px;
  word-break: break-all;
  transition: opacity 140ms ease;
}

.model-error-link:hover {
  opacity: 0.82;
}
</style>
