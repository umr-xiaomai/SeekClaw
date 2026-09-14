<script setup lang="ts">
import { Save, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import {
  MCP_SCOPE_OPTIONS,
  MCP_TRANSPORT_OPTIONS,
  buildMcpServerPayload,
  createMcpFormValue,
  isRemoteTransport,
  mcpFormError,
  mcpFormFromServer,
  transportLabel,
  type McpFormValue,
  type McpScope,
  type McpServerSummary
} from '../mcp-form'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  open: boolean
  /** null opens an empty form; a server opens the editor for that entry. */
  server: McpServerSummary | null
  saving?: boolean
  /** Error reported by the runtime for the last save attempt. */
  error?: string
}>()

const emit = defineEmits<{
  close: []
  save: [payload: { name: string; scope: McpScope; server: Record<string, unknown> }]
}>()

const form = reactive<McpFormValue>(createMcpFormValue())
const validationError = ref('')
const firstInput = ref<HTMLInputElement | null>(null)

const editing = computed(() => props.server !== null)
const remote = computed(() => isRemoteTransport(form.transport))
const message = computed(() => validationError.value || props.error || '')

function close(): void {
  if (!props.saving) emit('close')
}

function save(): void {
  if (props.saving) return

  const invalid = mcpFormError(form)
  if (invalid) {
    validationError.value = invalid
    return
  }

  validationError.value = ''
  emit('save', {
    name: form.name.trim(),
    scope: form.scope,
    server: buildMcpServerPayload(form)
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
  Object.assign(form, props.server ? mcpFormFromServer(props.server) : createMcpFormValue())
  validationError.value = ''
  document.addEventListener('keydown', handleKeydown)
  void nextTick(() => firstInput.value?.focus())
}, { immediate: true })

watch(() => props.open, (open, previous) => {
  if (!open && previous) document.removeEventListener('keydown', handleKeydown)
})

// Editing any field invalidates the previous validation message.
watch(form, () => {
  if (validationError.value) validationError.value = ''
})

onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div v-if="open" class="modal-backdrop mcp-editor-backdrop" @mousedown.self="close">
        <form class="mcp-editor-dialog" role="dialog" aria-modal="true" aria-labelledby="mcp-editor-title" @submit.prevent="save">
          <header class="mcp-editor-header">
            <div>
              <h2 id="mcp-editor-title">{{ editing ? '编辑 MCP 服务器' : '新增 MCP 服务器' }}</h2>
              <p>{{ editing ? form.name : '配置一个 MCP Server 并重新加载工具' }}</p>
            </div>
            <button class="icon-button" type="button" title="关闭" :disabled="saving" @click="close">
              <X :size="18" />
            </button>
          </header>

          <div class="mcp-editor-body">
            <section class="mcp-form-section">
              <div class="mcp-form-grid">
                <label class="form-field">
                  <span>名称</span>
                  <input
                    ref="firstInput"
                    v-model="form.name"
                    class="form-input"
                    placeholder="filesystem"
                    :disabled="editing"
                    autocomplete="off"
                  />
                </label>
                <label class="form-field">
                  <span>范围</span>
                  <SelectMenu v-model="form.scope" :options="MCP_SCOPE_OPTIONS" label="MCP 范围" />
                </label>
              </div>
            </section>

            <section class="mcp-form-section">
              <div class="mcp-section-heading">
                <strong>连接方式</strong>
                <small>{{ transportLabel(form.transport) }}</small>
              </div>
              <div class="mcp-form-grid">
                <label class="form-field full-width">
                  <SelectMenu v-model="form.transport" :options="MCP_TRANSPORT_OPTIONS" label="MCP 连接方式" />
                </label>

                <template v-if="!remote">
                  <label class="form-field full-width">
                    <span>命令</span>
                    <input v-model="form.command" class="form-input" placeholder="npx" autocomplete="off" />
                  </label>
                  <label class="form-field full-width">
                    <span>参数</span>
                    <textarea v-model="form.args" class="form-input" rows="3" placeholder="-y&#10;@modelcontextprotocol/server-filesystem"></textarea>
                  </label>
                  <label class="form-field full-width">
                    <span>环境变量</span>
                    <textarea v-model="form.env" class="form-input" rows="2" placeholder="TOKEN=..."></textarea>
                  </label>
                </template>

                <label v-else class="form-field full-width">
                  <span>URL</span>
                  <input
                    v-model="form.url"
                    class="form-input"
                    :placeholder="form.transport === 'sse' ? 'https://example.com/sse' : 'https://example.com/mcp'"
                    autocomplete="off"
                  />
                </label>
              </div>
            </section>

            <section class="mcp-form-section">
              <label class="mcp-enabled-row">
                <span>
                  <strong>启用</strong>
                  <small>保存后立即连接并注册该 Server 的工具与 Prompt</small>
                </span>
                <input v-model="form.enabled" class="sr-only" type="checkbox" />
                <span class="toggle-switch" aria-hidden="true"><span /></span>
              </label>
            </section>

            <p v-if="message" class="mcp-editor-error">{{ message }}</p>
          </div>

          <footer class="mcp-editor-footer">
            <span>按 Ctrl + Enter 保存</span>
            <div>
              <button class="secondary-button" type="button" :disabled="saving" @click="close">取消</button>
              <button class="secondary-button primary-action" type="submit" :disabled="saving">
                <Save :size="15" /> {{ saving ? '正在连接…' : '保存并重载' }}
              </button>
            </div>
          </footer>
        </form>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.mcp-editor-backdrop {
  position: fixed;
  z-index: 130;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(8px);
}

.mcp-editor-dialog {
  display: flex;
  flex-direction: column;
  width: min(100%, 620px);
  max-height: min(760px, calc(100vh - 40px));
  overflow: hidden;
  background: var(--surface-raised, #ffffff);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.28), 0 2px 10px rgba(0, 0, 0, 0.1);
}

.mcp-editor-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  padding: 20px 24px 16px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border);
}

