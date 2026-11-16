<script setup lang="ts">
import { Check, ChevronDown, ChevronLeft, ChevronRight, Search } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { ReasoningLevel } from '../types'

const props = withDefaults(defineProps<{
  model: string
  models: string[]
  reasoningLevel: ReasoningLevel
  maxReasoningLevel?: ReasoningLevel
  supportsReasoning?: boolean
  disabled?: boolean
}>(), {
  maxReasoningLevel: ReasoningLevel.Max,
  supportsReasoning: true,
  disabled: false
})

const emit = defineEmits<{
  'update:model': [model: string]
  'update:reasoningLevel': [level: ReasoningLevel]
}>()

interface ReasoningStep {
  value: ReasoningLevel
  label: string
  detailLabel: string
  rank: number
}

const ALL_STEPS: ReasoningStep[] = [
  { value: ReasoningLevel.Low, label: '低', detailLabel: '低强度 (Low)', rank: 1 },
  { value: ReasoningLevel.Medium, label: '中', detailLabel: '中等强度 (Medium)', rank: 2 },
  { value: ReasoningLevel.High, label: '高', detailLabel: '高强度 (High)', rank: 3 },
  { value: ReasoningLevel.Max, label: '最大', detailLabel: '最大强度 (Max)', rank: 4 },
  { value: ReasoningLevel.XHigh, label: '极高', detailLabel: '极高强度 (X-High)', rank: 5 },
  { value: ReasoningLevel.Ultra, label: '超级', detailLabel: '超级强度 (Ultra)', rank: 6 }
]

function getLevelRank(level: ReasoningLevel): number {
  if (level === ReasoningLevel.None) return 0
  const found = ALL_STEPS.find((s) => s.value === level)
  return found ? found.rank : 3
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
  const maxRank = Math.max(
    getLevelRank(props.maxReasoningLevel ?? ReasoningLevel.Max),
    getLevelRank(props.reasoningLevel)
  )

  const effectiveMaxRank =
    props.maxReasoningLevel === ReasoningLevel.High && getLevelRank(props.reasoningLevel) <= 3
      ? 3
      : Math.max(4, maxRank)

  const result = ALL_STEPS.filter((s) => s.rank <= effectiveMaxRank).map((s) => ({ ...s }))

  if (props.reasoningLevel === ReasoningLevel.None) {
    result.unshift({
      value: ReasoningLevel.None,
      label: '关闭',
      detailLabel: '已关闭思考 (None)',
      rank: 0
    })
  }

  return result
})

const currentStepIndex = computed(() => {
  const idx = steps.value.findIndex((s) => s.value === props.reasoningLevel)
  return idx >= 0 ? idx : steps.value.length - 1
})

const currentLabel = computed(() => {
  return steps.value[currentStepIndex.value]?.label ?? '高'
})

const currentDetailLabel = computed(() => {
  return steps.value[currentStepIndex.value]?.detailLabel ?? '高强度 (High)'
})

const isMaxTier = computed(() => {
  return (
    props.reasoningLevel === ReasoningLevel.Max ||
    props.reasoningLevel === ReasoningLevel.XHigh ||
    props.reasoningLevel === ReasoningLevel.Ultra
  )
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
  // Model references are providerId/modelId; the model id itself may contain '/'.
  const separator = trimmed.indexOf('/')
  if (separator > 0 && separator < trimmed.length - 1) return trimmed.slice(separator + 1)
  return trimmed
}

const displayModel = computed(() => formatModelName(props.model))

const triggerLabel = computed(() => {
  if (open.value && currentView.value === 'intensity') {
    return '选择强度'
  }
  return displayModel.value
})

const showTriggerReasoning = computed(() =>
  Boolean(props.model) && props.supportsReasoning && !(open.value && currentView.value === 'intensity')
)

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
  const width = Math.min(264, window.innerWidth - edge * 2)
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
  if (!props.supportsReasoning) {
    currentView.value = 'model'
  } else {
    currentView.value = 'intensity'
  }
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
  if (props.supportsReasoning) {
    currentView.value = 'intensity'
  } else {
    hide()
  }
}

