<script setup lang="ts">
import { Code2, LoaderCircle, Monitor, RefreshCw, ShieldCheck, Smartphone, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { ArtifactWidget } from '../types'
import { previewDocument } from '../conversation-widgets'

const props = defineProps<{ artifact: ArtifactWidget; sessionId: string; workspace?: string }>()
const emit = defineEmits<{ close: [] }>()
const dialog = ref<HTMLDialogElement | null>(null)
const html = ref<string | null>(null)
const error = ref('')
const loading = ref(false)
const mobile = ref(false)
const frameVersion = ref(0)
const previewHtml = computed(() => html.value === null ? undefined : previewDocument(html.value))
let generation = 0
let disposed = false
async function load(): Promise<void> {
  const version = ++generation
  loading.value = true; error.value = ''
  try {
    const response = await window.seekclaw.daemon.request('artifact.read', {
      widgetId: props.artifact.id, sessionId: props.sessionId, workspace: props.workspace, global: !props.workspace
    })
    const result = JSON.parse(response.data) as { html: string }
    if (typeof result.html !== 'string') throw new Error('预览内容无效')
    if (!disposed && version === generation) { html.value = result.html; frameVersion.value++ }
  } catch (reason) {
    if (!disposed && version === generation) error.value = reason instanceof Error ? reason.message : String(reason)
  } finally { if (!disposed && version === generation) loading.value = false }
}
onMounted(() => { dialog.value?.showModal(); void load() })
onBeforeUnmount(() => { disposed = true; generation++; dialog.value?.close() })
</script>

<template>
  <Teleport to="body">
    <dialog ref="dialog" class="artifact-preview-dialog" :aria-label="`预览 ${artifact.title}`" @cancel.prevent="emit('close')" @click="($event.target === dialog) && emit('close')">
      <div class="artifact-preview-shell">
        <header class="artifact-preview-toolbar">
          <Code2 :size="18" /><div class="artifact-preview-title"><strong>{{ artifact.title }}</strong><small :title="artifact.path">{{ artifact.path }}</small></div>
          <div class="artifact-viewport-control" aria-label="预览宽度"><button type="button" :aria-pressed="!mobile" title="桌面宽度" aria-label="桌面宽度" @click="mobile = false"><Monitor :size="16" /></button><button type="button" :aria-pressed="mobile" title="手机宽度" aria-label="手机宽度" @click="mobile = true"><Smartphone :size="16" /></button></div>
          <button type="button" class="widget-button" :disabled="loading" title="重新读取文件" aria-label="重新读取文件" @click="load"><RefreshCw :size="15" :class="{ spin: loading }" /></button>
          <button type="button" class="widget-button" title="关闭预览（Esc）" aria-label="关闭预览" autofocus @click="emit('close')"><X :size="18" /></button>
        </header>
        <div class="artifact-preview-canvas" :class="{ mobile }" :aria-busy="loading">
          <div v-if="loading" class="artifact-preview-state" role="status"><LoaderCircle :size="24" class="spin" /><p>正在加载预览…</p></div>
          <div v-else-if="error" class="artifact-preview-state" role="alert"><Code2 :size="28" /><strong>暂时无法预览</strong><p>{{ error }}</p><button type="button" class="widget-button" @click="load">重新加载</button></div>
          <iframe v-else-if="previewHtml" :key="frameVersion" :srcdoc="previewHtml" :title="artifact.title" sandbox="allow-scripts" referrerpolicy="no-referrer" />
        </div>
        <footer class="artifact-preview-status"><ShieldCheck :size="13" /><span>独立沙箱 · 无法访问应用数据或外部网络；请使用内嵌样式与资源</span><span class="artifact-preview-size">{{ mobile ? '390 px' : '自适应宽度' }}</span></footer>
      </div>
    </dialog>
  </Teleport>
</template>
