<script setup lang="ts">
import {
  ArrowLeft,
  Check,
  Download,
  ExternalLink,
  Globe,
  LoaderCircle,
  PackageOpen,
  RefreshCw,
  Search,
  ShieldCheck,
  Sparkles,
  Store,
  Upload,
  Users,
  Wrench,
  X
} from '@lucide/vue'
import MarkdownIt from 'markdown-it'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import TwoPaneLayout from './TwoPaneLayout.vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

interface RemoteSkill {
  id: number
  name: string
  slug: string
  summary: string
  author: string
  version: string
  isOfficial: boolean
  authorUserId: number | null
  authorUsername: string | null
  enabled: boolean
  hasPackage: boolean
  updatedAt: number
  homepage?: string | null
}

interface RemoteSkillDetail extends RemoteSkill {
  readmeMarkdown?: string
}

interface LocalSkillInfo {
  name: string
  description?: string
  version?: string
  enabled: boolean
  directory: string
  scope: 'workspace' | 'global'
}

const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true
})

const catalog = ref<RemoteSkill[]>([])
const localSkills = ref<LocalSkillInfo[]>([])
const loading = ref(false)
const errorMessage = ref('')
const query = ref('')
const activeCategory = ref<'all' | 'official' | 'community'>('all')

const actionLoadingSlug = ref<string | null>(null)
const selectedSkill = ref<RemoteSkillDetail | null>(null)
const loadingDetail = ref(false)

const categories = computed(() => [
  { id: 'all' as const, label: '全部技能', icon: Store, count: catalog.value.length },
  { id: 'official' as const, label: '官方精选', icon: ShieldCheck, count: catalog.value.filter((s) => s.isOfficial).length },
  { id: 'community' as const, label: '社区生态', icon: Users, count: catalog.value.filter((s) => !s.isOfficial).length }
])

const filtered = computed(() => {
  let list = catalog.value

  if (activeCategory.value === 'official') {
    list = list.filter((s) => s.isOfficial)
  } else if (activeCategory.value === 'community') {
    list = list.filter((s) => !s.isOfficial)
  }

  const normalized = query.value.trim().toLowerCase()
  if (!normalized) return list

  return list.filter((skill) =>
    skill.name.toLowerCase().includes(normalized) ||
    skill.slug.toLowerCase().includes(normalized) ||
    skill.summary.toLowerCase().includes(normalized) ||
    skill.author.toLowerCase().includes(normalized)
  )
})

const activeCategoryLabel = computed(() => {
  const cat = categories.value.find((c) => c.id === activeCategory.value)
  return cat ? cat.label : '技能市场'
})

async function requestDaemon<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.seekclaw.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

function getLocalSkill(skill: RemoteSkill): LocalSkillInfo | undefined {
  const slugLower = skill.slug.toLowerCase()
  const nameLower = skill.name.toLowerCase()
  return localSkills.value.find((item) => {
    const itemLower = item.name.toLowerCase()
    return itemLower === slugLower || itemLower === nameLower
  })
}

function isInstalled(skill: RemoteSkill): boolean {
  return !!getLocalSkill(skill)
}

function isSkillEnabled(skill: RemoteSkill): boolean {
  const local = getLocalSkill(skill)
  return local ? local.enabled : false
}

async function loadLocalSkills(): Promise<void> {
  try {
    localSkills.value = await requestDaemon<LocalSkillInfo[]>('skill.list')
  } catch (err) {
    console.error('Failed to load local skills:', err)
  }
}

async function loadCatalog(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    const res = await fetch('https://seekclaw.hoilai.com/api/skills')
    if (!res.ok) {
      throw new Error(`技能市场服务响应异常 (${res.status})`)
    }
    const data = (await res.json()) as RemoteSkill[]
    catalog.value = Array.isArray(data) ? data : []
    await loadLocalSkills()
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : '无法连接到官方技能市场，请检查网络连接。'
  } finally {
    loading.value = false
  }
}

async function installSkill(skill: RemoteSkill): Promise<void> {
  actionLoadingSlug.value = skill.slug
  try {
    const downloadUrl = `https://seekclaw.hoilai.com/api/skills/${encodeURIComponent(skill.slug)}/download`
    const updated = await requestDaemon<LocalSkillInfo[]>('skill.import', {
      path: downloadUrl,
      overwrite: true
    })
    localSkills.value = updated
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    alert(`安装技能失败: ${message}`)
  } finally {
    actionLoadingSlug.value = null
  }
}

const importingLocal = ref(false)

