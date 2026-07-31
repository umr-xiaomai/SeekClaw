<script setup lang="ts">
import { Braces, Check, ChevronDown, CircleAlert, LoaderCircle, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ChatMessage } from '../types'
import MarkdownMessage from './MarkdownMessage.vue'

const props = defineProps<{ message: ChatMessage }>()
const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
}>()
const thinkingOpen = ref(false)

const regularTools = computed(() => (props.message.tools ?? []).filter((tool) => !tool.diff))
const editedTools = computed(() => (props.message.tools ?? []).filter((tool) => tool.diff && tool.filePath))

function diffStats(diff?: string): { added: number; removed: number } {
  if (!diff) return { added: 0, removed: 0 }
  return diff.split(/\r?\n/).reduce((stats, line) => {
    if (line.startsWith('+++') || line.startsWith('---')) return stats
    if (line.startsWith('+')) stats.added++
    else if (line.startsWith('-')) stats.removed++
    return stats
  }, { added: 0, removed: 0 })
}

const editStats = computed(() => editedTools.value.reduce((stats, tool) => {
  const current = diffStats(tool.diff)
  stats.added += current.added
  stats.removed += current.removed
  return stats
}, { added: 0, removed: 0 }))
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

      <div v-if="regularTools.length" class="tool-list">
        <div v-for="tool in regularTools" :key="tool.id" class="tool-row">
          <LoaderCircle v-if="tool.state === 'running'" :size="15" class="spin" />
          <CircleAlert v-else-if="tool.state === 'error'" :size="15" />
          <Wrench v-else :size="15" />
          <span>{{ tool.name }}</span>
          <small v-if="tool.detail">{{ tool.detail }}</small>
        </div>
      </div>

      <section v-if="editedTools.length" class="change-card" aria-label="代码修改">
        <header class="change-card-header">
          <div class="change-card-icon"><Braces :size="18" /></div>
          <div class="change-card-title">
            <strong>已编辑 {{ editedTools.length }} 个文件</strong>
            <span>
              <b class="change-added">+{{ editStats.added }}</b>
              <b class="change-removed">-{{ editStats.removed }}</b>
            </span>
          </div>
          <span class="change-card-state">已完成</span>
        </header>
        <button
          v-for="tool in editedTools"
          :key="tool.id"
          type="button"
          class="change-file-row"
          :title="`查看 ${tool.filePath} 的 Diff`"
          @click="emit('openDiff', tool.filePath!, tool.diff!)"
        >
          <span class="change-file-path">{{ tool.filePath }}</span>
          <span class="change-file-stats">
            <b class="change-added">+{{ diffStats(tool.diff).added }}</b>
            <b class="change-removed">-{{ diffStats(tool.diff).removed }}</b>
          </span>
        </button>
      </section>

      <MarkdownMessage v-if="message.content" :content="message.content" />
      <div
        v-if="message.state === 'thinking' || message.state === 'streaming'"
        class="response-placeholder"
        aria-label="AI 正在思考"
      >
        <LoaderCircle :size="15" class="thinking-spinner spin" />
        <span /><span /><span />
      </div>
    </div>
  </article>
</template>
