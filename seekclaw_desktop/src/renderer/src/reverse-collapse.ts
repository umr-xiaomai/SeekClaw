export interface VisualLineBand {
  top: number
  bottom: number
  left: number
  right: number
  bandTop: number
  bandHeight: number
}

export interface CollapseSnapshot {
  clone: HTMLElement
  width: number
  height: number
  lines: VisualLineBand[]
}

export interface ReverseCollapseController {
  cancel: () => void
}

const DEFAULT_DURATION_MS = 500
const FEATHER_PX = 44

function isTransientStatusNode(el: Element): boolean {
  if (el.matches('.live-status-row, .response-placeholder, .process-thinking-row, .process-tail-status, footer.assistant-footer, footer.user-footer')) return true
  if (el.matches('.thinking-wrapper') && !el.querySelector('.thinking-content') && el.querySelector('.thinking-toggle.active')) return true
  return false
}

function collectLeafContainers(root: HTMLElement): HTMLElement[] {
  const result: HTMLElement[] = []
  const assistantMessages = root.matches('.assistant-message')
    ? [root]
    : Array.from(root.querySelectorAll<HTMLElement>('.assistant-message'))

  if (assistantMessages.length === 0) {
    for (const child of Array.from(root.children)) {
      if (child instanceof HTMLElement && !isTransientStatusNode(child)) {
        result.push(child)
      }
    }
    return result
  }

  for (const child of Array.from(root.children)) {
    if (!(child instanceof HTMLElement) || isTransientStatusNode(child)) continue
    if (assistantMessages.some((msg) => child.contains(msg))) {
      for (const msg of assistantMessages) {
        if (!child.contains(msg)) continue
        for (const leaf of Array.from(msg.children)) {
          if (leaf instanceof HTMLElement && !isTransientStatusNode(leaf)) {
            result.push(leaf)
          }
        }
      }
    } else {
      result.push(child)
    }
  }
  return result
}

function addTextNodeRects(
  container: HTMLElement,
  originLeft: number,
  originTop: number,
  rects: Array<{ top: number; bottom: number; left: number; right: number }>
): void {
  if (typeof document === 'undefined' || typeof document.createRange !== 'function') return
  const showText = typeof NodeFilter !== 'undefined' ? NodeFilter.SHOW_TEXT : 4
  const walker = document.createTreeWalker(container, showText)
  let node: Node | null
  while ((node = walker.nextNode())) {
    if (!node.nodeValue || !node.nodeValue.trim()) continue
    const parent = node.parentElement
    if (parent && parent.closest('.agent-lifecycle-row, .agent-lifecycle-group, .tool-call > summary, .thinking-toggle')) {
      continue
    }
    const range = document.createRange()
    range.selectNodeContents(node)
    if (typeof range.getClientRects !== 'function') continue
    for (const rect of Array.from(range.getClientRects())) {
      if (rect.width < 1 || rect.height < 1) continue
      rects.push({
        top: rect.top - originTop,
        bottom: rect.bottom - originTop,
        left: rect.left - originLeft,
        right: rect.right - originLeft
      })
    }
  }
}

/**
 * Measures wrapped visual line bands of an open stage container prior to collapsing into
 * `<details class="conversation-process">`, without mutating live text layout.
 */
