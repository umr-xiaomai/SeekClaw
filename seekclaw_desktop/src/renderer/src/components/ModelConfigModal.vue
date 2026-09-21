<script setup lang="ts">
import { Save, X } from '@lucide/vue'
import { nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import ComboboxInput, { ComboboxOption } from './ComboboxInput.vue'
import FieldLabel from './FieldLabel.vue'

const tokenPresets: ComboboxOption[] = [
  { value: 128000, label: '128K', description: '128,000 Tokens' },
  { value: 256000, label: '256K', description: '256,000 Tokens' },
  { value: 384000, label: '384K', description: '384,000 Tokens' },
  { value: 512000, label: '512K', description: '512,000 Tokens' },
  { value: 1000000, label: '1M', description: '1,000,000 Tokens' },
  { value: 1500000, label: '1.5M', description: '1,500,000 Tokens' },
  { value: 2000000, label: '2M', description: '2,000,000 Tokens' }
]

export interface ModelDetailConfig {
  id: string
  alias?: string
  contextWindow: number
  maxOutput: number
  vision: boolean
}

const props = defineProps<{
  open: boolean
  model: ModelDetailConfig | null
}>()

const emit = defineEmits<{
  close: []
  save: [value: ModelDetailConfig]
}>()

const form = reactive<ModelDetailConfig>({
  id: '',
  alias: '',
  contextWindow: 1000000,
  maxOutput: 128000,
  vision: false
})

const firstInput = ref<HTMLInputElement | null>(null)

function close(): void {
  emit('close')
}

function handleSave(): void {
  emit('save', {
    id: form.id,
    alias: form.alias?.trim() || undefined,
    contextWindow: Number(form.contextWindow) || 1000000,
    maxOutput: Number(form.maxOutput) || 128000,
    vision: Boolean(form.vision)
  })
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') close()
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
    event.preventDefault()
    handleSave()
  }
}

watch(() => props.open, (open) => {
  if (!open) {
    document.removeEventListener('keydown', handleKeydown)
    return
  }
  if (props.model) {
    form.id = props.model.id
    form.alias = props.model.alias ?? ''
    form.contextWindow = props.model.contextWindow || 1000000
    form.maxOutput = props.model.maxOutput || 128000
    form.vision = Boolean(props.model.vision)
  }
  document.addEventListener('keydown', handleKeydown)
  void nextTick(() => firstInput.value?.focus())
}, { immediate: true })

onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div v-if="open && model" class="modal-backdrop model-config-backdrop" @mousedown.self="close">
        <form
          class="model-config-dialog"
          role="dialog"
          aria-modal="true"
          aria-labelledby="model-config-title"
          novalidate
          @submit.prevent="handleSave"
        >
          <header class="model-config-header">
            <div>
              <h2 id="model-config-title">配置模型能力与参数</h2>
            </div>
            <button class="icon-button" type="button" title="关闭" @click="close">
              <X :size="18" />
            </button>
          </header>

          <div class="model-config-body">
            <section class="model-form-section">
              <div class="model-section-heading">
                <strong>基本信息</strong>
              </div>
              <div class="model-form-grid two-columns">
                <label class="form-field">
                  <FieldLabel en="Model ID" zh="模型 ID" help="模型的原生唯一标识符，不可修改。" required />
                  <input
                    :value="form.id"
                    class="form-input"
                    disabled
                  />
                </label>
                <label class="form-field">
                  <FieldLabel en="Display Alias" zh="显示别名" help="在任务会话与模型切换器中展示的易读别名；留空则直接展示模型原生 ID。" />
                  <input
                    ref="firstInput"
                    v-model="form.alias"
                    class="form-input"
                    placeholder="可选，例如 快速模型 / Flash"
                    autocomplete="off"
                  />
                </label>
              </div>
            </section>

            <section class="model-form-section">
              <div class="model-section-heading">
                <strong>Token 与上下文参数</strong>
              </div>
              <div class="model-form-grid two-columns">
                <label class="form-field">
                  <FieldLabel
                    en="Context Window"
                    zh="上下文长度 (Tokens)"
                    help="单次会话支持的最大上下文 Token 总量。可直接输入数值，或点击右侧下拉箭头选择常用预设值。"
                    required
                  />
                  <ComboboxInput
                    v-model="form.contextWindow"
                    :options="tokenPresets"
                    placeholder="例如 1000000"
                  />
                </label>
                <label class="form-field">
                  <FieldLabel
                    en="Max Output"
                    zh="最大输出 (Tokens)"
                    help="模型单次响应允许输出的最大 Token 数量。可直接输入数值，或点击右侧下拉箭头选择常用预设值。"
                    required
                  />
                  <ComboboxInput
                    v-model="form.maxOutput"
                    :options="tokenPresets"
                    placeholder="例如 128000"
                  />
                </label>
              </div>
              <small class="model-context-hint">当会话估算 Tokens 接近该上下文长度时，运行时会自动压缩较早的历史消息。</small>
            </section>

            <section class="model-form-section">
              <div class="model-section-heading">
                <strong>多模态能力</strong>
              </div>
              <label class="model-enabled-row">
                <span>
                  <strong>视觉 / 多模态输入</strong>
                  <small>启用后，在对话中上传或粘贴图片时将优先调用该模型</small>
                </span>
                <input v-model="form.vision" class="sr-only" type="checkbox" />
                <span class="toggle-switch" aria-hidden="true"><span /></span>
              </label>
            </section>
          </div>

          <footer class="model-config-footer">
            <span>按 Ctrl + Enter 保存</span>
            <div class="footer-buttons">
              <button class="secondary-button" type="button" @click="close">取消</button>
              <button class="secondary-button primary-action" type="submit">
                <Save :size="15" /> 保存配置
              </button>
            </div>
          </footer>
        </form>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.model-config-backdrop {
  position: fixed;
  z-index: 210;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(8px);
}

