<script setup lang="ts">
import { Braces, Check, ChevronDown, CircleAlert, Eye, Image as ImageIcon, LoaderCircle, Wrench } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ChatMessage } from '../types'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import MarkdownMessage from './MarkdownMessage.vue'

const props = withDefaults(defineProps<{
  message: ChatMessage
  imageSources?: Record<string, string>
}>(), { imageSources: () => ({}) })
const emit = defineEmits<{
  openDiff: [filePath: string, diff: string]
}>()
const thinkingOpen = ref(false)
const preview = ref<{ src: string; name: string } | null>(null)

const regularTools = computed(() => (props.message.tools ?? []).filter((tool) => !tool.diff))
const editedTools = computed(() => (props.message.tools ?? []).filter((tool) => tool.diff && tool.filePath))

function imageUrl(id: string): string | undefined {
  return props.imageSources[id]
}

function previewImage(id: string, name: string): void {
  const src = imageUrl(id)
  if (src) preview.value = { src, name }
}

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
    <div v-if="message.role === 'user'" class="user-message-stack">
      <div v-if="message.images?.length" class="user-image-grid" :class="{ single: message.images.length === 1 }">
        <button
          v-for="image in message.images"
          :key="image.id"
          type="button"
          class="user-image-button"
          :title="`预览 ${image.name}`"
          @click="previewImage(image.id, image.name)"
        >
          <img :src="imageUrl(image.id)" :alt="image.name">
          <span>{{ image.name }}</span>
        </button>
      </div>
      <div v-if="message.content" class="user-bubble">{{ message.content }}</div>
    </div>

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

      <div v-if="message.viewedImages?.length" class="image-view-list" aria-label="AI 已查看的图片">
        <button
          v-for="image in message.viewedImages"
          :key="image.id"
          type="button"
          class="image-view-row"
          :class="{ previewable: Boolean(imageUrl(image.id)) }"
          :disabled="!imageUrl(image.id)"
          :title="imageUrl(image.id) ? `预览 ${image.name}` : image.name"
          @click="previewImage(image.id, image.name)"
        >
          <span class="image-view-thumbnail">
            <img v-if="imageUrl(image.id)" :src="imageUrl(image.id)" :alt="image.name">
            <ImageIcon v-else :size="16" />
          </span>
          <Eye :size="15" />
          <span>已查看</span>
          <small>{{ image.name }}</small>
        </button>
      </div>

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

  <ImagePreviewDialog :src="preview?.src" :name="preview?.name" @close="preview = null" />
</template>
