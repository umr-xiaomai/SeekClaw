<script setup lang="ts">
import { Code2, LoaderCircle, Monitor, RefreshCw, Smartphone, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ArtifactWidget } from '../types'
import { previewDocument } from '../conversation-widgets'
import '../conversation-widgets.css'

const ARTIFACT_PANEL_WIDTH_STORAGE_KEY = 'seekclaw-artifact-panel-width-v1'
const DEFAULT_PANEL_WIDTH = 560
const MIN_PANEL_WIDTH = 320
const MAX_PANEL_WIDTH = 1400

function loadStoredPanelWidth(): number {
  if (typeof localStorage === 'undefined') return DEFAULT_PANEL_WIDTH
  try {
    const raw = localStorage.getItem(ARTIFACT_PANEL_WIDTH_STORAGE_KEY)
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

const props = withDefaults(defineProps<{
  open?: boolean
  artifact: ArtifactWidget
  sessionId: string
  workspace?: string
}>(), {
  open: true
})
const emit = defineEmits<{ close: [] }>()
const filename = computed(() => props.artifact.path.split(/[\\/]/).pop() || props.artifact.path)

const panelRef = ref<HTMLElement | null>(null)
const html = ref<string | null>(null)
const error = ref('')
const loading = ref(false)
const mobile = ref(false)
const frameVersion = ref(0)
const panelWidth = ref(loadStoredPanelWidth())
const resizing = ref(false)
const previewHtml = computed(() => html.value === null ? undefined : previewDocument(html.value))

let generation = 0
let disposed = false
let resizeStartX = 0
let resizeStartWidth = 0
let resizeRafId: number | null = null
let pendingWidth = panelWidth.value

async function load(): Promise<void> {
  const version = ++generation
  loading.value = true
  error.value = ''
  try {
    const response = await window.seekclaw.daemon.request('artifact.read', {
      widgetId: props.artifact.id,
      sessionId: props.sessionId,
      workspace: props.workspace,
      global: !props.workspace
    })
    const result = JSON.parse(response.data) as { html: string }
    if (typeof result.html !== 'string') throw new Error('预览内容无效')
    if (!disposed && version === generation) {
      html.value = result.html
      frameVersion.value++
    }
  } catch (reason) {
    if (!disposed && version === generation) {
      error.value = reason instanceof Error ? reason.message : String(reason)
    }
  } finally {
    if (!disposed && version === generation) {
      loading.value = false
    }
  }
}

function savePanelWidth(width: number): void {
  panelWidth.value = width
  if (typeof localStorage !== 'undefined') {
    try {
      localStorage.setItem(ARTIFACT_PANEL_WIDTH_STORAGE_KEY, String(width))
    } catch { /* ignore */ }
  }
}

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  if (resizeRafId !== null) {
    cancelAnimationFrame(resizeRafId)
    resizeRafId = null
  }
  savePanelWidth(pendingWidth)
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(window.innerWidth * 0.85)))
  const nextWidth = Math.round(resizeStartWidth + resizeStartX - event.clientX)
  if (nextWidth <= MIN_PANEL_WIDTH - 40) {
    stopResize()
    emit('close')
    return
  }
  const clampedWidth = Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth))
  pendingWidth = clampedWidth
  if (panelRef.value) {
    panelRef.value.style.setProperty('--artifact-panel-width', `${clampedWidth}px`)
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

function resetPanelWidth(): void {
  pendingWidth = DEFAULT_PANEL_WIDTH
  savePanelWidth(DEFAULT_PANEL_WIDTH)
  if (panelRef.value) {
    panelRef.value.style.setProperty('--artifact-panel-width', `${DEFAULT_PANEL_WIDTH}px`)
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && props.open) {
    emit('close')
  }
}

watch([() => props.open, () => props.artifact.id, () => props.sessionId, () => props.workspace], ([open]) => {
  if (open) void load()
  else {
    generation++
    stopResize()
  }
})

onMounted(() => {
  window.addEventListener('keydown', handleKeydown)
  if (props.open) void load()
})

onBeforeUnmount(() => {
  disposed = true
  generation++
  stopResize()
  window.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <Transition name="side-panel">
    <aside
      v-if="open"
      ref="panelRef"
      class="artifact-preview-panel"
      :class="{ 'is-resizing': resizing }"
      :style="{ '--artifact-panel-width': `${panelWidth}px` }"
      :aria-label="`预览 ${artifact.title}`"
    >
      <div
        class="artifact-panel-resize-handle"
        role="separator"
        aria-label="调整预览面板宽度"
        aria-orientation="vertical"
        @pointerdown="startResize"
        @dblclick="resetPanelWidth"
      />
      <header class="artifact-preview-toolbar">
        <div class="artifact-preview-title">
          <span class="artifact-title-badge">
            <Code2 :size="15" />
            <strong :title="artifact.path">{{ filename }}</strong>
          </span>
          <small v-if="artifact.title !== filename" :title="artifact.title">{{ artifact.title }}</small>
        </div>
        <div class="artifact-toolbar-actions">
          <div class="artifact-viewport-control" role="group" aria-label="预览宽度">
            <button type="button" :aria-pressed="!mobile" title="桌面宽度" aria-label="桌面宽度" @click="mobile = false">
              <Monitor :size="14" />
            </button>
            <button type="button" :aria-pressed="mobile" title="手机宽度 (390px)" aria-label="手机宽度" @click="mobile = true">
              <Smartphone :size="14" />
            </button>
          </div>
          <button type="button" class="icon-button-mini" :disabled="loading" title="重新读取文件" aria-label="重新读取文件" @click="load">
            <RefreshCw :size="14" :class="{ spin: loading }" />
          </button>
          <button type="button" class="icon-button-mini" title="关闭预览 (Esc)" aria-label="关闭预览" @click="emit('close')">
            <X :size="15" />
          </button>
        </div>
      </header>
      <div class="artifact-preview-canvas" :class="{ mobile }" :aria-busy="loading">
        <div v-if="loading" class="artifact-preview-state" role="status">
          <LoaderCircle :size="22" class="spin" />
          <p>正在加载预览…</p>
        </div>
        <div v-else-if="error" class="artifact-preview-state" role="alert">
          <Code2 :size="26" />
          <strong>暂时无法预览</strong>
          <p>{{ error }}</p>
          <button type="button" class="widget-button" @click="load">重新加载</button>
        </div>
        <iframe
          v-else-if="previewHtml"
          :key="frameVersion"
          :srcdoc="previewHtml"
          :title="artifact.title"
          sandbox="allow-scripts"
          referrerpolicy="no-referrer"
        />
      </div>
    </aside>
  </Transition>
</template>