function updateLevelFromClientX(clientX: number): void {
  if (!sliderTrack.value) return
  const rect = sliderTrack.value.getBoundingClientRect()
  if (rect.width <= 0) return
  const padding = 12
  const effectiveWidth = rect.width - padding * 2
  const offset = clientX - rect.left - padding
  const ratio = effectiveWidth > 0 ? Math.max(0, Math.min(1, offset / effectiveWidth)) : 0
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
      <span class="trigger-label">
        <span class="trigger-model-name">{{ triggerLabel }}</span>
        <span v-if="showTriggerReasoning" class="trigger-reasoning-label">{{ currentLabel }}</span>
      </span>
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
              <div class="intensity-title">{{ currentDetailLabel }}</div>
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
              :class="{ dragging: isDragging, 'max-track': isMaxTier }"
              role="slider"
              aria-label="思考强度"
              :aria-valuenow="currentStepIndex"
              :aria-valuemin="0"
              :aria-valuemax="steps.length - 1"
              :aria-valuetext="currentDetailLabel"
              tabindex="0"
              @pointerdown="handleTrackPointerDown"
              @pointermove="handleTrackPointerMove"
              @pointerup="handleTrackPointerUp"
              @pointercancel="handleTrackPointerUp"
            >
              <!-- Filled Blue Portion -->
              <div
                class="slider-bar-fill"
                :class="{ 'max-energy': isMaxTier }"
                :style="{
                  width: currentStepIndex === steps.length - 1
                    ? '100%'
                    : `calc(12px + (100% - 24px) * (${currentStepIndex} / ${steps.length - 1}))`
                }"
              >
                <div v-if="isMaxTier" class="max-particle-stream" aria-hidden="true">
                  <div class="stream-line l-1" />
                  <div class="stream-line l-2" />
                  <div class="stream-line l-3" />
                  <div class="stream-line l-4" />
                  <div class="stream-line l-5" />
                  <div class="stream-line l-6" />
                  <div class="stream-spark s-1" />
                  <div class="stream-spark s-2" />
                  <div class="stream-spark s-3" />
                  <div class="stream-spark s-4" />
                </div>
              </div>

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
                  left: `calc(12px + (100% - 24px) * (${index} / ${steps.length - 1}))`
                }"
              />

              <!-- White Circular Thumb -->
              <div
                class="slider-bar-thumb"
                :style="{
                  left: `calc(12px + (100% - 24px) * (${currentStepIndex} / ${steps.length - 1}))`
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
                aria-label="返回强度设置"
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
                :title="m"
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
  min-width: 0;
  max-width: 100%;
  flex: 0 1 auto;
}

.model-reasoning-trigger {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  max-width: 100%;
  min-height: 28px;
  padding: 5px 8px;
  font-size: 13px;
  font-weight: 400;
  border-radius: var(--radius-control);
  border: none;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  user-select: none;
  transition: background-color 140ms ease, color 140ms ease;
  text-align: left;
}

.model-reasoning-trigger:hover:not(:disabled) {
  color: var(--text);
  background: var(--surface-hover);
}

.model-reasoning-trigger.open {
  background: var(--surface-hover);
  color: var(--text);
}

.model-reasoning-trigger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.trigger-label {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0 4px;
  min-width: 0;
  font-size: 12.5px;
  line-height: 1.4;
}

.trigger-model-name {
  min-width: 0;
  overflow-wrap: anywhere;
}

.trigger-reasoning-label {
  white-space: nowrap;
}

.trigger-chevron {
  flex-shrink: 0;
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
  padding: 14px;
  border-radius: var(--radius-popover);
  border: 1px solid var(--border);
  background: var(--surface-raised);
  box-shadow: var(--popover-shadow);
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
  width: 100%;
  min-width: 0;
  margin-bottom: 14px;
}

.intensity-title {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 14px;
  font-weight: 500;
  color: var(--text);
  line-height: 1.4;
}

.intensity-model-link {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  max-width: 100%;
  margin-top: 3px;
  padding: 2px 6px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 400;
  border-radius: 6px;
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease;
}

.intensity-model-link:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.intensity-model-link span {
  min-width: 0;
  overflow-wrap: anywhere;
  line-height: 1.4;
}

.link-chevron {
  flex-shrink: 0;
  opacity: 0.6;
}

/* Stepped Slider Bar (Codex Style) */
.slider-bar-track {
  position: relative;
  width: 100%;
  height: 20px;
  border-radius: 10px;
  background: var(--surface-active);
  cursor: pointer;
  user-select: none;
  touch-action: none;
  outline: none;
  transition: box-shadow 200ms ease;
}

.slider-bar-track.max-track {
  box-shadow: 0 0 10px rgba(56, 189, 248, 0.35);
}

.slider-bar-fill {
  position: absolute;
  top: 0;
  left: 0;
  height: 100%;
  border-radius: 10px;
  background: var(--accent);
  pointer-events: none;
  transition: width 130ms cubic-bezier(0.4, 0, 0.2, 1);
  overflow: hidden;
}

