import { describe, expect, it } from 'vitest'
import { computeConversationVirtualWindow, correctConversationMeasurement } from './conversation-virtual'

function setup() {
  const items = Array.from({ length: 100 }, (_, index) => `turn-${index}`)
  const heights = new Map<string, number>()
  const getHeight = (item: string) => heights.get(item)
  const options = { items, getHeight, scrollTop: 6000, viewportHeight: 600 }
  return { items, heights, getHeight, options }
}

describe('conversation turn virtualization', () => {
  it('windows complete turns and accounts for all preceding and following heights', () => {
    const { options } = setup()
    const window = computeConversationVirtualWindow(options)

    expect(window).toMatchObject({ active: true, start: 37, end: 67, topPad: 4440, bottomPad: 3960 })
    expect(window.items).toEqual(options.items.slice(37, 67))
    expect(window.topPad + window.items.length * 120 + window.bottomPad).toBe(12000)
  })

  it('keeps reading the same turns when a preceding estimate grows', () => {
    const { options, heights } = setup()
    const before = computeConversationVirtualWindow(options)
    const scrollTop = correctConversationMeasurement({ ...options, measuredIndex: 38, height: 5000, following: false })
    // Apply the new offset before publishing the measured height to the reactive cache.
    heights.set('turn-38', 5000)
    const after = computeConversationVirtualWindow({ ...options, scrollTop })

    expect(scrollTop).toBe(10880)
    expect(before.items).toContain('turn-50')
    expect(after.items).toContain('turn-50')
    expect(scrollTop - (39 * 120 + 4880)).toBe(options.scrollTop - 39 * 120)
  })

  it('compensates shrinking preceding turns using their old cached height', () => {
    const { options, heights } = setup()
    heights.set('turn-10', 800)

    expect(correctConversationMeasurement({ ...options, measuredIndex: 10, height: 200, following: false })).toBe(5400)
  })

  it('does not compensate a visible turn, a following turn or a view pinned to the bottom', () => {
    const { options } = setup()

    expect(correctConversationMeasurement({ ...options, measuredIndex: 50, height: 900, following: false })).toBe(6000)
    expect(correctConversationMeasurement({ ...options, measuredIndex: 70, height: 900, following: false })).toBe(6000)
    expect(correctConversationMeasurement({ ...options, measuredIndex: 10, height: 900, following: true })).toBe(6000)
  })

  it('uses the content offset for both the window and the visibility boundary', () => {
    const { options } = setup()
    const withoutPadding = computeConversationVirtualWindow(options)
    const withPadding = computeConversationVirtualWindow({ ...options, scrollTop: 6042, contentOffsetTop: 42 })

    expect(withPadding).toEqual(withoutPadding)
    expect(correctConversationMeasurement({ ...options, scrollTop: 120, contentOffsetTop: 42,
      measuredIndex: 0, height: 240, following: false })).toBe(120)
    expect(correctConversationMeasurement({ ...options, scrollTop: 162, contentOffsetTop: 42,
      measuredIndex: 0, height: 240, following: false })).toBe(282)
  })

  it('retains the final turn if the old scroll offset exceeds the new conversation height', () => {
    const { options } = setup()
    const window = computeConversationVirtualWindow({ ...options, scrollTop: 100000 })

    expect(window.items).toEqual(['turn-99'])
    expect(window.end).toBe(100)
    expect(window.bottomPad).toBe(0)
  })

  it('renders all turns for small conversations and when search disables virtualization', () => {
    const { options } = setup()

    expect(computeConversationVirtualWindow({ ...options, items: options.items.slice(0, 60) }).active).toBe(false)
    expect(computeConversationVirtualWindow({ ...options, enabled: false })).toMatchObject({
      active: false, start: 0, end: 100, topPad: 0, bottomPad: 0, items: options.items
    })
  })

  it('falls back to estimates for unusable cached heights and ignores invalid measurements', () => {
    const { options, heights } = setup()
    heights.set('turn-0', 0)
    heights.set('turn-1', Number.NaN)
    heights.set('turn-2', Number.POSITIVE_INFINITY)

    expect(computeConversationVirtualWindow(options).topPad).toBe(4440)
    expect(correctConversationMeasurement({ ...options, measuredIndex: -1, height: 50, following: false })).toBe(6000)
    expect(correctConversationMeasurement({ ...options, measuredIndex: 100, height: 50, following: false })).toBe(6000)
    expect(correctConversationMeasurement({ ...options, measuredIndex: 0, height: 0, following: false })).toBe(6000)
  })
})
