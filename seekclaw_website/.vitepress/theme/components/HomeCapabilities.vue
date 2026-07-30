<template>
  <section class="capabilities-section">
    <div class="section-heading">
      <span>{{ isEn ? 'ONE AGENT, MANY KINDS OF WORK' : '一个 Agent，处理不同类型的工作' }}</span>
      <h2>{{ isEn ? 'One Agent. Work of every kind.' : '面向真实工作的通用 Agent。' }}</h2>
      <p>{{ isEn ? 'SeekClaw provides the runtime, context, tools, and continuity an Agent needs to move from a request to a real outcome.' : 'SeekClaw 为 Agent 提供运行时、上下文、工具与连续性，让一次请求真正走向可用结果。' }}</p>
    </div>

    <div class="capability-grid">
      <article v-for="item in capabilities" :key="item.title" class="capability-card" :class="item.size">
        <span class="capability-icon"><component :is="item.icon" :size="20" /></span>
        <div>
          <h3>{{ item.title }}</h3>
          <p>{{ item.description }}</p>
        </div>
        <span v-if="item.meta" class="capability-meta">{{ item.meta }}</span>
      </article>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'
import { Blocks, FolderKanban, Globe2, History, Route, Target } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const capabilities = computed(() => isEn.value ? [
  { title: 'From goal to outcome', description: 'Break down intent, choose the next action, use tools, inspect results, and continue until the work is actually complete.', icon: Target, size: 'wide', meta: 'Agent loop' },
  { title: 'Global tasks', description: 'Research, summarize, organize knowledge, and handle everyday tasks without binding a local directory.', icon: Globe2, size: 'normal', meta: 'No workspace required' },
  { title: 'Project workspaces', description: 'When local context matters, work with files, terminals, Git history, and project-specific memory.', icon: FolderKanban, size: 'normal', meta: 'Local context' },
  { title: 'Your tool universe', description: 'Extend the Agent with built-in tools, web access, MCP servers, and reusable Skills.', icon: Blocks, size: 'normal', meta: 'MCP · Skills · Web' },
  { title: 'Model freedom', description: 'Connect Anthropic, OpenAI-compatible APIs, local models, and route work by speed, quality, cost, or privacy.', icon: Route, size: 'normal', meta: 'Multi-provider' },
  { title: 'Continuity by default', description: 'Tasks, sessions, archives, memory, and usage survive restarts so work can continue where it stopped.', icon: History, size: 'wide', meta: 'Persistent state' }
] : [
  { title: '从目标到结果', description: '理解意图、拆解任务、选择下一步、调用工具、检查结果，并持续推进直到工作真正完成。', icon: Target, size: 'wide', meta: 'Agent Loop' },
  { title: '全局任务', description: '不绑定本地目录，也可以调研、总结、整理知识并处理各种日常任务。', icon: Globe2, size: 'normal', meta: '无需工作区' },
  { title: '项目工作区', description: '需要本地上下文时，可使用文件、终端、Git 历史与项目专属 Memory。', icon: FolderKanban, size: 'normal', meta: '本地上下文' },
  { title: '连接整个工具世界', description: '通过内置工具、网页能力、MCP Server 与可复用 Skills 扩展 Agent。', icon: Blocks, size: 'normal', meta: 'MCP · Skills · Web' },
  { title: '模型自由', description: '连接 Anthropic、OpenAI 兼容接口和本地模型，并按速度、质量、成本或隐私路由。', icon: Route, size: 'normal', meta: '多 Provider' },
  { title: '任务持续存在', description: '任务、Session、归档、Memory 与用量跨重启保留，随时从上次停止的地方继续。', icon: History, size: 'wide', meta: '持久化状态' }
])
</script>

<style scoped>
.capabilities-section {
  padding: 6.5rem 0;
}

.section-heading {
  max-width: 720px;
  margin-bottom: 2.2rem;
}

.section-heading > span {
  color: var(--vp-c-brand-1);
  font-size: 0.7rem;
  font-weight: 760;
  letter-spacing: 0.12em;
}

.section-heading h2 {
  margin: 0.65rem 0 0;
  color: var(--seek-text-primary);
  font-size: clamp(2.25rem, 4vw, 3.4rem);
  line-height: 1.08;
  letter-spacing: -0.045em;
}

.section-heading p {
  margin: 1rem 0 0;
  color: var(--seek-text-secondary);
  font-size: 1rem;
  line-height: 1.75;
}

.capability-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 0.9rem;
}

.capability-card {
  position: relative;
  display: flex;
  min-height: 220px;
  flex-direction: column;
  justify-content: space-between;
  padding: 1.45rem;
  overflow: hidden;
  border: 1px solid var(--seek-card-border);
  border-radius: 15px;
  background: var(--seek-card-bg);
  transition: border-color 0.2s ease, transform 0.2s ease, box-shadow 0.2s ease;
}

.capability-card::after {
  position: absolute;
  right: -2.5rem;
  bottom: -3.5rem;
  width: 9rem;
  height: 9rem;
  border-radius: 50%;
  background: var(--vp-c-brand-soft);
  content: '';
  opacity: 0;
  transition: opacity 0.2s ease;
}

.capability-card:hover {
  border-color: color-mix(in srgb, var(--vp-c-brand-1) 38%, var(--seek-card-border));
  box-shadow: 0 16px 40px rgba(15, 23, 42, 0.07);
  transform: translateY(-2px);
}

.capability-card:hover::after {
  opacity: 1;
}

.capability-card.wide {
  grid-column: span 2;
}

.capability-icon {
  display: grid;
  width: 40px;
  height: 40px;
  place-items: center;
  border: 1px solid color-mix(in srgb, var(--vp-c-brand-1) 18%, var(--seek-card-border));
  border-radius: 11px;
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
}

.capability-card h3 {
  margin: 1.8rem 0 0.55rem;
  color: var(--seek-text-primary);
  font-size: 1.05rem;
  letter-spacing: -0.015em;
}

.capability-card p {
  margin: 0;
  color: var(--seek-text-secondary);
  font-size: 0.84rem;
  line-height: 1.65;
}

.capability-meta {
  position: relative;
  z-index: 1;
  align-self: flex-start;
  margin-top: 1.25rem;
  color: var(--seek-text-muted);
  font-size: 0.66rem;
  font-weight: 650;
  letter-spacing: 0.04em;
}

@media (max-width: 900px) {
  .capability-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 580px) {
  .capabilities-section {
    padding: 4.5rem 0;
  }

  .capability-grid {
    grid-template-columns: 1fr;
  }

  .capability-card.wide {
    grid-column: auto;
  }

  .capability-card {
    min-height: 200px;
  }
}
</style>