.mcp-editor-header h2 {
  margin: 0;
  font-size: 18px;
  font-weight: 650;
  color: var(--text);
  letter-spacing: -0.01em;
}

.mcp-editor-header p {
  margin: 4px 0 0;
  color: var(--text-muted);
  font-size: 12px;
  font-family: var(--font-mono, monospace);
}

.mcp-editor-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 18px;
  padding: 20px 24px;
  overflow-x: hidden;
  overflow-y: auto;
  background: var(--surface-raised);
}

.mcp-form-section {
  display: flex;
  flex-direction: column;
  padding-bottom: 18px;
  border-bottom: 1px solid var(--border);
}

.mcp-form-section:last-of-type {
  padding-bottom: 0;
  border-bottom: none;
}

.mcp-section-heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.mcp-section-heading strong {
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.mcp-section-heading small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px 16px;
  width: 100%;
}

.form-field {
  display: flex;
  min-width: 0;
  width: 100%;
  flex-direction: column;
  gap: 7px;
}

.form-field.full-width {
  grid-column: 1 / -1;
}

.form-field > span {
  color: var(--text-muted);
  font-size: 12px;
}

.form-input {
  width: 100%;
  box-sizing: border-box;
  min-height: 38px;
  padding: 8px 12px;
  color: var(--text);
  font-size: 13px;
  font-family: inherit;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

textarea.form-input {
  resize: vertical;
  line-height: 1.5;
}

.form-input:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--text-muted) 58%, var(--border));
}

.form-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.form-input:disabled {
  opacity: .62;
}

.mcp-enabled-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  padding: 12px 14px;
  cursor: pointer;
  background: color-mix(in srgb, var(--surface-hover) 65%, transparent);
  border: 1px solid var(--border);
  border-radius: 9px;
  transition: background-color 140ms ease;
}

.mcp-enabled-row:hover {
  background: var(--surface-hover);
}

.mcp-enabled-row > span:first-child {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 3px;
}

.mcp-enabled-row strong {
  font-size: 13px;
  color: var(--text);
}

.mcp-enabled-row small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-editor-error {
  margin: 0;
  padding: 9px 12px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.mcp-editor-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 13px 24px;
  background: color-mix(in srgb, var(--sidebar) 72%, var(--surface-raised));
  border-top: 1px solid var(--border);
}

.mcp-editor-footer > span {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-editor-footer > div {
  display: flex;
  gap: 8px;
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

.mcp-enabled-row input:checked + .toggle-switch {
  background: var(--accent);
}

.mcp-enabled-row input:checked + .toggle-switch > span {
  transform: translateX(16px);
}
</style>
