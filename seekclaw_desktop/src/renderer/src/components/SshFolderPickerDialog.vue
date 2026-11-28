<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ArrowUp, Check, Eye, EyeOff, Folder, FolderPlus, Globe, RefreshCw, X } from '@lucide/vue'
import type { SshConnectionInfo, SshFsEntry, SshFsListResult } from '../../../shared/ssh'

const props = defineProps<{
  open: boolean
  connection: SshConnectionInfo | null
  initialPath?: string
}>()

const emit = defineEmits<{
  close: []
  select: [remotePath: string]
}>()

const currentPath = ref('~')
const parentPath = ref<string | null>(null)
const pathInput = ref('~')
const entries = ref<SshFsEntry[]>([])
const selectedPath = ref('')
const showHidden = ref(false)
const loading = ref(false)
const error = ref('')
const creatingFolder = ref(false)
const newFolderName = ref('')

const directoryEntries = computed(() => entries.value.filter((entry) => entry.isDirectory))

const breadcrumbs = computed(() => {
  const value = currentPath.value.trim()
  if (!value || value === '~') return [{ label: '~', path: '~' }]
  const parts = value.split('/').filter(Boolean)
  const items = [{ label: '/', path: '/' }]
  let acc = ''
  for (const part of parts) {
    acc += `/${part}`
    items.push({ label: part, path: acc })
  }
  return items
})

async function loadDirectory(targetPath?: string): Promise<void> {
  if (!props.connection) return
  loading.value = true
  error.value = ''
  try {
    const res = await window.seekclaw.daemon.request(
      'ssh.fs.list',
      {
        connectionId: props.connection.id,
        path: targetPath ?? (currentPath.value || '~'),
        showHidden: showHidden.value
      },
      { timeoutMs: 30_000 }
    )
    const result = JSON.parse(res.data) as SshFsListResult
    currentPath.value = result.path
    pathInput.value = result.path
    parentPath.value = result.parentPath
    entries.value = result.entries
    selectedPath.value = ''
  } catch (err) {
    error.value = err instanceof Error ? err.message : '读取远程服务器目录失败'
  } finally {
    loading.value = false
  }
}

watch(
  () => [props.open, props.connection?.id] as const,
  ([open]) => {
    if (!open) return
    creatingFolder.value = false
    newFolderName.value = ''
    selectedPath.value = ''
    currentPath.value = props.initialPath || '~'
    pathInput.value = currentPath.value
    void loadDirectory(currentPath.value)
  },
  { immediate: true }
)

async function submitNewFolder(): Promise<void> {
  const name = newFolderName.value.trim()
  if (!name || !props.connection) return
  error.value = ''
  try {
    const res = await window.seekclaw.daemon.request(
      'ssh.fs.mkdir',
      {
        connectionId: props.connection.id,
        parentPath: currentPath.value,
        name
      },
      { timeoutMs: 25_000 }
    )
    const created = JSON.parse(res.data) as { path: string }
    creatingFolder.value = false
    newFolderName.value = ''
    await loadDirectory(created.path)
  } catch (err) {
    error.value = err instanceof Error ? err.message : '创建远程文件夹失败'
  }
}

function confirmSelection(): void {
  const chosen = selectedPath.value || currentPath.value
  if (chosen) emit('select', chosen)
}
</script>

<template>
  <Teleport to="body">
    <div v-if="open && connection" class="modal-backdrop server-fs-backdrop" @click.self="emit('close')">
      <div class="server-fs-dialog" role="dialog" aria-modal="true" aria-label="选择远程 SSH 目录">
        <header class="server-fs-header">
          <div class="server-fs-title-group">
            <Globe :size="16" />
            <h3>选择远程服务器文件夹</h3>
            <span class="server-fs-badge">SSH · {{ connection.name || connection.host }}</span>
          </div>
          <button type="button" class="icon-button" aria-label="关闭" @click="emit('close')">
            <X :size="16" />
          </button>
        </header>

        <div class="server-fs-toolbar">
          <button
            type="button"
            class="server-fs-tool-btn"
            :disabled="!parentPath || loading"
            title="上一级目录"
            @click="parentPath && loadDirectory(parentPath)"
          >
            <ArrowUp :size="15" />
          </button>
          <button
            type="button"
            class="server-fs-tool-btn"
            :disabled="loading"
            title="刷新"
            @click="loadDirectory(currentPath)"
          >
            <RefreshCw :size="15" />
          </button>
          <form class="server-fs-path-form" @submit.prevent="loadDirectory(pathInput)">
            <input
              v-model="pathInput"
              type="text"
              class="server-fs-path-input"
              placeholder="输入远程 POSIX 路径后按 Enter..."
            />
          </form>
          <button
            type="button"
            class="server-fs-tool-btn"
            :title="showHidden ? '隐藏点目录' : '显示隐藏目录'"
            @click="showHidden = !showHidden; loadDirectory(currentPath)"
          >
            <EyeOff v-if="showHidden" :size="15" />
            <Eye v-else :size="15" />
          </button>
          <button
            type="button"
            class="server-fs-tool-btn"
            title="在远程新建文件夹"
            @click="creatingFolder = !creatingFolder"
          >
            <FolderPlus :size="15" />
          </button>
        </div>

        <div v-if="breadcrumbs.length" class="server-fs-breadcrumbs">
          <template v-for="(crumb, idx) in breadcrumbs" :key="crumb.path">
            <span v-if="idx > 0" class="server-fs-crumb-sep">/</span>
            <button type="button" class="server-fs-crumb" @click="loadDirectory(crumb.path)">
              {{ crumb.label }}
            </button>
          </template>
        </div>

        <div v-if="creatingFolder" class="server-fs-mkdir-bar">
          <input
            v-model="newFolderName"
            type="text"
            placeholder="输入远程文件夹名称..."
            @keydown.enter.prevent="submitNewFolder"
          />
          <button type="button" class="server-fs-btn primary" @click="submitNewFolder">创建</button>
          <button type="button" class="server-fs-btn" @click="creatingFolder = false">取消</button>
        </div>

        <div class="server-fs-list-pane" style="flex: 1; min-height: 0;">
          <div v-if="error" class="server-fs-error">{{ error }}</div>
          <div v-if="loading" class="server-fs-empty">正在通过 SSH 读取目录...</div>
          <div v-else-if="directoryEntries.length === 0" class="server-fs-empty">
            此目录下没有子文件夹（可直接选择当前目录）
          </div>
          <div v-else class="server-fs-entries">
            <button
              v-for="entry in directoryEntries"
              :key="entry.path"
              type="button"
              class="server-fs-entry directory"
              :class="{ selected: selectedPath === entry.path }"
              @click="selectedPath = entry.path"
              @dblclick="loadDirectory(entry.path)"
            >
              <span class="server-fs-entry-icon"><Folder :size="16" /></span>
              <span class="server-fs-entry-name">{{ entry.name }}</span>
              <span v-if="selectedPath === entry.path" class="server-fs-entry-check"><Check :size="14" /></span>
            </button>
          </div>
        </div>

        <footer class="server-fs-footer">
          <div class="server-fs-footer-left">
            <span class="server-fs-summary">将打开：{{ selectedPath || currentPath }}</span>
          </div>
          <div class="server-fs-footer-actions">
            <button type="button" class="server-fs-btn" @click="emit('close')">取消</button>
            <button type="button" class="server-fs-btn primary" :disabled="loading" @click="confirmSelection">
              {{ selectedPath ? '选择所选文件夹' : '选择当前目录' }}
            </button>
          </div>
        </footer>
      </div>
    </div>
  </Teleport>
</template>
