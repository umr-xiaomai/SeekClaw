<script setup lang="ts">
import {
  Check,
  ChevronRight,
  Code2,
  Copy,
  ExternalLink,
  Eye,
  FileText,
  Folder,
  FolderOpen,
  LoaderCircle,
  Maximize2,
  Minimize2,
  RefreshCw,
  X
} from '@lucide/vue'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  getLinkVisualMeta,
  renderLinkIconHtml,
  type FilePreviewPayload
} from '../../../shared/markdown-links'
import { highlightCodeLines } from './hljs-setup'

const MarkdownMessage = defineAsyncComponent(() => import('./MarkdownMessage.vue'))

export interface FileViewerTab {
  id: string
  href: string
  workspace?: string
  label: string
  line?: number
  endLine?: number
  focusNonce?: number
}

const FILE_VIEWER_WIDTH_STORAGE_KEY = 'seekclaw-file-viewer-width-v1'
const DEFAULT_PANEL_WIDTH = 560
const MIN_PANEL_WIDTH = 340
const MAX_PANEL_WIDTH = 1400

function loadStoredWidth(): number {
  if (typeof localStorage === 'undefined') return DEFAULT_PANEL_WIDTH
  try {
    const raw = localStorage.getItem(FILE_VIEWER_WIDTH_STORAGE_KEY)
    if (!raw) return DEFAULT_PANEL_WIDTH
    const val = Number(raw)
    if (Number.isFinite(val) && val >= MIN_PANEL_WIDTH && val <= MAX_PANEL_WIDTH) {
      return val
    }
    return DEFAULT_PANEL_WIDTH
  } catch {
    return DEFAULT_PANEL_WIDTH
  }
}

const props = defineProps<{
  tabs: FileViewerTab[]
  activeTabId: string
  workspace?: string
  width?: number
}>()

const emit = defineEmits<{
  selectTab: [id: string]
  closeTab: [id: string]
  openPath: [href: string, workspace?: string]
  resize: [width: number]
  close: []
}>()

const panelRef = ref<HTMLElement | null>(null)
const contentRef = ref<HTMLElement | null>(null)
const panelWidth = ref(loadStoredWidth())
const isMaximized = ref(false)
const resizing = ref(false)
const copiedPath = ref(false)

interface TabState {
  loading: boolean
  payload: FilePreviewPayload | null
  error: string
  mode: 'preview' | 'source'
}

const tabStates = ref<Record<string, TabState>>({})
const pdfObjectUrls = new Map<string, string>()

let resizeStartX = 0
let resizeStartWidth = 0
let resizeRafId: number | null = null
let pendingWidth = panelWidth.value
let copiedTimer: ReturnType<typeof setTimeout> | undefined
const loadGeneration = new Map<string, number>()

const activeTab = computed(() => props.tabs.find((t) => t.id === props.activeTabId) ?? props.tabs[0] ?? null)
const activeState = computed<TabState | null>(() => {
  const tab = activeTab.value
  return tab ? (tabStates.value[tab.id] ?? null) : null
})
const activePayload = computed(() => {
  const state = activeState.value
  return state?.payload && state.payload.ok ? state.payload : null
})

const effectiveWidth = computed(() => {
  if (isMaximized.value && typeof window !== 'undefined') {
    return Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(window.innerWidth * 0.68)))
  }
  return props.width ?? panelWidth.value
})

const breadcrumbs = computed(() => {
  const pathStr = activePayload.value?.path || activeTab.value?.href || ''
  if (!pathStr) return []
  const cleaned = pathStr.replace(/[\\/]+$/, '')
  const segments = cleaned.split(/[\\/]+/).filter(Boolean)
  if (segments.length > 6) {
    return ['…', ...segments.slice(-5)]
  }
  return segments
})

const fileDirectory = computed(() => {
  const p = activePayload.value?.path
  if (!p) return props.workspace
  if (activePayload.value?.kind === 'directory') return p
  const lastSep = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'))
  return lastSep > 0 ? p.slice(0, lastSep) : props.workspace
})

