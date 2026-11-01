import { describe, expect, it } from 'vitest'
import type { ChatMessage } from '../types'
import {
  cleanMarkdownPreview,
  extractConversationTurns
} from './conversation-turns'

describe('conversation-turns', () => {
  it('cleans markdown symbols from preview text', () => {
    const raw = '## 标题\n这是包含 `inline_code` 和 **粗体** 的段落。\n```ts\nconst a = 1\n```'
    const cleaned = cleanMarkdownPreview(raw)
    expect(cleaned).toContain('标题 这是包含 inline_code 和 粗体 的段落。 [代码块]')
    expect(cleaned).not.toContain('##')
    expect(cleaned).not.toContain('**')
  })

  it('extracts turns with user text and assistant preview', () => {
    const messages: ChatMessage[] = [
      {
        id: 'msg-1',
        role: 'user',
        content: '请你回复这个PR的说明问题',
        createdAt: 1730455200000
      },
      {
        id: 'msg-2',
        role: 'assistant',
        content: '已在 PR #162 发布代码审查评论，内容包括 2 个问题修复。',
        createdAt: 1730455205000
      },
      {
        id: 'msg-3',
        role: 'user',
        content: '好的，请继续修改',
        createdAt: 1730455500000
      },
      {
        id: 'msg-4',
        role: 'assistant',
        content: '正在为你调整对应文件...',
        createdAt: 1730455503000
      }
    ]

    const turns = extractConversationTurns(messages)
    expect(turns.length).toBe(2)

    expect(turns[0]?.id).toBe('msg-1')
    expect(turns[0]?.userText).toBe('请你回复这个PR的说明问题')
    expect(turns[0]?.assistantPreview).toContain('已在 PR #162 发布代码审查评论')
    expect(turns[0]?.index).toBe(1)

    expect(turns[1]?.id).toBe('msg-3')
    expect(turns[1]?.userText).toBe('好的，请继续修改')
    expect(turns[1]?.assistantPreview).toContain('正在为你调整对应文件')
    expect(turns[1]?.index).toBe(2)
  })

  it('skips system memory and error cards', () => {
    const messages: ChatMessage[] = [
      {
        id: 'sys-1',
        role: 'user',
        content: 'System: [Memory Recall] 用户偏好中文',
        createdAt: 1730455000000
      },
      {
        id: 'msg-1',
        role: 'user',
        content: '帮我检查代码',
        createdAt: 1730455100000
      }
    ]

    const turns = extractConversationTurns(messages)
    expect(turns.length).toBe(1)
    expect(turns[0]?.id).toBe('msg-1')
    expect(turns[0]?.userText).toBe('帮我检查代码')
  })
})
