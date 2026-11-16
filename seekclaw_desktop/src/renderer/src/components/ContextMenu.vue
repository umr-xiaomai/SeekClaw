<script setup lang="ts">
import { ChevronRight } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch, type ComponentPublicInstance } from 'vue'
import {
  contextMenuFocusIndex, isContextMenuAction, positionContextMenu, positionContextSubmenu,
  type ContextMenuAction, type ContextMenuAnchor, type ContextMenuEntry
} from './context-menu'

const props = withDefaults(defineProps<{
  items: ContextMenuEntry[]
  anchor: ContextMenuAnchor
  open?: boolean
  label?: string
  width?: number
  returnFocus?: HTMLElement | null
}>(), { open: true, label: '操作菜单', width: 224, returnFocus: null })

const emit = defineEmits<{ select: [id: string]; close: [] }>()
const menuId = `context-menu-${useId()}`
const visible = ref(props.open)
const openPath = ref<number[]>([])
const activeIndices = ref<number[]>([contextMenuFocusIndex(props.items, -1, 'first')])
const positions = ref<Array<Record<string, string>>>([])
const panelElements = new Map<number, HTMLElement>()
const rowElements = new Map<string, HTMLElement>()
let restoreTarget: HTMLElement | null = null
let mounted = false

const panels = computed(() => {
  const result: Array<{ depth: number; items: ContextMenuEntry[]; label: string }> = [
    { depth: 0, items: props.items, label: props.label }
  ]
  for (const index of openPath.value) {
    const entry = result.at(-1)?.items[index]
    if (!entry || !isContextMenuAction(entry) || entry.disabled || !entry.children?.length) break
    result.push({ depth: result.length, items: entry.children, label: entry.label })
  }
  return result
})

function setElement(map: Map<number | string, HTMLElement>, key: number | string,
  element: Element | ComponentPublicInstance | null): void {
  if (element instanceof HTMLElement) map.set(key, element)
  else map.delete(key)
}

function positionPanels(): void {
  if (!visible.value) return
  const viewport = { width: window.innerWidth, height: window.innerHeight }
  const next: Array<Record<string, string>> = []
  for (const panel of panels.value) {
    const element = panelElements.get(panel.depth)
    if (!element) { next.push({ visibility: 'hidden', width: `${props.width}px` }); continue }
    const size = element.getBoundingClientRect()
    const parent = panel.depth > 0
      ? rowElements.get(`${panel.depth - 1}:${openPath.value[panel.depth - 1]}`)?.getBoundingClientRect()
      : undefined
    const oldParent = panelElements.get(panel.depth - 1)?.getBoundingClientRect()
    const parentLeft = Number.parseFloat(next[panel.depth - 1]?.left ?? '')
    const parentTop = Number.parseFloat(next[panel.depth - 1]?.top ?? '')
    const dx = oldParent && Number.isFinite(parentLeft) ? parentLeft - oldParent.left : 0
    const dy = oldParent && Number.isFinite(parentTop) ? parentTop - oldParent.top : 0
    const position = parent
      ? positionContextSubmenu({ left: parent.left + dx, right: parent.right + dx,
        top: parent.top + dy, bottom: parent.bottom + dy }, size, viewport)
      : positionContextMenu(props.anchor, size, viewport)
    next.push({ left: `${position.left}px`, top: `${position.top}px`, width: `${position.width}px`,
      maxHeight: `${position.maxHeight}px`, visibility: 'visible' })
  }
  positions.value = next
}

function focusRow(depth: number, index: number): void {
  void nextTick(() => {
    if (!visible.value) return
    const panel = panelElements.get(depth)
    const row = rowElements.get(`${depth}:${index}`)
    if (!row) { panel?.focus({ preventScroll: true }); return }
    row.focus({ preventScroll: true })
    if (!panel) return
    // scrollIntoView can move the conversation behind the menu. Move only this panel.
    const bounds = panel.getBoundingClientRect()
    const rect = row.getBoundingClientRect()
    if (rect.top < bounds.top + 4) panel.scrollTop -= bounds.top + 4 - rect.top
    else if (rect.bottom > bounds.bottom - 4) panel.scrollTop += rect.bottom - bounds.bottom + 4
    if (openPath.value.length > depth) positionPanels()
  })
}

function activate(depth: number, index: number, focus = true): void {
  const entry = panels.value[depth]?.items[index]
  if (!entry || !isContextMenuAction(entry) || entry.disabled) return
  activeIndices.value = [...activeIndices.value.slice(0, depth), index]
  if (openPath.value[depth] !== index) openPath.value = openPath.value.slice(0, depth)
  if (focus) focusRow(depth, index)
}

