// Pure TypeScript ISO/IEC 18004 QR Code SVG generator (Byte mode, ECC Level L, versions 1..10).
// Covers arbitrary UTF-8 URLs up to 271 bytes with zero external runtime dependencies.

interface VersionSpec {
  version: number
  totalDataBytes: number
  ecPerBlock: number
  g1Blocks: number
  g1DataBytes: number
  g2Blocks: number
  g2DataBytes: number
  alignment: number[]
}

// Level-L capacity and RS block specs from ISO/IEC 18004 Table 7 & Table 9.
const VERSION_SPECS: VersionSpec[] = [
  { version: 1, totalDataBytes: 19, ecPerBlock: 7, g1Blocks: 1, g1DataBytes: 19, g2Blocks: 0, g2DataBytes: 0, alignment: [] },
  { version: 2, totalDataBytes: 34, ecPerBlock: 10, g1Blocks: 1, g1DataBytes: 34, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 18] },
  { version: 3, totalDataBytes: 55, ecPerBlock: 15, g1Blocks: 1, g1DataBytes: 55, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 22] },
  { version: 4, totalDataBytes: 80, ecPerBlock: 20, g1Blocks: 1, g1DataBytes: 80, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 26] },
  { version: 5, totalDataBytes: 108, ecPerBlock: 26, g1Blocks: 1, g1DataBytes: 108, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 30] },
  { version: 6, totalDataBytes: 136, ecPerBlock: 18, g1Blocks: 2, g1DataBytes: 68, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 34] },
  { version: 7, totalDataBytes: 156, ecPerBlock: 20, g1Blocks: 2, g1DataBytes: 78, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 22, 38] },
  { version: 8, totalDataBytes: 194, ecPerBlock: 24, g1Blocks: 2, g1DataBytes: 97, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 24, 42] },
  { version: 9, totalDataBytes: 232, ecPerBlock: 30, g1Blocks: 2, g1DataBytes: 116, g2Blocks: 0, g2DataBytes: 0, alignment: [6, 26, 46] },
  { version: 10, totalDataBytes: 274, ecPerBlock: 18, g1Blocks: 2, g1DataBytes: 68, g2Blocks: 2, g2DataBytes: 69, alignment: [6, 28, 50] }
]

// Format bits for ECC Level L (01) and masks 0..7 after BCH(15,5) + XOR 0x5412.
const FORMAT_BITS_L = [
  0x77c4, 0x72f3, 0x7daa, 0x789d, 0x662f, 0x6318, 0x6c41, 0x6976
]

// Version info 18-bit BCH words for versions 7..10.
const VERSION_BITS: Record<number, number> = {
  7: 0x07c94,
  8: 0x085bc,
  9: 0x09a99,
  10: 0x0a4d3
}

const GF_EXP = new Uint8Array(512)
const GF_LOG = new Uint8Array(256)
{
  let value = 1
  for (let i = 0; i < 255; i++) {
    GF_EXP[i] = value
    GF_LOG[value] = i
    value <<= 1
    if (value & 0x100) value ^= 0x11d
  }
  for (let i = 255; i < 512; i++) {
    GF_EXP[i] = GF_EXP[i - 255]!
  }
}

function gfMul(a: number, b: number): number {
  if (a === 0 || b === 0) return 0
  return GF_EXP[GF_LOG[a]! + GF_LOG[b]!]!
}

function rsGeneratorPoly(degree: number): Uint8Array {
  let poly = new Uint8Array([1])
  for (let i = 0; i < degree; i++) {
    const next = new Uint8Array(poly.length + 1)
    const factor = GF_EXP[i]!
    for (let j = 0; j < poly.length; j++) {
      next[j] = (next[j]! ^ poly[j]!) & 0xff
      next[j + 1] = (next[j + 1]! ^ gfMul(poly[j]!, factor)) & 0xff
    }
    poly = next
  }
  return poly
}

function rsComputeEcc(data: Uint8Array, ecCount: number): Uint8Array {
  const gen = rsGeneratorPoly(ecCount)
  const buffer = new Uint8Array(data.length + ecCount)
  buffer.set(data, 0)
  for (let i = 0; i < data.length; i++) {
    const lead = buffer[i]!
    if (lead === 0) continue
    for (let j = 1; j < gen.length; j++) {
      buffer[i + j] = (buffer[i + j]! ^ gfMul(gen[j]!, lead)) & 0xff
    }
  }
  return buffer.slice(data.length)
}

