<script setup lang="ts">
import { ArrowUpRight, Globe2, X } from '@lucide/vue'
import { computed, ref } from 'vue'
import { browserResultFromTool } from '../browser-result'
import type { ToolActivity } from '../types'

const DEFAULT_WIDTH = 520
const MIN_WIDTH = 320
const MAX_WIDTH = 1400
const props = withDefaults(defineProps<{ tool: ToolActivity; width?: number; liveAvailable?: boolean }>(), {
  width: DEFAULT_WIDTH,
  liveAvailable: false
})
const emit = defineEmits<{ close: []; openLive: []; resize: [width: number] }>()
const result = computed(() => browserResultFromTool(props.tool))
const title = computed(() => result.value.title || '浏览器结果')
const panel = ref<HTMLElement>()

function startResize(event: PointerEvent): void {
  if (event.button !== 0 || !panel.value) return
  event.preventDefault()
  const handle = event.currentTarget as HTMLElement
  const startX = event.clientX
  const startWidth = panel.value.getBoundingClientRect().width
  const maxWidth = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, Math.floor(window.innerWidth * .85)))
  handle.setPointerCapture(event.pointerId)
  const move = (pointer: PointerEvent) => {
    const width = Math.min(maxWidth, Math.max(MIN_WIDTH, startWidth + startX - pointer.clientX))
    emit('resize', width)
  }
  const stop = () => {
    handle.removeEventListener('pointermove', move)
  }
  handle.addEventListener('pointermove', move)
  handle.addEventListener('lostpointercapture', stop, { once: true })
}
</script>

<template>
  <aside ref="panel" class="browser-result-panel" :style="{ '--browser-result-width': `${width}px` }"
    aria-label="浏览器结果" @keydown.esc.stop="emit('close')">
    <div class="browser-result-resizer" role="separator" aria-label="调整浏览器结果宽度" aria-orientation="vertical"
      @pointerdown="startResize" @dblclick="emit('resize', DEFAULT_WIDTH)" />
    <header class="browser-result-header">
      <Globe2 :size="16" aria-hidden="true" />
      <h2 :title="title">{{ title }}</h2>
      <button v-if="liveAvailable" type="button" class="browser-result-live" @click="emit('openLive')">
        <ArrowUpRight :size="14" aria-hidden="true" />会话浏览器
      </button>
      <button type="button" class="icon-button" title="关闭浏览器结果" aria-label="关闭浏览器结果" @click="emit('close')">
        <X :size="16" aria-hidden="true" />
      </button>
    </header>
    <div class="browser-result-content">
      <p v-if="result.url" class="browser-result-url">{{ result.url }}</p>
      <section v-if="result.text" aria-label="页面文字">
        <h3>页面文字</h3>
        <p class="browser-result-text">{{ result.text }}</p>
      </section>
      <details v-if="result.elements.length" class="browser-result-elements">
        <summary>页面元素（{{ result.elements.length }}）</summary>
        <ul>
          <li v-for="(element, index) in result.elements" :key="index">{{ element }}</li>
        </ul>
      </details>
      <pre v-if="result.fallback" class="browser-result-text">{{ result.fallback }}</pre>
      <p v-else-if="!result.text && !result.elements.length" class="browser-result-empty">此调用没有记录页面文字。</p>
    </div>
  </aside>
</template>

<style scoped>
.browser-result-panel {
  position: relative;
  z-index: 6;
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  width: min(var(--browser-result-width), 100%);
  min-width: 0;
  min-height: 0;
  height: 100%;
  overflow: hidden;
  background: var(--surface);
  border-left: 1px solid var(--border);
}
.browser-result-resizer { position: absolute; z-index: 1; inset: 0 auto 0 0; width: 8px; cursor: col-resize; touch-action: none; }
.browser-result-resizer::after { position: absolute; inset: 50% auto auto 2px; width: 2px; height: 34px; border-radius: 2px; background: var(--accent); content: ''; opacity: 0; transform: translateY(-50%); }
.browser-result-resizer:hover::after, .browser-result-resizer:active::after { opacity: 1; }
.browser-result-header { display: flex; align-items: center; flex: none; gap: 8px; min-height: 48px; padding: 6px 12px 6px 16px; border-bottom: 1px solid var(--border); }
.browser-result-header > svg { flex: none; color: var(--text-muted); }
.browser-result-header h2 { flex: 1; min-width: 0; margin: 0; overflow: hidden; font-size: 13px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.browser-result-header > button { flex: none; }
.browser-result-live { display: inline-flex; align-items: center; gap: 4px; padding: 5px 7px; border-radius: 6px; background: transparent; color: var(--text-secondary); font-size: 12px; cursor: pointer; }
.browser-result-live:hover { background: var(--surface-hover); color: var(--text); }
.browser-result-content { display: grid; align-content: start; gap: 13px; min-height: 0; overflow: auto; padding: 16px; overscroll-behavior: contain; }
.browser-result-content :is(p, h3, pre, ul) { margin: 0; overflow-wrap: anywhere; }
.browser-result-content h3 { margin-bottom: 6px; font-size: 13px; font-weight: 600; }
.browser-result-url, .browser-result-empty { color: var(--text-muted); font-size: 12px; }
.browser-result-text { color: var(--text-secondary); font-size: 13px; line-height: 1.65; white-space: pre-wrap; }
.browser-result-elements { color: var(--text-secondary); font-size: 12px; }
.browser-result-elements > summary { width: fit-content; cursor: pointer; }
.browser-result-elements ul { padding: 7px 0 0; list-style: none; font: 11px/1.6 var(--font-mono); }
.browser-result-panel :is(button, summary):focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
