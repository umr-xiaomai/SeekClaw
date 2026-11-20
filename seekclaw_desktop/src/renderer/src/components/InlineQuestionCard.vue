<script setup lang="ts">
import { ArrowRight, Pencil, X } from '@lucide/vue'
import { computed, useId } from 'vue'
import type { UserQuestion } from '../../../shared/user-questions'

const props = defineProps<{
  request: UserQuestion
  submitting: boolean
  submitted: boolean
  error: string
}>()
const draft = defineModel<string>('draft', { required: true })
const emit = defineEmits<{ answer: [answer: string]; cancel: [] }>()
const questionLabelId = useId()
const disabled = computed(() => props.submitting || props.submitted)

function submitCustom(): void {
  if (!disabled.value && draft.value.trim()) emit('answer', draft.value.trim())
}
</script>

<template>
  <section class="question-card" :aria-labelledby="questionLabelId" @keydown.enter.stop @keydown.esc.stop>
    <header class="question-header">
      <strong :id="questionLabelId" class="question-text">{{ request.question }}</strong>
      <button type="button" class="question-cancel" title="停止任务" aria-label="停止任务" @click="emit('cancel')">
        <X :size="16" aria-hidden="true" />
      </button>
    </header>
    <ol class="question-options" aria-label="回答选项">
      <li v-for="(option, index) in request.options" :key="index">
        <button type="button" class="question-option" :disabled="disabled" @click="emit('answer', option)">
          <span class="question-number" aria-hidden="true">{{ index + 1 }}</span>
          <span class="question-text">{{ option }}</span>
          <ArrowRight class="question-arrow" :size="16" aria-hidden="true" />
        </button>
      </li>
    </ol>
    <form class="question-custom" @submit.prevent="submitCustom">
      <Pencil :size="16" aria-hidden="true" />
      <input
        v-model="draft"
        type="text"
        maxlength="4000"
        placeholder="否，并告诉SeekClaw应该怎么做。"
        aria-label="自定义回答"
        :disabled="disabled"
      />
      <button type="submit" class="question-send" :disabled="disabled || !draft.trim()">回答</button>
    </form>
    <p v-if="submitting || submitted" class="question-status" role="status">
      {{ submitting ? '正在发送回答…' : '回答已发送' }}
    </p>
    <p v-if="error" class="question-status question-error" role="alert">{{ error }}</p>
  </section>
</template>

<style scoped>
.question-card {
  display: grid;
  gap: 12px;
  min-width: 0;
  max-height: 55vh;
  overflow: auto;
  box-sizing: border-box;
  padding: 14px 12px 12px;
  border: 1px solid var(--border);
  box-shadow: var(--composer-shadow);
  border-radius: var(--radius-dialog, 16px);
  background: var(--surface-raised);
  color: var(--text);
}

.question-header,
.question-custom {
  display: flex;
  align-items: center;
  gap: 8px;
}

.question-header {
  justify-content: space-between;
  padding-inline: 4px;
}

.question-text {
  min-width: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.question-options {
  display: grid;
  gap: 4px;
  padding: 0;
  margin: 0;
  list-style: none;
}

.question-option,
.question-cancel,
.question-send {
  color: inherit;
  cursor: pointer;
  font: inherit;
  border-radius: var(--radius-control, 8px);
}

.question-option {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: start;
  gap: 10px;
  width: 100%;
  padding: 8px;
  text-align: start;
  border: 0;
  background: transparent;
  transition: background-color 120ms ease;
}

.question-option:is(:hover, :focus-visible):not(:disabled) .question-arrow {
  opacity: 1;
}

.question-number {
  display: grid;
  place-items: center;
  min-width: 22px;
  min-height: 22px;
  border-radius: 5px;
  background: var(--surface-active);
  color: var(--text-secondary);
  font-size: 12px;
}

.question-arrow {
  align-self: center;
  color: var(--text-secondary);
  opacity: 0;
  transition: opacity 120ms ease;
}

.question-cancel {
  display: grid;
  place-items: center;
  flex: 0 0 28px;
  height: 28px;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
}

.question-custom {
  padding: 4px 4px 4px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-control, 8px);
  color: var(--text-secondary);
}

.question-custom input {
  flex: 1;
  min-width: 0;
  padding: 5px 2px;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
}

.question-send {
  padding: 5px 10px;
  border: 1px solid var(--border);
  background: var(--surface-active);
  color: var(--text);
}

.question-card button:hover:not(:disabled) {
  background: var(--surface-hover);
}

.question-card button:disabled {
  cursor: default;
  opacity: .55;
}

.question-card :is(button, input):focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.question-status {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.question-error {
  color: var(--danger);
}
</style>
