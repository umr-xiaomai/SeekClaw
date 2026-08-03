<script setup lang="ts">
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import katex from 'katex'
import 'katex/dist/katex.min.css'
import MarkdownIt from 'markdown-it'
import { computed, nextTick, ref, watch } from 'vue'

const props = defineProps<{ content: string }>()

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('cs', csharp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('diff', diff)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('js', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('markdown', markdown)
hljs.registerLanguage('md', markdown)
hljs.registerLanguage('python', python)
hljs.registerLanguage('py', python)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('ts', typescript)
hljs.registerLanguage('vue', xml)
hljs.registerLanguage('html', xml)

const md = new MarkdownIt({
  html: false,
  linkify: true,
  typographer: true,
  breaks: true
})

md.renderer.rules.link_open = (tokens, index, options, _env, self) => {
  tokens[index]?.attrSet('target', '_blank')
  tokens[index]?.attrSet('rel', 'noreferrer')
  return self.renderToken(tokens, index, options)
}

md.renderer.rules.fence = (tokens, index) => {
  const token = tokens[index]!
  const language = token.info.trim().split(/\s+/)[0] ?? ''

  if (language.toLocaleLowerCase() === 'mermaid') {
    const encoded = encodeURIComponent(token.content)
    return `<div class="mermaid-render" data-src="${encoded}"><span class="mermaid-placeholder">加载图表…</span></div>`
  }

  const highlighted = language && hljs.getLanguage(language)
    ? hljs.highlight(token.content, { language, ignoreIllegals: true }).value
    : md.utils.escapeHtml(token.content)
  const encoded = encodeURIComponent(token.content)
  const label = language || 'text'
  return `<div class="code-block"><div class="code-header"><span>${md.utils.escapeHtml(label)}</span><button class="copy-code" data-code="${encoded}">复制</button></div><pre><code class="hljs language-${md.utils.escapeHtml(language)}">${highlighted}</code></pre></div>`
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
  result = result.replace(/(^|[^\\$])\$([^$\n]+?)\$/g, (_all, prefix: string, tex: string) => {
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
  const target = (event.target as HTMLElement).closest<HTMLButtonElement>('.copy-code')
  if (!target?.dataset.code) return
  await navigator.clipboard.writeText(decodeURIComponent(target.dataset.code))
  const previous = target.textContent
  target.textContent = '已复制'
  window.setTimeout(() => { target.textContent = previous }, 1200)
}
</script>

<template>
  <div ref="body" class="markdown-body" @click="handleClick" v-html="rendered" />
</template>
