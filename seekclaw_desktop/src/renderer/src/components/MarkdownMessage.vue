<script setup lang="ts">
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
import katex from 'katex'
import 'katex/dist/katex.min.css'
import MarkdownIt from 'markdown-it'
import { computed, nextTick, ref, watch } from 'vue'

const props = defineProps<{ content: string }>()

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

const md = new MarkdownIt({
  html: false,
  linkify: true,
  typographer: true,
  breaks: true
})

// Enable task lists: - [ ] or - [x]
md.core.ruler.after('inline', 'task-lists', (state) => {
  const tokens = state.tokens
  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i]?.type !== 'inline') continue
    const inlineToken = tokens[i]!
    const children = inlineToken.children
    if (!children || children.length === 0) continue

    const firstChild = children[0]
    if (firstChild?.type === 'text') {
      const match = firstChild.content.match(/^\[([ xX])\]\s+/)
      if (match) {
        const checked = match[1]?.toLowerCase() === 'x'
        firstChild.content = firstChild.content.slice(match[0].length)
        const checkboxToken = new state.Token('html_inline', '', 0)
        checkboxToken.content = `<input type="checkbox" class="task-list-item-checkbox" disabled ${checked ? 'checked' : ''} /> `
        children.unshift(checkboxToken)

        for (let j = i - 1; j >= 0; j--) {
          if (tokens[j]?.type === 'list_item_open') {
            tokens[j]?.attrJoin('class', 'task-list-item')
            break
          }
        }
      }
    }
  }
})

md.renderer.rules.link_open = (tokens, index, options, _env, self) => {
  tokens[index]?.attrSet('target', '_blank')
  tokens[index]?.attrSet('rel', 'noreferrer')
  return self.renderToken(tokens, index, options)
}

function formatLanguage(lang: string): string {
  const map: Record<string, string> = {
    python: 'Python', py: 'Python',
    typescript: 'TypeScript', ts: 'TypeScript',
    javascript: 'JavaScript', js: 'JavaScript',
    csharp: 'C#', cs: 'C#',
    cpp: 'C++', 'c++': 'C++', c: 'C',
    rust: 'Rust', rs: 'Rust',
    go: 'Go', golang: 'Go',
    java: 'Java',
    html: 'HTML', css: 'CSS', scss: 'SCSS',
    json: 'JSON',
    bash: 'Bash', sh: 'Shell', shell: 'Shell', zsh: 'Zsh',
    powershell: 'PowerShell', pwsh: 'PowerShell',
    sql: 'SQL',
    yaml: 'YAML', yml: 'YAML',
    markdown: 'Markdown', md: 'Markdown',
    diff: 'Diff',
    dockerfile: 'Dockerfile', docker: 'Dockerfile',
    text: 'Text', txt: 'Text'
  }
  const lower = lang.toLowerCase()
  return map[lower] || (lang ? lang.charAt(0).toUpperCase() + lang.slice(1) : 'Text')
}

md.renderer.rules.fence = (tokens, index) => {
  const token = tokens[index]!
  const rawLang = token.info.trim().split(/\\s+/)[0] ?? ''

  if (rawLang.toLocaleLowerCase() === 'mermaid') {
    const encoded = encodeURIComponent(token.content)
    return `<div class="mermaid-render" data-src="${encoded}"><span class="mermaid-placeholder">加载图表…</span></div>`
  }

  const highlighted = rawLang && hljs.getLanguage(rawLang)
    ? hljs.highlight(token.content, { language: rawLang, ignoreIllegals: true }).value
    : md.utils.escapeHtml(token.content)
  const encoded = encodeURIComponent(token.content)
  const label = formatLanguage(rawLang)

  return `<div class="code-block"><div class="code-header"><div class="code-lang-badge"><svg class="code-lang-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg><span>${md.utils.escapeHtml(label)}</span></div><button class="copy-code" type="button" data-code="${encoded}" title="复制代码" aria-label="复制代码"><svg class="copy-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg><svg class="check-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg><span class="copy-text">复制</span></button></div><pre><code class="hljs language-${md.utils.escapeHtml(rawLang)}">${highlighted}</code></pre></div>`
}

md.renderer.rules.table_open = (tokens, index, options, _env, self) => {
  return `<div class="table-container"><button class="copy-table" type="button" title="复制表格" aria-label="复制表格"><svg class="copy-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg><svg class="check-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg><span class="copy-table-text">复制表格</span></button><div class="table-wrapper">${self.renderToken(tokens, index, options)}`
}

md.renderer.rules.table_close = (tokens, index, options, _env, self) => {
  return `${self.renderToken(tokens, index, options)}</div></div>`
}

