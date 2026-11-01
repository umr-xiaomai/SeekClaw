import type { ChatMessage } from '../types'

export interface ConversationTurn {
  id: string
  userText: string
  assistantPreview: string
  index: number
  timestamp?: number
}

export function cleanMarkdownPreview(text?: string | null): string {
  if (!text) return ''
  return text
    .replace(/^#{1,6}\s+/gm, '')
    .replace(/```[\s\S]*?```/g, '[代码块]')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\*\*([^*]+)\*\*/g, '$1')
    .replace(/\*([^*]+)\*/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
    .replace(/\r?\n+/g, ' ')
    .trim()
}

export function extractConversationTurns(messages: ChatMessage[] = []): ConversationTurn[] {
  const turns: ConversationTurn[] = []
  let currentTurnIndex = -1

  for (const message of messages) {
    if (message.content?.startsWith('>>> [output truncated]')) continue

    if (message.role === 'user') {
      const content = message.content?.trim() || ''
      if (content.startsWith('System: [Memory Recall]') || content.startsWith('System: [Error]')) {
        continue
      }

      const turn: ConversationTurn = {
        id: message.id,
        userText: content || (message.images?.length ? '[图片]' : (message.files?.length ? '[文件]' : '新消息')),
        assistantPreview: '',
        index: turns.length + 1,
        timestamp: message.createdAt
      }
      turns.push(turn)
      currentTurnIndex = turns.length - 1
    } else if (message.role === 'assistant' && currentTurnIndex >= 0) {
      const turnItem = turns[currentTurnIndex]
      if (turnItem) {
        if (message.content?.trim()) {
          const cleaned = cleanMarkdownPreview(message.content)
          turnItem.assistantPreview = cleaned.length > 150 ? cleaned.slice(0, 150) + '...' : cleaned
        } else if (!turnItem.assistantPreview) {
          if (message.tools?.length) {
            turnItem.assistantPreview = '执行了工具操作'
          } else if (message.state === 'thinking') {
            turnItem.assistantPreview = '正在深度思考...'
          }
        }
      }
    }
  }

  return turns
}
