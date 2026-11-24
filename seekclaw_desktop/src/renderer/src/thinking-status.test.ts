import { describe, expect, it } from 'vitest'
import { extractThinkingHeading } from './thinking-status'

describe('thinking status heading', () => {
  it('accepts a completed first heading in either language without including its body', () => {
    expect(extractThinkingHeading('## Keeping scope minimal\nPrivate reasoning body')).toBe('Keeping scope minimal')
    expect(extractThinkingHeading(' \n### 检查 会话状态 ###\r\n详细的推理过程')).toBe('检查 会话状态')
    expect(extractThinkingHeading('**检查会话状态**')).toBe('检查会话状态')
    expect(extractThinkingHeading('**Keeping scope minimal**\nDetails')).toBe('Keeping scope minimal')
  })

  it('waits for a stable heading boundary while chunks arrive', () => {
    expect(extractThinkingHeading('# Keeping scope mini')).toBeUndefined()
    expect(extractThinkingHeading('**Keeping scope mini')).toBeUndefined()
    expect(extractThinkingHeading('**Keeping scope mini*')).toBeUndefined()
    expect(extractThinkingHeading('# Keeping scope minimal\n')).toBe('Keeping scope minimal')
  })

  it.each([
    undefined,
    '',
    'I need to inspect the implementation before changing it.\n## A later heading',
    '先检查当前状态，再决定修改范围。',
    'Keeping scope minimal',
    '**A heading** followed by private reasoning',
    '```ts\nconst value = 1',
    '# `console.log(value)`\n',
    '# [Read this](https://example.com)\n',
    '# https://example.com/report\n',
    '**Inspect www.example.com**',
    '# <script>alert(1)</script>\n',
    '# function example() { return value }\n'
  ])('does not promote prose, links, or code into a status: %s', (source) => {
    expect(extractThinkingHeading(source)).toBeUndefined()
  })

  it('bounds an explicit long heading to 80 complete graphemes including the ellipsis', () => {
    const emoji = '👩🏽‍💻'
    const text = emoji.repeat(100)
    const result = extractThinkingHeading(`**${text}**`)
    expect(result).toBe(emoji.repeat(79) + '…')
    expect(Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(result!))).toHaveLength(80)
    expect(extractThinkingHeading(`**${'e\u0301'.repeat(100)}**`)).toBe('e\u0301'.repeat(79) + '…')
  })
})
