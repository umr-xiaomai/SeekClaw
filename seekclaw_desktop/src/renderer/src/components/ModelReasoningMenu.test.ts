import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import ModelReasoningMenu from './ModelReasoningMenu.vue'
import { ReasoningLevel } from '../types'

function renderModel(model: string, supportsReasoning = true): Promise<string> {
  return renderToString(createSSRApp({
    render: () => h(ModelReasoningMenu, {
      model,
      models: [model],
      reasoningLevel: ReasoningLevel.Max,
      supportsReasoning
    })
  }))
}

describe('model reasoning menu labels', () => {
  it.each([
    ['openai/gpt-5.4', 'gpt-5.4'],
    ['openrouter/anthropic/claude-sonnet-4.6', 'anthropic/claude-sonnet-4.6'],
    ['gateway/team/namespace/model-preview', 'team/namespace/model-preview'],
    ['  local/Qwen/Qwen3.5-397B-A17B  ', 'Qwen/Qwen3.5-397B-A17B'],
    ['gpt-5.4', 'gpt-5.4']
  ])('preserves the full model id in %s', async (reference, modelId) => {
    const html = await renderModel(reference)

    expect(html).toContain(`>${modelId}</span>`)
    expect(html).toContain(`模型与强度：${modelId} (最大)`)
    expect(html).toMatch(/class="trigger-reasoning-label"[^>]*>最大<\/span>/)
    expect(html).not.toContain('最大强度 (Max)')
  })

  it('renders a model without a reasoning label when reasoning is unsupported', async () => {
    const html = await renderModel('gateway/team/vision-model', false)

    expect(html).toContain('>team/vision-model</span>')
    expect(html).not.toContain('class="trigger-reasoning-label"')
  })

  it('keeps the unconfigured-model fallback', async () => {
    const html = await renderModel('')

    expect(html).toContain('>未配置模型</span>')
    expect(html).not.toContain('class="trigger-reasoning-label"')
  })
})
