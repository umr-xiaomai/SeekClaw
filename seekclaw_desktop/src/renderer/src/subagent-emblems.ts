import type { SubAgentActivity, SubAgentLifecycleEvent } from './types'

export interface SubAgentEmblemSpec {
  color: string
  path: string
}

/** Build a symmetrical hollow flower emblem path inside a 16x16 viewBox. */
export function buildFlowerPath(
  petals: number,
  outerR: number,
  valleyR: number,
  holeR: number,
  rotationDeg = 0
): string {
  const cx = 8
  const cy = 8
  const step = (Math.PI * 2) / petals
  const rot = (rotationDeg * Math.PI) / 180
  const ctrlFactor = petals === 4 ? 1.24 : petals === 6 ? 1.18 : 1.14
  const ctrlR = outerR * ctrlFactor
  const pt = (r: number, angle: number) => `${(cx + r * Math.cos(angle)).toFixed(2)} ${(cy + r * Math.sin(angle)).toFixed(2)}`
  const segments: string[] = []

  for (let i = 0; i < petals; i++) {
    const center = rot + i * step
    const start = center - step / 2
    const end = center + step / 2
    const c1 = center - step * 0.24
    const c2 = center + step * 0.24
    if (i === 0) segments.push(`M ${pt(valleyR, start)}`)
    segments.push(`C ${pt(ctrlR, c1)}, ${pt(ctrlR, c2)}, ${pt(valleyR, end)}`)
  }
  segments.push('Z')
  segments.push(
    `M ${(cx + holeR).toFixed(2)} ${cy} A ${holeR} ${holeR} 0 1 0 ${(cx - holeR).toFixed(2)} ${cy} A ${holeR} ${holeR} 0 1 0 ${(cx + holeR).toFixed(2)} ${cy} Z`
  )
  return segments.join(' ')
}

export const SUBAGENT_EMBLEMS: readonly SubAgentEmblemSpec[] = [
  { color: '#4c8bf5', path: buildFlowerPath(4, 6.3, 2.7, 1.35, 0) },
  { color: '#f59331', path: buildFlowerPath(8, 6.4, 3.6, 1.35, 0) },
  { color: '#8b5cf6', path: buildFlowerPath(6, 6.3, 3.2, 1.35, 0) },
  { color: '#10b981', path: buildFlowerPath(4, 6.3, 2.7, 1.35, 45) }
]

/** Assign a sequential flower emblem slot per distinct sub-agent across a list/turn. */
export function collectSubAgentEmblemIndices(
  agents: readonly SubAgentActivity[],
  events: readonly SubAgentLifecycleEvent[] = []
): Map<string, number> {
  const map = new Map<string, number>()
  let next = 0
  for (const agent of agents) {
    if (!map.has(agent.id)) {
      map.set(agent.id, next % SUBAGENT_EMBLEMS.length)
      next++
    }
  }
  for (const event of events) {
    if (!map.has(event.agentId)) {
      map.set(event.agentId, next % SUBAGENT_EMBLEMS.length)
      next++
    }
  }
  return map
}

export function subAgentEmblem(
  agentId: string,
  indexById?: ReadonlyMap<string, number>
): SubAgentEmblemSpec {
  const idx = indexById?.get(agentId) ?? 0
  return SUBAGENT_EMBLEMS[idx % SUBAGENT_EMBLEMS.length] ?? SUBAGENT_EMBLEMS[0]!
}
