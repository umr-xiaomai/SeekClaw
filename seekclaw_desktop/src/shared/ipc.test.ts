import { describe, expect, it } from 'vitest'
import { toIpcPayload } from './ipc.js'

describe('toIpcPayload', () => {
  it('converts reactive-style proxies into structured-cloneable payloads', () => {
    const reactive = new Proxy({ args: new Proxy(['-y'], {}), nested: { enabled: true } }, {})
    // This is the failure the renderer used to hit: "An object could not be cloned."
    expect(() => structuredClone(reactive)).toThrow()

    const payload = toIpcPayload(reactive)
    expect(() => structuredClone(payload)).not.toThrow()
    expect(payload).toEqual({ args: ['-y'], nested: { enabled: true } })
  })

  it('passes primitives and nullish values through untouched', () => {
    expect(toIpcPayload('create')).toBe('create')
    expect(toIpcPayload(42)).toBe(42)
    expect(toIpcPayload(null)).toBeNull()
    expect(toIpcPayload(undefined)).toBeUndefined()
  })

  it('drops undefined fields exactly like the JSON daemon protocol does', () => {
    expect(toIpcPayload({ name: 'StarLife', url: undefined })).toEqual({ name: 'StarLife' })
  })
})