.model-config-dialog {
  display: flex;
  flex-direction: column;
  width: min(100%, 580px);
  max-height: min(760px, calc(100vh - 40px));
  background: var(--surface-raised, #ffffff);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.28), 0 2px 10px rgba(0, 0, 0, 0.1);
  overflow: hidden;
}

.model-config-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  padding: 18px 24px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border);
}

.model-config-header h2 {
  margin: 0;
  font-size: 17px;
  font-weight: 650;
  color: var(--text);
  letter-spacing: -0.01em;
}

.model-config-body {
  flex: 1;
  padding: 20px 24px;
  overflow-y: auto;
  overflow-x: hidden;
  display: flex;
  flex-direction: column;
  gap: 18px;
  background: var(--surface-raised);
}

.model-form-section {
  display: flex;
  flex-direction: column;
  padding-bottom: 18px;
  border-bottom: 1px solid var(--border);
}

.model-form-section:last-of-type {
  padding-bottom: 0;
  border-bottom: none;
}

.model-section-heading {
  display: flex;
  align-items: baseline;
  margin-bottom: 12px;
}

.model-section-heading strong {
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.model-form-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 14px 16px;
  width: 100%;
}

.model-form-grid.two-columns {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.form-field {
  display: flex;
  flex-direction: column;
  gap: 7px;
  min-width: 0;
  width: 100%;
}

.form-field.full-width {
  grid-column: 1 / -1;
}

.form-input {
  width: 100%;
  box-sizing: border-box;
  min-height: 38px;
  padding: 8px 12px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  font-size: 13px;
  font-family: inherit;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.form-input:hover {
  border-color: color-mix(in srgb, var(--text-muted) 58%, var(--border));
}

.form-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.form-input:disabled {
  color: var(--text-muted);
  background: var(--surface-hover);
  cursor: not-allowed;
  border-color: var(--border);
}

.model-context-hint {
  display: block;
  margin-top: 8px;
  color: var(--text-muted);
  font-size: 11.5px;
  line-height: 1.5;
}

.model-enabled-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  padding: 12px 14px;
  background: color-mix(in srgb, var(--surface-hover) 65%, transparent);
  border-radius: 9px;
  cursor: pointer;
  border: 1px solid var(--border);
  transition: background-color 140ms ease;
}

.model-enabled-row:hover {
  background: var(--surface-hover);
}

.model-enabled-row > span:first-child {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 3px;
}

.model-enabled-row strong {
  font-size: 13px;
  color: var(--text);
}

.model-enabled-row small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  white-space: nowrap;
  border: 0;
  clip: rect(0, 0, 0, 0);
}

.toggle-switch {
  display: flex;
  width: 38px;
  height: 22px;
  flex: 0 0 auto;
  align-items: center;
  padding: 2px;
  background: var(--border-strong);
  border-radius: 999px;
  transition: background-color 160ms ease;
}

.toggle-switch > span {
  width: 18px;
  height: 18px;
  background: white;
  border-radius: 50%;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.2);
  transition: transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

.model-enabled-row input:checked + .toggle-switch {
  background: var(--accent);
}

.model-enabled-row input:checked + .toggle-switch > span {
  transform: translateX(16px);
}

.model-config-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 13px 24px;
  background: color-mix(in srgb, var(--sidebar) 72%, var(--surface-raised));
  border-top: 1px solid var(--border);
}

.model-config-footer > span {
  color: var(--text-muted);
  font-size: 11.5px;
}

.footer-buttons {
  display: flex;
  gap: 8px;
}
</style>
