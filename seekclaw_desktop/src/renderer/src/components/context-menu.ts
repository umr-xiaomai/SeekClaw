import type { Component } from 'vue'

export interface ContextMenuAction {
  type?: 'item'
  id: string
  label: string
  icon?: Component
  disabled?: boolean
  danger?: boolean
  /** Display only a shortcut that the owning view actually implements. */
  shortcut?: string
  children?: ContextMenuEntry[]
}

export interface ContextMenuSeparator {
  type: 'separator'
  id?: string
}

export type ContextMenuEntry = ContextMenuAction | ContextMenuSeparator
export type ContextMenuRect = Pick<DOMRect, 'top' | 'right' | 'bottom' | 'left'>
export type ContextMenuAnchor = { x: number; y: number } | { rect: ContextMenuRect; align?: 'start' | 'end' }

export function isContextMenuAction(item: ContextMenuEntry): item is ContextMenuAction {
  return item.type !== 'separator'
}

/** Roving focus excludes separators and unavailable actions, wrapping at either end. */
export function contextMenuFocusIndex(
  items: readonly ContextMenuEntry[], current: number, direction: 'next' | 'previous' | 'first' | 'last'
): number {
  const enabled = items.flatMap((item, index) => isContextMenuAction(item) && !item.disabled ? [index] : [])
  if (!enabled.length) return -1
  if (direction === 'first') return enabled[0]!
  if (direction === 'last') return enabled.at(-1)!
  const position = enabled.indexOf(current)
  if (position < 0) return direction === 'next' ? enabled[0]! : enabled.at(-1)!
  const step = direction === 'next' ? 1 : -1
  return enabled[(position + step + enabled.length) % enabled.length]!
}

interface MenuSize { width: number; height: number }
interface Viewport { width: number; height: number }
export interface ContextMenuPosition { left: number; top: number; width: number; maxHeight: number }

function clampPosition(left: number, top: number, size: MenuSize, viewport: Viewport): ContextMenuPosition {
  const edge = Math.min(8, viewport.width / 2, viewport.height / 2)
  const width = Math.min(size.width, Math.max(0, viewport.width - edge * 2))
  const maxHeight = Math.max(0, viewport.height - edge * 2)
  const height = Math.min(size.height, maxHeight)
  return {
    left: Math.max(edge, Math.min(left, viewport.width - width - edge)),
    top: Math.max(edge, Math.min(top, viewport.height - height - edge)),
    width, maxHeight
  }
}

/** Position a point menu or an anchored button menu without changing document scroll. */
export function positionContextMenu(
  anchor: ContextMenuAnchor, size: MenuSize, viewport: Viewport
): ContextMenuPosition {
  const edge = 8
  let left: number
  let top: number
  if ('rect' in anchor) {
    left = anchor.align === 'end' ? anchor.rect.right - size.width : anchor.rect.left
    top = anchor.rect.bottom + 4
    if (top + size.height > viewport.height - edge && anchor.rect.top - 4 - size.height >= edge) {
      top = anchor.rect.top - 4 - size.height
    }
  } else {
    left = anchor.x
    top = anchor.y
    if (left + size.width > viewport.width - edge) left -= size.width
    if (top + size.height > viewport.height - edge) top -= size.height
  }
  return clampPosition(left, top, size, viewport)
}

export function positionContextSubmenu(
  parent: ContextMenuRect, size: MenuSize, viewport: Viewport
): ContextMenuPosition {
  const left = parent.right + 4 + size.width <= viewport.width - 8
    ? parent.right + 4 : parent.left - size.width - 4
  return clampPosition(left, parent.top - 4, size, viewport)
}
