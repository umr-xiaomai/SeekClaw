<script setup lang="ts">
import { Check, ChevronDown, ChevronLeft, ChevronRight, Search } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { ReasoningLevel } from '../types'

const props = withDefaults(defineProps<{
  model: string
  models: string[]
  reasoningLevel: ReasoningLevel
  disabled?: boolean
}>(), {
  disabled: false
})

const emit = defineEmits<{
  'update:model': [model: string]
  'update:reasoningLevel': [level: ReasoningLevel]
}>()

interface ReasoningStep {
  value: ReasoningLevel
  label: string
}

const open = ref(false)
const currentView = ref<'intensity' | 'model'>('intensity')
const searchQuery = ref('')
const trigger = ref<HTMLButtonElement | null>(null)
const popover = ref<HTMLElement | null>(null)
const sliderTrack = ref<HTMLElement | null>(null)
const popoverStyle = ref<Record<string, string>>({})
const isDragging = ref(false)

const steps = computed<ReasoningStep[]>(() => {
  if (props.reasoningLevel === ReasoningLevel.None) {
    return [
      { value: ReasoningLevel.None, label: '关闭' },
      { value: ReasoningLevel.Low, label: '低' },
      { value: ReasoningLevel.Medium, label: '中' },
      { value: ReasoningLevel.High, label: '高' }
    ]
  }
  if (
    props.reasoningLevel === ReasoningLevel.Max ||
    props.reasoningLevel === ReasoningLevel.XHigh ||
    props.reasoningLevel === ReasoningLevel.Ultra
  ) {
    return [
      { value: ReasoningLevel.Low, label: '低' },
      { value: ReasoningLevel.Medium, label: '中' },
      { value: ReasoningLevel.High, label: '高' },
      { value: ReasoningLevel.Max, label: '最大' }
    ]
  }
  return [
    { value: ReasoningLevel.Low, label: '低' },
    { value: ReasoningLevel.Medium, label: '中' },
    { value: ReasoningLevel.High, label: '高' }
  ]
})

const currentStepIndex = computed(() => {
  const idx = steps.value.findIndex((s) => s.value === props.reasoningLevel)
  return idx >= 0 ? idx : steps.value.length - 1
})

const currentLabel = computed(() => {
  return steps.value[currentStepIndex.value]?.label ?? '高'
})

const fillPercentNumber = computed(() => {
  if (steps.value.length <= 1) return 100
  return (currentStepIndex.value / (steps.value.length - 1)) * 100
})

const fillPercent = computed(() => `${fillPercentNumber.value}%`)

function formatModelName(name?: string): string {
  if (!name) return '未配置模型'
  const trimmed = name.trim()
  if (!trimmed) return '未配置模型'
  // If format is provider/model, keep it readable
  if (trimmed.includes('/')) {
    const parts = trimmed.split('/')
    return parts[parts.length - 1] || trimmed
  }
  return trimmed
}

const displayModel = computed(() => formatModelName(props.model))

const triggerLabel = computed(() => {
  if (open.value && currentView.value === 'intensity') {
    return '选择强度'
  }
  if (!props.model) return '未配置模型'
  return `${displayModel.value} ${currentLabel.value}`
})

const filteredModels = computed(() => {
  const query = searchQuery.value.trim().toLowerCase()
  if (!query) return props.models
  return props.models.filter((m) => m.toLowerCase().includes(query))
})

