<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'

const props = withDefaults(
  defineProps<{
    storageKey?: string
    defaultWidth?: number
    minWidth?: number
    maxWidth?: number
    canCollapse?: boolean
    collapseThreshold?: number
    ariaLabel?: string
  }>(),
  {
    storageKey: '',
    defaultWidth: 260,
    minWidth: 200,
    maxWidth: 500,
    canCollapse: false,
    collapseThreshold: 160,
    ariaLabel: '侧边栏导航'
  }
)

const emit = defineEmits<{
  resize: [width: number]
  collapse: []
}>()

function readStoredWidth(): number | null {
  if (!props.storageKey) return null
  try {
    const raw = localStorage.getItem(props.storageKey)
    if (!raw) return null
    const val = Number.parseInt(raw, 10)
    if (Number.isFinite(val) && val >= props.minWidth && val <= 800) {
      return val
    }
  } catch {
    // ignore
  }
  return null
}

function persistWidth(w: number): void {
  if (!props.storageKey) return
  try {
    localStorage.setItem(props.storageKey, String(w))
  } catch {
    // ignore
  }
}

const currentWidth = ref<number>(readStoredWidth() ?? props.defaultWidth)
const resizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-sidebar')
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const delta = event.clientX - resizeStartX
  const nextWidth = resizeStartWidth + delta

  if (props.canCollapse && nextWidth < props.collapseThreshold) {
    stopResize()
    emit('collapse')
    return
  }

  const effectiveMax = Math.min(
    props.maxWidth,
    Math.max(props.minWidth, Math.floor(window.innerWidth * 0.55))
  )
  const clamped = Math.min(effectiveMax, Math.max(props.minWidth, Math.round(nextWidth)))
  currentWidth.value = clamped
  persistWidth(clamped)
  emit('resize', clamped)
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = currentWidth.value
  document.body.classList.add('is-resizing-sidebar')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

function resetWidth(): void {
  currentWidth.value = props.defaultWidth
  persistWidth(props.defaultWidth)
  emit('resize', props.defaultWidth)
}

onBeforeUnmount(() => {
  stopResize()
})
</script>

<template>
  <div class="two-pane-layout">
    <aside
      class="two-pane-sidebar"
      :style="{ width: `${currentWidth}px` }"
      :aria-label="ariaLabel"
    >
      <div class="two-pane-sidebar-content">
        <slot name="sidebar" />
      </div>

      <div
        class="sidebar-resize-handle"
        role="separator"
        aria-orientation="vertical"
        :title="canCollapse ? '拖拽调节宽度，拖至最小时隐藏，双击重置' : '拖拽调节宽度，双击重置'"
        @pointerdown="startResize"
        @dblclick="resetWidth"
      />
    </aside>

    <main class="two-pane-content">
      <slot />
    </main>
  </div>
</template>

<style scoped>
.two-pane-layout {
  display: flex;
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}

.two-pane-sidebar {
  position: relative;
  flex: 0 0 auto;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: var(--sidebar-panel);
  border-top: 1px solid var(--border);
  border-left: 1px solid var(--border);
  border-top-left-radius: 12px;
  border-right: 1px solid var(--border);
  box-sizing: border-box;
  overflow: hidden;
  user-select: none;
}

.two-pane-sidebar-content {
  flex: 1 1 auto;
  min-height: 0;
  min-width: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.sidebar-resize-handle {
  position: absolute;
  z-index: 25;
  top: 0;
  bottom: 0;
  right: 0;
  width: 7px;
  cursor: col-resize;
  user-select: none;
}

.sidebar-resize-handle::after {
  content: "";
  position: absolute;
  top: 0;
  bottom: 0;
  right: 0;
  width: 2px;
  background: var(--accent);
  opacity: 0;
  transition: opacity 140ms ease;
}

.sidebar-resize-handle:hover::after {
  opacity: 1;
}

:global(body.is-resizing-sidebar) .sidebar-resize-handle::after {
  opacity: 1;
}

.two-pane-content {
  position: relative;
  flex: 1 1 auto;
  min-width: 0;
  min-height: 0;
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--surface);
  border-top: 1px solid var(--border);
  border-top-left-radius: 0;
  border-left: none;
  box-sizing: border-box;
  overflow: hidden;
}
</style>
