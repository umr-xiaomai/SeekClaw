import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import InlineApprovalCard from './components/InlineApprovalCard.vue'
import {
  describeSessionApprovalRule,
  enqueuePendingApproval,
  extractSafeShellPrefix,
  formatApprovalArguments,
  matchesSessionApprovalRule,
  removeFinishedApprovals,
  removePendingApprovalById,
  type PendingToolApproval
} from './tool-approval'

function sampleApproval(overrides: Partial<PendingToolApproval> = {}): PendingToolApproval {
  return {
    approvalId: 'apr-1',
    callId: 'call-1',
    threadId: 'thread-1',
    sessionId: 'session-1',
    requestId: 10,
    toolName: 'bash',
    summary: 'git status',
    argumentsText: '{"command":"git status"}',
    createdAt: 1,
    ...overrides
  }
}

describe('tool approval helpers', () => {
  it('extracts safe shell command prefixes while refusing chained or redirected scripts', () => {
    expect(extractSafeShellPrefix('git diff --stat')).toBe('git')
    expect(extractSafeShellPrefix('  /usr/bin/pnpm test  ')).toBe('pnpm')
    expect(extractSafeShellPrefix('git status && rm -rf /')).toBeNull()
    expect(extractSafeShellPrefix('cat foo | grep bar')).toBeNull()
    expect(extractSafeShellPrefix('echo hi > out.txt')).toBeNull()
  })

  it('derives scoped session rules and matches subsequent similar calls', () => {
    const gitStatus = sampleApproval()
    const descriptor = describeSessionApprovalRule(gitStatus)
    expect(descriptor).toEqual({
      signature: 'shell:git',
      buttonLabel: '本会话始终允许 git 命令'
    })

    const rules = new Set([descriptor.signature])
    expect(matchesSessionApprovalRule(rules, sampleApproval({ argumentsText: '{"command":"git diff"}' }))).toBe(true)
    expect(matchesSessionApprovalRule(rules, sampleApproval({ argumentsText: '{"command":"rm -rf ."}' }))).toBe(false)

    const fileEdit = sampleApproval({ toolName: 'edit_file', argumentsText: '{"filePath":"src/a.ts"}' })
    expect(describeSessionApprovalRule(fileEdit).signature).toBe('tool:edit_file')
  })

  it('parses formatted arguments cleanly and manages per-thread queues', () => {
    const parsed = formatApprovalArguments('{"command":"dotnet test","cwd":"E:/repo"}')
    expect(parsed.primaryTarget).toBe('dotnet test')
    expect(parsed.isShell).toBe(true)

    let queue = enqueuePendingApproval([], sampleApproval({ approvalId: 'a1', requestId: 5 }))
    queue = enqueuePendingApproval(queue, sampleApproval({ approvalId: 'a2', requestId: 6, background: true }))
    queue = enqueuePendingApproval(queue, sampleApproval({ approvalId: 'a1', requestId: 5 }))
    expect(queue).toHaveLength(2)

    expect(removePendingApprovalById(queue, 'a1').map((x) => x.approvalId)).toEqual(['a2'])
    expect(removeFinishedApprovals(queue, { requestId: 5 }).map((x) => x.approvalId)).toEqual(['a2'])
  })

  it('renders InlineApprovalCard with session rule label and background task indicator', async () => {
    const html = await renderToString(createSSRApp({
      render: () => h(InlineApprovalCard, {
        approval: sampleApproval(),
        queueCount: 2,
        backgroundPending: []
      })
    }))
    expect(html).toContain('请求批准执行工具')
    expect(html).toContain('bash')
    expect(html).toContain('git status')
    expect(html).toContain('本会话始终允许 git 命令')
    expect(html).toContain('还有 1 项待处理')

    const bgHtml = await renderToString(createSSRApp({
      render: () => h(InlineApprovalCard, {
        approval: null,
        queueCount: 0,
        backgroundPending: [{ threadId: 't-bg', title: '重构任务', count: 1 }]
      })
    }))
    expect(bgHtml).toContain('后台任务「重构任务」有 1 项工具调用等待批准')
    expect(bgHtml).toContain('查看并处理')
  })
})
