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

export interface ModelCapabilityConfig {
  thinking?: boolean
  reasoning?: boolean
  maxReasoningLevel?: string
}

export interface ModelDetailConfig {
  id: string
  alias?: string
  contextWindow: number
  maxOutput: number
  vision?: boolean
  contextWindowAuto?: boolean
  maxOutputAuto?: boolean
  detectedContextWindow?: number | null
  detectedMaxOutput?: number | null
  capabilities?: ModelCapabilityConfig
}

const reasoningLevels = [
  { value: 'low', label: '低 (Low)' },
  { value: 'medium', label: '中 (Medium)' },
  { value: 'high', label: '高 (High)' },
  { value: 'max', label: '最大 (Max)' },
  { value: 'xhigh', label: '极高 (X-High)' },
  { value: 'ultra', label: '超级 (Ultra)' }
]

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
  contextWindowAuto: true, maxOutputAuto: true
})

const firstInput = ref<HTMLInputElement | null>(null)
const reasoningMode = ref<'auto' | 'off' | 'level'>('auto')
const reasoningMax = ref('max')

function close(): void {
  emit('close')
}

function handleSave(): void {
  emit('save', {
    id: form.id,
    alias: form.alias?.trim() || undefined,
    contextWindow: Number(form.contextWindow) || 1000000,
    maxOutput: Number(form.maxOutput) || 128000,
    contextWindowAuto: form.contextWindowAuto, maxOutputAuto: form.maxOutputAuto,
    detectedContextWindow: form.detectedContextWindow, detectedMaxOutput: form.detectedMaxOutput,
    capabilities: reasoningMode.value === 'auto'
      ? undefined
      : reasoningMode.value === 'off'
        ? { thinking: false, reasoning: false, maxReasoningLevel: 'none' }
        : { thinking: true, reasoning: true, maxReasoningLevel: reasoningMax.value }
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
    form.contextWindowAuto = props.model.contextWindowAuto ?? false
    form.maxOutputAuto = props.model.maxOutputAuto ?? false
    form.detectedContextWindow = props.model.detectedContextWindow
    form.detectedMaxOutput = props.model.detectedMaxOutput

    const caps = props.model.capabilities
    if (caps && caps.maxReasoningLevel === 'none' && !caps.reasoning && !caps.thinking) {
      reasoningMode.value = 'off'
      reasoningMax.value = 'max'
    } else if (caps && (caps.reasoning || caps.thinking || caps.maxReasoningLevel)) {
      reasoningMode.value = 'level'
      reasoningMax.value = caps.maxReasoningLevel && caps.maxReasoningLevel !== 'none'
        ? caps.maxReasoningLevel
        : 'high'
    } else {
      reasoningMode.value = 'auto'
      reasoningMax.value = 'max'
    }
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
              <h2 id="model-config-title">配置模型参数</h2>
            </div>
            <button class="icon-button" type="button" title="关闭" aria-label="关闭" @click="close">
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
                  <span class="limit-mode"><input v-model="form.contextWindowAuto" type="checkbox"> 自动
                    <small>{{ form.detectedContextWindow ? '供应商：' + form.detectedContextWindow.toLocaleString() : '未提供参数，使用 1M' }}</small></span>
                  <ComboboxInput
                    v-if="!form.contextWindowAuto"
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
                  <span class="limit-mode"><input v-model="form.maxOutputAuto" type="checkbox"> 自动
                    <small>{{ form.detectedMaxOutput ? '供应商：' + form.detectedMaxOutput.toLocaleString() : '未提供参数，使用 128K' }}</small></span>
                  <ComboboxInput
                    v-if="!form.maxOutputAuto"
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
                <strong>思考能力</strong>
              </div>
              <div class="model-form-grid two-columns">
                <label class="form-field">
                  <FieldLabel
                    en="Reasoning Capability"
                    zh="推理支持"
                    help="自动：按供应商元数据与模型家族推断；不启用：始终不发送思考参数；声明档位：手动声明模型支持的最高档位。"
                  />
                  <select v-model="reasoningMode" class="form-input">
                    <option value="auto">自动推断</option>
                    <option value="off">不启用推理</option>
                    <option value="level">声明最高档位</option>
                  </select>
                </label>
                <label v-if="reasoningMode === 'level'" class="form-field">
                  <FieldLabel
                    en="Max Reasoning Level"
                    zh="最高思考档位"
                    help="运行时会把超出的档位自动收敛到该值，避免向供应商发送不支持的参数。"
                  />
                  <select v-model="reasoningMax" class="form-input">
                    <option v-for="level in reasoningLevels" :key="level.value" :value="level.value">
                      {{ level.label }}
                    </option>
                  </select>
                </label>
              </div>
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
  background: rgb(0 0 0 / 36%);
  backdrop-filter: blur(3px);
}

.model-config-dialog {
  display: flex;
  flex-direction: column;
  width: min(100%, 580px);
  max-height: min(760px, calc(100dvh - 40px));
  background: var(--modal-surface);
  border: 1px solid var(--border);
  border-radius: var(--radius-dialog, 16px);
  box-shadow: var(--dialog-shadow, var(--shadow));
  overflow: hidden;
}

.model-config-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  padding: 18px 24px;
  background: transparent;
}

.model-config-header h2 {
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  color: var(--text);
  letter-spacing: -0.01em;
}

.model-config-body {
  flex: 1;
  min-height: 0;
  padding: 4px 24px 20px;
  overflow-y: auto;
  overflow-x: hidden;
  display: flex;
  flex-direction: column;
  gap: 24px;
  background: transparent;
}

.model-form-section {
  display: flex;
  flex-direction: column;
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
  background: transparent;
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

.limit-mode {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
  min-height: 38px;
  font-size: 13px;
  cursor: pointer;
}

.limit-mode input {
  width: 15px;
  height: 15px;
  margin: 0;
  accent-color: var(--accent);
}

.limit-mode input:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
}

.limit-mode small {
  min-width: 0;
  color: var(--text-muted);
  font-size: 11px;
  overflow-wrap: anywhere;
}

.model-config-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 14px 24px 20px;
  flex-wrap: wrap;
  background: transparent;
}

.model-config-footer > span {
  color: var(--text-muted);
  font-size: 11.5px;
}

.footer-buttons {
  display: flex;
  gap: 8px;
}

@media (max-width: 560px) {
  .model-config-backdrop { padding: 12px; }
  .model-config-dialog { max-height: calc(100dvh - 24px); }
  .model-config-header { padding: 18px; }
  .model-config-body { padding: 0 18px 18px; }
  .model-config-footer { padding: 12px 18px 18px; }
  .model-form-grid.two-columns { grid-template-columns: minmax(0, 1fr); }
  .model-config-footer > span { flex-basis: 100%; }
  .footer-buttons { margin-left: auto; flex-wrap: wrap; justify-content: flex-end; }
}
</style>
