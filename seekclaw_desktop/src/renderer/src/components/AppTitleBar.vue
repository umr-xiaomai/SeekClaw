<script setup lang="ts">
import { ArrowLeft, ArrowRight, PanelLeft } from '@lucide/vue'

defineProps<{ sidebarOpen: boolean }>()
const emit = defineEmits<{
  toggleSidebar: []
  openWorkspace: []
  focusComposer: []
}>()
</script>

<template>
  <header class="titlebar">
    <div class="titlebar-actions no-drag">
      <button class="icon-button" title="切换侧栏" @click="emit('toggleSidebar')">
        <PanelLeft :size="18" />
      </button>
      <button class="icon-button is-muted" title="后退" disabled>
        <ArrowLeft :size="18" />
      </button>
      <button class="icon-button is-muted" title="前进" disabled>
        <ArrowRight :size="18" />
      </button>
    </div>
    <nav class="app-menu no-drag" aria-label="应用菜单">
      <button @click="emit('openWorkspace')">文件</button>
      <button @click="emit('focusComposer')">编辑</button>
      <button @click="emit('toggleSidebar')">视图</button>
      <a href="https://seekclaw.hoilai.com" target="_blank" rel="noreferrer">帮助</a>
    </nav>
    <div class="drag-fill" />
    <span class="titlebar-name">SeekClaw</span>
    <div class="window-controls-space" />
  </header>
</template>