// ---------------------------------------------------------------- math

const mathHtml: string[] = []

/** Extracts fenced code (math inside code must stay untouched). */
function protectFences(source: string): { text: string; fences: string[] } {
  const fences: string[] = []
  const text = source.replace(/```[\s\S]*?```/g, (fence) => {
    const token = `@@FENCE${fences.length}@@`
    fences.push(fence)
    return token
  })
  return { text, fences }
}

function renderMath(source: string): string {
  let result = source
  result = result.replace(/\$\$([\s\S]+?)\$\$/g, (_all, tex: string) => {
    const token = `@@MATH${mathHtml.length}@@`
    mathHtml.push(katex.renderToString(tex, { displayMode: true, throwOnError: false }))
    return token
  })
  result = result.replace(/(^|[^\$])\$([^$\n]+?)\$/g, (_all, prefix: string, tex: string) => {
    const token = `@@MATH${mathHtml.length}@@`
    mathHtml.push(katex.renderToString(tex, { displayMode: false, throwOnError: false }))
    return `${prefix}${token}`
  })
  return result
}

const rendered = computed(() => {
  mathHtml.length = 0
  const { text, fences } = protectFences(props.content)
  let processed = renderMath(text)
  fences.forEach((fence, index) => {
    processed = processed.replace(`@@FENCE${index}@@`, fence)
  })
  const html = md.render(processed)
  return html.replace(/@@MATH(\d+)@@/g, (_all, index: string) => mathHtml[Number(index)] ?? '')
})

// ---------------------------------------------------------------- mermaid

const body = ref<HTMLElement | null>(null)
let mermaidPromise: Promise<typeof import('mermaid')> | null = null
let mermaidSequence = 0

async function renderMermaid(): Promise<void> {
  const container = body.value
  if (!container) return
  const nodes = Array.from(container.querySelectorAll<HTMLElement>('.mermaid-render[data-src]'))
  if (nodes.length === 0) return
  try {
    mermaidPromise ??= import('mermaid').then((mod) => {
      mod.default.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' })
      return mod
    })
    const mermaid = (await mermaidPromise).default
    for (const node of nodes) {
      const source = decodeURIComponent(node.dataset.src ?? '')
      if (!source) continue
      node.classList.add('is-loading')
      try {
        const id = `mermaid-${++mermaidSequence}`
        const { svg } = await mermaid.render(id, source)
        node.innerHTML = svg
      } catch {
        node.innerHTML = `<pre class="mermaid-error">${md.utils.escapeHtml(source)}</pre>`
      } finally {
        node.classList.remove('is-loading')
      }
    }
  } catch {
    // Rendering unavailable; leave placeholders as-is.
  }
}

watch(rendered, () => { void nextTick(() => void renderMermaid()) }, { immediate: true })

async function handleClick(event: MouseEvent): Promise<void> {
  // 1. Copy code block
  const copyCodeBtn = (event.target as HTMLElement).closest<HTMLButtonElement>('.copy-code')
  if (copyCodeBtn?.dataset.code) {
    await navigator.clipboard.writeText(decodeURIComponent(copyCodeBtn.dataset.code))
    copyCodeBtn.classList.add('copied')
    const textSpan = copyCodeBtn.querySelector('.copy-text')
    if (textSpan) textSpan.textContent = '已复制'
    window.setTimeout(() => {
      copyCodeBtn.classList.remove('copied')
      if (textSpan) textSpan.textContent = '复制'
    }, 1500)
    return
  }

  // 2. Copy table
  const copyTableBtn = (event.target as HTMLElement).closest<HTMLButtonElement>('.copy-table')
  if (copyTableBtn) {
    const container = copyTableBtn.closest('.table-container')
    const table = container?.querySelector('table')
    if (table) {
      const rows = Array.from(table.querySelectorAll('tr'))
      const tsv = rows.map((row) =>
        Array.from(row.querySelectorAll('th, td')).map((cell) => cell.textContent?.trim() || '').join('\t')
      ).join('\n')
      await navigator.clipboard.writeText(tsv)
      copyTableBtn.classList.add('copied')
      const textSpan = copyTableBtn.querySelector('.copy-table-text')
      if (textSpan) textSpan.textContent = '已复制'
      window.setTimeout(() => {
        copyTableBtn.classList.remove('copied')
        if (textSpan) textSpan.textContent = '复制表格'
      }, 1500)
    }
  }
}
</script>

<template>
  <div v-if="rendered.trim()" ref="body" class="markdown-body" @click="handleClick" v-html="rendered" />
</template>
