<script setup lang="ts">
import {
  Braces,
  Check,
  ChevronDown,
  CircleAlert,
  Copy,
  Eye,
  Image as ImageIcon,
  Layers,
  LoaderCircle,
  Pencil,
  Split,
  Wrench
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref } from 'vue'
import type { ChatMessage } from '../types'
import { fileBadgeText, fileExtClass, formatMessageTime } from '../app-helpers'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import MarkdownMessage from './MarkdownMessage.vue'

const props = withDefaults(defineProps<{
  message: ChatMessage
  imageSources?: Record<string, string>
  /** Dimmed while the conversation search does not match this message. */
  dimmed?: boolean
  /** True while this bubble belongs to the actively running turn. Gates the "..." placeholder. */
  streaming?: boolean
  /** Only true at the final bubble tail of an assistant turn. */
  showFooter?: boolean
}>(), { imageSources: () => ({}), showFooter: false })

const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
  branch: [message: ChatMessage]
  edit: [message: ChatMessage]
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
  if (!props.message.content) return
  try {
    await navigator.clipboard.writeText(props.message.content)
    isCopied.value = true
    if (copyTimer) clearTimeout(copyTimer)
    copyTimer = setTimeout(() => {
      isCopied.value = false
    }, 2000)
  } catch (err) {
    console.error('Failed to copy to clipboard', err)
  }
}

const regularTools = computed(() => (props.message.tools ?? []).filter((tool) => !tool.diff))
const editedTools = computed(() => (props.message.tools ?? []).filter((tool) => tool.diff && tool.filePath))

/** System-injected messages (compaction / verification) get dedicated cards. */
const systemKind = computed<'memory' | 'verify' | null>(() => {
  const content = props.message.content
  if (content.startsWith('>>> [Context compaction]')) return 'memory'
  if (content.startsWith('The automatic verification step failed')) return 'verify'
  return null
})

const systemMeta = computed(() => {
  switch (systemKind.value) {
    case 'memory':
      return { title: '记忆压缩', subtitle: '较早的对话已被总结，以保持上下文可容纳' }
    case 'verify':
      return { title: '构建验证', subtitle: '修改后自动运行构建未通过，已自动继续修复' }
    default:
      return { title: '', subtitle: '' }
  }
})

const systemBody = computed(() => {
  const content = props.message.content
  const marker = content.indexOf('\n')
  return marker >= 0 ? content.slice(marker + 1).trim() : ''
})

const thinkingLabel = computed(() => {
  if (props.streaming && props.message.state === 'thinking') {
    const chars = props.message.thinking?.length ?? 0
    return chars > 0 ? `正在思考 · 已 ${chars.toLocaleString()} 字` : '正在思考'
  }
  return '已完成思考'
})

function imageUrl(id: string): string | undefined {
  return props.imageSources[id]
}

function previewImage(id: string, name: string): void {
  const src = imageUrl(id)
  if (src) preview.value = { src, name }
}

function diffStats(diff?: string): { added: number; removed: number } {
  if (!diff) return { added: 0, removed: 0 }
  return diff.split(/\r?\n/).reduce((stats, line) => {
    if (line.startsWith('+++') || line.startsWith('---')) return stats
    if (line.startsWith('+')) stats.added++
    else if (line.startsWith('-')) stats.removed++
    return stats
  }, { added: 0, removed: 0 })
}

const editStats = computed(() => editedTools.value.reduce((stats, tool) => {
  const current = diffStats(tool.diff)
  stats.added += current.added
  stats.removed += current.removed
  return stats
}, { added: 0, removed: 0 }))

function openFileLocation(path?: string): void {
  if (path) {
    window.seekclaw?.showItemInFolder?.(path)
  }
}
</script>

