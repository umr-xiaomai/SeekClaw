<script setup lang="ts">
import { ArrowLeft, ArrowRight, CircleAlert, Globe2, LoaderCircle, Plus, RotateCw, Send, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { BrowserCommand, ConversationBrowserState } from '../../../shared/browser'
import type { RemoteBrowserFrame, RemoteBrowserInput } from '../../../shared/web-server'

const props = withDefaults(defineProps<{
  state: ConversationBrowserState
  width?: number
}>(), {
  width: 520
})
const emit = defineEmits<{
  close: []
  changed: [state: ConversationBrowserState]
  resize: [width: number]
}>()

const MIN_PANEL_WIDTH = 320
const MAX_PANEL_WIDTH = 1400
const DEFAULT_PANEL_WIDTH = 520

const panelRef = ref<HTMLElement | null>(null)
const viewport = ref<HTMLElement | null>(null)
const remoteCanvas = ref<HTMLCanvasElement | null>(null)
const isRemoteStream = computed(() => Boolean(window.seekclaw.browser.subscribeStream))
const remoteTextDraft = ref('')
const address = ref('')
const error = ref('')
const resizing = ref(false)
const frameSize = ref({ width: 520, height: 680 })
let stopRemoteStream: (() => void) | null = null
let streamResizeTimer: number | null = null
let lastStreamKey = ''
const tab = computed(() => props.state.tabs.find(tab => tab.id === props.state.activeTabId))
const activeError = computed(() => tab.value?.error || error.value || '')
const panelStyle = computed(() => ({
  '--browser-panel-width': `${props.width}px`
}))
const parsedError = computed(() => {
  const raw = activeError.value
  const stripped = raw
    .replace(/^Error invoking remote method '[^']+':\s*/i, '')
    .replace(/^Error:\s*/i, '')
    .trim()
  const codeMatch = stripped.match(/\b(ERR_[A-Z0-9_]+)\b/)
  const code = codeMatch?.[1]

  if (code === 'ERR_NAME_NOT_RESOLVED') {
    return {
      title: '无法访问此网站',
      description: '找不到服务器 IP 地址，请检查域名拼写是否正确或网络连接是否正常。',
      code
    }
  }
  if (code === 'ERR_CONNECTION_REFUSED') {
    return {
      title: '连接被拒绝',
      description: '目标服务器拒绝了连接请求，请确认对应服务已启动并监听端口。',
      code
    }
  }
  if (code === 'ERR_CONNECTION_TIMED_OUT' || code === 'ERR_TIMED_OUT') {
    return {
      title: '连接超时',
      description: '服务器响应时间过长，请检查网络状况或稍后重试。',
      code
    }
  }
  if (code === 'ERR_INTERNET_DISCONNECTED' || code === 'ERR_NAME_RESOLUTION_FAILED') {
    return {
      title: '未连接到网络',
      description: '当前设备似乎已断开网络连接，请检查网络后重试。',
      code
    }
  }
  if (code === 'ERR_ADDRESS_UNREACHABLE' || code === 'ERR_CONNECTION_RESET' || code === 'ERR_CONNECTION_CLOSED') {
    return {
      title: '无法连接到服务器',
      description: '目标地址当前不可达或连接被中断，请稍后重试。',
      code
    }
  }
  if (code?.startsWith('ERR_CERT_') || code === 'ERR_SSL_PROTOCOL_ERROR') {
    return {
      title: '安全连接失败',
      description: '该站点的 SSL/TLS 证书无效或握手失败，出于安全原因未能建立连接。',
      code
    }
  }
  if (stripped.includes('HTTP(S)') || stripped.includes('Invalid URL')) {
    return {
      title: '网址格式无效',
      description: '请输入有效的 HTTP(S) 网址或本地开发地址（例如 localhost:3000）。',
      code
    }
  }
  return {
    title: '页面加载失败',
    description: stripped || '加载目标页面时发生未知错误，请检查网址后重试。',
    code
  }
})

let resizeObserver: ResizeObserver | undefined
let mutationObserver: MutationObserver | undefined
let frame = 0
let disposed = false
let overlayVisible = false
let resizeStartX = 0
let resizeStartWidth = 0
let resizeRafId: number | null = null
let pendingWidth = props.width

async function command(cmd: BrowserCommand): Promise<void> {
  error.value = ''
  try {
    const nextState = await window.seekclaw.browser.command(props.state.scope, cmd)
    emit('changed', nextState)
    if (cmd.action === 'close' && nextState.tabs.length === 0) {
      emit('close')
    }
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}
function navigate(): void {
  const url = address.value.trim()
  if (url) void command({ action: tab.value ? 'navigate' : 'open', url: /^https?:\/\//i.test(url) ? url : `https://${url}` })
}
function retryCurrentPage(): void {
  error.value = ''
  if (address.value.trim()) {
    navigate()
  } else {
    void command({ action: 'reload' })
  }
}
function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  if (resizeRafId !== null) {
    cancelAnimationFrame(resizeRafId)
    resizeRafId = null
  }
  emit('resize', pendingWidth)
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
  void nextTick(() => {
    if (panelRef.value) {
      panelRef.value.style.setProperty('--browser-panel-width', `${props.width}px`)
    }
    updateLayout()
  })
}
function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const viewportWidth = window.innerWidth
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(viewportWidth * 0.85)))
  const nextWidth = Math.round(resizeStartWidth + resizeStartX - event.clientX)
  if (nextWidth <= MIN_PANEL_WIDTH - 40) {
    stopResize()
    emit('close')
    return
  }
  const clampedWidth = Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth))
  pendingWidth = clampedWidth
  if (panelRef.value) {
    panelRef.value.style.setProperty('--browser-panel-width', `${clampedWidth}px`)
  }
  updateLayout()
  if (resizeRafId === null) {
    resizeRafId = requestAnimationFrame(() => {
      resizeRafId = null
      emit('resize', pendingWidth)
    })
  }
}
function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = panelRef.value?.getBoundingClientRect().width || props.width
  pendingWidth = resizeStartWidth
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}
function resetPanelWidth(): void {
  pendingWidth = DEFAULT_PANEL_WIDTH
  emit('resize', DEFAULT_PANEL_WIDTH)
  void nextTick(() => {
    if (panelRef.value) {
      panelRef.value.style.setProperty('--browser-panel-width', `${props.width}px`)
    }
    updateLayout()
  })
}
function updateLayout(): void {
  if (disposed || frame) return
  frame = requestAnimationFrame(() => {
    frame = 0
    if (isRemoteStream.value) {
      syncRemoteStream()
      return
    }
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
      visible: !!tab.value && tab.value.url !== 'about:blank' && !activeError.value && rect.width > 0 && rect.height > 0 && !overlay } : null).catch(() => undefined)
  })
}

