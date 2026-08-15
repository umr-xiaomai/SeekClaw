<script setup lang="ts">
import { Archive, FolderOpen, RotateCcw, Save, Trash2, X } from '@lucide/vue'
import { ref, watch } from 'vue'
import type { ProjectItem, ThreadItem } from '../types'

const props = defineProps<{
  open: boolean
  thread?: ThreadItem
  project?: ProjectItem
}>()

const emit = defineEmits<{
  close: []
  saveTitle: [title: string]
  archive: []
  restore: []
  delete: []
}>()

const title = ref('')

watch(() => [props.open, props.thread?.id, props.thread?.title] as const, () => {
  if (props.open) title.value = props.thread?.title ?? ''
}, { immediate: true })

function save(): void {
  const value = title.value.trim()
  if (!value) return
  emit('saveTitle', value)
}

function showWorkspace(): void {
  if (props.project) void window.seekclaw.showItemInFolder(props.project.path)
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open && thread" class="modal-backdrop task-settings-backdrop" @mousedown.self="emit('close')">
      <section class="task-settings-dialog" role="dialog" aria-modal="true" aria-labelledby="task-settings-title">
      <header class="task-settings-header">
        <div>
          <h2 id="task-settings-title">任务设置</h2>
          <p>{{ project ? '任务始终归属于创建它的项目。' : '任务不绑定项目或工作目录。' }}</p>
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
            <input :value="project?.path || '无工作目录（任务）'" readonly />
            <button v-if="project" class="secondary-button" @click="showWorkspace">
              <FolderOpen :size="16" />打开
            </button>
          </div>
          <small>{{ project ? '工作目录由所属项目决定，避免任务在项目之间意外漂移。' : '任务不绑定项目，但可以正常读写文件和运行终端，只是没有固定的项目工作目录。' }}</small>
        </div>
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
