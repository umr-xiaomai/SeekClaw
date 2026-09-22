export interface ChartPoint {
  x: number
  y: number
}

export function formatTokenCount(n: number): string {
  if (n >= 1_000_000) {
    const val = n / 1_000_000
    return val >= 10 ? `${val.toFixed(1)}M` : `${val.toFixed(2)}M`
  }
  if (n >= 10_000) {
    return `${(n / 1_000).toFixed(0)}k`
  }
  if (n >= 1_000) {
    return `${(n / 1_000).toFixed(1)}k`
  }
  return n.toLocaleString()
}

export function formatDateLabel(dateStr: string): string {
  if (!dateStr) return ''
  const parts = dateStr.split('-')
  if (parts.length >= 3) {
    return `${parts[1]}/${parts[2]}`
  }
  return dateStr
}

export function calculateNiceMax(rawMax: number, defaultMax = 100): number {
  if (rawMax <= 0) return defaultMax
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawMax)))
  const normalized = rawMax / magnitude
  let factor = 10
  if (normalized <= 1) factor = 1
  else if (normalized <= 2) factor = 2
  else if (normalized <= 5) factor = 5
  else factor = 10
  return factor * magnitude
}

/**
 * Builds a smooth cubic bezier SVG path definition through the given points.
 */
export function buildLinePath(points: ChartPoint[]): string {
  if (points.length === 0) return ''
  const pStart = points[0]
  if (!pStart) return ''
  if (points.length === 1) return `M ${pStart.x},${pStart.y}`
  const pNext = points[1]
  if (points.length === 2 && pNext) {
    return `M ${pStart.x},${pStart.y} L ${pNext.x},${pNext.y}`
  }

  let d = `M ${pStart.x},${pStart.y}`
  for (let i = 0; i < points.length - 1; i++) {
    const p0 = points[i === 0 ? i : i - 1] ?? pStart
    const p1 = points[i] ?? pStart
    const p2 = points[i + 1] ?? p1
    const p3 = points[i + 2 < points.length ? i + 2 : i + 1] ?? p2

    // Cubic bezier control points (tension = 0.15)
    const cp1x = p1.x + (p2.x - p0.x) * 0.15
    const cp1y = p1.y + (p2.y - p0.y) * 0.15
    const cp2x = p2.x - (p3.x - p1.x) * 0.15
    const cp2y = p2.y - (p3.y - p1.y) * 0.15

    d += ` C ${cp1x.toFixed(1)},${cp1y.toFixed(1)} ${cp2x.toFixed(1)},${cp2y.toFixed(1)} ${p2.x.toFixed(1)},${p2.y.toFixed(1)}`
  }
  return d
}

/**
 * Builds a closed SVG area path from the smooth curve down to bottomY.
 */
export function buildAreaPath(points: ChartPoint[], bottomY: number): string {
  if (points.length === 0) return ''
  const first = points[0]
  const last = points[points.length - 1]
  if (!first || !last) return ''
  const linePath = buildLinePath(points)
  return `${linePath} L ${last.x.toFixed(1)},${bottomY} L ${first.x.toFixed(1)},${bottomY} Z`
}