function pickVersion(byteLength: number): VersionSpec {
  for (const spec of VERSION_SPECS) {
    const countBits = spec.version >= 10 ? 16 : 8
    const headerBits = 4 + countBits
    const capacityBits = spec.totalDataBytes * 8
    if (headerBits + byteLength * 8 <= capacityBits) return spec
  }
  throw new Error('QR payload exceeds supported length (max 271 UTF-8 bytes)')
}

function encodeDataCodewords(bytes: Uint8Array, spec: VersionSpec): Uint8Array {
  const bits: number[] = []
  const pushBits = (value: number, count: number): void => {
    for (let i = count - 1; i >= 0; i--) {
      bits.push((value >> i) & 1)
    }
  }

  // Byte mode indicator (0100) + character count.
  pushBits(0b0100, 4)
  pushBits(bytes.length, spec.version >= 10 ? 16 : 8)
  for (const byte of bytes) {
    pushBits(byte, 8)
  }

  const maxBits = spec.totalDataBytes * 8
  const terminator = Math.min(4, maxBits - bits.length)
  for (let i = 0; i < terminator; i++) bits.push(0)
  while (bits.length % 8 !== 0) bits.push(0)

  const out = new Uint8Array(spec.totalDataBytes)
  for (let i = 0; i < bits.length; i += 8) {
    let val = 0
    for (let b = 0; b < 8; b++) val = (val << 1) | bits[i + b]!
    out[i >> 3] = val
  }

  const pads = [0xec, 0x11]
  let idx = bits.length >> 3
  let padIdx = 0
  while (idx < spec.totalDataBytes) {
    out[idx++] = pads[padIdx & 1]!
    padIdx++
  }
  return out
}

function buildFinalBitStream(dataBytes: Uint8Array, spec: VersionSpec): number[] {
  const dataBlocks: Uint8Array[] = []
  const ecBlocks: Uint8Array[] = []
  let offset = 0

  for (let i = 0; i < spec.g1Blocks; i++) {
    const slice = dataBytes.slice(offset, offset + spec.g1DataBytes)
    offset += spec.g1DataBytes
    dataBlocks.push(slice)
    ecBlocks.push(rsComputeEcc(slice, spec.ecPerBlock))
  }
  for (let i = 0; i < spec.g2Blocks; i++) {
    const slice = dataBytes.slice(offset, offset + spec.g2DataBytes)
    offset += spec.g2DataBytes
    dataBlocks.push(slice)
    ecBlocks.push(rsComputeEcc(slice, spec.ecPerBlock))
  }

  const interleaved: number[] = []
  const maxDataLen = Math.max(spec.g1DataBytes, spec.g2DataBytes)
  for (let col = 0; col < maxDataLen; col++) {
    for (const block of dataBlocks) {
      if (col < block.length) interleaved.push(block[col]!)
    }
  }
  for (let col = 0; col < spec.ecPerBlock; col++) {
    for (const block of ecBlocks) {
      interleaved.push(block[col]!)
    }
  }

  const bits: number[] = []
  for (const byte of interleaved) {
    for (let b = 7; b >= 0; b--) bits.push((byte >> b) & 1)
  }
  // Remainder bits (versions 2..6 need 7 bits, 7..10 need 0 bits).
  const remainder = spec.version >= 2 && spec.version <= 6 ? 7 : 0
  for (let i = 0; i < remainder; i++) bits.push(0)
  return bits
}

function createMatrix(size: number): { modules: Uint8Array; isFunction: Uint8Array } {
  return {
    modules: new Uint8Array(size * size),
    isFunction: new Uint8Array(size * size)
  }
}

function setModule(
  modules: Uint8Array,
  isFunction: Uint8Array,
  size: number,
  row: number,
  col: number,
  dark: boolean,
  fn = true
): void {
  const idx = row * size + col
  modules[idx] = dark ? 1 : 0
  if (fn) isFunction[idx] = 1
}

function drawFinder(modules: Uint8Array, isFunction: Uint8Array, size: number, top: number, left: number): void {
  for (let r = -1; r <= 7; r++) {
    for (let c = -1; c <= 7; c++) {
      const rr = top + r
      const cc = left + c
      if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue
      const inSquare = r >= 0 && r <= 6 && c >= 0 && c <= 6
      const dark =
        inSquare &&
        (r === 0 || r === 6 || c === 0 || c === 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4))
      setModule(modules, isFunction, size, rr, cc, dark, true)
    }
  }
}

