<script setup lang="ts">
import {
  Archive,
  Folder,
  Globe2,
  RotateCcw,
  Search,
  Trash2,
  X
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ProjectItem, ThreadItem } from '../types'
import SelectMenu from './SelectMenu.vue'

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

const taskFilterOptions = [
  { value: 'all', label: '所有任务' },
  { value: 'global', label: '全局任务' },
  { value: 'project', label: '项目任务' }
]

const projectFilterOptions = computed(() => [
  { value: 'all', label: '所有项目' },
  ...props.projects.map((project) => ({ value: project.id, label: project.name }))
])

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
      name: project?.name ?? '全局任务',
      path: project?.path,
      threads: [thread]
    })
  })
  return [...groups.values()]
})

const archivedCount = computed(() => props.threads.filter((thread) => thread.archived).length)

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
  <Transition name="modal-fade">
    <div
      v-if="open"
      class="modal-backdrop archived-backdrop"
      @mousedown.self="emit('close')"
    >
      <section class="archived-tasks-dialog" role="dialog" aria-modal="true" aria-labelledby="archived-title">
        <header class="archived-header">
          <div>
            <span class="archived-eyebrow"><Archive :size="14" /> TASK HISTORY</span>
            <h2 id="archived-title">已归档任务</h2>
            <p>{{ archivedCount }} 个任务保存在这里，可随时恢复继续工作。</p>
          </div>
          <div class="archived-header-actions">
            <button
              class="archive-delete-all"
              :disabled="archivedCount === 0"
              @click="emit('deleteAll')"
            ><Trash2 :size="16" />全部删除</button>
            <button class="icon-button" title="关闭" @click="emit('close')"><X :size="19" /></button>
          </div>
        </header>

        <div class="archived-toolbar">
          <label class="archived-search">
            <Search :size="17" />
            <input v-model="query" autofocus placeholder="搜索已归档任务" aria-label="搜索已归档任务" />
          </label>

          <SelectMenu
            v-model="taskFilter"
            class="archive-select-control"
            :options="taskFilterOptions"
            label="任务范围"
            :menu-min-width="180"
          />

          <div class="archive-select-control project-filter-control">
            <Folder :size="17" />
            <SelectMenu
              v-model="projectFilter"
              class="archive-project-select"
              :options="projectFilterOptions"
              label="项目筛选"
              :menu-min-width="220"
            />
          </div>
        </div>

        <div class="archived-list-scroll">
          <div v-if="archiveGroups.length === 0" class="archived-empty">
            <Archive :size="28" />
            <strong>{{ archivedCount === 0 ? '还没有已归档任务' : '没有匹配的任务' }}</strong>
            <span>{{ archivedCount === 0 ? '归档后的任务会出现在这里。' : '试试其他搜索词或筛选条件。' }}</span>
          </div>

          <section v-for="group in archiveGroups" :key="group.id" class="archive-group">
            <header class="archive-group-header">
              <div class="archive-group-title">
                <Globe2 v-if="group.id === 'global'" :size="18" />
                <Folder v-else :size="18" />
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
                  <button class="icon-button compact archive-row-delete" title="永久删除" @click.stop="emit('deleteTask', thread)">
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
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.archived-backdrop {
  z-index: 120;
  padding: clamp(16px, 4vw, 54px);
  background: rgb(14 17 14 / 38%);
  backdrop-filter: blur(3px);
}

.archived-tasks-dialog {
  display: grid;
  width: min(1080px, 100%);
  height: min(760px, calc(100vh - 44px));
  grid-template-rows: auto auto minmax(0, 1fr);
  min-height: 0;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 18px;
  box-shadow: 0 24px 70px rgb(15 19 16 / 24%);
}

.archived-header,
.archived-header-actions,
.archived-toolbar,
.archive-group-header,
.archive-group-title,
.archive-group-meta,
.archive-task-actions {
  display: flex;
  align-items: center;
}

.archived-header {
  justify-content: space-between;
  gap: 20px;
  padding: 21px 22px 18px;
  border-bottom: 1px solid var(--border);
}

.archived-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--accent);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .1em;
}

.archived-header h2,
.archived-header p {
  margin: 0;
}

.archived-header h2 {
  margin-top: 8px;
  font-size: 25px;
  font-weight: 650;
  letter-spacing: -.02em;
}

.archived-header p {
  margin-top: 7px;
  color: var(--text-secondary);
  font-size: 12px;
}

.archived-header-actions {
  gap: 10px;
}

.archive-delete-all {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  height: 36px;
  padding: 0 13px;
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 12%, transparent);
  border-radius: 999px;
}

.archive-delete-all:hover:not(:disabled) {
  background: color-mix(in srgb, var(--danger) 18%, transparent);
}

.archive-delete-all:disabled {
  cursor: default;
  opacity: .45;
}

.archived-toolbar {
  gap: 10px;
  padding: 18px 22px 14px;
}