const highlightedLines = computed(() => {
  const payload = activePayload.value
  if (!payload || (payload.kind !== 'text' && payload.kind !== 'markdown' && payload.kind !== 'html')) {
    return []
  }
  return highlightCodeLines(payload.content, payload.language)
})

const highlightStart = computed(() => {
  const tabLine = activeTab.value?.line
  const payload = activePayload.value
  const payloadLine = payload && (payload.kind === 'text' || payload.kind === 'markdown' || payload.kind === 'html')
    ? payload.line
    : undefined
  return tabLine ?? payloadLine
})

const highlightEnd = computed(() => {
  const tabEnd = activeTab.value?.endLine
  const payload = activePayload.value
  const payloadEnd = payload && (payload.kind === 'text' || payload.kind === 'markdown' || payload.kind === 'html')
    ? payload.endLine
    : undefined
  return tabEnd ?? payloadEnd ?? highlightStart.value
})

const activePdfUrl = computed(() => {
  const tab = activeTab.value
  const payload = activePayload.value
  if (!tab || !payload || payload.kind !== 'pdf') return ''
  const cached = pdfObjectUrls.get(tab.id)
  if (cached) return cached
  try {
    const binary = atob(payload.dataBase64)
    const bytes = new Uint8Array(binary.length)
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i)
    }
    const blob = new Blob([bytes], { type: 'application/pdf' })
    const url = URL.createObjectURL(blob)
    pdfObjectUrls.set(tab.id, url)
    return url
  } catch {
    return ''
  }
})

function revokePdfUrl(tabId: string): void {
  const existing = pdfObjectUrls.get(tabId)
  if (existing) {
    URL.revokeObjectURL(existing)
    pdfObjectUrls.delete(tabId)
  }
}

function tabIconHtml(tab: FileViewerTab): string {
  const state = tabStates.value[tab.id]
  if (state?.payload && state.payload.ok && state.payload.kind === 'directory') {
    return renderLinkIconHtml({ kind: 'folder', variant: 'folder', label: '', color: '#d97706', ext: '' })
  }
  const targetHref = state?.payload && state.payload.ok ? state.payload.path : tab.href
  return renderLinkIconHtml(getLinkVisualMeta(targetHref, tab.label))
}

