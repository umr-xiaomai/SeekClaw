import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import ToolActivityList from './ToolActivityList.vue'
import ToolCallRow from './ToolCallRow.vue'
import { toolKind, toolTarget } from './tool-presentation'
import type { ToolActivity } from '../types'

describe('conversation activities', () => {
  it('collapses consecutive settled edits and commands without failure badges, keeping running actions visible', async () => {
    const tools: ToolActivity[] = [
      { id: 'edit', name: 'edit_file', state: 'done', detail: 'app.ts', diff: '+code' },
      { id: 'run', name: 'command_session', state: 'done', detail: 'pnpm build', output: 'passed' },
      { id: 'failed', name: 'bash', state: 'error', detail: 'pnpm test', output: 'failure' },
      { id: 'running', name: 'browser', state: 'running', detail: 'https://example.com' }
    ]
    const html = await renderToString(createSSRApp({ render: () => h(ToolActivityList, { tools }) }))
    expect(html).toContain('已编辑 1 个文件，运行了 2 条命令')
    expect(html).toContain('+1')
    expect(html).not.toContain('命令失败')
    expect(html).not.toContain('项失败')
    expect(html).toContain('正在检查页面')
    expect(html).not.toContain('查看结果')
    expect(html).not.toContain('<pre') // Heavy results mount only when expanded.
  })
  it('classifies persistent command sessions and grep and preserves multiline command input', () => {
    expect(toolKind({ id: 'x', name: 'command_session', state: 'done' })).toBe('shell')
    expect(toolKind({ id: 'x', name: 'grep', state: 'done' })).toBe('search')
    expect(toolTarget({ id: 'x', name: 'bash', state: 'done', detail: '{"command":"first\\nsecond"}' })).toBe('first\nsecond')
  })

  it('shows +N/-M diff counts on edit rows and group headers without command durations or exit badges', async () => {
    const tools: ToolActivity[] = [
      { id: 'e1', name: 'edit_file', state: 'done', filePath: 'a.ts', diff: '--- a/a.ts\n+++ b/a.ts\n@@ -1 +1,2 @@\n-x\n+y\n+z' },
      { id: 's1', name: 'bash', state: 'done', detail: 'pnpm test' }
    ]
    const html = await renderToString(createSSRApp({ render: () => h(ToolActivityList, { tools }) }))
    expect(html).toContain('+2')
    expect(html).toContain('-1')
    expect(html).not.toContain('exit 0')
  })

  it('shows a terminal exit error even when the runtime invocation succeeded', async () => {
    const tool: ToolActivity = {
      id: 'command', name: 'exec_command', state: 'done', input: '{"cmd":"pnpm test"}',
      output: JSON.stringify({ sessionId: 'pty', status: 'exited', exitCode: 1, output: 'One test failed' })
    }
    const html = await renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool, expanded: true }) }))
    expect(html).toContain('命令失败')
    expect(html).toContain('$ pnpm test')
    expect(html).toContain('退出码 1')
    expect(html).toContain('One test failed')
    expect(html).not.toContain('is-error')
    expect(html).not.toContain('command-error')
    expect(html).not.toContain('lucide-circle-alert')
    expect(html).not.toContain('sessionId')
  })

  it('leaves terminal input values out of the transcript and marks pending approval', async () => {
    const tool: ToolActivity = {
      id: 'write', name: 'write_stdin', state: 'running',
      input: JSON.stringify({ session_id: 'pty', chars: 'credential-example' }),
      detail: 'session_id: pty, chars: credential-example'
    }
    const html = await renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool, expanded: true, awaitingApproval: true }) }))
    expect(html).toContain('等待批准')
    expect(html).toContain('session_id')
    expect(html).not.toContain('credential-example')
    expect(html).not.toContain('run-dot')
  })

  it('labels an unfinished call without leaving an approval or running indicator', async () => {
    const tool: ToolActivity = { id: 'shell', name: 'exec_command', detail: 'pnpm test', state: 'interrupted' }
    const html = await renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool, awaitingApproval: true }) }))
    expect(html).toContain('is-interrupted')
    expect(html).toContain('未完成')
    expect(html).not.toContain('等待批准')
    expect(html).not.toContain('run-dot')
  })

  it('shows named plan steps and their status when the plan update is expanded', async () => {
    const tool: ToolActivity = {
      id: 'plan', name: 'update_plan', state: 'done',
      input: JSON.stringify({
        explanation: '更新验证步骤',
        steps: [
          { title: '定位菜单', status: 'completed' },
          { title: '精简菜单', status: 'in_progress', detail: '保留删除确认' },
          { title: '执行回归检查', status: 'pending' }
        ]
      }),
      output: 'Plan updated successfully with 3 steps (1 completed).'
    }
    const renderRow = (expanded: boolean) => renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool, expanded }) }))
    expect(await renderRow(false)).not.toContain('执行回归检查')
    const html = await renderRow(true)
    expect(html).toContain('aria-label="计划步骤"')
    expect(html).toContain('更新验证步骤')
    expect(html).toMatch(/定位菜单<\/span><span[^>]*>，已完成<\/span>/)
    expect(html).toMatch(/精简菜单<\/span><span[^>]*>，进行中<\/span>/)
    expect(html).toMatch(/执行回归检查<\/span><span[^>]*>，待处理<\/span>/)
    expect(html).toContain('保留删除确认')
    expect(html).toContain(tool.output)
    expect(html).not.toContain('aria-label="工具参数"')
  })

  it('keeps the raw parameters available when historical plan arguments do not have valid steps', async () => {
    const tool: ToolActivity = {
      id: 'legacy-plan', name: 'update_plan', state: 'done',
      detail: '{"steps":[{"title":"旧计划"}]}'
    }
    const html = await renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool, expanded: true }) }))
    expect(html).toContain('aria-label="工具参数"')
    expect(html).toContain('旧计划')
  })

  it('keeps a finished browser result available through its details', async () => {
    const tool: ToolActivity = {
      id: 'browser', name: 'browser', state: 'done', input: '{"action":"snapshot"}',
      output: '{"title":"Test page","url":"https://example.com","text":"Menu preview"}'
    }
    const html = await renderToString(createSSRApp({ render: () => h(ToolCallRow, { tool, expanded: true }) }))
    expect(html).toContain('已检查页面')
    expect(html).toContain('读取页面')
    expect(html).toContain('Menu preview')
    expect(html).toContain('在右侧查看浏览结果')
  })
})
