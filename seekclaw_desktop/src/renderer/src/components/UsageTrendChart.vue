<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  calculateNiceMax,
  formatDateLabel,
  formatTokenCount,
  buildLinePath,
  buildAreaPath,
  type ChartPoint
} from './usage-chart-utils'

export interface TimelinePoint {
  date: string
  totalTokens: number
  inputTokens: number
  totalInputTokens: number
  cachedInputTokens: number
  outputTokens: number
  calls: number
  failures: number
  avgLatencyMs: number
}

const props = defineProps<{
  data: TimelinePoint[]
}>()

const activeMetric = ref<'tokens' | 'calls'>('tokens')
const hoverIndex = ref<number | null>(null)

const width = 640
const height = 210
const padding = { top: 16, right: 18, bottom: 28, left: 52 }
const plotWidth = width - padding.left - padding.right
const plotHeight = height - padding.top - padding.bottom

const maxVal = computed(() => {
  if (!props.data || props.data.length === 0) return activeMetric.value === 'tokens' ? 1000 : 10
  const values = props.data.map((d) => (activeMetric.value === 'tokens' ? d.totalTokens : d.calls))
  const highest = Math.max(...values, 0)
  return calculateNiceMax(highest, activeMetric.value === 'tokens' ? 1000 : 10)
})

const yTicks = computed(() => {
  const max = maxVal.value
  return [
    { value: max, y: padding.top, label: activeMetric.value === 'tokens' ? formatTokenCount(max) : `${max}` },
    { value: Math.round(max * 0.66), y: padding.top + plotHeight * 0.33, label: activeMetric.value === 'tokens' ? formatTokenCount(Math.round(max * 0.66)) : `${Math.round(max * 0.66)}` },
    { value: Math.round(max * 0.33), y: padding.top + plotHeight * 0.66, label: activeMetric.value === 'tokens' ? formatTokenCount(Math.round(max * 0.33)) : `${Math.round(max * 0.33)}` },
    { value: 0, y: padding.top + plotHeight, label: '0' }
  ]
})

const points = computed<Array<ChartPoint & { raw: TimelinePoint; value: number }>>(() => {
  if (!props.data || props.data.length === 0) return []
  const n = props.data.length
  const stepX = n > 1 ? plotWidth / (n - 1) : 0
  const max = maxVal.value

  return props.data.map((item, idx) => {
    const val = activeMetric.value === 'tokens' ? item.totalTokens : item.calls
    const ratio = max > 0 ? Math.min(1, Math.max(0, val / max)) : 0
    const x = n > 1 ? padding.left + idx * stepX : padding.left + plotWidth / 2
    const y = padding.top + plotHeight - ratio * plotHeight
    return { x, y, raw: item, value: val }
  })
})

const linePathData = computed(() => buildLinePath(points.value))
const areaPathData = computed(() => buildAreaPath(points.value, padding.top + plotHeight))

const hoveredPoint = computed(() => {
  if (hoverIndex.value === null || !points.value[hoverIndex.value]) return null
  return points.value[hoverIndex.value]
})

function onSvgMouseMove(e: MouseEvent): void {
  if (points.value.length === 0) return
  const svg = (e.currentTarget as SVGElement).getBoundingClientRect()
  const mouseX = ((e.clientX - svg.left) / svg.width) * width

  let closestIdx = 0
  let minDist = Infinity
  for (let i = 0; i < points.value.length; i++) {
    const pt = points.value[i]
    if (!pt) continue
    const dist = Math.abs(pt.x - mouseX)
    if (dist < minDist) {
      minDist = dist
      closestIdx = i
    }
  }
  hoverIndex.value = closestIdx
}

function onSvgMouseLeave(): void {
  hoverIndex.value = null
}

function shouldShowLabel(idx: number, total: number): boolean {
  if (total <= 8) return true
  if (total <= 16) return idx % 2 === 0 || idx === total - 1
  return idx % 4 === 0 || idx === total - 1
}
</script>