function drawRemoteFrame(payload: RemoteBrowserFrame): void {
  const canvas = remoteCanvas.value
  if (!canvas || payload.scope !== props.state.scope) return
  frameSize.value = { width: payload.width, height: payload.height }
  const img = new Image()
  img.onload = () => {
    if (disposed || !remoteCanvas.value) return
    if (remoteCanvas.value.width !== img.naturalWidth || remoteCanvas.value.height !== img.naturalHeight) {
      remoteCanvas.value.width = img.naturalWidth
      remoteCanvas.value.height = img.naturalHeight
    }
    const ctx = remoteCanvas.value.getContext('2d')
    if (ctx) {
      ctx.drawImage(img, 0, 0)
    }
  }
  img.src = `data:image/jpeg;base64,${payload.data}`
}

function stopCurrentRemoteStream(): void {
  if (streamResizeTimer !== null) {
    window.clearTimeout(streamResizeTimer)
    streamResizeTimer = null
  }
  if (stopRemoteStream) {
    stopRemoteStream()
    stopRemoteStream = null
  }
  lastStreamKey = ''
}

function syncRemoteStream(): void {
  if (!isRemoteStream.value || !window.seekclaw.browser.subscribeStream) return
  const shouldStream = Boolean(tab.value && tab.value.url !== 'about:blank' && !activeError.value)
  if (!shouldStream) {
    stopCurrentRemoteStream()
    return
  }
  const rect = viewport.value?.getBoundingClientRect()
  const width = Math.max(280, Math.round(rect?.width || 520))
  const height = Math.max(220, Math.round(rect?.height || 680))
  const key = `${props.state.scope}:${width}x${height}`
  if (stopRemoteStream && key === lastStreamKey) return

  const subscribe = (): void => {
    if (disposed || !window.seekclaw.browser.subscribeStream) return
    stopCurrentRemoteStream()
    lastStreamKey = key
    stopRemoteStream = window.seekclaw.browser.subscribeStream(props.state.scope, { width, height }, drawRemoteFrame)
  }

  if (stopRemoteStream) {
    if (streamResizeTimer !== null) window.clearTimeout(streamResizeTimer)
    streamResizeTimer = window.setTimeout(() => {
      streamResizeTimer = null
      subscribe()
    }, 150)
    return
  }

  subscribe()
}

