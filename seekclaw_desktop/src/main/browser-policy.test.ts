import { describe, expect, it } from 'vitest'
import { browserBounds, browserUrl } from './browser-policy.js'

describe('conversation browser policy', () => {
  it('accepts development servers and rejects local files, executable schemes and URL credentials', () => {
    expect(browserUrl('http://localhost:5173/demo')).toBe('http://localhost:5173/demo')
    expect(browserUrl('about:blank')).toBe('about:blank')
    for (const url of ['file:///C:/secret', 'javascript:alert(1)', 'data:text/html,hello', 'https://user:password@example.com', 'seekclaw://open'])
      expect(() => browserUrl(url)).toThrow()
  })
  it('keeps zoomed views below native window controls and inside content bounds', () => {
    expect(browserBounds({ x: 500, y: 10, width: 500, height: 800 }, 1.5, { width: 1200, height: 900 }))
      .toEqual({ x: 750, y: 48, width: 450, height: 852 })
    expect(() => browserBounds({ x: NaN, y: 0, width: 1, height: 1 }, 1, { width: 100, height: 100 })).toThrow()
  })
})