async function importLocalSkills(): Promise<void> {
  const selection = await window.seekclaw.selectSkillFiles()
  if (!selection || selection.paths.length === 0) return
  importingLocal.value = true
  try {
    for (const path of selection.paths) {
      localSkills.value = await requestDaemon<LocalSkillInfo[]>('skill.import', {
        path,
        overwrite: true
      })
    }
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    alert(`导入技能失败: ${message}`)
  } finally {
    importingLocal.value = false
  }
}

async function toggleSkill(skill: RemoteSkill): Promise<void> {
  const local = getLocalSkill(skill)
  if (!local) return
  actionLoadingSlug.value = skill.slug
  try {
    const updated = await requestDaemon<LocalSkillInfo[]>('skill.toggle', {
      name: local.name,
      enabled: !local.enabled
    })
    localSkills.value = updated
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    alert(`切换技能状态失败: ${message}`)
  } finally {
    actionLoadingSlug.value = null
  }
}

async function openDetail(skill: RemoteSkill): Promise<void> {
  selectedSkill.value = { ...skill }
  loadingDetail.value = true
  try {
    const res = await fetch(`https://seekclaw.hoilai.com/api/skills/${encodeURIComponent(skill.slug)}`)
    if (res.ok) {
      const detail = (await res.json()) as RemoteSkillDetail
      if (selectedSkill.value && selectedSkill.value.slug === skill.slug) {
        selectedSkill.value = detail
      }
    }
  } catch (err) {
    console.error('Failed to load skill detail:', err)
  } finally {
    loadingDetail.value = false
  }
}

function closeDetail(): void {
  selectedSkill.value = null
}

const renderedReadme = computed(() => {
  if (!selectedSkill.value) return ''
  const content = selectedSkill.value.readmeMarkdown || selectedSkill.value.summary
  return md.render(content)
})

watch(() => props.open, (open) => {
  if (open) {
    query.value = ''
    activeCategory.value = 'all'
    selectedSkill.value = null
    void loadCatalog()
  }
})

function closeOnEscape(event: KeyboardEvent): void {
  if (!props.open) return
  if (selectedSkill.value) {
    selectedSkill.value = null
    event.stopPropagation()
    return
  }
  if (event.key === 'Escape') emit('close')
}

