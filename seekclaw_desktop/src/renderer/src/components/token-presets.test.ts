import { describe, expect, it } from 'vitest'
import { formatTokenCount } from '../app-helpers'

describe('Token presets configuration and formatting', () => {
  const unifiedPresets = [
    { value: 128000, label: '128K' },
    { value: 256000, label: '256K' },
    { value: 384000, label: '384K' },
    { value: 512000, label: '512K' },
    { value: 1000000, label: '1M' },
    { value: 1500000, label: '1.5M' },
    { value: 2000000, label: '2M' }
  ]

  it('starts presets at 128K and contains all requested steps up to 2M', () => {
    expect(unifiedPresets[0]!.label).toBe('128K')
    expect(unifiedPresets[0]!.value).toBe(128000)

    const labels = unifiedPresets.map((p) => p.label)
    expect(labels).toEqual(['128K', '256K', '384K', '512K', '1M', '1.5M', '2M'])
  })

  it('formats every preset into readable token string', () => {
    expect(formatTokenCount(128000)).toBe('128k')
    expect(formatTokenCount(256000)).toBe('256k')
    expect(formatTokenCount(384000)).toBe('384k')
    expect(formatTokenCount(512000)).toBe('512k')
    expect(formatTokenCount(1000000)).toBe('1M')
    expect(formatTokenCount(1500000)).toBe('1.5M')
    expect(formatTokenCount(2000000)).toBe('2M')
  })
})
