import type { MarkdownIt } from 'markdown-it'

/** Parse once so references and nested structures retain their document context. */
export function renderMarkdownBlocks(markdown: MarkdownIt, source: string): string[] {
  const environment = {}
  const tokens = markdown.parse(source, environment)
  const blocks: string[] = []
  let start = 0
  let nesting = 0

  for (let index = 0; index < tokens.length; index++) {
    nesting += tokens[index]!.nesting
    if (nesting !== 0) continue

    // A complete top-level block may contain an entire list, table or blockquote.
    const html = markdown.renderer.render(tokens.slice(start, index + 1), markdown.options, environment)
    if (html.trim()) blocks.push(html)
    start = index + 1
  }

  return blocks
}
