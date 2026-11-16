/// <reference types="node" />
import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import type { SubAgentActivity, SubAgentLifecycleEvent } from '../types'
import { projectConversationTurns, type ConversationProcessItem } from '../conversation-process'
import ConversationProcess from './ConversationProcess.vue'

function items(): ConversationProcessItem[] {
  return [
    { message: { id: 'user-1', role: 'user', content: '检查项目', createdAt: 0 }, widgets: [], showFooter: false },
    { message: { id: 'step-1', role: 'assistant', content: '只在展开时挂载的详细工作记录', createdAt: 0,
      tools: [{ id: 'read', name: 'read_file', filePath: '/src/app.ts', state: 'done' }],
      subAgents: [{ id: 'child-1', role: 'tester', status: 'running', live: true }] }, widgets: [], showFooter: false, isIntermediate: true },
    { message: { id: 'answer-1', role: 'assistant', content: '项目检查结果已准备好', state: 'streaming', createdAt: 0 }, widgets: [], showFooter: true }
  ]
}

describe('conversation process presentation', () => {
  it('keeps detailed history unmounted by default while the user, streaming answer, and child lifecycle stay visible', async () => {
    const turn = projectConversationTurns(items(), { running: true, assistantId: 'answer-1' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'answer-1' }) }))
    expect(html).toContain('message-user-1')
    expect(html).toContain('项目检查结果已准备好')
    expect(html).toContain('测试工程师正在工作')
    expect(html).toContain('工作过程')
    expect(html).not.toContain('只在展开时挂载的详细工作记录')
    expect(html).not.toContain('process-history')
  })

  it('reveals searchable history without hiding the final result or its artifact action', async () => {
    const source = items()
    source[2]!.message.state = 'done'
    source[2]!.widgets = [{ version: 1, kind: 'artifact', id: 'artifact:report', title: '检查报告', path: '/report.html', mediaType: 'text/html' }]
    const turn = projectConversationTurns(source)[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, query: '详细工作记录', sessionId: 'session-1' }) }))
    expect(html).toContain('process-history')
    expect(html).toContain('只在展开时挂载的详细工作记录')
    expect(html).toContain('项目检查结果已准备好')
    expect(html).toContain('检查报告')
    expect(html).toContain('打开交互预览')
  })

  it('restores the disclosure state supplied by the virtualized conversation', async () => {
    const turn = projectConversationTurns(items())[0]!
    const expanded = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, expanded: true }) }))
    expect(expanded).toContain('process-history')
    const collapsed = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, expanded: false }) }))
    expect(collapsed).not.toContain('process-history')
  })

  it('keeps actual child completion ahead of a later stale tool snapshot across the turn', async () => {
    const source = items()
    source[1]!.message.subAgents![0]!.status = 'completed'
    source[1]!.message.subAgents![0]!.live = false
    source[2]!.message.tools = [{ id: 'status', name: 'subagent', input: '{"action":"status","id":"child-1"}',
      output: '{"id":"child-1","role":"tester","status":"running"}', state: 'done' }]
    const turn = projectConversationTurns(source)[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn }) }))
    expect(html).toContain('测试工程师已完成工作')
    expect(html).not.toContain('测试工程师已启动')
    expect(html).not.toContain('测试工程师正在工作')
  })

  it('keeps start and completion at separate flow positions and reveals each once when history expands', async () => {
    const source = items()
    const agent: SubAgentActivity = { id: 'child-1', role: 'tester', status: 'completed', live: false }
    source[1]!.message.subAgents = [agent]
    const start: SubAgentLifecycleEvent = { id: 'child:start', agentId: agent.id, kind: 'started', agent: { ...agent, status: 'running' }, createdAt: 1, contentOffset: 0 }
    const done: SubAgentLifecycleEvent = { id: 'child:done', agentId: agent.id, kind: 'completed', agent, createdAt: 2, contentOffset: 0 }
    source[1]!.message.subAgentEvents = [start]
    source[2]!.message.subAgentEvents = [done]
    const turn = projectConversationTurns(source)[0]!
    const collapsed = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn }) }))
    expect(collapsed.indexOf('data-subagent-event-id="child:start"')).toBeLessThan(collapsed.indexOf('项目检查结果已准备好'))
    expect(collapsed.indexOf('data-subagent-event-id="child:done"')).toBeGreaterThan(collapsed.indexOf('项目检查结果已准备好'))
    expect(collapsed.match(/data-subagent-event-id=/g)).toHaveLength(2)
    const expanded = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, expanded: true }) }))
    expect(expanded.match(/data-subagent-event-id=/g)).toHaveLength(2)
    expect(expanded).toContain('测试工程师开始工作')
    expect(expanded).toContain('测试工程师已完成工作')
  })

})
