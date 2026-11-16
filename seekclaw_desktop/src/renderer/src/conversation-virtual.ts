interface ConversationHeights<T> {
  items: readonly T[]
  getHeight: (item: T, index: number) => number | undefined
  estimatedHeight?: number
  /** Distance from the scrolling area's start to the first turn, including padding. */
  contentOffsetTop?: number
}

export interface ConversationVirtualOptions<T> extends ConversationHeights<T> {
  scrollTop: number
  viewportHeight: number
  enabled?: boolean
  threshold?: number
  /** Number of estimated turn heights to retain on either side of the viewport. */
  overscan?: number
}

export interface ConversationVirtualWindow<T> {
  active: boolean
  start: number
  end: number
  topPad: number
  bottomPad: number
  items: readonly T[]
}

function validHeight(height: number | undefined, fallback: number): number {
  return height !== undefined && Number.isFinite(height) && height > 0 ? height : fallback
}

/** Window the same complete turn units that the view measures. */
export function computeConversationVirtualWindow<T>(
  options: ConversationVirtualOptions<T>
): ConversationVirtualWindow<T> {
  const { items, getHeight, scrollTop, viewportHeight } = options
  const total = items.length
  if (options.enabled === false || total <= (options.threshold ?? 60)) {
    return { active: false, start: 0, end: total, topPad: 0, bottomPad: 0, items }
  }

  const estimate = validHeight(options.estimatedHeight, 120)
  const heights = items.map((item, index) => validHeight(getHeight(item, index), estimate))
  const overscan = Math.max(0, options.overscan ?? 12) * estimate
  const viewportTop = Math.max(0, scrollTop - (options.contentOffsetTop ?? 0))
  const targetTop = Math.max(0, viewportTop - overscan)
  let topPad = 0
  let start = 0
  // Keep the final turn mounted even when a previous thread left a larger scroll offset.
  while (start < total - 1 && topPad + heights[start]! < targetTop) {
    topPad += heights[start++]!
  }

  const targetBottom = viewportTop + Math.max(0, viewportHeight) + overscan
  let end = start
  let bottom = topPad
  while (end < total && (bottom < targetBottom || end === start)) bottom += heights[end++]!

  let bottomPad = 0
  for (let index = end; index < total; index++) bottomPad += heights[index]!
  return { active: true, start, end, topPad, bottomPad, items: items.slice(start, end) }
}

export interface ConversationMeasurementOptions<T> extends ConversationHeights<T> {
  scrollTop: number
  following: boolean
  measuredIndex: number
  height: number
}

/**
 * Keep the reader's position when a preceding turn's estimate changes. Call with
 * the old height cache, synchronize this offset, then write the new height.
 * Native scroll anchoring must be disabled while applying this compensation.
 */
export function correctConversationMeasurement<T>(options: ConversationMeasurementOptions<T>): number {
  const { items, getHeight, scrollTop, measuredIndex, height } = options
  if (options.following || measuredIndex < 0 || measuredIndex >= items.length
    || !Number.isFinite(height) || height <= 0) return scrollTop

  const estimate = validHeight(options.estimatedHeight, 120)
  let bottom = options.contentOffsetTop ?? 0
  let previousHeight = estimate
  for (let index = 0; index <= measuredIndex; index++) {
    previousHeight = validHeight(getHeight(items[index]!, index), estimate)
    bottom += previousHeight
  }
  if (bottom > scrollTop) return scrollTop
  return Math.max(0, scrollTop + height - previousHeight)
}
