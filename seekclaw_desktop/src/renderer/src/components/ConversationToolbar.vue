<script setup lang="ts">
import { Braces, Check, FolderOpen, Globe2, History, MoreHorizontal, PanelLeft, RefreshCw, Search, Settings2, TerminalSquare, X } from '@lucide/vue'
import { nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'

const props = defineProps<{
  modelValue: string
  contextKey: string
  hasProject: boolean
  hasWorkspace: boolean
  hasThread: boolean
  connected: boolean
  reconnecting: boolean
  connectionTitle: string
  connectionLabel: string
  changesOpen: boolean
  historyOpen: boolean
  browserOpen: boolean
  sidebarOpen: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  showProject: []
  openTerminal: []
  openChanges: []
  openHistory: []
  openBrowser: []
  openTaskSettings: []
  toggleSidebar: []
  reconnect: []
}>()

type Popup = 'search' | 'more'
const openPopup = ref<Popup | null>(null)
const toolbar = ref<HTMLElement | null>(null)
const searchTrigger = ref<HTMLButtonElement | null>(null)
const moreTrigger = ref<HTMLButtonElement | null>(null)
const popup = ref<HTMLElement | null>(null)
const searchInput = ref<HTMLInputElement | null>(null)
const popupStyle = ref<Record<string, string>>({})
const id = useId()
let resizeObserver: ResizeObserver | undefined

function triggerFor(kind: Popup): HTMLButtonElement | null {
  return kind === 'search' ? searchTrigger.value : moreTrigger.value
}

function positionPopup(): void {
  if (!openPopup.value || !popup.value) return
  const trigger = triggerFor(openPopup.value)
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  const edge = 8
  const gap = 6
  const width = Math.min(openPopup.value === 'search' ? 300 : 232, window.innerWidth - edge * 2)
  const height = Math.min(popup.value.scrollHeight, window.innerHeight - edge * 2)
  const below = window.innerHeight - rect.bottom - gap - edge
  const above = rect.top - gap - edge
  const placeAbove = below < height && above > below
  const top = placeAbove ? rect.top - gap - height : rect.bottom + gap
  popupStyle.value = {
    width: `${width}px`,
    left: `${Math.max(edge, Math.min(rect.right - width, window.innerWidth - width - edge))}px`,
    top: `${Math.max(edge, Math.min(top, window.innerHeight - height - edge))}px`,
    maxHeight: `${window.innerHeight - edge * 2}px`
  }
}

function menuItems(): HTMLButtonElement[] {
  return [...(popup.value?.querySelectorAll<HTMLButtonElement>('[role^="menuitem"]:not(:disabled)') ?? [])]
}

function hidePopup(restoreFocus = false): void {
  const previous = openPopup.value
  openPopup.value = null
  if (restoreFocus && previous) triggerFor(previous)?.focus({ preventScroll: true })
}

async function showPopup(kind: Popup, focusLast = false): Promise<void> {
  openPopup.value = kind
  await nextTick()
  if (openPopup.value !== kind) return
  positionPopup()
  if (kind === 'search') searchInput.value?.focus({ preventScroll: true })
  else {
    const items = menuItems()
    items[focusLast ? items.length - 1 : 0]?.focus({ preventScroll: true })
  }
}

function togglePopup(kind: Popup): void {
  if (openPopup.value === kind) hidePopup()
  else void showPopup(kind)
}

function handleTriggerKey(event: KeyboardEvent, kind: Popup): void {
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
  event.preventDefault()
  void showPopup(kind, event.key === 'ArrowUp')
}

function handleMenuKey(event: KeyboardEvent): void {
  const items = menuItems()
  const index = items.indexOf(document.activeElement as HTMLButtonElement)
  let next: number
  switch (event.key) {
    case 'ArrowDown': next = (index + 1) % items.length; break
    case 'ArrowUp': next = (index - 1 + items.length) % items.length; break
    case 'Home': next = 0; break
    case 'End': next = items.length - 1; break
    case 'Tab': hidePopup(true); return
    default: return
  }
  event.preventDefault()
  items[next]?.focus({ preventScroll: true })
}

function handleOutsidePointer(event: PointerEvent): void {
  const target = event.target as Node | null
  if (!target || popup.value?.contains(target)) return
  if (searchTrigger.value?.contains(target) || moreTrigger.value?.contains(target)) return
  hidePopup()
}

function handleEscape(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || event.isComposing || event.keyCode === 229) return
  event.preventDefault()
  event.stopPropagation()
  hidePopup(true)
}

function handleSearchKey(event: KeyboardEvent): void {
  if (event.key !== 'Tab') return
  const leaving = event.shiftKey
    ? event.target === searchInput.value
    : !props.modelValue || event.target !== searchInput.value
  if (leaving) hidePopup(true)
}

function handleFocusOut(event: FocusEvent): void {
  const target = event.relatedTarget as Node | null
  if (target && (popup.value?.contains(target) || searchTrigger.value?.contains(target) || moreTrigger.value?.contains(target))) return
  hidePopup()
}

