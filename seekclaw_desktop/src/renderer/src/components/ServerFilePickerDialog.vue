<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  ArrowUp,
  Check,
  Eye,
  EyeOff,
  File as FileIcon,
  Folder,
  FolderOpen,
  FolderPlus,
  HardDrive,
  Home,
  Image as ImageIcon,
  RefreshCw,
  Search,
  Upload,
  X
} from '@lucide/vue'
import type { ProjectItem } from '../types'
import type { ServerFsEntry, ServerFsRoot } from '@shared/web-server'
import { finishServerFilePicker, serverFilePickerState } from '../server-file-picker'

defineProps<{
  projects?: ProjectItem[]
}>()

const roots = ref<ServerFsRoot[]>([])
const currentPath = ref('')
const parentPath = ref<string | null>(null)
const pathInput = ref('')
const entries = ref<ServerFsEntry[]>([])
const selectedPaths = ref<Set<string>>(new Set())
const filterQuery = ref('')
const showHidden = ref(false)
const loading = ref(false)
const error = ref('')
const creatingFolder = ref(false)
const newFolderName = ref('')
const uploading = ref(false)
const localUploadInput = ref<HTMLInputElement | null>(null)

const active = computed(() => serverFilePickerState.active)

const filteredEntries = computed(() => {
  const q = filterQuery.value.trim().toLowerCase()
  if (!q) return entries.value
  return entries.value.filter((entry) => entry.name.toLowerCase().includes(q))
})

