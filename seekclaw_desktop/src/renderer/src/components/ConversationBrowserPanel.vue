<script setup lang="ts">
import { ArrowLeft, ArrowRight, Globe2, LoaderCircle, Pause, Play, Plus, RotateCw, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { BrowserCommand, ConversationBrowserState } from '../../../shared/browser'

const props = defineProps<{ state: ConversationBrowserState }>()
const emit = defineEmits<{ close: []; changed: [state: ConversationBrowserState] }>()
const viewport = ref<HTMLElement | null>(null)
const address = ref('')
const error = ref('')
const tab = computed(() => props.state.tabs.find(tab => tab.id === props.state.activeTabId))
let resizeObserver: ResizeObserver | undefined
let mutationObserver: MutationObserver | undefined
let frame = 0
let disposed = false
let overlayVisible = false

async function command(command: BrowserCommand): Promise<void> {
  error.value = ''
  try { emit('changed', await window.seekclaw.browser.command(props.state.scope, command)) }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
}
function navigate(): void {
  const url = address.value.trim()
  if (url) void command({ action: tab.value ? 'navigate' : 'open', url: /^https?:\/\//i.test(url) ? url : `https://${url}` })
}
async function togglePause(): Promise<void> {
  try { emit('changed', await window.seekclaw.browser.pause(props.state.scope, !props.state.paused)) }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
}
function updateLayout(): void {
  if (disposed || frame) return
  frame = requestAnimationFrame(() => {
    frame = 0
    const rect = viewport.value?.getBoundingClientRect()
    // Native views sit above renderer content. Hide them whenever an app overlay is visible.
    const overlay = [...document.querySelectorAll('dialog[open], [role="dialog"], [role="menu"], [role="listbox"], .modal-backdrop, .artifact-preview-backdrop, .context-menu, .select-menu-popover')]
      .some(element => element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden')
    if (overlay !== overlayVisible) {
      overlayVisible = overlay
      void window.seekclaw.browser.overlay(overlay).catch(() => undefined)
    }
    void window.seekclaw.browser.layout(rect ? { scope: props.state.scope,
      bounds: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
      visible: !!tab.value && tab.value.url !== 'about:blank' && rect.width > 0 && rect.height > 0 && !overlay } : null).catch(() => undefined)
  })
}
watch(() => tab.value?.url, url => { address.value = url === 'about:blank' ? '' : url || '' }, { immediate: true })
watch(() => [props.state.scope, props.state.activeTabId, tab.value?.url, error.value], () => void nextTick(updateLayout))
onMounted(() => {
  resizeObserver = new ResizeObserver(updateLayout)
  if (viewport.value) resizeObserver.observe(viewport.value)
  mutationObserver = new MutationObserver(updateLayout)
  mutationObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden', 'open'] })
  window.addEventListener('resize', updateLayout)
  updateLayout()
})
onBeforeUnmount(() => {
  disposed = true
  cancelAnimationFrame(frame)
  resizeObserver?.disconnect()
  mutationObserver?.disconnect()
  window.removeEventListener('resize', updateLayout)
  void window.seekclaw.browser.layout(null).catch(() => undefined)
  void window.seekclaw.browser.overlay(false).catch(() => undefined)
})
</script>

<template>
  <aside class="conversation-browser-panel" aria-label="会话浏览器">
    <header class="browser-panel-header">
      <Globe2 :size="16" /><strong>浏览器</strong>
      <span class="browser-agent-state" role="status"><LoaderCircle v-if="state.busy" :size="12" class="spin" />{{ state.paused ? '已暂停 Agent' : state.busy ? 'Agent 正在操作' : '当前会话' }}</span>
      <button class="icon-button" title="关闭浏览器面板" aria-label="关闭浏览器面板" @click="emit('close')"><X :size="16" /></button>
    </header>
    <div class="browser-tab-strip" role="tablist" aria-label="浏览器标签页">
      <div v-for="item in state.tabs" :key="item.id" class="browser-tab" :class="{ active: item.id === state.activeTabId }">
        <button role="tab" :aria-selected="item.id === state.activeTabId" :title="item.title" @click="command({ action: 'select', tab_id: item.id })">{{ item.title }}</button>
        <button class="browser-tab-close" :title="`关闭 ${item.title}`" :aria-label="`关闭 ${item.title}`" @click="command({ action: 'close', tab_id: item.id })"><X :size="12" /></button>
      </div>
      <button class="icon-button" title="新标签页" aria-label="新标签页" :disabled="state.tabs.length >= 6" @click="command({ action: 'open' })"><Plus :size="15" /></button>
    </div>
    <form class="browser-navigation" @submit.prevent="navigate">
      <button type="button" class="icon-button" title="后退" aria-label="后退" :disabled="!tab?.canGoBack || state.busy" @click="command({ action: 'back' })"><ArrowLeft :size="15" /></button>
      <button type="button" class="icon-button" title="前进" aria-label="前进" :disabled="!tab?.canGoForward || state.busy" @click="command({ action: 'forward' })"><ArrowRight :size="15" /></button>
      <button type="button" class="icon-button" title="刷新" aria-label="刷新页面" :disabled="!tab || state.busy" @click="command({ action: 'reload' })"><RotateCw :size="14" :class="{ spin: tab?.loading }" /></button>
      <input v-model="address" aria-label="浏览器地址" placeholder="输入网址或 localhost 地址" spellcheck="false" autocomplete="off">
    </form>
    <div class="browser-takeover-bar">
      <span>{{ state.paused ? '你可以操作页面，恢复后 Agent 可继续。' : 'Agent 在此浏览，不占用系统鼠标。' }}</span>
      <button type="button" @click="togglePause"><Play v-if="state.paused" :size="13" /><Pause v-else :size="13" />{{ state.paused ? '恢复 Agent' : '接管' }}</button>
    </div>
    <p v-if="error || tab?.error" class="browser-error" role="alert">{{ error || tab?.error }}</p>
    <div ref="viewport" class="browser-viewport">
      <div v-if="!tab || tab.url === 'about:blank'" class="browser-empty"><Globe2 :size="30" /><strong>会话专属浏览器</strong><span>输入网址，或让 Agent 打开页面</span></div>
    </div>
  </aside>
</template>

<style scoped>
.conversation-browser-panel { display: flex; flex: 0 0 clamp(360px, 44vw, 680px); min-width: 0; height: 100%; flex-direction: column; border-left: 1px solid var(--border); background: var(--surface); container-type: inline-size; }
.browser-panel-header { display: flex; align-items: center; gap: 8px; height: 48px; padding: 0 12px 0 16px; flex: none; font-size: 13px; }
.browser-panel-header > svg { flex-shrink: 0; color: var(--text-secondary); }
.browser-panel-header strong { font-weight: 550; }
.browser-agent-state { margin-left: auto; min-width: 0; color: var(--text-muted); font-size: 11px; display: flex; align-items: center; gap: 5px; white-space: nowrap; }
.browser-tab-strip { display: flex; align-items: center; gap: 4px; padding: 4px 10px 7px; min-height: 40px; overflow-x: auto; flex: none; border-bottom: 1px solid var(--border); scrollbar-width: thin; }
.browser-tab { display: flex; align-items: center; min-width: 80px; max-width: 180px; flex: 1; padding-right: 3px; border-radius: 8px; }
.browser-tab:hover { background: var(--surface-hover); }
.browser-tab.active { background: var(--surface-hover); color: var(--text); }
.browser-tab button { min-width: 0; padding: 8px; background: transparent; color: var(--text-muted); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; cursor: pointer; }
.browser-tab.active button { color: var(--text); }
.browser-tab button:first-child { flex: 1; text-align: left; }
.browser-tab .browser-tab-close { display: grid; place-items: center; width: 24px; height: 24px; padding: 0; flex: none; border-radius: 5px; }
.browser-tab-close:hover { background: var(--surface-active); }
.browser-navigation { display: flex; align-items: center; gap: 3px; padding: 10px; flex: none; }
.browser-navigation input { flex: 1; min-width: 0; height: 32px; padding: 6px 10px; border: 1px solid var(--border); border-radius: 8px; background: transparent; font: 12px var(--font-sans); color: var(--text); }
.browser-navigation input::placeholder { color: var(--text-muted); }
.browser-takeover-bar { display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 0 12px 10px 16px; flex: none; color: var(--text-muted); font-size: 11px; line-height: 1.5; border-bottom: 1px solid var(--border); }
.browser-takeover-bar button { display: flex; align-items: center; gap: 5px; min-height: 28px; padding: 5px 8px; color: var(--text); background: transparent; border: 1px solid var(--border); border-radius: 7px; white-space: nowrap; cursor: pointer; }
.browser-takeover-bar button:hover { background: var(--surface-hover); }
.browser-error { padding: 10px 16px; margin: 0; font-size: 12px; line-height: 1.6; color: var(--danger); overflow-wrap: anywhere; }
.browser-viewport { position: relative; flex: 1; min-height: 0; background: white; }
.browser-empty { height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; padding: 24px; font-size: 12px; line-height: 1.6; text-align: center; color: var(--text-muted); background: var(--surface); }
.browser-empty > strong { margin-top: 2px; color: var(--text-secondary); font-size: 14px; font-weight: 500; }
.conversation-browser-panel .icon-button { width: 30px; height: 30px; flex-shrink: 0; border-radius: 7px; }
.conversation-browser-panel :is(button, input):focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.conversation-browser-panel button:disabled { opacity: .4; cursor: default; }
@container (max-width: 360px) {
  .browser-panel-header { padding-left: 12px; }
  .browser-agent-state { font-size: 10px; }
  .browser-takeover-bar { align-items: flex-start; padding-left: 12px; }
  .browser-takeover-bar > span { max-width: 180px; }
}
@media (max-width: 1000px) { .conversation-browser-panel { flex-basis: 48%; } }
</style>