function clearSearch(): void {
  emit('update:modelValue', '')
  searchInput.value?.focus()
}

function activate(action: 'openHistory' | 'openBrowser' | 'openTaskSettings' | 'toggleSidebar'): void {
  hidePopup(true)
  switch (action) {
    case 'openHistory': emit('openHistory'); break
    case 'openBrowser': emit('openBrowser'); break
    case 'openTaskSettings': emit('openTaskSettings'); break
    case 'toggleSidebar': emit('toggleSidebar'); break
  }
}

function removeListeners(): void {
  document.removeEventListener('pointerdown', handleOutsidePointer, true)
  document.removeEventListener('keydown', handleEscape, true)
  window.removeEventListener('resize', positionPopup)
  window.removeEventListener('scroll', positionPopup, true)
}

watch(openPopup, (open) => {
  removeListeners()
  if (!open) return
  document.addEventListener('pointerdown', handleOutsidePointer, true)
  document.addEventListener('keydown', handleEscape, true)
  window.addEventListener('resize', positionPopup)
  window.addEventListener('scroll', positionPopup, true)
})
watch(() => props.contextKey, () => hidePopup())
onMounted(() => {
  resizeObserver = new ResizeObserver(positionPopup)
  if (toolbar.value) {
    resizeObserver.observe(toolbar.value)
    if (toolbar.value.parentElement) resizeObserver.observe(toolbar.value.parentElement)
  }
})
onBeforeUnmount(() => {
  removeListeners()
  resizeObserver?.disconnect()
})
</script>

<template>
  <nav ref="toolbar" class="conversation-toolbar" aria-label="会话操作">
    <button v-if="!connected" type="button" class="toolbar-button toolbar-reconnect" :title="connectionTitle"
      :aria-label="connectionLabel" :disabled="reconnecting" @click="emit('reconnect')">
      <RefreshCw :size="16" :class="{ spin: reconnecting }" />
      <span class="toolbar-button-label">{{ connectionLabel }}</span>
    </button>
    <button ref="searchTrigger" type="button" class="toolbar-button" :class="{ active: openPopup === 'search' || modelValue.trim() }"
      :title="modelValue.trim() ? `搜索对话：${modelValue}` : '搜索对话'" aria-label="搜索对话" aria-haspopup="dialog"
      :aria-expanded="openPopup === 'search'" :aria-controls="openPopup === 'search' ? `${id}-search` : undefined"
      @click="togglePopup('search')" @keydown="handleTriggerKey($event, 'search')">
      <Search :size="18" />
      <span v-if="modelValue.trim()" class="toolbar-filter-dot" aria-label="已应用搜索" />
    </button>
    <div v-if="hasProject || hasWorkspace" class="toolbar-group">
      <button v-if="hasProject" type="button" class="toolbar-button toolbar-open" title="打开项目所在文件夹"
        aria-label="打开项目所在文件夹" @click="emit('showProject')">
        <FolderOpen :size="17" /><span class="toolbar-button-label">打开位置</span>
      </button>
      <button v-if="hasWorkspace" type="button" class="toolbar-button" title="在工作区目录打开终端"
        aria-label="在工作区目录打开终端" @click="emit('openTerminal')"><TerminalSquare :size="18" /></button>
    </div>
    <span v-if="hasProject" class="toolbar-separator" aria-hidden="true" />
    <button v-if="hasProject" type="button" class="toolbar-button" :class="{ active: changesOpen }" title="查看代码更改"
      aria-label="查看代码更改" :aria-pressed="changesOpen" @click="emit('openChanges')"><Braces :size="18" /></button>
    <button ref="moreTrigger" type="button" class="toolbar-button" :class="{ active: openPopup === 'more' }"
      title="更多操作" aria-label="更多操作" aria-haspopup="menu" :aria-expanded="openPopup === 'more'"
      :aria-controls="openPopup === 'more' ? `${id}-more` : undefined"
      @click="togglePopup('more')" @keydown="handleTriggerKey($event, 'more')"><MoreHorizontal :size="18" /></button>
  </nav>

  <Teleport to="body">
    <section v-if="openPopup === 'search'" :id="`${id}-search`" ref="popup" class="toolbar-popover toolbar-search-popover"
      role="dialog" aria-label="搜索对话" :style="popupStyle" @focusout="handleFocusOut" @keydown="handleSearchKey">
      <form class="toolbar-search-field" role="search" @submit.prevent="hidePopup(true)">
        <Search :size="16" aria-hidden="true" />
        <input ref="searchInput" :value="modelValue" type="search" placeholder="搜索对话…" aria-label="搜索内容" autocomplete="off"
          @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)">
        <button v-if="modelValue" type="button" class="toolbar-search-clear" title="清除搜索" aria-label="清除搜索" @click="clearSearch"><X :size="14" /></button>
      </form>
    </section>
    <div v-if="openPopup === 'more'" :id="`${id}-more`" ref="popup" class="toolbar-popover toolbar-more-menu"
      role="menu" aria-label="更多会话操作" :style="popupStyle" @keydown="handleMenuKey" @focusout="handleFocusOut">
      <button type="button" role="menuitem" :disabled="!hasThread || !connected" @click="activate('openBrowser')">
        <Globe2 :size="16" /><span>会话浏览器</span><Check v-if="browserOpen" :size="14" aria-label="已打开" />
      </button>
      <button v-if="hasProject" type="button" role="menuitem" @click="activate('openHistory')">
        <History :size="16" /><span>Git 提交记录</span><Check v-if="historyOpen" :size="14" aria-label="已打开" />
      </button>
      <div class="toolbar-menu-separator" role="separator" />
      <button type="button" role="menuitem" :disabled="!hasThread" @click="activate('openTaskSettings')">
        <Settings2 :size="16" /><span>任务设置</span>
      </button>
      <button type="button" role="menuitemcheckbox" :aria-checked="sidebarOpen" @click="activate('toggleSidebar')">
        <PanelLeft :size="16" /><span>显示侧栏</span><Check v-if="sidebarOpen" :size="14" />
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.conversation-toolbar, .toolbar-group {
  display: flex;
  align-items: center;
  gap: 4px;
  flex: 0 0 auto;
  -webkit-app-region: no-drag;
}

