<script setup lang="ts">
import katex from 'katex'
import 'katex/dist/katex.min.css'
import MarkdownIt from 'markdown-it'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import { getLinkVisualMeta, renderLinkIconHtml } from '../../../shared/markdown-links'
import { requestSideLinkOpen } from '../side-link-preview'
import { useStreamingText } from '../use-streaming-text'
import { hljs } from './hljs-setup'
import { renderMarkdownBlocks } from './markdown-blocks'
import { encodeMarkdownData, isClosedFence, transformOutsideFences } from './markdown-fences'

const props = defineProps<{ content: string; streaming?: boolean; workspace?: string }>()
const displayedContent = useStreamingText(() => props.content, () => props.streaming === true)

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

const defaultValidateLink = md.validateLink.bind(md)
md.validateLink = (url: string) => {
  if (/^file:\/\//i.test(url.trim())) return true
  return defaultValidateLink(url)
}

md.renderer.rules.link_open = (tokens, index, options, _env, self) => {
  const token = tokens[index]
  token?.attrSet('target', '_blank')
  token?.attrSet('rel', 'noreferrer')
  const rawHref = token?.attrGet('href')
  const hrefStr = typeof rawHref === 'string' ? rawHref : ''
  if (hrefStr && !token?.attrGet('title')) {
    let decodedTitle = hrefStr
    try {
      decodedTitle = decodeURIComponent(hrefStr)
    } catch {
      decodedTitle = hrefStr
    }
    token?.attrSet('title', decodedTitle)
  }
  let linkText = ''
  let depth = 1
  for (let i = index + 1; i < tokens.length && depth > 0; i++) {
    const child = tokens[i]
    if (!child) continue
    if (child.type === 'link_open') depth++
    else if (child.type === 'link_close') depth--
    else if (child.content) linkText += child.content
  }
  const meta = getLinkVisualMeta(hrefStr, linkText)
  token?.attrSet('class', `md-link md-link--${meta.kind}`)
  token?.attrSet('data-link-kind', meta.kind)
  return `${self.renderToken(tokens, index, options)}${renderLinkIconHtml(meta)}`
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

const CODE_COLLAPSE_LINE_THRESHOLD = 24
const wrappedBlockKeys = ref(new Set<string>())
const expandedBlockKeys = ref(new Set<string>())

function codeBlockKey(rawLang: string, content: string): string {
  // Fast deterministic key scoped per block content + language
  let hash = 2166136261
  for (let i = 0; i < content.length; i++) {
    hash ^= content.charCodeAt(i)
    hash = Math.imul(hash, 16777619)
  }
  return `${rawLang}:${content.length}:${hash >>> 0}`
}

function countCodeLines(content: string): number {
  const trimmedTrailing = content.endsWith('\n') ? content.slice(0, -1) : content
  if (!trimmedTrailing) return 1
  return trimmedTrailing.split('\n').length
}

md.renderer.rules.fence = (tokens, index) => {
  const token = tokens[index]!
  const rawLang = token.info.trim().split(/\s+/)[0] ?? ''

  if (rawLang.toLocaleLowerCase() === 'mermaid') {
    if (props.streaming && !isClosedFence(token)) {
      return '<div class="mermaid-render"><span class="mermaid-placeholder">正在生成图表…</span></div>'
    }
    const encoded = encodeMarkdownData(token.content)
    return `<div class="mermaid-render" data-src="${encoded}"><span class="mermaid-placeholder">加载图表…</span></div>`
  }

  const highlighted = rawLang && hljs.getLanguage(rawLang)
    ? hljs.highlight(token.content, { language: rawLang, ignoreIllegals: true }).value
    : md.utils.escapeHtml(token.content)
  const encoded = encodeMarkdownData(token.content)
  const label = formatLanguage(rawLang)
  const key = codeBlockKey(rawLang, token.content)
  const lineCount = countCodeLines(token.content)
  const isCollapsible = !props.streaming && lineCount > CODE_COLLAPSE_LINE_THRESHOLD
  const isExpanded = expandedBlockKeys.value.has(key)
  const isCollapsed = isCollapsible && !isExpanded
  const isWrapped = wrappedBlockKeys.value.has(key)

  const blockClasses = [
    'code-block',
    isWrapped ? 'is-wrapped' : '',
    isCollapsible ? 'is-collapsible' : '',
    isCollapsed ? 'is-collapsed' : ''
  ].filter(Boolean).join(' ')

  const foldBar = isCollapsible
    ? `<div class="code-fold-bar"><button class="code-fold-toggle" type="button" data-code-key="${key}" data-lines="${lineCount}">${isCollapsed ? `展开全部 ${lineCount} 行` : '收起代码'}</button></div>`
    : ''

  return `<div class="${blockClasses}" data-code-key="${key}" data-lines="${lineCount}"><div class="code-header"><div class="code-lang-badge"><svg class="code-lang-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg><span>${md.utils.escapeHtml(label)}</span>${isCollapsible ? `<span class="code-line-count">${lineCount} 行</span>` : ''}</div><div class="code-header-actions"><button class="code-action-btn toggle-wrap${isWrapped ? ' active' : ''}" type="button" data-code-key="${key}" aria-pressed="${isWrapped ? 'true' : 'false'}" title="切换自动换行" aria-label="切换自动换行"><span>${isWrapped ? '取消换行' : '换行'}</span></button><button class="copy-code" type="button" data-code="${encoded}" title="复制代码" aria-label="复制代码"><svg class="copy-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg><svg class="check-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg><span class="copy-text">复制</span></button></div></div><pre><code class="hljs language-${md.utils.escapeHtml(rawLang)}">${highlighted}</code></pre>${foldBar}</div>`
}

md.renderer.rules.table_open = (tokens, index, options, _env, self) => {
  return `<div class="table-container"><button class="copy-table" type="button" title="复制表格" aria-label="复制表格"><svg class="copy-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg><svg class="check-icon" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg><span class="copy-table-text">复制表格</span></button><div class="table-wrapper">${self.renderToken(tokens, index, options)}`
}

md.renderer.rules.table_close = (tokens, index, options, _env, self) => {
  return `${self.renderToken(tokens, index, options)}</div></div>`
}

// ---------------------------------------------------------------- math

const mathHtml: string[] = []

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

const renderedBlocks = computed(() => {
  mathHtml.length = 0
  const source = displayedContent.value
  const processed = source.includes('$') ? transformOutsideFences(md, source, renderMath) : source
  return renderMarkdownBlocks(md, processed).map((html) =>
    html.replace(/@@MATH(\d+)@@/g, (_all, index: string) => mathHtml[Number(index)] ?? '')
  )
})

// ---------------------------------------------------------------- mermaid

const body = ref<HTMLElement | null>(null)
const linkNotice = ref('')
let linkNoticeTimer: ReturnType<typeof setTimeout> | undefined

function showLinkNotice(message: string): void {
  linkNotice.value = message
  if (linkNoticeTimer) clearTimeout(linkNoticeTimer)
  linkNoticeTimer = setTimeout(() => {
    linkNotice.value = ''
    linkNoticeTimer = undefined
  }, 3500)
}
let mermaidPromise: Promise<typeof import('mermaid')> | null = null
let initializedMermaidTheme: 'dark' | 'default' | null = null
let mermaidSequence = 0
const mermaidPrefix = useId()
const pendingMermaids = new WeakSet<HTMLElement>()
const renderedMermaids = new WeakMap<HTMLElement, string>()

function resolveMermaidTheme(): 'dark' | 'default' {
  if (typeof document !== 'undefined') {
    const explicit = document.documentElement?.dataset?.theme
    if (explicit === 'dark') return 'dark'
    if (explicit === 'light') return 'default'
  }
  if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default'
  }
  return 'default'
}

const activeMermaidTheme = ref<'dark' | 'default'>(resolveMermaidTheme())

function syncMermaidTheme(): void {
  const next = resolveMermaidTheme()
  if (next !== activeMermaidTheme.value) {
    activeMermaidTheme.value = next
    void nextTick(() => void renderMermaid())
  }
}

let themeObserver: MutationObserver | null = null
let darkMediaQuery: MediaQueryList | null = null

onMounted(() => {
  if (typeof MutationObserver !== 'undefined' && typeof document !== 'undefined') {
    themeObserver = new MutationObserver(syncMermaidTheme)
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
  }
  if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    darkMediaQuery = window.matchMedia('(prefers-color-scheme: dark)')
    darkMediaQuery.addEventListener?.('change', syncMermaidTheme)
  }
})

