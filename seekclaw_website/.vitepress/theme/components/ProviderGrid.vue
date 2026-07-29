<template>
  <div class="provider-matrix seek-glass-card">
    <div class="matrix-header">
      <h3>
        <Bot class="icon-header" :size="22" /> 多模型提供商支持与路由矩阵
      </h3>
      <p>统一接口无缝对接顶级云端 LLM 与本地开源模型，支持多维度负载均衡与熔断避障</p>
    </div>

    <div class="providers-grid">
      <div v-for="p in providers" :key="p.name" class="provider-card">
        <div class="card-top">
          <component :is="p.icon" class="provider-icon" :size="20" />
          <span class="provider-name">{{ p.name }}</span>
          <span class="status-pill" :class="p.status">{{ p.statusLabel }}</span>
        </div>
        <p class="models-list"><strong>支持模型:</strong> {{ p.models }}</p>
        <div class="features-tags">
          <span v-for="f in p.features" :key="f" class="tag-chip">{{ f }}</span>
        </div>
      </div>
    </div>

    <div class="routing-strategies">
      <h4>
        <Zap class="icon-sub" :size="18" /> 智能路由策略 (Routing Strategies)
      </h4>
      <div class="strategies-grid">
        <div class="strategy-item">
          <span class="badge fast">
            <Zap :size="12" class="badge-icon" /> 快速 (Fast)
          </span>
          <p>优先调度低延迟小模型（如 GPT-5.5-mini / Claude Haiku / Gemini Flash），适合小修改与代码格式化。</p>
        </div>
        <div class="strategy-item">
          <span class="badge balanced">
            <Scale :size="12" class="badge-icon" /> 均衡 (Balanced)
          </span>
          <p>兼顾代码质量与推理成本，在速度与深度之间自动取得最佳权衡。</p>
        </div>
        <div class="strategy-item">
          <span class="badge quality">
            <Brain :size="12" class="badge-icon" /> 质量 (Quality)
          </span>
          <p>调度顶级旗舰大模型（如 GPT-5.5 / Claude Opus），用于复杂重构、架构规划与疑难 Debug。</p>
        </div>
        <div class="strategy-item">
          <span class="badge offline">
            <ShieldCheck :size="12" class="badge-icon" /> 离线 (Offline)
          </span>
          <p>零外网访问，完全路由至本地 Ollama 或 LM Studio 本地部署模型，确保商业代码隐私安全。</p>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { Bot, Zap, Scale, Brain, ShieldCheck, Cpu, Server, Layers } from 'lucide-vue-next'

const providers = [
  {
    name: 'OpenAI 兼容接口',
    icon: Cpu,
    status: 'online',
    statusLabel: '原生态支持',
    models: 'GPT-5.5, GPT-5.5-mini, DeepSeek V3/R1, Qwen 2.5',
    features: ['流式 Token', '工具调用 (Function Calling)', 'JSON Schema 约束']
  },
  {
    name: 'Anthropic Claude',
    icon: Brain,
    status: 'online',
    statusLabel: '原生态支持',
    models: 'Claude Opus, Claude Sonnet, Claude Haiku',
    features: ['系统 Prompt 增强', '长上下文推理', '多轮思维链']
  },
  {
    name: 'Google Gemini',
    icon: Layers,
    status: 'online',
    statusLabel: '原生态支持',
    models: 'Gemini Pro, Gemini Flash',
    features: ['超长 Context Window', '高并发吞吐', '多模态准备']
  },
  {
    name: 'Ollama & LM Studio',
    icon: Server,
    status: 'local',
    statusLabel: '本地局域网',
    models: 'Llama 3.3, DeepSeek R1 Local, Mistral',
    features: ['零网络出境', '本地 GPU 加速', '离线降级备选']
  }
]
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
