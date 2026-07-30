<template>
  <section class="desktop-section">
    <div class="section-heading centered">
      <span>SEEKCLAW DESKTOP</span>
      <h2>{{ isEn ? 'The workspace where your Agent gets things done' : 'Desktop 是 Agent 的工作台' }}</h2>
      <p>
        {{ isEn
          ? 'Start a task, connect the context it needs, and follow every action from one focused interface.'
          : '发起任务、连接所需上下文，并在一个专注的界面中掌握 Agent 的每一步。'
        }}
      </p>
    </div>

    <div class="desktop-frame">
      <div class="window-bar" aria-hidden="true">
        <span /><span /><span />
        <strong>SeekClaw</strong>
      </div>
      <a href="/screenshots/desktop/chat-and-projects.png" :aria-label="isEn ? 'Open the Desktop screenshot' : '打开 Desktop 截图'">
        <img src="/screenshots/desktop/chat-and-projects.png" :alt="isEn ? 'SeekClaw Desktop Agent workspace' : 'SeekClaw Desktop Agent 工作台'" loading="lazy" />
      </a>
    </div>

    <div class="desktop-points">
      <article v-for="point in points" :key="point.title">
        <span><component :is="point.icon" :size="19" /></span>
        <div>
          <h3>{{ point.title }}</h3>
          <p>{{ point.description }}</p>
        </div>
      </article>
    </div>

    <a :href="isEn ? '/en/doc/desktop' : '/doc/desktop'" class="guide-link">
      {{ isEn ? 'Explore SeekClaw Desktop' : '查看 Desktop 完整指南' }}
      <ArrowRight :size="16" />
    </a>
  </section>
</template>

<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'
import { Archive, ArrowRight, Blocks, FolderKanban } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const points = computed(() => isEn.value ? [
  { title: 'Projects and global tasks', description: 'Work with rich local context or start directory-free tasks for research, writing, and everyday work.', icon: FolderKanban },
  { title: 'Models, MCP, and Skills', description: 'Configure providers and extend the Agent with connected tools without leaving the app.', icon: Blocks },
  { title: 'Sessions you can return to', description: 'Resume, archive, diagnose, and inspect usage while all task state remains available.', icon: Archive }
] : [
  { title: '项目与全局任务', description: '既能连接丰富的本地上下文，也能创建无需目录的调研、写作与日常任务。', icon: FolderKanban },
  { title: '模型、MCP 与 Skills', description: '在应用内配置 Provider，并用连接的工具持续扩展 Agent 能力。', icon: Blocks },
  { title: '随时回到任务现场', description: '恢复、归档、诊断并查看用量，所有任务状态都持续保留。', icon: Archive }
])
</script>

<style scoped>
.desktop-section {
  padding: 7rem 0;
  border-top: 1px solid var(--seek-card-border);
}

.section-heading {
  max-width: 760px;
  margin: 0 auto 2.4rem;
  text-align: center;
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
  margin: 1rem auto 0;
  color: var(--seek-text-secondary);
  font-size: 1rem;
  line-height: 1.75;
}

.desktop-frame {
  overflow: hidden;
  border: 1px solid var(--seek-card-border);
  border-radius: 18px;
  background: var(--seek-card-bg);
  box-shadow: 0 30px 80px rgba(15, 23, 42, 0.13);
}

.window-bar {
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
  height: 36px;
  padding: 0 14px;
  border-bottom: 1px solid var(--seek-card-border);
  background: var(--vp-c-bg-soft);
}

.window-bar span {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--seek-text-muted);
  opacity: 0.35;
}

.window-bar strong {
  position: absolute;
  left: 50%;
  color: var(--seek-text-muted);
  font-size: 0.67rem;
  font-weight: 600;
  transform: translateX(-50%);
}

.desktop-frame a,
.desktop-frame img {
  display: block;
  width: 100%;
}

.desktop-points {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 2.5rem;
  margin-top: 2.5rem;
}

.desktop-points article {
  display: flex;
  gap: 0.9rem;
}

.desktop-points article > span {
  display: grid;
  width: 38px;
  height: 38px;
  flex: 0 0 auto;
  place-items: center;
  border-radius: 10px;
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
}

.desktop-points h3 {
  margin: 0 0 0.4rem;
  color: var(--seek-text-primary);
  font-size: 0.94rem;
}

.desktop-points p {
  margin: 0;
  color: var(--seek-text-secondary);
  font-size: 0.79rem;
  line-height: 1.65;
}

.guide-link {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.45rem;
  width: fit-content;
  margin: 2.2rem auto 0;
  color: var(--vp-c-brand-1);
  font-size: 0.86rem;
  font-weight: 650;
  text-decoration: none;
}

.guide-link:hover {
  color: var(--vp-c-brand-2);
}

@media (max-width: 800px) {
  .desktop-section {
    padding: 5rem 0;
  }

  .desktop-points {
    grid-template-columns: 1fr;
    gap: 1.4rem;
  }
}

@media (max-width: 580px) {
  .desktop-frame {
    border-radius: 12px;
  }

  .window-bar {
    height: 28px;
  }
}
</style>