function entryIconHtml(entry: { name: string; path: string; isDirectory: boolean }): string {
  if (entry.isDirectory) {
    return renderLinkIconHtml({ kind: 'folder', variant: 'folder', label: '', color: '#d97706', ext: '' })
  }
  return renderLinkIconHtml(getLinkVisualMeta(entry.path, entry.name))
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

async function scrollToHighlightedLine(): Promise<void> {
  const line = highlightStart.value
  if (!line) return
  await nextTick()
  const container = contentRef.value
  if (!container) return
  const target = container.querySelector<HTMLElement>(`[data-line-number="${line}"]`)
  if (target) {
    target.scrollIntoView({ block: 'center', behavior: 'smooth' })
  }
}

async function loadTab(tab: FileViewerTab, force = false): Promise<void> {
  const existing = tabStates.value[tab.id]
  if (!force && existing && (existing.loading || existing.payload?.ok)) {
    if (tab.line && existing.payload?.ok && (existing.payload.kind === 'markdown' || existing.payload.kind === 'html')) {
      existing.mode = 'source'
    }
    if (tab.line) void scrollToHighlightedLine()
    return
  }

  const nextGen = (loadGeneration.get(tab.id) ?? 0) + 1
  loadGeneration.set(tab.id, nextGen)
  revokePdfUrl(tab.id)

  tabStates.value = {
    ...tabStates.value,
    [tab.id]: {
      loading: true,
      payload: existing?.payload ?? null,
      error: '',
      mode: existing?.mode ?? 'preview'
    }
  }

  try {
    const reader = window.seekclaw.readLinkPreview
    if (!reader) {
      throw new Error('当前环境不支持侧边栏文件预览')
    }
    const payload = await reader(tab.href, tab.workspace ?? props.workspace)
    if (loadGeneration.get(tab.id) !== nextGen) return

    if (!payload.ok) {
      tabStates.value = {
        ...tabStates.value,
        [tab.id]: {
          loading: false,
          payload,
          error: payload.error || '无法读取该文件',
          mode: 'source'
        }
      }
      return
    }

    const defaultMode: 'preview' | 'source' =
      (payload.kind === 'markdown' || payload.kind === 'html') && !tab.line && !payload.line
        ? 'preview'
        : 'source'

    tabStates.value = {
      ...tabStates.value,
      [tab.id]: {
        loading: false,
        payload,
        error: '',
        mode: defaultMode
      }
    }

    if (highlightStart.value) {
      void scrollToHighlightedLine()
    }
  } catch (err) {
    if (loadGeneration.get(tab.id) !== nextGen) return
    tabStates.value = {
      ...tabStates.value,
      [tab.id]: {
        loading: false,
        payload: null,
        error: err instanceof Error ? err.message : '读取文件失败',
        mode: 'source'
      }
    }
  }
}

function toggleViewMode(): void {
  const tab = activeTab.value
  const state = activeState.value
  if (!tab || !state) return
  state.mode = state.mode === 'preview' ? 'source' : 'preview'
  if (state.mode === 'source' && highlightStart.value) {
    void scrollToHighlightedLine()
  }
}

async function copyCurrentPath(): Promise<void> {
  const pathText = activePayload.value?.path || activeTab.value?.href
  if (!pathText) return
  try {
    await navigator.clipboard.writeText(pathText)
    copiedPath.value = true
    if (copiedTimer) clearTimeout(copiedTimer)
    copiedTimer = setTimeout(() => {
      copiedPath.value = false
    }, 1500)
  } catch {
    // ignore clipboard errors
  }
}

function revealInFolder(): void {
  const p = activePayload.value?.path
  if (p) void window.seekclaw.showItemInFolder(p)
}

function openExternal(): void {
  const target = activePayload.value?.path || activeTab.value?.href
  if (target) void window.seekclaw.openLink?.(target, activeTab.value?.workspace ?? props.workspace)
}

function isLineHighlighted(lineNo: number): boolean {
  const start = highlightStart.value
  if (!start) return false
  const end = highlightEnd.value ?? start
  return lineNo >= start && lineNo <= end
}

function saveWidth(width: number): void {
  panelWidth.value = width
  if (typeof localStorage !== 'undefined') {
    try {
      localStorage.setItem(FILE_VIEWER_WIDTH_STORAGE_KEY, String(width))
    } catch {
      // ignore
    }
  }
}

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  if (resizeRafId !== null) {
    cancelAnimationFrame(resizeRafId)
    resizeRafId = null
  }
  saveWidth(pendingWidth)
  emit('resize', pendingWidth)
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
  void nextTick(() => {
    if (panelRef.value) {
      panelRef.value.style.setProperty('--file-viewer-width', `${effectiveWidth.value}px`)
    }
  })
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  isMaximized.value = false
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(window.innerWidth * 0.85)))
  const nextWidth = Math.round(resizeStartWidth + resizeStartX - event.clientX)
  if (nextWidth <= MIN_PANEL_WIDTH - 60) {
    stopResize()
    emit('close')
    return
  }
  const clamped = Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth))
  pendingWidth = clamped
  if (panelRef.value) {
    panelRef.value.style.setProperty('--file-viewer-width', `${clamped}px`)
  }
  if (resizeRafId === null) {
    resizeRafId = requestAnimationFrame(() => {
      resizeRafId = null
      panelWidth.value = pendingWidth
    })
  }
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = panelRef.value?.getBoundingClientRect().width || panelWidth.value
  pendingWidth = resizeStartWidth
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

function resetWidth(): void {
  isMaximized.value = false
  pendingWidth = DEFAULT_PANEL_WIDTH
  saveWidth(DEFAULT_PANEL_WIDTH)
  emit('resize', DEFAULT_PANEL_WIDTH)
  void nextTick(() => {
    if (panelRef.value) {
      panelRef.value.style.setProperty('--file-viewer-width', `${effectiveWidth.value}px`)
    }
  })
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && props.tabs.length > 0) {
    emit('close')
  }
}

