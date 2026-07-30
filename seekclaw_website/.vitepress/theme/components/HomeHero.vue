<template>
  <section class="hero-shell">
    <div class="hero-copy">
      <div class="hero-kicker">
        <span class="status-dot" />
        {{ isEn ? 'LOCAL-FIRST · OPEN SOURCE · EXTENSIBLE' : '本地优先 · 开源 · 可扩展' }}
      </div>

      <h1>
        {{ isEn ? 'An Agent that turns intent into' : '不止回答问题，' }}
        <span>{{ isEn ? 'finished work.' : '真正把事情做完。' }}</span>
      </h1>

      <p class="hero-lead">
        {{ isEn
          ? 'SeekClaw is a local-first, general-purpose Agent runtime. Give it a goal and it can reason, use local tools, search the web, connect to MCP and Skills, and keep working across persistent tasks.'
          : 'SeekClaw 是本地优先的通用 Agent Runtime。给它一个目标，它可以持续思考、调用本地工具、搜索网页、连接 MCP 与 Skills，并在持久化任务中推进到结果。'
        }}
      </p>

      <div class="hero-actions">
        <a :href="isEn ? '/en/doc/quickstart' : '/doc/quickstart'" class="primary-action">
          {{ isEn ? 'Get started' : '开始使用' }} <ArrowRight :size="17" />
        </a>
        <a :href="isEn ? '/en/doc/desktop' : '/doc/desktop'" class="secondary-action">
          <Monitor :size="17" /> {{ isEn ? 'Meet Desktop' : '了解 Desktop' }}
        </a>
        <a href="https://github.com/umr-xiaomai/SeekClaw" target="_blank" rel="noopener noreferrer" class="github-action" aria-label="GitHub">
          <Github :size="19" />
        </a>
      </div>

      <div class="hero-facts">
        <span><ShieldCheck :size="15" /> {{ isEn ? 'Runs locally' : '本地运行' }}</span>
        <span><Waypoints :size="15" /> {{ isEn ? 'Any model' : '模型自由' }}</span>
        <span><Puzzle :size="15" /> MCP + Skills</span>
      </div>
    </div>

    <div class="agent-panel" :aria-label="isEn ? 'Agent execution preview' : 'Agent 执行过程预览'">
      <div class="panel-header">
        <div class="panel-identity">
          <span class="agent-mark"><Sparkles :size="17" /></span>
          <div>
            <strong>SeekClaw Agent</strong>
            <small><span class="online-dot" /> {{ isEn ? 'Working' : '执行中' }}</small>
          </div>
        </div>
        <span class="runtime-pill">Runtime 2.0</span>
      </div>

      <div class="goal-card">
        <span>{{ isEn ? 'CURRENT GOAL' : '当前目标' }}</span>
        <strong>{{ isEn ? 'Research the topic, organize the material, and deliver an actionable brief' : '调研主题、整理资料，并交付一份可执行的简报' }}</strong>
      </div>

      <div class="execution-list">
        <div v-for="(step, index) in steps" :key="step.label" class="execution-step" :class="{ active: index === steps.length - 1 }">
          <span class="step-icon"><component :is="step.icon" :size="16" /></span>
          <div>
            <strong>{{ step.label }}</strong>
            <small>{{ step.detail }}</small>
          </div>
          <Check v-if="index < steps.length - 1" :size="16" class="step-check" />
          <span v-else class="activity-dots"><i /><i /><i /></span>
        </div>
      </div>

      <div class="tool-strip">
        <span><Globe2 :size="14" /> Web</span>
        <span><FolderOpen :size="14" /> Files</span>
        <span><Blocks :size="14" /> MCP</span>
        <span><Terminal :size="14" /> Shell</span>
      </div>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'
import {
  ArrowRight, Blocks, BrainCircuit, Check, FileText, FolderOpen, Github,
  Globe2, Monitor, Puzzle, Search, ShieldCheck, Sparkles, Terminal, Waypoints
} from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const steps = computed(() => isEn.value ? [
  { label: 'Understand the goal', detail: 'Plan the path and required context', icon: BrainCircuit },
  { label: 'Gather evidence', detail: 'Search the web and inspect local material', icon: Search },
  { label: 'Use connected tools', detail: 'Call MCP, Skills, files, and commands', icon: Blocks },
  { label: 'Shape the deliverable', detail: 'Synthesizing findings into a useful result', icon: FileText }
] : [
  { label: '理解目标', detail: '规划路径与所需上下文', icon: BrainCircuit },
  { label: '收集可靠信息', detail: '搜索网页并检查本地资料', icon: Search },
  { label: '调用已连接工具', detail: '使用 MCP、Skills、文件与命令', icon: Blocks },
  { label: '形成最终交付', detail: '正在把发现整理成可用结果', icon: FileText }
])
</script>

