<script setup lang="ts">
import { Check, Hand, ShieldAlert, ShieldCheck } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

export type ApprovalMode = 'manual' | 'guardrail' | 'full'

const props = withDefaults(defineProps<{
  modelValue: string
  disabled?: boolean
}>(), { disabled: false })

const emit = defineEmits<{
  'update:modelValue': [value: ApprovalMode]
}>()

const open = ref(false)
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const menuStyle = ref<Record<string, string>>({})

const normalizedMode = computed<ApprovalMode>(() => {
  const m = (props.modelValue || '').trim().toLowerCase()
  if (m === 'manual' || m === 'ask' || m === 'readonly' || m === 'plan') return 'manual'
  if (m === 'full' || m === 'full_access' || m === 'full-access' || m === 'auto') return 'full'
  return 'guardrail'
})

interface ModeOption {
  id: ApprovalMode
  title: string
  shortLabel: string
  description: string
  icon: typeof Hand
}

const options: ModeOption[] = [
  {
    id: 'manual',
    title: '请求批准',
    shortLabel: '请求批准',
    description: '编辑外部文件和使用互联网时始终询问',
    icon: Hand
  },
  {
    id: 'guardrail',
    title: '帮我批准',
    shortLabel: '帮我批准',
    description: '仅对检测到的风险操作请求批准',
    icon: ShieldCheck
  },
  {
    id: 'full',
    title: '完全访问权限',
    shortLabel: '完全访问',
    description: '不受限制地访问网络和电脑上的任何文件',
    icon: ShieldAlert
  }
]

const defaultOption: ModeOption = options[1]!

const currentOption = computed<ModeOption>(() =>
  options.find((opt) => opt.id === normalizedMode.value) ?? defaultOption
)

const placedAbove = ref(true)