watch(
  () => activeTab.value,
  (tab) => {
    if (tab) void loadTab(tab)
  },
  { immediate: true }
)

watch(
  () => activeTab.value?.focusNonce,
  () => {
    const tab = activeTab.value
    if (!tab) return
    const state = tabStates.value[tab.id]
    if (state?.payload?.ok) {
      if (tab.line && (state.payload.kind === 'markdown' || state.payload.kind === 'html')) {
        state.mode = 'source'
      }
      if (tab.line) void scrollToHighlightedLine()
    }
  }
)

watch(
  () => props.tabs.map((t) => t.id),
  (ids) => {
    const idSet = new Set(ids)
    for (const key of Object.keys(tabStates.value)) {
      if (!idSet.has(key)) {
        revokePdfUrl(key)
        loadGeneration.delete(key)
        const next = { ...tabStates.value }
        delete next[key]
        tabStates.value = next
      }
    }
  }
)

onMounted(() => {
  window.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  stopResize()
  if (copiedTimer) clearTimeout(copiedTimer)
  for (const key of [...pdfObjectUrls.keys()]) {
    revokePdfUrl(key)
  }
  window.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <aside
    ref="panelRef"
    class="file-viewer-panel"
    :class="{ 'is-resizing': resizing }"
    :style="{ '--file-viewer-width': `${effectiveWidth}px` }"
    aria-label="文件侧边预览"
  >
    <div
      class="file-viewer-resize-handle"
      role="separator"
      aria-label="调整文件预览宽度"
      aria-orientation="vertical"
      @pointerdown="startResize"
      @dblclick="resetWidth"
    />

    <header class="file-viewer-tabs">
      <div class="file-viewer-tab-strip" role="tablist" aria-label="已打开的文件">
        <div
          v-for="tab in tabs"
          :key="tab.id"
          class="file-viewer-tab"
          :class="{ active: tab.id === activeTab?.id }"
          role="tab"
          :aria-selected="tab.id === activeTab?.id"
          :title="tab.href"
          @click="emit('selectTab', tab.id)"
        >
          <span class="file-viewer-tab-icon" v-html="tabIconHtml(tab)" />
          <span class="file-viewer-tab-label">{{ tab.label }}</span>
          <button
            type="button"
            class="file-viewer-tab-close"
            title="关闭标签页"
            aria-label="关闭标签页"
            @click.stop="emit('closeTab', tab.id)"
          >
            <X :size="13" />
          </button>
        </div>
      </div>

      <div class="file-viewer-window-actions">
        <button
          type="button"
          class="icon-button-mini"
          :title="isMaximized ? '还原宽度' : '加宽预览'"
          :aria-label="isMaximized ? '还原宽度' : '加宽预览'"
          @click="isMaximized = !isMaximized"
        >
          <Minimize2 v-if="isMaximized" :size="14" />
          <Maximize2 v-else :size="14" />
        </button>
        <button
          type="button"
          class="icon-button-mini"
          title="关闭侧边栏 (Esc)"
          aria-label="关闭侧边栏"
          @click="emit('close')"
        >
          <X :size="15" />
        </button>
      </div>
    </header>

    <div v-if="activeTab" class="file-viewer-toolbar">
      <div class="file-viewer-breadcrumbs" :title="activePayload?.path || activeTab.href">
        <template v-for="(seg, idx) in breadcrumbs" :key="`${idx}:${seg}`">
          <ChevronRight v-if="idx > 0" :size="12" class="breadcrumb-sep" aria-hidden="true" />
          <span v-if="idx === breadcrumbs.length - 1" class="breadcrumb-leaf">
            <span class="breadcrumb-leaf-icon" v-html="tabIconHtml(activeTab)" />
            <span>{{ seg }}</span>
          </span>
          <span v-else class="breadcrumb-seg">{{ seg }}</span>
        </template>
      </div>

      <div class="file-viewer-actions">
        <button
          v-if="activePayload && (activePayload.kind === 'markdown' || activePayload.kind === 'html')"
          type="button"
          class="file-viewer-mode-btn"
          :title="activeState?.mode === 'preview' ? '查看源码' : '查看渲染效果'"
          @click="toggleViewMode"
        >
          <Code2 v-if="activeState?.mode === 'preview'" :size="13" />
          <Eye v-else :size="13" />
          <span>{{ activeState?.mode === 'preview' ? '源码' : '预览' }}</span>
        </button>
        <button
          type="button"
          class="icon-button-mini"
          :title="copiedPath ? '已复制路径' : '复制路径'"
          aria-label="复制路径"
          @click="copyCurrentPath"
        >
          <Check v-if="copiedPath" :size="13" />
          <Copy v-else :size="13" />
        </button>
        <button
          type="button"
          class="icon-button-mini"
          :disabled="activeState?.loading"
          title="刷新文件内容"
          aria-label="刷新文件内容"
          @click="activeTab && loadTab(activeTab, true)"
        >
          <RefreshCw :size="13" :class="{ spin: activeState?.loading }" />
        </button>
        <button
          v-if="activePayload?.path"
          type="button"
          class="icon-button-mini"
          title="在文件夹中定位"
          aria-label="在文件夹中定位"
          @click="revealInFolder"
        >
          <FolderOpen :size="13" />
        </button>
        <button
          v-if="activePayload?.path"
          type="button"
          class="icon-button-mini"
          title="用系统默认程序打开"
          aria-label="用系统默认程序打开"
          @click="openExternal"
        >
          <ExternalLink :size="13" />
        </button>
      </div>
    </div>

    <div ref="contentRef" class="file-viewer-body">
      <div v-if="activeState?.loading" class="file-viewer-state" role="status">
        <LoaderCircle :size="22" class="spin" />
        <p>正在加载内容…</p>
      </div>

      <div v-else-if="activeState?.error" class="file-viewer-state" role="alert">
        <FileText :size="26" />
        <strong>无法打开该内容</strong>
        <p>{{ activeState.error }}</p>
        <button type="button" class="widget-button" @click="activeTab && loadTab(activeTab, true)">重新加载</button>
      </div>

      <template v-else-if="activePayload">
        <div
          v-if="activePayload.kind === 'markdown' && activeState?.mode === 'preview'"
          class="file-viewer-markdown"
        >
          <MarkdownMessage :content="activePayload.content" :workspace="fileDirectory" />
        </div>

        <iframe
          v-else-if="activePayload.kind === 'html' && activeState?.mode === 'preview'"
          class="file-viewer-iframe"
          :srcdoc="activePayload.content"
          :title="activePayload.name"
          sandbox="allow-scripts"
          referrerpolicy="no-referrer"
        />

        <div
          v-else-if="activePayload.kind === 'text' || activePayload.kind === 'markdown' || activePayload.kind === 'html'"
          class="file-viewer-code-wrap"
        >
          <div v-if="activePayload.truncated" class="file-viewer-banner">
            文件较大（{{ formatBytes(activePayload.sizeBytes) }}），当前仅预览前 512 KB 内容
          </div>
          <div class="file-viewer-code" role="region" :aria-label="activePayload.name">
            <div
              v-for="(lineHtml, idx) in highlightedLines"
              :key="idx"
              class="file-viewer-code-row"
              :class="{ 'is-highlighted': isLineHighlighted(idx + 1) }"
              :data-line-number="idx + 1"
            >
              <span class="file-viewer-line-no" aria-hidden="true">{{ idx + 1 }}</span>
              <code class="file-viewer-line-code hljs" v-html="lineHtml || '&nbsp;'" />
            </div>
          </div>
        </div>

        <div v-else-if="activePayload.kind === 'image'" class="file-viewer-image-wrap">
          <img
            :src="`data:${activePayload.mediaType};base64,${activePayload.dataBase64}`"
            :alt="activePayload.name"
          />
          <p class="file-viewer-caption">{{ activePayload.name }} · {{ formatBytes(activePayload.sizeBytes) }}</p>
        </div>

        <iframe
          v-else-if="activePayload.kind === 'pdf' && activePdfUrl"
          class="file-viewer-iframe"
          :src="activePdfUrl"
          :title="activePayload.name"
        />

        <div v-else-if="activePayload.kind === 'directory'" class="file-viewer-directory">
          <div v-if="activePayload.entries.length === 0" class="file-viewer-state">
            <Folder :size="26" />
            <p>该文件夹为空</p>
          </div>
          <button
            v-for="entry in activePayload.entries"
            :key="entry.path"
            type="button"
            class="file-viewer-dir-entry"
            @click="emit('openPath', entry.path, workspace)"
          >
            <span class="file-viewer-dir-icon" v-html="entryIconHtml(entry)" />
            <span class="file-viewer-dir-name">{{ entry.name }}</span>
            <span class="file-viewer-dir-meta">
              {{ entry.isDirectory ? '文件夹' : formatBytes(entry.sizeBytes) }}
            </span>
          </button>
        </div>

        <div v-else-if="activePayload.kind === 'binary'" class="file-viewer-state">
          <FileText :size="28" />
          <strong>{{ activePayload.name }}</strong>
          <p>
            {{ activePayload.ext ? `${activePayload.ext.toUpperCase()} 文件` : '二进制文件' }}
            · {{ formatBytes(activePayload.sizeBytes) }}，暂不支持直接文本渲染
          </p>
          <div class="file-viewer-state-actions">
            <button type="button" class="widget-button" @click="revealInFolder">在文件夹中显示</button>
            <button type="button" class="widget-button widget-primary" @click="openExternal">用系统程序打开</button>
          </div>
        </div>
      </template>
    </div>
  </aside>
</template>

<style scoped>
.file-viewer-panel {
  position: relative;
  z-index: 6;
  flex: 0 0 auto;
  display: flex;
  flex-direction: column;
  width: min(var(--file-viewer-width, 560px), 100%);
  height: 100%;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  color: var(--text);
  background: var(--surface);
  border-left: 1px solid var(--border);
}

.file-viewer-panel.is-resizing,
.file-viewer-panel.is-resizing * {
  user-select: none;
  transition: none !important;
}

.file-viewer-panel.is-resizing iframe {
  pointer-events: none;
}

.file-viewer-resize-handle {
  position: absolute;
  top: 0;
  bottom: 0;
  left: -3px;
  z-index: 3;
  width: 6px;
  cursor: col-resize;
}

.file-viewer-resize-handle::after {
  content: '';
  position: absolute;
  top: 0;
  bottom: 0;
  left: 2px;
  width: 2px;
  background: transparent;
  transition: background-color 150ms ease;
}

.file-viewer-resize-handle:hover::after,
.file-viewer-panel.is-resizing .file-viewer-resize-handle::after {
  background: var(--accent);
}

.file-viewer-tabs {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 40px;
  padding: 6px 10px 0 12px;
  background: var(--bg);
  border-bottom: 1px solid var(--border);
}

.file-viewer-tab-strip {
  display: flex;
  align-items: center;
  gap: 4px;
  flex: 1 1 auto;
  min-width: 0;
  overflow-x: auto;
  scrollbar-width: none;
}

.file-viewer-tab-strip::-webkit-scrollbar {
  display: none;
}

.file-viewer-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 200px;
  height: 33px;
  padding: 0 8px 0 10px;
  border: 1px solid transparent;
  border-bottom: none;
  border-radius: 8px 8px 0 0;
  color: var(--text-muted);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
  flex: 0 0 auto;
  transition: background-color 120ms ease, color 120ms ease;
}

.file-viewer-tab:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface) 60%, transparent);
}

