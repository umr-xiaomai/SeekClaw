import { describe, expect, it } from 'vitest'
import { countDiffLines, parseUnifiedDiff } from './diff-parser'

describe('countDiffLines', () => {
  it('counts added and removed lines while excluding +++ and --- headers', () => {
    const diff = [
      '--- a/src/demo.ts',
      '+++ b/src/demo.ts',
      '@@ -1,3 +1,4 @@',
      ' const a = 1',
      '-const b = 2',
      '+const b = 20',
      '+const c = 3',
      ' export { a, b }'
    ].join('\n')

    expect(countDiffLines(diff)).toEqual({ addedLines: 2, removedLines: 1 })
  })
})

describe('parseUnifiedDiff', () => {
  it('parses single-file tool diffs without diff --git prefix and computes inline word diffs', () => {
    const toolDiff = [
      '--- a/src/app.ts',
      '+++ b/src/app.ts',
      '@@ -10,3 +10,3 @@',
      ' const title = "old"',
      '-const count = 1',
      '+const count = 2',
      ' export default title'
    ].join('\n')

    const parsed = parseUnifiedDiff(toolDiff)
    expect(parsed).toHaveLength(1)
    expect(parsed[0]).toMatchObject({
      displayPath: 'src/app.ts',
      fileName: 'app.ts',
      additions: 1,
      deletions: 1
    })
    const delLine = parsed[0]!.hunks[0]!.lines[1]!
    const addLine = parsed[0]!.hunks[0]!.lines[2]!
    expect(delLine.wordDiff?.some((part) => part.changed && part.text === '1')).toBe(true)
    expect(addLine.wordDiff?.some((part) => part.changed && part.text === '2')).toBe(true)
  })

  it('parses multi-file diff --git patches', () => {
    const raw = [
      'diff --git a/a.ts b/a.ts',
      '--- a/a.ts',
      '+++ b/a.ts',
      '@@ -1 +1 @@',
      '-old',
      '+new',
      'diff --git a/b.ts b/b.ts',
      'new file mode 100644',
      '--- /dev/null',
      '+++ b/b.ts',
      '@@ -0,0 +1 @@',
      '+hello'
    ].join('\n')

    const parsed = parseUnifiedDiff(raw)
    expect(parsed).toHaveLength(2)
    expect(parsed[0]!.fileName).toBe('a.ts')
    expect(parsed[1]!.status).toBe('added')
    expect(parsed[1]!.additions).toBe(1)
  })
})
