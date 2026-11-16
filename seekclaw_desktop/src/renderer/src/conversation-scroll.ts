interface ScrollTarget {
  scrollHeight: number
  scrollTo(options: ScrollToOptions): void
}

interface ConversationScrollOptions {
  element: () => ScrollTarget | null
  shouldFollow: () => boolean
  afterRender: () => Promise<unknown>
  requestFrame: (callback: FrameRequestCallback) => number
  cancelFrame: (handle: number) => void
}

/** Batches stream updates into one scroll after rendering, using the latest layout. */
export function createConversationScroller(options: ConversationScrollOptions) {
  let pending: Promise<void> | null = null
  let settle: (() => void) | undefined
  let frame: number | undefined
  let forced = false
  let smooth = true
  let disposed = false

  function finish(): void {
    pending = null
    forced = false
    smooth = true
    const resolve = settle
    settle = undefined
    resolve?.()
  }

  function scrollToBottom(animate = false, force = false): Promise<void> {
    if (disposed) return Promise.resolve()
    forced ||= force
    smooth &&= animate
    if (pending) return pending
    pending = new Promise<void>((resolve) => { settle = resolve })
    const completion = pending
    void options.afterRender().then(() => {
      if (disposed) return
      frame = options.requestFrame(() => {
        frame = undefined
        const element = options.element()
        if (element && (forced || options.shouldFollow())) {
          element.scrollTo({ top: element.scrollHeight, behavior: smooth ? 'smooth' : 'instant' })
        }
        finish()
      })
    })
    return completion
  }

  function dispose(): void {
    disposed = true
    if (frame !== undefined) options.cancelFrame(frame)
    finish()
  }

  return { scrollToBottom, dispose }
}
