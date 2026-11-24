/// <reference types="node" />
import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import { applySubAgentEvent } from '../subagent-activity'
import { collectTurnDiff } from '../conversation-diffs'
import type { ChatMessage, SubAgentActivity, SubAgentLifecycleEvent } from '../types'
import { buildConversationProjection, projectConversationTurns, type ConversationProcessItem } from '../conversation-process'
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
  it('shows intermediate work live while running and collapses into 已完成工作 用时 after completion', async () => {
    const timedItems: ConversationProcessItem[] = [
      { message: { id: 'user-1', role: 'user', content: '检查项目', createdAt: 1_700_000_000_000 }, widgets: [], showFooter: false },
      {
        message: {
          id: 'step-1', role: 'assistant', content: '只在展开时挂载的详细工作记录', thinking: '先排查入口文件', createdAt: 1_700_000_002_000,
          tools: [{ id: 'read', name: 'read_file', filePath: '/src/app.ts', state: 'done' }],
          subAgents: [{ id: 'child-1', role: 'tester', status: 'running', live: true }]
        },
        widgets: [], showFooter: false, isIntermediate: true
      },
      {
        message: {
          id: 'answer-1', role: 'assistant', content: '项目检查结果已准备好', thinking: '汇总检查结果', state: 'streaming', createdAt: 1_700_000_018_000
        },
        widgets: [], showFooter: true
      }
    ]

    const liveTurn = projectConversationTurns(timedItems, { running: true, assistantId: 'answer-1' })[0]!
    const liveHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: liveTurn, assistantId: 'answer-1' }) }))
    expect(liveHtml).toContain('message-user-1')
    expect(liveHtml).toContain('process-live-timeline')
    expect(liveHtml).toContain('只在展开时挂载的详细工作记录')
    expect(liveHtml).toContain('项目检查结果已准备好')
    expect(liveHtml).not.toContain('process-history')
    expect(liveHtml).not.toContain('已完成思考')
    expect(liveHtml).not.toContain('先排查入口文件')
    expect(liveHtml).not.toContain('汇总检查结果')

    timedItems[1]!.message.subAgents![0]!.status = 'completed'
    timedItems[1]!.message.subAgents![0]!.live = false
    timedItems[2]!.message.state = 'done'
    const finishedTurn = projectConversationTurns(timedItems, { running: false })[0]!
    const collapsedHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: finishedTurn }) }))
    expect(collapsedHtml).toContain('已完成工作')
    expect(collapsedHtml).toContain('用时 18s')
    expect(collapsedHtml).not.toContain('项操作')
    expect(collapsedHtml).not.toContain('个步骤')
    expect(collapsedHtml).not.toContain('只在展开时挂载的详细工作记录')
    expect(collapsedHtml).not.toContain('process-history')
    expect(collapsedHtml).toContain('项目检查结果已准备好')
    // Finished reasoning remains inspectable inside the existing disclosure.
    expect(collapsedHtml).not.toContain('已完成思考')

    const expandedHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: finishedTurn, expanded: true }) }))
    expect(expandedHtml).toContain('process-history')
    expect(expandedHtml).toContain('只在展开时挂载的详细工作记录')
    expect(expandedHtml.match(/<details class="tool-group"/g)).toHaveLength(1)
    expect(expandedHtml).not.toContain('已完成思考')
    expect(expandedHtml).not.toContain('先排查入口文件')
    expect(expandedHtml).not.toContain('汇总检查结果')
    expect(expandedHtml).not.toContain('/src/app.ts')
  })

  it('restores progress and one collapsed action summary per work segment when reopening a saved turn', async () => {
    const { turns: [turn] } = buildConversationProjection([
      { id: 'u1', role: 'user', content: '修复保存问题', createdAt: 0 },
      { id: 'p1', role: 'assistant', content: '我先复现保存问题。', createdAt: 1 },
      { id: 'a1', role: 'assistant', content: '', thinking: '定位保存入口', createdAt: 2,
        tools: [{ id: 'read-1', name: 'read_file', filePath: '/src/save.ts', state: 'done' }] },
      { id: 'a2', role: 'assistant', content: '', thinking: '比较两条路径', createdAt: 3 },
      { id: 'a3', role: 'assistant', content: '', thinking: '更新保存逻辑', createdAt: 4,
        tools: [{ id: 'edit-1', name: 'edit_file', filePath: '/src/save.ts', state: 'done', addedLines: 2, removedLines: 1 }] },
      { id: 'a4', role: 'assistant', content: '', thinking: '运行检查', createdAt: 5,
        tools: [{ id: 'run-1', name: 'exec_command', detail: 'pnpm run smoke:save', output: 'smoke output', state: 'error' }] },
      { id: 'p2', role: 'assistant', content: '已经定位到问题，补上回归测试。', createdAt: 6 },
      { id: 'a5', role: 'assistant', content: '修复完成。', thinking: '整理验证结果', createdAt: 7,
        tools: [{ id: 'run-2', name: 'exec_command', detail: 'pnpm test -- save', state: 'done' }] }
    ])
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: turn!, expanded: true }) }))
    const progress = html.indexOf('已经定位到问题，补上回归测试。')
    expect(html.match(/<details class="tool-group"/g)).toHaveLength(2)
    expect(html).toContain('已编辑 1 个文件，读取并探索了 1 项，运行了 1 条命令')
    expect(progress).toBeGreaterThan(html.indexOf('已编辑 1 个文件'))
    expect(html.lastIndexOf('运行了 1 条命令')).toBeGreaterThan(progress)
    expect(html).toContain('1 项失败')
    expect(html).toMatch(/class="stat-add"[^>]*>\+2<\/span>/)
    for (const text of ['已完成思考', '思考记录', '定位保存入口', '/src/save.ts', 'pnpm run smoke:save', 'pnpm test -- save', 'smoke output']) {
      expect(html).not.toContain(text)
    }
    expect(html.indexOf('修复完成。')).toBeGreaterThan(html.indexOf('turn-response-host'))
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

  it('keeps a combined patch card with the finished response, independent of collapsed tool details', async () => {
    const source = items()
    const patch = (path: string, oldText: string, newText: string) =>
      `--- a/${path}\n+++ b/${path}\n@@ -1 +1 @@\n-${oldText}\n+${newText}\n`
    const edits = [
      { id: 'edit-1', name: 'edit_file', state: 'done', diff: patch('src/save.ts', 'old', 'first') },
      { id: 'edit-2', name: 'edit_file', state: 'done', diff: patch('src/save.test.ts', 'test before', 'test after') },
      { id: 'edit-3', name: 'edit_file', state: 'done', diff: patch('src/save.ts', 'first', 'second') }
    ] as const
    source[1]!.message.tools = [...edits]
    const live = projectConversationTurns(source, { running: true, assistantId: 'answer-1' })[0]!
    const liveHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: live, assistantId: 'answer-1' }) }))
    expect(liveHtml).not.toContain('查看代码变更')

    source[2]!.message.state = 'done'
    const finished = projectConversationTurns(source)[0]!
    const finishedHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: finished }) }))
    expect(finishedHtml).not.toContain('process-history')
    expect(finishedHtml.indexOf('查看代码变更')).toBeGreaterThan(finishedHtml.indexOf('项目检查结果已准备好'))
    expect(finishedHtml).toContain('2 个文件')
    expect(finishedHtml).toContain('新增 3 行，删除 3 行')

    const preview = collectTurnDiff(edits)
    expect(preview?.files.map((file) => [file.path, file.edits.length])).toEqual([['src/save.ts', 2], ['src/save.test.ts', 1]])
    expect(preview?.files[0]?.edits[1]?.hunks[0]?.lines.map((line) => line.content)).toEqual(['first', 'second'])
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
    expect(html.match(/thinking-shimmer/g)).toHaveLength(1)
    expect(html).not.toContain('分析需求')
    expect(html).not.toContain('lucide-loader-circle')
    expect(html).not.toContain('工作过程')
  })

  it('shows child lifecycle events between the tools received before and after them', async () => {
    const tools: NonNullable<ChatMessage['tools']> = [
      { id: 'read-1', name: 'read_file', state: 'done' },
      { id: 'spawn-1', name: 'subagent', input: '{"action":"spawn","role":"tester"}', state: 'running' }
    ]
    const message: ChatMessage = { id: 'model-step', role: 'assistant', content: '我先检查项目。', createdAt: 0, tools }
    const messages: ChatMessage[] = [{ id: 'prompt', role: 'user', content: '检查项目', createdAt: 0 }, message]
    const details = { subAgentId: 'child-1', name: '检查员', role: 'tester' }
    applySubAgentEvent(messages, { id: 1, event: 'subagent_start', data: '运行检查', details }, message)
    tools[1]!.state = 'done'
    tools.push({ id: 'edit-1', name: 'edit_file', state: 'done' })
    applySubAgentEvent(messages, { id: 1, event: 'subagent_done', data: '通过', details: { ...details, success: true } }, message)
    tools.push({ id: 'run-1', name: 'exec_command', input: '{"cmd":"pnpm test"}', state: 'running' })

    const { turns: [turn] } = buildConversationProjection(messages, { running: true, assistantId: message.id })
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: turn! }) }))
    const timeline = ['我先检查项目。', '读取并探索了 1 项', '检查员开始工作', '已编辑 1 个文件', '检查员已完成', 'pnpm test']
    const positions = timeline.map((text) => html.indexOf(text))
    expect(positions.every((position, index) => position >= 0 && (index === 0 || position > positions[index - 1]!))).toBe(true)
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
    expect(html).not.toContain('process-live-timeline')
  })

  it('keeps a single titled thinking status after progress while coalescing live tool steps across hidden reasoning', async () => {
    const source: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '检查项目', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'p1', role: 'assistant', content: '', thinking: '先检查入口文件', createdAt: 1,
        tools: [{ id: 'read-a', name: 'read_file', filePath: '/src/a.ts', state: 'done' }] }, widgets: [], showFooter: false },
      { message: { id: 'reasoning', role: 'assistant', content: '', thinking: '比较内部实现细节', createdAt: 2 }, widgets: [], showFooter: false },
      { message: { id: 'p2', role: 'assistant', content: '', thinking: '再检查依赖', createdAt: 3,
        tools: [{ id: 'read-b', name: 'read_file', filePath: '/src/b.ts', state: 'done' }] }, widgets: [], showFooter: false },
      { message: { id: 'progress', role: 'assistant', content: '入口和依赖已确认，继续检查边界。', createdAt: 4 }, widgets: [], showFooter: false },
      { message: { id: 'current', role: 'assistant', content: '', thinking: '## Keeping scope minimal\n内部推理内容',
        state: 'thinking', createdAt: 5 }, widgets: [], showFooter: false }
    ]
    const snapshot = JSON.stringify(source)
    const turn = projectConversationTurns(source, { running: true, assistantId: 'current' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'current' }) }))
    expect(html).toContain('读取并探索了 2 项')
    expect(html).toContain('入口和依赖已确认，继续检查边界。')
    expect(html.match(/thinking-shimmer/g)).toHaveLength(1)
    expect(html).toContain('Keeping scope minimal')
    expect(html.lastIndexOf('process-tail-status')).toBeGreaterThan(html.indexOf('继续检查边界'))
    expect(html).not.toContain('已完成思考')
    expect(html).not.toContain('先检查入口文件')
    expect(html).not.toContain('比较内部实现细节')
    expect(html).not.toContain('再检查依赖')
    expect(html).not.toContain('内部推理内容')
    expect(JSON.stringify(source)).toBe(snapshot)
  })

  it('shows the growing response without a redundant status row', async () => {
    const source: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '检查项目', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'a1', role: 'assistant', content: '已经确认入口，正在整理后续内容。', thinking: '不展示原始思考',
        state: 'streaming', createdAt: 1 }, widgets: [], showFooter: false }
    ]
    source[1]!.message.subAgents = [{ id: 'child-1', role: 'researcher', status: 'running', live: true }]
    const turn = projectConversationTurns(source, { running: true, assistantId: 'a1' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'a1' }) }))
    expect(html).toContain('已经确认入口，正在整理后续内容。')
    expect(html).not.toContain('process-tail-status')
    expect(html).not.toContain('不展示原始思考')
    expect(html).not.toContain('已完成思考')
  })

  it.each(['模型连接中断，正在重试 (1/3)', '正在压缩上下文'])('preserves the runtime phase as the single live status: %s', async (phase) => {
    const source: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '继续', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'a1', role: 'assistant', content: '', thinking: '**先检查项目**',
        state: 'thinking', createdAt: 1 }, widgets: [], showFooter: false }
    ]
    const turn = projectConversationTurns(source, { running: true, assistantId: 'a1' })[0]!
    const html = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn, assistantId: 'a1', phase }) }))
    expect(html).toContain(phase)
    expect(html.match(/role="status"/g)).toHaveLength(1)
    expect(html).not.toContain('thinking-shimmer')
    expect(html).not.toContain('先检查项目')
  })

  it('lets a running tool own its indicator and keeps errors reachable without a misleading tail status', async () => {
    const source: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '读取文件', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'a1', role: 'assistant', content: '', thinking: '检查内容', state: 'thinking', createdAt: 1,
        tools: [{ id: 'read', name: 'read_file', filePath: '/src/app.ts', state: 'running' }] }, widgets: [], showFooter: false }
    ]
    const activeTurn = projectConversationTurns(source, { running: true, assistantId: 'a1' })[0]!
    const activeHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: activeTurn, assistantId: 'a1' }) }))
    expect(activeHtml).toContain('正在读取')
    expect(activeHtml).not.toContain('process-tail-status')
    expect(activeHtml).not.toContain('thinking-shimmer')

    source[1]!.message.tools![0]!.state = 'error'
    source[1]!.message.errorMessage = '读取失败：访问被拒绝'
    const failedTurn = projectConversationTurns(source, { running: true, assistantId: 'a1' })[0]!
    const failedHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: failedTurn, assistantId: 'a1' }) }))
    expect(failedHtml).toContain('读取失败：访问被拒绝')
    expect(failedHtml).not.toContain('process-tail-status')
    expect(failedHtml).not.toContain('thinking-shimmer')
  })

  it('keeps one open work segment as active calls finish and join the completed tools', async () => {
    const messages = items().slice(0, 2).map(({ message }) => message)
    const commentary = messages[1]!
    commentary.tools = [
      { id: 'read', name: 'read_file', filePath: 'src/app.ts', state: 'done' },
      { id: 'search', name: 'grep', input: '{"pattern":"test"}', output: 'search result', state: 'running' },
      { id: 'shell', name: 'exec_command', input: '{"cmd":"pnpm test"}', state: 'running' }
    ]
    const { turns: [initial] } = buildConversationProjection(messages, { running: true, assistantId: commentary.id })
    const disclosures = new Set(['activity:message:step-1', 'tool:search'])
    const render = (turn: NonNullable<typeof initial>) => renderToString(createSSRApp({
      render: () => h(ConversationProcess, { turn, assistantId: commentary.id, activityDisclosures: disclosures })
    }))
    const active = await render(initial!)
    expect(active).not.toContain('process-title')
    expect(active.indexOf('只在展开时挂载的详细工作记录')).toBeLessThan(active.indexOf('读取并探索了 1 项'))
    expect(active.indexOf('读取并探索了 1 项')).toBeLessThan(active.indexOf('正在搜索'))
    expect(active).not.toContain('process-tail-status')

    commentary.tools[1]!.state = 'error'
    const { turns: [updated] } = buildConversationProjection(messages, { running: true, assistantId: commentary.id })
    const failed = await render(updated!)
    expect(failed.match(/<details class="tool-group"/g)).toHaveLength(1)
    expect(failed).toContain('读取并探索了 2 项')
    expect(failed).toContain('1 项失败')
    expect(failed).toContain('search result')
    expect(failed.indexOf('search result')).toBeLessThan(failed.indexOf('正在运行'))
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
    expect(html.match(/>读取并探索了 2 项<\/span>/g)).toHaveLength(1)
    expect(html.match(/>研究员已完成工作</g)).toHaveLength(1)
    expect(html).not.toContain('研究员已启动')
    expect(html.match(/class="process-step"/g)).toHaveLength(2)
    expect(html).toContain('aria-label="重新生成"')
  })

  it('shows one quiet thinking line before the first token and between tool calls', async () => {
    const initialTtft: ConversationProcessItem[] = [
      { message: { id: 'u1', role: 'user', content: '你好', createdAt: 0 }, widgets: [], showFooter: false },
      { message: { id: 'a1', role: 'assistant', content: '', thinking: '', state: 'thinking', thinkingActive: false, createdAt: 0 }, widgets: [], showFooter: false }
    ]
    const initialTurn = projectConversationTurns(initialTtft, { running: true, assistantId: 'a1' })[0]!
    const initialHtml = await renderToString(createSSRApp({ render: () => h(ConversationProcess, { turn: initialTurn, assistantId: 'a1' }) }))
    expect(initialHtml.match(/>正在思考<\/span>/g)).toHaveLength(1)
    expect(initialHtml).toContain('thinking-shimmer')
    expect(initialHtml).not.toContain('lucide-loader-circle')

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
    expect(expandedHtml.match(/>正在思考<\/span>/g)).toHaveLength(1)
    expect(expandedHtml.indexOf('正在思考')).toBeGreaterThan(expandedHtml.indexOf('已编辑'))
    expect(expandedHtml).not.toContain('正在等待模型响应')

    const pausedHtml = await renderToString(createSSRApp({
      render: () => h(ConversationProcess, { turn: expandedTurn, assistantId: 'p2', awaitingUser: true })
    }))
    expect(pausedHtml).toContain('已编辑')
    expect(pausedHtml).not.toContain('process-tail-status')
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

  it('supports shared read-only sub-agent mode with compact task disclosure and copy-only footer', async () => {
    const source = items()
    source[2]!.message.state = 'done'
    source[2]!.message.createdAt = 1_700_000_000_000
    const turn = projectConversationTurns(source)[0]!
    const html = await renderToString(createSSRApp({
      render: () => h(ConversationProcess, {
        turn,
        readOnly: true,
        compactTaskPrompt: true
      })
    }))

    expect(html).toContain('task-prompt-disclosure')
    expect(html).toContain('任务指令')
    expect(html).not.toContain('user-bubble')
    expect(html).not.toContain('编辑此消息')
    expect(html).toContain('已完成工作')
    expect(html).toContain('assistant-footer')
    expect(html).toContain('复制回答')
    expect(html).not.toContain('在此分叉新任务')
    expect(html).not.toContain('重新生成')
    expect(html).not.toContain('message-time')
  })
})
