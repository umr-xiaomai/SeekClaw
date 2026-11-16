import MarkdownIt from 'markdown-it'
import { describe, expect, it } from 'vitest'
import { encodeMarkdownData, isClosedFence, transformOutsideFences } from './markdown-fences'

const markdown = new MarkdownIt()
const replaceMath = (source: string) => source.replace(/\$([^$]+)\$/g, '<math>$1</math>')

describe('code fence preprocessing', () => {
  it.each([
    '```text\n$x$\n```',
    '```text\n$x$',
    '~~~text\n$x$\n~~~',
    '~~~text\n$x$',
    '````text\n```\n$x$\n````',
    '~~~~text\n~~~\n$x$',
    '> ```text\n> $x$\n> ```',
    '- ~~~text\n  $x$\n  ~~~'
  ])('protects the actual fenced range: %s', (fence) => {
    expect(transformOutsideFences(markdown, `Before $a$\n\n${fence}`, replaceMath))
      .toBe(`Before <math>a</math>\n\n${fence}`)
  })

  it('continues transforming after a closed fence without interpreting shorter markers as a close', () => {
    const source = '````text\n$x$\n```\n$y$\n````\n\nAfter $z$'
    expect(transformOutsideFences(markdown, source, replaceMath))
      .toBe('````text\n$x$\n```\n$y$\n````\n\nAfter <math>z</math>')
  })

  it('uses parser boundaries when an unclosed nested fence ends with its blockquote', () => {
    const source = '> ~~~text\n> $x$\n\nOutside $y$'
    expect(transformOutsideFences(markdown, source, replaceMath))
      .toBe('> ~~~text\n> $x$\n\nOutside <math>y</math>')
  })

  it('normalizes CRLF before mapping fence source lines', () => {
    expect(transformOutsideFences(markdown, 'Before $a$\r\n\r\n~~~text\r\n$x$\r\n~~~\r\n\r\nAfter $b$', replaceMath))
      .toBe('Before <math>a</math>\n\n~~~text\n$x$\n~~~\n\nAfter <math>b</math>')
  })
})

describe('fence completion', () => {
  it.each([
    ['```mermaid\ngraph TD; A-->B\n```', true],
    ['~~~mermaid\ngraph TD; A-->B\n~~~~\t', true],
    ['```mermaid\n```', true],
    ['```mermaid', false],
    ['```mermaid\ngraph TD; A-->B', false],
    ['````mermaid\ngraph TD; A-->B\n```', false],
    ['```mermaid\ngraph TD; A-->B\n~~~', false],
    ['```mermaid\ngraph TD; A-->B\n> ```', false],
    ['> ```mermaid\n> graph TD; A-->B\n> ```', true],
    ['> ```mermaid\n> graph TD; A-->B\n\nOutside', false],
    ['- ~~~mermaid\n  graph TD; A-->B\n  ~~~', true]
  ])('identifies the parsed closing marker: %s', (source, closed) => {
    const token = markdown.parse(source, {}).find((item) => item.type === 'fence')!
    expect(isClosedFence(token)).toBe(closed)
  })
})

describe('encoded Markdown data', () => {
  it('preserves valid Unicode, including emoji and combining sequences', () => {
    const source = 'const label = "👩‍💻 café e\u0301"\n'
    expect(decodeURIComponent(encodeMarkdownData(source))).toBe(source)
  })

  it('replaces unpaired surrogates while preserving adjacent complete pairs', () => {
    const source = '\uD83D\uD83D\uDE00\uDE00'
    expect(() => encodeMarkdownData(source)).not.toThrow()
    expect(decodeURIComponent(encodeMarkdownData(source))).toBe('\uFFFD😀\uFFFD')
  })
})
