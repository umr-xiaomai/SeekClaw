<script setup lang="ts">
import { ChevronDown, Gavel, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

const props = withDefaults(defineProps<{
  modelValue: string[] | undefined
  models: string[]
  disabled?: boolean
}>(), { disabled: false })

const emit = defineEmits<{
  'update:modelValue': [value: string[] | undefined]
}>()

const open = ref(false)
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const menuStyle = ref<Record<string, string>>({})
const draft = ref<string[]>([])

const selected = computed(() => props.modelValue ?? [])
const label = computed(() => {
  if (selected.value.length === 0) return '自动'
  if (selected.value.length <= 2) return selected.value.join('、')
  return `${selected.value.length} 个模型`
})

function positionMenu(): void {
  if (!trigger.value || !open.value) return
  const rect = trigger.value.getBoundingClientRect()
  const edge = 10
  const gap = 8
  const width = Math.min(360, window.innerWidth - edge * 2)
  const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))
  const placeAbove = window.innerHeight - rect.bottom < 320
  menuStyle.value = placeAbove
    ? { width: `${width}px`, left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px` }
    : { width: `${width}px`, left: `${left}px`, top: `${rect.bottom + gap}px` }
}

function toggle(): void {
  if (props.disabled) return
  if (open.value) hide()
  else {
    draft.value = [...selected.value]
    open.value = true
    void nextTick(() => { positionMenu(); menu.value?.querySelector<HTMLInputElement>('input')?.focus() })
  }
}

function hide(): void {
  open.value = false
}

function toggleModel(ref: string): void {
  draft.value = draft.value.includes(ref)
    ? draft.value.filter((item) => item !== ref)
    : [...draft.value, ref]
}

function apply(): void {
  emit('update:modelValue', draft.value.length > 0 ? [...draft.value] : undefined)
  hide()
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    hide()
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

watch(open, (value) => (value ? addListeners() : removeListeners()))
watch(() => props.disabled, (value) => { if (value) hide() })
onBeforeUnmount(removeListeners)
</script>

<template>
  <div ref="root" class="panel-models-control" :class="{ open, disabled }">
    <button
      ref="trigger"
      type="button"
      class="panel-models-trigger"
      :disabled="disabled"
      aria-haspopup="dialog"
      :aria-expanded="open"
      @click="toggle"
      @keydown="handleKeydown"
    >
      <Gavel :size="14" />
      <span class="panel-models-label">评审模型：{{ label }}</span>
      <ChevronDown :size="13" class="panel-models-chevron" :class="{ rotated: open }" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <section
          v-if="open"
          ref="menu"
          class="panel-models-popover"
          role="dialog"
          aria-label="选择评审团模型"
          :style="menuStyle"
          @keydown="handleKeydown"
        >
          <header class="panel-models-header">
            <div><strong>评审团模型</strong><small>任务完成后由这些模型对抗式审查结果</small></div>
            <button type="button" class="panel-models-close" title="收起" @click="hide"><X :size="15" /></button>
          </header>

          <label class="panel-models-option is-auto" :class="{ active: draft.length === 0 }">
            <input type="checkbox" :checked="draft.length === 0" @change="draft = []" />
            <span class="panel-models-check"><span /></span>
            <span class="panel-models-name">自动选择</span>
            <small>按路由链自动挑选不同厂商的模型</small>
          </label>

          <div class="panel-models-list">
            <label
              v-for="ref in models"
              :key="ref"
              class="panel-models-option"
              :class="{ active: draft.includes(ref) }"
            >
              <input type="checkbox" :checked="draft.includes(ref)" @change="toggleModel(ref)" />
              <span class="panel-models-check"><span /></span>
              <span class="panel-models-name">{{ ref }}</span>
            </label>
            <p v-if="models.length === 0" class="panel-models-empty">没有可用的模型，请先在「模型与 Provider」中配置。</p>
          </div>

          <footer class="panel-models-footer">
            <button type="button" class="secondary-button" @click="hide">取消</button>
            <button type="button" class="secondary-button primary-action" @click="apply">应用</button>
          </footer>
        </section>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.panel-models-trigger {
  display: inline-flex;
  height: 30px;
  align-items: center;
  gap: 6px;
  padding: 0 10px;
  color: var(--text-secondary);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.panel-models-trigger:hover:not(:disabled) {
  color: var(--text);
  border-color: var(--border-strong);
}

.panel-models-trigger:disabled {
  cursor: default;
  opacity: .5;
}

.panel-models-label {
  max-width: 180px;
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-models-chevron {
  flex: none;
  transition: transform 160ms ease;
}

.panel-models-chevron.rotated {
  transform: rotate(180deg);
}

.panel-models-popover {
  position: fixed;
  z-index: 200;
  display: grid;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--shadow);
}

.panel-models-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 13px 14px 10px;
}

.panel-models-header strong,
.panel-models-header small {
  display: block;
}

.panel-models-header strong {
  font-size: 13px;
}

.panel-models-header small {
  margin-top: 3px;
  color: var(--text-secondary);
  font-size: 11px;
}

.panel-models-close {
  display: grid;
  width: 24px;
  height: 24px;
  place-items: center;
  color: var(--text-muted);
  border-radius: 6px;
}

.panel-models-close:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.panel-models-option {
  display: flex;
  min-height: 36px;
  align-items: center;
  gap: 9px;
  padding: 6px 14px;
  cursor: pointer;
}

.panel-models-option:hover {
  background: var(--surface-hover);
}

.panel-models-option input {
  position: absolute;
  opacity: 0;
  pointer-events: none;
}

.panel-models-check {
  display: grid;
  width: 16px;
  height: 16px;
  flex: none;
  place-items: center;
  border: 1px solid var(--border-strong);
  border-radius: 5px;
}

.panel-models-option.active .panel-models-check {
  background: var(--accent);
  border-color: var(--accent);
}

.panel-models-check span {
  width: 8px;
  height: 4px;
  border-bottom: 2px solid #fff;
  border-left: 2px solid #fff;
  opacity: 0;
  transform: rotate(-45deg) translate(1px, -1px);
}

.panel-models-option.active .panel-models-check span {
  opacity: 1;
}

.panel-models-name {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-models-option small {
  color: var(--text-muted);
  font-size: 10px;
}

.panel-models-list {
  overflow-y: auto;
  max-height: 260px;
  padding-bottom: 6px;
  border-top: 1px solid var(--border);
}

.panel-models-option.is-auto {
  border-bottom: 1px solid var(--border);
}

.panel-models-empty {
  margin: 0;
  padding: 14px;
  color: var(--text-muted);
  font-size: 11px;
  text-align: center;
}

.panel-models-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 10px 14px;
  border-top: 1px solid var(--border);
}
</style>
