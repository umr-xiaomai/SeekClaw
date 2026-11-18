import type { GitCommit } from '../../shared/ipc'

export interface ParsedCommitSubject {
  badge?: {
    type: string
    label: string
    colorClass: string
  }
  scope?: string
  cleanSubject: string
}

export interface FormattedGitCommit {
  commit: GitCommit
  index: number
  parsed: ParsedCommitSubject
  formattedDate: string
}

const COMMIT_DATE_FORMATTER = new Intl.DateTimeFormat('zh-CN', {
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit'
})

/** Format an ISO commit timestamp using a single shared ICU formatter instance. */
export function formatCommitDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : COMMIT_DATE_FORMATTER.format(date)
}

/** Parse conventional-commit style tags and optional scope from a Git commit subject. */
export function parseCommitSubject(rawSubject: string): ParsedCommitSubject {
  if (!rawSubject) return { cleanSubject: '' }
  const s = rawSubject.trim()

  if (/^merge\b/i.test(s) || s.startsWith('合并')) {
    return {
      badge: { type: 'merge', label: 'merge', colorClass: 'commit-tag-merge' },
      cleanSubject: s
    }
  }

  const match = s.match(
    /^(?:([a-zA-Z]+)(?:\(([^)]+)\))?\s*[:：]|\[([a-zA-Z0-9_\-\u4e00-\u9fa5]+)\]\s*|【([a-zA-Z0-9_\-\u4e00-\u9fa5]+)】\s*|([\u4e00-\u9fa5]{2,4})\s*[:：])\s*(.*)$/
  )

  if (match) {
    const rawType = (match[1] || match[3] || match[4] || match[5] || '').toLowerCase()
    const scope = match[2]?.trim()
    const rest = (match[6] || '').trim() || s

    let type = ''
    let label = rawType
    let colorClass = 'commit-tag-other'

    if (/^(feat|feature|add|新增|特性)$/i.test(rawType)) {
      type = 'feat'
      label = 'feat'
      colorClass = 'commit-tag-feat'
    } else if (/^(fix|bug|hotfix|patch|修复)$/i.test(rawType)) {
      type = 'fix'
      label = 'fix'
      colorClass = 'commit-tag-fix'
    } else if (/^(refactor|重构)$/i.test(rawType)) {
      type = 'refactor'
      label = 'refactor'
      colorClass = 'commit-tag-refactor'
    } else if (/^(perf|opt|optimize|优化|性能)$/i.test(rawType)) {
      type = 'perf'
      label = 'perf'
      colorClass = 'commit-tag-perf'
    } else if (/^(docs?|文档)$/i.test(rawType)) {
      type = 'docs'
      label = 'docs'
      colorClass = 'commit-tag-docs'
    } else if (/^(test|tests|测试)$/i.test(rawType)) {
      type = 'test'
      label = 'test'
      colorClass = 'commit-tag-test'
    } else if (/^(style|样式)$/i.test(rawType)) {
      type = 'style'
      label = 'style'
      colorClass = 'commit-tag-style'
    } else if (/^(chore|build|ci|deps|杂项)$/i.test(rawType)) {
      type = 'chore'
      label = 'chore'
      colorClass = 'commit-tag-chore'
    } else if (/^(revert|回滚)$/i.test(rawType)) {
      type = 'revert'
      label = 'revert'
      colorClass = 'commit-tag-revert'
    }

    if (type) {
      return {
        badge: { type, label, colorClass },
        scope: scope || undefined,
        cleanSubject: rest
      }
    }
  }

  return { cleanSubject: s }
}

/** Pre-compute commit metadata once per history update so resize/hover renders stay O(1). */
export function formatGitHistoryCommits(commits: readonly GitCommit[]): FormattedGitCommit[] {
  return commits.map((commit, index) => ({
    commit,
    index,
    parsed: parseCommitSubject(commit.subject),
    formattedDate: formatCommitDate(commit.authoredAt)
  }))
}