function drawAlignment(modules: Uint8Array, isFunction: Uint8Array, size: number, centerR: number, centerC: number): void {
  for (let r = -2; r <= 2; r++) {
    for (let c = -2; c <= 2; c++) {
      const dark = Math.max(Math.abs(r), Math.abs(c)) !== 1
      setModule(modules, isFunction, size, centerR + r, centerC + c, dark, true)
    }
  }
}

function drawFunctionPatterns(modules: Uint8Array, isFunction: Uint8Array, spec: VersionSpec): void {
  const size = spec.version * 4 + 17
  drawFinder(modules, isFunction, size, 0, 0)
  drawFinder(modules, isFunction, size, 0, size - 7)
  drawFinder(modules, isFunction, size, size - 7, 0)

  // Timing patterns
  for (let i = 8; i < size - 8; i++) {
    const dark = i % 2 === 0
    setModule(modules, isFunction, size, 6, i, dark, true)
    setModule(modules, isFunction, size, i, 6, dark, true)
  }

  // Dark module
  setModule(modules, isFunction, size, size - 8, 8, true, true)

  // Reserve format info cells
  for (let i = 0; i <= 8; i++) {
    if (i !== 6) {
      setModule(modules, isFunction, size, 8, i, false, true)
      setModule(modules, isFunction, size, i, 8, false, true)
    }
  }
  for (let i = size - 8; i < size; i++) {
    setModule(modules, isFunction, size, 8, i, false, true)
    if (i >= size - 7) setModule(modules, isFunction, size, i, 8, false, true)
  }

  // Alignment patterns
  const centers = spec.alignment
  for (const r of centers) {
    for (const c of centers) {
      if ((r <= 8 && c <= 8) || (r <= 8 && c >= size - 8) || (r >= size - 8 && c <= 8)) continue
      drawAlignment(modules, isFunction, size, r, c)
    }
  }

  // Version information (v7+)
  const versionBits = VERSION_BITS[spec.version]
  if (versionBits !== undefined) {
    for (let i = 0; i < 18; i++) {
      const dark = ((versionBits >> i) & 1) === 1
      const a = Math.floor(i / 3)
      const b = size - 11 + (i % 3)
      setModule(modules, isFunction, size, a, b, dark, true)
      setModule(modules, isFunction, size, b, a, dark, true)
    }
  }
}

function drawFormatBits(modules: Uint8Array, size: number, mask: number): void {
  const format = FORMAT_BITS_L[mask]!
  for (let i = 0; i <= 5; i++) modules[8 * size + i] = (format >> (14 - i)) & 1
  modules[8 * size + 7] = (format >> 8) & 1
  modules[8 * size + 8] = (format >> 7) & 1
  modules[7 * size + 8] = (format >> 6) & 1
  for (let i = 9; i < 15; i++) modules[(14 - i) * size + 8] = (format >> (14 - i)) & 1

  for (let i = 0; i < 8; i++) modules[8 * size + (size - 1 - i)] = (format >> i) & 1
  for (let i = 0; i < 7; i++) modules[(size - 1 - i) * size + 8] = (format >> (14 - i)) & 1
}

