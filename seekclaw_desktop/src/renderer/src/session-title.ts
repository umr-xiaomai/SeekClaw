import type { ThreadItem } from './types'

export function applySessionTitle(thread: ThreadItem, saved: { title?: string; titleSource?: ThreadItem['titleSource'] }): void {
  const source = saved.titleSource ?? 'manual'
  // An older session snapshot can race a completed summary or a manual rename.
  if ((source === 'pending' || source === 'input')
    && (thread.titleSource === 'generated' || thread.titleSource === 'manual')) return
  if (source === 'generated' && thread.titleSource === 'manual') return
  thread.titleSource = source
  thread.title = source === 'input' || source === 'pending' ? '未命名任务' : saved.title || '未命名任务'
  if (source === 'generated' || source === 'manual') thread.titleGenerating = false
}
