import { describe, expect, it } from 'vitest'
import { encodeQrMatrix, renderQrData, renderQrSvg } from './qr-svg'

describe('qr-svg', () => {
  it('encodes LAN URL with token into valid QR matrix with finder patterns', () => {
    const matrix = encodeQrMatrix('http://192.168.1.42:19420/?token=AbcDef123456')
    expect(matrix.size).toBeGreaterThanOrEqual(21)
    expect(matrix.modules.length).toBe(matrix.size * matrix.size)

    // Top-left finder pattern outer & inner dark center
    expect(matrix.modules[0]).toBe(1)
    expect(matrix.modules[3 * matrix.size + 3]).toBe(1)
    expect(matrix.modules[1 * matrix.size + 1]).toBe(0)
  })

  it('renders crisp SVG markup and viewBox data', () => {
    const data = renderQrData('http://192.168.1.100:19420/?token=xyz', 2)
    expect(data.viewBox).toMatch(/^0 0 \d+ \d+$/)
    expect(data.path.length).toBeGreaterThan(20)

    const svg = renderQrSvg('http://192.168.1.100:19420/?token=xyz')
    expect(svg.startsWith('<svg ')).toBe(true)
    expect(svg).toContain('shape-rendering="crispEdges"')
  })
})
