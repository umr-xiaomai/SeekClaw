const MAX_HEADING_LENGTH = 80
const graphemes = new Intl.Segmenter(undefined, { granularity: 'grapheme' })

/**
 * Use an explicitly titled reasoning section as a compact status. Ordinary
 * reasoning prose is not a summary and must never become the status label.
 */
export function extractThinkingHeading(thinking?: string): string | undefined {
  if (!thinking) return undefined
  const source = thinking.trimStart()
  const lineEnd = source.search(/[\r\n]/)
  const firstLine = lineEnd >= 0 ? source.slice(0, lineEnd) : source
  // Hash headings are complete only after a newline; a closing bold marker
  // already provides a boundary even when the rest of the stream is pending.
  const heading = (lineEnd >= 0 ? /^#{1,6}[\t ]+(.+?)(?:[\t ]+#+)?[\t ]*$/.exec(firstLine)?.[1] : undefined)
    ?? /^\*\*([^*]+)\*\*[\t ]*$/.exec(firstLine)?.[1]
  if (!heading) return undefined
  const text = heading.replace(/[\t ]+/g, ' ').trim()
  if (!text || /[`<>\[\]{}\u0000-\u001f\u007f]|\b[a-z][a-z\d+.-]*:\/\/|\bwww\./i.test(text)) return undefined
  const elements = Array.from(graphemes.segment(text), ({ segment }) => segment)
  return elements.length > MAX_HEADING_LENGTH
    ? elements.slice(0, MAX_HEADING_LENGTH - 1).join('') + '…'
    : text
}
