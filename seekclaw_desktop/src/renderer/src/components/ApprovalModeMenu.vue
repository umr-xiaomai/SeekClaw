<script setup lang="ts">
import { AlertTriangle, Check, ChevronDown, Hand, ShieldCheck } from '@lucide/vue'
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
    description: '可不受限制地访问互联网和你电脑上的任何文件',
    icon: AlertTriangle
  }
]

const defaultOption: ModeOption = options[1]!

const currentOption = computed<ModeOption>(() =>
  options.find((opt) => opt.id === normalizedMode.value) ?? defaultOption
)

function positionMenu(): void {
  if (!trigger.value || !open.value) return
  const rect = trigger.value.getBoundingClientRect()
  const edge = 10
  const gap = 8
  const width = Math.min(360, window.innerWidth - edge * 2)
  const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))
  const placeAbove = window.innerHeight - rect.bottom < 260
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
    <button
      ref="trigger"
      type="button"
      class="composer-approval-pill"
      :class="[
        `mode-${normalizedMode}`,
        { open, disabled }
      ]"
      :disabled="disabled"
      aria-haspopup="dialog"
      :aria-expanded="open"
      :title="`操作批准模式：${currentOption.title} - ${currentOption.description}`"
      @click="toggle"
      @keydown="handleKeydown"
    >
      <component :is="currentOption.icon" :size="14" class="pill-icon" />
      <span class="pill-label">{{ currentOption.shortLabel }}</span>
      <ChevronDown :size="12" class="pill-chevron" :class="{ rotated: open }" />
    </button>

    <Teleport to="body">
      <Transition name="select-popover">
        <section
          v-if="open"
          ref="menu"
          class="approval-popover-card"
          role="dialog"
          aria-label="选择操作批准模式"
          :style="menuStyle"
          @keydown="handleKeydown"
        >
          <header class="approval-popover-header">
            <span class="approval-header-title">应如何批准 SeekClaw 操作？</span>
            <a
              href="https://seekclaw.hoilai.com/doc/daemon/#agent-mode"
              target="_blank"
              rel="noopener noreferrer"
              class="approval-learn-more"
              title="查看操作批准模式说明"
            >
              了解更多
            </a>
          </header>

          <div class="approval-options-list" role="radiogroup" aria-label="批准模式选项">
            <button
              v-for="opt in options"
              :key="opt.id"
              type="button"
              class="approval-option-item"
              :class="[
                `item-${opt.id}`,
                { active: normalizedMode === opt.id }
              ]"
              role="radio"
              :aria-checked="normalizedMode === opt.id"
              @click="selectMode(opt.id)"
            >
              <div class="option-icon-box">
                <component :is="opt.icon" :size="18" />
              </div>
              <div class="option-text-box">
                <div class="option-title">{{ opt.title }}</div>
                <div class="option-desc">{{ opt.description }}</div>
              </div>
              <div class="option-check-wrap">
                <Check v-if="normalizedMode === opt.id" :size="16" class="option-check-icon" />
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
}

.composer-approval-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  font-size: 12px;
  font-weight: 500;
  border-radius: 14px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
  cursor: pointer;
  user-select: none;
  transition: all 140ms ease;
  white-space: nowrap;
}

.composer-approval-pill:hover:not(:disabled) {
  background: var(--surface-hover);
  border-color: color-mix(in srgb, var(--accent) 40%, var(--border));
}

.composer-approval-pill:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

/* Full Access Warning Orange Accent */
.composer-approval-pill.mode-full {
  background: color-mix(in srgb, #f97316 11%, transparent);
  border-color: color-mix(in srgb, #f97316 38%, var(--border));
  color: #ea580c;
}

:root[data-theme="dark"] .composer-approval-pill.mode-full {
  background: color-mix(in srgb, #ea580c 18%, transparent);
  border-color: color-mix(in srgb, #ea580c 45%, var(--border));
  color: #fb923c;
}

.composer-approval-pill.mode-full:hover:not(:disabled) {
  background: color-mix(in srgb, #f97316 18%, transparent);
  border-color: #ea580c;
}

.pill-icon {
  flex-shrink: 0;
}

.pill-label {
  font-size: 12px;
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
  padding: 16px 14px 12px;
  border-radius: 18px;
  border: 1px solid var(--border);
  background: var(--surface);
  box-shadow: 0 16px 42px rgba(0, 0, 0, 0.16), 0 3px 12px rgba(0, 0, 0, 0.08);
}

:root[data-theme="dark"] .approval-popover-card {
  background: var(--surface-raised);
  box-shadow: 0 20px 48px rgba(0, 0, 0, 0.45), 0 4px 16px rgba(0, 0, 0, 0.25);
}

.approval-popover-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
  padding: 0 6px 12px;
  border-bottom: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
}

.approval-header-title {
  font-size: 13.5px;
  font-weight: 500;
  color: var(--text-muted);
  user-select: none;
}

.approval-learn-more {
  font-size: 12.5px;
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
  gap: 3px;
  margin-top: 8px;
}

.approval-option-item {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  width: 100%;
  padding: 10px 10px;
  border-radius: 12px;
  border: 1px solid transparent;
  background: transparent;
  text-align: left;
  cursor: pointer;
  transition: background-color 130ms ease, border-color 130ms ease;
}

.approval-option-item:hover {
  background: var(--surface-hover);
}

.approval-option-item:focus-visible {
  outline: none;
  background: var(--surface-hover);
  border-color: var(--accent);
}

.option-icon-box {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  margin-top: 1px;
  color: var(--text-secondary);
}

.option-text-box {
  flex: 1 1 auto;
  min-width: 0;
}

.option-title {
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text);
  line-height: 1.3;
}

.option-desc {
  font-size: 12px;
  color: var(--text-muted);
  line-height: 1.45;
  margin-top: 3px;
}

.option-check-wrap {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  align-self: center;
  color: var(--accent);
}

/* Full Access Highlight Colors (as shown in Codex screenshot) */
.approval-option-item.item-full.active .option-title,
.approval-option-item.item-full.active .option-desc,
.approval-option-item.item-full.active .option-icon-box,
.approval-option-item.item-full.active .option-check-wrap {
  color: #ea580c;
}

:root[data-theme="dark"] .approval-option-item.item-full.active .option-title,
:root[data-theme="dark"] .approval-option-item.item-full.active .option-desc,
:root[data-theme="dark"] .approval-option-item.item-full.active .option-icon-box,
:root[data-theme="dark"] .approval-option-item.item-full.active .option-check-wrap {
  color: #fb923c;
}

.approval-option-item.item-full.active {
  background: color-mix(in srgb, #f97316 7%, transparent);
}

:root[data-theme="dark"] .approval-option-item.item-full.active {
  background: color-mix(in srgb, #ea580c 14%, transparent);
}
</style>
