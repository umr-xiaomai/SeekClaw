<script setup lang="ts">
import {
  ArrowLeft,
  Blocks,
  Code2,
  FolderOpen,
  PackageOpen,
  RefreshCw,
  Search,
  Sparkles,
  Store,
  Wrench
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import TwoPaneLayout from './TwoPaneLayout.vue'

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

const catalog = ref<OfficialSkill[]>([])
const loading = ref(false)
const query = ref('')
const activeCategory = ref('all')

const categories = [
  { id: 'all', label: '全部技能', icon: Store },
  { id: 'featured', label: '官方推荐', icon: Sparkles },
  { id: 'dev', label: '开发与代码', icon: Code2 },
  { id: 'tools', label: '工作流与工具', icon: Wrench }
]

const filtered = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase()
  if (!normalized) return catalog.value
  return catalog.value.filter((skill) =>
    skill.name.toLocaleLowerCase().includes(normalized) ||
    skill.description.toLocaleLowerCase().includes(normalized))
})

const activeCategoryLabel = computed(() => {
  const cat = categories.find((c) => c.id === activeCategory.value)
  return cat ? cat.label : '官方技能'
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
    activeCategory.value = 'all'
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
  <div v-if="open" class="official-skills-workbench embedded-page" role="region" aria-labelledby="official-skills-title">
    <TwoPaneLayout
      storage-key="seekclaw-skills-sidebar-width"
      :default-width="260"
      :min-width="200"
      :max-width="480"
      :can-collapse="false"
      aria-label="官方技能导航"
    >
      <template #sidebar>
        <div class="skills-nav">
          <div class="skills-nav-header">
            <button class="page-back-button" type="button" title="返回应用" @click="emit('close')">
              <ArrowLeft :size="16" />
              <span>返回应用</span>
            </button>
          </div>

          <div id="official-skills-title" class="skills-nav-group-title">
            <span>官方技能</span>
            <span class="skills-chip">建设中</span>
          </div>

          <div class="skills-sidebar-search">
            <Search :size="15" />
            <input v-model="query" placeholder="搜索官方技能..." aria-label="搜索官方技能" />
          </div>

          <div class="skills-nav-list">
            <button
              v-for="cat in categories"
              :key="cat.id"
              class="skills-nav-item"
              :class="{ active: activeCategory === cat.id }"
              @click="activeCategory = cat.id"
            >
              <component :is="cat.icon" :size="16" />
              <span>{{ cat.label }}</span>
            </button>
          </div>
        </div>
      </template>

      <div class="skills-main">
        <header class="skills-content-header">
          <div class="skills-content-title">
            <h3>{{ activeCategoryLabel }}</h3>
          </div>

          <button class="icon-button" title="刷新技能目录" :disabled="loading" @click="loadCatalog">
            <RefreshCw :size="16" :class="{ spin: loading }" />
          </button>
        </header>

        <div class="skills-main-scroll">
          <div v-if="catalog.length === 0" class="official-skills-empty">
            <PackageOpen :size="38" />
            <strong>{{ query.trim() ? '没有匹配的官方技能' : '官方技能市场正在建设中' }}</strong>
            <span>
              {{ query.trim() ? '试试其他搜索词。' : '官方技能市场正在快速开发中，更多精选的 Agent 扩展能力即将上线。' }}
            </span>
          </div>

          <div v-else-if="filtered.length === 0" class="official-skills-empty">
            <Search :size="32" />
            <strong>没有匹配的官方技能</strong>
            <span>试试其他搜索词。</span>
          </div>

          <div v-else class="official-skills-items">
            <div v-for="skill in filtered" :key="skill.id" class="official-skill-row">
              <div class="official-skill-icon">
                <PackageOpen :size="18" />
              </div>
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
          官方技能由 SeekClaw 团队维护 · 本地技能请前往「设置 → 技能」管理
        </footer>
      </div>
    </TwoPaneLayout>
  </div>
</template>

<style scoped>
.official-skills-workbench.embedded-page {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: transparent;
  overflow: hidden;
}

.skills-nav {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: 10px 12px 14px;
  overflow-y: auto;
  user-select: none;
  box-sizing: border-box;
}

.skills-nav-header {
  margin-bottom: 8px;
}

.skills-nav .page-back-button {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  height: 34px;
  padding: 0 10px;
  color: var(--text-secondary);
  font-size: 13px;
  font-weight: 500;
  background: transparent;
  border: none;
  border-radius: 8px;
  box-sizing: border-box;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.skills-nav .page-back-button:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 76%, transparent);
}

.skills-nav-group-title {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px 6px;
  color: var(--text-muted);
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.skills-chip {
  padding: 1px 6px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--accent) 15%, transparent);
  color: var(--accent);
  font-size: 10.5px;
  font-weight: 600;
}

.skills-sidebar-search {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 10px;
  margin: 4px 0 10px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
  color: var(--text-secondary);
}

.skills-sidebar-search input {
  flex: 1;
  min-width: 0;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  outline: none;
}

.skills-nav-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
  flex: 1 1 auto;
}

.skills-nav-item {
  display: flex;
  align-items: center;
  width: 100%;
  min-height: 36px;
  gap: 10px;
  padding: 0 10px;
  color: var(--text-secondary);
  font-size: 13.5px;
  font-weight: 450;
  text-align: left;
  background: transparent;
  border: none;
  border-radius: 8px;
  box-sizing: border-box;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.skills-nav-item:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 75%, transparent);
}

.skills-nav-item.active {
  color: var(--text);
  background: var(--surface-raised);
  box-shadow: 0 1px 2px rgb(0 0 0 / 4%);
}

.skills-main {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: var(--surface);
}

.skills-content-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 28px;
  border-bottom: 1px solid var(--border);
}

.skills-content-title h3 {
  margin: 0;
  font-size: 17px;
  font-weight: 650;
  letter-spacing: -0.01em;
}

.skills-main-scroll {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  padding: 24px 28px;
}

.official-skills-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 70px 0;
  color: var(--text-muted);
}

.official-skills-empty strong {
  font-size: 16px;
  color: var(--text);
}

.official-skills-empty span {
  font-size: 13px;
  max-width: 360px;
  text-align: center;
  line-height: 1.5;
}

.official-skills-items {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.official-skill-row {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 14px 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.official-skill-icon {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  color: var(--accent);
  border-radius: 8px;
  flex-shrink: 0;
}

.list-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.list-main strong {
  font-size: 14.5px;
  color: var(--text);
}

.list-main small {
  color: var(--text-muted);
  font-size: 12.5px;
}

.version-text {
  margin-left: 8px;
  font-size: 12px;
  color: var(--text-muted);
}

.inline-badge {
  margin-left: 6px;
  padding: 2px 6px;
  background: var(--surface-hover);
  border-radius: 4px;
  font-size: 11px;
  color: var(--text-secondary);
}

.official-skills-footer {
  padding: 12px 28px;
  font-size: 12px;
  color: var(--text-muted);
  border-top: 1px solid var(--border);
  background: var(--surface);
}
</style>
