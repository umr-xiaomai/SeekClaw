import { describe, expect, it, vi } from 'vitest'
import { createConversationScroller } from './conversation-scroll'

function setup() {
  let follow = true
  let nextFrame: FrameRequestCallback | undefined
  const target = { scrollHeight: 100, scrollTo: vi.fn() }
  const cancelFrame = vi.fn()
  const scroller = createConversationScroller({
    element: () => target,
    shouldFollow: () => follow,
    afterRender: () => Promise.resolve(),
    requestFrame: (callback) => { nextFrame = callback; return 1 },
    cancelFrame
  })
  return {
    ...scroller, target, cancelFrame,
    setFollow: (value: boolean) => { follow = value },
    flush: async () => { await Promise.resolve(); nextFrame?.(0); nextFrame = undefined }
  }
}

describe('conversation scroll scheduling', () => {
  it('coalesces a burst of tokens and reads the final rendered height', async () => {
    const scroll = setup()
    const first = scroll.scrollToBottom()
    const second = scroll.scrollToBottom()
    expect(first).toBe(second)
    scroll.target.scrollHeight = 240
    await scroll.flush()
    await first
    expect(scroll.target.scrollTo).toHaveBeenCalledExactlyOnceWith({ top: 240, behavior: 'instant' })
  })

  it('does not pull the user back down if they stop following before the frame', async () => {
    const scroll = setup()
    const pending = scroll.scrollToBottom()
    scroll.setFollow(false)
    await scroll.flush()
    await pending
    expect(scroll.target.scrollTo).not.toHaveBeenCalled()
  })

  it('allows explicit navigation to force following while stream updates remain instant', async () => {
    const scroll = setup()
    scroll.setFollow(false)
    const pending = scroll.scrollToBottom(true, true)
    scroll.scrollToBottom()
    await scroll.flush()
    await pending
    expect(scroll.target.scrollTo).toHaveBeenCalledExactlyOnceWith({ top: 100, behavior: 'instant' })
  })

  it('cancels a queued scroll on unmount and releases its caller', async () => {
    const scroll = setup()
    const pending = scroll.scrollToBottom()
    await Promise.resolve()
    scroll.dispose()
    await pending
    expect(scroll.cancelFrame).toHaveBeenCalledWith(1)
    expect(scroll.target.scrollTo).not.toHaveBeenCalled()
    await scroll.scrollToBottom()
  })
})