<style scoped>
.hero-shell {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(420px, 0.88fr);
  align-items: center;
  gap: clamp(3rem, 7vw, 7rem);
  width: min(1200px, calc(100% - 48px));
  margin: 0 auto;
  padding: 7.5rem 0 6.5rem;
}

.hero-shell::before {
  position: absolute;
  z-index: -1;
  top: 5rem;
  right: -8rem;
  width: 32rem;
  height: 32rem;
  border-radius: 50%;
  background: color-mix(in srgb, var(--vp-c-brand-1) 8%, transparent);
  filter: blur(90px);
  content: '';
  pointer-events: none;
}

.hero-kicker {
  display: inline-flex;
  align-items: center;
  gap: 0.55rem;
  margin-bottom: 1.5rem;
  color: var(--vp-c-brand-1);
  font-size: 0.72rem;
  font-weight: 750;
  letter-spacing: 0.12em;
}

.status-dot,
.online-dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--vp-c-brand-1);
  box-shadow: 0 0 0 4px var(--vp-c-brand-soft);
}

h1 {
  max-width: 720px;
  margin: 0;
  color: var(--seek-text-primary);
  font-size: clamp(3.4rem, 6.2vw, 5.55rem);
  font-weight: 820;
  line-height: 0.98;
  letter-spacing: -0.062em;
}

h1 span {
  display: block;
  margin-top: 0.12em;
  color: var(--vp-c-brand-1);
}

.hero-lead {
  max-width: 650px;
  margin: 1.75rem 0 0;
  color: var(--seek-text-secondary);
  font-size: 1.07rem;
  line-height: 1.82;
}

.hero-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
  margin-top: 2rem;
}

.hero-actions a {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  min-height: 44px;
  border-radius: 10px;
  font-size: 0.9rem;
  font-weight: 650;
  text-decoration: none;
  transition: border-color 0.18s ease, background 0.18s ease, color 0.18s ease, transform 0.18s ease;
}

.primary-action {
  padding: 0 1.2rem;
  background: var(--vp-c-brand-1);
  color: #fff !important;
  box-shadow: 0 8px 24px color-mix(in srgb, var(--vp-c-brand-1) 22%, transparent);
}

.primary-action:hover {
  background: var(--vp-c-brand-2);
  transform: translateY(-1px);
}

.secondary-action {
  padding: 0 1.15rem;
  border: 1px solid var(--seek-card-border);
  background: var(--seek-card-bg);
  color: var(--seek-text-primary) !important;
}

.secondary-action:hover,
.github-action:hover {
  border-color: color-mix(in srgb, var(--vp-c-brand-1) 55%, var(--seek-card-border));
  color: var(--vp-c-brand-1) !important;
}

.github-action {
  width: 44px;
  border: 1px solid var(--seek-card-border);
  color: var(--seek-text-secondary) !important;
}

.hero-facts {
  display: flex;
  flex-wrap: wrap;
  gap: 1.25rem;
  margin-top: 1.75rem;
  color: var(--seek-text-muted);
  font-size: 0.78rem;
}

