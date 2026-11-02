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
async function reveal(): Promise<void> {
  error.value = ''
  try { await window.seekclaw.showItemInFolder(props.widget.path) }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
}
</script>

<template>
  <section class="conversation-widget artifact-widget" :aria-label="html ? 'HTML 预览' : '成果文件'">
    <header class="widget-heading">
      <span class="widget-icon"><Code2 v-if="html" :size="19" /><FileText v-else :size="19" /></span>
      <div class="widget-heading-text"><span class="widget-eyebrow">{{ html ? 'HTML 预览' : '成果文件' }}</span><h3>{{ widget.title }}</h3></div>
      <span class="widget-format">{{ html ? 'HTML' : filename.split('.').pop()?.toUpperCase().slice(0, 8) || 'FILE' }}</span>
    </header>
    <div class="widget-body">
      <p v-if="widget.description" class="widget-description">{{ widget.description }}</p>
      <button v-if="html" type="button" class="artifact-preview-launch" :disabled="!sessionId" @click="previewOpen = true">
        <span class="artifact-preview-glyph"><Code2 :size="28" :stroke-width="1.4" /></span>
        <span><strong>打开交互预览</strong><small>{{ filename }} · 在独立沙箱中运行</small></span><Maximize2 :size="16" />
      </button>
      <p v-else class="artifact-file-path" :title="widget.path">{{ filename }}</p>
      <p v-if="html && !sessionId" class="widget-footnote">会话保存后可打开预览</p>
      <p v-if="error" class="widget-error" role="alert">{{ error }}</p>
    </div>
    <footer class="widget-footer"><span class="widget-footnote artifact-location" :title="widget.path">{{ widget.path }}</span><button type="button" class="widget-button" @click="reveal"><FolderOpen :size="13" />打开位置</button></footer>
  </section>
  <ArtifactPreviewDialog v-if="previewOpen && sessionId" :artifact="widget" :session-id="sessionId" :workspace="workspace" @close="previewOpen = false" />
</template>
