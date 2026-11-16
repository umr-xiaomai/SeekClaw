import { createRenderer, defineComponent, h, nextTick, ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useStreamingText } from './use-streaming-text'

interface TestNode { text: string; children: TestNode[] }
const node = (text = ''): TestNode => ({ text, children: [] })
const renderer = createRenderer<TestNode, TestNode>({
  createElement: () => node(), createText: node, createComment: node,
  insert: (child, parent) => { parent.children.push(child) }, remove: () => {},
  setText: (element, text) => { element.text = text },
  setElementText: (element, text) => { element.text = text },
  parentNode: () => null, nextSibling: () => null, patchProp: () => {}
})

let unmount: (() => void) | undefined
afterEach(() => { unmount?.(); unmount = undefined; vi.restoreAllMocks(); vi.unstubAllGlobals() })

function setup() {
  const frames = new Map<number, FrameRequestCallback>()
  const motionListeners = new Set<() => void>()
  const visibilityListeners = new Set<() => void>()
  const media = {
    matches: false,
    addEventListener: (_event: string, listener: () => void) => motionListeners.add(listener),
    removeEventListener: (_event: string, listener: () => void) => motionListeners.delete(listener)
  }
  const document = {
    visibilityState: 'visible',
    addEventListener: (_event: string, listener: () => void) => visibilityListeners.add(listener),
    removeEventListener: (_event: string, listener: () => void) => visibilityListeners.delete(listener)
  }
  let frameId = 0
  vi.stubGlobal('window', { matchMedia: () => media })
  vi.stubGlobal('document', document)
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frames.set(++frameId, callback); return frameId })
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id))
  const content = ref('已经收到的文字。')
  const streaming = ref(true)
  const root = node()
  const app = renderer.createApp(defineComponent({ setup() {
    const displayed = useStreamingText(() => content.value, () => streaming.value)
    return () => h('span', displayed.value)
  } }))
  app.mount(root)
  unmount = () => app.unmount()
  return { content, streaming, frames, media, document, motionListeners, visibilityListeners, text: () => root.children[0]!.text }
}

describe('streaming text view lifecycle', () => {
  it('shows existing text immediately, buffers new text, and flushes when the turn stops', async () => {
    const view = setup()
    expect(view.text()).toBe(view.content.value)
    view.content.value += '这段新文字正在等待平滑显示。'
    await nextTick()
    expect(view.text()).not.toBe(view.content.value)
    expect(view.frames.size).toBeGreaterThan(0)
    view.streaming.value = false
    await nextTick()
    expect(view.text()).toBe(view.content.value)
    expect(view.frames.size).toBe(0)
  })

  it('flushes immediately for reduced motion and while the document is hidden', async () => {
    const view = setup()
    view.content.value += '新的输出。'
    view.media.matches = true
    view.motionListeners.forEach((listener) => listener())
    await nextTick()
    expect(view.text()).toBe(view.content.value)
    expect(view.frames.size).toBe(0)

    view.media.matches = false
    view.motionListeners.forEach((listener) => listener())
    view.content.value += '更多输出。'
    expect(view.frames.size).toBeGreaterThan(0)
    view.document.visibilityState = 'hidden'
    view.visibilityListeners.forEach((listener) => listener())
    await nextTick()
    expect(view.text()).toBe(view.content.value)
    expect(view.frames.size).toBe(0)
  })

  it('cancels queued animation and removes media and visibility listeners on unmount', () => {
    const view = setup()
    view.content.value += '等待中的输出。'
    expect(view.frames.size).toBeGreaterThan(0)
    unmount?.(); unmount = undefined
    expect(view.frames.size).toBe(0)
    expect(view.motionListeners.size).toBe(0)
    expect(view.visibilityListeners.size).toBe(0)
  })
})
