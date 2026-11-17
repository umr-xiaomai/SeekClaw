import { describe, expect, it } from 'vitest'
import { projectConversationTurns, summarizeConversationProcess, type ConversationProcessItem } from './conversation-process'
import type { ChatMessage, ConversationWidget, ToolActivity } from './types'

function item(id: string, role: ChatMessage['role'], content = '', extra: Partial<ChatMessage> = {}): ConversationProcessItem {
  return { message: { id, role, content, createdAt: 0, ...extra }, widgets: [], showFooter: role === 'assistant' }
}
function tool(id: string, state: ToolActivity['state'] = 'done'): ToolActivity {
  return { id, name: 'read_file', detail: `${id}.ts`, state }
}

describe('conversation process projection', () => {
  it('collapses assistant history per user turn and leaves the final answer and all metadata intact', () => {
    const user = item('u1', 'user', 'Review this project')
    const read = item('a1', 'assistant', 'Inspecting files', { tools: [tool('read')] })
    const answer = item('a2', 'assistant', 'The review is complete.')
    answer.widgets = [{ version: 1, kind: 'artifact', id: 'artifact:report', title: 'Report', path: '/report.html', mediaType: 'text/html' }]
    const secondUser = item('u2', 'user', 'Explain the result')
    const secondAnswer = item('a3', 'assistant', 'Here is why.')
    const source = [user, read, answer, secondUser, secondAnswer]
    const turns = projectConversationTurns(source)

    expect(turns).toHaveLength(2)
    expect(turns[0]).toMatchObject({ id: 'turn:u1', user, process: [read], response: answer, running: false })
    expect(turns[1]).toMatchObject({ id: 'turn:u2', user: secondUser, process: [], response: secondAnswer })
    expect(turns[0]?.response).toBe(answer)
    expect(turns[0]?.response?.widgets).toBe(answer.widgets)
    expect(turns.flatMap((turn) => [turn.user, ...turn.process, turn.response].filter(Boolean))).toEqual(source)
  })

  it('keeps a stable turn id while a tool step becomes history and the answer streams', () => {
    const user = item('u1', 'user')
    const active = item('a1', 'assistant', 'Reading the source', { tools: [tool('read', 'running')], state: 'streaming' })
    const initial = projectConversationTurns([user, active], { running: true, assistantId: 'a1' })[0]!
    expect(initial.process).toEqual([active])
    expect(initial.response).toBeNull()
    expect(initial.running).toBe(true)

    const finished = item('a1', 'assistant', 'Reading the source', { tools: [tool('read')], state: 'done' })
    const answer = item('a2', 'assistant', 'The first finding is', { state: 'streaming' })
    const next = projectConversationTurns([user, finished, answer], { running: true, assistantId: 'a2' })[0]!
    expect(next.id).toBe(initial.id)
    expect(next.process).toEqual([finished])
    expect(next.response).toBe(answer)
  })

  it('shows a growing answer with completed tools while retaining tool-only or thinking tails in process', () => {
    const user = item('u1', 'user')
    const answer = item('a1', 'assistant', 'Result', { tools: [tool('read')], state: 'streaming' })
    expect(projectConversationTurns([user, answer], { running: true, assistantId: 'a1' })[0]?.response).toBe(answer)
    const thinking = item('a2', 'assistant', '', { thinking: 'Working through this', state: 'thinking' })
    expect(projectConversationTurns([user, thinking], { running: true })[0]?.process).toEqual([thinking])
    const toolOnly = item('a3', 'assistant', '', { tools: [tool('read')] })
    expect(projectConversationTurns([user, toolOnly])[0]?.response).toBeNull()
  })

  it('keeps errors and result cards outside history even when no final text was returned', () => {
    const failed = item('a1', 'assistant', '', { errorMessage: 'Provider disconnected', tools: [tool('read', 'error')] })
    const result = item('a2', 'assistant', '', { tools: [tool('save')] })
    const widget: ConversationWidget = { version: 1, kind: 'artifact', id: 'artifact:report', title: 'Report', path: '/report.txt', mediaType: 'text/plain' }
    result.widgets = [widget]
    expect(projectConversationTurns([failed])[0]).toMatchObject({ user: null, process: [], response: failed })
    expect(projectConversationTurns([result])[0]?.response).toBe(result)
  })

  it('marks only the owning turn as running and tolerates user-only and empty histories', () => {
    const turns = projectConversationTurns([item('u1', 'user'), item('a1', 'assistant', 'Done'), item('u2', 'user'), item('a2', 'assistant')], { running: true, assistantId: 'a2' })
    expect(turns.map((turn) => turn.running)).toEqual([false, true])
    expect(projectConversationTurns([item('u3', 'user')])[0]).toMatchObject({ process: [], response: null })
    expect(projectConversationTurns([])).toEqual([])
  })

  it('does not mark the previous answer as running before the new assistant placeholder is created', () => {
    const turns = projectConversationTurns([
      item('u1', 'user'), item('a1', 'assistant', 'Previous answer'), item('u2', 'user', 'Next request')
    ], { running: true, assistantId: 'a1' })
    expect(turns.map((turn) => turn.running)).toEqual([false, true])
    expect(turns[0]?.response?.message.id).toBe('a1')
    expect(turns[1]?.process).toEqual([])
  })

  it('summarizes completed and active operations without mutating the source', () => {
    const source = [item('a1', 'assistant', '', { tools: [tool('read'), tool('failed', 'error')] }), item('a2', 'assistant', '', { tools: [tool('active', 'running')] })]
    const summary = summarizeConversationProcess(source)
    expect(summary).toMatchObject({ stepCount: 2, toolCount: 3, errorCount: 1 })
    expect(summary.activeTool).toBe(source[1]?.message.tools?.[0])
    expect(summary.latestMessage).toBe(source[1]?.message)
    expect(source[0]?.message.tools).toHaveLength(2)
  })

  it('ignores hidden runtime verification and directive user prompts without splitting the turn', () => {
    const user = item('u1', 'user', '继续')
    const draft = item('a1', 'assistant', '已重做完成')
    const verifyPrompt = item('u-verify', 'user', 'The automatic verification step reported errors:\nCommand: dotnet build')
    const directivePrompt = item('u-dir', 'user', '>>> [System Directive: 本回合已达上限]')
    const afterVerify = item('a2', 'assistant', '结论：构建失败是因为锁文件。')
    const turns = projectConversationTurns([user, draft, verifyPrompt, directivePrompt, afterVerify])

    expect(turns).toHaveLength(1)
    expect(turns[0]?.user).toBe(user)
    expect(turns[0]?.process).toEqual([draft])
    expect(turns[0]?.response).toBe(afterVerify)
  })
})
