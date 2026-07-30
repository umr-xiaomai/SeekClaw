<script setup lang="ts">
import { CircleAlert, RefreshCw } from '@lucide/vue'

defineProps<{
  open: boolean
  startup: boolean
  endpoint: string
  error?: string
}>()

const emit = defineEmits<{
  retry: []
  cancel: []
}>()
</script>

<template>
  <div v-if="open" class="modal-backdrop runtime-reconnect-backdrop">
    <section class="runtime-reconnect-dialog" role="alertdialog" aria-modal="true" aria-labelledby="runtime-reconnect-title">
      <CircleAlert :size="28" class="runtime-alert-icon" />
      <div class="runtime-reconnect-copy">
        <h2 id="runtime-reconnect-title">{{ startup ? '无法启动 Runtime 连接' : 'Runtime 连接已中断' }}</h2>
        <p>已连续尝试连接 5 次，但仍未连接到 SeekClaw Runtime。</p>
        <code>{{ endpoint }}</code>
        <small v-if="error">{{ error }}</small>
      </div>
      <footer>
        <button class="secondary-button" @click="emit('cancel')">
          {{ startup ? '取消并关闭' : '取消' }}
        </button>
        <button class="secondary-button primary-action" @click="emit('retry')">
          <RefreshCw :size="16" />继续重试
        </button>
      </footer>
    </section>
  </div>
</template>
