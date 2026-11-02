import { describe, expect, it } from 'vitest'
import { describeCron, parseConversationWidgets, previewDocument, widgetsAtTurnTails } from './conversation-widgets'
import type { ArtifactWidget, ChatMessage, ScheduleWidget } from './types'

const artifact: ArtifactWidget = { version: 1, kind: 'artifact', id: 'artifact:demo', title: 'Demo', path: 'E:\\demo.html', mediaType: 'text/html' }
const schedule: ScheduleWidget = { version: 1, kind: 'schedule', id: 'schedule:daily', title: 'Daily', task: {
  id: 'daily', name: 'Daily', prompt: 'Check progress', cron: '0 9 * * *', enabled: true, createdAt: '', updatedAt: ''
} }
const assistant = (id: string, widgets = [artifact]): ChatMessage => ({ id, role: 'assistant', content: '', createdAt: 0,
  tools: [{ id: `tool:${id}`, name: 'test', state: 'done', widgets }] })

describe('conversation widgets', () => {
  it('validates known widget kinds and versions without accepting action markup', () => {
    expect(parseConversationWidgets([artifact, schedule])).toEqual([artifact, schedule])
    expect(parseConversationWidgets([null, {}, { ...artifact, version: 2 }, { ...artifact, kind: 'script' },
      { ...schedule, task: { ...schedule.task, enabled: 'yes' } }, { ...artifact, path: null }])).toEqual([])
    expect(parseConversationWidgets(null)).toEqual([])
  })

  it('moves cards to the tail of each turn and replaces repeated resource snapshots', () => {
    const updated = { ...artifact, title: 'Updated demo' }
    const user: ChatMessage = { id: 'user', role: 'user', content: 'next', createdAt: 0 }
    const result = widgetsAtTurnTails([assistant('a1'), assistant('a2', [updated]), { ...assistant('a3'), tools: [] }, user, assistant('a4')])
    expect([...result.keys()]).toEqual(['a3', 'a4'])
    expect(result.get('a3')).toEqual([updated])
    expect(result.get('a4')).toEqual([artifact])
  })

  it('waits for turn completion while keeping cards from previous turns visible', () => {
    const user = (id: string): ChatMessage => ({ id, role: 'user', content: 'work', createdAt: 0 })
    const messages = [user('old-user'), assistant('old-result'), user('current-user'), assistant('write'),
      { ...assistant('final'), state: 'streaming' as const, tools: [] }]
    expect([...widgetsAtTurnTails(messages, { running: true, startMessageId: 'current-user' }).keys()])
      .toEqual(['old-result'])
    expect([...widgetsAtTurnTails(messages, { running: false }).keys()]).toEqual(['old-result', 'final'])
  })

  it('does not publish unfinished cards when guidance introduces another user bubble', () => {
    const user = (id: string): ChatMessage => ({ id, role: 'user', content: 'work', createdAt: 0 })
    const messages = [user('start'), assistant('write'), user('guidance'), assistant('edit')]
    expect(widgetsAtTurnTails(messages, { running: true, startMessageId: 'start' }).size).toBe(0)
    expect(widgetsAtTurnTails(messages, { running: true }).has('edit')).toBe(false)
  })

  it('keeps the final resource snapshot until the whole turn settles, even if a tool is done', () => {
    const messages = [assistant('write'), assistant('present', [{ ...artifact, title: 'Ready' }])]
    expect(widgetsAtTurnTails(messages, { running: true }).size).toBe(0)
    expect(widgetsAtTurnTails(messages, { running: false }).get('present')).toEqual([{ ...artifact, title: 'Ready' }])
  })

  it.each([
    ['0 9 * * *', '每天 09:00'], ['*/30 * * * *', '每 30 分钟'], ['0 * * * *', '每小时'],
    ['0 18 * * 1', '每周一 18:00'], ['0 9 1 * *', '0 9 1 * *']
  ])('describes %s without changing its meaning', (cron, expected) => expect(describeCron(cron)).toBe(expected))

  it('puts the restrictive CSP before untrusted HTML and leaves inline demos functional', () => {
    const html = '<script>document.body.dataset.ready = "yes"</script>'
    const result = previewDocument(html)
    expect(result.indexOf('Content-Security-Policy')).toBeLessThan(result.indexOf(html))
    expect(result).toContain("connect-src 'none'")
    expect(result).toContain("frame-src 'none'")
    expect(result).toContain("script-src 'unsafe-inline'")
  })
})
