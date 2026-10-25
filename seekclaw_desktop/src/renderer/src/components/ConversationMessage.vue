<script setup lang="ts">
import {
  Braces,
  Check,
  ChevronDown,
  CircleAlert,
  Copy,
  Eye,
  FileText,
  Image as ImageIcon,
  Layers,
  LoaderCircle,
  Pencil,
  Search,
  Split,
  SquareTerminal,
  Wrench
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ChatMessage, ToolActivity } from '../types'
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
  /** True when this assistant message is an intermediate step in a multi-step turn. */
  isIntermediate?: boolean
}>(), { imageSources: () => ({}), showFooter: false, isIntermediate: false })

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

const regularTools = computed(() => (props.message.tools ?? []).filter((tool) => !tool.diff))
const editedTools = computed(() => (props.message.tools ?? []).filter((tool) => tool.diff && tool.filePath))

function getToolIcon(tool: ToolActivity) {
  if (tool.state === 'running') return LoaderCircle
  if (tool.state === 'error') return CircleAlert
  const name = tool.name?.toLowerCase() || ''
  if (['bash', 'cmd', 'powershell', 'sh', 'terminal', 'exec_command'].includes(name)) return SquareTerminal
  if (['read_file', 'cat', 'view_file'].includes(name)) return FileText
  if (['glob', 'list_dir', 'search', 'search_code', 'find'].includes(name)) return Search
  if (['update_plan', 'plan'].includes(name)) return Layers
  return Wrench
}

function getToolVerb(tool: ToolActivity): string {
  const name = tool.name?.toLowerCase() || ''
  if (tool.state === 'running') {
    if (['bash', 'cmd', 'powershell', 'sh', 'terminal', 'exec_command'].includes(name)) return '正在运行'
    if (['read_file', 'cat', 'view_file'].includes(name)) return '正在读取'
    if (['write_file', 'apply_patch', 'edit_file'].includes(name)) return '正在修改'
    if (['glob', 'list_dir', 'search', 'search_code', 'find'].includes(name)) return '正在搜索'
    if (['update_plan', 'plan'].includes(name)) return '正在更新计划'
    return '正在调用'
  }
  if (tool.state === 'error') {
    return '运行失败'
  }
  if (['bash', 'cmd', 'powershell', 'sh', 'terminal', 'exec_command'].includes(name)) return '已运行'
  if (['read_file', 'cat', 'view_file'].includes(name)) return '已读取'
  if (['write_file', 'apply_patch', 'edit_file'].includes(name)) return '已修改'
  if (['glob', 'list_dir', 'search', 'search_code', 'find'].includes(name)) return '已搜索'
  if (['update_plan', 'plan'].includes(name)) return '已更新计划'
  return '已调用'
}

