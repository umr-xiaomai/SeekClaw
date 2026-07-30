<template>
  <div class="provider-matrix seek-glass-card">
    <div class="matrix-header">
      <h3>
        <Bot class="icon-header" :size="22" /> {{ isEn ? 'Provider protocols and model routing' : 'Provider 协议与模型路由' }}
      </h3>
      <p>{{ isEn ? 'Use Anthropic and OpenAI wire protocols with cloud, compatible, and local model endpoints.' : '通过 Anthropic 与 OpenAI 两种线协议，统一接入云端、兼容接口与本地模型。' }}</p>
    </div>

    <div class="providers-grid">
      <div v-for="p in providers" :key="p.name" class="provider-card">
        <div class="card-top">
          <component :is="p.icon" class="provider-icon" :size="20" />
          <span class="provider-name">{{ p.name }}</span>
          <span class="status-pill" :class="p.status">{{ p.statusLabel }}</span>
        </div>
        <p class="models-list"><strong>{{ isEn ? 'Examples:' : '示例：' }}</strong> {{ p.models }}</p>
        <div class="features-tags">
          <span v-for="f in p.features" :key="f" class="tag-chip">{{ f }}</span>
        </div>
      </div>
    </div>

    <div class="routing-strategies">
      <h4>
        <Zap class="icon-sub" :size="18" /> {{ isEn ? 'Routing strategies' : '智能路由策略' }}
      </h4>
      <div class="strategies-grid">
        <div v-for="strategy in strategies" :key="strategy.id" class="strategy-item">
          <span class="badge" :class="strategy.id">
            <component :is="strategy.icon" :size="12" class="badge-icon" /> {{ strategy.label }}
          </span>
          <p>{{ strategy.description }}</p>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'
import { Bot, Zap, Scale, Brain, ShieldCheck, Cpu, Server, Layers } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const zhProviders = [
  {
    name: 'OpenAI',
    icon: Cpu,
    status: 'online',
    statusLabel: 'OpenAI 协议',
    models: 'GPT-5.5、GPT-5.5-mini',
    features: ['流式输出', '工具调用', 'JSON Mode']
  },
  {
    name: 'Anthropic',
    icon: Brain,
    status: 'online',
    statusLabel: 'Anthropic 协议',
    models: 'Claude Opus 5、Claude Sonnet 5',
    features: ['Messages API', 'Thinking', '长上下文']
  },
  {
    name: 'OpenAI-compatible',
    icon: Layers,
    status: 'online',
    statusLabel: '可编辑模板',
    models: 'Google、MiMo、OpenRouter、DeepSeek',
    features: ['自定义 Base URL', '代理与超时', '自定义模型']
  },
  {
    name: 'Ollama & LM Studio',
    icon: Server,
    status: 'local',
    statusLabel: '本地',
    models: 'Ollama qwen3、LM Studio local-model',
    features: ['offline 策略', '本地服务', '无需云端 Key']
  }
]

const enProviders = [
  { name: 'OpenAI', icon: Cpu, status: 'online', statusLabel: 'OpenAI protocol', models: 'GPT-5.5, GPT-5.5-mini', features: ['Streaming', 'Tool calls', 'JSON mode'] },
  { name: 'Anthropic', icon: Brain, status: 'online', statusLabel: 'Anthropic protocol', models: 'Claude Opus 5, Claude Sonnet 5', features: ['Messages API', 'Thinking', 'Long context'] },
  { name: 'OpenAI-compatible', icon: Layers, status: 'online', statusLabel: 'Editable templates', models: 'Google, MiMo, OpenRouter, DeepSeek', features: ['Custom Base URL', 'Proxy and timeout', 'Custom models'] },
  { name: 'Ollama & LM Studio', icon: Server, status: 'local', statusLabel: 'Local', models: 'Ollama qwen3, LM Studio local-model', features: ['Offline strategy', 'Local endpoint', 'No cloud key'] }
]

const providers = computed(() => isEn.value ? enProviders : zhProviders)