onBeforeUnmount(() => {
  if (linkNoticeTimer) {
    clearTimeout(linkNoticeTimer)
    linkNoticeTimer = undefined
  }
  themeObserver?.disconnect()
  themeObserver = null
  darkMediaQuery?.removeEventListener?.('change', syncMermaidTheme)
  darkMediaQuery = null
})

function isCurrentMermaid(node: HTMLElement, encodedSource: string): boolean {
  return node.isConnected && body.value?.contains(node) === true && node.dataset.src === encodedSource
}

async function renderMermaid(): Promise<void> {
  const container = body.value
  if (!container) return
  const theme = activeMermaidTheme.value
  const nodes = Array.from(container.querySelectorAll<HTMLElement>('.mermaid-render[data-src]'))
    .filter((node) => {
      const renderKey = `${theme}:${node.dataset.src ?? ''}`
      return !pendingMermaids.has(node) && renderedMermaids.get(node) !== renderKey
    })
  if (nodes.length === 0) return
  for (const node of nodes) pendingMermaids.add(node)
  try {
    mermaidPromise ??= import('mermaid')
    const mermaid = (await mermaidPromise).default
    if (initializedMermaidTheme !== theme) {
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme })
      initializedMermaidTheme = theme
    }
    for (const node of nodes) {
      const encodedSource = node.dataset.src ?? ''
      if (!isCurrentMermaid(node, encodedSource)) continue
      const source = decodeURIComponent(encodedSource)
      if (!source) continue
      node.classList.add('is-loading')
      try {
        const id = `mermaid-${mermaidPrefix}-${++mermaidSequence}`
        const { svg } = await mermaid.render(id, source)
        if (isCurrentMermaid(node, encodedSource)) {
          node.innerHTML = svg
          renderedMermaids.set(node, `${theme}:${encodedSource}`)
        }
      } catch {
        if (isCurrentMermaid(node, encodedSource)) {
          node.innerHTML = `<pre class="mermaid-error">${md.utils.escapeHtml(source)}</pre>`
          renderedMermaids.set(node, `${theme}:${encodedSource}`)
        }
      } finally {
        node.classList.remove('is-loading')
      }
    }
  } catch {
    // Rendering unavailable; leave placeholders as-is.
  } finally {
    for (const node of nodes) pendingMermaids.delete(node)
  }
}

