import { parseUnifiedDiff, type ParsedFileDiff } from './diff-parser'
import type { ToolActivity } from './types'

interface ToolPatch {
  path: string
  diff: string
}

interface PreviewFile {
  path: string
  edits: ParsedFileDiff[]
  additions: number
  deletions: number
}

export interface ToolDiffPreview {
  files: PreviewFile[]
  unparsed: ToolPatch[]
  rawDiff: string
  fileCount: number
  additions: number
  deletions: number
}

export function createToolDiffPreview(patches: readonly ToolPatch[]): ToolDiffPreview {
  const files = new Map<string, PreviewFile>()
  const unparsed: ToolPatch[] = []
  let additions = 0
  let deletions = 0
  for (const patch of patches) {
    // Parse each call separately. File tools produce patches without `diff --git` headers.
    const edits = parseUnifiedDiff(patch.diff, patch.path)
    if (!edits.length) unparsed.push(patch)
    for (const edit of edits) {
      const file = files.get(edit.displayPath) ?? { path: edit.displayPath, edits: [], additions: 0, deletions: 0 }
      file.edits.push(edit)
      file.additions += edit.additions
      file.deletions += edit.deletions
      files.set(file.path, file)
      additions += edit.additions
      deletions += edit.deletions
    }
  }
  return {
    files: [...files.values()],
    unparsed,
    rawDiff: patches.map(({ diff }) => diff).join('\n'),
    fileCount: new Set([...files.keys(), ...unparsed.map(({ path }) => path)]).size,
    additions,
    deletions
  }
}

export function collectTurnDiff(tools: readonly ToolActivity[]): ToolDiffPreview | null {
  const patches = new Map<string, ToolPatch>()
  for (const tool of tools) {
    if (tool.diff?.trim() && tool.state !== 'running') {
      patches.set(tool.callId ?? tool.id, { path: tool.filePath || '变更预览', diff: tool.diff })
    }
  }
  return patches.size ? createToolDiffPreview([...patches.values()]) : null
}