function mapRemoteCoords(clientX: number, clientY: number): { x: number; y: number } | null {
  const canvas = remoteCanvas.value
  if (!canvas) return null
  const rect = canvas.getBoundingClientRect()
  if (rect.width <= 0 || rect.height <= 0) return null
  const scaleX = (frameSize.value.width || rect.width) / rect.width
  const scaleY = (frameSize.value.height || rect.height) / rect.height
  return {
    x: Math.max(0, Math.round((clientX - rect.left) * scaleX)),
    y: Math.max(0, Math.round((clientY - rect.top) * scaleY))
  }
}

function sendRemoteInput(input: RemoteBrowserInput): void {
  void window.seekclaw.browser.sendRemoteInput?.(props.state.scope, input).catch(() => undefined)
}

function pointerButtonName(button: number): 'left' | 'right' | 'middle' {
  if (button === 2) return 'right'
  if (button === 1) return 'middle'
  return 'left'
}

function onCanvasPointerDown(event: PointerEvent): void {
  remoteCanvas.value?.focus({ preventScroll: true })
  const coords = mapRemoteCoords(event.clientX, event.clientY)
  if (!coords) return
  sendRemoteInput({
    type: 'mousePressed',
    x: coords.x,
    y: coords.y,
    button: pointerButtonName(event.button),
    clickCount: Math.max(1, event.detail || 1)
  })
}

function onCanvasPointerMove(event: PointerEvent): void {
  const coords = mapRemoteCoords(event.clientX, event.clientY)
  if (!coords) return
  sendRemoteInput({
    type: 'mouseMoved',
    x: coords.x,
    y: coords.y,
    button: (event.buttons & 1) ? 'left' : (event.buttons & 2) ? 'right' : 'none'
  })
}

function onCanvasPointerUp(event: PointerEvent): void {
  const coords = mapRemoteCoords(event.clientX, event.clientY)
  if (!coords) return
  sendRemoteInput({
    type: 'mouseReleased',
    x: coords.x,
    y: coords.y,
    button: pointerButtonName(event.button),
    clickCount: 1
  })
}

function onCanvasWheel(event: WheelEvent): void {
  const coords = mapRemoteCoords(event.clientX, event.clientY)
  if (!coords) return
  sendRemoteInput({
    type: 'mouseWheel',
    x: coords.x,
    y: coords.y,
    deltaX: Math.round(event.deltaX),
    deltaY: Math.round(event.deltaY)
  })
}

function onCanvasKeyDown(event: KeyboardEvent): void {
  if (event.isComposing) return
  const modifiers = (event.altKey ? 1 : 0) | (event.ctrlKey ? 2 : 0) | (event.metaKey ? 4 : 0) | (event.shiftKey ? 8 : 0)
  const text = event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey
    ? event.key
    : event.key === 'Enter'
      ? '\r'
      : undefined
  if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Backspace', 'Tab', ' ', 'Enter'].includes(event.key)) {
    event.preventDefault()
  }
  sendRemoteInput({
    type: 'keyDown',
    key: event.key,
    code: event.code,
    keyCode: event.keyCode || 0,
    text,
    modifiers
  })
}

function onCanvasKeyUp(event: KeyboardEvent): void {
  if (event.isComposing) return
  const modifiers = (event.altKey ? 1 : 0) | (event.ctrlKey ? 2 : 0) | (event.metaKey ? 4 : 0) | (event.shiftKey ? 8 : 0)
  sendRemoteInput({
    type: 'keyUp',
    key: event.key,
    code: event.code,
    keyCode: event.keyCode || 0,
    modifiers
  })
}

