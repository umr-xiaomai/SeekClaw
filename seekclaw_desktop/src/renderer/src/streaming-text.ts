export interface StreamingTextOptions {
  initialText: string
  onText: (text: string) => void
  now: () => number
  requestFrame: (callback: FrameRequestCallback) => number
  cancelFrame: (handle: number) => void
}

interface AppendWindow {
  length: number
  receivedAt: number
  duration: number
}

const MIN_UPDATE_INTERVAL_MS = 32
const TAIL_GRACE_MS = 80
const MAX_BUFFERED_UNITS = 512

/**
 * Smooth only appended text. Independent playback windows keep later chunks from
 * restarting earlier progress; their 100–200ms duration bounds visual latency.
 * The caller already displays initialText. flush() publishes all remaining text;
 * dispose() cancels work without publishing stale text after an identity change.
 */
export function createStreamingText(options: StreamingTextOptions) {
  const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' })
  let target = options.initialText
  let shown = target.toWellFormed()
  let shownEnd = target.length
  let settledUnits = target.length
  let windows: AppendWindow[] = []
  let boundaries = segmentEnds(target)
  let frame: number | undefined
  let disposed = false
  let lastAppendAt: number | undefined
  let lastPublishedAt = options.now()
  let cadence = 100

  if (shown !== target) options.onText(shown)

  function segmentEnds(text: string, offset = 0): number[] {
    return Array.from(segmenter.segment(text), (part) => offset + part.index + part.segment.length)
  }

  function cancel(): void {
    if (frame !== undefined) options.cancelFrame(frame)
    frame = undefined
  }

  function publish(end: number, time: number): void {
    shownEnd = end
    const text = target.slice(0, end).toWellFormed()
    if (text === shown) return
    shown = text
    lastPublishedAt = time
    options.onText(text)
  }

  function progressAt(time: number): number {
    let progress = settledUnits
    windows = windows.filter((window) => {
      const fraction = Math.max(0, Math.min(1, (time - window.receivedAt) / window.duration))
      progress += window.length * fraction
      if (fraction < 1) return true
      settledUnits += window.length
      return false
    })
    return progress
  }

  function safeEnd(): number {
    if (!target) return 0
    const last = target.charCodeAt(target.length - 1)
    const previous = target.charCodeAt(target.length - 2)
    const highSurrogate = last >= 0xd800 && last <= 0xdbff
    const unmatchedLowSurrogate = last >= 0xdc00 && last <= 0xdfff
      && !(previous >= 0xd800 && previous <= 0xdbff)
    // These suffixes cannot be safely consumed yet. A later append or flush
    // releases them; no animation frames are needed while waiting for that input.
    if (highSurrogate || unmatchedLowSurrogate || last === 0x200d) return boundaries.at(-2) ?? 0
    return target.length
  }

  function boundaryBefore(end: number): number {
    let low = 0
    let high = boundaries.length
    while (low < high) {
      const middle = (low + high) >>> 1
      if (boundaries[middle]! <= end) low = middle + 1
      else high = middle
    }
    return boundaries[low - 1] ?? 0
  }

  function schedule(): void {
    if (!disposed && frame === undefined && safeEnd() > shownEnd) frame = options.requestFrame(tick)
  }

  function tick(): void {
    frame = undefined
    if (disposed) return
    const time = options.now()
    const progress = progressAt(time)
    let end = safeEnd()
    if (lastAppendAt !== undefined && time - lastAppendAt < TAIL_GRACE_MS) {
      end = Math.min(end, boundaries.at(-2) ?? 0)
    }
    const catchUp = target.length - MAX_BUFFERED_UNITS
    end = boundaryBefore(Math.min(end, Math.max(progress, catchUp)))
    if (end > shownEnd && time - lastPublishedAt >= MIN_UPDATE_INTERVAL_MS) publish(end, time)
    schedule()
  }

  function reset(text: string): void {
    cancel()
    target = text
    boundaries = segmentEnds(text)
    settledUnits = text.length
    windows = []
    cadence = 100
    lastAppendAt = undefined
    publish(text.length, options.now())
  }

  function update(text: string, animate: boolean): void {
    if (disposed) return
    if (!animate || !text.startsWith(target)) {
      reset(text)
      return
    }
    if (text === target) return

    const time = options.now()
    progressAt(time)
    if (lastAppendAt !== undefined) {
      const interval = Math.max(16, Math.min(600, time - lastAppendAt))
      cadence = cadence * 0.75 + interval * 0.25
    }
    windows.push({ length: text.length - target.length, receivedAt: time,
      duration: Math.max(100, Math.min(200, cadence + 40)) })
    lastAppendAt = time

    // Appends can extend the previous last grapheme (emoji, accents or CRLF).
    // Re-segment only that suffix so a long existing answer is not scanned anew.
    const suffixStart = boundaries.length > 1 ? boundaries[boundaries.length - 2]! : 0
    if (boundaries.length) boundaries.pop()
    target = text
    // A provider can deliver a whole large answer at once; avoid spreading its
    // boundaries as arguments, which exceeds the engine's call-argument limit.
    for (const end of segmentEnds(text.slice(suffixStart), suffixStart)) boundaries.push(end)
    schedule()
  }

  function flush(): void {
    if (!disposed) reset(target)
  }

  function dispose(): void {
    disposed = true
    cancel()
    windows = []
  }

  return { update, flush, dispose }
}
