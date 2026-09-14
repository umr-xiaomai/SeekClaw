import { describe, expect, it } from 'vitest'
import { computeTurnTaskSteps, type TaskStep } from './task-planner'
import type { ChatMessage } from './types'

function assistant(patch: Partial<ChatMessage> = {}): ChatMessage {
  return {
    id: 'assistant-1',
    role: 'assistant',
    content: '',
    createdAt: new Date(0).toISOString(),
    ...patch
  } as ChatMessage
}

function plan(): TaskStep[] {
  return [
    { id: 'plan-step-1', step: 1, title: '检查 App.vue 的渲染逻辑', state: 'done' },
    { id: 'plan-step-2', step: 2, title: '修复工作区面板的折叠状态', state: 'running' },
    { id: 'plan-step-3', step: 3, title: '运行 pnpm typecheck 验证', state: 'pending' }
  ]
}

describe('computeTurnTaskSteps', () => {
  it('renders nothing for a conversation without a model-created plan', () => {
    const steps = computeTurnTaskSteps({
      turnAssistants: [assistant({ content: '当然可以，下面是这段代码的说明……' })],
      isTurnRunning: false
    })

    expect(steps).toEqual([])
  })

  it('never synthesizes a plan from the user prompt alone', () => {
    // Regression: the renderer used to fabricate 4-5 milestone steps for every
    // prompt, which made the plan card appear on every single turn.
    const steps = computeTurnTaskSteps({
      turnAssistants: [],
      isTurnRunning: true
    })

    expect(steps).toEqual([])
  })

  it('never lifts a markdown checklist out of the reply', () => {
    const steps = computeTurnTaskSteps({
      turnAssistants: [assistant({
        content: '- [x] 浏览项目结构与核心模块\n- [/] 阅读测试文件\n- [ ] 运行单元测试'
      })],
      isTurnRunning: false
    })

    expect(steps).toEqual([])
  })

  it('passes a model-created plan through while the turn runs', () => {
    const steps = computeTurnTaskSteps({
      turnAssistants: [assistant({ state: 'streaming' })],
      isTurnRunning: true,
      customPlan: plan()
    })

    expect(steps).toEqual(plan())
  })

  it('settles the running step once the turn completes', () => {
    const steps = computeTurnTaskSteps({
      turnAssistants: [assistant({ state: 'done', content: '完成了' })],
      isTurnRunning: false,
      customPlan: plan()
    })

    expect(steps.map((step) => step.state)).toEqual(['done', 'done', 'pending'])
  })

  it('leaves the plan untouched when the turn failed', () => {
    const steps = computeTurnTaskSteps({
      turnAssistants: [assistant({ state: 'error' })],
      isTurnRunning: false,
      customPlan: plan()
    })

    expect(steps.map((step) => step.state)).toEqual(['done', 'running', 'pending'])
  })

  it('ignores an empty plan', () => {
    expect(computeTurnTaskSteps({
      turnAssistants: [],
      isTurnRunning: false,
      customPlan: []
    })).toEqual([])
  })
})
