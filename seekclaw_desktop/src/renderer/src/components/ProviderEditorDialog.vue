<script setup lang="ts">
import { Eye, EyeOff, Plus, Save, Settings2, Trash2, X } from '@lucide/vue'
import { nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import FieldLabel from './FieldLabel.vue'
import SelectMenu from './SelectMenu.vue'
import ModelConfigModal, { type ModelDetailConfig } from './ModelConfigModal.vue'
import { formatTokenCount } from '../app-helpers'

export interface ProviderFormValue {
  id: string
  name: string
  kind: 'openai' | 'anthropic'
  baseUrl: string
  modelListUrl: string
  apiKey: string
  models: string
  modelDetails?: ModelDetailConfig[]
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy: string
  promptCaching: boolean
}

const props = defineProps<{
  open: boolean
  editingId: string | null
  value: ProviderFormValue
  saving?: boolean
  error?: string
}>()

const emit = defineEmits<{
  close: []
  save: [value: ProviderFormValue]
}>()

const form = reactive<ProviderFormValue>({
  id: '', name: '', kind: 'openai', baseUrl: '', modelListUrl: '', apiKey: '', models: '',
  enabled: true, priority: 0, timeoutSeconds: 120, proxy: '', promptCaching: true
})
const modelList = ref<ModelDetailConfig[]>([])
const newModelInput = ref('')
const modelConfigModalOpen = ref(false)
const selectedModelConfig = ref<ModelDetailConfig | null>(null)
const firstInput = ref<HTMLInputElement | null>(null)
const revealKey = ref(false)
const protocolOptions = [
  { value: 'openai', label: 'OpenAI 兼容', description: '兼容 OpenAI Chat Completions 接口' },
  { value: 'anthropic', label: 'Anthropic', description: '使用 Anthropic Messages API' }
]

const showAdvanced = ref(false)

function hasCustomAdvancedSettings(val: ProviderFormValue): boolean {
  return (
    (val.timeoutSeconds !== undefined && val.timeoutSeconds !== 120) ||
    (val.priority !== undefined && val.priority !== 0) ||
    Boolean(val.proxy && val.proxy.trim() !== '') ||
    val.enabled === false ||
    val.promptCaching === false
  )
}

function resetAdvancedToDefaults(): void {
  form.timeoutSeconds = 120
  form.priority = 0
  form.proxy = ''
  form.enabled = true
  form.promptCaching = true
}

function onAdvancedToggle(): void {
  if (!showAdvanced.value) {
    resetAdvancedToDefaults()
  }
}

function syncModelsFromProps(): void {
  if (props.value.modelDetails && props.value.modelDetails.length > 0) {
    modelList.value = props.value.modelDetails.map((m) => ({ ...m }))
  } else if (props.value.models) {
    const ids = props.value.models
      .split(/[\n,]/)
      .map((s) => s.trim())
      .filter(Boolean)
    modelList.value = ids.map((id) => ({
      id,
      alias: undefined,
      contextWindow: 1000000,
      maxOutput: 128000,
      vision: false
    }))
  } else {
    modelList.value = []
  }
}

function addModel(): void {
  const raw = newModelInput.value.trim()
  if (!raw) return
  const ids = raw.split(/[\n,]/).map((s) => s.trim()).filter(Boolean)
  for (const id of ids) {
    if (!modelList.value.some((m) => m.id.toLowerCase() === id.toLowerCase())) {
      modelList.value.push({
        id,
        alias: undefined,
        contextWindow: 1000000,
        maxOutput: 128000,
        vision: false
      })
    }
  }
  newModelInput.value = ''
}

function removeModel(index: number): void {
  modelList.value.splice(index, 1)
}

function openModelConfig(model: ModelDetailConfig): void {
  selectedModelConfig.value = model
  modelConfigModalOpen.value = true
}

function handleModelConfigSave(updated: ModelDetailConfig): void {
  const index = modelList.value.findIndex((m) => m.id === updated.id)
  if (index !== -1) {
    modelList.value[index] = { ...updated }
  }
  modelConfigModalOpen.value = false
  selectedModelConfig.value = null
}

function close(): void {
  if (!props.saving) emit('close')
}

function save(): void {
  if (!form.id.trim() || props.saving) return
  if (!showAdvanced.value) {
    resetAdvancedToDefaults()
  }
  const currentModels = modelList.value.map((m) => m.id).filter(Boolean)
  emit('save', {
    ...form,
    id: form.id.trim(),
    name: form.name.trim(),
    baseUrl: form.baseUrl.trim(),
    models: currentModels.join('\n'),
    modelDetails: modelList.value.map((m) => ({ ...m })),
    timeoutSeconds: showAdvanced.value
      ? (Number.isFinite(form.timeoutSeconds) && form.timeoutSeconds >= 5 ? form.timeoutSeconds : 120)
      : 120,
    priority: showAdvanced.value
      ? (Number.isFinite(form.priority) ? form.priority : 0)
      : 0,
    proxy: showAdvanced.value ? form.proxy.trim() : '',
    enabled: showAdvanced.value ? form.enabled : true,
    promptCaching: showAdvanced.value ? form.promptCaching : true
  })
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') close()
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
    event.preventDefault()
    save()
  }
}

