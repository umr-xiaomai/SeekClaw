<script setup lang="ts">
import { Columns2, GitCompareArrows, LoaderCircle, X } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

const props = defineProps<{
  open: boolean
  models: string[]
  daemonConnected: boolean
}>()

const emit = defineEmits<{ close: [] }>()

interface CompareResult {
  ref: string
  text: string
  error?: string
}

const prompt = ref('')
const selected = ref<string[]>([])
const results = ref<CompareResult[]>([])
const running = ref(false)
const error = ref('')

watch(() => props.open, (open) => {
  if (!open) return
  selected.value = props.models.slice(0, 2)
  error.value = ''
})

async function run(): Promise<void> {
  if (!props.daemonConnected || selected.value.length < 2 || !prompt.value.trim() || running.value) return
  running.value = true
  error.value = ''
  results.value = []
  try {
    const response = await window.seekclaw.daemon.request('model.compare', {
      prompt: prompt.value.trim(),
      models: selected.value
    }, { timeoutMs: 120_000 })
    results.value = JSON.parse(response.data) as CompareResult[]
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    running.value = false
  }
}

function toggleModel(ref: string): void {
  selected.value = selected.value.includes(ref)
    ? selected.value.filter((item) => item !== ref)
    : [...selected.value, ref]
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open" class="modal-backdrop compare-backdrop" @mousedown.self="emit('close')">
      <section class="compare-dialog" role="dialog" aria-modal="true" aria-labelledby="compare-title">
        <header class="compare-header">
          <div>
            <span class="compare-eyebrow"><GitCompareArrows :size="14" /> MODEL COMPARE</span>
            <h2 id="compare-title">多模型对比</h2>
            <p>同一问题交给多个模型，并排查看不同回答。</p>
          </div>
          <button class="icon-button" title="关闭" @click="emit('close')"><X :size="19" /></button>
        </header>

        <div class="compare-body">
          <label class="compare-prompt-field">
            <span>问题</span>
            <textarea v-model="prompt" rows="4" placeholder="输入要对比的问题…" />
          </label>

          <div class="compare-models-field">
            <span>参与对比的模型（至少 2 个）</span>
            <div class="compare-model-chips">
              <button
                v-for="ref in models"
                :key="ref"
                type="button"
                class="compare-model-chip"
                :class="{ active: selected.includes(ref) }"
                @click="toggleModel(ref)"
              >{{ ref }}</button>
              <p v-if="models.length === 0" class="compare-models-empty">没有可用的模型，请先在「模型与 Provider」中配置。</p>
            </div>
          </div>

          <p v-if="error" class="compare-error">{{ error }}</p>

          <div class="compare-run-row">
            <button
              class="secondary-button primary-action"
              :disabled="running || selected.length < 2 || !prompt.trim()"
              @click="run"
            >
              <LoaderCircle v-if="running" :size="15" class="spin" />
              <Columns2 v-else :size="15" />
              {{ running ? '对比中…' : '开始对比' }}
            </button>
          </div>

          <div v-if="results.length" class="compare-results" :style="{ gridTemplateColumns: `repeat(${results.length}, minmax(0, 1fr))` }">
            <article v-for="result in results" :key="result.ref" class="compare-result" :class="{ failed: result.error }">
              <header class="compare-result-header">
                <span>{{ result.ref }}</span>
                <small v-if="result.error">失败</small>
              </header>
              <pre v-if="result.error">{{ result.error }}</pre>
              <pre v-else>{{ result.text || '（空回复）' }}</pre>
            </article>
          </div>
        </div>
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.compare-backdrop {
  z-index: 135;
  padding: clamp(16px, 4vw, 54px);
  background: rgb(14 17 14 / 38%);
  backdrop-filter: blur(3px);
}

.compare-dialog {
  display: grid;
  width: min(1100px, 100%);
  height: min(820px, calc(100vh - 44px));
  grid-template-rows: auto minmax(0, 1fr);
  min-height: 0;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 18px;
  box-shadow: 0 24px 70px rgb(15 19 16 / 24%);
}

.compare-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  padding: 21px 22px 18px;
  border-bottom: 1px solid var(--border);
}

.compare-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--accent);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .1em;
}

.compare-header h2,
.compare-header p {
  margin: 0;
}

.compare-header h2 {
  margin-top: 8px;
  font-size: 24px;
  font-weight: 650;
  letter-spacing: -.02em;
}

.compare-header p {
  margin-top: 6px;
  color: var(--text-secondary);
  font-size: 12px;
}

.compare-body {
  min-height: 0;
  overflow-y: auto;
  padding: 18px 22px;
}

.compare-prompt-field,
.compare-models-field {
  display: grid;
  gap: 7px;
  margin-bottom: 16px;
}

.compare-prompt-field > span,
.compare-models-field > span {
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 600;
}

.compare-prompt-field textarea {
  width: 100%;
  resize: vertical;
  padding: 10px 12px;
  color: var(--text);
  font-size: 13px;
  line-height: 1.5;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  outline: 0;
}

.compare-prompt-field textarea:focus {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.compare-model-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.compare-model-chip {
  padding: 6px 12px;
  color: var(--text-secondary);
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.compare-model-chip:hover {
  color: var(--text);
  border-color: var(--border-strong);
}

.compare-model-chip.active {
  color: var(--accent);
  background: var(--accent-soft);
  border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
}

.compare-models-empty {
  margin: 0;
  color: var(--text-muted);
  font-size: 11px;
}

.compare-error {
  margin: 0 0 12px;
  color: var(--danger);
  font-size: 12px;
}

.compare-run-row {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 16px;
}

.compare-results {
  display: grid;
  gap: 12px;
}

.compare-result {
  min-width: 0;
  overflow: hidden;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.compare-result.failed {
  border-color: color-mix(in srgb, var(--danger) 45%, var(--border));
}

.compare-result-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 12px;
  font-size: 12px;
  font-weight: 600;
  border-bottom: 1px solid var(--border);
}

.compare-result-header small {
  color: var(--danger);
  font-size: 10.5px;
}

.compare-result pre {
  margin: 0;
  padding: 12px;
  overflow: auto;
  max-height: 420px;
  color: var(--text-secondary);
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 11.5px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.spin {
  animation: compare-spin .8s linear infinite;
}

@keyframes compare-spin {
  to { transform: rotate(360deg); }
}
</style>