.archived-search {
  display: flex;
  min-width: 0;
  height: 40px;
  align-items: center;
  gap: 9px;
  padding: 0 13px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.archived-search {
  flex: 1;
}

.archived-search:focus-within,
.archive-select-control:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.archived-search input {
  min-width: 0;
  flex: 1;
  color: var(--text);
  background: transparent;
  border: 0;
  outline: 0;
}

.archived-search input::placeholder {
  color: var(--text-muted);
}

.archive-select-control {
  position: relative;
  width: 180px;
  flex: 0 0 auto;
}

.archive-select-control .custom-select-trigger {
  width: 100%;
  height: 40px;
  min-height: 40px;
  padding: 0 13px;
  border-radius: 10px;
}

.archive-select-control .custom-select-trigger:hover:not(:disabled),
.archive-select-control.open .custom-select-trigger {
  background: var(--surface);
}

.project-filter-control {
  display: flex;
  height: 40px;
  align-items: center;
  gap: 8px;
  padding: 0 13px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  width: 220px;
}

.project-filter-control > svg {
  flex: 0 0 auto;
}

.project-filter-control .archive-project-select {
  min-width: 0;
  flex: 1;
}

.project-filter-control .custom-select-trigger {
  height: 38px;
  min-height: 38px;
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
}

.project-filter-control .custom-select-trigger:hover:not(:disabled),
.project-filter-control .archive-project-select.open .custom-select-trigger {
  border: 0;
  background: transparent;
}

.archived-list-scroll {
  min-height: 0;
  padding: 4px 22px 22px;
  overflow-y: auto;
}

.archive-group {
  margin-top: 18px;
}

.archive-group-header {
  justify-content: space-between;
  gap: 16px;
  padding: 0 3px 10px;
}

.archive-group-title {
  min-width: 0;
  gap: 10px;
}

.archive-group-title > svg {
  flex: 0 0 auto;
  color: var(--text-secondary);
}

.archive-group-title div {
  display: grid;
  min-width: 0;
  gap: 3px;
}

.archive-group-title strong {
  overflow: hidden;
  font-size: 15px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.archive-group-title small {
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.archive-group-meta {
  flex: 0 0 auto;
  gap: 4px;
  color: var(--text-secondary);
  font-size: 12px;
}

.archive-task-card {
  overflow: hidden;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 15px;
}

.archive-task-row {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 18px;
  min-height: 76px;
  padding: 11px 14px 11px 18px;
}

.archive-task-row + .archive-task-row {
  border-top: 1px solid var(--border);
}

.archive-task-main {
  display: grid;
  min-width: 0;
  flex: 1;
  gap: 7px;
  padding: 3px 0;
  color: var(--text);
  text-align: left;
  background: transparent;
}

.archive-task-main:hover strong {
  color: var(--accent);
}

.archive-task-main strong {
  overflow: hidden;
  font-size: 14px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.archive-task-main time {
  color: var(--text-secondary);
  font-size: 12px;
}

.archive-task-actions {
  flex: 0 0 auto;
  gap: 12px;
}

.archive-row-delete {
  color: var(--text-muted);
}

.archive-row-delete:hover:not(:disabled) {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 12%, transparent);
}

.archive-restore-button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 34px;
  padding: 0 12px;
  color: var(--text);
  background: var(--surface-hover);
  border-radius: 9px;
}

.archive-restore-button:hover {
  color: var(--accent);
  background: var(--accent-soft);
}

.archived-empty {
  display: grid;
  min-height: 300px;
  place-content: center;
  justify-items: center;
  gap: 9px;
  color: var(--text-muted);
  text-align: center;
}

.archived-empty svg {
  margin-bottom: 5px;
  color: var(--accent);
}

.archived-empty strong {
  color: var(--text-secondary);
  font-size: 15px;
}

.archived-empty span {
  font-size: 12px;
}

@media (max-width: 700px) {
  .archived-backdrop {
    padding: 10px;
  }

  .archived-tasks-dialog {
    height: calc(100vh - 20px);
    border-radius: 14px;
  }

  .archived-header,
  .archived-toolbar,
  .archived-list-scroll {
    padding-right: 17px;
    padding-left: 17px;
  }

  .archived-header {
    align-items: flex-start;
    padding-top: 20px;
    padding-bottom: 17px;
  }

  .archived-header p {
    max-width: 230px;
    line-height: 1.5;
  }

  .archive-delete-all {
    width: 36px;
    justify-content: center;
    padding: 0;
    font-size: 0;
  }

  .archived-toolbar {
    flex-wrap: wrap;
  }

  .archived-search {
    width: 100%;
    flex-basis: 100%;
  }

  .archive-select-control,
  .project-filter-control {
    width: calc(50% - 5px);
  }

  .archive-task-row {
    align-items: flex-start;
    flex-direction: column;
    gap: 8px;
    padding: 13px 14px 13px 16px;
  }

  .archive-task-actions {
    width: 100%;
    justify-content: flex-end;
  }
}
</style>
