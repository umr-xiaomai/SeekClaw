import type { ChatMessage } from './types'

export interface TaskStep {
  id: string
  step: number
  title: string
  detail?: string
  state: 'running' | 'done' | 'error' | 'pending'
}

/**
 * The task plan is opt-in and belongs to the model: it exists only when the Agent
 * explicitly created one with the `update_plan` tool, which it does for tasks that
 * genuinely need planning.
 *
 * Earlier revisions also parsed markdown checklists out of the reply and synthesized
 * milestone steps from prompt keywords. That produced a plan card on nearly every
 * turn — including simple questions — so both heuristics are gone on purpose.
 */
export function computeTurnTaskSteps(params: {
  turnAssistants: ChatMessage[]
  isTurnRunning: boolean
  customPlan?: TaskStep[]
}): TaskStep[] {
  const { turnAssistants, isTurnRunning, customPlan } = params
  if (!customPlan || customPlan.length === 0) return []
  if (isTurnRunning) return customPlan

  const allTools = turnAssistants.flatMap((assistant) => assistant.tools ?? [])
  const lastAssistant = turnAssistants[turnAssistants.length - 1]
  const hasError = lastAssistant?.state === 'error' || allTools.some((tool) => tool.state === 'error')
  if (hasError) return customPlan

  // A finished turn never leaves a step mid-flight.
  return customPlan.map((step) => (step.state === 'running' ? { ...step, state: 'done' } : step))
}
