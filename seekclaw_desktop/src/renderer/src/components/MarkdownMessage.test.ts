import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import MarkdownMessage from './MarkdownMessage.vue'

function render(content: string, streaming = false): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(MarkdownMessage, { content, streaming }) }))
}

describe('markdown message rendering', () => {
  it('keeps list, table and code controls intact in separate top-level blocks', async () => {
    const html = await render('- [x] Finished\n  - Nested\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n```typescript\nconst value = 1\n```')

    expect(html.match(/class="markdown-block"/g)).toHaveLength(3)
    expect(html).toContain('task-list-item-checkbox')
    expect(html).toContain('checked')
    expect(html).toContain('<li>Nested</li>')
    expect(html).toContain('class="table-container"')
    expect(html).toContain('class="copy-table"')
    expect(html).toContain('class="copy-code"')
    expect(html).toContain('class="hljs language-typescript"')
    expect(html).toContain('hljs-keyword')
  })

  it('preserves math rendering and literal dollar signs inside completed code fences', async () => {
    const html = await render('Math $x^2$\n\n```text\n$x^2$\n```')

    expect(html.match(/class="markdown-block"/g)).toHaveLength(2)
    expect(html).toContain('class="katex"')
    expect(html).toContain('language-text">$x^2$\n</code>')
    expect(html).not.toContain('@@MATH')
    expect(html).not.toContain('@@FENCE')
  })

  it.each(['```', '~~~', '````', '~~~~'])('preserves code dollar signs while a %s fence is still streaming', async (marker) => {
    const html = await render(`Math $a$\n\n${marker}text\n$x^2$\n$$literal$$`, true)

    expect(html.match(/class="katex"/g)).toHaveLength(1)
    expect(html).toContain('language-text">$x^2$\n$$literal$$</code>')
    expect(html).not.toContain('@@MATH')
  })

  it('only schedules completed diagrams while the following diagram is streaming', async () => {
    const html = await render('```mermaid\ngraph TD; A-->B\n```\n\n~~~mermaid\ngraph TD; C-->', true)

    expect(html.match(/class="mermaid-render"/g)).toHaveLength(2)
    expect(html.match(/data-src=/g)).toHaveLength(1)
    expect(html).toContain('正在生成图表…')
    expect(html).toContain(`data-src="${encodeURIComponent('graph TD; A-->B\n')}"`)
  })

  it('does not treat a shorter closing marker as completing a streaming diagram', async () => {
    const html = await render('````mermaid\ngraph TD; A-->B\n```', true)

    expect(html).toContain('正在生成图表…')
    expect(html).not.toContain('data-src=')
  })

  it('allows an unterminated diagram after streaming has finished', async () => {
    const html = await render('~~~mermaid\ngraph TD; A-->B')

    expect(html).toContain('data-src=')
    expect(html).not.toContain('正在生成图表…')
  })

  it.each(['text', 'mermaid'])('does not crash when a %s chunk ends in an unpaired surrogate', async (language) => {
    const html = await render(`~~~${language}\nvalue \uD83D`)
    const data = html.match(/data-(?:code|src)="([^"]+)"/)?.[1]

    expect(data).toBeDefined()
    expect(decodeURIComponent(data!)).toBe('value \uFFFD')
  })

  it('keeps a completed diagram separate from the growing paragraph that follows it', async () => {
    const prefix = '```mermaid\ngraph TD; A-->B\n```\n\n'
    const before = await render(`${prefix}An answer`)
    const after = await render(`${prefix}An answer continues.`)
    const diagram = (html: string) => html.match(/<div class="mermaid-render"[\s\S]*?<\/div>/)?.[0]

    expect(before.match(/class="markdown-block"/g)).toHaveLength(2)
    expect(diagram(before)).toBeDefined()
    expect(diagram(after)).toBe(diagram(before))
    expect(after).toContain('<p>An answer continues.</p>')
  })

  it('continues escaping untrusted HTML', async () => {
    const html = await render('<script>alert(1)</script>\n\nNormal text')

    expect(html).not.toContain('<script>')
    expect(html).toContain('&lt;script&gt;')
  })
})
