import { describe, expect, it } from 'vitest'
import { createFallbackUuid } from './web-polyfills'

describe('web-polyfills', () => {
  it('generates valid unique RFC 4122 v4 UUIDs', () => {
    const a = createFallbackUuid()
    const b = createFallbackUuid()
    const uuidV4Regex = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
    expect(a).toMatch(uuidV4Regex)
    expect(b).toMatch(uuidV4Regex)
    expect(a).not.toBe(b)
  })
})
