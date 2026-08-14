<script setup lang="ts">
import { Eye, EyeOff, Save, X } from '@lucide/vue'
import { nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import FieldLabel from './FieldLabel.vue'
import SelectMenu from './SelectMenu.vue'

interface ProviderFormValue {
  id: string
  name: string
  kind: 'openai' | 'anthropic'
  baseUrl: string
  modelListUrl: string
  apiKey: string
  models: string
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
const firstInput = ref<HTMLInputElement | null>(null)
const revealKey = ref(false)
const protocolOptions = [
  { value: 'openai', label: 'OpenAI 兼容', description: '兼容 OpenAI Chat Completions 接口' },
  { value: 'anthropic', label: 'Anthropic', description: '使用 Anthropic Messages API' }
]

function close(): void {
  if (!props.saving) emit('close')
}

function save(): void {
  if (!form.id.trim() || props.saving) return
  emit('save', { ...form, id: form.id.trim(), name: form.name.trim(), baseUrl: form.baseUrl.trim() })
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
        <form class="provider-editor-dialog" role="dialog" aria-modal="true" aria-labelledby="provider-editor-title" @submit.prevent="save">
          <header class="provider-editor-header">
            <div>
              <span class="provider-editor-eyebrow">模型提供商</span>
              <h2 id="provider-editor-title">{{ editingId ? '编辑模型提供商' : '新增模型提供商' }}</h2>
              <p>{{ editingId ? `编辑模型提供商 · ${editingId}` : '新增模型提供商 · 配置模型服务的连接和路由信息' }}</p>
            </div>
            <button class="icon-button" type="button" title="关闭" :disabled="saving" @click="close"><X :size="18" /></button>
          </header>

          <div class="provider-editor-body">
            <section class="provider-form-section">
              <div class="provider-section-heading">
                <strong>基本信息</strong>
              </div>
              <div class="provider-form-grid">
                <label>
                  <FieldLabel en="Provider ID" zh="提供商 ID" help="用于模型引用和配置文件的唯一标识，例如 openai。创建后不可修改。" required />
                  <input ref="firstInput" v-model="form.id" :disabled="!!editingId" placeholder="openai" autocomplete="off" />
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
                <label>
                  <FieldLabel en="API Key" zh="API 密钥" help="直接查看和修改此模型提供商保存的访问密钥；清空后保存会删除密钥。" />
                  <span class="password-control">
                    <input
                      v-model="form.apiKey"
                      :type="revealKey ? 'text' : 'password'"
                      placeholder="sk-…"
                      autocomplete="new-password"
                    />
                    <button type="button" :title="revealKey ? '隐藏 API Key' : '显示 API Key'" @click="revealKey = !revealKey">
                      <EyeOff v-if="revealKey" :size="16" /><Eye v-else :size="16" />
                    </button>
                  </span>
                </label>
                <label class="span-2">
                  <FieldLabel en="Models" zh="模型" help="此模型提供商可用的模型 ID。每行填写一个，也支持使用英文逗号分隔。" required />
                  <textarea v-model="form.models" rows="4" placeholder="gpt-5&#10;gpt-5-mini" spellcheck="false" />
                  <small>每行一个模型 ID</small>
                </label>
              </div>
            </section>

            <section class="provider-form-section">
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
            </section>

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
</template>