.file-viewer-tab.active {
  color: var(--text);
  background: var(--surface);
  border-color: var(--border);
  position: relative;
}

.file-viewer-tab.active::after {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  bottom: -1px;
  height: 1px;
  background: var(--surface);
}

.file-viewer-tab-icon,
.breadcrumb-leaf-icon,
.file-viewer-dir-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
}

.file-viewer-tab-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-viewer-tab-close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  padding: 0;
  line-height: 1;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  opacity: 0.7;
  flex: none;
}

.file-viewer-tab-close svg {
  display: block;
  flex: none;
}

.file-viewer-tab-close:hover {
  opacity: 1;
  color: var(--text);
  background: var(--surface-hover);
}

.file-viewer-window-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  padding-bottom: 5px;
  flex: none;
}

.file-viewer-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 36px;
  padding: 5px 12px;
  background: var(--surface);
  border-bottom: 1px solid var(--border);
}

.file-viewer-breadcrumbs {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  flex: 1 1 auto;
  overflow: hidden;
  font-size: 12px;
  color: var(--text-muted);
}

.breadcrumb-seg {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 120px;
}

.breadcrumb-sep {
  flex: none;
  opacity: 0.6;
}

.breadcrumb-leaf {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  color: var(--text);
  font-weight: 500;
}