onMounted(() => document.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => document.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <div v-if="open" class="official-skills-workbench embedded-page" role="region"
    aria-labelledby="official-skills-title">
    <TwoPaneLayout storage-key="seekclaw-skills-sidebar-width" :default-width="260" :min-width="200" :max-width="480"
      :can-collapse="false" aria-label="官方技能导航">
      <template #sidebar>
        <div class="skills-nav">
          <div class="skills-nav-header">
            <button class="page-back-button" type="button" title="返回" @click="emit('close')">
              <ArrowLeft :size="16" />
              <span>返回</span>
            </button>
          </div>

          <div id="official-skills-title" class="skills-nav-group-title">
            <span>技能市场</span>

          </div>

          <div class="skills-sidebar-search">
            <Search :size="15" />
            <input v-model="query" placeholder="搜索技能名称或标识..." aria-label="搜索技能" />
          </div>

          <div class="skills-nav-list">
            <button v-for="cat in categories" :key="cat.id" class="skills-nav-item"
              :class="{ active: activeCategory === cat.id }" @click="activeCategory = cat.id">
              <component :is="cat.icon" :size="16" />
              <span class="nav-item-label">{{ cat.label }}</span>
              <span class="nav-item-count">{{ cat.count }}</span>
            </button>
          </div>
        </div>
      </template>

      <div class="skills-main">
        <header class="skills-content-header">
          <div class="skills-content-title">
            <h3>{{ activeCategoryLabel }}</h3>
            <span class="skills-content-count">共 {{ filtered.length }} 个技能</span>
          </div>

          <div class="skills-header-actions">
            <button class="secondary-action-button import-btn" :disabled="importingLocal" title="从本地导入 .zip 或 .md 技能文件" @click="importLocalSkills">
              <LoaderCircle v-if="importingLocal" :size="13" class="spin" />
              <Upload v-else :size="13" />
              <span>导入本地技能</span>
            </button>
            <button class="icon-button" title="刷新技能列表" :disabled="loading" @click="loadCatalog">
              <RefreshCw :size="16" :class="{ spin: loading }" />
            </button>
          </div>
        </header>

        <div class="skills-main-scroll">
          <div v-if="loading && catalog.length === 0" class="official-skills-loading">
            <LoaderCircle :size="32" class="spin text-accent" />
            <span>正在连接 SeekClaw 技能市场…</span>
          </div>

          <div v-else-if="errorMessage && catalog.length === 0" class="official-skills-empty error">
            <Wrench :size="38" />
            <strong>获取技能市场目录失败</strong>
            <span>{{ errorMessage }}</span>
            <button class="primary-button" type="button" @click="loadCatalog">
              <RefreshCw :size="14" />
              <span>重新加载</span>
            </button>
          </div>

          <div v-else-if="catalog.length === 0" class="official-skills-empty">
            <PackageOpen :size="38" />
            <strong>技能市场暂未上线技能</strong>
            <span>请稍后重试或前往网页端发布。</span>
          </div>

          <div v-else-if="filtered.length === 0" class="official-skills-empty">
            <Search :size="32" />
            <strong>没有找到匹配的技能</strong>
            <span>试试更换关键词或筛选分类。</span>
          </div>

          <div v-else class="official-skills-items">
            <div v-for="skill in filtered" :key="skill.slug" class="official-skill-card"
              :class="{ 'is-installed': isInstalled(skill) }">
              <div class="skill-card-top" @click="openDetail(skill)">
                <div class="official-skill-icon" :class="{ official: skill.isOfficial }">
                  <ShieldCheck v-if="skill.isOfficial" :size="20" />
                  <Sparkles v-else :size="20" />
                </div>
                <div class="skill-header-meta">
                  <div class="skill-title-row">
                    <span class="skill-title">{{ skill.name }}</span>
                    <span v-if="skill.version" class="skill-version">v{{ skill.version }}</span>
                    <span v-if="skill.isOfficial" class="badge-official">官方认证</span>
                    <span v-else class="badge-community">社区贡献</span>
                  </div>
                  <div class="skill-slug-row">
                    <code>{{ skill.slug }}</code>
                    <span class="skill-author">作者: {{ skill.author }}</span>
                  </div>
                </div>
              </div>

              <div class="skill-summary" @click="openDetail(skill)">
                {{ skill.summary }}
              </div>

              <div class="skill-card-footer">
                <button type="button" class="card-link-button" title="查看详细文档" @click="openDetail(skill)">
                  查看详情
                </button>

                <div class="card-action-group">
                  <template v-if="isInstalled(skill)">
                    <div class="installed-tag" title="本地已安装">
                      <Check :size="13" />
                      <span>已安装</span>
                    </div>

                    <button class="switch-control" :class="{ active: isSkillEnabled(skill) }"
                      :disabled="actionLoadingSlug === skill.slug" :title="isSkillEnabled(skill) ? '点击禁用技能' : '点击启用技能'"
                      aria-label="启用/禁用技能" @click="toggleSkill(skill)">
                      <span />
                    </button>

                    <button class="secondary-action-button" :disabled="actionLoadingSlug === skill.slug"
                      title="从市场重新拉取更新该技能" @click="installSkill(skill)">
                      <LoaderCircle v-if="actionLoadingSlug === skill.slug" :size="12" class="spin" />
                      <span>更新</span>
                    </button>
                  </template>

                  <template v-else>
                    <button class="install-button" :disabled="actionLoadingSlug === skill.slug"
                      @click="installSkill(skill)">
                      <LoaderCircle v-if="actionLoadingSlug === skill.slug" :size="14" class="spin" />
                      <Download v-else :size="14" />
                      <span>安装</span>
                    </button>
                  </template>
                </div>
              </div>
            </div>
          </div>
        </div>

        <footer class="official-skills-footer">
          <span>官方技能市场地址：<a href="https://seekclaw.hoilai.com/skills"
              target="_blank">https://seekclaw.hoilai.com</a></span>
          <span>已安装技能可在此页面中统一管理与启禁用</span>
        </footer>
      </div>
    </TwoPaneLayout>

    <!-- 技能详情弹窗 / 抽屉 -->
    <div v-if="selectedSkill" class="skill-detail-overlay" @click.self="closeDetail">
      <div class="skill-detail-modal" role="dialog" aria-modal="true"
        :aria-labelledby="`detail-title-${selectedSkill.slug}`">
        <header class="detail-header">
          <div class="detail-title-area">
            <div class="d-flex align-items-center gap-2">
              <span v-if="selectedSkill.isOfficial" class="badge-official">官方认证</span>
              <span v-else class="badge-community">社区贡献</span>
              <span class="skill-version">v{{ selectedSkill.version }}</span>
            </div>
            <h2 :id="`detail-title-${selectedSkill.slug}`" class="detail-title">{{ selectedSkill.name }}</h2>
            <div class="detail-slug-meta">
              <code>{{ selectedSkill.slug }}</code>
              <span>作者: {{ selectedSkill.author }}</span>
              <a v-if="selectedSkill.homepage" :href="selectedSkill.homepage" target="_blank"
                class="detail-homepage-link" title="打开技能主页">
                <Globe :size="13" />
                <span>主页</span>
                <ExternalLink :size="11" />
              </a>
            </div>
          </div>

          <div class="detail-header-actions">
            <template v-if="isInstalled(selectedSkill)">
              <div class="installed-tag">
                <Check :size="13" />
                <span>已安装</span>
              </div>
              <button class="switch-control" :class="{ active: isSkillEnabled(selectedSkill) }"
                :disabled="actionLoadingSlug === selectedSkill.slug" title="启用/禁用技能"
                @click="toggleSkill(selectedSkill)">
                <span />
              </button>
              <button class="secondary-action-button" :disabled="actionLoadingSlug === selectedSkill.slug"
                title="重新安装 / 覆盖" @click="installSkill(selectedSkill)">
                <LoaderCircle v-if="actionLoadingSlug === selectedSkill.slug" :size="13" class="spin" />
                <span>重新安装</span>
              </button>
            </template>
            <template v-else>
              <button class="install-button" :disabled="actionLoadingSlug === selectedSkill.slug"
                @click="installSkill(selectedSkill)">
                <LoaderCircle v-if="actionLoadingSlug === selectedSkill.slug" :size="14" class="spin" />
                <Download v-else :size="14" />
                <span>一键安装</span>
              </button>
            </template>

            <button class="close-icon-button" title="关闭详情" @click="closeDetail">
              <X :size="18" />
            </button>
          </div>
        </header>

        <div class="detail-body">
          <div class="detail-install-cmd">
            <span class="cmd-prompt">$</span>
            <code>seekclaw skill install {{ selectedSkill.slug }}</code>
          </div>

          <div v-if="loadingDetail" class="detail-loading">
            <LoaderCircle :size="24" class="spin" />
            <span>加载技能文档…</span>
          </div>

          <div class="detail-markdown-content" v-html="renderedReadme" />
        </div>
      </div>
    </div>
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
  background: var(--bg);
  overflow: hidden;
  position: relative;
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
  justify-content: space-between;
  padding: 8px 10px 6px;
  color: var(--text-muted);
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
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
  font-size: 13px;
  font-weight: 450;
  text-align: left;
  background: transparent;
  border: none;
  border-radius: 8px;
  box-sizing: border-box;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.nav-item-label {
  flex: 1;
}

.nav-item-count {
  font-size: 11.5px;
  color: var(--text-muted);
  padding: 0 6px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--surface-hover) 60%, transparent);
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

