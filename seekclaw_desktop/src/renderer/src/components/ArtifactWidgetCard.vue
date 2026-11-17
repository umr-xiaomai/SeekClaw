<script setup lang="ts">
import { Code2, FileText, FolderOpen, Maximize2 } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ArtifactWidget } from '../types'
import ArtifactPreviewDialog from './ArtifactPreviewDialog.vue'

const props = defineProps<{ widget: ArtifactWidget; sessionId?: string; workspace?: string }>()
const previewOpen = ref(false)
const error = ref('')
const html = computed(() => props.widget.mediaType === 'text/html')
const filename = computed(() => props.widget.path.split(/[\\/]/).pop() || props.widget.path)
const displayPath = computed(() => {
  const full = props.widget.path
  const root = props.workspace?.trim().replace(/[\\/]+$/, '')
  if (root) {
    const norm = (value: string) => value.replace(/\\/g, '/').toLowerCase()
    if (norm(full).startsWith(`${norm(root)}/`)) {
      return full.slice(root.length + 1)
    }
  }
  return full
})
async function reveal(): Promise<void> {
  error.value = ''
  try { await window.seekclaw.showItemInFolder(props.widget.path) }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
}
</script>

<template>
  <section class="conversation-widget artifact-widget" :aria-label="html ? 'HTML 预览' : '成果文件'">
    <header class="widget-heading">
      <span class="widget-icon"><Code2 v-if="html" :size="16" /><FileText v-else :size="16" /></span>
      <div class="widget-heading-text">
        <span class="widget-eyebrow">{{ html ? 'HTML 预览' : '成果文件' }}</span>
        <h3 :title="widget.title">{{ widget.title }}</h3>
      </div>
      <span class="widget-format">{{ html ? 'HTML' : filename.split('.').pop()?.toUpperCase().slice(0, 8) || 'FILE' }}</span>
    </header>
    <div class="widget-body">
      <p v-if="widget.description" class="widget-description">{{ widget.description }}</p>
      <p class="widget-footnote artifact-location" :title="widget.path">{{ displayPath }}</p>
      <p v-if="error" class="widget-error" role="alert">{{ error }}</p>
    </div>
    <footer class="widget-footer">
      <span v-if="html && !sessionId" class="widget-footnote artifact-save-hint">会话保存后可预览</span>
      <div class="widget-actions">
        <button type="button" class="widget-button" @click="reveal"><FolderOpen :size="13" />打开位置</button>
        <button v-if="html" type="button" class="widget-button widget-primary" :disabled="!sessionId" @click="previewOpen = true">
          <Maximize2 :size="13" />打开交互预览
        </button>
      </div>
    </footer>
  </section>
  <ArtifactPreviewDialog v-if="sessionId" :open="previewOpen" :artifact="widget" :session-id="sessionId" :workspace="workspace" @close="previewOpen = false" />
</template>
