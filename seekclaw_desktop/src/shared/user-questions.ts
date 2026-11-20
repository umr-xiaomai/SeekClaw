import type { DaemonMessage } from './ipc.js'

export interface UserQuestion {
  sessionId: string
  questionId: string
  turnId: number
  question: string
  options: string[]
}

function parseQuestionData(event: DaemonMessage) {
  if (!event.sessionId) return null
  try {
    const data = JSON.parse(event.data) as Record<string, unknown> | null
    if (typeof data?.questionId !== 'string' || !data.questionId.trim()) return null
    return { sessionId: event.sessionId, questionId: data.questionId, data }
  } catch {
    return null
  }
}

export function parseQuestionRequest(event: DaemonMessage): UserQuestion | null {
  if (event.event !== 'question_request' || !Number.isSafeInteger(event.id)) return null
  const parsed = parseQuestionData(event)
  if (!parsed) return null
  const { sessionId, questionId, data: { question, options } } = parsed
  if (typeof question !== 'string' || !question.trim() || !Array.isArray(options)
    || !options.every((option): option is string => typeof option === 'string' && !!option.trim())) return null
  return { sessionId, questionId, turnId: event.id, question, options }
}

export function parseQuestionResolved(event: DaemonMessage): Pick<UserQuestion, 'sessionId' | 'questionId'> | null {
  if (event.event !== 'question_resolved') return null
  const parsed = parseQuestionData(event)
  return parsed && { sessionId: parsed.sessionId, questionId: parsed.questionId }
}

export function finishesQuestionTurn(event: DaemonMessage, question: UserQuestion): boolean {
  return (event.event === 'done' || event.event === 'cancelled' || event.event === 'error' || event.event === 'result')
    && event.id === question.turnId && (!event.sessionId || event.sessionId === question.sessionId)
}
