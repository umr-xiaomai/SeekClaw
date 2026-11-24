import type { ToolActivity } from './types'

export interface BrowserResult {
  title: string
  url: string
  text: string
  elements: string[]
  tabId: string
  fallback: string
}

export function browserResultFromTool(tool: ToolActivity): BrowserResult {
  const output = tool.output?.trim() || ''
  let parsed: unknown = tool.structuredContent
  if (output) {
    try { parsed = JSON.parse(output) }
    catch { /* Older recordings and failed calls can contain plain text. */ }
  }
  const result = parsed && typeof parsed === 'object' && !Array.isArray(parsed)
    ? parsed as Record<string, unknown>
    : {}
  const stringField = (name: string): string => {
    const value = result[name]
    return typeof value === 'string' ? value : ''
  }
  const title = stringField('title').trim()
  const url = stringField('url')
  const text = stringField('text')
  const tabId = stringField('tab_id')
  const elements = Array.isArray(result.elements)
    ? result.elements.filter((entry): entry is string => typeof entry === 'string')
    : []
  const snapshot = Boolean(text || tabId || result.snapshot_id || elements.length)
  const typedText = tool.content?.flatMap(entry => typeof entry.text === 'string' ? [entry.text] : []).join('\n')
  return { title, url, text, elements, tabId, fallback: snapshot ? '' : (output || typedText || '') }
}
