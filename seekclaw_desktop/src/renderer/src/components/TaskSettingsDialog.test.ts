import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import TaskSettingsDialog from './TaskSettingsDialog.vue'
import type { ThreadItem } from '../types'

const thread: ThreadItem = { id: 'task', title: '当前任务标题', messages: [], updatedAt: 0 }

function render(renameOnly: boolean): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(TaskSettingsDialog, {
    open: true, thread, renameOnly, project: { id: 'p1', name: 'Project', path: 'E:/project' }
  }) }))
}

describe('task title editing', () => {
  it('keeps rename mode focused on its title and two completion actions', async () => {
    const html = await render(true)

    expect(html).toContain('重命名任务')
    expect(html).toContain('aria-label="任务标题"')
    expect(html).toContain('value="当前任务标题"')
    expect(html).toContain('取消')
    expect(html).toContain('保存')
    for (const label of ['工作目录', 'Git worktree', '归档', '删除', '保存标题']) expect(html).not.toContain(label)
  })

  it('preserves workspace and lifecycle controls in the normal settings view', async () => {
    const html = await render(false)

    for (const label of ['任务设置', '工作目录', 'Git worktree', '归档', '删除', '保存标题']) expect(html).toContain(label)
  })
})
