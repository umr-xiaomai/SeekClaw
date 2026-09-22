import { describe, expect, it } from 'vitest'
import {
  calculateNiceMax,
  formatDateLabel,
  formatTokenCount,
  buildLinePath,
  buildAreaPath
} from './usage-chart-utils'

describe('usage-chart-utils', () => {
  describe('formatTokenCount', () => {
    it('formats tokens below 1k as exact number string', () => {
      expect(formatTokenCount(0)).toBe('0')
      expect(formatTokenCount(850)).toBe('850')
    })

    it('formats tokens in thousands with k suffix', () => {
      expect(formatTokenCount(1200)).toBe('1.2k')
      expect(formatTokenCount(45000)).toBe('45k')
    })

    it('formats tokens in millions with M suffix', () => {
      expect(formatTokenCount(1500000)).toBe('1.50M')
      expect(formatTokenCount(12000000)).toBe('12.0M')
    })
  })

  describe('formatDateLabel', () => {
    it('formats YYYY-MM-DD into MM/DD', () => {
      expect(formatDateLabel('2026-09-22')).toBe('09/22')
      expect(formatDateLabel('2026-01-05')).toBe('01/05')
    })

    it('returns original string if not in standard format', () => {
      expect(formatDateLabel('')).toBe('')
      expect(formatDateLabel('Today')).toBe('Today')
    })
  })

  describe('calculateNiceMax', () => {
    it('rounds up to clean tick intervals', () => {
      expect(calculateNiceMax(0)).toBe(100)
      expect(calculateNiceMax(45)).toBe(50)
      expect(calculateNiceMax(88)).toBe(100)
      expect(calculateNiceMax(150)).toBe(200)
      expect(calculateNiceMax(7200)).toBe(10000)
    })
  })

  describe('buildLinePath and buildAreaPath', () => {
    it('generates valid SVG path data', () => {
      const points = [
        { x: 50, y: 150 },
        { x: 100, y: 80 },
        { x: 150, y: 120 }
      ]
      const line = buildLinePath(points)
      expect(line).toContain('M 50,150')
      expect(line).toContain('C')

      const area = buildAreaPath(points, 200)
      expect(area).toContain('M 50,150')
      expect(area).toContain('L 150.0,200 L 50.0,200 Z')
    })

    it('handles empty or single point lists gracefully', () => {
      expect(buildLinePath([])).toBe('')
      expect(buildAreaPath([], 200)).toBe('')
      expect(buildLinePath([{ x: 10, y: 20 }])).toBe('M 10,20')
    })
  })
})
