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
    const collapsed = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn }) }))
    expect(collapsed).not.toContain('测试工程师已启动')
    expect(collapsed).not.toContain('测试工程师正在工作')
    expect(collapsed).not.toContain('测试工程师已完成工作')
    const expanded = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, expanded: true }) }))
    expect(expanded).toContain('测试工程师已完成工作')
    expect(expanded).not.toContain('测试工程师已启动')
  })

  it('hides completed sub-agent lifecycle rows when collapsed after turn end and reveals each once when expanded', async () => {
    const source = items()
    const agent: SubAgentActivity = { id: 'child-1', role: 'tester', status: 'completed', live: false }
    source[1]!.message.subAgents = [agent]
    const start: SubAgentLifecycleEvent = { id: 'child:start', agentId: agent.id, kind: 'started', agent: { ...agent, status: 'running' }, createdAt: 1, contentOffset: 0 }
    const done: SubAgentLifecycleEvent = { id: 'child:done', agentId: agent.id, kind: 'completed', agent, createdAt: 2, contentOffset: 0 }
    source[1]!.message.subAgentEvents = [start]
    source[2]!.message.subAgentEvents = [done]
    const finishedTurn = projectConversationTurns(source)[0]!
    const collapsed = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: finishedTurn }) }))
    expect(collapsed).not.toContain('data-subagent-event-id=')
    expect(collapsed).toContain('项目检查结果已准备好')
    const expanded = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: finishedTurn, expanded: true }) }))
    expect(expanded.match(/data-subagent-event-id=/g)).toHaveLength(2)
    expect(expanded).toContain('测试工程师开始工作')
    expect(expanded).toContain('测试工程师已完成')
  })

  it('renders inline Codex sub-agent lifecycle with lead-in commentary and shimmering thinking (no spinner)', async () => {
    const a1: SubAgentActivity = { id: 'a1', name: 'Demo explainer', role: 'researcher', status: 'running', live: true }
    const a2: SubAgentActivity = { id: 'a2', name: 'Demo scenario', role: 'general', status: 'completed', live: false }
    const liveItems: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '演示一下子智能体', createdAt: 0 }, widgets: [], showFooter: false },
      {
        message: {
          id: 'step-1',
          role: 'assistant',
          content: '我来做个简单演示：让两个子智能体分别介绍自己的分工和适用场景，再把结果汇总给你。',
          state: 'thinking',
          createdAt: 0,
          tools: [
            { id: 't1', name: 'subagent', input: '{"action":"spawn","role":"researcher","name":"Demo explainer"}', state: 'done' },
            { id: 't2', name: 'subagent', input: '{"action":"spawn","role":"general","name":"Demo scenario"}', state: 'done' }
          ],
          subAgents: [a1, a2],
          subAgentEvents: [
            { id: 'e1', agentId: 'a1', kind: 'started', agent: a1, createdAt: 1, contentOffset: 0 },
            { id: 'e2', agentId: 'a2', kind: 'started', agent: { ...a2, status: 'running' }, createdAt: 2, contentOffset: 0 },
            { id: 'e3', agentId: 'a2', kind: 'completed', agent: a2, createdAt: 3, contentOffset: 0 }
          ]
        },
        widgets: [],
        showFooter: false,
        isIntermediate: true
      }
    ]
    const turn = projectConversationTurns(liveItems, { running: true, assistantId: 'step-1' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'step-1' }) }))
    expect(html).toContain('我来做个简单演示')
    expect(html).toContain('Demo explainer')
    expect(html).toContain('Demo scenario')
    expect(html).toContain('Demo scenario已完成')
    expect(html).toContain('thinking-shimmer')
    expect(html).toContain('正在思考')
    expect(html).not.toContain('lucide-loader-circle')
    expect(html).not.toContain('工作过程')
  })

  it('uses shimmering 正在思考 without a spinner when thinking initially', async () => {
    const thinkingItems: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '你好', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'a1', role: 'assistant', content: '', thinking: '分析需求', state: 'thinking', createdAt: 0 }, widgets: [], showFooter: false }
    ]
    const turn = projectConversationTurns(thinkingItems, { running: true, assistantId: 'a1' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'a1' }) }))
    expect(html).toContain('thinking-shimmer')
    expect(html).toContain('正在思考')
    expect(html).not.toContain('lucide-loader-circle')
    expect(html).not.toContain('response-placeholder')
  })

  it('coalesces consecutive tool-only steps and deduplicates repeated sub-agent wait polls when expanded', async () => {
    const polled: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '分析项目', createdAt: 0 }, widgets: [], showFooter: false },
      {
        message: {
          id: 'p1', role: 'assistant', content: '先阅读主要文件。', createdAt: 0,
          tools: [{ id: 'spawn-1', name: 'subagent', input: '{"action":"spawn","role":"researcher"}', output: '{"id":"child-1","role":"researcher","status":"running"}', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 'p2', role: 'assistant', content: '', createdAt: 0,
          tools: [{ id: 'read-1', name: 'read_file', filePath: '/a.ts', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 'p3', role: 'assistant', content: '', createdAt: 0,
          tools: [{ id: 'read-2', name: 'read_file', filePath: '/b.ts', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 'p4', role: 'assistant', content: '', createdAt: 0,
          tools: [{ id: 'wait-1', name: 'subagent', input: '{"action":"wait","id":"child-1"}', output: '{"id":"child-1","role":"researcher","status":"running"}', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 'p5', role: 'assistant', content: '', createdAt: 0,
          tools: [{ id: 'wait-2', name: 'subagent', input: '{"action":"wait","id":"child-1"}', output: '{"id":"child-1","role":"researcher","status":"completed"}', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 'p6', role: 'assistant', content: '', createdAt: 0,
          tools: [{ id: 'wait-3', name: 'subagent', input: '{"action":"wait","id":"child-1"}', output: '{"id":"child-1","role":"researcher","status":"running"}', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: { id: 'ans', role: 'assistant', content: '分析完成。', state: 'done', createdAt: 0 },
        widgets: [], showFooter: true
      }
    ]
    const turn = projectConversationTurns(polled)[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, expanded: true }) }))
    expect(html.match(/读取并探索了文件/g)).toHaveLength(1)
    expect(html.match(/>研究员已完成工作</g)).toHaveLength(1)
    expect(html).not.toContain('研究员已启动')
    expect(html.match(/class="process-step"/g)).toHaveLength(2)
  })

  it('shows a non-thinking waiting indicator on TTFT and at the bottom of expanded process history', async () => {
    const initialTtft: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '你好', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'a1', role: 'assistant', content: '', thinking: '', state: 'thinking', thinkingActive: false, createdAt: 0 }, widgets: [], showFooter: false }
    ]
    const initialTurn = projectConversationTurns(initialTtft, { running: true, assistantId: 'a1' })[0]!
    const initialHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: initialTurn, assistantId: 'a1' }) }))
    expect(initialHtml).toContain('正在等待模型响应…')
    expect(initialHtml).not.toContain('thinking-shimmer')

    const expandedTtft: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '继续', createdAt: 0 }, widgets: [], showFooter: false },
      {
        message: {
          id: 'p1', role: 'assistant', content: '', thinking: '先修改脚本', createdAt: 0,
          tools: [{ id: 'edit-1', name: 'edit_file', filePath: 'tmp/build.py', state: 'done' }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: { id: 'p2', role: 'assistant', content: '', thinking: '', state: 'thinking', thinkingActive: false, createdAt: 0 },
        widgets: [], showFooter: false
      }
    ]
    const expandedTurn = projectConversationTurns(expandedTtft, { running: true, assistantId: 'p2' })[0]!
    const expandedHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: expandedTurn, assistantId: 'p2', expanded: true }) }))
    expect(expandedHtml).toContain('已编辑')
    expect(expandedHtml).toContain('正在等待模型响应…')
    expect(expandedHtml).not.toContain('thinking-shimmer')
  })

  it('shows preparing tool status below open commentary before tool_start arrives', async () => {
    const commentaryWithPreparing: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '继续', createdAt: 0 }, widgets: [], showFooter: false },
      {
        message: {
          id: 'a1',
          role: 'assistant',
          content: '好的，我换一种做法：改为手工用 HTML 排版。',
          thinking: '切换输出方案',
          state: 'streaming',
          preparingTool: 'edit_file',
          createdAt: 0
        },
        widgets: [],
        showFooter: true
      }
    ]
    const turn = projectConversationTurns(commentaryWithPreparing, { running: true, assistantId: 'a1' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'a1' }) }))
    expect(html).toContain('好的，我换一种做法')
    expect(html).toContain('正在准备编辑文件…')
  })

  it('renders context compaction as neutral system card and coalesces consecutive sub-agent launch steps', async () => {
    const compaction: ConversationProcessItem = {
      message: { id: 'sys-compact', role: 'user', content: '>>> [Context compaction] 总结\n已完成阶段性总结', createdAt: 0 },
      widgets: [],
      showFooter: false
    }
    const a1: SubAgentActivity = { id: 'sa-1', role: 'researcher', status: 'running', live: false }
    const a2: SubAgentActivity = { id: 'sa-2', role: 'researcher', status: 'running', live: false }
    const turnItems: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '分析架构', createdAt: 0 }, widgets: [], showFooter: false },
      {
        message: {
          id: 's1', role: 'assistant', content: '', createdAt: 1,
          subAgents: [a1],
          subAgentEvents: [{ id: 'ev-1', agentId: 'sa-1', kind: 'started', agent: a1, createdAt: 1, contentOffset: 0 }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 's2', role: 'assistant', content: '', createdAt: 2,
          subAgents: [a2],
          subAgentEvents: [{ id: 'ev-2', agentId: 'sa-2', kind: 'started', agent: a2, createdAt: 2, contentOffset: 0 }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      { message: { id: 'ans', role: 'assistant', content: '分析完毕', state: 'done', createdAt: 3 }, widgets: [], showFooter: true }
    ]

    const compactionHtml = await renderToString(createSSRApp({
      render: () => h(ConversationProcess, { turn: projectConversationTurns([compaction])[0]! })
    }))
    expect(compactionHtml).toContain('message-system')
    expect(compactionHtml).not.toContain('message-user')
    expect(compactionHtml).toContain('记忆压缩')

    const turnHtml = await renderToString(createSSRApp({
      render: () => h(ConversationProcess, { turn: projectConversationTurns(turnItems)[0]!, expanded: true })
    }))
    expect(turnHtml.match(/class="process-step"/g)).toHaveLength(1)
    expect(turnHtml).toContain('agent-lifecycle-group')
  })
})
