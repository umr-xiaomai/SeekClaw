import { describe, expect, it } from 'vitest'
import {
  applyMentionReplacement,
  detectComposerTrigger,
  filterSlashCommands
} from './composer-triggers'

describe('composer triggers (@ and /)', () => {
  it('detects leading slash commands only at the start of input', () => {
    expect(detectComposerTrigger('/dif', 4)).toEqual({
      kind: 'command',
      query: 'dif',
      start: 0,
      end: 4
    })
    expect(detectComposerTrigger('  /set', 6)).toEqual({
      kind: 'command',
      query: 'set',
      start: 2,
      end: 6
    })
    // Should not trigger slash command mid-sentence
    expect(detectComposerTrigger('check path /diff', 16)).toBeNull()
  })

  it('detects @file mentions cleanly without firing on inline email addresses', () => {
    expect(detectComposerTrigger('@App.v', 6)).toEqual({
      kind: 'mention',
      query: 'App.v',
      start: 0,
      end: 6
    })
    expect(detectComposerTrigger('请检查 @src/main', 13)).toEqual({
      kind: 'mention',
      query: 'src/main',
      start: 4,
      end: 13
    })
    expect(detectComposerTrigger('email user@example.com', 22)).toBeNull()
  })

  it('filters slash commands by project and active session context', () => {
    const noProject = filterSlashCommands('', { hasProject: false, hasActiveSession: false })
    expect(noProject.some((c) => c.id === 'diff')).toBe(false)
    expect(noProject.some((c) => c.id === 'fork')).toBe(false)
    expect(noProject.some((c) => c.id === 'new')).toBe(true)

    const withContext = filterSlashCommands('历史', { hasProject: true, hasActiveSession: true })
    expect(withContext.map((c) => c.id)).toEqual(['history'])
  })

  it('replaces the @query token with @relativePath preserving trailing cursor position', () => {
    const text = '请帮我看看 @app 这个文件'
    const match = detectComposerTrigger(text, 10)!
    const replaced = applyMentionReplacement(text, match, 'src/App.vue')
    expect(replaced.nextText).toBe('请帮我看看 @src/App.vue 这个文件')
    expect(replaced.nextCursor).toBe('请帮我看看 @src/App.vue'.length)
  })
})
