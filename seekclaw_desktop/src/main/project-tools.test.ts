import { describe, expect, it } from 'vitest'
import { parseGitLog } from './project-tools.js'

describe('parseGitLog', () => {
  it('parses record and unit separated git log output', () => {
    const commits = parseGitLog(
      'abcdef\x1fabcdef\x1fAlice\x1f2026-07-30T10:00:00+08:00\x1fAdd diff panel\x1e' +
      '123456\x1f123456\x1fBob\x1f2026-07-29T09:00:00+08:00\x1fInitial commit\x1e')

    expect(commits).toHaveLength(2)
    expect(commits[0]).toMatchObject({ shortHash: 'abcdef', author: 'Alice', subject: 'Add diff panel' })
    expect(commits[1]?.subject).toBe('Initial commit')
  })
})
