<script setup lang="ts">
import { ArrowUpRight, FileCode, FileText, FolderOpen } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { ArtifactWidget } from '../types'
import { requestArtifactPreview } from '../artifact-preview'

const props = defineProps<{ widget: ArtifactWidget; sessionId?: string; workspace?: string }>()
const error = ref('')
const html = computed(() => props.widget.mediaType === 'text/html')
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
function preview(event: MouseEvent): void {
  if (!props.sessionId) return
  requestArtifactPreview({
    artifact: props.widget,
    sessionId: props.sessionId,
    workspace: props.workspace,
    trigger: event.currentTarget as HTMLButtonElement
  })
}
async function reveal(): Promise<void> {
  error.value = ''
  try { await window.seekclaw.showItemInFolder(props.widget.path) }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
}
</script>

<template>
  <section class="conversation-widget artifact-widget" :aria-label="html ? 'HTML 预览' : '成果文件'">
    <div class="artifact-card-row">
      <button v-if="html" type="button" class="artifact-card-result" :disabled="!sessionId"
        :title="sessionId ? '打开交互预览' : '会话保存后可预览'"
        :aria-label="`打开交互预览：${widget.title}`" @click="preview">
        <FileCode :size="16" class="artifact-card-icon" aria-hidden="true" />
        <span class="artifact-card-label">
          <strong :title="widget.title">{{ widget.title }}</strong>
          <small :title="widget.path">{{ displayPath }}</small>
        </span>
        <ArrowUpRight v-if="sessionId" :size="15" class="artifact-card-icon" aria-hidden="true" />
      </button>
      <div v-else class="artifact-card-result">
        <FileText :size="16" class="artifact-card-icon" aria-hidden="true" />
        <span class="artifact-card-label">
          <strong :title="widget.title">{{ widget.title }}</strong>
          <small :title="widget.path">{{ displayPath }}</small>
        </span>
      </div>
      <button type="button" class="artifact-reveal" title="打开位置" aria-label="打开位置" @click="reveal">
        <FolderOpen :size="15" aria-hidden="true" />
      </button>
    </div>
    <p v-if="widget.description" class="artifact-card-description">{{ widget.description }}</p>
    <p v-if="error" class="artifact-card-error" role="alert">{{ error }}</p>
  </section>
</template>

<style scoped>
.artifact-widget {
  align-self: start;
  background: var(--surface);
  border-radius: 9px;
}
.artifact-card-row {
  display: flex;
  align-items: center;
  min-width: 0;
  min-height: 46px;
  padding-inline-end: 6px;
}
.artifact-card-result {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 9px;
  min-width: 0;
  padding: 7px 10px;
  background: transparent;
  color: var(--text);
  text-align: left;
}
button.artifact-card-result:enabled { cursor: pointer; }
.artifact-widget:has(button.artifact-card-result:enabled:hover) { background: var(--surface-hover); }
.artifact-card-label { flex: 1; min-width: 0; }
.artifact-card-label > :is(strong, small) { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.artifact-card-label strong { font-size: 13px; font-weight: 600; line-height: 1.45; }
.artifact-card-label small { color: var(--text-muted); font-size: 11px; line-height: 1.4; }
.artifact-card-icon { flex: none; color: var(--accent); }
.artifact-reveal {
  display: grid;
  place-items: center;
  flex: none;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}
.artifact-reveal:hover { background: var(--surface-active); color: var(--text); }
.artifact-card-result:focus-visible, .artifact-reveal:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.artifact-card-description, .artifact-card-error { margin: 0; padding: 0 10px 8px; color: var(--text-secondary); font-size: 12px; overflow-wrap: anywhere; }
.artifact-card-error { color: var(--danger); }
</style>
