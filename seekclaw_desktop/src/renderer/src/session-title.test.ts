import { describe, expect, it } from 'vitest'
import { applySessionTitle } from './session-title'
import type { ThreadItem } from './types'

const task = (): ThreadItem => ({ id: 'task', title: '新任务', messages: [], updatedAt: 1, titleGenerating: true })

describe('session title synchronization', () => {
  it('never exposes the first user prompt while a generated title is pending', () => {
    const thread = task()
    applySessionTitle(thread, { title: 'private full prompt', titleSource: 'input' })
    expect(thread.title).toBe('未命名任务')
    expect(thread.titleGenerating).toBe(true)
    applySessionTitle(thread, { title: '修复子智能体侧栏', titleSource: 'generated' })
    expect(thread.title).toBe('修复子智能体侧栏')
    expect(thread.titleGenerating).toBe(false)
  })

  it('keeps completed and manual titles when an older snapshot arrives', () => {
    const thread = task()
    applySessionTitle(thread, { title: 'AI summary', titleSource: 'generated' })
    applySessionTitle(thread, { title: 'first prompt', titleSource: 'input' })
    expect(thread.title).toBe('AI summary')
    applySessionTitle(thread, { title: '我的名称', titleSource: 'manual' })
    applySessionTitle(thread, { title: 'stale AI summary', titleSource: 'generated' })
    expect(thread.title).toBe('我的名称')
  })

  it('preserves names from existing versions and applies external manual renames', () => {
    const thread = task()
    applySessionTitle(thread, { title: '旧会话' })
    expect(thread.titleSource).toBe('manual')
    expect(thread.titleGenerating).toBe(false)
    applySessionTitle(thread, { title: '另一个客户端改名', titleSource: 'manual' })
    expect(thread.title).toBe('另一个客户端改名')
  })
})
