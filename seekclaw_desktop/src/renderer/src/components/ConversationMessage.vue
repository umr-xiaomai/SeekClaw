<script setup lang="ts">
import {
  Braces,
  Check,
  ChevronDown,
  CircleAlert,
  Eye,
  Gavel,
  Image as ImageIcon,
  Layers,
  LoaderCircle,
  MessageSquarePlus,
  RefreshCw,
  Wrench
} from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ChatMessage } from '../types'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import MarkdownMessage from './MarkdownMessage.vue'

const props = withDefaults(defineProps<{
  message: ChatMessage
  imageSources?: Record<string, string>
  /** Dimmed while the conversation search does not match this message. */
  dimmed?: boolean
}>(), { imageSources: () => ({}) })

const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
  continue: [message: ChatMessage]
  regenerate: [message: ChatMessage]
}>()

const thinkingOpen = ref(false)
const systemOpen = ref(false)
const preview = ref<{ src: string; name: string } | null>(null)

const regularTools = computed(() => (props.message.tools ?? []).filter((tool) => !tool.diff))
const editedTools = computed(() => (props.message.tools ?? []).filter((tool) => tool.diff && tool.filePath))

/** System-injected messages (compaction / review / truncation) get dedicated cards. */
const systemKind = computed<'memory' | 'review' | 'truncated' | null>(() => {
  const content = props.message.content
  if (content.startsWith('>>> [Context compaction]')) return 'memory'
  if (content.startsWith('>>> [评审团反馈]')) return 'review'
  if (content.startsWith('>>> [output truncated]')) return 'truncated'
  return null
})

const systemBody = computed(() => {
  const content = props.message.content
  const marker = content.indexOf('\n')
  return marker >= 0 ? content.slice(marker + 1).trim() : ''
})

/** Parses "### provider/model（N 个问题）\n1. ..." sections into per-model review cards. */
const reviewSections = computed(() => {
  const body = systemBody.value
  const lines = body.split('\n')
  const sections: Array<{ ref: string; lines: string[] }> = []
  let current: { ref: string; lines: string[] } | null = null
  for (const line of lines) {
    const heading = /^###\s+(.+?)(?:（(\d+)\s*个问题）)?$/.exec(line.trim())
    if (heading) {
      current = { ref: (heading[1] ?? heading[0]).trim(), lines: [] }
      sections.push(current)
    } else if (current && line.trim()) {
      current.lines.push(line.trim())
    }
  }
  return sections
})

