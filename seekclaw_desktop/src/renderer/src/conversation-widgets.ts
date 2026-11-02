import type { ChatMessage, ConversationWidget } from './types'

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** Reject unknown versions/kinds rather than interpreting model-provided markup or actions. */
export function parseConversationWidgets(value: unknown): ConversationWidget[] {
  if (!Array.isArray(value)) return []
  return value.filter((widget): widget is ConversationWidget => {
    if (!record(widget) || widget.version !== 1 || typeof widget.id !== 'string' || !widget.id
      || typeof widget.title !== 'string' || !widget.title) return false
    if (widget.kind === 'artifact') return typeof widget.path === 'string' && !!widget.path
      && typeof widget.mediaType === 'string' && (widget.description == null || typeof widget.description === 'string')
    if (widget.kind === 'schedule') {
      const task = widget.task
      return record(task) && typeof task.id === 'string' && !!task.id && typeof task.name === 'string'
        && typeof task.prompt === 'string' && typeof task.cron === 'string' && typeof task.enabled === 'boolean'
        && (widget.deleted == null || typeof widget.deleted === 'boolean')
    }
    return false
  })
}

/** Collect each turn's tool cards at its last assistant bubble; the newest resource snapshot wins. */
export function widgetsAtTurnTails(
  messages: ChatMessage[],
  activeTurn?: { running: boolean; startMessageId?: string }
): Map<string, ConversationWidget[]> {
  const tails = new Map<string, ConversationWidget[]>()
  const explicitStart = activeTurn?.startMessageId
    ? messages.findIndex((message) => message.id === activeTurn.startMessageId) : -1
  const activeStart = activeTurn?.running
    ? (explicitStart >= 0 ? explicitStart : Math.max(0, messages.findLastIndex((message) => message.role === 'user')))
    : messages.length
  let widgets = new Map<string, ConversationWidget>()
  let tail: string | undefined
  const flush = (): void => {
    if (tail && widgets.size) tails.set(tail, [...widgets.values()])
    widgets = new Map()
    tail = undefined
  }
  for (let index = 0; index < messages.length; index++) {
    const message = messages[index]!
    if (message.role === 'user') { flush(); continue }
    // Tool completion is not turn completion. Publish the final resource snapshot
    // only after the owning turn settles, including when guidance adds user bubbles.
    if (index >= activeStart) continue
    tail = message.id
    for (const tool of message.tools ?? []) {
      for (const widget of tool.widgets ?? []) widgets.set(widget.id, widget)
    }
  }
  flush()
  return tails
}

export function describeCron(cron: string): string {
  const parts = cron.trim().split(/\s+/)
  const [minute, hour, day, month, weekday] = parts
  if (parts.length !== 5 || day !== '*' || month !== '*') return cron
  if (hour === '*' && weekday === '*') {
    const interval = minute?.match(/^\*\/(\d+)$/)?.[1]
    if (interval) return `每 ${interval} 分钟`
    if (minute === '0') return '每小时'
  }
  if (/^\d+$/.test(minute ?? '') && /^\d+$/.test(hour ?? '')) {
    const time = `${hour!.padStart(2, '0')}:${minute!.padStart(2, '0')}`
    if (weekday === '*') return `每天 ${time}`
    const days: Record<string, string> = { '0': '日', '1': '一', '2': '二', '3': '三', '4': '四', '5': '五', '6': '六', '7': '日' }
    if (weekday && days[weekday]) return `每周${days[weekday]} ${time}`
  }
  return cron
}

/** An opaque-origin iframe can run inline demos but cannot access the app, network APIs or parent DOM. */
export function previewDocument(html: string): string {
  const policy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'"
  return `<!doctype html><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${policy}">${html}`
}
