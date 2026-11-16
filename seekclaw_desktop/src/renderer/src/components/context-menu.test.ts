import { describe, expect, it } from 'vitest'
import {
  contextMenuFocusIndex, positionContextMenu, positionContextSubmenu, type ContextMenuEntry
} from './context-menu'

describe('context menu keyboard navigation', () => {
  const items: ContextMenuEntry[] = [
    { type: 'separator' }, { id: 'unavailable', label: 'Unavailable', disabled: true },
    { id: 'rename', label: 'Rename' }, { type: 'separator' },
    { id: 'more', label: 'More', children: [{ id: 'archive', label: 'Archive' }] }
  ]

  it('starts and ends at enabled actions, including submenu triggers', () => {
    expect(contextMenuFocusIndex(items, -1, 'first')).toBe(2)
    expect(contextMenuFocusIndex(items, -1, 'last')).toBe(4)
  })

  it('skips separators and disabled actions and wraps in both directions', () => {
    expect(contextMenuFocusIndex(items, 2, 'next')).toBe(4)
    expect(contextMenuFocusIndex(items, 4, 'next')).toBe(2)
    expect(contextMenuFocusIndex(items, 2, 'previous')).toBe(4)
    expect(contextMenuFocusIndex(items, 4, 'previous')).toBe(2)
  })

  it('recovers navigation if the focused action was removed or disabled', () => {
    expect(contextMenuFocusIndex(items, 1, 'next')).toBe(2)
    expect(contextMenuFocusIndex(items, 1, 'previous')).toBe(4)
  })

  it('does not invent an actionable focus target for empty or disabled menus', () => {
    expect(contextMenuFocusIndex([], 0, 'next')).toBe(-1)
    expect(contextMenuFocusIndex(items.slice(0, 2), -1, 'first')).toBe(-1)
  })
})

describe('context menu viewport placement', () => {
  const size = { width: 224, height: 200 }
  const viewport = { width: 1000, height: 700 }

  it('uses mouse client coordinates without applying page scroll offsets', () => {
    expect(positionContextMenu({ x: 200, y: 250 }, size, viewport)).toEqual({
      left: 200, top: 250, width: 224, maxHeight: 684
    })
  })

  it('flips a point menu left and up near the window edges', () => {
    expect(positionContextMenu({ x: 980, y: 680 }, size, viewport)).toMatchObject({ left: 756, top: 480 })
  })

  it('places button menus below their anchor and supports trailing-edge alignment', () => {
    const rect = { left: 300, right: 332, top: 100, bottom: 132 }
    expect(positionContextMenu({ rect }, size, viewport)).toMatchObject({ left: 300, top: 136 })
    expect(positionContextMenu({ rect, align: 'end' }, size, viewport)).toMatchObject({ left: 108, top: 136 })
  })

  it('flips button menus above the anchor when that side has room', () => {
    const rect = { left: 300, right: 332, top: 650, bottom: 682 }
    expect(positionContextMenu({ rect }, size, viewport).top).toBe(446)
  })

  it('opens submenus beside the focused row and flips them toward available space', () => {
    expect(positionContextSubmenu({ left: 200, right: 424, top: 100, bottom: 132 }, size, viewport))
      .toMatchObject({ left: 428, top: 96 })
    expect(positionContextSubmenu({ left: 740, right: 964, top: 100, bottom: 132 }, size, viewport))
      .toMatchObject({ left: 512, top: 96 })
  })

  it('clamps tall menus and oversized widths to a small viewport', () => {
    const placement = positionContextMenu({ x: 190, y: 140 }, { width: 300, height: 1000 }, { width: 200, height: 150 })
    expect(placement).toEqual({ left: 8, top: 8, width: 184, maxHeight: 134 })
  })

  it('keeps submenus within the viewport vertically', () => {
    expect(positionContextSubmenu({ left: 200, right: 424, top: 680, bottom: 712 }, size, viewport).top).toBe(492)
    expect(positionContextSubmenu({ left: 200, right: 424, top: -10, bottom: 22 }, size, viewport).top).toBe(8)
  })
})
