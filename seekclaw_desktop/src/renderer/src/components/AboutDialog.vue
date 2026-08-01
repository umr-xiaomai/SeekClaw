<script setup lang="ts">
import { ExternalLink, X } from '@lucide/vue'
import type { AppInfo } from '../../../shared/ipc'
import logoUrl from '../../../../resources/logo.png?url'

const props = defineProps<{
  open: boolean
  appInfo: AppInfo | null
}>()
const emit = defineEmits<{
  close: []
}>()

const platformNames: Record<string, string> = {
  win32: 'Windows',
  darwin: 'macOS',
  linux: 'Linux'
}

function platformLabel(): string {
  const raw = props.appInfo?.platform
  return (raw && platformNames[raw]) || raw || '未知'
}
</script>

<template>
  <Transition name="modal-fade">
    <div
      v-if="open"
      class="modal-backdrop about-backdrop"
      @mousedown.self="emit('close')"
    >
      <section class="about-dialog" role="dialog" aria-modal="true" aria-labelledby="about-title">
        <header class="about-header">
          <div class="brand-lockup about-lockup">
            <img :src="logoUrl" alt="" />
            <div>
              <h2 id="about-title">SeekClaw</h2>
              <p>AI Agent 运行时</p>
            </div>
          </div>
          <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
        </header>

        <dl class="about-meta">
          <div class="about-row">
            <dt>版本</dt>
            <dd>v{{ appInfo?.version ?? '—' }}</dd>
          </div>
          <div class="about-row">
            <dt>平台</dt>
            <dd>{{ platformLabel() }}</dd>
          </div>
        </dl>

        <p class="about-description">
          基于 .NET 构建的现代化、高性能 AI Agent 运行时，为 AI 驱动的编码助手提供完整平台。
        </p>

        <nav class="about-links" aria-label="SeekClaw 相关链接">
          <a href="https://seekclaw.hoilai.com" target="_blank" rel="noreferrer">
            官网 <ExternalLink :size="13" />
          </a>
          <a href="https://seekclaw.hoilai.com/doc/" target="_blank" rel="noreferrer">
            文档 <ExternalLink :size="13" />
          </a>
          <a href="https://github.com/umr-xiaomai/SeekClaw" target="_blank" rel="noreferrer">
            GitHub <ExternalLink :size="13" />
          </a>
        </nav>

        <footer class="about-footer">MIT License · © 2026 SeekClaw</footer>
      </section>
    </div>
  </Transition>
</template>
