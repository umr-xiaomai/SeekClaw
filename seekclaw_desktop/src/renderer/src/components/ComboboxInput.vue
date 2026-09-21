<script setup lang="ts">
import { Check, ChevronDown } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

export interface ComboboxOption {
  value: number
  label: string
  description?: string
}

const props = withDefaults(defineProps<{
  modelValue: number
  options: ComboboxOption[]
  placeholder?: string
  disabled?: boolean
  menuMinWidth?: number
}>(), {
  placeholder: '',
  disabled: false,
  menuMinWidth: 240
})

const emit = defineEmits<{
  'update:modelValue': [value: number]
  change: [value: number]
}>()

const wrapperRef = ref<HTMLElement | null>(null)
const inputRef = ref<HTMLInputElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)
const open = ref(false)
const highlightedIndex = ref(-1)
const menuStyle = ref<Record<string, string>>({})

const selectedIndex = computed(() =>
  props.options.findIndex((opt) => opt.value === props.modelValue)
)

function positionMenu(): void {
  if (!wrapperRef.value || !open.value) return
  const rect = wrapperRef.value.getBoundingClientRect()
  const gap = 5
  const edge = 8
  const width = Math.min(window.innerWidth - edge * 2, Math.max(rect.width, props.menuMinWidth))
  const desiredHeight = Math.min(280, props.options.length * 38 + 12)
  const below = window.innerHeight - rect.bottom - gap - edge
  const above = rect.top - gap - edge
  const placeAbove = below < Math.min(desiredHeight, 160) && above > below
  const maxHeight = Math.max(96, Math.min(280, placeAbove ? above : below))
  const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))

  menuStyle.value = placeAbove
    ? { left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px`, width: `${width}px`, maxHeight: `${maxHeight}px` }
    : { left: `${left}px`, top: `${rect.bottom + gap}px`, width: `${width}px`, maxHeight: `${maxHeight}px` }
}

function show(): void {
  if (props.disabled) return
  open.value = true
  highlightedIndex.value = selectedIndex.value >= 0 ? selectedIndex.value : 0
  void nextTick(() => {
    positionMenu()
    scrollHighlightedIntoView()
  })
}

function hide(): void {
  open.value = false
  highlightedIndex.value = -1
}

function toggle(): void {
  if (open.value) hide()
  else show()
}

function select(option: ComboboxOption): void {
  emit('update:modelValue', option.value)
  emit('change', option.value)
  hide()
  void nextTick(() => inputRef.value?.focus())
}

function scrollHighlightedIntoView(): void {
  void nextTick(() => {
    menuRef.value?.querySelector<HTMLElement>(`[data-index="${highlightedIndex.value}"]`)?.scrollIntoView({ block: 'nearest' })
  })
}

function handleInput(event: Event): void {
  const target = event.target as HTMLInputElement
  const raw = target.value.replace(/[^\d]/g, '')
  target.value = raw
  if (raw !== '') {
    const num = Number(raw)
    if (!isNaN(num)) {
      emit('update:modelValue', num)
      emit('change', num)
    }
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (props.disabled) return
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    if (!open.value) {
      show()
    } else {
      highlightedIndex.value = (highlightedIndex.value + 1) % props.options.length
      scrollHighlightedIntoView()
    }
    return
  }
  if (event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) {
      show()
    } else {
      highlightedIndex.value = (highlightedIndex.value - 1 + props.options.length) % props.options.length
      scrollHighlightedIntoView()
    }
    return
  }
  if (event.key === 'Enter') {
    if (open.value && highlightedIndex.value >= 0 && highlightedIndex.value < props.options.length) {
      const opt = props.options[highlightedIndex.value]
      if (opt) {
        event.preventDefault()
        select(opt)
        return
      }
    }
  }
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    event.stopPropagation()
    hide()
    return
  }
}

function handleDocumentPointer(event: MouseEvent): void {
  const target = event.target as Node
  if (wrapperRef.value?.contains(target) || menuRef.value?.contains(target)) return
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

watch(open, (val) => {
  if (val) addFloatingListeners()
  else removeFloatingListeners()
})

watch(() => props.disabled, (val) => {
  if (val) hide()
})

onBeforeUnmount(() => {
  removeFloatingListeners()
})
</script>

<template>
  <div ref="wrapperRef" class="combobox-wrapper" :class="{ open, disabled }">
    <input
      ref="inputRef"
      class="form-input combobox-input"
      type="text"
      inputmode="numeric"
      :value="modelValue"
      :placeholder="placeholder"
      :disabled="disabled"
      @input="handleInput"
      @keydown="handleKeydown"
    />
    <button
      type="button"
      class="combobox-chevron-btn"
      :class="{ open }"
      :disabled="disabled"
      tabindex="-1"
      title="选择常用预设"
      @click="toggle"
    >
      <ChevronDown :size="15" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <div
          v-if="open"
          ref="menuRef"
          class="combobox-menu"
          role="listbox"
          :style="menuStyle"
        >
          <button
            v-for="(opt, idx) in options"
            :key="opt.value"
            type="button"
            class="combobox-option"
            :class="{ selected: opt.value === modelValue, highlighted: idx === highlightedIndex }"
            :data-index="idx"
            role="option"
            :aria-selected="opt.value === modelValue"
            @mouseenter="highlightedIndex = idx"
            @click="select(opt)"
          >
            <span class="combobox-option-label">
              <strong>{{ opt.label }}</strong>
              <small v-if="opt.description">{{ opt.description }}</small>
            </span>
            <Check v-if="opt.value === modelValue" class="combobox-option-check" :size="15" />
          </button>
        </div>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.combobox-wrapper {
  position: relative;
  display: flex;
  align-items: center;
  width: 100%;
}

.combobox-input {
  width: 100%;
  box-sizing: border-box;
  min-height: 38px;
  padding: 8px 36px 8px 12px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  font-size: 13px;
  font-family: inherit;
  transition: border-color 140ms ease, box-shadow 140ms ease;
  -moz-appearance: textfield;
}

.combobox-input::-webkit-inner-spin-button,
.combobox-input::-webkit-outer-spin-button {
  -webkit-appearance: none;
  margin: 0;
}

.combobox-input:hover {
  border-color: color-mix(in srgb, var(--text-muted) 58%, var(--border));
}

.combobox-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.combobox-chevron-btn {
  position: absolute;
  right: 4px;
  top: 50%;
  transform: translateY(-50%);
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.combobox-chevron-btn:hover:not(:disabled) {
  color: var(--text);
  background: var(--surface-hover);
}

.combobox-chevron-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.combobox-chevron-btn svg {
  transition: transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

.combobox-chevron-btn.open svg {
  transform: rotate(180deg);
}

.combobox-menu {
  position: fixed;
  z-index: 260;
  box-sizing: border-box;
  padding: 5px;
  overflow-y: auto;
  max-height: 280px;
  color: var(--text);
  background: color-mix(in srgb, var(--surface-raised) 96%, transparent);
  border: 1px solid var(--border);
  border-radius: 9px;
  box-shadow: 0 14px 36px rgba(0, 0, 0, 0.22), 0 2px 8px rgba(0, 0, 0, 0.1);
  backdrop-filter: blur(20px) saturate(130%);
}

.combobox-option {
  display: flex;
  width: 100%;
  box-sizing: border-box;
  min-height: 34px;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 10px;
  color: var(--text-secondary, #a0aec0);
  text-align: left;
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-family: inherit;
  font-size: 13px;
  transition: background-color 120ms ease, color 120ms ease;
}

.combobox-option:hover,
.combobox-option.highlighted {
  color: var(--text);
  background: var(--surface-hover);
}

.combobox-option.selected {
  color: var(--text);
  font-weight: 600;
  background: color-mix(in srgb, var(--surface-hover) 80%, transparent);
}

.combobox-option-label {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.combobox-option-label strong {
  font-size: 13px;
  color: var(--text);
}

.combobox-option-label small {
  font-size: 11.5px;
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
}

.combobox-option-check {
  color: var(--accent);
  flex-shrink: 0;
}
</style>
