import { Archive, Pencil } from '@lucide/vue'
import { createSSRApp, h } from 'vue'
import { renderToString, type SSRContext } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import ContextMenu from './ContextMenu.vue'
import type { ContextMenuEntry } from './context-menu'

async function render(items: ContextMenuEntry[], open = true): Promise<string> {
  const context: SSRContext = {}
  await renderToString(createSSRApp({ render: () => h(ContextMenu, {
    items, anchor: { x: 100, y: 200 }, open, label: '项目操作'
  }) }), context)
  return context.teleports?.body ?? ''
}

describe('context menu rendering', () => {
  it('renders supplied actions with icons, real shortcuts, separators and availability', async () => {
    const html = await render([
      { id: 'rename', label: '重命名', icon: Pencil, shortcut: 'F2' },
      { type: 'separator' },
      { id: 'archive', label: '归档', icon: Archive, disabled: true },
      { id: 'remove', label: '删除', danger: true }
    ])

    expect(html).toContain('role="menu"')
    expect(html).toContain('aria-label="项目操作"')
    expect(html.match(/role="menuitem"/g)).toHaveLength(3)
    expect(html).toContain('role="separator"')
    expect(html).toContain('F2')
    expect(html).toContain('lucide-pencil')
    expect(html).toContain('aria-disabled="true"')
    expect(html).toMatch(/class="[^"]*\bdanger\b[^"]*\bcontext-menu-item\b[^"]*"/)
    expect(html).not.toContain('复制')
  })

  it('exposes submenu semantics while keeping its actions closed initially', async () => {
    const html = await render([{ id: 'more', label: '更多', children: [{ id: 'archive', label: '归档子菜单' }] }])

    expect(html).toContain('aria-haspopup="menu"')
    expect(html).toContain('aria-expanded="false"')
    expect(html).not.toContain('归档子菜单')
  })

  it('focuses the first available item instead of disabled rows', async () => {
    const html = await render([
      { id: 'disabled', label: '禁用', disabled: true },
      { id: 'enabled', label: '可用' }
    ])

    expect(html.match(/tabindex="0"/g)).toHaveLength(1)
    expect(html).toMatch(/class="[^"]*\bactive\b[^"]*"[^>]*tabindex="0"/)
  })

  it('does not render a closed menu', async () => {
    const html = await render([{ id: 'rename', label: '重命名' }], false)

    expect(html).not.toContain('role="menu"')
    expect(html).not.toContain('重命名')
  })
})
