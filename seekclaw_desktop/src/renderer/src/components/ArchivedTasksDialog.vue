<script setup lang="ts">
import {
  Archive,
  ArrowLeft,
  Folder,
  Globe2,
  RotateCcw,
  Search,
  Trash2
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ProjectItem, ThreadItem } from '../types'
import TwoPaneLayout from './TwoPaneLayout.vue'

const props = defineProps<{
  open: boolean
  projects: ProjectItem[]
  threads: ThreadItem[]
}>()

const emit = defineEmits<{
  close: []
  selectThread: [id: string]
  restoreTask: [thread: ThreadItem]
  deleteTask: [thread: ThreadItem]
  deleteAll: []
}>()

type TaskFilter = 'all' | 'global' | 'project'

const query = ref('')
const taskFilter = ref<TaskFilter>('all')
const projectFilter = ref('all')

const archivedThreads = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase()
  return props.threads
    .filter((thread) => thread.archived)
    .filter((thread) => {
      if (taskFilter.value === 'global' && thread.projectId) return false
      if (taskFilter.value === 'project' && !thread.projectId) return false
      if (projectFilter.value !== 'all' && thread.projectId !== projectFilter.value) return false
      return !normalized || thread.title.toLocaleLowerCase().includes(normalized)
    })
    .sort((left, right) => right.updatedAt - left.updatedAt)
})

const archiveGroups = computed(() => {
  const groups = new Map<string, { id: string; name: string; path?: string; threads: ThreadItem[] }>()
  archivedThreads.value.forEach((thread) => {
    const project = props.projects.find((item) => item.id === thread.projectId)
    const id = project?.id ?? 'global'
    const existing = groups.get(id)
    if (existing) {
      existing.threads.push(thread)
      return
    }
    groups.set(id, {
      id,
      name: project?.name ?? '任务 · 未绑定项目',
      path: project?.path,
      threads: [thread]
    })
  })
  return [...groups.values()]
})

const archivedCount = computed(() => props.threads.filter((thread) => thread.archived).length)
const globalArchivedCount = computed(() => props.threads.filter((thread) => thread.archived && !thread.projectId).length)

const projectGroupsWithCount = computed(() => {
  return props.projects.map((project) => {
    const count = props.threads.filter((thread) => thread.archived && thread.projectId === project.id).length
    return {
      id: project.id,
      name: project.name,
      path: project.path,
      count
    }
  }).filter((item) => item.count > 0)
})

const activeCategoryTitle = computed(() => {
  if (taskFilter.value === 'global') return '未绑定项目任务'
  if (taskFilter.value === 'project' && projectFilter.value !== 'all') {
    const proj = props.projects.find((p) => p.id === projectFilter.value)
    return proj ? `${proj.name} 任务` : '项目任务'
  }
  return '全部已归档任务'
})

