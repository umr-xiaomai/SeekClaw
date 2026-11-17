/// <reference types="node" />
import { describe, expect, it } from 'vitest'
import { animateReverseLineCollapse, measureVisualLines } from './reverse-collapse'

function fakeBox(rect: { top: number; bottom: number; left: number; right: number }): DOMRect {
  return {
    top: rect.top,
    bottom: rect.bottom,
    left: rect.left,
    right: rect.right,
    width: rect.right - rect.left,
    height: rect.bottom - rect.top,
    x: rect.left,
    y: rect.top,
    toJSON: () => rect
  }
}

describe('reverse line collapse animation', () => {
  it('clusters text rects into seamless abutting horizontal line bands', () => {
    const fakeTextNode = {
      nodeType: 3,
      nodeValue: '测试文本行',
      parentElement: { closest: () => null }
    }
    const fakeRange = {
      selectNodeContents: () => undefined,
      getClientRects: () => [
        fakeBox({ top: 10, bottom: 28, left: 0, right: 300 }),
        fakeBox({ top: 10, bottom: 28, left: 300, right: 560 }),
        fakeBox({ top: 36, bottom: 54, left: 0, right: 420 })
      ]
    }
    const prevDoc = globalThis.document
    globalThis.document = {
      createRange: () => fakeRange,
      createTreeWalker: () => {
        let returned = false
        return {
          nextNode: () => {
            if (returned) return null
            returned = true
            return fakeTextNode
          }
        }
      }
    } as unknown as Document

    try {
      const container = {
        matches: () => false,
        querySelectorAll: () => []
      } as unknown as HTMLElement

      const bands = measureVisualLines([container], 0, 10, 44)
      expect(bands).toHaveLength(2)
      expect(bands[0]).toMatchObject({ left: 0, right: 560, bandTop: 0 })
      expect(bands[1]!.bandTop).toBe(bands[0]!.bandTop + bands[0]!.bandHeight)
      expect(bands[1]!.bandTop + bands[1]!.bandHeight).toBe(44)
    } finally {
      globalThis.document = prevDoc
    }
  })

  it('no-ops cleanly when host or snapshot is absent', () => {
    const controller = animateReverseLineCollapse(null, null, 500)
    expect(() => controller.cancel()).not.toThrow()
  })
})