export function measureVisualLines(
  containers: readonly HTMLElement[],
  originLeft: number,
  originTop: number,
  totalHeight: number
): VisualLineBand[] {
  const rawRects: Array<{ top: number; bottom: number; left: number; right: number }> = []

  for (const container of containers) {
    for (const row of Array.from(container.querySelectorAll<HTMLElement>(
      '.thinking-toggle, .agent-lifecycle-row, .agent-lifecycle-group, .tool-call > summary'
    ))) {
      const r = row.getBoundingClientRect()
      if (r.width > 1 && r.height > 1) {
        rawRects.push({
          top: r.top - originTop,
          bottom: r.bottom - originTop,
          left: r.left - originLeft,
          right: r.right - originLeft
        })
      }
    }
    if (container.matches('.agent-lifecycle')) {
      for (const row of Array.from(container.children)) {
        if (row instanceof HTMLElement && !row.matches('.agent-lifecycle-row, .agent-lifecycle-group')) {
          const r = row.getBoundingClientRect()
          if (r.width > 1 && r.height > 1) {
            rawRects.push({
              top: r.top - originTop,
              bottom: r.bottom - originTop,
              left: r.left - originLeft,
              right: r.right - originLeft
            })
          }
        }
      }
    }
    addTextNodeRects(container, originLeft, originTop, rawRects)
  }

  rawRects.sort((a, b) => a.top - b.top || a.left - b.left)

  const clustered: Array<{ top: number; bottom: number; left: number; right: number; cy: number }> = []
  for (const rect of rawRects) {
    const cy = (rect.top + rect.bottom) / 2
    const last = clustered.at(-1)
    const tolerance = last ? Math.max(8, (last.bottom - last.top) * 0.45) : 9
    if (last && Math.abs(cy - last.cy) < tolerance) {
      last.top = Math.min(last.top, rect.top)
      last.bottom = Math.max(last.bottom, rect.bottom)
      last.left = Math.min(last.left, rect.left)
      last.right = Math.max(last.right, rect.right)
      last.cy = (last.top + last.bottom) / 2
    } else {
      clustered.push({ ...rect, cy })
    }
  }

  const height = Math.max(totalHeight, 1)
  return clustered.map((line, index) => {
    const prev = clustered[index - 1]
    const next = clustered[index + 1]
    const bandTop = index === 0 ? 0 : Math.round((prev!.bottom + line.top) / 2)
    const bandBottom = index === clustered.length - 1 ? Math.ceil(height) : Math.round((line.bottom + next!.top) / 2)
    return {
      top: line.top,
      bottom: line.bottom,
      left: line.left,
      right: line.right,
      bandTop,
      bandHeight: Math.max(1, bandBottom - bandTop)
    }
  })
}

/** Captures an in-flow DOM subtree right before Vue folds it away. */
export function captureCollapseSnapshot(sourceEl: HTMLElement | null): CollapseSnapshot | null {
  if (!sourceEl) return null
  const retained = collectLeafContainers(sourceEl)
  if (retained.length === 0) return null

  const rootRect = sourceEl.getBoundingClientRect()
  if (rootRect.width <= 0 || rootRect.height <= 0) return null

  let originTop = Infinity
  let maxBottom = -Infinity
  for (const el of retained) {
    const r = el.getBoundingClientRect()
    if (r.height <= 0) continue
    originTop = Math.min(originTop, r.top)
    maxBottom = Math.max(maxBottom, r.bottom)
  }
  if (!Number.isFinite(originTop) || maxBottom <= originTop) return null

  const height = maxBottom - originTop
  const lines = measureVisualLines(retained, rootRect.left, originTop, height)

  const clone = sourceEl.cloneNode(true) as HTMLElement
  for (const transient of Array.from(clone.querySelectorAll('.live-status-row, .response-placeholder, .process-thinking-row, .process-tail-status'))) {
    transient.remove()
  }
  for (const wrapper of Array.from(clone.querySelectorAll('.thinking-wrapper'))) {
    if (!wrapper.querySelector('.thinking-content') && wrapper.querySelector('.thinking-toggle.active')) {
      wrapper.remove()
    }
  }
  for (const footer of Array.from(clone.querySelectorAll('footer.assistant-footer'))) {
    footer.remove()
  }

  return {
    clone,
    width: Math.max(rootRect.width, 280),
    height,
    lines
  }
}

const clamp01 = (v: number): number => (v < 0 ? 0 : v > 1 ? 1 : v)
const easeOutCubic = (t: number): number => 1 - Math.pow(1 - t, 3)
const easeInOutQuart = (t: number): number => (t < 0.5 ? 8 * t * t * t * t : 1 - Math.pow(-2 * t + 2, 4) / 2)

/**
 * Animates an exiting block inside `host` by wiping lines backward (right-to-left,
 * bottom line to top line) and smoothly collapsing height within 500ms.
 */