function openSubmenu(depth: number, index: number, enter: boolean): void {
  const entry = panels.value[depth]?.items[index]
  if (!entry || !isContextMenuAction(entry) || entry.disabled || !entry.children?.length) return
  activate(depth, index, !enter)
  openPath.value = [...openPath.value.slice(0, depth), index]
  activeIndices.value[depth + 1] = contextMenuFocusIndex(entry.children, -1, 'first')
  void nextTick(() => {
    positionPanels()
    if (enter) focusRow(depth + 1, activeIndices.value[depth + 1] ?? -1)
  })
}

function closeSubmenu(depth: number): void {
  const parent = openPath.value[depth - 1] ?? -1
  openPath.value = openPath.value.slice(0, depth - 1)
  activeIndices.value = activeIndices.value.slice(0, depth)
  focusRow(depth - 1, parent)
}

function close(restore = false, selectedId?: string): void {
  if (!visible.value) return
  visible.value = false
  removeListeners()
  // Restore before emitting: an action may open a dialog and take focus itself.
  if (restore && restoreTarget?.isConnected) restoreTarget.focus({ preventScroll: true })
  // The caller can still read its owning project/task before close clears that context.
  if (selectedId !== undefined) emit('select', selectedId)
  emit('close')
}

function select(depth: number, index: number): void {
  const entry = panels.value[depth]?.items[index]
  if (!entry || !isContextMenuAction(entry) || entry.disabled) return
  if (entry.children?.length) { openSubmenu(depth, index, true); return }
  close(true, entry.id)
}

function hover(depth: number, index: number, entry: ContextMenuAction): void {
  if (entry.disabled) return
  activate(depth, index)
  if (entry.children?.length) openSubmenu(depth, index, false)
}

function handleKeydown(event: KeyboardEvent, depth: number): void {
  if (event.isComposing) return
  const current = activeIndices.value[depth] ?? -1
  const items = panels.value[depth]?.items ?? []
  const direction = { ArrowDown: 'next', ArrowUp: 'previous', Home: 'first', End: 'last' }[event.key]
  if (direction) {
    event.preventDefault()
    event.stopPropagation()
    const index = contextMenuFocusIndex(items, current, direction as 'next' | 'previous' | 'first' | 'last')
    if (index >= 0) activate(depth, index)
    return
  }
  if (event.key === 'Tab') { close(true); return }
  if (!['Enter', ' ', 'ArrowRight', 'ArrowLeft', 'Escape'].includes(event.key)) return
  event.preventDefault()
  event.stopPropagation()
  if (event.key === 'ArrowRight') openSubmenu(depth, current, true)
  else if (event.key === 'ArrowLeft') { if (depth > 0) closeSubmenu(depth) }
  else if (event.key === 'Escape') { if (depth > 0) closeSubmenu(depth); else close(true) }
  else select(depth, current)
}

function isInside(target: EventTarget | null): boolean {
  return target instanceof Node && [...panelElements.values()].some((panel) => panel.contains(target))
}

function handleOutsidePointer(event: PointerEvent): void {
  if (isInside(event.target)) return
  // A button anchor owns its toggle click. A point menu closes on any outside click.
  if ('rect' in props.anchor && event.target instanceof Node && restoreTarget?.contains(event.target)) return
  close()
}

function handleScroll(event: Event): void {
  if (isInside(event.target)) { positionPanels(); return }
  close()
}

function handleFocus(event: FocusEvent): void {
  if (!isInside(event.target)) close()
}

function handleWindowBlur(): void { close() }

function addListeners(): void {
  document.addEventListener('pointerdown', handleOutsidePointer, true)
  document.addEventListener('scroll', handleScroll, true)
  document.addEventListener('focusin', handleFocus)
  window.addEventListener('resize', positionPanels)
  window.addEventListener('blur', handleWindowBlur)
}

function removeListeners(): void {
  document.removeEventListener('pointerdown', handleOutsidePointer, true)
  document.removeEventListener('scroll', handleScroll, true)
  document.removeEventListener('focusin', handleFocus)
  window.removeEventListener('resize', positionPanels)
  window.removeEventListener('blur', handleWindowBlur)
}

