<script setup lang="ts">
import type { ToolActivity } from '../types'

defineProps<{ tool: ToolActivity }>()

function mediaUrl(block: Record<string, unknown>, kind: 'image' | 'audio'): string | undefined {
  const mime = typeof block.mimeType === 'string' ? block.mimeType : ''
  const data = typeof block.data === 'string' ? block.data : ''
  const allowed = kind === 'image' ? /^image\/(png|jpeg|gif|webp|avif)$/ : /^audio\/[a-z0-9.+-]+$/
  if (!allowed.test(mime) || data.length > 14 * 1024 * 1024 || !/^[A-Za-z0-9+/=\s]*$/.test(data)) return undefined
  return `data:${mime};base64,${data}`
}

function resourceUrl(uri: unknown): string | undefined {
  if (typeof uri !== 'string') return undefined
  try {
    const url = new URL(uri)
    return ['https:', 'http:'].includes(url.protocol) ? url.href : undefined
  } catch { return undefined }
}

function resource(block: Record<string, unknown>): Record<string, unknown> {
  return typeof block.resource === 'object' && block.resource !== null ? block.resource as Record<string, unknown> : {}
}
</script>

<template>
  <details v-if="tool.content?.length || tool.structuredContent != null || tool.output" class="mcp-result">
    <summary>{{ tool.name }} · 查看结果</summary>
    <div class="mcp-result-body">
      <template v-for="(block, index) in tool.content" :key="index">
        <pre v-if="block.type === 'text'">{{ block.text }}</pre>
        <img v-else-if="block.type === 'image' && mediaUrl(block, 'image')" :src="mediaUrl(block, 'image')" alt="工具返回的图片" loading="lazy" />
        <audio v-else-if="block.type === 'audio' && mediaUrl(block, 'audio')" :src="mediaUrl(block, 'audio')" controls preload="none" />
        <div v-else-if="block.type === 'resource_link'">
          <a v-if="resourceUrl(block.uri)" :href="resourceUrl(block.uri)" target="_blank" rel="noopener noreferrer">{{ block.name || block.uri }}</a>
          <code v-else>{{ block.name }} {{ block.uri }}</code>
          <p v-if="block.description">{{ block.description }}</p>
        </div>
        <div v-else-if="block.type === 'resource'">
          <code>{{ resource(block).uri }}</code>
          <pre v-if="resource(block).text">{{ resource(block).text }}</pre>
          <small v-else>{{ resource(block).mimeType }} · 二进制资源</small>
        </div>
        <pre v-else>{{ JSON.stringify(block, null, 2) }}</pre>
      </template>
      <pre v-if="tool.structuredContent != null">{{ JSON.stringify(tool.structuredContent, null, 2) }}</pre>
      <pre v-if="!tool.content?.length && tool.structuredContent == null && tool.output">{{ tool.output }}</pre>
    </div>
  </details>
</template>

<style scoped>
.mcp-result { margin: 6px 0; font-size: 12px; color: var(--text-muted); }
.mcp-result summary { cursor: pointer; }
.mcp-result-body { display: grid; gap: 8px; max-height: 480px; overflow: auto; padding: 12px; border: 1px solid var(--border); border-radius: 8px; }
.mcp-result img { max-width: 100%; max-height: 320px; object-fit: contain; }
.mcp-result pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; }
.mcp-result audio { max-width: 100%; }
</style>
