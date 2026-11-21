import { ReasoningLevel } from './types'

export interface ModelReasoningCapabilities {
  thinking?: boolean
  reasoning?: boolean
  maxReasoningLevel?: string
  supportedReasoningLevels?: string[] | null
  defaultReasoningLevel?: string | null
}

export const reasoningLevels = [
  { value: ReasoningLevel.Low, label: '低', detailLabel: '低强度 (Low)' },
  { value: ReasoningLevel.Medium, label: '中', detailLabel: '中等强度 (Medium)' },
  { value: ReasoningLevel.High, label: '高', detailLabel: '高强度 (High)' },
  { value: ReasoningLevel.Max, label: '最大', detailLabel: '最大强度 (Max)' },
  { value: ReasoningLevel.XHigh, label: '超高', detailLabel: '超高强度 (X-High)' },
  { value: ReasoningLevel.Ultra, label: '极致', detailLabel: '极致强度 (Ultra)' }
]

export function supportedReasoningLevels(capabilities?: ModelReasoningCapabilities): ReasoningLevel[] {
  const ordered = reasoningLevels.map(({ value }) => value)
  if (capabilities?.supportedReasoningLevels) {
    return ordered.filter((level) => capabilities.supportedReasoningLevels?.includes(level))
  }
  if (capabilities?.maxReasoningLevel === ReasoningLevel.None
    || (capabilities?.thinking === false && capabilities.reasoning === false)) return []
  const maximum = ordered.findIndex((level) => level === capabilities?.maxReasoningLevel)
  return ordered.slice(0, maximum >= 0 ? maximum + 1 : 4)
}

export function effectiveReasoningLevel(requested: ReasoningLevel, supported: ReasoningLevel[]): ReasoningLevel {
  if (!supported.length || requested === ReasoningLevel.None) return ReasoningLevel.None
  const rank = (value: ReasoningLevel): number => reasoningLevels.findIndex((level) => level.value === value)
  return supported.reduce((best, level) =>
    Math.abs(rank(level) - rank(requested)) < Math.abs(rank(best) - rank(requested)) ? level : best)
}