watch(() => props.open, (open) => {
  if (!open) return
  Object.assign(form, props.value)
  syncModelsFromProps()
  newModelInput.value = ''
  showAdvanced.value = Boolean(props.editingId && hasCustomAdvancedSettings(props.value))
  if (!showAdvanced.value) {
    resetAdvancedToDefaults()
  }
  revealKey.value = true
  document.addEventListener('keydown', handleKeydown)
  void nextTick(() => firstInput.value?.focus())
}, { immediate: true })

watch(() => props.open, (open, previous) => {
  if (!open && previous) document.removeEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div v-if="open" class="modal-backdrop provider-editor-backdrop" @mousedown.self="close">
        <form class="provider-editor-dialog" role="dialog" aria-modal="true" aria-labelledby="provider-editor-title"
          @submit.prevent="save">
          <header class="provider-editor-header">
            <div>
              <h2 id="provider-editor-title">{{ editingId ? '编辑模型提供商' : '新增模型提供商' }}</h2>
            </div>
            <button class="icon-button" type="button" title="关闭" :disabled="saving" @click="close">
              <X :size="18" />
            </button>
          </header>

          <div class="provider-editor-body">
            <section class="provider-form-section">
              <div class="provider-section-heading">
                <strong>基本信息</strong>
              </div>
              <div class="provider-form-grid">
                <label>
                  <FieldLabel en="Provider ID" zh="提供商 ID" help="用于模型引用和配置文件的唯一标识，例如 openai。创建后不可修改。" required />
                  <input ref="firstInput" v-model="form.id" :disabled="!!editingId" placeholder="openai"
                    autocomplete="off" />
                </label>
                <label>
                  <FieldLabel en="Display Name" zh="显示名称" help="仅用于界面展示；留空时会使用提供商 ID。" />
                  <input v-model="form.name" placeholder="OpenAI" autocomplete="off" />
                </label>
                <label>
                  <FieldLabel en="Protocol" zh="接口协议" help="选择服务端实际兼容的请求格式；协议与 API 地址必须匹配。" required />
                  <SelectMenu v-model="form.kind" label="接口协议" :options="protocolOptions" :menu-min-width="300" />
                </label>
                <label>
                  <FieldLabel en="Base URL" zh="API 地址" help="模型服务的 API 根地址。OpenAI 兼容服务通常以 /v1 结尾。" required />
                  <input v-model="form.baseUrl" placeholder="https://api.openai.com/v1" spellcheck="false" />
                </label>
                <label class="span-2">
                  <FieldLabel en="Model List URL" zh="模型列表 URL" help="获取模型目录的地址；留空时自动使用 API 地址下的 /models 接口。" />
                  <input v-model="form.modelListUrl" placeholder="留空自动推断 /models" spellcheck="false" />
                </label>
              </div>
            </section>

            <section class="provider-form-section">
              <div class="provider-section-heading">
                <strong>鉴权与模型</strong>
              </div>
              <div class="provider-form-grid">
                <label class="span-2">
                  <FieldLabel en="API Key" zh="API 密钥" help="直接查看和修改此模型提供商保存的访问密钥；清空后保存会删除密钥。" />
                  <span class="password-control">
                    <input v-model="form.apiKey" :type="revealKey ? 'text' : 'password'" placeholder="sk-…"
                      autocomplete="new-password" />
                    <button type="button" :title="revealKey ? '隐藏 API Key' : '显示 API Key'"
                      @click="revealKey = !revealKey">
                      <EyeOff v-if="revealKey" :size="16" />
                      <Eye v-else :size="16" />
                    </button>
                  </span>
                </label>
                <div class="span-2 provider-models-block">
                  <div class="provider-models-header">
                    <FieldLabel en="Models" zh="模型列表" help="此模型提供商可用的模型。可点击每行右侧设置按钮配置多模态/视觉支持与上下文参数。" required />
                    <small class="models-count-badge">{{ modelList.length }} 个模型</small>
                  </div>

                  <div class="model-add-bar">
                    <input v-model="newModelInput" class="model-add-input" placeholder="输入模型 ID，如 gpt-5，支持逗号或换行粘贴批量输入"
                      @keydown.enter.prevent="addModel" />
                    <button type="button" class="secondary-button compact-button add-model-btn"
                      :disabled="!newModelInput.trim()" @click="addModel">
                      <Plus :size="14" /> 添加模型
                    </button>
                  </div>

                  <div v-if="modelList.length === 0" class="models-empty-tip">
                    暂未添加模型，请在上方输入框添加模型 ID
                  </div>

                  <div v-else class="provider-models-table">
                    <div v-for="(model, idx) in modelList" :key="model.id" class="model-table-row">
                      <div class="model-row-left">
                        <span class="model-row-id">{{ model.id }}</span>
                        <span v-if="model.vision" class="model-badge vision-badge" title="支持多模态 / 视觉">
                          <Eye :size="11" /> 多模态
                        </span>
                        <span v-if="model.alias" class="model-badge alias-badge" :title="`别名: ${model.alias}`">
                          {{ model.alias }}
                        </span>
                        <span class="model-badge context-badge"
                          :title="`上下文窗口: ${(model.contextWindow || 1000000).toLocaleString()} Tokens`">
                          {{ formatTokenCount(model.contextWindow) }}
                        </span>
                      </div>
                      <div class="model-row-actions">
                        <button type="button" class="icon-button compact" title="配置模型参数与多模态"
                          @click="openModelConfig(model)">
                          <Settings2 :size="15" />
                        </button>
                        <button type="button" class="icon-button compact danger-icon" title="删除模型"
                          @click="removeModel(idx)">
                          <Trash2 :size="15" />
                        </button>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </section>

            <div class="provider-advanced-section">
              <label class="provider-advanced-check" style="margin-top: 10px;">
                <input v-model="showAdvanced" type="checkbox" @change="onAdvancedToggle" />
                <span>高级选项</span>
              </label>

              <div v-if="showAdvanced" class="provider-advanced-content">
                <div class="provider-section-heading">
                  <strong>请求与路由</strong>
                </div>
                <div class="provider-form-grid three-columns">
                  <label>
                    <FieldLabel en="Timeout" zh="超时（秒）" help="单次模型请求允许等待的最长时间；网络较慢或推理模型可适当调高。" />
                    <input v-model.number="form.timeoutSeconds" type="number" min="5" step="1" />
                  </label>
                  <label>
                    <FieldLabel en="Priority" zh="优先级" help="自动路由时的模型提供商顺序。数值越小优先级越高；相同数值按配置顺序选择。" />
                    <input v-model.number="form.priority" type="number" step="1" />
                  </label>
                  <label>
                    <FieldLabel en="Proxy" zh="代理地址" help="仅此模型提供商使用的 HTTP/HTTPS 代理；留空表示遵循运行时默认网络设置。" />
                    <input v-model="form.proxy" placeholder="http://127.0.0.1:7890" spellcheck="false" />
                  </label>
                </div>
                <label class="provider-enabled-row">
                  <span>
                    <strong>启用</strong>
                    <small>允许该模型提供商参与模型选择和自动路由</small>
                  </span>
                  <input v-model="form.enabled" class="sr-only" type="checkbox" />
                  <span class="toggle-switch" aria-hidden="true"><span /></span>
                </label>
                <label class="provider-enabled-row">
                  <span>
                    <strong>提示词缓存</strong>
                    <small>保持稳定前缀；Anthropic 会发送原生 cache_control 检查点</small>
                  </span>
                  <input v-model="form.promptCaching" class="sr-only" type="checkbox" />
                  <span class="toggle-switch" aria-hidden="true"><span /></span>
                </label>
              </div>
            </div>

            <div v-if="error" class="provider-editor-error">{{ error }}</div>
          </div>

          <footer class="provider-editor-footer">
            <span>按 Ctrl + Enter 保存</span>
            <div>
              <button class="secondary-button" type="button" :disabled="saving" @click="close">取消</button>
              <button class="secondary-button primary-action" type="submit" :disabled="saving || !form.id.trim()">
                <Save :size="15" /> {{ saving ? '正在保存…' : '保存模型提供商' }}
              </button>
            </div>
          </footer>
        </form>
      </div>
    </Transition>
  </Teleport>

  <ModelConfigModal :open="modelConfigModalOpen" :model="selectedModelConfig" @close="modelConfigModalOpen = false"
    @save="handleModelConfigSave" />
</template>