watch(renderedBlocks, () => { void nextTick(() => void renderMermaid()) }, { immediate: true })

async function handleClick(event: MouseEvent): Promise<void> {
  const target = event.target as HTMLElement

  // 1. Toggle wrap on code block
  const wrapBtn = target.closest<HTMLButtonElement>('.toggle-wrap')
  if (wrapBtn?.dataset.codeKey) {
    const key = wrapBtn.dataset.codeKey
    const next = new Set(wrappedBlockKeys.value)
    const nowWrapped = !next.has(key)
    if (nowWrapped) next.add(key)
    else next.delete(key)
    wrappedBlockKeys.value = next
    const block = wrapBtn.closest<HTMLElement>('.code-block')
    block?.classList.toggle('is-wrapped', nowWrapped)
    wrapBtn.classList.toggle('active', nowWrapped)
    wrapBtn.setAttribute('aria-pressed', String(nowWrapped))
    const labelSpan = wrapBtn.querySelector('span')
    if (labelSpan) labelSpan.textContent = nowWrapped ? '取消换行' : '换行'
    return
  }

  // 2. Fold/expand long code block
  const foldBtn = target.closest<HTMLButtonElement>('.code-fold-toggle')
  if (foldBtn?.dataset.codeKey) {
    const key = foldBtn.dataset.codeKey
    const totalLines = foldBtn.dataset.lines || ''
    const next = new Set(expandedBlockKeys.value)
    const nowExpanded = !next.has(key)
    if (nowExpanded) next.add(key)
    else next.delete(key)
    expandedBlockKeys.value = next
    const block = foldBtn.closest<HTMLElement>('.code-block')
    block?.classList.toggle('is-collapsed', !nowExpanded)
    foldBtn.textContent = nowExpanded ? '收起代码' : `展开全部 ${totalLines} 行`
    return
  }

  // 3. Copy code block
  const copyCodeBtn = target.closest<HTMLButtonElement>('.copy-code')
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

  // 4. Copy table
  const copyTableBtn = target.closest<HTMLButtonElement>('.copy-table')
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
    return
  }

  // 5. Open links (external URLs, local file paths, workspace-relative paths, fragments)
  const anchor = target.closest<HTMLAnchorElement>('a[href]')
  if (anchor) {
    const rawHref = anchor.getAttribute('href')?.trim() ?? ''
    event.preventDefault()
    if (!rawHref) return

    if (rawHref.startsWith('#')) {
      let targetId = rawHref.slice(1)
      try {
        targetId = decodeURIComponent(targetId)
      } catch {
        // keep raw fragment
      }
      if (targetId && typeof document !== 'undefined') {
        const matched = document.getElementById(targetId)
          ?? (typeof CSS !== 'undefined' && typeof CSS.escape === 'function'
            ? body.value?.querySelector<HTMLElement>(`#${CSS.escape(targetId)}`) ?? null
            : null)
        matched?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      }
      return
    }

    if (typeof window !== 'undefined') {
      requestSideLinkOpen({
        href: rawHref,
        workspace: props.workspace,
        label: anchor.textContent?.trim(),
        trigger: anchor
      })
      return
    }

    showLinkNotice('无法在当前环境打开该链接')
  }
}
</script>

<template>
  <div v-if="renderedBlocks.length" ref="body" class="markdown-body" @click="handleClick">
    <div v-for="(html, index) in renderedBlocks" :key="index" class="markdown-block" v-html="html" />
    <p v-if="linkNotice" class="markdown-link-notice" role="status">{{ linkNotice }}</p>
  </div>
</template>

<style scoped>
.markdown-block {
  display: contents;
}

.markdown-link-notice {
  margin: 6px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

.markdown-body :deep(.markdown-block:first-child > :first-child) {
  margin-top: 0;
}

.markdown-body :deep(.markdown-block:last-child > :last-child) {
  margin-bottom: 0;
}
</style>
