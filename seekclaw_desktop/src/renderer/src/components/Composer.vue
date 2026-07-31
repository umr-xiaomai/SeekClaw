<script setup lang="ts">
import { ArrowUp, FolderPlus, Square } from '@lucide/vue'
import { nextTick, ref, watch } from 'vue'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  busy: boolean
  disabled?: boolean
  model: string
  models: string[]
  mode: string
}>()

const emit = defineEmits<{
  send: [message: string]
  stop: []
  attach: []
  changeModel: [model: string]
  changeMode: [mode: string]
}>()

const value = ref('')
const textarea = ref<HTMLTextAreaElement | null>(null)
const modeOptions = [
  { value: 'edit', label: 'Edit', description: '可读取并修改文件' },
  { value: 'plan', label: 'Plan', description: '先分析并制定计划' },
  { value: 'readonly', label: 'Read', description: '仅分析，不修改文件' },
  { value: 'auto', label: 'Auto', description: '根据任务自动选择' }
]

function resize(): void {
  if (!textarea.value) return
  textarea.value.style.height = '0'
  textarea.value.style.height = `${Math.min(176, Math.max(30, textarea.value.scrollHeight))}px`
}

function submit(): void {
  const message = value.value.trim()
  if (!message || props.busy || props.disabled) return
  emit('send', message)
  value.value = ''
  void nextTick(resize)
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  submit()
}

function focus(): void {
  textarea.value?.focus()
}

function setValue(nextValue: string): void {
  value.value = nextValue
  void nextTick(() => {
    resize()
    focus()
  })
}

defineExpose({ focus, setValue })
watch(value, resize)
</script>

<template>
  <div class="composer-shell">
    <textarea
      ref="textarea"
      v-model="value"
      :disabled="disabled"
      rows="1"
      :placeholder="disabled ? '恢复任务后可继续对话' : '交给 SeekClaw'"
      aria-label="消息"
      @keydown="handleKeydown"
    />
    <div class="composer-toolbar">
      <button class="icon-button composer-icon" title="添加工作区" @click="emit('attach')">
        <FolderPlus :size="18" />
      </button>
      <SelectMenu
        class="composer-select mode-control"
        :model-value="mode"
        :options="modeOptions"
        label="Agent 模式"
        :disabled="busy || disabled"
        :menu-min-width="220"
        @update:model-value="emit('changeMode', $event)"
      />
      <SelectMenu
        class="composer-select model-control"
        :model-value="model"
        :options="models.length > 0 ? models.map((item) => ({ value: item, label: item })) : [{ value: model, label: model }]"
        label="模型"
        :disabled="busy || disabled"
        :menu-min-width="300"
        @update:model-value="emit('changeModel', $event)"
      />
      <span class="toolbar-spacer" />
      <button v-if="busy" class="send-button" title="停止" @click="emit('stop')">
        <Square :size="14" fill="currentColor" />
      </button>
      <button v-else class="send-button" title="发送" :disabled="disabled || !value.trim()" @click="submit">
        <ArrowUp :size="19" />
      </button>
    </div>
  </div>
</template>
