<script setup lang="ts">
import { Moon, Sun, X } from '@lucide/vue'

defineProps<{
  open: boolean
  theme: 'light' | 'dark'
  daemonConnected: boolean
  daemonEndpoint: string
}>()

const emit = defineEmits<{
  close: []
  changeTheme: [theme: 'light' | 'dark']
  reconnect: []
}>()
</script>

<template>
  <div v-if="open" class="modal-backdrop" @mousedown.self="emit('close')">
    <section class="settings-dialog" role="dialog" aria-modal="true" aria-label="设置">
      <header>
        <h2>设置</h2>
        <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
      </header>
      <div class="settings-row">
        <div><strong>外观</strong><small>选择桌面界面主题</small></div>
        <div class="segmented-control">
          <button :class="{ active: theme === 'light' }" title="浅色" @click="emit('changeTheme', 'light')"><Sun :size="16" /></button>
          <button :class="{ active: theme === 'dark' }" title="深色" @click="emit('changeTheme', 'dark')"><Moon :size="16" /></button>
        </div>
      </div>
      <div class="settings-row">
        <div><strong>SeekClaw Daemon</strong><small>{{ daemonEndpoint }}</small></div>
        <button class="secondary-button" @click="emit('reconnect')">
          {{ daemonConnected ? '重新连接' : '连接' }}
        </button>
      </div>
    </section>
  </div>
</template>