.breadcrumb-leaf > span:last-child {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-viewer-actions {
  display: flex;
  align-items: center;
  gap: 3px;
  flex: none;
}

.file-viewer-mode-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--bg);
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
}

.file-viewer-mode-btn:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.icon-button-mini {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  line-height: 1;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.icon-button-mini svg {
  display: block;
  flex: none;
}

.icon-button-mini:hover:not(:disabled) {
  color: var(--text);
  background: var(--surface-hover);
}

.icon-button-mini:disabled {
  opacity: 0.5;
  cursor: default;
}

.file-viewer-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow: auto;
  background: var(--surface);
  display: flex;
  flex-direction: column;
}

.file-viewer-state {
  flex: 1 1 auto;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 28px;
  text-align: center;
  color: var(--text-muted);
}

.file-viewer-state strong {
  color: var(--text);
  font-size: 14px;
}

.file-viewer-state p {
  margin: 0;
  max-width: 380px;
  font-size: 12.5px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.file-viewer-state-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
}

.file-viewer-banner {
  padding: 6px 14px;
  background: color-mix(in srgb, var(--accent) 8%, var(--surface));
  border-bottom: 1px solid var(--border);
  color: var(--text-secondary);
  font-size: 12px;
}

.file-viewer-code-wrap {
  min-width: 0;
  flex: 1 1 auto;
  display: flex;
  flex-direction: column;
}

