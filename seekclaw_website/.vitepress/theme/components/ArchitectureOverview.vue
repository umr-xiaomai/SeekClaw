<template>
  <section class="runtime-section">
    <div class="runtime-copy">
      <span class="eyebrow">OPEN AGENT RUNTIME</span>
      <h2>{{ isEn ? 'Build around your models, tools, and data' : '开放的 Agent Runtime' }}</h2>
      <p>
        {{ isEn
          ? 'SeekClaw keeps the orchestration layer open. Choose the models you trust, connect the tools you need, and keep task state under your control.'
          : 'SeekClaw 将编排层保持开放：选择你信任的模型，连接任务需要的工具，并把任务状态掌握在自己手中。'
        }}
      </p>
      <a :href="isEn ? '/en/doc/architecture' : '/doc/architecture'">
        {{ isEn ? 'How the Runtime works' : '了解 Runtime 架构' }} <ArrowRight :size="16" />
      </a>
    </div>

    <div class="runtime-visual" :aria-label="isEn ? 'SeekClaw Runtime flow' : 'SeekClaw Runtime 流程'">
      <div class="flow-row">
        <div class="flow-group compact">
          <span class="flow-label">MODELS</span>
          <div class="chip-row">
            <span>Anthropic</span><span>OpenAI</span><span>Local</span>
          </div>
        </div>
        <ArrowRight class="flow-arrow" :size="19" />
        <div class="runtime-core">
          <Sparkles :size="22" />
          <div><strong>SeekClaw</strong><small>Agent Runtime</small></div>
        </div>
        <ArrowRight class="flow-arrow" :size="19" />
        <div class="flow-group compact">
          <span class="flow-label">CAPABILITIES</span>
          <div class="chip-row">
            <span>Tools</span><span>MCP</span><span>Skills</span>
          </div>
        </div>
      </div>

      <div class="output-line" />
      <div class="output-row">
        <span><Monitor :size="16" /> Desktop</span>
        <span><Terminal :size="16" /> CLI</span>
        <span><Database :size="16" /> {{ isEn ? 'Local state' : '本地状态' }}</span>
      </div>
    </div>

    <div class="runtime-facts">
      <article v-for="fact in facts" :key="fact.title">
        <strong>{{ fact.title }}</strong>
        <span>{{ fact.description }}</span>
      </article>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'
import { ArrowRight, Database, Monitor, Sparkles, Terminal } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const facts = computed(() => isEn.value ? [
  { title: 'Any model', description: 'Cloud APIs, compatible providers, and local inference.' },
  { title: 'Any tool', description: 'Built-ins, web access, MCP servers, and reusable Skills.' },
  { title: 'Local control', description: 'Persistent tasks, sessions, memory, and diagnostics.' }
] : [
  { title: '任意模型', description: '云端 API、兼容 Provider 与本地推理。' },
  { title: '任意工具', description: '内置工具、网页、MCP Server 与 Skills。' },
  { title: '本地掌控', description: '持久化任务、Session、Memory 与诊断。' }
])
</script>

<style scoped>
.runtime-section {
  display: grid;
  grid-template-columns: minmax(0, 0.75fr) minmax(480px, 1.25fr);
  gap: 2rem 4rem;
  margin-top: 1rem;
  padding: clamp(2rem, 5vw, 4rem);
  overflow: hidden;
  border: 1px solid #26352f;
  border-radius: 22px;
  background: #0c1512;
  color: #fff;
}

.eyebrow {
  color: #52d7a0;
  font-size: 0.68rem;
  font-weight: 760;
  letter-spacing: 0.12em;
}

.runtime-copy h2 {
  margin: 0.7rem 0 0;
  color: #f4fbf8;
  font-size: clamp(2.1rem, 4vw, 3.25rem);
  line-height: 1.08;
  letter-spacing: -0.045em;
}

.runtime-copy p {
  margin: 1rem 0 0;
  color: #a9bdb5;
  font-size: 0.92rem;
  line-height: 1.75;
}

.runtime-copy a {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  margin-top: 1.5rem;
  color: #6ee7b7;
  font-size: 0.84rem;
  font-weight: 650;
  text-decoration: none;
}

.runtime-visual {
  align-self: center;
  padding: 1.4rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 15px;
  background: rgba(255, 255, 255, 0.035);
}

.flow-row,
.output-row,
.chip-row,
.runtime-core {
  display: flex;
  align-items: center;
}

.flow-row {
  justify-content: center;
  gap: 0.8rem;
}

.flow-group {
  min-width: 0;
  flex: 1;
}

.flow-label {
  display: block;
  margin-bottom: 0.55rem;
  color: #70867e;
  font-size: 0.56rem;
  font-weight: 750;
  letter-spacing: 0.1em;
  text-align: center;
}

.chip-row {
  flex-wrap: wrap;
  justify-content: center;
  gap: 0.35rem;
}

.chip-row span,
.output-row span {
  padding: 0.38rem 0.48rem;
  border: 1px solid rgba(255, 255, 255, 0.09);
  border-radius: 7px;
  background: rgba(255, 255, 255, 0.04);
  color: #a9bdb5;
  font-size: 0.61rem;
}

.flow-arrow {
  flex: 0 0 auto;
  color: #49685c;
}

.runtime-core {
  flex: 0 0 auto;
  gap: 0.65rem;
  padding: 0.85rem;
  border: 1px solid rgba(110, 231, 183, 0.24);
  border-radius: 11px;
  background: rgba(16, 185, 129, 0.09);
  color: #6ee7b7;
}

.runtime-core div {
  display: grid;
}

.runtime-core strong {
  color: #f4fbf8;
  font-size: 0.78rem;
}

.runtime-core small {
  color: #789086;
  font-size: 0.56rem;
}

.output-line {
  width: 1px;
  height: 28px;
  margin: 0 auto;
  background: #365449;
}

.output-row {
  justify-content: center;
  gap: 0.45rem;
}

.output-row span {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.runtime-facts {
  grid-column: 1 / -1;
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 1px;
  margin-top: 0.75rem;
  overflow: hidden;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 11px;
  background: rgba(255, 255, 255, 0.08);
}

.runtime-facts article {
  display: grid;
  gap: 0.35rem;
  padding: 1rem 1.1rem;
  background: #0c1512;
}

.runtime-facts strong {
  color: #edf8f3;
  font-size: 0.79rem;
}

.runtime-facts span {
  color: #789086;
  font-size: 0.68rem;
  line-height: 1.5;
}

@media (max-width: 920px) {
  .runtime-section {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .runtime-section {
    padding: 1.5rem;
    border-radius: 16px;
  }

  .flow-row {
    flex-direction: column;
  }

  .flow-arrow {
    transform: rotate(90deg);
  }

  .flow-group {
    width: 100%;
  }

  .runtime-facts {
    grid-template-columns: 1fr;
  }
}
</style>
