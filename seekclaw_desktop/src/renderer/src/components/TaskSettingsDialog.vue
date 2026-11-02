<script setup lang="ts">
import { Archive, FolderOpen, RotateCcw, Save, Trash2, X } from '@lucide/vue'
import { ref, watch } from 'vue'
import type { ProjectItem, ThreadItem } from '../types'
import TaskActivityPanel from './TaskActivityPanel.vue'

const props = defineProps<{
  open: boolean
  thread?: ThreadItem
  project?: ProjectItem
  worktreeBusy?: boolean
  worktreeError?: string
}>()

const emit = defineEmits<{
  close: []
  saveTitle: [title: string]
  archive: []
  restore: []
  delete: []
  worktree: [action: 'create' | 'archive' | 'restore', baseRef: string]
}>()

const title = ref('')
const baseRef = ref('HEAD')

watch(() => [props.open, props.thread?.id, props.thread?.title] as const, () => {
  if (props.open) title.value = props.thread?.title ?? ''
}, { immediate: true })

function save(): void {
  const value = title.value.trim()
  if (!value) return
  emit('saveTitle', value)
}

function showWorkspace(): void {
  const path = props.thread?.executionWorkspace || props.project?.path
  if (path) void window.seekclaw.showItemInFolder(path)
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open && thread" class="modal-backdrop task-settings-backdrop" @mousedown.self="emit('close')">
      <section class="task-settings-dialog" role="dialog" aria-modal="true" aria-labelledby="task-settings-title">
      <header class="task-settings-header">
        <div>
          <h2 id="task-settings-title">任务设置</h2>
        </div>
        <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
      </header>

      <div class="task-settings-body">
        <label class="task-settings-field">
          <span>标题</span>
          <input v-model="title" maxlength="120" @keydown.enter="save" />
        </label>

        <div class="task-settings-field">
          <span>工作目录</span>
          <div class="workspace-path-control">
            <input :value="thread.executionWorkspace || project?.path || '无工作目录（任务）'" readonly />
            <button v-if="project" class="secondary-button" @click="showWorkspace">
              <FolderOpen :size="16" />打开
            </button>
          </div>
          <small>{{ project ? '工作目录由所属项目决定，避免任务在项目之间意外漂移。' : '任务不绑定项目，但可以正常读写文件和运行终端，只是没有固定的项目工作目录。' }}</small>
        </div>
        <div v-if="project" class="task-settings-field">
          <span>Git worktree 隔离</span>
          <template v-if="!thread.worktree">
            <input v-model="baseRef" aria-label="起始 Git 引用" placeholder="HEAD 或分支名" :disabled="worktreeBusy || thread.running" />
            <small>从指定提交创建独立工作目录。项目中的未提交改动不会复制。</small>
            <button class="secondary-button" :disabled="worktreeBusy || thread.running || !baseRef.trim()" @click="emit('worktree', 'create', baseRef.trim())">创建隔离工作目录</button>
          </template>
          <template v-else>
            <small>{{ thread.worktree.status === 'active' ? '正在使用独立工作目录' : '隔离工作目录已归档或不可用' }}</small>
            <button class="secondary-button" :disabled="worktreeBusy || thread.running" @click="emit('worktree', thread.worktree.status === 'active' ? 'archive' : 'restore', baseRef)">
              {{ worktreeBusy ? '处理中…' : thread.worktree.status === 'active' ? '保存快照并归档工作目录' : '恢复工作目录' }}
            </button>
          </template>
          <small v-if="worktreeError" role="alert">{{ worktreeError }}</small>
        </div>
        <TaskActivityPanel v-if="thread.sessionId" :key="thread.sessionId" :session-id="thread.sessionId" :workspace="project?.path" />
      </div>

      <footer class="task-settings-footer">
        <div class="task-danger-actions">
          <button v-if="thread.archived" class="secondary-button" @click="emit('restore')"><RotateCcw :size="16" />恢复</button>
          <button v-else class="secondary-button" @click="emit('archive')"><Archive :size="16" />归档</button>
          <button class="danger-button" @click="emit('delete')"><Trash2 :size="16" />删除</button>
        </div>
        <button class="secondary-button primary-action" :disabled="!title.trim()" @click="save"><Save :size="16" />保存标题</button>
      </footer>
      </section>
    </div>
  </Transition>
</template>
