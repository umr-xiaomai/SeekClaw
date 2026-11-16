import type { MarkdownIt, Token } from 'markdown-it'

/** Apply text preprocessing only outside code fences recognized by Markdown. */
export function transformOutsideFences(markdown: MarkdownIt, source: string, transform: (text: string) => string): string {
  // Block source maps refer to normalized lines, including inside lists and quotes.
  const normalized = source.replace(/\r\n?/g, '\n').replace(/\0/g, '\uFFFD')
  const tokens: Token[] = []
  markdown.block.parse(normalized, markdown, {}, tokens)
  const lineOffsets = [0]
  for (let index = 0; index < normalized.length; index++) {
    if (normalized[index] === '\n') lineOffsets.push(index + 1)
  }

  const result: string[] = []
  let cursor = 0
  for (const token of tokens) {
    if (token.type !== 'fence' || !token.map) continue
    const start = lineOffsets[token.map[0]] ?? normalized.length
    const end = lineOffsets[token.map[1]] ?? normalized.length
    result.push(transform(normalized.slice(cursor, start)), normalized.slice(start, end))
    cursor = end
  }
  result.push(transform(normalized.slice(cursor)))
  return result.join('')
}

/** A closing fence adds one source line beyond the opener and content lines. */
export function isClosedFence(token: Token): boolean {
  if (token.type !== 'fence' || !token.map) return false
  let contentLines = token.content && !token.content.endsWith('\n') ? 1 : 0
  for (const character of token.content) {
    if (character === '\n') contentLines++
  }
  return token.map[1] - token.map[0] === contentLines + 2
}

/** A transport chunk can end halfway through a Unicode surrogate pair. */
export function encodeMarkdownData(source: string): string {
  const wellFormed = source.replace(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g, '\uFFFD')
  return encodeURIComponent(wellFormed)
}
