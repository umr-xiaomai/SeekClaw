<script setup lang="ts">
import {
  Bell,
  Camera,
  Check,
  Download,
  Globe2,
  MapPin,
  Mic,
  ShieldAlert,
  X
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted } from 'vue'
import type { BrowserApprovalDecision, BrowserApprovalRequest } from '../../../shared/browser'

const props = defineProps<{
  request: BrowserApprovalRequest | null
  queueCount?: number
}>()

const emit = defineEmits<{
  decide: [decision: BrowserApprovalDecision]
}>()

const isDownload = computed(() => props.request?.kind === 'download')

const headerIcon = computed(() => {
  if (isDownload.value) return Download
  const p = props.request?.permission || ''
  if (p === 'camera' || p === 'camera+microphone' || p === 'media') return Camera
  if (p === 'microphone') return Mic
  if (p === 'geolocation') return MapPin
  if (p === 'notifications') return Bell
  return ShieldAlert
})

const title = computed(() => {
  if (!props.request) return ''
  if (props.request.kind === 'download') return '会话浏览器请求下载文件'
  return `会话浏览器请求「${props.request.permissionLabel || props.request.permission || '网页'}」权限`
})

const formattedBytes = computed(() => {
  const bytes = props.request?.totalBytes
  if (!bytes || !Number.isFinite(bytes) || bytes <= 0) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`
})

function decide(approved: boolean, rememberSession = false): void {
  if (!props.request) return
  emit('decide', {
    id: props.request.id,
    approved,
    ...(rememberSession ? { rememberSession: true } : {})
  })
}

function handleKeydown(event: KeyboardEvent): void {
  if (!props.request) return
  if (event.key === 'Escape') {
    event.preventDefault()
    decide(false)
  } else if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    decide(true, false)
  }
}

onMounted(() => window.addEventListener('keydown', handleKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', handleKeydown))
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="request" class="modal-backdrop browser-approval-backdrop" @mousedown.self="decide(false)">
      <section class="confirmation-dialog browser-approval-dialog" role="alertdialog" aria-modal="true" :aria-label="title">
        <header>
          <span class="confirmation-icon browser-approval-icon">
            <component :is="headerIcon" :size="20" />
          </span>
          <div class="browser-approval-head">
            <h2>{{ title }}</h2>
            <p class="browser-approval-origin">
              <Globe2 :size="13" aria-hidden="true" />
              <span>{{ request.origin || request.url }}</span>
              <span v-if="(queueCount ?? 0) > 1" class="browser-approval-queue">还有 {{ (queueCount ?? 1) - 1 }} 项待确认</span>
            </p>
          </div>
          <button type="button" class="icon-button" title="拒绝 (Esc)" aria-label="关闭" @click="decide(false)">
            <X :size="17" />
          </button>
        </header>

        <div class="browser-approval-details">
          <div v-if="isDownload" class="browser-approval-kv">
            <div><strong>文件名</strong><span>{{ request.filename || 'download' }}</span></div>
            <div v-if="formattedBytes"><strong>大小</strong><span>{{ formattedBytes }}</span></div>
            <div v-if="request.savePath"><strong>保存到</strong><code>{{ request.savePath }}</code></div>
            <div><strong>来源地址</strong><code>{{ request.url }}</code></div>
          </div>
          <div v-else class="browser-approval-kv">
            <div><strong>请求权限</strong><span>{{ request.permissionLabel || request.permission }}</span></div>
            <div><strong>站点来源</strong><code>{{ request.origin || request.url }}</code></div>
          </div>

          <p v-if="request.reason" class="browser-approval-reason">
            <strong>AI 说明：</strong>{{ request.reason }}
          </p>
        </div>

        <footer class="browser-approval-footer">
          <button type="button" class="secondary-button" @click="decide(false)">拒绝</button>
          <div class="browser-approval-actions-right">
            <button
              v-if="request.allowRememberSession && !isDownload"
              type="button"
              class="secondary-button"
              @click="decide(true, true)"
            >
              本会话始终允许
            </button>
            <button type="button" class="secondary-button primary-action" @click="decide(true, false)">
              <Check :size="14" aria-hidden="true" />
              <span>{{ isDownload ? '允许下载' : '允许本次' }}</span>
            </button>
          </div>
        </footer>
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.browser-approval-backdrop {
  z-index: 1350;
}

.browser-approval-dialog {
  width: min(500px, calc(100vw - 32px));
}

.browser-approval-icon {
  background: color-mix(in srgb, var(--accent) 14%, var(--surface));
  color: var(--accent);
}

.browser-approval-head {
  min-width: 0;
  flex: 1 1 auto;
}

.browser-approval-origin {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px;
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

.browser-approval-queue {
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-hover);
  color: var(--text-secondary);
  font-size: 11px;
}

.browser-approval-details {
  display: grid;
  gap: 10px;
  margin-top: 12px;
}

.browser-approval-kv {
  display: grid;
  gap: 7px;
  padding: 10px 12px;
  border-radius: 10px;
  background: var(--surface-subtle);
  border: 1px solid var(--border);
  font-size: 12.5px;
}

.browser-approval-kv > div {
  display: grid;
  grid-template-columns: 74px minmax(0, 1fr);
  gap: 8px;
  align-items: baseline;
}

.browser-approval-kv strong {
  color: var(--text-muted);
  font-weight: 500;
}

.browser-approval-kv span,
.browser-approval-kv code {
  min-width: 0;
  overflow-wrap: anywhere;
  color: var(--text);
}

.browser-approval-kv code {
  font-family: var(--font-mono);
  font-size: 11.5px;
}

.browser-approval-reason {
  margin: 0;
  padding: 8px 10px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent) 7%, var(--surface));
  color: var(--text-secondary);
  font-size: 12.5px;
  line-height: 1.5;
}

.browser-approval-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.browser-approval-actions-right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.browser-approval-actions-right .primary-action {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}
</style>
