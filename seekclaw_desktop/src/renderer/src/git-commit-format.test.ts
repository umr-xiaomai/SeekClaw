import { describe, expect, it } from 'vitest'
import { formatCommitDate, formatGitHistoryCommits, parseCommitSubject } from './git-commit-format'

describe('git commit formatting & caching helpers', () => {
  it('parses conventional, localized, merge, and plain commit subjects consistently', () => {
    expect(parseCommitSubject('feat(ui): 优化子智能体标签页')).toEqual({
      badge: { type: 'feat', label: 'feat', colorClass: 'commit-tag-feat' },
      scope: 'ui',
      cleanSubject: '优化子智能体标签页'
    })
    expect(parseCommitSubject('修复：历史记录缩放重渲染')).toEqual({
      badge: { type: 'fix', label: 'fix', colorClass: 'commit-tag-fix' },
      scope: undefined,
      cleanSubject: '历史记录缩放重渲染'
    })
    expect(parseCommitSubject('Merge branch \'main\' into dev')).toEqual({
      badge: { type: 'merge', label: 'merge', colorClass: 'commit-tag-merge' },
      cleanSubject: "Merge branch 'main' into dev"
    })
    expect(parseCommitSubject('Plain commit headline')).toEqual({
      cleanSubject: 'Plain commit headline'
    })
  })

  it('precomputes formatted commit entries cleanly once per history array', () => {
    const entries = formatGitHistoryCommits([
      { hash: '1111111111', shortHash: '1111111', author: 'Alice', authoredAt: '2026-11-17T10:30:00+08:00', subject: 'perf(git): 缓存提交列表条目' },
      { hash: '2222222222', shortHash: '2222222', author: 'Bob', authoredAt: 'invalid-date', subject: 'Initial commit' }
    ])

    expect(entries).toHaveLength(2)
    expect(entries[0]).toMatchObject({
      index: 0,
      parsed: {
        badge: { type: 'perf', label: 'perf', colorClass: 'commit-tag-perf' },
        scope: 'git',
        cleanSubject: '缓存提交列表条目'
      },
      formattedDate: formatCommitDate('2026-11-17T10:30:00+08:00')
    })
    expect(entries[0]?.formattedDate).not.toBe('2026-11-17T10:30:00+08:00')
    expect(entries[1]?.formattedDate).toBe('invalid-date')
  })
})
