import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import { ReasoningLevel } from '../types'
import Composer from './Composer.vue'
import InlineQuestionCard from './InlineQuestionCard.vue'

describe('question card replaces the composer', () => {
  it('hides the actual composer frame while keeping the answer available', async () => {
    const html = await renderToString(createSSRApp({
      render: () => h('footer', [
        h(InlineQuestionCard, {
          request: { sessionId: 'session', turnId: 1, questionId: 'question', question: '去哪儿？', options: ['海边', '山顶'] },
          submitting: false,
          submitted: false,
          error: '',
          draft: ''
        }),
        h(Composer, {
          obscured: true,
          busy: true,
          model: '',
          models: [],
          mode: 'full',
          reasoningLevel: ReasoningLevel.High,
          supportsImages: true
        })
      ])
    }))

    expect(html).toMatch(/class="composer-frame"[^>]*style="display:none;?"/)
    expect(html).toContain('否，并告诉SeekClaw应该怎么做。')
    expect(html).toContain('海边')
    expect(html).toContain('山顶')
  })
})