const thinkingLabel = computed(() => {
  if (props.message.state === 'thinking') {
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
</script>

<template>
  <article class="message" :class="[`message-${message.role}`, { dimmed }]">
    <!-- user: either a real message or a system-injected card -->
    <template v-if="message.role === 'user'">
      <div v-if="systemKind" class="system-card" :class="`system-${systemKind}`">
        <button class="system-card-header" type="button" @click="systemOpen = !systemOpen">
          <span class="system-card-icon">
            <Layers v-if="systemKind === 'memory'" :size="15" />
            <Gavel v-else-if="systemKind === 'review'" :size="15" />
            <CircleAlert v-else :size="15" />
          </span>
          <div class="system-card-title">
            <strong>
              {{ systemKind === 'memory' ? '记忆压缩' : systemKind === 'review' ? '评审团反馈' : '输出截断' }}
            </strong>
            <small v-if="systemKind === 'memory'">较早的对话已被总结，以保持上下文可容纳</small>
            <small v-else-if="systemKind === 'review'">{{ reviewSections.length }} 个评审模型报告</small>
            <small v-else>上一轮输出达到长度上限，已要求模型继续</small>
          </div>
          <ChevronDown :size="15" :class="{ rotated: systemOpen }" />
        </button>
        <div v-if="systemOpen" class="system-card-body">
          <template v-if="systemKind === 'review' && reviewSections.length > 0">
            <div v-for="section in reviewSections" :key="section.ref" class="review-section">
              <strong>{{ section.ref }}</strong>
              <ul>
                <li v-for="(line, index) in section.lines" :key="index">{{ line }}</li>
              </ul>
            </div>
          </template>
          <pre v-else class="system-card-text">{{ systemBody }}</pre>
        </div>
      </div>
      <div v-else class="user-message-stack">
        <div v-if="message.images?.length" class="user-image-grid" :class="{ single: message.images.length === 1 }">
          <button
            v-for="image in message.images"
            :key="image.id"
            type="button"
            class="user-image-button"
            :title="`预览 ${image.name}`"
            @click="previewImage(image.id, image.name)"
          >
            <img :src="imageUrl(image.id)" :alt="image.name">
            <span>{{ image.name }}</span>
          </button>
        </div>
        <div v-if="message.content" class="user-bubble">{{ message.content }}</div>
      </div>
    </template>

    <div v-else class="assistant-message">
      <button
        v-if="message.thinking"
        class="thinking-toggle"
        :class="{ active: message.state === 'thinking' }"
        @click="thinkingOpen = !thinkingOpen"
      >
        <LoaderCircle v-if="message.state === 'thinking'" :size="16" class="spin" />
        <Check v-else :size="16" />
        <span>{{ thinkingLabel }}</span>
        <ChevronDown :size="15" :class="{ rotated: thinkingOpen }" />
      </button>
      <div v-if="thinkingOpen && message.thinking" class="thinking-content">{{ message.thinking }}</div>

      <div v-if="message.viewedImages?.length" class="image-view-list" aria-label="AI 已查看的图片">
        <button
          v-for="image in message.viewedImages"
          :key="image.id"
          type="button"
          class="image-view-row"
          :class="{ previewable: Boolean(imageUrl(image.id)) }"
          :disabled="!imageUrl(image.id)"
          :title="imageUrl(image.id) ? `预览 ${image.name}` : image.name"
          @click="previewImage(image.id, image.name)"
        >
          <span class="image-view-thumbnail">
            <img v-if="imageUrl(image.id)" :src="imageUrl(image.id)" :alt="image.name">
            <ImageIcon v-else :size="16" />
          </span>
          <Eye :size="15" />
          <span>已查看</span>
          <small>{{ image.name }}</small>
        </button>
      </div>

      <div v-if="regularTools.length" class="tool-list">
        <div v-for="tool in regularTools" :key="tool.id" class="tool-row">
          <LoaderCircle v-if="tool.state === 'running'" :size="15" class="spin" />
          <CircleAlert v-else-if="tool.state === 'error'" :size="15" />
          <Wrench v-else :size="15" />
          <span>{{ tool.name }}</span>
          <small v-if="tool.detail">{{ tool.detail }}</small>
        </div>
      </div>

      <section v-if="editedTools.length" class="change-card" aria-label="代码修改">
        <header class="change-card-header">
          <div class="change-card-icon"><Braces :size="18" /></div>
          <div class="change-card-title">
            <strong>已编辑 {{ editedTools.length }} 个文件</strong>
            <span>
              <b class="change-added">+{{ editStats.added }}</b>
              <b class="change-removed">-{{ editStats.removed }}</b>
            </span>
          </div>
          <span class="change-card-state">已完成</span>
        </header>
        <button
          v-for="tool in editedTools"
          :key="tool.id"
          type="button"
          class="change-file-row"
          :title="`查看 ${tool.filePath} 的 Diff`"
          @click="emit('openDiff', tool.filePath!, tool.diff!)"
        >
          <span class="change-file-path">{{ tool.filePath }}</span>
          <span class="change-file-stats">
            <b class="change-added">+{{ diffStats(tool.diff).added }}</b>
            <b class="change-removed">-{{ diffStats(tool.diff).removed }}</b>
          </span>
        </button>
      </section>

      <MarkdownMessage v-if="message.content" :content="message.content" />
      <div
        v-if="message.state === 'thinking' || message.state === 'streaming'"
        class="response-placeholder"
        aria-label="AI 正在思考"
      >
        <span /><span /><span />
      </div>

      <div v-if="message.modelRef && message.state !== 'streaming' && message.state !== 'thinking'" class="assistant-meta">
        <span class="model-badge">{{ message.modelRef }}</span>
        <button class="continue-button" type="button" title="继续生成" @click="emit('continue', message)">
          <MessageSquarePlus :size="13" />继续
        </button>
        <button class="continue-button regenerate-button" type="button" title="重新生成这条回答" @click="emit('regenerate', message)">
          <RefreshCw :size="13" />重新生成
        </button>
      </div>
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

.system-review .system-card-icon {
  color: var(--accent);
  background: var(--accent-soft);
}

.system-truncated .system-card-icon {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 12%, transparent);
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

.system-card-header > .lucide-chevron-down {
  flex: none;
  color: var(--text-muted);
  transition: transform 160ms ease;
}

.system-card-header > .lucide-chevron-down.rotated {
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

.review-section + .review-section {
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px dashed var(--border);
}

.review-section strong {
  font-size: 12px;
}

.review-section ul {
  margin: 6px 0 0;
  padding-left: 18px;
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.65;
}

.thinking-toggle > .lucide-chevron-down {
  transition: transform 160ms ease;
}

.thinking-toggle > .lucide-chevron-down.rotated {
  transform: rotate(180deg);
}

.assistant-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 10px;
}

.model-badge {
  padding: 3px 9px;
  color: var(--text-secondary);
  font-size: 10.5px;
  font-weight: 600;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.continue-button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  color: var(--accent);
  font-size: 11.5px;
  background: var(--accent-soft);
  border-radius: 999px;
  opacity: 0;
  transition: opacity 150ms ease;
}

.message:hover .continue-button,
.continue-button:focus-visible {
  opacity: 1;
}

.regenerate-button {
  color: var(--text-secondary);
  background: var(--surface);
  border: 1px solid var(--border);
}

.regenerate-button:hover {
  color: var(--text);
  border-color: var(--border-strong);
}
</style>
