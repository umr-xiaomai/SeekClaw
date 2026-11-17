export type ComposerCommandId =
  | 'new'
  | 'fork'
  | 'diff'
  | 'history'
  | 'settings'
  | 'optimize'
  | 'clear'
  | 'mode:manual'
  | 'mode:guardrail'
  | 'mode:full'

export interface SlashCommandItem {
  id: ComposerCommandId
  command: string
  title: string
  description: string
  requiresActiveSession?: boolean
  requiresProject?: boolean
}

export const SLASH_COMMANDS: SlashCommandItem[] = [
  { id: 'new', command: '/new', title: '新建任务', description: '在当前项目中创建一个新的空白任务' },
  { id: 'diff', command: '/diff', title: '工作区变更', description: '打开右侧 Git Diff 面板查看当前改动', requiresProject: true },
  { id: 'history', command: '/history', title: '提交记录', description: '查看当前项目最近的 Git 提交历史', requiresProject: true },
  { id: 'fork', command: '/fork', title: '分叉任务', description: '从当前对话分叉出独立新任务', requiresActiveSession: true },
  { id: 'optimize', command: '/optimize', title: '优化提示词', description: '使用当前模型润色并优化输入框里的指令' },
  { id: 'clear', command: '/clear', title: '清空草稿', description: '清除当前输入框中的文本、图片和文件附件' },
  { id: 'mode:manual', command: '/mode manual', title: '请求批准模式', description: '编辑外部文件和使用互联网时始终询问' },
  { id: 'mode:guardrail', command: '/mode guardrail', title: '帮我批准模式', description: '仅对检测到的风险操作请求批准' },
  { id: 'mode:full', command: '/mode full', title: '完全访问模式', description: '不受限制地访问网络和工作区文件' },
  { id: 'settings', command: '/settings', title: '打开设置', description: '配置模型、MCP、外观与高级选项' }
]

export type ComposerTriggerMatch =
  | { kind: 'mention'; query: string; start: number; end: number }
  | { kind: 'command'; query: string; start: number; end: number }

/**
 * Detects an active `@file` mention or leading `/command` token right before the textarea cursor.
 */
export function detectComposerTrigger(text: string, cursor: number): ComposerTriggerMatch | null {
  const safeCursor = Math.max(0, Math.min(cursor, text.length))
  const before = text.slice(0, safeCursor)

  // 1. Slash command only triggers at the very beginning of the input (leading whitespace allowed).
  const cmdMatch = before.match(/^\s*(\/[^\n]*)$/)
  if (cmdMatch && cmdMatch[1] !== undefined) {
    const token = cmdMatch[1]
    const start = before.length - token.length
    return {
      kind: 'command',
      query: token.slice(1).trimStart(),
      start,
      end: safeCursor
    }
  }

  // 2. `@mention` triggers after whitespace or at start of input, without whitespace inside the query.
  const mentionMatch = before.match(/(?:^|[\s\n])@([^\s@\n]*)$/)
  if (mentionMatch && mentionMatch[1] !== undefined) {
    const query = mentionMatch[1]
    const start = safeCursor - query.length - 1
    return {
      kind: 'mention',
      query,
      start,
      end: safeCursor
    }
  }

  return null
}

export function filterSlashCommands(
  query: string,
  context: { hasProject: boolean; hasActiveSession: boolean }
): SlashCommandItem[] {
  const needle = query.trim().toLowerCase()
  return SLASH_COMMANDS.filter((item) => {
    if (item.requiresProject && !context.hasProject) return false
    if (item.requiresActiveSession && !context.hasActiveSession) return false
    if (!needle) return true
    return (
      item.command.toLowerCase().includes(needle)
      || item.title.toLowerCase().includes(needle)
      || item.description.toLowerCase().includes(needle)
    )
  })
}

export function applyMentionReplacement(
  text: string,
  match: { start: number; end: number },
  relativePath: string
): { nextText: string; nextCursor: number } {
  const prefix = text.slice(0, match.start)
  const suffix = text.slice(match.end)
  const insertion = `@${relativePath}`
  const needsTrailingSpace = suffix.length === 0 || !/^\s/.test(suffix)
  const finalInsertion = needsTrailingSpace ? `${insertion} ` : insertion
  return {
    nextText: `${prefix}${finalInsertion}${suffix}`,
    nextCursor: prefix.length + finalInsertion.length
  }
}
