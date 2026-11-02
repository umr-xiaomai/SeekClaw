/// <reference types="node" />

import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import McpToolContent from './McpToolContent.vue'
import type { ToolActivity } from '../types'

const render = (result: Partial<ToolActivity>) => renderToString(createSSRApp({
  render: () => h(McpToolContent, { tool: { id: 'call', name: 'bash', state: 'done', ...result } })
}))

describe('tool results', () => {
  it('does not render a result for absent optional MCP fields', async () => {
    for (const structuredContent of [null, undefined]) {
      const html = await render({ structuredContent })
      expect(html).not.toContain('<details')
      expect(html).not.toContain('null')
    }
  })

  it('renders ordinary multiline output when structured content is null', async () => {
    const html = await render({ output: 'first line\nsecond line', structuredContent: null })
    expect(html).toContain('<details')
    expect(html).toContain('first line\nsecond line')
    expect(html).not.toContain('>null</pre>')
  })

  it.each([false, 0, '', { answer: 42 }])('renders valid structured value %j', async (structuredContent) => {
    const html = await render({ structuredContent })
    expect(html).toContain('<details')
    expect(html).toContain('<pre')
    if (structuredContent === false || structuredContent === 0)
      expect(html).toContain(String(structuredContent))
  })

  it('preserves a literal null text result', async () => {
    expect(await render({ output: 'null', structuredContent: null })).toContain('>null</pre>')
  })

  it('renders MCP blocks without duplicating their projected text', async () => {
    const html = await render({ content: [{ type: 'text', text: 'MCP text' }], output: 'MCP text' })
    expect(html.match(/MCP text/g)).toHaveLength(1)
  })
})