export function animateReverseLineCollapse(
  host: HTMLElement | null,
  snapshot: CollapseSnapshot | null,
  durationMs = DEFAULT_DURATION_MS
): ReverseCollapseController {
  const noop: ReverseCollapseController = { cancel: () => undefined }
  if (!host || !snapshot || snapshot.height <= 0) return noop
  if (typeof window === 'undefined' || window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches) {
    return noop
  }
  if (typeof requestAnimationFrame !== 'function') return noop

  const shell = document.createElement('div')
  shell.className = 'collapse-wipe-shell'
  shell.setAttribute('aria-hidden', 'true')
  shell.style.height = `${snapshot.height}px`

  const body = document.createElement('div')
  body.className = 'collapse-wipe-body'
  body.appendChild(snapshot.clone)
  shell.appendChild(body)
  host.replaceChildren(shell)

  let rafId = 0
  let finished = false
  const cleanup = () => {
    if (finished) return
    finished = true
    if (rafId) cancelAnimationFrame(rafId)
    shell.remove()
  }

  const lines = snapshot.lines
  if (lines.length === 0) {
    const start = performance.now()
    const stepFallback = (now: number) => {
      if (finished) return
      const raw = clamp01((now - start) / durationMs)
      const p = easeInOutQuart(raw)
      const cut = (1 - p) * 124 - 24
      const grad = `linear-gradient(to bottom, #000 0%, #000 ${cut}%, transparent ${cut + 26}%, transparent 100%)`
      body.style.webkitMaskImage = grad
      body.style.maskImage = grad
      shell.style.height = `${Math.max(0, snapshot.height * (1 - p)).toFixed(1)}px`
      shell.style.opacity = String(raw > 0.85 ? 1 - (raw - 0.85) / 0.15 : 1)
      if (raw < 1) {
        rafId = requestAnimationFrame(stepFallback)
      } else {
        cleanup()
      }
    }
    rafId = requestAnimationFrame(stepFallback)
    return { cancel: cleanup }
  }

  const count = lines.length
  const solidStop = Math.ceil(snapshot.width + 64)
  const maskW = solidStop + FEATHER_PX + Math.ceil(snapshot.width + 64)
  const bandGrad = `linear-gradient(90deg, #000 0px, #000 ${solidStop}px, transparent ${solidStop + FEATHER_PX}px, transparent 100%)`

  body.style.webkitMaskImage = lines.map(() => bandGrad).join(', ')
  body.style.maskImage = lines.map(() => bandGrad).join(', ')
  body.style.webkitMaskRepeat = lines.map(() => 'no-repeat').join(', ')
  body.style.maskRepeat = lines.map(() => 'no-repeat').join(', ')
  body.style.webkitMaskSize = lines.map((l) => `${maskW}px ${l.bandHeight}px`).join(', ')
  body.style.maskSize = lines.map((l) => `${maskW}px ${l.bandHeight}px`).join(', ')

  const schedule = lines.map((line, i) => {
    const reverseIdx = count - 1 - i
    const startAt = count === 1 ? 0 : (reverseIdx / Math.max(count, 2)) * 0.56
    const span = i === 0 ? 0.36 : 0.42
    const posStart = Math.ceil(line.right + 6) - solidStop
    const posEnd = Math.floor(Math.min(line.left, 0) - 6) - (solidStop + FEATHER_PX)
    return { line, startAt, endAt: Math.min(0.96, startAt + span), posStart, posEnd }
  })

  const start = performance.now()
  const stepLines = (now: number) => {
    if (finished) return
    const raw = clamp01((now - start) / durationMs)

    const positions = schedule.map(({ line, startAt, endAt, posStart, posEnd }) => {
      const localT = clamp01((raw - startAt) / Math.max(0.05, endAt - startAt))
      const eased = easeOutCubic(localT)
      const x = posStart + (posEnd - posStart) * eased
      return `${x.toFixed(1)}px ${line.bandTop}px`
    })

    const posStr = positions.join(', ')
    body.style.webkitMaskPosition = posStr
    body.style.maskPosition = posStr

    let frameH = 0
    for (const item of schedule) {
      const localT = clamp01((raw - item.startAt) / Math.max(0.05, item.endAt - item.startAt))
      const collapse = easeInOutQuart(clamp01((localT - 0.52) / 0.48))
      frameH += item.line.bandHeight * (1 - collapse)
    }
    shell.style.height = `${Math.max(0, frameH).toFixed(1)}px`
    shell.style.opacity = String(raw > 0.88 ? 1 - (raw - 0.88) / 0.12 : 1)

    if (raw < 1) {
      rafId = requestAnimationFrame(stepLines)
    } else {
      cleanup()
    }
  }

  rafId = requestAnimationFrame(stepLines)
  return { cancel: cleanup }
}
