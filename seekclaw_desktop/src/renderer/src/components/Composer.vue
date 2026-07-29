<script setup lang="ts">
import { ArrowUp, ChevronDown, FolderPlus, Square } from '@lucide/vue'
import { nextTick, ref, watch } from 'vue'

const props = defineProps<{
  busy: boolean
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

function resize(): void {
  if (!textarea.value) return
  textarea.value.style.height = '0'
  textarea.value.style.height = `${Math.min(176, Math.max(30, textarea.value.scrollHeight))}px`
}

function submit(): void {
  const message = value.value.trim()
  if (!message || props.busy) return
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

defineExpose({ focus })
watch(value, resize)
</script>

<template>
  <div class="composer-shell">
    <textarea
      ref="textarea"
      v-model="value"
      rows="1"
      placeholder="交给 SeekClaw"
      aria-label="消息"
      @keydown="handleKeydown"
    />
    <div class="composer-toolbar">
      <button class="icon-button composer-icon" title="添加工作区" @click="emit('attach')">
        <FolderPlus :size="18" />
      </button>
      <label class="select-control">
        <select :value="mode" :disabled="busy" aria-label="Agent 模式" @change="emit('changeMode', ($event.target as HTMLSelectElement).value)">
          <option value="edit">Edit</option>
          <option value="plan">Plan</option>
          <option value="readonly">Read only</option>
          <option value="auto">Auto</option>
        </select>
        <ChevronDown :size="14" />
      </label>
      <label class="select-control model-control">
        <select :value="model" :disabled="busy" aria-label="模型" @change="emit('changeModel', ($event.target as HTMLSelectElement).value)">
          <option v-if="models.length === 0" :value="model">{{ model }}</option>
          <option v-for="item in models" :key="item" :value="item">{{ item }}</option>
        </select>
        <ChevronDown :size="14" />
      </label>
      <span class="toolbar-spacer" />
      <button v-if="busy" class="send-button" title="停止" @click="emit('stop')">
        <Square :size="14" fill="currentColor" />
      </button>
      <button v-else class="send-button" title="发送" :disabled="!value.trim()" @click="submit">
        <ArrowUp :size="19" />
      </button>
    </div>
  </div>
</template>
