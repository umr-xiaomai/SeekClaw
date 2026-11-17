<script setup lang="ts">
import {
  Check,
  ChevronDown,
  CircleAlert,
  Copy,
  Image as ImageIcon,
  Layers,
  LoaderCircle,
  Pencil,
  Split
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref } from 'vue'
import type { ChatMessage, ConversationWidget, SubAgentActivity } from '../types'
import { fileBadgeText, fileExtClass, formatMessageTime } from '../app-helpers'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import ToolActivityList from './ToolActivityList.vue'
import ConversationWidgets from './ConversationWidgets.vue'
import MarkdownMessage from './MarkdownMessage.vue'
import SubAgentActivityList from './SubAgentActivityList.vue'
import { messageSubAgents } from '../subagent-activity'
import { isHiddenSystemUserMessage, isSystemBannerUserMessage } from '../internal-messages'
import { preparingToolLabel, phaseStatusText } from './tool-presentation'

const props = withDefaults(defineProps<{
  message: ChatMessage
  widgets?: ConversationWidget[]
  sessionId?: string
  workspace?: string
  phase?: string
  imageSources?: Record<string, string>
  /** Dimmed while the conversation search does not match this message. */
  dimmed?: boolean
  /** True while this bubble belongs to the actively running turn. Gates the "..." placeholder. */
  streaming?: boolean
  /** Search reveals all received text immediately, even in a live response. */
  smoothOutput?: boolean
  /** Only true at the final bubble tail of an assistant turn. */
  showFooter?: boolean
  /** True when this assistant message is an intermediate step in a multi-step turn. */
  isIntermediate?: boolean
  /** A parent process summary can render these activities outside its disclosure. */
  hideSubAgents?: boolean
  /** Control whether a fallback sub-agent summary row renders when events are absent. */
  showSubAgentSummary?: boolean
}>(), { imageSources: () => ({}), widgets: () => [], showFooter: false, isIntermediate: false, smoothOutput: true, showSubAgentSummary: true })

const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
  branch: [message: ChatMessage]
  edit: [message: ChatMessage]
  openSubAgent: [activity: SubAgentActivity]
}>()

const thinkingOpen = ref(false)
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

const isThinking = computed(() => Boolean(
  props.streaming
  && props.message.state === 'thinking'
  && !props.message.preparingTool
  && (props.message.thinkingActive ?? true)
))
const thinkingLabel = computed(() => isThinking.value ? '正在思考' : '已完成思考')

function imageUrl(id: string): string | undefined {
  return props.imageSources[id]
}

function previewImage(id: string, name: string): void {
  const src = imageUrl(id)
  if (src) preview.value = { src, name }
}

function openFileLocation(path?: string): void {
  if (path) {
    window.seekclaw?.showItemInFolder?.(path)
  }
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
const hasThinking = computed(() => Boolean(props.message.thinking?.trim()))
const hasTools = computed(() => (props.message.tools?.length ?? 0) > 0)
const subAgents = computed(() => messageSubAgents(props.message))
const visibleTools = computed(() => {
  const represented = new Set([...(props.message.subAgents ?? []), ...subAgents.value].map((agent) => agent.sourceToolId).filter(Boolean))
  return props.message.tools?.filter((tool) => !represented.has(tool.id)) ?? []
})
const hasImages = computed(() =>
  (props.message.viewedImages?.length ?? 0) > 0 ||
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
  if (props.streaming) return true
  return hasContent.value || hasThinking.value || visibleTools.value.length > 0 || hasVisibleSubAgents.value
    || hasImages.value || hasError.value || props.widgets.length > 0
})

const isToolsOnly = computed(() => {
  if (props.message.role !== 'assistant') return false
  return visibleTools.value.length > 0 && !hasContent.value && !hasThinking.value && !hasError.value
})

const hasRunningTool = computed(() => (props.message.tools ?? []).some((tool) => tool.state === 'running'))
const isPreparingTool = computed(() => Boolean(
  props.streaming
  && props.message.preparingTool
  && !isThinking.value
  && !hasRunningTool.value
))
const isWaitingResponse = computed(() => Boolean(
  props.streaming
  && (props.message.state === 'thinking' || props.message.state === 'streaming')
  && !isThinking.value
  && !isPreparingTool.value
  && !hasRunningTool.value
  && !hasContent.value
  && !hasError.value
  && !subAgents.value.length
))
const liveStatusLabel = computed(() => {
  if (isPreparingTool.value) return preparingToolLabel(props.message.preparingTool)
  const p = phaseStatusText(props.phase)
  if (p) return p.endsWith('…') ? p : `${p}…`
  return '正在等待模型响应…'
})

</script>