.slider-bar-fill.max-energy {
  background: linear-gradient(90deg, #1d4ed8, #2563eb, #0284c7, #38bdf8, #2563eb);
  background-size: 200% 100%;
  animation: max-bar-flow 2.4s linear infinite;
  box-shadow: 0 0 8px rgba(56, 189, 248, 0.5);
}

:global(:root[data-theme="dark"]) .slider-bar-fill.max-energy {
  background: linear-gradient(90deg, #0369a1, #0284c7, #38bdf8, #60a5fa, #0284c7);
  background-size: 200% 100%;
  box-shadow: 0 0 10px rgba(56, 189, 248, 0.6);
}

@keyframes max-bar-flow {
  from { background-position: 100% 0; }
  to { background-position: -100% 0; }
}

.max-particle-stream {
  position: absolute;
  inset: 0;
  overflow: hidden;
  border-radius: inherit;
  pointer-events: none;
}

.stream-line {
  position: absolute;
  border-radius: 999px;
  background: linear-gradient(90deg, transparent, rgba(125, 211, 252, 0.75) 40%, #ffffff);
  box-shadow: 0 0 5px rgba(56, 189, 248, 0.9), 0 0 2px #ffffff;
  animation: stream-rush linear infinite;
  opacity: 0;
}

.stream-spark {
  position: absolute;
  border-radius: 50%;
  background: #ffffff;
  box-shadow: 0 0 4px #38bdf8, 0 0 2px #ffffff;
  animation: stream-rush linear infinite;
  opacity: 0;
}

.stream-line.l-1 { top: 20%; height: 2px; width: 36px; animation-duration: 1.05s; animation-delay: 0s; }
.stream-line.l-2 { top: 42%; height: 1.5px; width: 24px; animation-duration: 0.85s; animation-delay: 0.22s; }
.stream-line.l-3 { top: 65%; height: 2px; width: 42px; animation-duration: 1.15s; animation-delay: 0.48s; }
.stream-line.l-4 { top: 82%; height: 1.5px; width: 28px; animation-duration: 0.95s; animation-delay: 0.12s; }
.stream-line.l-5 { top: 30%; height: 2px; width: 44px; animation-duration: 1.10s; animation-delay: 0.65s; }
.stream-line.l-6 { top: 55%; height: 1.5px; width: 32px; animation-duration: 0.88s; animation-delay: 0.35s; }

.stream-spark.s-1 { top: 36%; width: 3px; height: 3px; animation-duration: 0.98s; animation-delay: 0.16s; }
.stream-spark.s-2 { top: 62%; width: 2.5px; height: 2.5px; animation-duration: 0.82s; animation-delay: 0.42s; }
.stream-spark.s-3 { top: 22%; width: 3px; height: 3px; animation-duration: 0.92s; animation-delay: 0.60s; }
.stream-spark.s-4 { top: 76%; width: 2px; height: 2px; animation-duration: 1.08s; animation-delay: 0.06s; }

@keyframes stream-rush {
  0% { left: -48px; opacity: 0; }
  18% { opacity: 1; }
  82% { opacity: 0.95; }
  100% { left: 105%; opacity: 0; }
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
  background: color-mix(in srgb, var(--text) 25%, transparent);
  pointer-events: none;
  transition: background-color 130ms ease;
}

.slider-step-dot.active {
  background: rgba(255, 255, 255, 0.85);
}

/* White Circle Thumb */
.slider-bar-thumb {
  position: absolute;
  top: -2px;
  width: 24px;
  height: 24px;
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
  border-radius: var(--radius-control);
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease;
}

.model-back-btn:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.model-select-title {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-secondary);
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
  height: 34px;
  padding: 0 8px 0 28px;
  font-size: 13px;
  border-radius: var(--radius-control);
  border: 1px solid var(--border);
  background: var(--surface-subtle);
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
  overscroll-behavior: contain;
}

.model-option-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  width: 100%;
  min-height: 34px;
  padding: 7px 8px;
  border: none;
  background: transparent;
  color: var(--text);
  border-radius: var(--radius-control);
  cursor: pointer;
  text-align: left;
  transition: background-color 120ms ease;
}

.model-option-row:hover {
  background: var(--surface-hover);
}

.model-option-row.active {
  background: var(--surface-subtle);
}

.model-option-row.active:hover {
  background: var(--surface-hover);
}

.model-option-name {
  min-width: 0;
  font-size: 13px;
  font-weight: 400;
  overflow-wrap: anywhere;
  line-height: 1.4;
}

.model-option-check {
  color: var(--text);
  flex-shrink: 0;
}

.model-empty-hint {
  padding: 16px 8px;
  text-align: center;
  font-size: 12.5px;
  color: var(--text-muted);
}

.model-reasoning-trigger:focus-visible,
.intensity-model-link:focus-visible,
.model-back-btn:focus-visible,
.model-option-row:focus-visible,
.slider-bar-track:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.model-option-row:focus-visible {
  outline-offset: -2px;
}

@media (prefers-reduced-motion: reduce) {
  .slider-bar-fill.max-energy {
    animation: none;
  }

  .max-particle-stream {
    display: none;
  }

  .model-reasoning-trigger,
  .trigger-chevron,
  .intensity-model-link,
  .slider-bar-track,
  .slider-bar-fill,
  .slider-bar-thumb,
  .slider-step-dot,
  .model-back-btn,
  .model-option-row {
    transition: none;
  }
}
</style>