const breadcrumbs = computed(() => {
  const value = currentPath.value.trim()
  if (!value) return []
  const isWindows = /^[A-Za-z]:/.test(value) || value.includes('\\')
  if (isWindows) {
    const clean = value.replace(/\//g, '\\')
    const driveMatch = clean.match(/^([A-Za-z]:\\?)(.*)$/)
    if (!driveMatch) return [{ label: clean, path: clean }]
    const driveRoot = driveMatch[1]!.endsWith('\\') ? driveMatch[1]! : `${driveMatch[1]!}\\`
    const rest = driveMatch[2]!.split('\\').filter(Boolean)
    const items = [{ label: driveRoot.replace(/\\$/, ''), path: driveRoot }]
    let acc = driveRoot
    for (const part of rest) {
      acc = acc.endsWith('\\') ? `${acc}${part}` : `${acc}\\${part}`
      items.push({ label: part, path: acc })
    }
    return items
  }
  const parts = value.split('/').filter(Boolean)
  const items = [{ label: '/', path: '/' }]
  let acc = ''
  for (const part of parts) {
    acc += `/${part}`
    items.push({ label: part, path: acc })
  }
  return items
})

const selectedList = computed(() => [...selectedPaths.value])

const canConfirm = computed(() => {
  if (!active.value) return false
  if (active.value.kind === 'directory') {
    return Boolean(selectedList.value[0] || currentPath.value)
  }
  return selectedList.value.length > 0
})

function formatBytes(bytes: number): string {
  if (!bytes) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function rootIcon(kind: ServerFsRoot['kind']) {
  if (kind === 'project') return FolderOpen
  if (kind === 'drive') return HardDrive
  if (kind === 'home') return Home
  return Folder
}

async function loadDirectory(targetPath?: string): Promise<void> {
  if (!active.value || !window.seekclaw.serverFs) return
  loading.value = true
  error.value = ''
  try {
    const result = await window.seekclaw.serverFs.list({
      path: targetPath ?? (currentPath.value || active.value.initialPath),
      kind: active.value.kind,
      showHidden: showHidden.value
    })
    currentPath.value = result.path
    pathInput.value = result.path
    parentPath.value = result.parentPath
    entries.value = result.entries
    selectedPaths.value = new Set()
  } catch (err) {
    error.value = err instanceof Error ? err.message : '读取服务器目录失败'
  } finally {
    loading.value = false
  }
}

async function initializePicker(): Promise<void> {
  if (!active.value) return
  filterQuery.value = ''
  creatingFolder.value = false
  newFolderName.value = ''
  selectedPaths.value = new Set()
  error.value = ''
  if (window.seekclaw.serverFs) {
    try {
      roots.value = await window.seekclaw.serverFs.roots()
    } catch {
      roots.value = []
    }
    await loadDirectory(active.value.initialPath || roots.value[0]?.path)
  }
}

watch(
  () => active.value?.id,
  (id) => {
    if (id) void initializePicker()
  }
)

function handleEntryClick(entry: ServerFsEntry): void {
  if (!active.value) return
  if (active.value.kind === 'directory') {
    if (!entry.isDirectory) return
    const next = new Set<string>()
    next.add(entry.path)
    selectedPaths.value = next
    return
  }

  if (entry.isDirectory) {
    void loadDirectory(entry.path)
    return
  }

  const next = new Set(selectedPaths.value)
  if (active.value.multiple) {
    if (next.has(entry.path)) next.delete(entry.path)
    else next.add(entry.path)
  } else {
    next.clear()
    next.add(entry.path)
  }
  selectedPaths.value = next
}

function handleEntryDoubleClick(entry: ServerFsEntry): void {
  if (!active.value) return
  if (entry.isDirectory) {
    void loadDirectory(entry.path)
    return
  }
  if (active.value.kind !== 'directory') {
    finishServerFilePicker([entry.path])
  }
}

async function submitNewFolder(): Promise<void> {
  const name = newFolderName.value.trim()
  if (!name || !currentPath.value || !window.seekclaw.serverFs) return
  error.value = ''
  try {
    const created = await window.seekclaw.serverFs.mkdir(currentPath.value, name)
    creatingFolder.value = false
    newFolderName.value = ''
    await loadDirectory(created.path)
  } catch (err) {
    error.value = err instanceof Error ? err.message : '创建文件夹失败'
  }
}

function triggerLocalUpload(): void {
  localUploadInput.value?.click()
}

async function handleLocalUploadChange(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const fileList = input.files
  if (!fileList || !fileList.length || !window.seekclaw.serverFs || !active.value) return
  uploading.value = true
  error.value = ''
  try {
    const uploadedPaths: string[] = []
    for (const file of Array.from(fileList)) {
      const arrayBuf = await file.arrayBuffer()
      const bytes = new Uint8Array(arrayBuf)
      let binary = ''
      const chunkSize = 0x8000
      for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize))
      }
      const saved = await window.seekclaw.serverFs.uploadFile({
        name: file.name,
        dataBase64: btoa(binary),
        targetDir: active.value.kind === 'files' && currentPath.value ? currentPath.value : undefined
      })
      uploadedPaths.push(saved.path)
    }
    if (uploadedPaths.length > 0) {
      finishServerFilePicker(uploadedPaths)
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : '上传文件到服务器失败'
  } finally {
    uploading.value = false
    input.value = ''
  }
}

function confirmSelection(): void {
  if (!active.value) return
  if (active.value.kind === 'directory') {
    const target = selectedList.value[0] || currentPath.value
    if (target) finishServerFilePicker([target])
    return
  }
  if (selectedList.value.length > 0) {
    finishServerFilePicker(selectedList.value)
  }
}

function cancelPicker(): void {
  finishServerFilePicker(null)
}
</script>