.file-viewer-code {
  padding: 10px 0 24px;
  font-family: var(--font-mono, "Cascadia Code", "SFMono-Regular", Consolas, monospace);
  font-size: 12.5px;
  line-height: 1.62;
  min-width: max-content;
}

.file-viewer-code-row {
  display: flex;
  align-items: flex-start;
  padding: 0 16px 0 0;
}

.file-viewer-code-row:hover {
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.file-viewer-code-row.is-highlighted {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.file-viewer-line-no {
  flex: 0 0 50px;
  padding-right: 14px;
  text-align: right;
  color: var(--text-muted);
  user-select: none;
  font-size: 11.5px;
  opacity: 0.72;
}

.file-viewer-line-code {
  flex: 1 1 auto;
  white-space: pre;
  color: var(--text);
  background: transparent !important;
  padding: 0 !important;
  border: none !important;
}

.file-viewer-markdown {
  padding: 20px 24px 36px;
  min-width: 0;
}

.file-viewer-iframe {
  flex: 1 1 auto;
  width: 100%;
  height: 100%;
  min-height: 300px;
  border: none;
  background: #ffffff;
}

.file-viewer-image-wrap {
  flex: 1 1 auto;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  padding: 24px;
}

.file-viewer-image-wrap img {
  max-width: 100%;
  max-height: calc(100% - 36px);
  object-fit: contain;
  border-radius: 8px;
  border: 1px solid var(--border);
  box-shadow: 0 4px 16px rgb(0 0 0 / 8%);
}

.file-viewer-caption {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
}

.file-viewer-directory {
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.file-viewer-dir-entry {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 7px 10px;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}

.file-viewer-dir-entry:hover {
  background: var(--surface-hover);
}

.file-viewer-dir-name {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-viewer-dir-meta {
  flex: none;
  font-size: 11.5px;
  color: var(--text-muted);
  font-family: var(--font-mono);
}
</style>
