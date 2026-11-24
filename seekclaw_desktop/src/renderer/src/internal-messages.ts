import type { ChatMessage } from './types'

const HIDDEN_SYSTEM_PREFIXES = [
  '>>> [Delegated context]',
  '>>> [output truncated]',
  '>>> [System Directive:',
  'The automatic verification step',
  'System: [Memory Recall]',
  'System: [Error]'
] as const

const COMPACTION_PREFIX = '>>> [Context compaction]'

/**
 * Runtime-injected loop directives (`output truncated`, `System Directive`,
 * `The automatic verification step`, memory/error markers) use `role: 'user'`
 * for LLM turn alternation, but must never render as user messages, appear in
 * the timeline, or split an active turn.
 */
export function isHiddenSystemUserMessage(message: Pick<ChatMessage, 'role' | 'content'>): boolean {
  if (message.role !== 'user') return false
  const text = (message.content ?? '').trimStart()
  return HIDDEN_SYSTEM_PREFIXES.some((prefix) => text.startsWith(prefix))
}

/** Context compaction summaries sit at the top of truncated history as neutral system cards. */
export function isSystemBannerUserMessage(message: Pick<ChatMessage, 'role' | 'content'>): boolean {
  if (message.role !== 'user') return false
  return (message.content ?? '').trimStart().startsWith(COMPACTION_PREFIX)
}

export function isInternalUserMessage(message: Pick<ChatMessage, 'role' | 'content'>): boolean {
  return isHiddenSystemUserMessage(message) || isSystemBannerUserMessage(message)
}