<template>
  <article v-if="!message.content?.startsWith('>>> [output truncated]')" class="message" :class="[`message-${message.role}`, { dimmed }]">
    <!-- user: either a real message or a system-injected card -->
    <template v-if="message.role === 'user'">
      <div v-if="systemKind" class="system-card" :class="`system-${systemKind}`">
        <button class="system-card-header" type="button" @click="systemOpen = !systemOpen">
          <span class="system-card-icon">
            <Layers v-if="systemKind === 'memory'" :size="15" />
            <CircleAlert v-else :size="15" />
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
          <button
            v-for="file in message.files"
            :key="file.id"
            type="button"
            class="user-file-chip"
            :title="`在文件夹中显示: ${file.path}`"
            @click="openFileLocation(file.path)"
          >
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
        <footer
          v-if="!streaming"
          class="user-footer"
          :class="{ active: isCopied }"
        >
          <span v-if="message.createdAt" class="message-time">
            {{ formatMessageTime(message.createdAt) }}
          </span>
          <div class="user-actions">
            <button
              v-if="message.content"
              type="button"
              class="action-btn"
              :class="{ success: isCopied }"
              :title="isCopied ? '已复制' : '复制'"
              aria-label="复制"
              @click="copyContent"
            >
              <Check v-if="isCopied" :size="13" class="action-icon success-icon" />
              <Copy v-else :size="13" class="action-icon" />
            </button>
            <button
              type="button"
              class="action-btn"
              title="编辑此消息"
              aria-label="编辑此消息"
              @click="emit('edit', message)"
            >
              <Pencil :size="13" class="action-icon" />
            </button>
          </div>
        </footer>
      </div>
    </template>

    <div v-else class="assistant-message">
      <div v-if="message.viewedImages?.length" class="image-view-list" aria-label="AI 已查看的图片">
        <button v-for="image in message.viewedImages" :key="image.id" type="button" class="image-view-row"
          :class="{ previewable: Boolean(imageUrl(image.id)) }" :disabled="!imageUrl(image.id)"
          :title="imageUrl(image.id) ? `预览 ${image.name}` : image.name" @click="previewImage(image.id, image.name)">
          <span class="image-view-thumbnail">
            <img v-if="imageUrl(image.id)" :src="imageUrl(image.id)" :alt="image.name">
            <ImageIcon v-else :size="16" />
          </span>
          <Eye :size="15" />
          <span>已查看</span>
          <small>{{ image.name }}</small>
        </button>
      </div>

      <div v-if="message.thinking || regularTools.length || editedTools.length" class="assistant-activity">
        <button v-if="message.thinking" class="thinking-toggle"
          :class="{ active: streaming && message.state === 'thinking' }" @click="thinkingOpen = !thinkingOpen">
          <LoaderCircle v-if="streaming && message.state === 'thinking'" :size="15" class="spin" />
          <Check v-else :size="15" />
          <span>{{ thinkingLabel }}</span>
          <ChevronDown :size="14" :class="{ rotated: thinkingOpen }" />
        </button>
        <div v-if="thinkingOpen && message.thinking" class="thinking-content">{{ message.thinking }}</div>

        <div v-if="regularTools.length" class="tool-list">
          <div v-for="tool in regularTools" :key="tool.id" class="tool-row" :title="tool.detail || tool.name">
            <LoaderCircle v-if="tool.state === 'running'" :size="14" class="spin" />
            <CircleAlert v-else-if="tool.state === 'error'" :size="14" class="tool-error" />
            <Wrench v-else :size="14" class="tool-done" />
            <span>{{ tool.name }}</span>
            <small v-if="tool.detail">{{ tool.detail }}</small>
          </div>
        </div>

        <section v-if="editedTools.length" class="change-card" aria-label="代码修改">
          <header class="change-card-header">
            <Braces :size="14" />
            <strong>已编辑 {{ editedTools.length }} 个文件</strong>
            <span class="change-file-stats">
              <b class="change-added">+{{ editStats.added }}</b>
              <b class="change-removed">-{{ editStats.removed }}</b>
            </span>
            <span class="change-card-state">已完成</span>
          </header>
          <button v-for="tool in editedTools" :key="tool.id" type="button" class="change-file-row"
            :title="`查看 ${tool.filePath} 的 Diff`" @click="emit('openDiff', tool.filePath!, tool.diff!)">
            <span class="change-file-path">{{ tool.filePath }}</span>
            <span class="change-file-stats">
              <b class="change-added">+{{ diffStats(tool.diff).added }}</b>
              <b class="change-removed">-{{ diffStats(tool.diff).removed }}</b>
            </span>
          </button>
        </section>
      </div>

      <MarkdownMessage v-if="message.content" :content="message.content" />
      <div v-if="streaming && (message.state === 'thinking' || message.state === 'streaming')"
        class="response-placeholder" aria-label="AI 正在思考">
        <span /><span /><span />
      </div>

      <footer
        v-if="showFooter && !streaming && message.content"
        class="assistant-footer"
        :class="{ active: isCopied }"
      >
        <div class="assistant-actions">
          <button
            type="button"
            class="action-btn"
            :class="{ success: isCopied }"
            :title="isCopied ? '已复制' : '复制回答'"
            aria-label="复制回答"
            @click="copyContent"
          >
            <Check v-if="isCopied" :size="13" class="action-icon success-icon" />
            <Copy v-else :size="13" class="action-icon" />
          </button>
          <button
            type="button"
            class="action-btn"
            title="在此分叉新任务"
            aria-label="在此分叉新任务"
            @click="emit('branch', message)"
          >
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

.system-card {
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

.system-verify .system-card-icon {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 10%, transparent);
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
</style>
