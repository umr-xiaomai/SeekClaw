import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import ToolActivityList from './ToolActivityList.vue'
import { toolKind, toolTarget } from './tool-presentation'
import type { ToolActivity } from '../types'

describe('conversation activities', () => {
  it('collapses consecutive completed edits and commands, keeping failures and running actions visible', async () => {
    const tools: ToolActivity[] = [
      { id: 'edit', name: 'edit_file', state: 'done', detail: 'app.ts', diff: '+code' },
      { id: 'run', name: 'command_session', state: 'done', detail: 'pnpm build', output: 'passed' },
      { id: 'failed', name: 'bash', state: 'error', detail: 'pnpm test', output: 'failure' },
      { id: 'running', name: 'browser', state: 'running', detail: 'https://example.com' }
    ]
    const html = await renderToString(createSSRApp({ render: () => h(ToolActivityList, { tools }) }))
    expect(html).toContain('已编辑 1 个文件，运行了 1 条命令')
    expect(html).toContain('+1')
    expect(html).toContain('命令失败')
    expect(html).toContain('正在浏览')
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
})
