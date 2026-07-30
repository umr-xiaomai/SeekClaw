<script setup lang="ts">
import { Check, ChevronDown } from '@lucide/vue'
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
}>(), {
  disabled: false,
  placeholder: '请选择',
  menuMinWidth: 180
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

const selectedIndex = computed(() => props.options.findIndex((option) => option.value === props.modelValue))
const selectedOption = computed(() => props.options[selectedIndex.value])

function firstEnabledIndex(): number {
  return props.options.findIndex((option) => !option.disabled)
}

function lastEnabledIndex(): number {
  for (let index = props.options.length - 1; index >= 0; index -= 1) {
    if (!props.options[index]?.disabled) return index
  }
  return -1
}

function moveHighlight(direction: 1 | -1): void {
  if (props.options.length === 0) return
  let index = highlighted.value
  for (let count = 0; count < props.options.length; count += 1) {
    index = (index + direction + props.options.length) % props.options.length
    if (!props.options[index]?.disabled) {
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
  const desiredHeight = Math.min(320, props.options.length * 42 + 8)
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
  highlighted.value = selectedIndex.value >= 0 && !props.options[selectedIndex.value]?.disabled
    ? selectedIndex.value
    : firstEnabledIndex()
  void nextTick(() => {
    positionMenu()
    scrollHighlightedIntoView()
  })
}

function hide(restoreFocus = false): void {
  open.value = false
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
      const option = props.options[highlighted.value]
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
    const match = props.options.findIndex((option) => !option.disabled && option.label.toLocaleLowerCase().startsWith(typeahead))
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
    <button
      ref="trigger"
      class="custom-select-trigger"
      type="button"
      :disabled="disabled"
      :aria-label="label"
      aria-haspopup="listbox"
      :aria-expanded="open"
      :aria-controls="listboxId"
      @click="toggle"
      @keydown="handleKeydown"
    >
      <span class="custom-select-value" :class="{ placeholder: !selectedOption }">
        {{ selectedOption?.label || modelValue || placeholder }}
      </span>
      <ChevronDown class="custom-select-chevron" :size="15" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <div
          v-if="open"
          :id="listboxId"
          ref="menu"
          class="custom-select-menu"
          role="listbox"
          :aria-label="label"
          :style="menuStyle"
          @keydown="handleKeydown"
        >
          <button
            v-for="(option, index) in options"
            :key="option.value"
            type="button"
            class="custom-select-option"
            :class="{ selected: option.value === modelValue, highlighted: index === highlighted }"
            :disabled="option.disabled"
            role="option"
            :aria-selected="option.value === modelValue"
            :data-index="index"
            @mouseenter="!option.disabled && (highlighted = index)"
            @click="select(option)"
          >
            <span class="custom-select-option-copy">
              <strong>{{ option.label }}</strong>
              <small v-if="option.description">{{ option.description }}</small>
            </span>
            <Check v-if="option.value === modelValue" :size="16" />
          </button>
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
