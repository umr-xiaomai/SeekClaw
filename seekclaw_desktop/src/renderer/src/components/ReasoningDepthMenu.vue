<script setup lang="ts">
import { ChevronDown, Gauge, Info, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { ReasoningLevel } from '../types'

const props = withDefaults(defineProps<{
  modelValue: ReasoningLevel
  disabled?: boolean
}>(), { disabled: false })

const emit = defineEmits<{
  'update:modelValue': [value: ReasoningLevel]
}>()

const levels = [
  { value: ReasoningLevel.Low, label: '低(low)', extended: false },
  { value: ReasoningLevel.Medium, label: '中(medium)', extended: false },
  { value: ReasoningLevel.High, label: '高(high)', extended: false },
  { value: ReasoningLevel.Max, label: '最大(max)', extended: false },
  { value: ReasoningLevel.XHigh, label: '极高(xhigh)', extended: true },
  { value: ReasoningLevel.Ultra, label: '超级(ultra)', extended: true }
] as const

const labels: Record<ReasoningLevel, string> = {
  [ReasoningLevel.None]: '关闭(none)',
  [ReasoningLevel.Low]: '低(low)',
  [ReasoningLevel.Medium]: '中(medium)',
  [ReasoningLevel.High]: '高(high)',
  [ReasoningLevel.Max]: '最大(max)',
  [ReasoningLevel.XHigh]: '极高(xhigh)',
  [ReasoningLevel.Ultra]: '超级(ultra)'
}

const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const open = ref(false)
const menuStyle = ref<Record<string, string>>({})
const selectedIndex = computed(() => levels.findIndex((level) => level.value === props.modelValue))
const fillPercent = computed(() => {
  const index = Math.max(0, selectedIndex.value)
  return `${index / (levels.length - 1) * 100}%`
})

function positionMenu(): void {
  if (!trigger.value || !open.value) return
  const rect = trigger.value.getBoundingClientRect()
  const edge = 10
  const gap = 8
  const width = Math.min(548, window.innerWidth - edge * 2)
  const left = Math.max(edge, Math.min(rect.right - width, window.innerWidth - width - edge))
  const placeAbove = window.innerHeight - rect.bottom < 230
  menuStyle.value = placeAbove
    ? { width: `${width}px`, left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px` }
    : { width: `${width}px`, left: `${left}px`, top: `${rect.bottom + gap}px` }
}

function show(): void {
  if (props.disabled) return
  open.value = true
  void nextTick(() => {
    positionMenu()
    menu.value?.querySelector<HTMLButtonElement>('.reasoning-node.active')?.focus({ preventScroll: true })
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

function select(level: ReasoningLevel): void {
  emit('update:modelValue', level)
}

function move(direction: -1 | 1): void {
  const current = selectedIndex.value >= 0 ? selectedIndex.value : 0
  const index = Math.max(0, Math.min(levels.length - 1, current + direction))
  const level = levels[index]
  if (level) {
    select(level.value)
    void nextTick(() => menu.value?.querySelectorAll<HTMLButtonElement>('.reasoning-node')[index]?.focus())
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    event.stopPropagation()
    hide(true)
  } else if (open.value && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
    event.preventDefault()
    move(event.key === 'ArrowLeft' ? -1 : 1)
  } else if (open.value && event.key === 'Home') {
    event.preventDefault()
    select(levels[0].value)
  } else if (open.value && event.key === 'End') {
    event.preventDefault()
    const last = levels.at(-1)
    if (last) select(last.value)
  }
}

function handleOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node
  if (root.value?.contains(target) || menu.value?.contains(target)) return
  hide()
}

function addListeners(): void {
  document.addEventListener('mousedown', handleOutsidePointer, true)
  window.addEventListener('resize', positionMenu)
  window.addEventListener('scroll', positionMenu, true)
}

function removeListeners(): void {
  document.removeEventListener('mousedown', handleOutsidePointer, true)
  window.removeEventListener('resize', positionMenu)
  window.removeEventListener('scroll', positionMenu, true)
}

watch(open, (value) => value ? addListeners() : removeListeners())
watch(() => props.disabled, (value) => { if (value) hide() })
onBeforeUnmount(removeListeners)
</script>

<template>
  <div ref="root" class="reasoning-control" :class="{ open, disabled }">
    <button
      ref="trigger"
      type="button"
      class="reasoning-trigger"
      :disabled="disabled"
      aria-haspopup="dialog"
      :aria-expanded="open"
      @click="toggle"
      @keydown="handleKeydown"
    >
      <Gauge :size="15" />
      <span>思考深度：{{ labels[modelValue] }}</span>
      <ChevronDown :size="13" class="reasoning-chevron" :class="{ rotated: open }" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <section
          v-if="open"
          ref="menu"
          class="reasoning-popover"
          role="dialog"
          aria-label="选择思考深度"
          :style="menuStyle"
          @keydown="handleKeydown"
        >
          <header class="reasoning-popover-header">
            <div><strong>思考深度</strong><small>固定档位 · 当前为{{ labels[modelValue] }}</small></div>
            <button type="button" class="reasoning-close" title="收起" @click="hide(true)"><X :size="15" /></button>
          </header>

          <div class="reasoning-slider" role="radiogroup" aria-label="思考深度档位">
            <div class="reasoning-track"><span :style="{ width: fillPercent }" /></div>
            <button
              v-for="(level, index) in levels"
              :key="level.value"
              type="button"
              class="reasoning-node"
              :class="{
                active: index === selectedIndex,
                completed: selectedIndex >= 0 && index < selectedIndex,
                extended: level.extended
              }"
              role="radio"
              :aria-checked="level.value === modelValue"
              :title="level.label"
              @click="select(level.value)"
            >
              <i /><span>{{ level.label }}</span>
            </button>
          </div>

          <p class="reasoning-hint">
            <Info :size="14" />
            <span>极高(xhigh)和超级(ultra)为扩展模式，部分模型可能不支持，请求时可能自动转换为最大(max)模式。</span>
          </p>
        </section>
      </Transition>
    </Teleport>
  </div>
</template>
