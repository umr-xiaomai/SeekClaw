<script setup lang="ts">
import { Check, ChevronDown, Search, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'

interface SelectOption {
  value: string
  label: string
  description?: string
  disabled?: boolean
}

const props = withDefaults(defineProps<{
  modelValue: string
  options: SelectOption[]
  label: string
  disabled?: boolean
  placeholder?: string
  menuMinWidth?: number
  searchable?: boolean
}>(), {
  disabled: false,
  placeholder: '请选择',
  menuMinWidth: 180,
  searchable: false
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
  change: [value: string]
}>()

const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const open = ref(false)
const highlighted = ref(-1) 
const menuStyle = ref<Record<string, string>>({})
const listboxId = `select-menu-${useId()}`
let typeahead = ''
let typeaheadTimer: ReturnType<typeof setTimeout> | undefined

const query = ref('')
const searchInput = ref<HTMLInputElement | null>(null)

const selectedIndex = computed(() => props.options.findIndex((option) => option.value === props.modelValue))
const selectedOption = computed(() => props.options[selectedIndex.value])

// Options after applying the search query (only used when `searchable`).
const visibleOptions = computed(() => {
  const needle = query.value.trim().toLocaleLowerCase()
  if (!props.searchable || !needle) return props.options
  return props.options.filter((option) =>
    option.label.toLocaleLowerCase().includes(needle)
    || option.value.toLocaleLowerCase().includes(needle)
    || (option.description?.toLocaleLowerCase().includes(needle) ?? false))
})
const selectedVisibleIndex = computed(() =>
  visibleOptions.value.findIndex((option) => option.value === props.modelValue))

function firstEnabledIndex(): number {
  return visibleOptions.value.findIndex((option) => !option.disabled)
}

function lastEnabledIndex(): number {
  for (let index = visibleOptions.value.length - 1; index >= 0; index -= 1) {
    if (!visibleOptions.value[index]?.disabled) return index
  }
  return -1
}

function moveHighlight(direction: 1 | -1): void {
  const count = visibleOptions.value.length
  if (count === 0) return
  let index = highlighted.value
  for (let step = 0; step < count; step += 1) {
    index = (index + direction + count) % count
    if (!visibleOptions.value[index]?.disabled) {
      highlighted.value = index
      scrollHighlightedIntoView()
      return
    }
  }
}

function positionMenu(): void {
  if (!trigger.value || !open.value) return
  const rect = trigger.value.getBoundingClientRect()
  const gap = 6
  const edge = 8
  const width = Math.min(window.innerWidth - edge * 2, Math.max(rect.width, props.menuMinWidth))
  const desiredHeight = Math.min(320, props.options.length * 42 + 8 + (props.searchable ? 48 : 0))
  const below = window.innerHeight - rect.bottom - gap - edge
  const above = rect.top - gap - edge
  const placeAbove = below < Math.min(desiredHeight, 190) && above > below
  const maxHeight = Math.max(96, Math.min(320, placeAbove ? above : below))
  const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))

  menuStyle.value = placeAbove
    ? { left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px`, width: `${width}px`, maxHeight: `${maxHeight}px` }
    : { left: `${left}px`, top: `${rect.bottom + gap}px`, width: `${width}px`, maxHeight: `${maxHeight}px` }
}

function scrollHighlightedIntoView(): void {
  void nextTick(() => {
    menu.value?.querySelector<HTMLElement>(`[data-index="${highlighted.value}"]`)?.scrollIntoView({ block: 'nearest' })
  })
}

function show(): void {
  if (props.disabled || props.options.length === 0) return
  open.value = true
  query.value = ''
  highlighted.value = selectedVisibleIndex.value >= 0 && !visibleOptions.value[selectedVisibleIndex.value]?.disabled
    ? selectedVisibleIndex.value
    : firstEnabledIndex()
  void nextTick(() => {
    positionMenu()
    scrollHighlightedIntoView()
    if (props.searchable) searchInput.value?.focus()
  })
}

function hide(restoreFocus = false): void {
  open.value = false
  query.value = ''
  highlighted.value = -1
  if (restoreFocus) void nextTick(() => trigger.value?.focus())
}

function toggle(): void {
  if (open.value) hide()
  else show()
}

function select(option: SelectOption): void {
  if (option.disabled) return
  emit('update:modelValue', option.value)
  emit('change', option.value)
  hide(true)
}

function handleKeydown(event: KeyboardEvent): void {
  if (props.disabled) return
  // Key events typed inside the search box bubble up to the menu; handle only
  // navigation keys there and let the input itself process text editing.
  const fromSearch = props.searchable && event.target === searchInput.value
  if (fromSearch) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      moveHighlight(event.key === 'ArrowDown' ? 1 : -1)
      return
    }
    if (event.key === 'Home') {
      event.preventDefault()
      highlighted.value = firstEnabledIndex()
      scrollHighlightedIntoView()
      return
    }
    if (event.key === 'End') {
      event.preventDefault()
      highlighted.value = lastEnabledIndex()
      scrollHighlightedIntoView()
      return
    }
    if (event.key === 'Enter') {
      event.preventDefault()
      if (highlighted.value >= 0) {
        const option = visibleOptions.value[highlighted.value]
        if (option) select(option)
      }
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      hide(true)
      return
    }
    if (event.key === 'Tab') {
      hide()
      return
    }
    return
  }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) show()
    else moveHighlight(event.key === 'ArrowDown' ? 1 : -1)
    return
  }
  if (event.key === 'Home' && open.value) {
    event.preventDefault()
    highlighted.value = firstEnabledIndex()
    scrollHighlightedIntoView()
    return
  }
  if (event.key === 'End' && open.value) {
    event.preventDefault()
    highlighted.value = lastEnabledIndex()
    scrollHighlightedIntoView()
    return
  }
  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    if (!open.value) show()
    else if (highlighted.value >= 0) {
      const option = visibleOptions.value[highlighted.value]
      if (option) select(option)
    }
    return
  }
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    event.stopPropagation()
    hide(true)
    return
  }
  if (event.key === 'Tab') {
    hide()
    return
  }
  if (!event.ctrlKey && !event.metaKey && !event.altKey && event.key.length === 1) {
    typeahead += event.key.toLocaleLowerCase()
    if (typeaheadTimer) clearTimeout(typeaheadTimer)
    typeaheadTimer = setTimeout(() => { typeahead = '' }, 650)
    const match = visibleOptions.value.findIndex((option) => !option.disabled && option.label.toLocaleLowerCase().startsWith(typeahead))
    if (match >= 0) {
      if (!open.value) show()
      highlighted.value = match
      scrollHighlightedIntoView()
    }
  }
}

function handleDocumentPointer(event: MouseEvent): void {
  const target = event.target as Node
  if (root.value?.contains(target) || menu.value?.contains(target)) return
  hide()
}

function addFloatingListeners(): void {
  document.addEventListener('mousedown', handleDocumentPointer, true)
  window.addEventListener('resize', positionMenu)
  window.addEventListener('scroll', positionMenu, true)
}

function removeFloatingListeners(): void {
  document.removeEventListener('mousedown', handleDocumentPointer, true)
  window.removeEventListener('resize', positionMenu)
  window.removeEventListener('scroll', positionMenu, true)
}

watch(open, (value) => {
  if (value) addFloatingListeners()
  else removeFloatingListeners()
})
watch(() => props.disabled, (value) => { if (value) hide() })
onBeforeUnmount(() => {
  removeFloatingListeners()
  if (typeaheadTimer) clearTimeout(typeaheadTimer)
})
</script>

<template>
  <div ref="root" class="custom-select" :class="{ open, disabled }">
    <button ref="trigger" class="custom-select-trigger" type="button" :disabled="disabled" :aria-label="label"
      aria-haspopup="listbox" :aria-expanded="open" :aria-controls="listboxId" @click="toggle" @keydown="handleKeydown">
      <span class="custom-select-value" :class="{ placeholder: !selectedOption }">
        {{ selectedOption?.label || modelValue || placeholder }}
      </span>
      <ChevronDown class="custom-select-chevron" :size="15" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <div v-if="open" :id="listboxId" ref="menu" class="custom-select-menu" role="listbox" :aria-label="label"
          :style="menuStyle" @keydown="handleKeydown">
          <div v-if="searchable" class="custom-select-search">
            <Search :size="14" />
            <input
              ref="searchInput"
              v-model="query"
              type="text"
              :placeholder="`搜索${label}`"
              aria-label="搜索选项"
              spellcheck="false"
            >
            <button v-if="query" type="button" class="custom-select-search-clear" title="清空" @click="query = ''">
              <X :size="13" />
            </button>
          </div>
          <button v-for="(option, index) in visibleOptions" :key="option.value" type="button" class="custom-select-option"
            :class="{ selected: option.value === modelValue, highlighted: index === highlighted }"
            :disabled="option.disabled" role="option" :aria-selected="option.value === modelValue" :data-index="index"
            @mouseenter="!option.disabled && (highlighted = index)" @click="select(option)">
            <span class="custom-select-option-copy">
              <strong>{{ option.label }}</strong>
              <small v-if="option.description">{{ option.description }}</small>
            </span>
            <Check v-if="option.value === modelValue" :size="16" />
          </button>
          <p v-if="visibleOptions.length === 0" class="custom-select-empty">没有匹配的选项</p>
        </div>
      </Transition>
    </Teleport>
  </div>
</template>

<style>
.custom-select {
  position: relative;
  min-width: 0;
}

.custom-select-trigger {
  display: flex;
  width: 100%;
  min-width: 0;
  min-height: 34px;
  align-items: center;
  gap: 8px;
  padding: 6px 9px 6px 10px;
  color: var(--text);
  text-align: left;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 7px;
}

.custom-select-trigger:hover:not(:disabled),
.custom-select.open .custom-select-trigger {
  background: var(--surface-hover);
  border-color: color-mix(in srgb, var(--text-muted) 58%, var(--border));
}

.custom-select.open .custom-select-trigger {
  outline: 2px solid color-mix(in srgb, var(--accent) 34%, transparent);
  outline-offset: 1px;
}

.custom-select-value {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.custom-select-value.placeholder {
  color: var(--text-muted);
}

.custom-select-chevron {
  flex: 0 0 auto;
  color: var(--text-muted);
  transition: transform 160ms ease;
}

.custom-select.open .custom-select-chevron {
  transform: rotate(180deg);
}

.custom-select.disabled {
  opacity: .58;
}

.custom-select-menu {
  position: fixed;
  z-index: 240;
  padding: 4px;
  overflow-y: auto;
  /* Keep the sticky search bar clear of items scrolled into view. */
  scroll-padding-top: 44px;
  color: var(--text);
  background: color-mix(in srgb, var(--surface-raised) 96%, transparent);
  border: 1px solid var(--border);
  border-radius: 9px;
  box-shadow: 0 14px 36px rgb(15 19 16 / 18%), 0 2px 8px rgb(15 19 16 / 10%);
  backdrop-filter: blur(20px) saturate(130%);
}

.custom-select-option {
  display: flex;
  width: 100%;
  min-height: 36px;
  align-items: center;
  gap: 10px;
  padding: 7px 9px;
  color: var(--text-secondary);
  text-align: left;
  background: transparent;
  border-radius: 6px;
}

.custom-select-option:hover:not(:disabled),
.custom-select-option.highlighted:not(:disabled) {
  color: var(--text);
  background: var(--surface-hover);
}

.custom-select-option.selected {
  color: var(--text);
  font-weight: 600;
}

.custom-select-option:disabled {
  cursor: not-allowed;
  opacity: .42;
}

.custom-select-option-copy {
  min-width: 0;
  flex: 1;
}

.custom-select-option-copy strong,
.custom-select-option-copy small {
  display: block;
  overflow: hidden;
  font-size: 13px;
  font-weight: inherit;
  line-height: 1.35;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.custom-select-option-copy small {
  margin-top: 2px;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 400;
}

.custom-select-option>svg {
  flex: 0 0 auto;
  color: var(--accent);
}

.custom-select-search {
  position: sticky;
  top: 0;
  z-index: 1;
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  padding: 5px 8px;
  color: var(--text-muted);
  background: color-mix(in srgb, var(--surface-raised) 97%, transparent);
  border: 1px solid var(--border);
  border-radius: 7px;
}

.custom-select-search input {
  min-width: 0;
  flex: 1;
  color: var(--text);
  font-size: 13px;
  background: transparent;
  border: 0;
}

/* The search bar already has an outer border; the input must not add a second
   (accent-colored) focus ring on top of it. */
.custom-select-search input:focus,
.custom-select-search input:focus-visible {
  outline: none;
  box-shadow: none;
}

.custom-select-search-clear {
  display: grid;
  flex: 0 0 auto;
  place-items: center;
  padding: 2px;
  color: var(--text-muted);
  background: transparent;
  border-radius: 4px;
}

.custom-select-search-clear:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.custom-select-empty {
  padding: 12px 9px;
  color: var(--text-muted);
  font-size: 13px;
  text-align: center;
}

.select-popover-enter-active,
.select-popover-leave-active {
  transition: opacity 120ms ease, transform 150ms cubic-bezier(.2, .8, .2, 1);
  transform-origin: center top;
}

.select-popover-enter-from,
.select-popover-leave-to {
  opacity: 0;
  transform: translateY(-4px) scale(.985);
}
</style>