function sendRemoteTextInput(): void {
  const text = remoteTextDraft.value
  if (!text) return
  remoteTextDraft.value = ''
  sendRemoteInput({ type: 'insertText', text })
}
const hadTabs = ref(props.state.tabs.length > 0)
watch(() => [props.state.tabs.length, props.state.busy] as const, ([len, busy]) => {
  if (len > 0) {
    hadTabs.value = true
  } else if (hadTabs.value && !busy) {
    hadTabs.value = false
    emit('close')
  }
})
watch(() => tab.value?.url, url => { address.value = url === 'about:blank' ? '' : url || '' }, { immediate: true })
watch(() => [props.state.scope, props.state.activeTabId, tab.value?.url, activeError.value], () => void nextTick(updateLayout))
onMounted(() => {
  resizeObserver = new ResizeObserver(updateLayout)
  if (viewport.value) resizeObserver.observe(viewport.value)
  if (!isRemoteStream.value) {
    mutationObserver = new MutationObserver(updateLayout)
    mutationObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden', 'open'] })
  }
  window.addEventListener('resize', updateLayout)
  updateLayout()
})
onBeforeUnmount(() => {
  disposed = true
  stopResize()
  stopCurrentRemoteStream()
  cancelAnimationFrame(frame)
  resizeObserver?.disconnect()
  mutationObserver?.disconnect()
  window.removeEventListener('resize', updateLayout)
  if (!isRemoteStream.value) {
    void window.seekclaw.browser.layout(null).catch(() => undefined)
    void window.seekclaw.browser.overlay(false).catch(() => undefined)
  }
})
</script>

<template>
  <aside ref="panelRef" class="conversation-browser-panel" :class="{ 'is-resizing': resizing }" :style="panelStyle" aria-label="会话浏览器">
    <div
      class="browser-panel-resize-handle"
      role="separator"
      aria-label="调整浏览器面板宽度"
      aria-orientation="vertical"
      @pointerdown="startResize"
      @dblclick="resetPanelWidth"
    />
    <header class="browser-panel-header">
      <Globe2 :size="16" /><strong>浏览器</strong>
      <span class="browser-agent-state" role="status"><LoaderCircle v-if="state.busy" :size="12" class="spin" />{{ state.busy ? 'Agent 正在操作' : '当前会话' }}</span>
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
      <button type="button" class="icon-button" title="后退" aria-label="后退" :disabled="!tab?.canGoBack" @click="command({ action: 'back' })"><ArrowLeft :size="15" /></button>
      <button type="button" class="icon-button" title="前进" aria-label="前进" :disabled="!tab?.canGoForward" @click="command({ action: 'forward' })"><ArrowRight :size="15" /></button>
      <button type="button" class="icon-button" title="刷新" aria-label="刷新页面" :disabled="!tab" @click="command({ action: 'reload' })"><RotateCw :size="14" :class="{ spin: tab?.loading }" /></button>
      <input v-model="address" aria-label="浏览器地址" placeholder="输入网址或 localhost 地址" spellcheck="false" autocomplete="off">
    </form>
    <div ref="viewport" class="browser-viewport">
      <div v-if="activeError" class="browser-error-page" role="alert">
        <div class="browser-error-icon"><CircleAlert :size="28" /></div>
        <strong>{{ parsedError.title }}</strong>
        <p>{{ parsedError.description }}</p>
        <code v-if="tab?.url && tab.url !== 'about:blank'" class="browser-error-url">{{ tab.url }}</code>
        <span v-if="parsedError.code" class="browser-error-code">{{ parsedError.code }}</span>
        <button type="button" class="browser-retry-btn" @click="retryCurrentPage"><RotateCw :size="13" /><span>重新加载</span></button>
      </div>
      <div v-else-if="!tab || tab.url === 'about:blank'" class="browser-empty"><Globe2 :size="30" /><strong>会话专属浏览器</strong><span>输入网址，或让 Agent 打开页面</span></div>
      <canvas
        v-else-if="isRemoteStream"
        ref="remoteCanvas"
        class="browser-remote-canvas"
        tabindex="0"
        @pointerdown="onCanvasPointerDown"
        @pointermove="onCanvasPointerMove"
        @pointerup="onCanvasPointerUp"
        @wheel.prevent="onCanvasWheel"
        @keydown="onCanvasKeyDown"
        @keyup="onCanvasKeyUp"
        @contextmenu.prevent
      />
    </div>
    <form v-if="isRemoteStream && tab && tab.url !== 'about:blank' && !activeError" class="browser-remote-ime-bar" @submit.prevent="sendRemoteTextInput">
      <input v-model="remoteTextDraft" type="text" placeholder="在此输入文本并发送至远程页面（支持中文输入法）" />
      <button type="submit" class="icon-button" title="发送文本到页面" :disabled="!remoteTextDraft">
        <Send :size="13" />
      </button>
    </form>
  </aside>
