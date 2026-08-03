<script setup lang="ts">
import { Check, ChevronDown, Gavel, LoaderCircle, ShieldAlert, X } from '@lucide/vue'
import { computed, ref } from 'vue'
import type { PanelReviewState } from '../types'

const props = defineProps<{
  panel?: PanelReviewState
}>()

const expanded = ref<string | null>(null)

const visible = computed(() => Boolean(props.panel?.running || props.panel?.reviews.length))

const issueTotal = computed(() =>
  (props.panel?.reviews ?? []).reduce((sum, review) => sum + (review.issueCount ?? 0), 0))

function toggle(ref: string): void {
  expanded.value = expanded.value === ref ? null : ref
}
</script>

<template>
  <Transition name="panel-card">
    <section v-if="visible && panel" class="panel-card" :class="{ complete: !panel.running }">
      <header class="panel-card-header">
        <div class="panel-title">
          <span class="panel-icon"><Gavel :size="15" /></span>
          <div>
            <strong>{{ panel.running ? '评审团审查中' : '评审团完成' }}</strong>
            <small>
              <template v-if="panel.running">正在由多个模型对抗式审查本轮结果…</template>
              <template v-else>
                共发现 {{ issueTotal }} 个问题{{ issueTotal > 0 ? '，已反馈给主 Agent 修复' : '，本轮通过' }}
              </template>
            </small>
          </div>
        </div>
        <span class="panel-round">第 {{ panel.round ?? 1 }} 轮</span>
      </header>

      <ul class="panel-review-list">
        <li
          v-for="review in panel.reviews"
          :key="review.ref"
          class="panel-review-row"
          :class="review.status"
        >
          <button class="panel-review-main" type="button" @click="toggle(review.ref)">
            <span class="panel-review-status">
              <LoaderCircle v-if="review.status === 'reviewing'" class="spin" :size="15" />
              <Check v-else-if="review.status === 'passed'" :size="15" />
              <ShieldAlert v-else-if="review.status === 'issues'" :size="15" />
              <X v-else :size="15" />
            </span>
            <span class="panel-review-ref">{{ review.ref }}</span>
            <span class="panel-review-verdict">
              <template v-if="review.status === 'reviewing'">审查中…</template>
              <template v-else-if="review.status === 'passed'">通过</template>
              <template v-else-if="review.status === 'issues'">{{ review.issueCount }} 个问题</template>
              <template v-else>失败</template>
            </span>
            <ChevronDown
              v-if="review.summary && review.status !== 'reviewing'"
              class="panel-chevron"
              :class="{ rotated: expanded === review.ref }"
              :size="14"
            />
          </button>
          <Transition name="panel-expand">
            <pre v-if="expanded === review.ref && review.summary" class="panel-review-summary">{{ review.summary }}</pre>
          </Transition>
        </li>
      </ul>
    </section>
  </Transition>
</template>

<style scoped>
.panel-card {
  margin: 0 0 12px;
  overflow: hidden;
  background: color-mix(in srgb, var(--surface-raised) 88%, var(--accent-soft) 12%);
  border: 1px solid var(--border);
  border-radius: 14px;
}

.panel-card.complete {
  background: var(--surface-raised);
}

.panel-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 11px 14px;
}

.panel-title {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 10px;
}

.panel-icon {
  display: grid;
  width: 28px;
  height: 28px;
  flex: none;
  place-items: center;
  color: var(--accent);
  background: var(--accent-soft);
  border-radius: 8px;
}

.panel-title strong,
.panel-title small {
  display: block;
}

.panel-title strong {
  font-size: 13px;
}

.panel-title small {
  margin-top: 2px;
  color: var(--text-secondary);
  font-size: 11px;
}

.panel-round {
  flex: none;
  padding: 3px 9px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.panel-review-list {
  margin: 0;
  padding: 0 10px 10px;
  list-style: none;
}

.panel-review-row {
  margin-top: 6px;
  overflow: hidden;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.panel-review-row.issues {
  border-color: color-mix(in srgb, var(--danger) 45%, var(--border));
}

.panel-review-row.passed {
  border-color: color-mix(in srgb, var(--accent) 35%, var(--border));
}

.panel-review-main {
  display: flex;
  width: 100%;
  min-height: 40px;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  color: var(--text);
  text-align: left;
  background: transparent;
}

.panel-review-main:hover {
  background: var(--surface-hover);
}

.panel-review-status {
  display: grid;
  width: 22px;
  height: 22px;
  flex: none;
  place-items: center;
  color: var(--text-muted);
}

.panel-review-row.passed .panel-review-status {
  color: var(--accent);
}

.panel-review-row.issues .panel-review-status {
  color: var(--danger);
}

.panel-review-ref {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  font-size: 12px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-review-verdict {
  flex: none;
  color: var(--text-secondary);
  font-size: 11px;
}

.panel-review-row.issues .panel-review-verdict {
  color: var(--danger);
}

.panel-chevron {
  flex: none;
  color: var(--text-muted);
  transition: transform 160ms ease;
}

.panel-chevron.rotated {
  transform: rotate(180deg);
}

.panel-review-summary {
  margin: 0;
  padding: 10px 12px;
  overflow: auto;
  max-height: 220px;
  color: var(--text-secondary);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
  border-top: 1px solid var(--border);
}

.spin {
  animation: panel-spin .8s linear infinite;
}

@keyframes panel-spin {
  to { transform: rotate(360deg); }
}

.panel-card-enter-active,
.panel-card-leave-active {
  transition: opacity 180ms ease, transform 200ms ease;
}

.panel-card-enter-from,
.panel-card-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}

.panel-expand-enter-active,
.panel-expand-leave-active {
  transition: opacity 150ms ease;
}

.panel-expand-enter-from,
.panel-expand-leave-to {
  opacity: 0;
}
</style>