function positionMenu(): void {
  if (!trigger.value || !open.value) return
  const rect = trigger.value.getBoundingClientRect()
  const edge = 10
  const gap = 8
  const width = Math.min(290, window.innerWidth - edge * 2)
  const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))
  const placeAbove = window.innerHeight - rect.bottom < 260
  placedAbove.value = placeAbove
  menuStyle.value = placeAbove
    ? { width: `${width}px`, left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px` }
    : { width: `${width}px`, left: `${left}px`, top: `${rect.bottom + gap}px` }
}

function show(): void {
  if (props.disabled) return
  open.value = true
  void nextTick(() => {
    positionMenu()
    menu.value?.querySelector<HTMLButtonElement>('.approval-option-item.active')?.focus({ preventScroll: true })
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

function selectMode(id: ApprovalMode): void {
  emit('update:modelValue', id)
  hide(true)
}

function handleOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node | null
  if (!target) return
  if (trigger.value?.contains(target)) return
  if (menu.value?.contains(target)) return
  hide()
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) {
    event.preventDefault()
    event.stopPropagation()
    hide(true)
    return
  }
  if (!open.value) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      show()
    }
    return
  }
  const items = Array.from(menu.value?.querySelectorAll<HTMLButtonElement>('.approval-option-item') ?? [])
  if (items.length === 0) return
  const currentIndex = items.findIndex((el) => el === document.activeElement)
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    items[(currentIndex + 1 + items.length) % items.length]?.focus()
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    items[(currentIndex - 1 + items.length) % items.length]?.focus()
  } else if (event.key === 'Home') {
    event.preventDefault()
    items[0]?.focus()
  } else if (event.key === 'End') {
    event.preventDefault()
    items[items.length - 1]?.focus()
  }
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

watch(open, (isOpen) => isOpen ? addListeners() : removeListeners())
watch(() => props.disabled, (disabled) => { if (disabled) hide() })
onBeforeUnmount(removeListeners)
</script>

<template>
  <div class="composer-approval-wrap">
    <button ref="trigger" type="button" class="composer-approval-pill" :class="[
      `mode-${normalizedMode}`,
      { open, disabled }
    ]" :disabled="disabled" aria-haspopup="dialog" :aria-expanded="open"
      :title="`操作批准模式：${currentOption.title} - ${currentOption.description}`" @click="toggle" @keydown="handleKeydown">
      <component :is="currentOption.icon" :size="15" class="pill-icon" />
      <span class="pill-label">{{ currentOption.shortLabel }}</span>
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <section v-if="open" ref="menu" class="approval-popover-card" :data-placement="placedAbove ? 'above' : 'below'" role="dialog" aria-label="选择操作批准模式"
          :style="menuStyle" @keydown="handleKeydown">
          <header class="approval-popover-header">
            <span class="approval-header-title">应如何批准 SeekClaw 操作？</span>
            <a href="https://seekclaw.hoilai.com/doc/daemon/#agent-mode" target="_blank" rel="noopener noreferrer"
              class="approval-learn-more" title="查看操作批准模式说明">
              了解更多
            </a>
          </header>

          <div class="approval-options-list" role="radiogroup" aria-label="批准模式选项">
            <button v-for="opt in options" :key="opt.id" type="button" class="approval-option-item" :class="[
              `item-${opt.id}`,
              { active: normalizedMode === opt.id }
            ]" role="radio" :aria-checked="normalizedMode === opt.id" @click="selectMode(opt.id)">
              <div class="option-icon-box">
                <component :is="opt.icon" :size="15" />
              </div>
              <div class="option-text-box">
                <div class="option-title">{{ opt.title }}</div>
                <div class="option-desc">{{ opt.description }}</div>
              </div>
              <div class="option-check-wrap">
                <Check v-if="normalizedMode === opt.id" :size="14" class="option-check-icon" />
              </div>
            </button>
          </div>
        </section>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.composer-approval-wrap {
  position: relative;
  display: inline-flex;
  align-items: center;
  min-width: 0;
}

.composer-approval-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 8px;
  font-size: 13px;
  font-weight: 500;
  border-radius: var(--radius-control);
  border: none;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  user-select: none;
  transition: background-color 140ms ease, color 140ms ease;
  white-space: nowrap;
}

.composer-approval-pill:hover:not(:disabled),
.composer-approval-pill.open {
  background: var(--surface-hover);
  color: var(--text);
}

.composer-approval-pill:focus-visible,
.approval-learn-more:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.composer-approval-pill:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

/* Full Access Warning Orange Accent (as shown in Codex screenshot) */
.composer-approval-pill.mode-full {
  background: transparent;
  color: light-dark(#c45116, #fb923c);
}

.composer-approval-pill.mode-full:hover:not(:disabled) {
  background: color-mix(in srgb, #f97316 12%, transparent);
}

.pill-icon {
  flex-shrink: 0;
}

.pill-label {
  font-size: 13px;
  line-height: 1;
}

.pill-chevron {
  opacity: 0.6;
  transition: transform 140ms ease;
}

.pill-chevron.rotated {
  transform: rotate(180deg);
}

/* Popover Card (Codex Style) */
.approval-popover-card {
  position: fixed;
  z-index: 1000;
  box-sizing: border-box;
  padding: 6px;
  border-radius: var(--radius-popover);
  border: 1px solid var(--border);
  background: var(--surface-raised);
  box-shadow: var(--popover-shadow);
}

.approval-popover-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 10px;
  padding: 7px 8px 9px;
  border-bottom: 1px solid var(--border);
}

.approval-header-title {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-muted);
  user-select: none;
}

.approval-learn-more {
  font-size: 11.5px;
  color: var(--text-muted);
  text-decoration: underline;
  text-underline-offset: 3px;
  transition: color 120ms ease;
  white-space: nowrap;
}

.approval-learn-more:hover {
  color: var(--accent);
}

.approval-options-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin-top: 5px;
}

.approval-option-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  width: 100%;
  padding: 9px 8px;
  border-radius: var(--radius-control);
  border: 1px solid transparent;
  background: transparent;
  text-align: left;
  cursor: pointer;
  transition: background-color 130ms ease, border-color 130ms ease;
}

.approval-option-item:hover {
  background: var(--surface-hover);
}

.approval-option-item.active {
  background: var(--surface-subtle);
}

.approval-option-item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
  background: var(--surface-hover);
}

.option-icon-box {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  margin-top: 1px;
  color: var(--text-secondary);
}

.option-text-box {
  flex: 1 1 auto;
  min-width: 0;
}

.option-title {
  font-size: 13px;
  font-weight: 500;
  color: var(--text);
  line-height: 1.4;
}

.option-desc {
  font-size: 12px;
  color: var(--text-muted);
  line-height: 1.5;
  margin-top: 2px;
}

.option-check-wrap {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  align-self: center;
  color: var(--text);
}

/* Full Access Highlight Colors (as shown in Codex screenshot) */
.approval-option-item.item-full.active .option-title,
.approval-option-item.item-full.active .option-icon-box,
.approval-option-item.item-full.active .option-check-wrap {
  color: light-dark(#c45116, #fb923c);
}

.approval-option-item.item-full.active {
  background: color-mix(in srgb, #f97316 6%, var(--surface-raised));
}

@media (prefers-reduced-motion: reduce) {
  .composer-approval-pill,
  .approval-option-item,
  .approval-learn-more {
    transition: none;
  }
}
</style>
