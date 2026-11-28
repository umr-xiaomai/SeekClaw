import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import cpp from 'highlight.js/lib/languages/cpp'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import go from 'highlight.js/lib/languages/go'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('sh', bash)
hljs.registerLanguage('shell', bash)
hljs.registerLanguage('zsh', bash)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('cs', csharp)
hljs.registerLanguage('cpp', cpp)
hljs.registerLanguage('c++', cpp)
hljs.registerLanguage('c', cpp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('scss', css)
hljs.registerLanguage('diff', diff)
hljs.registerLanguage('go', go)
hljs.registerLanguage('golang', go)
hljs.registerLanguage('java', java)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('js', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('markdown', markdown)
hljs.registerLanguage('md', markdown)
hljs.registerLanguage('python', python)
hljs.registerLanguage('py', python)
hljs.registerLanguage('rust', rust)
hljs.registerLanguage('rs', rust)
hljs.registerLanguage('sql', sql)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('ts', typescript)
hljs.registerLanguage('vue', xml)
hljs.registerLanguage('html', xml)
hljs.registerLanguage('xml', xml)
hljs.registerLanguage('yaml', yaml)
hljs.registerLanguage('yml', yaml)

export { hljs }

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

/**
 * Splits syntax-highlighted HTML into per-line HTML fragments while balancing
 * `<span>` tags across line breaks so multiline tokens stay validly highlighted.
 */
export function highlightCodeLines(content: string, language: string): string[] {
  const normalized = content.replace(/\r\n?/g, '\n')
  const trimmed = normalized.length > 0 && normalized.endsWith('\n') ? normalized.slice(0, -1) : normalized
  if (!trimmed) return ['']

  const highlighted = language && hljs.getLanguage(language)
    ? hljs.highlight(trimmed, { language, ignoreIllegals: true }).value
    : escapeHtml(trimmed)

  const lines: string[] = []
  const openTags: string[] = []
  let current = ''
  let cursor = 0

  while (cursor < highlighted.length) {
    const ch = highlighted[cursor]
    if (ch === '\n') {
      lines.push(current + '</span>'.repeat(openTags.length))
      current = openTags.join('')
      cursor++
      continue
    }
    if (ch === '<') {
      const closeIdx = highlighted.indexOf('>', cursor)
      if (closeIdx !== -1) {
        const tag = highlighted.slice(cursor, closeIdx + 1)
        if (tag === '</span>') {
          if (openTags.length > 0) openTags.pop()
          current += tag
        } else if (tag.startsWith('<span')) {
          openTags.push(tag)
          current += tag
        } else {
          current += tag
        }
        cursor = closeIdx + 1
        continue
      }
    }
    const nextSpecial = highlighted.slice(cursor).search(/[\n<]/)
    if (nextSpecial === -1) {
      current += highlighted.slice(cursor)
      break
    }
    if (nextSpecial === 0) {
      current += ch
      cursor++
    } else {
      current += highlighted.slice(cursor, cursor + nextSpecial)
      cursor += nextSpecial
    }
  }

  lines.push(current + '</span>'.repeat(openTags.length))
  return lines
}