.toolbar-button {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 7px;
  flex: 0 0 auto;
  height: 32px;
  min-width: 32px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: var(--radius-control);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  white-space: nowrap;
  transition: color 120ms ease, background-color 120ms ease;
}

.toolbar-button:hover:not(:disabled), .toolbar-button.active {
  background: var(--surface-hover);
  color: var(--text);
}

.toolbar-button:disabled {
  opacity: .45;
  cursor: not-allowed;
}

.toolbar-button.toolbar-open {
  padding: 0 10px;
  margin: 0 2px;
  border-color: var(--border);
  border-radius: 9px;
  color: var(--text);
  font-weight: 500;
}

.toolbar-reconnect {
  padding: 0 8px;
  color: var(--danger);
}

.toolbar-separator {
  width: 1px;
  height: 16px;
  margin: 0 5px;
  background: var(--border);
}

.toolbar-filter-dot {
  position: absolute;
  top: 5px;
  right: 4px;
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--accent);
}

.toolbar-popover {
  position: fixed;
  z-index: 1000;
  overflow: auto;
  padding: 5px;
  border: 1px solid var(--border);
  border-radius: var(--radius-popover);
  background: var(--surface-raised);
  color: var(--text);
  box-shadow: var(--popover-shadow);
  -webkit-app-region: no-drag;
}

.toolbar-search-popover {
  padding: 7px;
}

.toolbar-search-field {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 7px;
  color: var(--text-muted);
}

.toolbar-search-field input {
  min-width: 0;
  flex: 1;
  padding: 4px 0;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--text);
  font: 13px var(--font-sans);
}

.toolbar-search-field:focus-within {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
  border-radius: 6px;
}

.toolbar-search-field input:focus-visible {
  outline: none;
}

.toolbar-search-field input::placeholder {
  color: var(--text-muted);
}

.toolbar-search-field input::-webkit-search-cancel-button {
  display: none;
}

.toolbar-search-clear {
  display: grid;
  place-items: center;
  flex: none;
  width: 24px;
  height: 24px;
  padding: 0;
  border-radius: 5px;
  color: var(--text-muted);
}

.toolbar-search-clear:hover {
  background: var(--surface-hover);
  color: var(--text);
}

.toolbar-more-menu button {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  min-height: 34px;
  padding: 7px 9px;
  border-radius: 7px;
  color: var(--text);
  background: transparent;
  font-size: 13px;
  text-align: left;
}

.toolbar-more-menu button > span {
  flex: 1;
}

.toolbar-more-menu button > svg {
  flex: none;
  color: var(--text-secondary);
}

.toolbar-more-menu button:hover:not(:disabled), .toolbar-more-menu button:focus-visible {
  background: var(--surface-hover);
}

.toolbar-more-menu button:disabled {
  opacity: .4;
  cursor: not-allowed;
}

.toolbar-menu-separator {
  height: 1px;
  margin: 5px 4px;
  background: var(--border);
}

.toolbar-button:focus-visible, .toolbar-more-menu button:focus-visible, .toolbar-search-clear:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

@container conversation-header (max-width: 560px) {
  .toolbar-button-label {
    display: none;
  }
  .toolbar-button.toolbar-open, .toolbar-reconnect {
    width: 32px;
    padding: 0;
    margin: 0;
  }
  .conversation-toolbar, .toolbar-group {
    gap: 2px;
  }
  .toolbar-separator {
    margin: 0 3px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .toolbar-button {
    transition: none;
  }
}
</style>
