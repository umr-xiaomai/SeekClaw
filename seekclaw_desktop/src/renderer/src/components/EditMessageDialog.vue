<script setup lang="ts">
import { FileText, RotateCcw, X } from '@lucide/vue'
import type { ChatMessage } from '../types'

const props = defineProps<{
  open: boolean
  message?: ChatMessage | null
  modifiedFiles: string[]
}>()

const emit = defineEmits<{
  close: []
  confirm: [revertFiles: boolean]
}>()

function fileShortName(path: string): string {
  const parts = path.split(/[/\\]/)
  return parts[parts.length - 1] || path
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open" class="modal-backdrop edit-message-backdrop" @mousedown.self="emit('close')">
      <section class="edit-message-dialog" role="dialog" aria-modal="true" aria-labelledby="edit-dialog-title">
        <header class="dialog-header">
          <h2 id="edit-dialog-title">编辑消息</h2>
          <button class="icon-button" title="关闭" aria-label="关闭" @click="emit('close')">
            <X :size="18" />
          </button>
        </header>

        <div class="dialog-body">
          <p class="dialog-desc">
            回退将清除此消息之后的所有对话记录，并将内容填回输入框供您重新编辑。
          </p>

          <div v-if="modifiedFiles.length > 0" class="file-changes-box">
            <div class="file-changes-header">
              <RotateCcw :size="14" />
              <span>检测到此消息之后共修改了 <strong>{{ modifiedFiles.length }}</strong> 个文件：</span>
            </div>
            <ul class="file-changes-list">
              <li v-for="file in modifiedFiles" :key="file" class="file-change-item" :title="file">
                <FileText :size="13" class="file-icon" />
                <span class="file-name">{{ fileShortName(file) }}</span>
                <span class="file-path">{{ file }}</span>
              </li>
            </ul>
          </div>
        </div>

        <footer class="dialog-footer">
          <button class="secondary-button" type="button" @click="emit('close')">取消</button>
          <template v-if="modifiedFiles.length > 0">
            <button class="secondary-button" type="button" @click="emit('confirm', false)">
              仅回退对话
            </button>
            <button class="secondary-button primary-action" type="button" @click="emit('confirm', true)">
              回退并撤销修改
            </button>
          </template>
          <template v-else>
            <button class="secondary-button primary-action" type="button" @click="emit('confirm', false)">
              回退并编辑
            </button>
          </template>
        </footer>
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.edit-message-backdrop {
  z-index: 250;
  display: flex;
  align-items: center;
  justify-content: center;
}

.edit-message-dialog {
  width: min(520px, 92vw);
  max-height: 85vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--shadow);
}

.dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px 12px;
}

.dialog-header h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--text);
}

.dialog-body {
  padding: 0 20px 18px;
  overflow-y: auto;
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.dialog-desc {
  margin: 0;
}

.file-changes-box {
  margin-top: 14px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
  overflow: hidden;
}

.file-changes-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 9px 12px;
  font-size: 12px;
  color: var(--text);
  background: var(--surface-hover);
  border-bottom: 1px solid var(--border);
}

.file-changes-header strong {
  color: var(--accent);
}

.file-changes-list {
  margin: 0;
  padding: 6px 0;
  list-style: none;
  max-height: 160px;
  overflow-y: auto;
}

.file-change-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 12px;
  font-size: 12px;
}

.file-change-item:hover {
  background: var(--surface-hover);
}

.file-icon {
  flex: none;
  color: var(--text-muted);
}

.file-name {
  font-weight: 500;
  color: var(--text);
  flex: none;
}

.file-path {
  color: var(--text-muted);
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}

.dialog-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 20px;
  background: var(--sidebar);
  border-top: 1px solid var(--border);
}
</style>