<template>
  <article v-if="isVisible" :id="'message-' + message.id" class="message"
    :class="[systemKind ? 'message-system' : `message-${message.role}`, { dimmed, 'is-intermediate': isIntermediate, 'is-tools-only': isToolsOnly }]">
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
      <div v-else class="user-message-stack">
        <div v-if="message.files?.length" class="user-files-grid">
          <button v-for="file in message.files" :key="file.id" type="button" class="user-file-chip"
            :title="`在文件夹中显示: ${file.path}`" @click="openFileLocation(file.path)">
            <span class="file-ext-badge" :class="fileExtClass(file.extension)">
              {{ fileBadgeText(file.extension) }}
            </span>
            <span class="file-chip-name">{{ file.name }}</span>
          </button>
        </div>
        <div v-if="message.images?.length" class="user-image-grid" :class="{ single: message.images.length === 1 }">
          <button v-for="image in message.images" :key="image.id" type="button" class="user-image-button"
            :title="`预览 ${image.name}`" @click="previewImage(image.id, image.name)">
            <img :src="imageUrl(image.id)" :alt="image.name">
            <span>{{ image.name }}</span>
          </button>
        </div>
        <div v-if="message.content" class="user-bubble">{{ message.content }}</div>
        <footer v-if="!streaming" class="user-footer" :class="{ active: isCopied }">
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

    <div v-else class="assistant-message" :class="{ 'tools-only': isToolsOnly }">
      <details v-if="message.viewedImages?.length" class="image-activity">
        <summary><ImageIcon :size="16" /> 已查看 {{ message.viewedImages.length }} 张图像 <ChevronDown :size="14" /></summary>
        <div class="image-activity-thumbnails">
          <button v-for="image in message.viewedImages" :key="image.id" type="button" :disabled="!imageUrl(image.id)"
            :title="image.name" @click="previewImage(image.id, image.name)">
            <img v-if="imageUrl(image.id)" :src="imageUrl(image.id)" :alt="image.name" loading="lazy">
            <span v-else>{{ image.name }}</span>
          </button>
        </div>
      </details>

      <div v-if="hasThinking || isThinking" class="thinking-wrapper">
        <button type="button" class="thinking-toggle" :class="{ active: isThinking }"
          :disabled="!hasThinking" @click="hasThinking && (thinkingOpen = !thinkingOpen)">
          <span :class="{ 'thinking-shimmer': isThinking }">{{ thinkingLabel }}</span>
          <ChevronDown v-if="hasThinking && !isThinking" :size="14" :class="{ rotated: thinkingOpen }" />
        </button>
        <div v-if="thinkingOpen && message.thinking" class="thinking-content">{{ message.thinking }}</div>
      </div>

      <MarkdownMessage v-if="hasContent" :key="message.id" :content="displayContent"
        :streaming="smoothOutput && streaming && message.state === 'streaming' && !hasError" />
      <SubAgentActivityList v-if="!hideSubAgents" :agents="subAgents" :events="message.subAgentEvents"
        :show-summary="showSubAgentSummary" @open-sub-agent="emit('openSubAgent', $event)" />
      <div v-if="visibleTools.length" class="tool-list">
        <ToolActivityList :tools="visibleTools" :streaming="streaming" @open-diff="(path, diff) => emit('openDiff', path, diff)" />
      </div>
      <ConversationWidgets :widgets="widgets" :session-id="sessionId" :workspace="workspace" />
      <div v-if="activeError" class="model-error-card" role="alert">
        <CircleAlert :size="16" class="model-error-icon" />
        <div class="model-error-body">
          <span v-for="(part, idx) in errorParts" :key="idx">
            <a v-if="part.type === 'link'" :href="part.url" target="_blank" rel="noreferrer" class="model-error-link">{{ part.content }}</a>
            <template v-else>{{ part.content }}</template>
          </span>
        </div>
      </div>

      <div v-if="isPreparingTool || isWaitingResponse" class="live-status-row" role="status" aria-live="polite">
        <LoaderCircle :size="14" class="spin live-status-icon" aria-hidden="true" />
        <span>{{ liveStatusLabel }}</span>
      </div>

      <footer v-if="showFooter && !streaming && (displayContent || activeError)" class="assistant-footer" :class="{ active: isCopied }">
        <div class="assistant-actions">
          <button type="button" class="action-btn" :class="{ success: isCopied }" :title="isCopied ? '已复制' : '复制回答'"
            aria-label="复制回答" @click="copyContent">
            <Check v-if="isCopied" :size="13" class="action-icon success-icon" />
            <Copy v-else :size="13" class="action-icon" />
          </button>
          <button type="button" class="action-btn" title="在此分叉新任务" aria-label="在此分叉新任务"
            @click="emit('branch', message)">
            <Split :size="13" class="action-icon" />
          </button>
        </div>
        <span v-if="message.createdAt" class="message-time">
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

.thinking-toggle>.lucide-chevron-down {
  transition: transform 160ms ease;
}

.thinking-toggle>.lucide-chevron-down.rotated {
  transform: rotate(180deg);
}

.assistant-footer,
.user-footer {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  user-select: none;
  opacity: 0;
  pointer-events: none;
  transition: opacity 160ms ease;
}

.message:hover .assistant-footer,
.assistant-message:hover .assistant-footer,
.assistant-footer:focus-within,
.assistant-footer.active,
.message:hover .user-footer,
.user-message-stack:hover .user-footer,
.user-footer:focus-within,
.user-footer.active {
  opacity: 1;
  pointer-events: auto;
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

.thinking-wrapper {
  margin: 0 0 6px;
}

.tool-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 4px 0;
}

.live-status-row {
  display: flex;
  align-items: center;
  gap: 7px;
  min-height: 26px;
  margin: 4px 0 2px;
  padding: 2px 0;
  color: var(--text-muted);
  font-size: 13px;
  line-height: 1.45;
}

.live-status-icon {
  flex: none;
}

.image-activity { margin: 4px 0 14px; color: var(--text-muted); font-size: 14px; }
.image-activity summary { display: flex; align-items: center; gap: 8px; min-height: 32px; list-style: none; cursor: pointer; }
.image-activity summary:hover { color: var(--text); }
.image-activity summary::-webkit-details-marker { display: none; }
.image-activity-thumbnails { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 8px; }
.image-activity-thumbnails button { width: 96px; height: 96px; padding: 0; overflow: hidden; border: 1px solid var(--border); border-radius: 8px; background: transparent; }
.image-activity-thumbnails img { width: 100%; height: 100%; object-fit: cover; }
.message.is-intermediate { margin-bottom: 16px; }
.message.is-intermediate.is-tools-only { margin-bottom: 8px; }

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