function formatDate(timestamp: number): string {
  const date = new Date(timestamp)
  const pad = (value: number): string => String(value).padStart(2, '0')
  return `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日，${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function closeOnEscape(event: KeyboardEvent): void {
  if (props.open && event.key === 'Escape') emit('close')
}

watch(() => props.open, (open) => {
  if (open) {
    query.value = ''
    taskFilter.value = 'all'
    projectFilter.value = 'all'
  }
})

onMounted(() => document.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => document.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <div v-if="open" class="archived-tasks-workbench embedded-page" role="region" aria-labelledby="archived-title">
    <TwoPaneLayout
      storage-key="seekclaw-archived-sidebar-width"
      :default-width="260"
      :min-width="200"
      :max-width="480"
      :can-collapse="false"
      aria-label="已归档导航"
    >
      <template #sidebar>
        <div class="archived-nav">
          <div class="archived-nav-header">
            <button class="page-back-button" type="button" title="返回应用" @click="emit('close')">
              <ArrowLeft :size="16" />
              <span>返回应用</span>
            </button>
          </div>

          <div id="archived-title" class="archived-nav-group-title">
            已归档
          </div>

          <div class="archived-sidebar-search">
            <Search :size="15" />
            <input v-model="query" placeholder="搜索已归档..." aria-label="搜索已归档" />
          </div>

          <div class="archived-nav-list">
            <button
              class="archived-nav-item"
              :class="{ active: taskFilter === 'all' && projectFilter === 'all' }"
              @click="taskFilter = 'all'; projectFilter = 'all'"
            >
              <Archive :size="16" />
              <span class="nav-item-title">全部任务</span>
              <span class="nav-item-badge">{{ archivedCount }}</span>
            </button>

            <button
              class="archived-nav-item"
              :class="{ active: taskFilter === 'global' }"
              @click="taskFilter = 'global'; projectFilter = 'all'"
            >
              <Globe2 :size="16" />
              <span class="nav-item-title">未绑定项目</span>
              <span class="nav-item-badge">{{ globalArchivedCount }}</span>
            </button>

            <template v-if="projectGroupsWithCount.length > 0">
              <div class="archived-nav-section-label">
                项目分类
              </div>

              <button
                v-for="item in projectGroupsWithCount"
                :key="item.id"
                class="archived-nav-item"
                :class="{ active: taskFilter === 'project' && projectFilter === item.id }"
                @click="taskFilter = 'project'; projectFilter = item.id"
              >
                <Folder :size="16" />
                <span class="nav-item-title" :title="item.name">{{ item.name }}</span>
                <span class="nav-item-badge">{{ item.count }}</span>
              </button>
            </template>
          </div>

          <div class="archived-nav-footer">
            <button
              class="archived-delete-all-btn"
              :disabled="archivedCount === 0"
              title="全部永久删除"
              @click="emit('deleteAll')"
            >
              <Trash2 :size="15" />
              <span>全部删除</span>
            </button>
          </div>
        </div>
      </template>

      <div class="archived-main">
        <header class="archived-content-header">
          <div class="archived-content-title">
            <h3>{{ activeCategoryTitle }}</h3>
            <span class="archived-count-tag">{{ archivedThreads.length }} 个任务</span>
          </div>
        </header>

        <div class="archived-list-scroll">
          <div v-if="archiveGroups.length === 0" class="archived-empty">
            <Archive :size="32" />
            <strong>{{ archivedCount === 0 ? '还没有已归档任务' : '没有匹配的任务' }}</strong>
            <span>{{ archivedCount === 0 ? '归档后的任务会出现在这里。' : '试试其他搜索词或筛选条件。' }}</span>
          </div>

          <section v-for="group in archiveGroups" :key="group.id" class="archive-group">
            <header class="archive-group-header">
              <div class="archive-group-title">
                <Globe2 v-if="group.id === 'global'" :size="16" />
                <Folder v-else :size="16" />
                <div>
                  <strong>{{ group.name }}</strong>
                  <small v-if="group.path">{{ group.path }}</small>
                </div>
              </div>
              <div class="archive-group-meta">
                <span>{{ group.threads.length }} 个任务</span>
              </div>
            </header>

            <div class="archive-task-card">
              <article v-for="thread in group.threads" :key="thread.id" class="archive-task-row">
                <button class="archive-task-main" @click="emit('selectThread', thread.id)">
                  <strong>{{ thread.title }}</strong>
                  <time>{{ formatDate(thread.updatedAt) }}</time>
                </button>
                <div class="archive-task-actions">
                  <button class="icon-button compact archive-row-delete" title="永久删除"
                    @click.stop="emit('deleteTask', thread)">
                    <Trash2 :size="16" />
                  </button>
                  <button class="archive-restore-button" @click.stop="emit('restoreTask', thread)">
                    <RotateCcw :size="15" />恢复任务
                  </button>
                </div>
              </article>
            </div>
          </section>
        </div>
      </div>
    </TwoPaneLayout>
  </div>
</template>

<style scoped>
.archived-tasks-workbench.embedded-page {
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

.archived-nav {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  padding: 10px 12px 14px;
  overflow-y: auto;
  user-select: none;
  box-sizing: border-box;
}

.archived-nav-header {
  margin-bottom: 8px;
}

.archived-nav .page-back-button {
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

.archived-nav .page-back-button:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 76%, transparent);
}

.archived-nav-group-title {
  padding: 8px 10px 6px;
  color: var(--text-muted);
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.archived-sidebar-search {
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

.archived-sidebar-search input {
  flex: 1;
  min-width: 0;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  outline: none;
}

.archived-nav-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
  flex: 1 1 auto;
  overflow-y: auto;
}

.archived-nav-item {
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

.archived-nav-item:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 75%, transparent);
}

.archived-nav-item.active {
  color: var(--text);
  background: var(--surface-raised);
  box-shadow: 0 1px 2px rgb(0 0 0 / 4%);
}

.nav-item-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.nav-item-badge {
  flex: 0 0 auto;
  padding: 2px 7px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 550;
}

.archived-nav-section-label {
  padding: 12px 10px 4px;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.archived-nav-footer {
  padding-top: 10px;
  margin-top: auto;
  border-top: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
}

.archived-delete-all-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  width: 100%;
  height: 32px;
  padding: 0 10px;
  color: var(--danger);
  font-size: 12.5px;
  font-weight: 500;
  background: transparent;
  border: 1px solid color-mix(in srgb, var(--danger) 30%, transparent);
  border-radius: 7px;
  cursor: pointer;
  transition: background-color 140ms ease;
}

.archived-delete-all-btn:hover:not(:disabled) {
  background: color-mix(in srgb, var(--danger) 10%, transparent);
}

.archived-delete-all-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.archived-main {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: var(--surface);
}

.archived-content-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 28px;
  border-bottom: 1px solid var(--border);
}

.archived-content-title {
  display: flex;
  align-items: center;
  gap: 12px;
}

.archived-content-title h3 {
  margin: 0;
  font-size: 17px;
  font-weight: 650;
  letter-spacing: -0.01em;
}

.archived-count-tag {
  padding: 3px 8px;
  border-radius: 12px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text-secondary);
  font-size: 12px;
}

.archived-list-scroll {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  padding: 20px 28px 40px;
}

.archived-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 60px 0;
  color: var(--text-muted);
}

.archive-group {
  margin-bottom: 24px;
}

.archive-group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}

.archive-group-title {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text);
}

.archive-group-title strong {
  font-size: 14px;
}

.archive-group-title small {
  color: var(--text-muted);
  font-size: 12px;
  margin-left: 6px;
}

.archive-group-meta {
  color: var(--text-muted);
  font-size: 12px;
}

.archive-task-card {
  display: flex;
  flex-direction: column;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  overflow: hidden;
}

.archive-task-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 16px;
  border-bottom: 1px solid var(--border);
  transition: background-color 140ms ease;
}

.archive-task-row:last-child {
  border-bottom: none;
}

.archive-task-row:hover {
  background: color-mix(in srgb, var(--surface-hover) 50%, transparent);
}

.archive-task-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 4px;
  background: transparent;
  border: none;
  padding: 0;
  cursor: pointer;
  text-align: left;
}

.archive-task-main strong {
  color: var(--text);
  font-size: 14px;
  font-weight: 550;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.archive-task-main time {
  color: var(--text-muted);
  font-size: 12px;
}

.archive-task-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 0 0 auto;
}

.archive-restore-button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  color: var(--text);
  font-size: 12px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 6px;
  cursor: pointer;
  transition: background-color 140ms ease, border-color 140ms ease;
}

.archive-restore-button:hover {
  background: var(--surface-hover);
  border-color: var(--border-strong);
}

.archive-row-delete {
  color: var(--text-muted);
}

.archive-row-delete:hover {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 10%, transparent);
}
</style>
