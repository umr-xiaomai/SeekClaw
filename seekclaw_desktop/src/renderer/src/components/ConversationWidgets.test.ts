/// <reference types="node" />
import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import ConversationWidgets from './ConversationWidgets.vue'
import type { ConversationWidget } from '../types'

describe('conversation widget rendering', () => {
  it('renders compact task controls and HTML/file actions without raw protocol data', async () => {
    const widgets: ConversationWidget[] = [
      { version: 1, kind: 'schedule', id: 'schedule:daily', title: 'Daily', task: { id: 'daily', name: '每日进度', prompt: '检查有意义的变化', cron: '0 9 * * *', enabled: true, sessionId: 'session', createdAt: '', updatedAt: '' } },
      { version: 1, kind: 'artifact', id: 'artifact:demo', title: '页面演示', path: 'E:\\demo.html', mediaType: 'text/html' },
      { version: 1, kind: 'artifact', id: 'artifact:report', title: '分析报告', path: 'E:\\report.pdf', mediaType: 'application/octet-stream' }
    ]
    const html = await renderToString(createSSRApp({ render: () => h(ConversationWidgets, { widgets, sessionId: 'session' }) }))
    for (const text of ['每日进度', '每天 09:00', '延续此对话', '暂停', '立即运行', '编辑', '打开交互预览', '分析报告', '打开位置']) expect(html).toContain(text)
    expect(html).not.toContain('"notificationPolicy"')
    expect(html).not.toContain('<iframe') // Previews load only after an explicit click.
  })

  it('keeps a deleted task as a receipt without runnable controls', async () => {
    const widgets: ConversationWidget[] = [{ version: 1, kind: 'schedule', id: 'schedule:daily', title: 'Daily', deleted: true,
      task: { id: 'daily', name: 'Daily', prompt: 'Check', cron: '0 9 * * *', enabled: false, createdAt: '', updatedAt: '' } }]
    const html = await renderToString(createSSRApp({ render: () => h(ConversationWidgets, { widgets }) }))
    expect(html).toContain('已删除')
    expect(html).not.toContain('立即运行')
    expect(html).not.toContain('>暂停<')
  })
})
