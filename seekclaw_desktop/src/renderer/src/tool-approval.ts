export interface PendingToolApproval {
  approvalId: string
  callId: string
  sessionId?: string
  threadId: string
  requestId?: number
  toolName: string
  summary: string
  argumentsText: string
  mode?: string
  mutating?: boolean
  requiresNetwork?: boolean
  isMcp?: boolean
  background?: boolean
  childSessionId?: string
  createdAt: number
}

const SHELL_TOOL_NAMES = new Set(['bash', 'cmd', 'powershell', 'sh', 'terminal', 'exec_command'])
const FILE_MUTATING_TOOL_NAMES = new Set(['write_file', 'edit_file', 'apply_patch'])

/** Extracts a readable command, target path/url, or pretty-printed JSON from raw tool arguments. */
export function formatApprovalArguments(rawArguments: string, fallbackSummary = ''): {
  primaryTarget: string
  formattedArgs: string
  isShell: boolean
  toolCategory: 'shell' | 'edit' | 'network' | 'mcp' | 'other'
} {
  const trimmed = rawArguments.trim()
  let parsedObj: Record<string, unknown> | null = null
  if (trimmed.startsWith('{')) {
    try {
      const candidate = JSON.parse(trimmed)
      if (candidate && typeof candidate === 'object' && !Array.isArray(candidate)) {
        parsedObj = candidate as Record<string, unknown>
      }
    } catch {
      parsedObj = null
    }
  }

  let primaryTarget = fallbackSummary.trim()
  if (parsedObj) {
    for (const key of ['command', 'cmd', 'path', 'file_path', 'file', 'url', 'query', 'action']) {
      const val = parsedObj[key]
      if (typeof val === 'string' && val.trim()) {
        primaryTarget = val.trim()
        break
      }
    }
  }

  const formattedArgs = parsedObj
    ? JSON.stringify(parsedObj, null, 2)
    : (trimmed || fallbackSummary.trim())

  const isShell = Boolean(parsedObj && (typeof parsedObj.command === 'string' || typeof parsedObj.cmd === 'string'))

  return {
    primaryTarget,
    formattedArgs,
    isShell,
    toolCategory: isShell ? 'shell' : 'other'
  }
}

export function extractSafeShellPrefix(command: string): string | null {
  const normalized = command.trim()
  if (!normalized || normalized.includes('\n')) return null
  // Do not widen chained, piped, subshell, or redirected shell commands into prefix rules.
  if (/[;&|`<>]|\$\(/.test(normalized)) return null
  const firstToken = normalized.split(/\s+/)[0]?.replace(/^["']|["']$/g, '')
  if (!firstToken) return null
  const binary = firstToken.split(/[/\\]/).pop()?.toLowerCase() ?? ''
  if (!binary || !/^[a-z0-9._-]+$/.test(binary)) return null
  return binary
}

export interface ApprovalRuleDescriptor {
  signature: string
  buttonLabel: string
}

/**
 * Derives a bounded session-level rule signature so "Always allow similar in this session"
 * generalizes safely (for example, simple `git` or `pnpm` commands) without widening arbitrary scripts.
 */
export function describeSessionApprovalRule(
  request: Pick<PendingToolApproval, 'toolName' | 'summary' | 'argumentsText'>
): ApprovalRuleDescriptor {
  const name = request.toolName.trim().toLowerCase()
  if (SHELL_TOOL_NAMES.has(name)) {
    const { primaryTarget } = formatApprovalArguments(request.argumentsText, request.summary)
    const binary = extractSafeShellPrefix(primaryTarget)
    if (binary) {
      return {
        signature: `shell:${binary}`,
        buttonLabel: `本会话始终允许 ${binary} 命令`
      }
    }
    return {
      signature: `shell-exact:${primaryTarget || request.argumentsText.trim()}`,
      buttonLabel: '本会话始终允许此脚本'
    }
  }

  if (FILE_MUTATING_TOOL_NAMES.has(name)) {
    return {
      signature: `tool:${name}`,
      buttonLabel: '本会话始终允许编辑文件'
    }
  }

  return {
    signature: `tool:${name || 'unknown'}`,
    buttonLabel: `本会话始终允许 ${request.toolName || '此类操作'}`
  }
}

export function matchesSessionApprovalRule(
  rules: ReadonlySet<string> | undefined,
  request: Pick<PendingToolApproval, 'toolName' | 'summary' | 'argumentsText'>
): boolean {
  if (!rules || rules.size === 0) return false
  const descriptor = describeSessionApprovalRule(request)
  return rules.has(descriptor.signature)
}

export function enqueuePendingApproval(
  queue: PendingToolApproval[],
  next: PendingToolApproval
): PendingToolApproval[] {
  if (queue.some((item) => item.approvalId === next.approvalId)) return queue
  return [...queue, next]
}

export function removePendingApprovalById(
  queue: PendingToolApproval[],
  approvalId: string
): PendingToolApproval[] {
  return queue.filter((item) => item.approvalId !== approvalId)
}

export function removeFinishedApprovals(
  queue: PendingToolApproval[],
  opts: { sessionId?: string; requestId?: number }
): PendingToolApproval[] {
  return queue.filter((item) => {
    if (opts.requestId !== undefined && item.requestId === opts.requestId) return false
    if (opts.sessionId && item.sessionId === opts.sessionId && !item.background) return false
    return true
  })
}