.skills-nav-item.active .nav-item-count {
  background: color-mix(in srgb, var(--accent) 15%, transparent);
  color: var(--accent);
  font-weight: 600;
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

.skills-content-title {
  display: flex;
  align-items: baseline;
  gap: 12px;
}

.skills-content-title h3 {
  margin: 0;
  font-size: 17px;
  font-weight: 650;
  letter-spacing: -0.01em;
}

.skills-content-count {
  font-size: 12.5px;
  color: var(--text-muted);
}

.skills-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.skills-main-scroll {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  padding: 24px 28px;
}

.official-skills-loading,
.official-skills-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  padding: 80px 0;
  color: var(--text-muted);
  text-align: center;
}

.official-skills-empty strong {
  font-size: 15.5px;
  color: var(--text);
}

.official-skills-empty span {
  font-size: 13px;
  max-width: 360px;
  line-height: 1.5;
}

.official-skills-items {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 16px;
}

.official-skill-card {
  display: flex;
  flex-direction: column;
  padding: 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.02);
  transition: transform 140ms ease, box-shadow 140ms ease, border-color 140ms ease;
}

.official-skill-card:hover {
  border-color: color-mix(in srgb, var(--accent) 40%, var(--border));
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.05);
}

.official-skill-card.is-installed {
  border-left: 3px solid var(--accent);
}

.skill-card-top {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  cursor: pointer;
}

.official-skill-icon {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  background: var(--surface-hover);
  color: var(--text-secondary);
  border-radius: 10px;
  flex-shrink: 0;
}