<template>
  <Teleport to="body">
    <div v-if="active" class="modal-backdrop server-fs-backdrop" @click.self="cancelPicker">
      <div class="server-fs-dialog" role="dialog" aria-modal="true" :aria-label="active.title">
        <header class="server-fs-header">
          <div class="server-fs-title-group">
            <h3>{{ active.title }}</h3>
            <span class="server-fs-badge">宿主机服务器目录</span>
          </div>
          <button type="button" class="icon-button" aria-label="关闭" @click="cancelPicker">
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
              placeholder="输入服务器路径后按 Enter 跳转..."
            />
          </form>
          <button
            type="button"
            class="server-fs-tool-btn"
            :title="showHidden ? '隐藏点文件' : '显示隐藏文件'"
            @click="showHidden = !showHidden; loadDirectory(currentPath)"
          >
            <EyeOff v-if="showHidden" :size="15" />
            <Eye v-else :size="15" />
          </button>
          <button
            type="button"
            class="server-fs-tool-btn"
            title="新建文件夹"
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
            placeholder="输入新文件夹名称..."
            @keydown.enter.prevent="submitNewFolder"
          />
          <button type="button" class="server-fs-btn primary" @click="submitNewFolder">创建</button>
          <button type="button" class="server-fs-btn" @click="creatingFolder = false">取消</button>
        </div>

        <div class="server-fs-body">
          <aside class="server-fs-roots">
            <button
              v-for="root in roots"
              :key="root.id"
              type="button"
              class="server-fs-root-item"
              :class="{ active: currentPath === root.path }"
              :title="root.path"
              @click="loadDirectory(root.path)"
            >
              <component :is="rootIcon(root.kind)" :size="15" />
              <span>{{ root.label }}</span>
            </button>
          </aside>

          <section class="server-fs-list-pane">
            <div class="server-fs-search-row">
              <Search :size="14" />
              <input v-model="filterQuery" type="text" placeholder="过滤当前目录名称..." />
            </div>

            <div v-if="error" class="server-fs-error">{{ error }}</div>

            <div v-if="loading" class="server-fs-empty">正在读取服务器目录...</div>
            <div v-else-if="filteredEntries.length === 0" class="server-fs-empty">
              {{ active.kind === 'directory' ? '此目录下没有子文件夹（可直接点击底部按钮选择当前目录）' : '此目录下没有匹配的项目' }}
            </div>
            <div v-else class="server-fs-entries">
              <button
                v-for="entry in filteredEntries"
                :key="entry.path"
                type="button"
                class="server-fs-entry"
                :class="{ selected: selectedPaths.has(entry.path), directory: entry.isDirectory }"
                @click="handleEntryClick(entry)"
                @dblclick="handleEntryDoubleClick(entry)"
              >
                <span class="server-fs-entry-icon">
                  <Folder v-if="entry.isDirectory" :size="16" />
                  <ImageIcon v-else-if="entry.isImage" :size="16" />
                  <FileIcon v-else :size="16" />
                </span>
                <span class="server-fs-entry-name">{{ entry.name }}</span>
                <span class="server-fs-entry-meta">
                  {{ entry.isDirectory ? '文件夹' : formatBytes(entry.sizeBytes) }}
                </span>
                <span v-if="selectedPaths.has(entry.path)" class="server-fs-entry-check">
                  <Check :size="14" />
                </span>
              </button>
            </div>
          </section>
        </div>

        <footer class="server-fs-footer">
          <div class="server-fs-footer-left">
            <template v-if="active.kind !== 'directory'">
              <input
                ref="localUploadInput"
                type="file"
                class="sr-only"
                :multiple="active.multiple"
                :accept="active.kind === 'images' ? 'image/*' : active.kind === 'skills' ? '.md,.zip' : undefined"
                @change="handleLocalUploadChange"
              />
              <button type="button" class="server-fs-btn" :disabled="uploading" @click="triggerLocalUpload">
                <Upload :size="14" />
                <span>{{ uploading ? '上传中...' : '从本设备上传' }}</span>
              </button>
            </template>
            <span v-if="active.kind === 'directory'" class="server-fs-summary">
              将选择：{{ selectedList[0] || currentPath || '—' }}
            </span>
            <span v-else class="server-fs-summary">
              已选择 {{ selectedList.length }} 项
            </span>
          </div>

          <div class="server-fs-footer-actions">
            <button type="button" class="server-fs-btn" @click="cancelPicker">取消</button>
            <button type="button" class="server-fs-btn primary" :disabled="!canConfirm" @click="confirmSelection">
              {{ active.kind === 'directory' ? (selectedList[0] ? '选择所选文件夹' : '选择当前目录') : '确定选择' }}
            </button>
          </div>
        </footer>
      </div>
    </div>
  </Teleport>
</template>
