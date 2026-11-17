export interface DiffLine {
  type: 'context' | 'addition' | 'deletion'
  content: string
  oldLineNumber?: number
  newLineNumber?: number
  wordDiff?: Array<{ text: string; changed: boolean }>
}

export interface DiffHunk {
  header: string
  oldStart: number
  oldCount: number
  newStart: number
  newCount: number
  unmodifiedBefore: number
  lines: DiffLine[]
}

export interface ParsedFileDiff {
  id: string
  oldPath: string
  newPath: string
  displayPath: string
  fileName: string
  dirPath: string
  status: 'modified' | 'added' | 'deleted' | 'renamed'
  additions: number
  deletions: number
  hunks: DiffHunk[]
  collapsed: boolean
}

/** Counts added and removed content lines inside a unified diff string. */
export function countDiffLines(diff: string): { addedLines: number; removedLines: number } {
  if (!diff) return { addedLines: 0, removedLines: 0 }
  let addedLines = 0
  let removedLines = 0
  for (const line of diff.split(/\r?\n/)) {
    if (line.startsWith('+') && !line.startsWith('+++')) addedLines++
    else if (line.startsWith('-') && !line.startsWith('---')) removedLines++
  }
  return { addedLines, removedLines }
}

function computeWordDiff(
  delText: string,
  addText: string
): { delParts: Array<{ text: string; changed: boolean }>; addParts: Array<{ text: string; changed: boolean }> } {
  let prefixLen = 0
  while (prefixLen < delText.length && prefixLen < addText.length && delText[prefixLen] === addText[prefixLen]) {
    prefixLen++
  }
  let suffixLen = 0
  while (
    suffixLen < (delText.length - prefixLen) &&
    suffixLen < (addText.length - prefixLen) &&
    delText[delText.length - 1 - suffixLen] === addText[addText.length - 1 - suffixLen]
  ) {
    suffixLen++
  }

  return {
    delParts: [
      { text: delText.slice(0, prefixLen), changed: false },
      { text: delText.slice(prefixLen, delText.length - suffixLen), changed: true },
      { text: delText.slice(delText.length - suffixLen), changed: false }
    ].filter((p) => p.text.length > 0),
    addParts: [
      { text: addText.slice(0, prefixLen), changed: false },
      { text: addText.slice(prefixLen, addText.length - suffixLen), changed: true },
      { text: addText.slice(addText.length - suffixLen), changed: false }
    ].filter((p) => p.text.length > 0)
  }
}

export function attachWordDiffs(lines: DiffLine[]): void {
  let i = 0
  while (i < lines.length) {
    const curLine = lines[i]
    if (curLine && curLine.type === 'deletion') {
      const delStart = i
      while (i < lines.length && lines[i]?.type === 'deletion') i++
      const addStart = i
      while (i < lines.length && lines[i]?.type === 'addition') i++
      const delCount = addStart - delStart
      const addCount = i - addStart
      const delLine = lines[delStart]
      const addLine = lines[addStart]
      if (delCount === 1 && addCount === 1 && delLine && addLine) {
        const diff = computeWordDiff(delLine.content, addLine.content)
        delLine.wordDiff = diff.delParts
        addLine.wordDiff = diff.addParts
      }
    } else {
      i++
    }
  }
}

