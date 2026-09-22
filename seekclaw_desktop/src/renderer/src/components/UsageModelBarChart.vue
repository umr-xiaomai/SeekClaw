<script setup lang="ts">
import { computed } from 'vue'
import { formatTokenCount } from './usage-chart-utils'

export interface ModelUsageItem {
  provider: string
  model: string
  calls: number
  failures: number
  inputTokens: number
  totalInputTokens?: number
  cachedInputTokens?: number
  cacheCreationInputTokens?: number
  outputTokens: number
  totalTokens?: number
  avgLatencyMs: number
  successRate: number
}

const props = defineProps<{
  items: ModelUsageItem[]
}>()

function promptInputTokens(item: ModelUsageItem): number {
  return item.totalInputTokens && item.totalInputTokens > 0 ? item.totalInputTokens : item.inputTokens
}

const sortedModels = computed(() => {
  const mapped = props.items.map((item) => {
    const promptIn = promptInputTokens(item)
    const cached = item.cachedInputTokens ?? 0
    const regularIn = Math.max(0, promptIn - cached)
    const out = item.outputTokens
    const total = promptIn + out
    return {
      ...item,
      promptIn,
      cached,
      regularIn,
      out,
      total
    }
  })

  const hasAnyTokens = mapped.some((m) => m.total > 0)
  const filtered = hasAnyTokens ? mapped.filter((m) => m.total > 0) : mapped.filter((m) => m.calls > 0)

  return filtered
    .sort((a, b) => {
      if (b.total !== a.total) return b.total - a.total
      return b.calls - a.calls
    })
    .slice(0, 6)
})

const maxModelTotal = computed(() => {
  if (sortedModels.value.length === 0) return 1
  return Math.max(...sortedModels.value.map((m) => m.total), 1)
})
</script>

<template>
  <div class="model-bar-card">
    <div class="model-bar-header">
      <div class="model-bar-title-wrap">
        <span class="model-bar-title">模型用量分布</span>
        <span class="model-bar-sub">对比各模型消耗规模与 Token 构成</span>
      </div>
      <div class="model-bar-legend">
        <span class="legend-item"><i class="legend-dot dot-cached" />缓存输入</span>
        <span class="legend-item"><i class="legend-dot dot-input" />普通输入</span>
        <span class="legend-item"><i class="legend-dot dot-output" />生成输出</span>
      </div>
    </div>

    <div v-if="sortedModels.length === 0" class="model-bar-empty">
      暂无模型用量记录
    </div>

    <div v-else class="model-bar-list">
      <div
        v-for="item in sortedModels"
        :key="`${item.provider}/${item.model}`"
        class="model-bar-row"
      >
        <div class="model-row-meta">
          <div class="model-name-group">
            <strong class="model-id" :title="`${item.provider}/${item.model}`">
              {{ item.provider }}/{{ item.model }}
            </strong>
            <span class="model-calls-tag">{{ item.calls }} 次调用</span>
          </div>
          <div class="model-tokens-stat">
            <strong>{{ item.total.toLocaleString() }}</strong>
            <span class="stat-unit">Tokens</span>
          </div>
        </div>

        <!-- Relative Bar -->
        <div class="model-progress-track">
          <div
            class="model-progress-bar"
            :style="{ width: item.total > 0 ? `${Math.max(3, Math.min(100, (item.total / maxModelTotal) * 100))}%` : '0%' }"
          >
            <!-- Segment: Cached Input -->
            <div
              v-if="item.cached > 0"
              class="bar-segment seg-cached"
              :style="{ width: `${item.total > 0 ? (item.cached / item.total) * 100 : 0}%` }"
              :title="`缓存命中输入: ${item.cached.toLocaleString()}`"
            />
            <!-- Segment: Regular Input -->
            <div
              v-if="item.regularIn > 0"
              class="bar-segment seg-input"
              :style="{ width: `${item.total > 0 ? (item.regularIn / item.total) * 100 : 0}%` }"
              :title="`普通输入: ${item.regularIn.toLocaleString()}`"
            />
            <!-- Segment: Output -->
            <div
              v-if="item.out > 0"
              class="bar-segment seg-output"
              :style="{ width: `${item.total > 0 ? (item.out / item.total) * 100 : 0}%` }"
              :title="`生成输出: ${item.out.toLocaleString()}`"
            />
          </div>
        </div>

        <div class="model-row-footer">
          <div class="segment-breakdown">
            <span v-if="item.cached > 0" class="breakdown-tag">
              缓存: {{ formatTokenCount(item.cached) }}
            </span>
            <span class="breakdown-tag">
              输入: {{ formatTokenCount(item.regularIn) }}
            </span>
            <span class="breakdown-tag">
              输出: {{ formatTokenCount(item.out) }}
            </span>
          </div>
          <span class="model-latency-tag">均延: {{ Math.round(item.avgLatencyMs) }}ms</span>
        </div>
      </div>
    </div>
  </div>
</template>
