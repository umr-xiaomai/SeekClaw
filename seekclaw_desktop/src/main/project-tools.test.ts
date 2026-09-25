import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { normalizeDiffForGit, parseGitLog, revertFileDiffs } from './project-tools.js'

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

describe('normalizeDiffForGit', () => {
  it('converts backslashes in headers to forward slashes and normalizes line endings', () => {
    const raw = '--- a\\src\\foo.ts\r\n+++ b\\src\\foo.ts\r\n@@ -1,2 +1,3 @@\r\n'
    const normalized = normalizeDiffForGit(raw)
    expect(normalized).toContain('--- a/src/foo.ts\n+++ b/src/foo.ts\n')
  })
})

describe('revertFileDiffs', () => {
  it('reverts a modified file back to original content in reverse order', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'seekclaw-test-revert-'))
    try {
      const file = join(dir, 'sample.txt')
      // Current content after an edit
      writeFileSync(file, 'line 1\ninserted line\nline 2\n', 'utf8')

      const diff = '--- a/sample.txt\n+++ b/sample.txt\n@@ -1,2 +1,3 @@\n line 1\n+inserted line\n line 2\n'
      const result = await revertFileDiffs(dir, [{ filePath: file, diff }])

      expect(result.failed).toHaveLength(0)
      expect(result.reverted).toContain(file)
      expect(readFileSync(file, 'utf8').replace(/\r\n/g, '\n')).toBe('line 1\nline 2\n')
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('reports failed files when diff does not apply cleanly', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'seekclaw-test-conflict-'))
    try {
      const file = join(dir, 'sample.txt')
      writeFileSync(file, 'completely unmatching content\n', 'utf8')

      const diff = '--- a/sample.txt\n+++ b/sample.txt\n@@ -1,2 +1,3 @@\n line 1\n+inserted line\n line 2\n'
      const result = await revertFileDiffs(dir, [{ filePath: file, diff }])

      expect(result.failed).toHaveLength(1)
      expect(result.failed[0]?.filePath).toBe(file)
      expect(result.reverted).toHaveLength(0)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })
})
