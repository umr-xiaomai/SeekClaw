<script setup lang="ts">
import { ArrowLeft, PackageOpen, RefreshCw, Search, Store } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

/** 官方技能市场条目。后续由 daemon 的官方技能目录接口返回。 */
interface OfficialSkill {
  id: string
  name: string
  description: string
  version?: string
  tags?: string[]
  enabled: boolean
}

// TODO(官方技能): 上线时改为从 daemon 加载目录（例如 skill.official.list）。
// 当前阶段列表为空，用于验证市场入口与 UI/UX。
const catalog = ref<OfficialSkill[]>([])
const loading = ref(false)
const query = ref('')

const filtered = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase()
  if (!normalized) return catalog.value
  return catalog.value.filter((skill) =>
    skill.name.toLocaleLowerCase().includes(normalized) ||
    skill.description.toLocaleLowerCase().includes(normalized))
})

async function loadCatalog(): Promise<void> {
  loading.value = true
  try {
    // const response = await window.seekclaw.daemon.request('skill.official.list')
    // catalog.value = JSON.parse(response.data) as OfficialSkill[]
  } finally {
    loading.value = false
  }
}

watch(() => props.open, (open) => {
  if (open) {
    query.value = ''
    void loadCatalog()
  }
})

function closeOnEscape(event: KeyboardEvent): void {
  if (props.open && event.key === 'Escape') emit('close')
}

onMounted(() => document.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => document.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <section
    v-if="open"
    class="official-skills-dialog embedded-page"
    role="region"
    aria-labelledby="official-skills-title"
  >
    <header class="official-skills-header">
      <div class="official-skills-heading">
        <button class="page-back-button" type="button" @click="emit('close')">
          <ArrowLeft :size="18" />
          <span>返回应用</span>
        </button>
        <div class="official-skills-title-copy">
          <span class="official-skills-eyebrow"><Store :size="14" /> OFFICIAL SKILLS</span>
          <h2 id="official-skills-title">
            官方技能
            <span class="official-skills-chip">建设中</span>
          </h2>
          <p>从官方技能市场中选择并开启你需要的功能，随时可以关闭。</p>
        </div>
      </div>
    </header>

        <div class="official-skills-toolbar">
          <label class="official-skills-search">
            <Search :size="17" />
            <input v-model="query" autofocus placeholder="搜索官方技能" aria-label="搜索官方技能" />
          </label>
          <button class="icon-button" title="刷新" :disabled="loading" @click="loadCatalog">
            <RefreshCw :size="17" :class="{ spinning: loading }" />
          </button>
        </div>

        <div class="official-skills-list">
          <div v-if="catalog.length === 0" class="official-skills-empty">
            <PackageOpen :size="34" />
            <strong>{{ query.trim() ? '没有匹配的官方技能' : '官方技能列表为空' }}</strong>
            <span>
              {{ query.trim() ? '试试其他搜索词。' : '官方技能市场正在建设中，更多可选的官方能力即将上线。' }}
            </span>
          </div>
          <div v-else-if="filtered.length === 0" class="official-skills-empty">
            <Search :size="30" />
            <strong>没有匹配的官方技能</strong>
            <span>试试其他搜索词。</span>
          </div>
          <div v-else class="official-skills-items">
            <div v-for="skill in filtered" :key="skill.id" class="official-skill-row">
              <div class="official-skill-icon"><PackageOpen :size="18" /></div>
              <div class="list-main">
                <div>
                  <strong>{{ skill.name }}</strong>
                  <span v-if="skill.version" class="version-text">v{{ skill.version }}</span>
                  <span v-for="tag in skill.tags" :key="tag" class="inline-badge">{{ tag }}</span>
                </div>
                <small>{{ skill.description }}</small>
              </div>
              <button class="switch-control" :class="{ active: skill.enabled }" aria-label="启用/禁用"><span /></button>
            </div>
          </div>
        </div>

        <footer class="official-skills-footer">
          官方技能由 SeekClaw 团队维护 · 本地技能请前往「设置 → Skills」管理
        </footer>
  </section>
</template>

<style scoped>
.official-skills-backdrop {
  z-index: 130;
  padding: clamp(16px, 4vw, 54px);
  background: rgb(14 17 14 / 38%);
  backdrop-filter: blur(3px);
}

.official-skills-dialog.embedded-page {
  width: 100%;
  height: 100%;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: var(--bg);
}

.official-skills-dialog {
  display: grid;
  width: min(760px, 100%);
  height: min(620px, calc(100vh - 44px));
  grid-template-rows: auto auto minmax(0, 1fr) auto;
  min-height: 0;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 18px;
  box-shadow: 0 24px 70px rgb(15 19 16 / 24%);
}

.official-skills-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  padding: 21px 22px 18px;
  border-bottom: 1px solid var(--border);
}

.official-skills-heading {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 12px;
}

.official-skills-title-copy {
  min-width: 0;
}

.official-skills-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--accent);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .1em;
}

.official-skills-header h2,
.official-skills-header p {
  margin: 0;
}

.official-skills-header h2 {
  display: flex;
  align-items: center;
  gap: 9px;
  margin-top: 8px;
  font-size: 25px;
  font-weight: 650;
  letter-spacing: -.02em;
}

.official-skills-chip {
  padding: 3px 9px;
  color: var(--accent);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0;
  background: var(--accent-soft);
  border-radius: 999px;
}

.official-skills-header p {
  margin-top: 7px;
  color: var(--text-secondary);
  font-size: 12px;
}

.official-skills-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 18px 22px 14px;
}

.official-skills-search {
  display: flex;
  min-width: 0;
  height: 40px;
  flex: 1;
  align-items: center;
  gap: 9px;
  padding: 0 13px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.official-skills-search:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.official-skills-search input {
  min-width: 0;
  flex: 1;
  color: var(--text);
  background: transparent;
  border: 0;
  outline: 0;
}

.official-skills-search input::placeholder {
  color: var(--text-muted);
}

.official-skills-list {
  min-height: 0;
  overflow-y: auto;
  padding: 0 22px 16px;
}

.official-skills-empty {
  display: grid;
  height: 100%;
  min-height: 260px;
  place-content: center;
  justify-items: center;
  gap: 7px;
  color: var(--text-muted);
  text-align: center;
}

.official-skills-empty svg {
  margin-bottom: 6px;
  color: var(--text-muted);
  opacity: .7;
}

.official-skills-empty strong {
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

.official-skills-empty span {
  font-size: 12px;
}

.official-skills-items {
  display: grid;
  gap: 8px;
}

.official-skill-row {
  display: flex;
  min-width: 0;
  min-height: 72px;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.official-skill-icon {
  display: grid;
  width: 40px;
  height: 40px;
  flex: none;
  place-items: center;
  color: var(--accent);
  background: var(--accent-soft);
  border-radius: 10px;
}

.official-skill-row .list-main {
  min-width: 0;
  flex: 1;
}

.official-skill-row .list-main > div {
  display: flex;
  align-items: center;
  gap: 8px;
}

.official-skill-row small {
  display: block;
  margin-top: 4px;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.official-skills-footer {
  padding: 12px 22px;
  color: var(--text-muted);
  font-size: 11px;
  text-align: center;
  border-top: 1px solid var(--border);
}

.spinning {
  animation: official-skills-spin .8s linear infinite;
}

@keyframes official-skills-spin {
  to { transform: rotate(360deg); }
}
</style>