function normalizeHeaderPath(rawPath: string): string {
  const cleaned = rawPath.trim().replace(/^[ab]\//, '').replace(/\\/g, '/')
  return cleaned === '/dev/null' ? '' : cleaned
}

function parseSingleFileChunk(lines: string[], fallbackPath = 'unknown'): ParsedFileDiff | null {
  let oldPath = ''
  let newPath = ''
  let status: 'modified' | 'added' | 'deleted' | 'renamed' = 'modified'
  let additions = 0
  let deletions = 0
  const hunks: DiffHunk[] = []

  const first = lines[0] ?? ''
  const gitHeader = first.match(/^a\/(.+?)\s+b\/(.+)$/)
  if (gitHeader) {
    oldPath = normalizeHeaderPath(gitHeader[1] ?? '')
    newPath = normalizeHeaderPath(gitHeader[2] ?? '')
  }

  let currentHunk: DiffHunk | null = null
  let oldLine = 0
  let newLine = 0
  let lastOldEnd = 0
  const startIndex = gitHeader ? 1 : 0

  for (let i = startIndex; i < lines.length; i++) {
    const line = lines[i]
    if (line === undefined) continue
    if (!currentHunk && line.startsWith('--- ')) {
      oldPath = normalizeHeaderPath(line.slice(4))
      if (!oldPath) status = 'added'
      continue
    }
    if (!currentHunk && line.startsWith('+++ ')) {
      newPath = normalizeHeaderPath(line.slice(4))
      if (!newPath) status = 'deleted'
      continue
    }
    if (line.startsWith('new file mode')) {
      status = 'added'
      continue
    }
    if (line.startsWith('deleted file mode')) {
      status = 'deleted'
      continue
    }
    if (line.startsWith('similarity index') || line.startsWith('rename from')) {
      status = 'renamed'
      continue
    }
    if (line.startsWith('@@')) {
      const hunkMatch = line.match(/^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$/)
      if (hunkMatch) {
        const oldStart = parseInt(hunkMatch[1] ?? '1', 10)
        const oldCount = hunkMatch[2] !== undefined ? parseInt(hunkMatch[2], 10) : 1
        const newStart = parseInt(hunkMatch[3] ?? '1', 10)
        const newCount = hunkMatch[4] !== undefined ? parseInt(hunkMatch[4], 10) : 1
        const unmodifiedBefore = lastOldEnd > 0 ? Math.max(0, oldStart - lastOldEnd - 1) : Math.max(0, oldStart - 1)
        lastOldEnd = oldStart + oldCount - 1
        oldLine = oldStart
        newLine = newStart
        currentHunk = {
          header: line,
          oldStart,
          oldCount,
          newStart,
          newCount,
          unmodifiedBefore,
          lines: []
        }
        hunks.push(currentHunk)
      }
      continue
    }
    if (currentHunk) {
      if (line.startsWith('+')) {
        additions++
        currentHunk.lines.push({ type: 'addition', content: line.slice(1), newLineNumber: newLine++ })
      } else if (line.startsWith('-')) {
        deletions++
        currentHunk.lines.push({ type: 'deletion', content: line.slice(1), oldLineNumber: oldLine++ })
      } else if (line.startsWith(' ') || line === '') {
        // Keep trailing empty context lines inside an active hunk if there are more hunk lines or explicit space prefix
        if (line.startsWith(' ')) {
          currentHunk.lines.push({ type: 'context', content: line.slice(1), oldLineNumber: oldLine++, newLineNumber: newLine++ })
        }
      }
    }
  }

  if (hunks.length === 0 && status !== 'deleted' && status !== 'added') return null

  const displayPath = newPath || oldPath || normalizeHeaderPath(fallbackPath) || 'unknown'
  const pathParts = displayPath.split('/')
  const fileName = pathParts.pop() || displayPath
  const dirPath = pathParts.join('/')

  for (const hunk of hunks) {
    attachWordDiffs(hunk.lines)
  }

  return {
    id: displayPath.replace(/[^a-zA-Z0-9_-]/g, '_'),
    oldPath,
    newPath,
    displayPath,
    fileName,
    dirPath,
    status,
    additions,
    deletions,
    hunks,
    collapsed: false
  }
}

/**
 * Parses both multi-file `diff --git` output and single-file tool patches (`--- a/... +++ b/...` or bare `@@` hunks).
 */
export function parseUnifiedDiff(rawDiff: string, fallbackPath = 'unknown'): ParsedFileDiff[] {
  if (!rawDiff || !rawDiff.trim()) return []

  if (/^diff --git /m.test(rawDiff)) {
    const fileChunks = rawDiff.split(/^diff --git /m)
    const files: ParsedFileDiff[] = []
    for (const chunk of fileChunks) {
      if (!chunk.trim() || chunk.startsWith('# ')) continue
      const parsed = parseSingleFileChunk(chunk.split(/\r?\n/), fallbackPath)
      if (parsed) files.push(parsed)
    }
    return files
  }

  // Handle concatenated `--- ...` single-file tool diffs or a single bare hunk diff
  const parsed = parseSingleFileChunk(rawDiff.split(/\r?\n/), fallbackPath)
  return parsed ? [parsed] : []
}