function show(): void {
  removeListeners()
  if (props.returnFocus) restoreTarget = props.returnFocus
  else if (!isInside(document.activeElement)) {
    restoreTarget = document.activeElement instanceof HTMLElement ? document.activeElement : null
  }
  openPath.value = []
  activeIndices.value = [contextMenuFocusIndex(props.items, -1, 'first')]
  positions.value = []
  visible.value = true
  addListeners()
  void nextTick(() => {
    if (!visible.value) return
    positionPanels()
    focusRow(0, activeIndices.value[0] ?? -1)
  })
}

watch(() => [props.open, props.anchor, props.items] as const, () => {
  if (!mounted) return
  if (props.open) show()
  else { visible.value = false; removeListeners() }
})
onMounted(() => { mounted = true; if (props.open) show() })
onBeforeUnmount(() => { mounted = false; removeListeners() })
</script>

<template>
  <Teleport to="body">
    <template v-if="visible">
      <div v-for="panel in panels" :id="`${menuId}-${panel.depth}`" :key="panel.depth"
        :ref="(el) => setElement(panelElements, panel.depth, el)" class="context-menu" role="menu"
        :aria-label="panel.label" :style="positions[panel.depth] ?? { width: `${width}px`, visibility: 'hidden' }"
        tabindex="-1" @keydown="handleKeydown($event, panel.depth)" @contextmenu.prevent>
        <template v-for="(entry, index) in panel.items" :key="entry.id ?? `separator-${index}`">
          <div v-if="!isContextMenuAction(entry)" class="context-menu-separator" role="separator" />
          <button v-else :id="`${menuId}-${panel.depth}-${index}`" type="button" class="context-menu-item"
            :ref="(el) => setElement(rowElements, `${panel.depth}:${index}`, el)"
            :class="{ active: activeIndices[panel.depth] === index, danger: entry.danger }"
            role="menuitem" :disabled="entry.disabled" :aria-disabled="entry.disabled || undefined"
            :tabindex="activeIndices[panel.depth] === index ? 0 : -1"
            :aria-haspopup="entry.children?.length ? 'menu' : undefined"
            :aria-expanded="entry.children?.length ? openPath[panel.depth] === index : undefined"
            :aria-controls="entry.children?.length && openPath[panel.depth] === index ? `${menuId}-${panel.depth + 1}` : undefined"
            @pointerenter="hover(panel.depth, index, entry)" @focus="activate(panel.depth, index, false)"
            @click="select(panel.depth, index)">
            <span class="context-menu-icon" aria-hidden="true"><component :is="entry.icon" v-if="entry.icon" :size="16" :stroke-width="1.7" /></span>
            <span class="context-menu-label">{{ entry.label }}</span>
            <span v-if="entry.shortcut" class="context-menu-shortcut" aria-hidden="true">{{ entry.shortcut }}</span>
            <ChevronRight v-if="entry.children?.length" :size="14" class="context-menu-chevron" aria-hidden="true" />
          </button>
        </template>
      </div>
    </template>
  </Teleport>
</template>

<style scoped>
.context-menu {
  position: fixed;
  z-index: 1100;
  box-sizing: border-box;
  max-width: calc(100vw - 16px);
  max-height: calc(100vh - 16px);
  overflow: auto;
  padding: 4px;
  color: var(--text);
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--popover-shadow);
  scrollbar-width: thin;
  overscroll-behavior: contain;
  outline: none;
}
.context-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 32px;
  padding: 6px 8px;
  border: 0;
  border-radius: 8px;
  color: inherit;
  background: transparent;
  font-size: 13px;
  line-height: 20px;
  text-align: left;
  cursor: pointer;
  transition: background-color 100ms ease, color 100ms ease;
  outline: none;
}
.context-menu-item.active:not(:disabled) { background: var(--surface-hover); }
.context-menu-item:focus-visible { box-shadow: inset 0 0 0 1px var(--border-strong); }
.context-menu-item.danger { color: var(--danger); }
.context-menu-item:disabled { color: var(--text-muted); opacity: .5; cursor: default; }
.context-menu-icon { display: inline-flex; align-items: center; justify-content: center; width: 16px; height: 20px; flex: none; }
.context-menu-label { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.context-menu-shortcut { margin-left: 12px; color: var(--text-muted); font-size: 12px; white-space: nowrap; }
.context-menu-chevron { flex: none; color: var(--text-muted); }
.context-menu-separator { height: 1px; margin: 4px 8px; background: var(--border); }
@media (prefers-reduced-motion: reduce) { .context-menu-item { transition: none; } }
</style>