const strategies = computed(() => isEn.value ? [
  { id: 'fast', icon: Zap, label: 'Fast', description: 'Prioritizes low-latency models for small changes and quick analysis.' },
  { id: 'balanced', icon: Scale, label: 'Balanced', description: 'Balances quality, latency, and cost for everyday work.' },
  { id: 'quality', icon: Brain, label: 'Quality', description: 'Prioritizes high-capability models for complex refactors.' },
  { id: 'offline', icon: ShieldCheck, label: 'Offline', description: 'Routes to local Ollama or LM Studio models.' }
] : [
  { id: 'fast', icon: Zap, label: '快速 Fast', description: '优先低延迟模型，适合小修改与快速分析。' },
  { id: 'balanced', icon: Scale, label: '均衡 Balanced', description: '平衡质量、延迟和成本，适合日常任务。' },
  { id: 'quality', icon: Brain, label: '质量 Quality', description: '优先高能力模型，适合复杂重构与架构分析。' },
  { id: 'offline', icon: ShieldCheck, label: '离线 Offline', description: '把请求路由到本地 Ollama 或 LM Studio。' }
])
</script>

<style scoped>
.provider-matrix {
  margin: 3rem 0;
  border-radius: 16px;
  padding: 2rem;
}

.matrix-header h3 {
  font-size: 1.5rem;
  color: var(--seek-text-primary);
  margin-bottom: 0.5rem;
  font-weight: 800;
  display: flex;
  align-items: center;
  gap: 10px;
}

.icon-header {
  color: var(--vp-c-brand-1);
}

.matrix-header p {
  color: var(--seek-text-secondary);
  margin-bottom: 1.5rem;
}

.providers-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 1.25rem;
  margin-bottom: 2rem;
}

.provider-card {
  background: var(--vp-c-bg-soft);
  border: 1px solid var(--seek-card-border);
  border-radius: 12px;
  padding: 1.25rem;
  transition: all 0.3s;
}

.provider-card:hover {
  border-color: rgba(16, 185, 129, 0.5);
  transform: translateY(-2px);
}

.card-top {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 10px;
}

.provider-icon {
  color: var(--vp-c-brand-1);
}

.provider-name { font-weight: 700; color: var(--seek-text-primary); flex: 1; }

.status-pill {
  font-size: 0.7rem;
  padding: 2px 8px;
  border-radius: 9999px;
  font-weight: 600;
}
.status-pill.online { background: rgba(16, 185, 129, 0.18); color: var(--vp-c-brand-1); }
.status-pill.local { background: rgba(6, 182, 212, 0.18); color: #0284c7; }

.models-list {
  font-size: 0.85rem;
  color: var(--seek-text-secondary);
  margin-bottom: 12px;
}

.features-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.tag-chip {
  background: var(--vp-c-bg-alt);
  color: var(--seek-text-muted);
  font-size: 0.75rem;
  padding: 2px 8px;
  border-radius: 4px;
  border: 1px solid var(--seek-card-border);
}

.routing-strategies {
  border-top: 1px solid var(--seek-card-border);
  padding-top: 1.5rem;
}

.routing-strategies h4 {
  color: var(--seek-text-primary);
  font-size: 1.1rem;
  margin-bottom: 1rem;
  display: flex;
  align-items: center;
  gap: 8px;
}

.icon-sub {
  color: #0284c7;
}

.strategies-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1rem;
}

.strategy-item {
  background: var(--vp-c-bg-soft);
  padding: 1rem;
  border-radius: 8px;
  border: 1px solid var(--seek-card-border);
}

.strategy-item .badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 0.8rem;
  font-weight: 700;
  margin-bottom: 8px;
}

.badge-icon {
  vertical-align: middle;
}

.badge.fast { color: #0284c7; }
.badge.balanced { color: var(--vp-c-brand-1); }
.badge.quality { color: #7c3aed; }
.badge.offline { color: #d97706; }

.strategy-item p {
  font-size: 0.8rem;
  color: var(--seek-text-secondary);
  margin: 0;
  line-height: 1.5;
}
</style>
