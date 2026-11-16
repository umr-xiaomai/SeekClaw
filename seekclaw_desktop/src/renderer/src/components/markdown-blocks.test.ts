import MarkdownIt from 'markdown-it'
import { describe, expect, it } from 'vitest'
import { renderMarkdownBlocks } from './markdown-blocks'

describe('markdown block rendering', () => {
  const markdown = new MarkdownIt({ html: false, breaks: true, linkify: true })

  it.each([
    '# Heading\n\nFirst paragraph.\n\nSecond paragraph.',
    '- First\n  - Nested\n  - Another\n- Last\n\nAfter the list.',
    '- First paragraph.\n\n  Another paragraph in the same item.\n\n- Last item.',
    '> Quote\n>\n> - First\n> - Last\n\nAfter the quote.',
    '| First | Second |\n| --- | --- |\n| A | B |\n\nAfter the table.',
    '```typescript\nconst value = "text"\n```\n\nAfter the code.',
    '[Shared link][ref]\n\nAnother [link][ref].\n\n[ref]: https://example.com "Title"',
    'Before\n\n---\n\nAfter'
  ])('preserves document rendering and parser context: %s', (source) => {
    expect(renderMarkdownBlocks(markdown, source).join('')).toBe(markdown.render(source))
  })

  it('keeps a nested list and table within complete top-level blocks', () => {
    const blocks = renderMarkdownBlocks(markdown,
      '- First\n  - Nested\n- Last\n\n| Name | Value |\n| --- | --- |\n| A | B |\n\nTail')

    expect(blocks).toHaveLength(3)
    expect(blocks[0]).toMatch(/^<ul>[\s\S]*<ul>[\s\S]*<\/ul>[\s\S]*<\/ul>\n$/)
    expect(blocks[1]).toMatch(/^<table>[\s\S]*<\/table>\n$/)
    expect(blocks[2]).toBe('<p>Tail</p>\n')
  })

  it('leaves completed block HTML unchanged as new tokens extend only the tail', () => {
    const prefix = '# Finished heading\n\n```mermaid\ngraph TD; A-->B\n```\n\n'
    const first = renderMarkdownBlocks(markdown, `${prefix}The answer`)
    const second = renderMarkdownBlocks(markdown, `${prefix}The answer continues.`)

    expect(second.slice(0, -1)).toEqual(first.slice(0, -1))
    expect(second.at(-1)).toBe('<p>The answer continues.</p>\n')
  })

  it('allows a later reference definition to update its earlier block', () => {
    const before = renderMarkdownBlocks(markdown, '[Link][later]\n\nTail')
    const after = renderMarkdownBlocks(markdown, '[Link][later]\n\nTail\n\n[later]: https://example.com')

    expect(before[0]).toBe('<p>[Link][later]</p>\n')
    expect(after[0]).toContain('<a href="https://example.com">Link</a>')
  })
})