</template>

<style scoped>
.conversation-browser-panel { position: relative; z-index: 6; display: flex; flex: 0 0 auto; width: min(var(--browser-panel-width, 520px), 100%); min-width: 0; height: 100%; flex-direction: column; border-left: 1px solid var(--border); background: var(--surface); container-type: inline-size; }
.conversation-browser-panel.is-resizing, .conversation-browser-panel.is-resizing * { user-select: none; transition: none !important; }
.browser-panel-resize-handle { position: absolute; z-index: 10; top: 0; bottom: 0; left: 0; width: 8px; cursor: col-resize; }
.browser-panel-resize-handle::after { position: absolute; top: 50%; left: 2px; width: 2px; height: 34px; content: ''; background: var(--border-strong); border-radius: 2px; opacity: 0; transform: translateY(-50%); transition: opacity 140ms ease, background-color 140ms ease; }
.browser-panel-resize-handle:hover::after, .conversation-browser-panel.is-resizing .browser-panel-resize-handle::after { background: var(--accent); opacity: 1; }
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
.browser-navigation { display: flex; align-items: center; gap: 3px; padding: 10px; flex: none; border-bottom: 1px solid var(--border); }
.browser-navigation input { flex: 1; min-width: 0; height: 32px; padding: 6px 10px; border: 1px solid var(--border); border-radius: 8px; background: transparent; font: 12px var(--font-sans); color: var(--text); }
.browser-navigation input::placeholder { color: var(--text-muted); }
.browser-viewport { position: relative; flex: 1; min-height: 0; background: white; }
.browser-remote-canvas { display: block; width: 100%; height: 100%; object-fit: contain; background: #ffffff; cursor: default; outline: none; touch-action: none; }
.browser-remote-ime-bar { display: flex; align-items: center; gap: 6px; padding: 6px 10px; border-top: 1px solid var(--border); background: var(--surface-subtle); }
.browser-remote-ime-bar input { flex: 1; min-width: 0; height: 28px; padding: 4px 8px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface); color: var(--text); font-size: 12px; }
.browser-empty, .browser-error-page { height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; padding: 28px; font-size: 12px; line-height: 1.65; text-align: center; color: var(--text-muted); background: var(--surface); }
.browser-empty > strong, .browser-error-page > strong { margin-top: 2px; color: var(--text); font-size: 15px; font-weight: 600; }
.browser-error-page > p { margin: 0; max-width: 340px; color: var(--text-secondary); font-size: 12.5px; overflow-wrap: anywhere; }
.browser-error-icon { display: grid; place-items: center; width: 48px; height: 48px; border-radius: 12px; color: var(--danger); background: color-mix(in srgb, var(--danger) 12%, transparent); }
.browser-error-url { max-width: min(100%, 360px); padding: 4px 10px; border-radius: 6px; background: var(--surface-subtle); border: 1px solid var(--border); color: var(--text-secondary); font-family: var(--font-mono); font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.browser-error-code { font-family: var(--font-mono); font-size: 11px; color: var(--text-muted); letter-spacing: 0.02em; }
.browser-retry-btn { display: inline-flex; align-items: center; gap: 6px; margin-top: 6px; min-height: 32px; padding: 6px 14px; border-radius: 8px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); font-size: 12px; font-weight: 500; cursor: pointer; transition: background 140ms ease, border-color 140ms ease; }
.browser-retry-btn:hover { background: var(--surface-hover); border-color: var(--border-strong); }
.conversation-browser-panel .icon-button { width: 30px; height: 30px; flex-shrink: 0; border-radius: 7px; }
.conversation-browser-panel :is(button, input):focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.conversation-browser-panel button:disabled { opacity: .4; cursor: default; }
@container (max-width: 360px) {
  .browser-panel-header { padding-left: 12px; }
  .browser-agent-state { font-size: 10px; }
}
</style>
