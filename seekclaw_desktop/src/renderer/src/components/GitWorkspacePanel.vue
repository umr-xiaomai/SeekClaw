<script setup lang="ts">
import { Braces, GitBranch, History, RefreshCw, TerminalSquare, X } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import type { GitHistory, GitOverview } from '../../../shared/ipc'
import type { ProjectItem } from '../types'

type PanelTab = 'diff' | 'history'

const props = defineProps<{
  open: boolean
  project?: ProjectItem
  initialTab: PanelTab
  diffOverride?: { path: string; diff: string } | null
}>()

const emit = defineEmits<{
  close: []
  openTerminal: []
}>()

const tab = ref<PanelTab>('diff')
const overview = ref<GitOverview | null>(null)
const history = ref<GitHistory | null>(null)
const loading = ref(false)
const requestId = ref(0)

const displayedDiff = computed(() => props.diffOverride?.diff ?? overview.value?.diff ?? '')
const diffLines = computed(() => displayedDiff.value.split(/\r?\n/).filter((line, index, lines) =>
  line.length > 0 || (index > 0 && index < lines.length - 1)))

function diffLineClass(line: string): string {
  if (line.startsWith('+++') || line.startsWith('---')) return 'diff-line-meta'
  if (line.startsWith('+')) return 'diff-line-addition'
  if (line.startsWith('-')) return 'diff-line-deletion'
  if (line.startsWith('@@')) return 'diff-line-hunk'
  if (line.startsWith('diff --git') || line.startsWith('# ')) return 'diff-line-file'
  return 'diff-line-context'
}

function statusLabel(line: string): string {
  const code = line.slice(0, 2).trim() || '?'
  const labels: Record<string, string> = {
    M: '修改', A: '新增', D: '删除', R: '重命名', C: '复制', U: '冲突', '??': '未跟踪'
  }
  return labels[code] ?? labels[code.at(-1) ?? ''] ?? code
}

function statusPath(line: string): string {
  return line.length > 3 ? line.slice(3) : line
}

function formatDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat('zh-CN', {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit'
  }).format(date)
}

async function refresh(): Promise<void> {
  if (!props.open || !props.project || props.diffOverride) return
  const currentRequest = ++requestId.value
  loading.value = true
  try {
    if (tab.value === 'diff') {
      const value = await window.seekclaw.project.gitOverview(props.project.path)
      if (currentRequest === requestId.value) overview.value = value
    } else {
      const value = await window.seekclaw.project.gitHistory(props.project.path)
      if (currentRequest === requestId.value) history.value = value
    }
  } finally {
    if (currentRequest === requestId.value) loading.value = false
  }
}

function selectTab(value: PanelTab): void {
  tab.value = value
}

watch(() => [props.open, props.project?.id, props.initialTab, props.diffOverride?.path] as const, ([open]) => {
  if (!open) {
    requestId.value++
    return
  }
  tab.value = props.initialTab
  void refresh()
}, { immediate: true })

watch(tab, () => {
  if (props.open) void refresh()
})
</script>

<template>
  <Transition name="drawer">
    <div v-if="open" class="git-panel-layer">
      <button class="git-panel-scrim" aria-label="关闭 Git 面板" @click="emit('close')" />
      <aside class="git-panel" aria-label="项目 Git 工具">
        <header class="git-panel-header">
          <div class="git-panel-title">
            <Braces :size="19" />
            <div>
              <strong>{{ diffOverride ? '文件 Diff' : '项目更改' }}</strong>
              <small>{{ diffOverride?.path || project?.name }}</small>
            </div>
          </div>
          <div class="git-panel-actions">
            <button class="icon-button" title="在项目目录打开终端" :disabled="!project" @click="emit('openTerminal')">
              <TerminalSquare :size="18" />
            </button>
            <button class="icon-button" title="刷新" :disabled="loading || !project" @click="refresh">
              <RefreshCw :size="18" :class="{ spin: loading }" />
            </button>
            <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
          </div>
        </header>

        <div class="git-panel-tabs" role="tablist">
          <button :class="{ active: tab === 'diff' }" role="tab" @click="selectTab('diff')">
            <Braces :size="16" />更改
          </button>
          <button v-if="!diffOverride" :class="{ active: tab === 'history' }" role="tab" @click="selectTab('history')">
            <History :size="16" />提交记录
          </button>
        </div>

        <div class="git-panel-body">
          <template v-if="diffOverride">
            <div class="git-repository-summary tool-diff-summary">
              <span><Braces :size="15" />本次修改</span>
              <small>{{ diffOverride.path }}</small>
            </div>
            <pre v-if="diffLines.length" class="git-diff"><code><span
              v-for="(line, index) in diffLines"
              :key="`${index}:${line}`"
              class="diff-line"
              :class="diffLineClass(line)"
            >{{ line }}
</span></code></pre>
            <div v-else class="git-panel-state">没有可显示的 Diff。</div>
          </template>

          <div v-else-if="loading" class="git-panel-state"><RefreshCw :size="18" class="spin" />正在读取 Git 数据…</div>

          <template v-else-if="tab === 'diff'">
            <div v-if="overview?.isRepository" class="git-repository-summary">
              <span><GitBranch :size="15" />{{ overview.branch }}</span>
              <small>{{ overview.root }}</small>
            </div>
            <div v-if="overview?.error" class="git-panel-state is-error">{{ overview.error }}</div>
            <template v-else-if="overview?.isRepository">
              <section v-if="overview.status.length" class="git-status-list">
                <div v-for="line in overview.status" :key="line" class="git-status-row">
                  <span>{{ statusLabel(line) }}</span><code>{{ statusPath(line) }}</code>
                </div>
              </section>
              <pre v-if="diffLines.length" class="git-diff"><code><span
                v-for="(line, index) in diffLines"
                :key="`${index}:${line}`"
                class="diff-line"
                :class="diffLineClass(line)"
              >{{ line }}
</span></code></pre>
              <div v-else-if="overview.status.length" class="git-panel-state">未跟踪文件尚无可显示的行级 Diff。</div>
              <div v-else class="git-panel-state">工作区是干净的，没有待查看的更改。</div>
            </template>
            <div v-else-if="overview" class="git-panel-state">当前项目不是 Git 仓库。</div>
          </template>

          <template v-else>
            <div v-if="history?.error" class="git-panel-state is-error">{{ history.error }}</div>
            <ol v-else-if="history?.commits.length" class="git-history-list">
              <li v-for="commit in history.commits" :key="commit.hash">
                <span class="git-history-dot" />
                <div>
                  <strong>{{ commit.subject }}</strong>
                  <p><code>{{ commit.shortHash }}</code> · {{ commit.author }}</p>
                </div>
                <time :datetime="commit.authoredAt">{{ formatDate(commit.authoredAt) }}</time>
              </li>
            </ol>
            <div v-else-if="history" class="git-panel-state">没有可显示的提交记录。</div>
          </template>
        </div>
      </aside>
    </div>
  </Transition>
</template>
