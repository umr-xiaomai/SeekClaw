<script setup lang="ts">
import { Check, ChevronDown, CircleAlert, LoaderCircle, Wrench } from '@lucide/vue'
import { ref } from 'vue'
import type { ChatMessage } from '../types'
import MarkdownMessage from './MarkdownMessage.vue'

defineProps<{ message: ChatMessage }>()
const thinkingOpen = ref(false)
</script>

<template>
  <article class="message" :class="`message-${message.role}`">
    <div v-if="message.role === 'user'" class="user-bubble">{{ message.content }}</div>

    <div v-else class="assistant-message">
      <button
        v-if="message.thinking"
        class="thinking-toggle"
        :class="{ active: message.state === 'thinking' }"
        @click="thinkingOpen = !thinkingOpen"
      >
        <LoaderCircle v-if="message.state === 'thinking'" :size="16" class="spin" />
        <Check v-else :size="16" />
        <span>{{ message.state === 'thinking' ? '正在思考' : '已完成思考' }}</span>
        <ChevronDown :size="15" :class="{ rotated: thinkingOpen }" />
      </button>
      <div v-if="thinkingOpen && message.thinking" class="thinking-content">{{ message.thinking }}</div>

      <div v-if="message.tools?.length" class="tool-list">
        <div v-for="tool in message.tools" :key="tool.id" class="tool-row">
          <LoaderCircle v-if="tool.state === 'running'" :size="15" class="spin" />
          <CircleAlert v-else-if="tool.state === 'error'" :size="15" />
          <Wrench v-else :size="15" />
          <span>{{ tool.name }}</span>
          <small v-if="tool.detail">{{ tool.detail }}</small>
        </div>
      </div>

      <MarkdownMessage v-if="message.content" :content="message.content" />
      <div v-else-if="message.state === 'thinking'" class="response-placeholder">
        <span /><span /><span />
      </div>
    </div>
  </article>
</template>