function maskCondition(mask: number, r: number, c: number): boolean {
  switch (mask) {
    case 0: return (r + c) % 2 === 0
    case 1: return r % 2 === 0
    case 2: return c % 3 === 0
    case 3: return (r + c) % 3 === 0
    case 4: return (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0
    case 5: return ((r * c) % 2) + ((r * c) % 3) === 0
    case 6: return (((r * c) % 2) + ((r * c) % 3)) % 2 === 0
    default: return (((r + c) % 2) + ((r * c) % 3)) % 2 === 0
  }
}

function computePenalty(modules: Uint8Array, size: number): number {
  let penalty = 0
  const at = (r: number, c: number): number => modules[r * size + c]!

  // Rule 1: adjacent runs >= 5
  for (let r = 0; r < size; r++) {
    let runColor = at(r, 0)
    let runLen = 1
    for (let c = 1; c < size; c++) {
      const color = at(r, c)
      if (color === runColor) {
        runLen++
      } else {
        if (runLen >= 5) penalty += 3 + (runLen - 5)
        runColor = color
        runLen = 1
      }
    }
    if (runLen >= 5) penalty += 3 + (runLen - 5)
  }
  for (let c = 0; c < size; c++) {
    let runColor = at(0, c)
    let runLen = 1
    for (let r = 1; r < size; r++) {
      const color = at(r, c)
      if (color === runColor) {
        runLen++
      } else {
        if (runLen >= 5) penalty += 3 + (runLen - 5)
        runColor = color
        runLen = 1
      }
    }
    if (runLen >= 5) penalty += 3 + (runLen - 5)
  }

  // Rule 2: 2x2 blocks
  for (let r = 0; r < size - 1; r++) {
    for (let c = 0; c < size - 1; c++) {
      const sum = at(r, c) + at(r, c + 1) + at(r + 1, c) + at(r + 1, c + 1)
      if (sum === 0 || sum === 4) penalty += 3
    }
  }

  // Rule 4: dark ratio
  let darkCount = 0
  for (let i = 0; i < modules.length; i++) darkCount += modules[i]!
  const percentDiff = Math.abs(Math.round((darkCount * 100) / modules.length) - 50)
  penalty += Math.floor(percentDiff / 5) * 10
  return penalty
}

export function encodeQrMatrix(text: string): { size: number; modules: Uint8Array } {
  const bytes = new TextEncoder().encode(text)
  const spec = pickVersion(bytes.length)
  const size = spec.version * 4 + 17
  const dataCodewords = encodeDataCodewords(bytes, spec)
  const bitStream = buildFinalBitStream(dataCodewords, spec)

  const { modules: baseModules, isFunction } = createMatrix(size)
  drawFunctionPatterns(baseModules, isFunction, spec)

  // Place data bits upward/downward in 2-column zig-zag strips.
  let bitIdx = 0
  let upward = true
  for (let rightCol = size - 1; rightCol >= 1; rightCol -= 2) {
    if (rightCol === 6) rightCol = 5
    for (let step = 0; step < size; step++) {
      const r = upward ? size - 1 - step : step
      for (let colOffset = 0; colOffset < 2; colOffset++) {
        const c = rightCol - colOffset
        const idx = r * size + c
        if (isFunction[idx]) continue
        baseModules[idx] = bitIdx < bitStream.length ? bitStream[bitIdx++]! : 0
      }
    }
    upward = !upward
  }

  let bestMask = 0
  let bestScore = Number.POSITIVE_INFINITY
  let bestModules = new Uint8Array(baseModules)

  for (let mask = 0; mask < 8; mask++) {
    const candidate = new Uint8Array(baseModules)
    for (let r = 0; r < size; r++) {
      for (let c = 0; c < size; c++) {
        const idx = r * size + c
        if (!isFunction[idx] && maskCondition(mask, r, c)) {
          candidate[idx] = candidate[idx]! ^ 1
        }
      }
    }
    drawFormatBits(candidate, size, mask)
    const score = computePenalty(candidate, size)
    if (score < bestScore) {
      bestScore = score
      bestMask = mask
      bestModules = candidate
    }
  }
  void bestMask

  return { size, modules: bestModules }
}

export function renderQrData(text: string, marginModules = 2): { size: number; viewBox: string; path: string } {
  const { size, modules } = encodeQrMatrix(text)
  const viewSize = size + marginModules * 2
  const paths: string[] = []
  for (let r = 0; r < size; r++) {
    let runStart = -1
    for (let c = 0; c <= size; c++) {
      const dark = c < size && modules[r * size + c] === 1
      if (dark && runStart === -1) {
        runStart = c
      } else if (!dark && runStart !== -1) {
        paths.push(`M${runStart + marginModules},${r + marginModules}h${c - runStart}v1h-${c - runStart}z`)
        runStart = -1
      }
    }
  }
  return {
    size: viewSize,
    viewBox: `0 0 ${viewSize} ${viewSize}`,
    path: paths.join('')
  }
}

export function renderQrSvg(text: string, marginModules = 2): string {
  const data = renderQrData(text, marginModules)
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${data.viewBox}" shape-rendering="crispEdges"><rect width="${data.size}" height="${data.size}" fill="#ffffff"/><path d="${data.path}" fill="#111827"/></svg>`
}