.hero-facts span {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.agent-panel {
  position: relative;
  overflow: hidden;
  padding: 1.15rem;
  border: 1px solid color-mix(in srgb, var(--seek-card-border) 78%, var(--vp-c-brand-1));
  border-radius: 20px;
  background: color-mix(in srgb, var(--seek-card-bg) 94%, var(--vp-c-brand-1));
  box-shadow: 0 28px 80px rgba(15, 23, 42, 0.13);
}

.agent-panel::after {
  position: absolute;
  inset: 0;
  border-radius: inherit;
  background: linear-gradient(145deg, rgba(255, 255, 255, 0.18), transparent 42%);
  content: '';
  pointer-events: none;
}

.panel-header,
.panel-identity,
.execution-step,
.tool-strip {
  display: flex;
  align-items: center;
}

.panel-header {
  justify-content: space-between;
  padding: 0.15rem 0.2rem 1rem;
}

.panel-identity {
  gap: 0.7rem;
}

.agent-mark {
  display: grid;
  width: 36px;
  height: 36px;
  place-items: center;
  border-radius: 10px;
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
}

.panel-identity div {
  display: grid;
  gap: 0.18rem;
}

.panel-identity strong {
  color: var(--seek-text-primary);
  font-size: 0.87rem;
}

.panel-identity small {
  display: flex;
  align-items: center;
  gap: 0.38rem;
  color: var(--seek-text-muted);
  font-size: 0.67rem;
}

.online-dot {
  width: 5px;
  height: 5px;
  box-shadow: none;
}

.runtime-pill {
  padding: 0.3rem 0.55rem;
  border: 1px solid var(--seek-card-border);
  border-radius: 999px;
  color: var(--seek-text-muted);
  font-size: 0.65rem;
}

.goal-card {
  display: grid;
  gap: 0.5rem;
  padding: 1rem 1.05rem;
  border: 1px solid var(--seek-card-border);
  border-radius: 12px;
  background: var(--vp-c-bg);
}

.goal-card span {
  color: var(--vp-c-brand-1);
  font-size: 0.61rem;
  font-weight: 760;
  letter-spacing: 0.1em;
}

.goal-card strong {
  color: var(--seek-text-primary);
  font-size: 0.9rem;
  line-height: 1.5;
}

.execution-list {
  display: grid;
  gap: 0.4rem;
  margin-top: 0.75rem;
}

.execution-step {
  gap: 0.75rem;
  padding: 0.7rem 0.75rem;
  border-radius: 10px;
}

.execution-step.active {
  background: var(--vp-c-brand-soft);
}

.step-icon {
  display: grid;
  width: 30px;
  height: 30px;
  flex: 0 0 auto;
  place-items: center;
  border: 1px solid var(--seek-card-border);
  border-radius: 8px;
  background: var(--seek-card-bg);
  color: var(--seek-text-muted);
}

.execution-step.active .step-icon {
  border-color: color-mix(in srgb, var(--vp-c-brand-1) 30%, var(--seek-card-border));
  color: var(--vp-c-brand-1);
}

.execution-step > div {
  display: grid;
  min-width: 0;
  flex: 1;
  gap: 0.13rem;
}

.execution-step strong {
  color: var(--seek-text-primary);
  font-size: 0.76rem;
}

.execution-step small {
  overflow: hidden;
  color: var(--seek-text-muted);
  font-size: 0.65rem;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.step-check {
  color: var(--vp-c-brand-1);
}

.activity-dots {
  display: flex;
  gap: 3px;
}

.activity-dots i {
  width: 4px;
  height: 4px;
  border-radius: 50%;
  background: var(--vp-c-brand-1);
  animation: agentPulse 1.15s infinite ease-in-out;
}

.activity-dots i:nth-child(2) { animation-delay: 0.15s; }
.activity-dots i:nth-child(3) { animation-delay: 0.3s; }

.tool-strip {
  flex-wrap: wrap;
  gap: 0.4rem;
  margin-top: 0.85rem;
  padding-top: 0.85rem;
  border-top: 1px solid var(--seek-card-border);
}

.tool-strip span {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.34rem 0.5rem;
  border-radius: 7px;
  background: var(--vp-c-bg-soft);
  color: var(--seek-text-muted);
  font-size: 0.65rem;
}

@keyframes agentPulse {
  0%, 70%, 100% { opacity: 0.25; transform: translateY(0); }
  35% { opacity: 1; transform: translateY(-2px); }
}

@media (max-width: 960px) {
  .hero-shell {
    grid-template-columns: 1fr;
    gap: 3rem;
    padding: 6rem 0 4.5rem;
  }

  .hero-copy {
    text-align: center;
  }

  .hero-lead {
    margin-right: auto;
    margin-left: auto;
  }

  .hero-actions,
  .hero-facts {
    justify-content: center;
  }

  .agent-panel {
    width: min(560px, 100%);
    margin: 0 auto;
  }
}

@media (max-width: 640px) {
  .hero-shell {
    width: min(100% - 32px, 1200px);
    padding: 4.75rem 0 3.5rem;
  }

  h1 {
    font-size: clamp(2.75rem, 13vw, 3.8rem);
  }

  .hero-lead {
    font-size: 0.98rem;
  }

  .hero-actions a:not(.github-action) {
    flex: 1 1 150px;
  }

  .agent-panel {
    padding: 0.9rem;
    border-radius: 16px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .activity-dots i {
    animation: none;
  }
}
</style>