<template>
  <div class="trend-chart-card">
    <div class="trend-chart-header">
      <div class="trend-title-wrap">
        <span class="trend-chart-title">用量走向</span>
        <span class="trend-chart-sub">连续每日调用与 Token 波动趋势</span>
      </div>
      <div class="trend-metric-switch">
        <button
          type="button"
          class="metric-switch-btn"
          :class="{ active: activeMetric === 'tokens' }"
          @click="activeMetric = 'tokens'"
        >
          Tokens 走势
        </button>
        <button
          type="button"
          class="metric-switch-btn"
          :class="{ active: activeMetric === 'calls' }"
          @click="activeMetric = 'calls'"
        >
          调用量走势
        </button>
      </div>
    </div>

    <div class="trend-svg-wrap">
      <svg
        class="trend-svg"
        :viewBox="`0 0 ${width} ${height}`"
        preserveAspectRatio="none"
        @mousemove="onSvgMouseMove"
        @mouseleave="onSvgMouseLeave"
      >
        <defs>
          <linearGradient id="areaGradient" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="var(--accent)" stop-opacity="0.32" />
            <stop offset="85%" stop-color="var(--accent)" stop-opacity="0.03" />
            <stop offset="100%" stop-color="var(--accent)" stop-opacity="0.0" />
          </linearGradient>
        </defs>

        <!-- Y Axis Grid Lines & Labels -->
        <g class="chart-grid">
          <g v-for="tick in yTicks" :key="tick.y">
            <line
              :x1="padding.left"
              :y1="tick.y"
              :x2="width - padding.right"
              :y2="tick.y"
              class="grid-line"
            />
            <text
              :x="padding.left - 8"
              :y="tick.y + 4"
              text-anchor="end"
              class="axis-label"
            >
              {{ tick.label }}
            </text>
          </g>
        </g>

        <!-- Area Fill -->
        <path
          v-if="areaPathData"
          :d="areaPathData"
          fill="url(#areaGradient)"
          class="chart-area"
        />

        <!-- Smooth Line -->
        <path
          v-if="linePathData"
          :d="linePathData"
          fill="none"
          stroke="var(--accent)"
          stroke-width="2.5"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="chart-line"
        />

        <!-- X Axis Labels -->
        <g class="x-axis-labels">
          <template v-for="(p, idx) in points" :key="p.raw.date">
            <text
              v-if="shouldShowLabel(idx, points.length)"
              :x="p.x"
              :y="height - 8"
              text-anchor="middle"
              class="axis-label"
            >
              {{ formatDateLabel(p.raw.date) }}
            </text>
          </template>
        </g>

        <!-- Hover Indicator Line & Dot -->
        <g v-if="hoveredPoint" class="hover-group">
          <line
            :x1="hoveredPoint.x"
            :y1="padding.top"
            :x2="hoveredPoint.x"
            :y2="padding.top + plotHeight"
            stroke="var(--accent)"
            stroke-width="1.5"
            stroke-dasharray="3,3"
            class="hover-line"
          />
          <circle
            :cx="hoveredPoint.x"
            :cy="hoveredPoint.y"
            r="5.5"
            fill="var(--surface-raised)"
            stroke="var(--accent)"
            stroke-width="2.5"
            class="hover-dot"
          />
        </g>
      </svg>

      <!-- Floating Hover Tooltip -->
      <div
        v-if="hoveredPoint"
        class="chart-tooltip"
        :style="{
          left: `${(hoveredPoint.x / width) * 100}%`,
          top: `${Math.max(10, Math.min(height - 110, hoveredPoint.y - 45))}px`,
          transform: hoveredPoint.x / width > 0.65 ? 'translateX(-105%)' : 'translateX(10px)'
        }"
      >
        <div class="tooltip-header">
          <strong>{{ hoveredPoint.raw.date }}</strong>
        </div>
        <div v-if="activeMetric === 'tokens'" class="tooltip-body">
          <div class="tooltip-main-val">
            <span>总计：</span>
            <strong>{{ hoveredPoint.raw.totalTokens.toLocaleString() }} Tokens</strong>
          </div>
          <div class="tooltip-sub-row">
            <span>输入：</span>
            <span>{{ (hoveredPoint.raw.totalInputTokens || hoveredPoint.raw.inputTokens).toLocaleString() }}</span>
          </div>
          <div v-if="hoveredPoint.raw.cachedInputTokens" class="tooltip-sub-row">
            <span>缓存命中：</span>
            <span class="cache-highlight">{{ hoveredPoint.raw.cachedInputTokens.toLocaleString() }}</span>
          </div>
          <div class="tooltip-sub-row">
            <span>输出：</span>
            <span>{{ hoveredPoint.raw.outputTokens.toLocaleString() }}</span>
          </div>
        </div>
        <div v-else class="tooltip-body">
          <div class="tooltip-main-val">
            <span>调用次数：</span>
            <strong>{{ hoveredPoint.raw.calls }} 次</strong>
          </div>
          <div class="tooltip-sub-row">
            <span>成功率：</span>
            <span>{{ hoveredPoint.raw.calls > 0 ? Math.round(((hoveredPoint.raw.calls - hoveredPoint.raw.failures) / hoveredPoint.raw.calls) * 100) : 100 }}%</span>
          </div>
          <div class="tooltip-sub-row">
            <span>平均延迟：</span>
            <span>{{ Math.round(hoveredPoint.raw.avgLatencyMs) }} ms</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
