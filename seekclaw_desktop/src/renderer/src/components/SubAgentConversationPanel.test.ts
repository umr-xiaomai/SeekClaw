/// <reference types="node" />
import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { buildConversationProjection } from '../conversation-process'
import { hydrateMessages } from '../app-helpers'
import ConversationProcess from './ConversationProcess.vue'
import SubAgentConversationPanel from './SubAgentConversationPanel.vue'

const originalWindow = globalThis.window

describe('SubAgentConversationPanel Codex layout & shared turn rendering', () => {
  beforeEach(() => {
    ;(globalThis as unknown as { window: unknown }).window = {
      seekclaw: {
        daemon: {
          request: async () => ({ data: '{}' }),
          onEvent: () => () => {}
        }
      }
    }
  })

  afterEach(() => {
    if (originalWindow === undefined) {
      delete (globalThis as unknown as { window?: unknown }).window
    } else {
      globalThis.window = originalWindow
    }
  })

  it('renders resizable handle, pill tab with close button, Codex detail header, and removes legacy header/footer chrome', async () => {
    const html = await renderToString(createSSRApp({
      render: () => h(SubAgentConversationPanel, {
        agent: {
          id: 'child-1',
          name: 'Demo scenario',
          role: 'researcher',
          model: '只因API/gpt-6.1-sol',
          status: 'completed',
          task: '比较两种路线优缺点'
        },
        agents: [
          {
            agent: {
              id: 'child-1',
              name: 'Demo scenario',
              role: 'researcher',
              model: '只因API/gpt-6.1-sol',
              status: 'completed'
            },
            updatedAt: 1_700_000_000_000,
            active: false
          }
        ],
        sessionId: 'parent-1',
        workspace: 'E:/Project/SeekClaw',
        width: 520
      })
    }))

    expect(html).toContain('subagent-panel-resize-handle')
    expect(html).toContain('--subagent-panel-width:520px')
    expect(html).toContain('subagent-tab-pill')
    expect(html).toContain('子智能体')
    expect(html).toContain('subagent-tab-close')
    expect(html).toContain('关闭子智能体')
    expect(html).toContain('subagent-back-btn')
    expect(html).toContain('agent-emblem')
    expect(html).toContain('Demo scenario')
    expect(html).toContain('gpt-6.1-sol')
    expect(html).not.toContain('child-panel-header')
    expect(html).not.toContain('child-panel-footer')
    expect(html).not.toContain('只读')
    expect(html).not.toContain('刷新工作过程')
  })

  it('renders hydrated child session turns identically to main chat via buildConversationProjection + ConversationProcess', async () => {
    const childMessages = hydrateMessages({
      id: 'child-session-1',
      createdAt: '2026-11-17T10:00:00Z',
      updatedAt: '2026-11-17T10:01:00Z',
      messages: [
        { role: 'user', text: '规划周末旅行并核对开放时间', timestamp: 1_700_000_000_000 },
        {
          role: 'assistant',
          text: '先读取本地文档',
          timestamp: 1_700_000_003_000,
          toolCalls: [{ id: 'tc-1', name: 'read_file', input: '{"path":"README.md"}' }]
        },
        { role: 'tool', text: '{"ok":true}', toolName: 'read_file', toolCallId: 'tc-1', timestamp: 1_700_000_005_000 },
        { role: 'assistant', text: '例如规划一次周末旅行：整理景点并与交通住宿整合。', timestamp: 1_700_000_012_000 }
      ]
    })

    const projection = buildConversationProjection(childMessages, { running: false })
    expect(projection.turns).toHaveLength(1)

    const html = await renderToString(createSSRApp({
      render: () => h(ConversationProcess, {
        turn: projection.turns[0]!,
        imageSources: projection.imageSources,
        readOnly: true,
        compactTaskPrompt: true
      })
    }))

    expect(html).toContain('task-prompt-disclosure')
    expect(html).toContain('任务指令')
    expect(html).not.toContain('user-bubble')
    expect(html).toContain('已完成工作')
    expect(html).toContain('用时 12s')
    expect(html).not.toContain('项操作')
    expect(html).toContain('例如规划一次周末旅行：整理景点并与交通住宿整合。')
    expect(html).toContain('复制回答')
    expect(html).not.toContain('在此分叉新任务')
    expect(html).not.toContain('重新生成')
  })
})
