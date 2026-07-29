<template>
  <div class="home-hero-container">
    <!-- Top Announcement Badge -->
    <div class="announcement-pill">
      <span class="pill-tag">v1.0.0 (.NET 10.0)</span>
      <span class="pill-text">
        {{ isEn ? 'Native AOT Friendly AI Agent Runtime for Developers' : '面向开发者的 Native AOT 高性能 AI Agent 运行时' }}
      </span>
      <span class="pill-arrow">➔</span>
    </div>

    <!-- Main Headline -->
    <h1 class="hero-headline">
      {{ isEn ? 'Modern, High-Performance' : '现代化、高性能的' }}
      <span class="highlight-text">AI Agent {{ isEn ? 'Runtime' : '运行时' }}</span>
    </h1>

    <p class="hero-subheadline">
      {{ isEn 
        ? 'Built on .NET 10.0 with Clean Architecture, event-driven terminal rendering, open MCP protocol, and auto-build self-healing code loops.'
        : '基于 .NET 10.0 构建，采用清洁架构、双缓冲终端渲染、开放 MCP 协议与代码构建自愈修复闭环。'
      }}
    </p>

    <!-- Call to Action Buttons -->
    <div class="hero-actions">
      <a :href="isEn ? '/en/doc/quickstart' : '/doc/quickstart'" class="btn-primary">
        <Rocket :size="16" /> {{ isEn ? 'Quick Start' : '快速开始' }}
      </a>
      <a :href="isEn ? '/en/doc/' : '/doc/'" class="btn-secondary">
        <BookOpen :size="16" /> {{ isEn ? 'Documentation' : '文档中心' }}
      </a>
      <a href="https://github.com/umr-xiaomai/SeekClaw" target="_blank" rel="noopener noreferrer" class="btn-outline">
        <Github :size="16" /> GitHub
      </a>
    </div>

    <!-- Quick Terminal Command Copy Box -->
    <div class="cmd-copy-box">
      <span class="cmd-prompt">$</span>
      <code class="cmd-text">dotnet run --project seekclaw_cli</code>
      <button @click="copyCommand" class="copy-btn" :title="copied ? 'Copied' : 'Copy'">
        <Check v-if="copied" :size="14" class="copy-icon success" />
        <Copy v-else :size="14" class="copy-icon" />
        <span>{{ copied ? (isEn ? 'Copied' : '已复制') : (isEn ? 'Copy' : '复制') }}</span>
      </button>
    </div>

    <!-- Metrics / Highlights Grid (3 Metrics) -->
    <div class="metrics-grid">
      <div class="metric-card">
        <div class="metric-value">100%</div>
        <div class="metric-label">{{ isEn ? 'Clean Architecture Decoupled' : '清洁架构 UI/业务解耦' }}</div>
      </div>
      <div class="metric-card">
        <div class="metric-value">MCP</div>
        <div class="metric-label">{{ isEn ? 'Model Context Protocol Native' : '原生 MCP 标准协议集成' }}</div>
      </div>
      <div class="metric-card">
        <div class="metric-value">Auto-Fix</div>
        <div class="metric-label">{{ isEn ? 'Self-Healing Build Verification' : '构建测试自愈修复闭环' }}</div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, computed } from 'vue'
import { useData } from 'vitepress'
import { Rocket, BookOpen, Github, Copy, Check } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const copied = ref(false)

const copyCommand = async () => {
  const text = 'dotnet run --project seekclaw_cli'
  try {
    await navigator.clipboard.writeText(text)
    copied.value = true
    setTimeout(() => { copied.value = false }, 2000)
  } catch (e) {
    console.error('Failed to copy', e)
  }
}
</script>

<style scoped>
.home-hero-container {
  text-align: center;
  padding: 3.5rem 1.5rem 2rem 1.5rem;
  max-width: 960px;
  margin: 0 auto;
}

.announcement-pill {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  background: var(--vp-c-bg-soft);
  border: 1px solid var(--seek-card-border);
  padding: 4px 14px;
  border-radius: 9999px;
  font-size: 0.8rem;
  margin-bottom: 1.75rem;
  color: var(--seek-text-secondary);
}

.pill-tag {
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
  font-weight: 700;
  padding: 2px 8px;
  border-radius: 9999px;
  font-size: 0.725rem;
}

.pill-arrow {
  color: var(--vp-c-brand-1);
}

.hero-headline {
  font-size: 3rem;
  line-height: 1.15;
  font-weight: 800;
  letter-spacing: -0.03em;
  color: var(--seek-text-primary);
  margin-bottom: 1.25rem;
}

@media (min-width: 768px) {
  .hero-headline {
    font-size: 3.75rem;
  }
}

.highlight-text {
  color: var(--vp-c-brand-1);
}

.hero-subheadline {
  font-size: 1.15rem;
  line-height: 1.6;
  color: var(--seek-text-secondary);
  max-width: 740px;
  margin: 0 auto 2rem auto;
}

.hero-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 12px;
  margin-bottom: 2rem;
}

.btn-primary {
  background: var(--vp-c-brand-1);
  color: #ffffff !important;
  font-weight: 600;
  padding: 10px 22px;
  border-radius: 8px;
  text-decoration: none !important;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  transition: all 0.2s ease;
}

.btn-primary:hover {
  background: var(--vp-c-brand-2);
  transform: translateY(-1px);
}

.btn-secondary {
  background: var(--vp-c-bg-soft);
  color: var(--seek-text-primary) !important;
  font-weight: 600;
  padding: 10px 22px;
  border-radius: 8px;
  border: 1px solid var(--seek-card-border);
  text-decoration: none !important;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  transition: all 0.2s ease;
}

.btn-secondary:hover {
  border-color: var(--vp-c-brand-1);
  color: var(--vp-c-brand-1) !important;
}

.btn-outline {
  background: transparent;
  color: var(--seek-text-secondary) !important;
  font-weight: 500;
  padding: 10px 20px;
  border-radius: 8px;
  border: 1px solid var(--seek-card-border);
  text-decoration: none !important;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  transition: all 0.2s ease;
}

.btn-outline:hover {
  color: var(--seek-text-primary) !important;
  border-color: var(--seek-text-secondary);
}

.cmd-copy-box {
  background: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 8px 16px;
  display: inline-flex;
  align-items: center;
  gap: 12px;
  font-family: 'Fira Code', JetBrains Mono, monospace;
  margin-bottom: 3rem;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

.cmd-prompt {
  color: #10b981;
  font-weight: bold;
}

.cmd-text {
  color: #34d399 !important;
  background: none !important;
  border: none !important;
  font-size: 0.9rem;
}

.copy-btn {
  background: #21262d;
  color: #c9d1d9;
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 4px 10px;
  font-size: 0.75rem;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  transition: all 0.2s;
}

.copy-btn:hover {
  background: #30363d;
  color: #ffffff;
}

.copy-icon.success {
  color: #34d399;
}

.metrics-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1.25rem;
  border-top: 1px solid var(--seek-card-border);
  padding-top: 2rem;
}

.metric-card {
  background: var(--seek-card-bg);
  border: 1px solid var(--seek-card-border);
  border-radius: 8px;
  padding: 1.25rem 1rem;
  text-align: center;
}

.metric-value {
  font-size: 1.75rem;
  font-weight: 800;
  color: var(--vp-c-brand-1);
  letter-spacing: -0.02em;
  margin-bottom: 4px;
}

.metric-label {
  font-size: 0.8rem;
  color: var(--seek-text-secondary);
  line-height: 1.4;
}
</style>