/** System-injected messages (compaction / verification) get dedicated cards. */
const systemKind = computed<'memory' | 'verify' | null>(() => {
  const content = props.message.content
  if (content.startsWith('>>> [Context compaction]')) return 'memory'
  if (content.startsWith('The automatic verification step')) return 'verify'
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
const hasImages = computed(() =>
  (props.message.viewedImages?.length ?? 0) > 0 ||
  (props.message.images?.length ?? 0) > 0 ||
  (props.message.files?.length ?? 0) > 0
)

const isVisible = computed(() => {
  if (props.message.content?.startsWith('>>> [output truncated]')) return false
  if (props.message.role === 'user') return true
  if (props.streaming) return true
  return hasContent.value || hasThinking.value || hasTools.value || hasImages.value || hasError.value
})

const isToolsOnly = computed(() => {
  if (props.message.role !== 'assistant') return false
  return hasTools.value && !hasContent.value && !hasThinking.value && !hasError.value
})

function getToolTarget(tool: ToolActivity): string {
  if (tool.filePath) {
    return tool.filePath
  }

  let text = tool.detail?.trim() || ''
  if (!text) return tool.name

  if (text.startsWith('{') && text.endsWith('}')) {
    try {
      const obj = JSON.parse(text)
      if (typeof obj.path === 'string') return obj.path
      if (typeof obj.file_path === 'string') return obj.file_path
      if (typeof obj.file === 'string') return obj.file
      if (typeof obj.directory === 'string') return obj.directory
      if (typeof obj.dir === 'string') return obj.dir
      if (typeof obj.command === 'string') return obj.command.trim().replace(/\r?\n/g, ' ')
      if (typeof obj.cmd === 'string') return obj.cmd.trim().replace(/\r?\n/g, ' ')
      if (typeof obj.pattern === 'string') return obj.pattern
      if (typeof obj.query === 'string') return obj.query
    } catch {
      // fallback
    }
  }

  const pathMatch = text.match(/(?:^|\s*)(?:path|file_path|file|directory|dir)\s*:\s*([^,]+)/i)
  if (pathMatch?.[1]) {
    return pathMatch[1].trim()
  }

  const cmdMatch = text.match(/(?:^|\s*)(?:command|cmd)\s*:\s*([^,]+)/i)
  if (cmdMatch?.[1]) {
    return cmdMatch[1].trim()
  }

  const patternMatch = text.match(/(?:^|\s*)(?:pattern|query)\s*:\s*([^,]+)/i)
  if (patternMatch?.[1]) {
    return patternMatch[1].trim()
  }

  const readLinesMatch = text.match(/Read\s+\d+\s+lines\s+from\s+(.+)/i)
  if (readLinesMatch?.[1]) {
    return readLinesMatch[1].trim()
  }

  if (text.includes('\n')) {
    const firstLine = text.split('\n')[0]?.trim() || ''
    if (/^\d+\t/.test(firstLine) || firstLine.startsWith('#') || firstLine.startsWith('using ') || firstLine.startsWith('import ')) {
      return tool.name
    }
    return firstLine || tool.name
  }
  return text
}
</script>

<template>
  <article v-if="isVisible" class="message"
    :class="[`message-${message.role}`, { dimmed, 'is-intermediate': isIntermediate, 'is-tools-only': isToolsOnly }]">
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

      <div v-if="hasThinking" class="thinking-wrapper">
        <button class="thinking-toggle" :class="{ active: streaming && message.state === 'thinking' }"
          @click="thinkingOpen = !thinkingOpen">
          <LoaderCircle v-if="streaming && message.state === 'thinking'" :size="15" class="spin" />
          <Check v-else :size="15" />
          <span>{{ thinkingLabel }}</span>
          <ChevronDown :size="14" :class="{ rotated: thinkingOpen }" />
        </button>
        <div v-if="thinkingOpen && message.thinking" class="thinking-content">{{ message.thinking }}</div>
      </div>

      <div v-if="isIntermediate && hasContent" class="step-preamble">
        <MarkdownMessage :content="displayContent" />
      </div>
      <MarkdownMessage v-else-if="hasContent" :content="displayContent" />

      <div v-if="regularTools.length" class="tool-list">
        <div v-for="tool in regularTools" :key="tool.id" class="tool-item"
          :class="{ 'is-running': tool.state === 'running', 'is-error': tool.state === 'error' }"
          :title="getToolTarget(tool)">
          <component :is="getToolIcon(tool)" :size="14" class="tool-item-icon"
            :class="{ spin: tool.state === 'running' }" />
          <span class="tool-verb">{{ getToolVerb(tool) }}</span>
          <span class="tool-target">{{ getToolTarget(tool) }}</span>
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
      <div v-if="activeError" class="model-error-card" role="alert">
        <CircleAlert :size="16" class="model-error-icon" />
        <div class="model-error-body">
          <span v-for="(part, idx) in errorParts" :key="idx">
            <a v-if="part.type === 'link'" :href="part.url" target="_blank" rel="noreferrer" class="model-error-link">{{ part.content }}</a>
            <template v-else>{{ part.content }}</template>
          </span>
        </div>
      </div>

      <div v-if="streaming && (message.state === 'thinking' || message.state === 'streaming')"
        class="response-placeholder" aria-label="AI 正在思考">
        <span /><span /><span />
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

.thinking-wrapper {
  margin: 0 0 6px;
}

.tool-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin: 3px 0 10px;
}

.response-placeholder {
  margin: 4px 0;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 0;
}

.tool-item {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  color: var(--text-muted);
  font-size: 13px;
  line-height: 1.45;
  user-select: text;
}

.tool-item.is-running {
  color: var(--accent);
}

.tool-item.is-error {
  color: var(--danger);
}

.tool-item-icon {
  flex: none;
  color: var(--text-muted);
}

.tool-item.is-running .tool-item-icon {
  color: var(--accent);
}

.tool-item.is-error .tool-item-icon {
  color: var(--danger);
}

.tool-verb {
  color: var(--text-secondary);
  font-size: 13px;
  flex: none;
}

.tool-target {
  color: var(--text-secondary);
  font-family: var(--font-mono, "Cascadia Code", Consolas, monospace);
  font-size: 12.5px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 720px;
}

.step-preamble {
  margin: 2px 0 6px;
  color: var(--text-secondary);
  font-size: 13.5px;
  line-height: 1.55;
}

.step-preamble :deep(.markdown-body p) {
  margin: 0 0 4px;
  color: var(--text-secondary);
  font-size: 13.5px;
}

.message.is-intermediate {
  margin-bottom: 8px;
}

.message.is-intermediate.is-tools-only {
  margin-bottom: 0;
}

.message.is-intermediate.is-tools-only .tool-list {
  margin-top: 0;
  margin-bottom: 10px;
}

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