function positionPopover(): void {
  if (!trigger.value || !open.value) return
  const rect = trigger.value.getBoundingClientRect()
  const edge = 10
  const gap = 8
  const width = Math.min(300, window.innerWidth - edge * 2)
  const right = Math.min(window.innerWidth - edge, rect.right)
  const left = Math.max(edge, right - width)
  const placeAbove = window.innerHeight - rect.bottom < 260
  popoverStyle.value = placeAbove
    ? { width: `${width}px`, left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px` }
    : { width: `${width}px`, left: `${left}px`, top: `${rect.bottom + gap}px` }
}

function show(): void {
  if (props.disabled) return
  open.value = true
  currentView.value = 'intensity'
  searchQuery.value = ''
  void nextTick(() => {
    positionPopover()
  })
}

function hide(restoreFocus = false): void {
  open.value = false
  isDragging.value = false
  if (restoreFocus) void nextTick(() => trigger.value?.focus())
}

function toggle(): void {
  if (open.value) hide()
  else show()
}

function selectModel(m: string): void {
  emit('update:model', m)
  currentView.value = 'intensity'
}

function updateLevelFromClientX(clientX: number): void {
  if (!sliderTrack.value) return
  const rect = sliderTrack.value.getBoundingClientRect()
  if (rect.width <= 0) return
  const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width))
  const stepCount = steps.value.length
  if (stepCount <= 1) return
  const nearestIndex = Math.round(ratio * (stepCount - 1))
  const targetStep = steps.value[nearestIndex]
  if (targetStep && targetStep.value !== props.reasoningLevel) {
    emit('update:reasoningLevel', targetStep.value)
  }
}

function handleTrackPointerDown(event: PointerEvent): void {
  if (props.disabled) return
  isDragging.value = true
  updateLevelFromClientX(event.clientX)
  ;(event.currentTarget as HTMLElement)?.setPointerCapture(event.pointerId)
}

function handleTrackPointerMove(event: PointerEvent): void {
  if (!isDragging.value) return
  updateLevelFromClientX(event.clientX)
}

function handleTrackPointerUp(event: PointerEvent): void {
  if (!isDragging.value) return
  isDragging.value = false
  try {
    ;(event.currentTarget as HTMLElement)?.releasePointerCapture(event.pointerId)
  } catch {
    // ignore
  }
}

function handleOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node | null
  if (!target) return
  if (trigger.value?.contains(target)) return
  if (popover.value?.contains(target)) return
  hide()
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    event.stopPropagation()
    hide(true)
    return
  }
  if (open.value && currentView.value === 'intensity') {
    if (event.key === 'ArrowLeft') {
      event.preventDefault()
      const nextIdx = Math.max(0, currentStepIndex.value - 1)
      const step = steps.value[nextIdx]
      if (step) emit('update:reasoningLevel', step.value)
    } else if (event.key === 'ArrowRight') {
      event.preventDefault()
      const nextIdx = Math.min(steps.value.length - 1, currentStepIndex.value + 1)
      const step = steps.value[nextIdx]
      if (step) emit('update:reasoningLevel', step.value)
    }
  }
}

function addListeners(): void {
  document.addEventListener('mousedown', handleOutsidePointer, true)
  window.addEventListener('resize', positionPopover)
  window.addEventListener('scroll', positionPopover, true)
}

function removeListeners(): void {
  document.removeEventListener('mousedown', handleOutsidePointer, true)
  window.removeEventListener('resize', positionPopover)
  window.removeEventListener('scroll', positionPopover, true)
}

watch(open, (isOpen) => isOpen ? addListeners() : removeListeners())
watch(() => props.disabled, (disabled) => { if (disabled) hide() })
onBeforeUnmount(removeListeners)
</script>

<template>
  <div class="model-reasoning-wrap">
    <button
      ref="trigger"
      type="button"
      class="model-reasoning-trigger"
      :class="{ open, disabled }"
      :disabled="disabled"
      aria-haspopup="dialog"
      :aria-expanded="open"
      :title="`模型与强度：${displayModel} (${currentLabel})`"
      @click="toggle"
      @keydown="handleKeydown"
    >
      <span class="trigger-label">{{ triggerLabel }}</span>
      <ChevronDown :size="12" class="trigger-chevron" :class="{ rotated: open }" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <section
          v-if="open"
          ref="popover"
          class="model-reasoning-popover"
          role="dialog"
          :aria-label="currentView === 'intensity' ? '选择思考强度' : '选择模型'"
          :style="popoverStyle"
          @keydown="handleKeydown"
        >
          <!-- View 1: Reasoning Intensity View (Codex Style) -->
          <div v-if="currentView === 'intensity'" class="intensity-view">
            <div class="intensity-header">
              <div class="intensity-title">{{ currentLabel }}</div>
              <button
                type="button"
                class="intensity-model-link"
                title="点击选择模型"
                @click="currentView = 'model'"
              >
                <span>{{ displayModel }}</span>
                <ChevronRight :size="13" class="link-chevron" />
              </button>
            </div>

            <!-- Thick Stepped Slider Bar -->
            <div
              ref="sliderTrack"
              class="slider-bar-track"
              :class="{ dragging: isDragging }"
              role="slider"
              aria-label="思考强度"
              :aria-valuenow="currentStepIndex"
              :aria-valuemin="0"
              :aria-valuemax="steps.length - 1"
              :aria-valuetext="currentLabel"
              tabindex="0"
              @pointerdown="handleTrackPointerDown"
              @pointermove="handleTrackPointerMove"
              @pointerup="handleTrackPointerUp"
              @pointercancel="handleTrackPointerUp"
            >
              <!-- Filled Blue Portion -->
              <div class="slider-bar-fill" :style="{ width: fillPercent }" />

              <!-- Intermediate Step Dots -->
              <div
                v-for="(step, index) in steps"
                :key="step.value"
                class="slider-step-dot"
                :class="{
                  active: index <= currentStepIndex,
                  first: index === 0,
                  last: index === steps.length - 1
                }"
                :style="{
                  left: `calc(13px + (100% - 26px) * (${index} / ${steps.length - 1}))`
                }"
              />

              <!-- White Circular Thumb -->
              <div
                class="slider-bar-thumb"
                :style="{
                  left: `calc(13px + (100% - 26px) * (${currentStepIndex} / ${steps.length - 1}))`
                }"
              />
            </div>
          </div>

          <!-- View 2: Model Selection View (Codex Style) -->
          <div v-else class="model-select-view">
            <header class="model-select-header">
              <button
                type="button"
                class="model-back-btn"
                title="返回强度设置"
                @click="currentView = 'intensity'"
              >
                <ChevronLeft :size="15" />
              </button>
              <span class="model-select-title">选择模型</span>
            </header>

            <div v-if="models.length > 6" class="model-search-box">
              <Search :size="13" class="search-icon" />
              <input
                v-model="searchQuery"
                type="text"
                class="model-search-input"
                placeholder="搜索模型…"
                aria-label="搜索模型"
              />
            </div>

            <div class="model-options-list" role="listbox" aria-label="可用模型列表">
              <button
                v-for="m in filteredModels"
                :key="m"
                type="button"
                class="model-option-row"
                :class="{ active: m === model }"
                role="option"
                :aria-selected="m === model"
                @click="selectModel(m)"
              >
                <span class="model-option-name">{{ formatModelName(m) }}</span>
                <Check v-if="m === model" :size="15" class="model-option-check" />
              </button>
              <div v-if="filteredModels.length === 0" class="model-empty-hint">
                没有找到匹配的模型
              </div>
            </div>
          </div>
        </section>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.model-reasoning-wrap {
  position: relative;
  display: inline-flex;
  align-items: center;
}

.model-reasoning-trigger {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 28px;
  padding: 0 4px;
  font-size: 13px;
  font-weight: 450;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  user-select: none;
  transition: all 140ms ease;
  white-space: nowrap;
}

.model-reasoning-trigger:hover:not(:disabled) {
  color: var(--text);
  background: var(--surface-hover);
}

.model-reasoning-trigger.open {
  background: var(--surface-hover, rgba(0, 0, 0, 0.06));
  color: var(--text);
  border-radius: 14px;
  padding: 0 10px;
}

:root[data-theme="dark"] .model-reasoning-trigger.open {
  background: var(--surface-hover, rgba(255, 255, 255, 0.08));
}

.model-reasoning-trigger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.trigger-label {
  font-size: 12.5px;
  line-height: 1;
}

.trigger-chevron {
  opacity: 0.55;
  transition: transform 140ms ease;
}

.trigger-chevron.rotated {
  transform: rotate(180deg);
}

/* Popover Card (Codex Style) */
.model-reasoning-popover {
  position: fixed;
  z-index: 1000;
  box-sizing: border-box;
  padding: 16px 18px 20px;
  border-radius: 20px;
  border: 1px solid var(--border);
  background: var(--surface);
  box-shadow: 0 16px 42px rgba(0, 0, 0, 0.16), 0 3px 12px rgba(0, 0, 0, 0.08);
}

:root[data-theme="dark"] .model-reasoning-popover {
  background: var(--surface-raised);
  box-shadow: 0 20px 48px rgba(0, 0, 0, 0.45), 0 4px 16px rgba(0, 0, 0, 0.25);
}

/* Intensity View */
.intensity-view {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 100%;
}

.intensity-header {
  display: flex;
  flex-direction: column;
  align-items: center;
  margin-bottom: 16px;
}

.intensity-title {
  font-size: 18px;
  font-weight: 600;
  color: #1677ff;
  line-height: 1.2;
}

:root[data-theme="dark"] .intensity-title {
  color: #38bdf8;
}

.intensity-model-link {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  margin-top: 4px;
  padding: 2px 6px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  font-size: 13px;
  font-weight: 400;
  border-radius: 6px;
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease;
}

.intensity-model-link:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.link-chevron {
  opacity: 0.6;
}

/* Stepped Slider Bar (Codex Style) */
.slider-bar-track {
  position: relative;
  width: 100%;
  height: 26px;
  border-radius: 13px;
  background: #e2e8f0;
  cursor: pointer;
  user-select: none;
  touch-action: none;
  outline: none;
}

:root[data-theme="dark"] .slider-bar-track {
  background: #2d3039;
}

.slider-bar-fill {
  position: absolute;
  top: 0;
  left: 0;
  height: 100%;
  border-radius: 13px;
  background: #1677ff;
  pointer-events: none;
  transition: width 130ms cubic-bezier(0.4, 0, 0.2, 1);
}

:root[data-theme="dark"] .slider-bar-fill {
  background: #0ea5e9;
}

.slider-bar-track.dragging .slider-bar-fill,
.slider-bar-track.dragging .slider-bar-thumb {
  transition: none;
}

/* Intermediate Dots */
.slider-step-dot {
  position: absolute;
  top: 50%;
  width: 4px;
  height: 4px;
  border-radius: 50%;
  transform: translate(-50%, -50%);
  background: rgba(0, 0, 0, 0.22);
  pointer-events: none;
  transition: background-color 130ms ease;
}

.slider-step-dot.active {
  background: rgba(255, 255, 255, 0.65);
}

:root[data-theme="dark"] .slider-step-dot {
  background: rgba(255, 255, 255, 0.25);
}

:root[data-theme="dark"] .slider-step-dot.active {
  background: rgba(255, 255, 255, 0.85);
}

/* White Circle Thumb */
.slider-bar-thumb {
  position: absolute;
  top: 0;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: #ffffff;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.22), 0 1px 2px rgba(0, 0, 0, 0.1);
  transform: translateX(-50%);
  pointer-events: none;
  transition: left 130ms cubic-bezier(0.4, 0, 0.2, 1);
}

/* Model Select View */
.model-select-view {
  display: flex;
  flex-direction: column;
  width: 100%;
}

.model-select-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 2px 10px;
}

.model-back-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  border-radius: 6px;
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease;
}

.model-back-btn:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.model-select-title {
  font-size: 13.5px;
  font-weight: 500;
  color: var(--text-muted);
}

.model-search-box {
  position: relative;
  margin-bottom: 8px;
}

.search-icon {
  position: absolute;
  top: 50%;
  left: 8px;
  transform: translateY(-50%);
  color: var(--text-muted);
  pointer-events: none;
}

.model-search-input {
  width: 100%;
  height: 30px;
  padding: 0 8px 0 28px;
  font-size: 12.5px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
  outline: none;
  box-sizing: border-box;
}

.model-search-input:focus {
  border-color: var(--accent);
}

.model-options-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 240px;
  overflow-y: auto;
  scrollbar-width: thin;
}

.model-option-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 8px 10px;
  border: none;
  background: transparent;
  color: var(--text);
  border-radius: 10px;
  cursor: pointer;
  text-align: left;
  transition: background-color 120ms ease;
}

.model-option-row:hover {
  background: var(--surface-hover);
}

.model-option-name {
  font-size: 13.5px;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.model-option-check {
  color: var(--accent, #1677ff);
  flex-shrink: 0;
}

.model-empty-hint {
  padding: 16px 8px;
  text-align: center;
  font-size: 12.5px;
  color: var(--text-muted);
}
</style>