.official-skill-icon.official {
  background: color-mix(in srgb, #16a34a 12%, transparent);
  color: #16a34a;
}

.skill-header-meta {
  flex: 1;
  min-width: 0;
}

.skill-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.skill-title {
  font-size: 14.5px;
  font-weight: 600;
  color: var(--text);
}

.skill-version {
  font-size: 11px;
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
}

.badge-official {
  padding: 1px 6px;
  font-size: 10.5px;
  font-weight: 600;
  border-radius: 4px;
  background: color-mix(in srgb, #16a34a 14%, transparent);
  color: #16a34a;
}

.badge-community {
  padding: 1px 6px;
  font-size: 10.5px;
  font-weight: 600;
  border-radius: 4px;
  background: var(--surface-hover);
  color: var(--text-secondary);
}

.skill-slug-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 2px;
  font-size: 12px;
}

.skill-slug-row code {
  color: var(--text-muted);
  font-size: 11px;
}

.skill-author {
  color: var(--text-muted);
  font-size: 11.5px;
}

.skill-summary {
  flex: 1;
  margin: 12px 0 16px;
  color: var(--text-secondary);
  font-size: 12.5px;
  line-height: 1.5;
  cursor: pointer;
  display: -webkit-box;
  -webkit-line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.skill-card-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-top: 12px;
  border-top: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
  margin-top: auto;
}

.card-link-button {
  background: transparent;
  border: none;
  padding: 4px 6px;
  font-size: 12px;
  color: var(--text-muted);
  cursor: pointer;
  border-radius: 4px;
  transition: color 120ms ease;
}

.card-link-button:hover {
  color: var(--accent);
}

.card-action-group {
  display: flex;
  align-items: center;
  gap: 8px;
}

.installed-tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11.5px;
  color: #16a34a;
  background: color-mix(in srgb, #16a34a 12%, transparent);
  padding: 2px 8px;
  border-radius: 12px;
  font-weight: 500;
}

.install-button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 12px;
  border: none;
  border-radius: 6px;
  background: var(--accent);
  color: #fff;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: opacity 140ms ease;
}

.install-button:hover {
  opacity: 0.9;
}

.install-button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.secondary-action-button {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--surface);
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
  transition: background-color 140ms ease;
}

.secondary-action-button:hover {
  background: var(--surface-hover);
  color: var(--text);
}

.official-skills-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 28px;
  font-size: 12px;
  color: var(--text-muted);
  border-top: 1px solid var(--border);
  background: var(--surface);
}

.official-skills-footer a {
  color: var(--accent);
  text-decoration: none;
}

.official-skills-footer a:hover {
  text-decoration: underline;
}

/* Detail Modal / Overlay */
.skill-detail-overlay {
  position: absolute;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  backdrop-filter: blur(2px);
  z-index: 100;
  display: flex;
  justify-content: center;
  align-items: center;
  padding: 24px;
  box-sizing: border-box;
}

.skill-detail-modal {
  display: flex;
  flex-direction: column;
  width: 100%;
  max-width: 720px;
  max-height: 85vh;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 14px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.16);
  overflow: hidden;
}

.detail-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 20px 24px;
  border-bottom: 1px solid var(--border);
  background: var(--surface-raised);
}

.detail-title {
  margin: 6px 0 4px;
  font-size: 18px;
  font-weight: 700;
  color: var(--text);
}

.detail-slug-meta {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 12px;
  color: var(--text-muted);
}

.detail-homepage-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--accent);
  text-decoration: none;
}

.detail-homepage-link:hover {
  text-decoration: underline;
}

.detail-header-actions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-shrink: 0;
}

.close-icon-button {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  border-radius: 6px;
  cursor: pointer;
  transition: background-color 140ms ease;
}

.close-icon-button:hover {
  background: var(--surface-hover);
  color: var(--text);
}

.detail-body {
  flex: 1 1 auto;
  overflow-y: auto;
  padding: 24px;
}

.detail-install-cmd {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 14px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 8px;
  margin-bottom: 20px;
  font-family: var(--font-mono, monospace);
  font-size: 12.5px;
}

.cmd-prompt {
  color: var(--accent);
  font-weight: bold;
}

.detail-loading {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 30px 0;
  color: var(--text-muted);
  font-size: 13px;
}

.detail-markdown-content {
  color: var(--text);
  font-size: 13.5px;
  line-height: 1.65;
}

.detail-markdown-content :deep(h1),
.detail-markdown-content :deep(h2),
.detail-markdown-content :deep(h3) {
  margin-top: 1.2em;
  margin-bottom: 0.6em;
  font-weight: 600;
}

.detail-markdown-content :deep(p) {
  margin: 0.6em 0;
}

.detail-markdown-content :deep(pre) {
  background: var(--surface-raised);
  border: 1px solid var(--border);
  padding: 12px;
  border-radius: 8px;
  overflow-x: auto;
}

.detail-markdown-content :deep(code) {
  font-family: var(--font-mono, monospace);
  font-size: 12px;
}

.spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }

  to {
    transform: rotate(360deg);
  }
}
</style>
