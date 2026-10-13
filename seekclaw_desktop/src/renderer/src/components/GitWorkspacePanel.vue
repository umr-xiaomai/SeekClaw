<script setup lang="ts">
import {
  Braces,
  Check,
  ChevronDown,
  ChevronRight,
  Cloud,
  Copy,
  ExternalLink,
  Eye,
  EyeOff,
  FileCode,
  FileText,
  Filter,
  Folder,
  FolderOpen,
  GitBranch,
  History,
  Layers,
  LoaderCircle,
  MoreHorizontal,
  PanelRight,
  PanelRightClose,
  PanelRightOpen,
  Plus,
  RefreshCw,
  Search,
  TerminalSquare,
  WrapText,
  X
} from "@lucide/vue"
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { GitBranchInfo, GitHistory, GitOverview } from '../../../shared/ipc'
import type { ProjectItem } from '../types'

type PanelTab = 'diff' | 'history'

interface DiffLine {
  type: 'context' | 'addition' | 'deletion'
  content: string
  oldLineNumber?: number
  newLineNumber?: number
  wordDiff?: Array<{ text: string; changed: boolean }>
}

interface DiffHunk {
  header: string
  oldStart: number
  oldCount: number
  newStart: number
  newCount: number
  unmodifiedBefore: number
  lines: DiffLine[]
}

interface FileDiff {
  id: string
  oldPath: string
  newPath: string
  displayPath: string
  fileName: string
  dirPath: string
  status: 'modified' | 'added' | 'deleted' | 'renamed'
  additions: number
  deletions: number
  hunks: DiffHunk[]
  collapsed: boolean
}

interface TreeNode {
  name: string
  fullPath: string
  isFolder: boolean
  expanded?: boolean
  file?: FileDiff
  children?: TreeNode[]
}

const props = withDefaults(defineProps<{
  open: boolean
  project?: ProjectItem
  initialTab: PanelTab
  diffOverride?: { path: string; diff: string } | null
  width?: number
}>(), {
  width: 240
})

const emit = defineEmits<{
  close: []
  openTerminal: []
  resize: [width: number]
}>()

const tab = ref<PanelTab>('diff')
const overview = ref<GitOverview | null>(null)
const history = ref<GitHistory | null>(null)
const loading = ref(false)
const requestId = ref(0)
const resizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0

const MIN_PANEL_WIDTH = 180
const MAX_PANEL_WIDTH = 1400
const DEFAULT_PANEL_WIDTH = 240

const panelRef = ref<HTMLElement | null>(null)
const branchSearchInputRef = ref<HTMLInputElement | null>(null)
const newBranchInputRef = ref<HTMLInputElement | null>(null)

const FILE_TREE_WIDTH_STORAGE_KEY = 'seekclaw-git-file-tree-width-v1'
const DEFAULT_FILE_TREE_WIDTH = 200
const MIN_FILE_TREE_WIDTH = 120
const MAX_FILE_TREE_WIDTH = 550

function loadStoredTreeWidth(): number {
  try {
    const raw = localStorage.getItem(FILE_TREE_WIDTH_STORAGE_KEY)
    if (!raw) return DEFAULT_FILE_TREE_WIDTH
    const val = Number(raw)
    if (Number.isFinite(val) && val >= MIN_FILE_TREE_WIDTH && val <= MAX_FILE_TREE_WIDTH) {
      return val
    }
    return DEFAULT_FILE_TREE_WIDTH
  } catch {
    return DEFAULT_FILE_TREE_WIDTH
  }
}

const fileTreeWidth = ref(loadStoredTreeWidth())
const treeResizing = ref(false)
let treeResizeStartX = 0
let treeResizeStartWidth = 0
let treeRafId: number | null = null
let pendingTreeWidth = fileTreeWidth.value

const GIT_FILE_TREE_STORAGE_KEY = 'seekclaw-git-file-tree-open-v2'
function loadStoredFileTreeOpen(): boolean {
  try {
    const raw = localStorage.getItem(GIT_FILE_TREE_STORAGE_KEY)
    return raw === 'true'
  } catch {
    return false
  }
}
const showFileTree = ref(loadStoredFileTreeOpen())
watch(showFileTree, (open) => {
  localStorage.setItem(GIT_FILE_TREE_STORAGE_KEY, String(open))
})
const fileFilterQuery = ref('')
const activeFileId = ref('')
const wrapLines = ref(false)
const wordDiffEnabled = ref(true)
const menuOpen = ref(false)
const copyToast = ref('')
let copyToastTimer: ReturnType<typeof setTimeout> | undefined

const branches = ref<GitBranchInfo[]>([])
const branchesLoading = ref(false)
const branchMenuOpen = ref(false)
const branchSearchQuery = ref('')
const creatingBranch = ref(false)
const newBranchName = ref('')
const switchingBranch = ref(false)

const localBranches = computed(() => {
  const q = branchSearchQuery.value.trim().toLowerCase()
  return branches.value
    .filter((b) => !b.isRemote)
    .filter((b) => !q || b.name.toLowerCase().includes(q))
})

const remoteBranches = computed(() => {
  const q = branchSearchQuery.value.trim().toLowerCase()
  return branches.value
    .filter((b) => b.isRemote)
    .filter((b) => !q || b.name.toLowerCase().includes(q))
})

const displayedDiff = computed(() => props.diffOverride?.diff ?? overview.value?.diff ?? '')
const panelStyle = computed(() => ({
  '--git-panel-width': `${props.width}px`,
  '--git-tree-width': `${fileTreeWidth.value}px`
}))

function computeWordDiff(
  delText: string,
  addText: string
): { delParts: Array<{ text: string; changed: boolean }>; addParts: Array<{ text: string; changed: boolean }> } {
  let prefixLen = 0
  while (prefixLen < delText.length && prefixLen < addText.length && delText[prefixLen] === addText[prefixLen]) {
    prefixLen++
  }
  let suffixLen = 0
  while (
    suffixLen < (delText.length - prefixLen) &&
    suffixLen < (addText.length - prefixLen) &&
    delText[delText.length - 1 - suffixLen] === addText[addText.length - 1 - suffixLen]
  ) {
    suffixLen++
  }

  const delPrefix = delText.slice(0, prefixLen)
  const delMiddle = delText.slice(prefixLen, delText.length - suffixLen)
  const delSuffix = delText.slice(delText.length - suffixLen)

  const addPrefix = addText.slice(0, prefixLen)
  const addMiddle = addText.slice(prefixLen, addText.length - suffixLen)
  const addSuffix = addText.slice(addText.length - suffixLen)

  return {
    delParts: [
      { text: delPrefix, changed: false },
      { text: delMiddle, changed: true },
      { text: delSuffix, changed: false }
    ].filter((p) => p.text.length > 0),
    addParts: [
      { text: addPrefix, changed: false },
      { text: addMiddle, changed: true },
      { text: addSuffix, changed: false }
    ].filter((p) => p.text.length > 0)
  }
}

function attachWordDiffs(lines: DiffLine[]): void {
  let i = 0
  while (i < lines.length) {
    const curLine = lines[i]
    if (curLine && curLine.type === 'deletion') {
      const delStart = i
      while (i < lines.length && lines[i]?.type === 'deletion') i++
      const addStart = i
      while (i < lines.length && lines[i]?.type === 'addition') i++
      const delCount = addStart - delStart
      const addCount = i - addStart
      const delLine = lines[delStart]
      const addLine = lines[addStart]
      if (delCount === 1 && addCount === 1 && delLine && addLine) {
        const diff = computeWordDiff(delLine.content, addLine.content)
        delLine.wordDiff = diff.delParts
        addLine.wordDiff = diff.addParts
      }
    } else {
      i++
    }
  }
}

function parseGitDiff(rawDiff: string): FileDiff[] {
  if (!rawDiff || !rawDiff.trim()) return []

  const fileChunks = rawDiff.split(/^diff --git /m)
  const files: FileDiff[] = []

  for (const chunk of fileChunks) {
    if (!chunk.trim() || chunk.startsWith('# ')) continue

    const lines = chunk.split(/\r?\n/)
    const firstLine = lines[0] ?? ''
    const matchPaths = firstLine.match(/^a\/(.+?)\s+b\/(.+)$/)
    const oldPath = matchPaths?.[1] ?? ''
    const newPath = matchPaths?.[2] ?? ''
    const displayPath = newPath || oldPath || 'unknown'
    const pathParts = displayPath.split('/')
    const fileName = pathParts.pop() || displayPath
    const dirPath = pathParts.join('/')

    let status: 'modified' | 'added' | 'deleted' | 'renamed' = 'modified'
    let additions = 0
    let deletions = 0
    const hunks: DiffHunk[] = []

    let currentHunk: DiffHunk | null = null
    let oldLine = 0
    let newLine = 0
    let lastOldEnd = 0

    for (let i = 1; i < lines.length; i++) {
      const line = lines[i]
      if (!line) continue
      if (line.startsWith('new file mode')) {
        status = 'added'
      } else if (line.startsWith('deleted file mode')) {
        status = 'deleted'
      } else if (line.startsWith('similarity index') || line.startsWith('rename from')) {
        status = 'renamed'
      } else if (line.startsWith('@@')) {
        const hunkMatch = line.match(/^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$/)
        if (hunkMatch) {
          const oldStart = parseInt(hunkMatch[1] ?? '1', 10)
          const oldCount = hunkMatch[2] !== undefined ? parseInt(hunkMatch[2], 10) : 1
          const newStart = parseInt(hunkMatch[3] ?? '1', 10)
          const newCount = hunkMatch[4] !== undefined ? parseInt(hunkMatch[4], 10) : 1

          const unmodifiedBefore = lastOldEnd > 0 ? Math.max(0, oldStart - lastOldEnd - 1) : Math.max(0, oldStart - 1)
          lastOldEnd = oldStart + oldCount - 1

          oldLine = oldStart
          newLine = newStart

          currentHunk = {
            header: line,
            oldStart,
            oldCount,
            newStart,
            newCount,
            unmodifiedBefore,
            lines: []
          }
          hunks.push(currentHunk)
        }
      } else if (currentHunk) {
        if (line.startsWith('+')) {
          additions++
          currentHunk.lines.push({
            type: 'addition',
            content: line.slice(1),
            newLineNumber: newLine++
          })
        } else if (line.startsWith('-')) {
          deletions++
          currentHunk.lines.push({
            type: 'deletion',
            content: line.slice(1),
            oldLineNumber: oldLine++
          })
        } else if (line.startsWith(' ')) {
          currentHunk.lines.push({
            type: 'context',
            content: line.slice(1),
            oldLineNumber: oldLine++,
            newLineNumber: newLine++
          })
        }
      }
    }

    if (hunks.length > 0 || status === 'deleted' || status === 'added') {
      for (const hunk of hunks) {
        attachWordDiffs(hunk.lines)
      }

      files.push({
        id: displayPath.replace(/[^a-zA-Z0-9_-]/g, '_'),
        oldPath,
        newPath,
        displayPath,
        fileName,
        dirPath,
        status,
        additions,
        deletions,
        hunks,
        collapsed: false
      })
    }
  }

  return files
}

const parsedFiles = computed<FileDiff[]>(() => parseGitDiff(displayedDiff.value))
const totalAdditions = computed(() => parsedFiles.value.reduce((sum, f) => sum + f.additions, 0))
const totalDeletions = computed(() => parsedFiles.value.reduce((sum, f) => sum + f.deletions, 0))

function buildTree(files: FileDiff[]): TreeNode[] {
  const rootNodes: TreeNode[] = []

  for (const file of files) {
    const parts = file.displayPath.split('/')
    let currentLevel = rootNodes
    let accumulatedPath = ''

    for (let i = 0; i < parts.length; i++) {
      const part = parts[i]
      if (part === undefined) continue
      accumulatedPath = accumulatedPath ? `${accumulatedPath}/${part}` : part
      const isFile = i === parts.length - 1

      let node = currentLevel.find((n) => n.name === part)
      if (!node) {
        node = {
          name: part,
          fullPath: accumulatedPath,
          isFolder: !isFile,
          expanded: true,
          file: isFile ? file : undefined,
          children: isFile ? undefined : []
        }
        currentLevel.push(node)
      }
      if (!isFile && node.children) {
        currentLevel = node.children
      }
    }
  }

  function compress(nodes: TreeNode[]): TreeNode[] {
    return nodes.map((node) => {
      if (node.isFolder && node.children) {
        node.children = compress(node.children)
        if (node.children.length === 1 && node.children[0]?.isFolder) {
          const child = node.children[0]
          return {
            name: `${node.name}/${child.name}`,
            fullPath: child.fullPath,
            isFolder: true,
            expanded: true,
            children: child.children
          }
        }
      }
      return node
    })
  }

  return compress(rootNodes)
}

const filteredFiles = computed(() => {
  const q = fileFilterQuery.value.trim().toLowerCase()
  if (!q) return parsedFiles.value
  return parsedFiles.value.filter((f) => f.displayPath.toLowerCase().includes(q))
})

const fileTree = computed(() => buildTree(filteredFiles.value))

// 响应式管理文件折叠状态 (Set 中存放被折叠的文件 ID，默认全部展开)
const collapsedFileIds = ref<Set<string>>(new Set())

function isFileCollapsed(fileId: string): boolean {
  return collapsedFileIds.value.has(fileId)
}

function toggleFileCollapse(fileId: string): void {
  const next = new Set(collapsedFileIds.value)
  if (next.has(fileId)) {
    next.delete(fileId)
  } else {
    next.add(fileId)
  }
  collapsedFileIds.value = next
}

function collapseAll(collapsed: boolean): void {
  if (collapsed) {
    collapsedFileIds.value = new Set(parsedFiles.value.map((f) => f.id))
  } else {
    collapsedFileIds.value = new Set()
  }
}

const isAllCollapsed = computed(() => {
  if (parsedFiles.value.length === 0) return false
  return parsedFiles.value.every((f) => collapsedFileIds.value.has(f.id))
})

function toggleCollapseAll(): void {
  collapseAll(!isAllCollapsed.value)
}

// 响应式管理右侧文件树文件夹折叠状态 (Set 中存放被折叠的文件夹路径，默认全部展开)
const collapsedFolderPaths = ref<Set<string>>(new Set())

function isFolderExpanded(fullPath: string): boolean {
  return !collapsedFolderPaths.value.has(fullPath)
}

function toggleFolderExpand(fullPath: string): void {
  const next = new Set(collapsedFolderPaths.value)
  if (next.has(fullPath)) {
    next.delete(fullPath)
  } else {
    next.add(fullPath)
  }
  collapsedFolderPaths.value = next
}

function scrollToFile(file: FileDiff): void {
  activeFileId.value = file.id
  if (collapsedFileIds.value.has(file.id)) {
    const next = new Set(collapsedFileIds.value)
    next.delete(file.id)
    collapsedFileIds.value = next
  }
  void nextTick(() => {
    const el = document.getElementById(`diff-file-${file.id}`)
    if (el) {
      el.scrollIntoView({ behavior: 'smooth', block: 'start' })
    }
  })
}

function showToast(message: string): void {
  copyToast.value = message
  if (copyToastTimer) clearTimeout(copyToastTimer)
  copyToastTimer = setTimeout(() => {
    copyToast.value = ''
  }, 2400)
}

function copyToClipboard(text: string, message: string): void {
  if (text) {
    void navigator.clipboard.writeText(text)
  }
  showToast(message)
}

function copyGitApply(): void {
  const patch = displayedDiff.value
  const command = `git apply << 'EOF'\n${patch}\nEOF`
  copyToClipboard(command, '已复制 git apply 命令')
  menuOpen.value = false
}

function copyFilePath(path: string): void {
  copyToClipboard(path, '已复制文件路径')
}

function openFileInFolder(relPath: string): void {
  if (!props.project) return
  const fullPath = `${props.project.path}/${relPath}`
  void window.seekclaw.showItemInFolder(fullPath)
}

function toggleMenu(): void {
  menuOpen.value = !menuOpen.value
  if (menuOpen.value) {
    closeBranchMenu()
  }
}

function closeMenu(): void {
  menuOpen.value = false
}

async function loadBranches(): Promise<void> {
  if (!props.project) return
  branchesLoading.value = true
  try {
    branches.value = await window.seekclaw.project.gitBranches(props.project.path)
  } catch (err) {
    console.error('Failed to load branches', err)
  } finally {
    branchesLoading.value = false
  }
}

function toggleBranchMenu(): void {
  branchMenuOpen.value = !branchMenuOpen.value
  if (branchMenuOpen.value) {
    menuOpen.value = false
    branchSearchQuery.value = ''
    creatingBranch.value = false
    void loadBranches()
    void nextTick(() => {
      branchSearchInputRef.value?.focus()
    })
  }
}

function closeBranchMenu(): void {
  branchMenuOpen.value = false
  creatingBranch.value = false
  newBranchName.value = ''
}

function startCreateBranch(): void {
  creatingBranch.value = true
  newBranchName.value = ''
  void nextTick(() => {
    newBranchInputRef.value?.focus()
  })
}

async function handleCheckoutBranch(branchName: string): Promise<void> {
  if (!props.project || switchingBranch.value) return
  switchingBranch.value = true
  try {
    const res = await window.seekclaw.project.checkoutBranch(props.project.path, branchName)
    if (res.success) {
      showToast(`已切换至分支: ${res.branch || branchName}`)
      closeBranchMenu()
      await refresh()
    } else {
      showToast(`切换分支失败: ${res.error || '未知错误'}`)
    }
  } catch (err) {
    showToast(`切换分支异常: ${String(err)}`)
  } finally {
    switchingBranch.value = false
  }
}

async function handleCreateBranch(): Promise<void> {
  const name = newBranchName.value.trim()
  if (!props.project || !name || switchingBranch.value) return
  switchingBranch.value = true
  try {
    const res = await window.seekclaw.project.createBranch(props.project.path, name)
    if (res.success) {
      showToast(`已创建并切换至分支: ${res.branch || name}`)
      newBranchName.value = ''
      creatingBranch.value = false
      closeBranchMenu()
      await refresh()
    } else {
      showToast(`创建分支失败: ${res.error || '未知错误'}`)
    }
  } catch (err) {
    showToast(`创建分支异常: ${String(err)}`)
  } finally {
    switchingBranch.value = false
  }
}

function formatDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat('zh-CN', {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit'
  }).format(date)
}

async function refresh(): Promise<void> {
  if (!props.open || !props.project || props.diffOverride) return
  const currentRequest = ++requestId.value
  loading.value = true
  try {
    if (tab.value === 'diff') {
      const [ov] = await Promise.all([
        window.seekclaw.project.gitOverview(props.project.path),
        loadBranches()
      ])
      if (currentRequest === requestId.value) overview.value = ov
    } else {
      const value = await window.seekclaw.project.gitHistory(props.project.path)
      if (currentRequest === requestId.value) history.value = value
    }
  } finally {
    if (currentRequest === requestId.value) loading.value = false
  }
}

function selectTab(value: PanelTab): void {
  tab.value = value
}

let mainRafId: number | null = null
let pendingMainWidth = props.width

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  if (mainRafId !== null) {
    cancelAnimationFrame(mainRafId)
    mainRafId = null
  }
  emit('resize', pendingMainWidth)
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const viewportWidth = window.innerWidth
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(viewportWidth * 0.9)))
  const nextWidth = Math.round(resizeStartWidth + resizeStartX - event.clientX)
  if (nextWidth <= MIN_PANEL_WIDTH - 40) {
    stopResize()
    emit('close')
    return
  }
  const clampedWidth = Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth))
  pendingMainWidth = clampedWidth
  if (panelRef.value) {
    panelRef.value.style.setProperty('--git-panel-width', `${clampedWidth}px`)
  }
  if (mainRafId === null) {
    mainRafId = requestAnimationFrame(() => {
      mainRafId = null
      emit('resize', pendingMainWidth)
    })
  }
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = props.width
  pendingMainWidth = props.width
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

function resetPanelWidth(): void {
  emit('resize', DEFAULT_PANEL_WIDTH)
}

function handleTreeResize(event: PointerEvent): void {
  if (!treeResizing.value) return
  const delta = treeResizeStartX - event.clientX
  const nextWidth = Math.min(MAX_FILE_TREE_WIDTH, Math.max(MIN_FILE_TREE_WIDTH, Math.round(treeResizeStartWidth + delta)))
  pendingTreeWidth = nextWidth
  if (panelRef.value) {
    panelRef.value.style.setProperty('--git-tree-width', `${nextWidth}px`)
  }
  if (treeRafId === null) {
    treeRafId = requestAnimationFrame(() => {
      treeRafId = null
      fileTreeWidth.value = pendingTreeWidth
    })
  }
}

function stopTreeResize(): void {
  if (!treeResizing.value) return
  treeResizing.value = false
  if (treeRafId !== null) {
    cancelAnimationFrame(treeRafId)
    treeRafId = null
  }
  fileTreeWidth.value = pendingTreeWidth
  localStorage.setItem(FILE_TREE_WIDTH_STORAGE_KEY, String(pendingTreeWidth))
  window.removeEventListener('pointermove', handleTreeResize)
  window.removeEventListener('pointerup', stopTreeResize)
  window.removeEventListener('pointercancel', stopTreeResize)
  document.body.classList.remove('is-resizing-git-panel')
}

function startTreeResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  treeResizing.value = true
  treeResizeStartX = event.clientX
  treeResizeStartWidth = fileTreeWidth.value
  pendingTreeWidth = fileTreeWidth.value
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleTreeResize)
  window.addEventListener('pointerup', stopTreeResize)
  window.addEventListener('pointercancel', stopTreeResize)
}

function resetTreeWidth(): void {
  fileTreeWidth.value = DEFAULT_FILE_TREE_WIDTH
  localStorage.setItem(FILE_TREE_WIDTH_STORAGE_KEY, String(DEFAULT_FILE_TREE_WIDTH))
  if (panelRef.value) {
    panelRef.value.style.setProperty('--git-tree-width', `${DEFAULT_FILE_TREE_WIDTH}px`)
  }
}

watch(() => [props.open, props.project?.id, props.initialTab, props.diffOverride?.path] as const, ([open, projectId], oldValues) => {
  if (!open) {
    requestId.value++
    return
  }
  tab.value = props.initialTab
  if (oldValues && oldValues[1] !== projectId) {
    collapsedFileIds.value = new Set()
    collapsedFolderPaths.value = new Set()
  }
  void refresh()
}, { immediate: true })

watch(tab, () => {
  if (props.open) void refresh()
})

watch(() => props.open, (open) => {
  if (!open) {
    stopResize()
    stopTreeResize()
    closeBranchMenu()
    closeMenu()
  }
})

onBeforeUnmount(() => {
  stopResize()
  stopTreeResize()
})
</script>

<template>
  <Transition name="side-panel">
    <aside
      ref="panelRef"
      v-if="open"
      class="git-panel codex-git-workbench"
      :class="{ 'is-resizing': resizing || treeResizing }"
      :style="panelStyle"
      aria-label="项目 Git 工具"
      @click="closeMenu(); closeBranchMenu()"
    >
      <div
        class="git-panel-resize-handle"
        role="separator"
        aria-label="调整 Git 面板宽度"
        aria-orientation="vertical"
        @pointerdown="startResize"
        @dblclick="resetPanelWidth"
      />

      <header class="git-panel-header">
        <div class="git-panel-title">
          <div class="git-header-title-badge">
            <Braces :size="16" />
            <span>变更</span>
          </div>
          <button
            class="icon-button-mini"
            title="刷新 Git 状态"
            :disabled="loading || !project"
            @click.stop="refresh"
          >
            <RefreshCw :size="15" :class="{ spin: loading }" />
          </button>
        </div>

        <div class="git-panel-header-actions">
          <button
            class="icon-button-mini"
            :class="{ active: showFileTree }"
            :title="showFileTree ? '隐藏文件列表' : '展开文件列表'"
            @click="showFileTree = !showFileTree"
          >
            <component :is="showFileTree ? PanelRightClose : PanelRightOpen" :size="15" />
          </button>
          <button
            class="icon-button-mini"
            title="在工作区目录打开终端"
            :disabled="!project"
            @click="emit('openTerminal')"
          >
            <TerminalSquare :size="15" />
          </button>
          <button class="icon-button-mini" title="关闭面板" @click="emit('close')">
            <X :size="14" />
          </button>
        </div>
      </header>

      <div v-if="!diffOverride && tab === 'diff'" class="git-workbench-toolbar">
        <div class="git-toolbar-row-top">
          <div class="git-branch-stats-chip" title="切换分支" @click.stop="toggleBranchMenu">
            <span class="git-branch-label">
              <span>分支</span>
              <ChevronDown :size="13" />
            </span>
            <span class="git-stats-summary">
              <span v-if="totalAdditions > 0" class="stat-add">+{{ totalAdditions }}</span>
              <span v-if="totalDeletions > 0" class="stat-del">-{{ totalDeletions }}</span>
              <span v-if="totalAdditions === 0 && totalDeletions === 0" class="stat-clean">0</span>
            </span>
          </div>

          <div class="git-toolbar-actions" @click.stop>
            <button
              type="button"
              class="git-tool-btn"
              :class="{ active: menuOpen }"
              title="更多选项"
              @click.stop="toggleMenu"
            >
              <MoreHorizontal :size="15" />
            </button>

            <button
              type="button"
              class="git-tool-btn"
              :title="isAllCollapsed ? '展开全部差异' : '折叠全部差异'"
              @click="toggleCollapseAll"
            >
              <Layers :size="15" />
            </button>

            <button
              type="button"
              class="git-tool-btn"
              title="复制完整 Patch"
              @click="copyToClipboard(displayedDiff, '已复制完整差异 Patch')"
            >
              <Copy :size="15" />
            </button>

            <Transition name="popover-fade">
              <div v-if="menuOpen" class="git-more-menu-popover" role="menu">
                <button type="button" class="git-menu-item" @click="refresh(); closeMenu()">
                  <RefreshCw :size="14" />
                  <span>刷新</span>
                </button>

                <button
                  type="button"
                  class="git-menu-item"
                  @click="wrapLines = !wrapLines; closeMenu()"
                >
                  <WrapText :size="14" />
                  <span>自动换行</span>
                  <Check v-if="wrapLines" :size="14" class="menu-item-check" />
                </button>

                <button
                  type="button"
                  class="git-menu-item"
                  @click="collapseAll(false); closeMenu()"
                >
                  <Layers :size="14" />
                  <span>展开全部差异</span>
                </button>

                <button
                  type="button"
                  class="git-menu-item"
                  @click="collapseAll(true); closeMenu()"
                >
                  <Layers :size="14" />
                  <span>折叠全部差异</span>
                </button>

                <div class="git-menu-divider" />

                <button
                  type="button"
                  class="git-menu-item"
                  @click="wordDiffEnabled = !wordDiffEnabled; closeMenu()"
                >
                  <FileText :size="14" />
                  <span>词级差异</span>
                  <Check v-if="wordDiffEnabled" :size="14" class="menu-item-check" />
                </button>

                <button type="button" class="git-menu-item" @click="copyGitApply">
                  <Copy :size="14" />
                  <span>复制 git apply 命令</span>
                </button>
              </div>
            </Transition>
          </div>
        </div>

        <div class="git-toolbar-row-bottom">
          <div class="git-upstream-tracking-pill" :title="overview?.upstream ? `追踪远端: ${overview.upstream}` : '本地分支'" @click.stop="toggleBranchMenu">
            <span class="tracking-current">{{ overview?.branch || 'master' }}</span>
            <span v-if="overview?.upstream" class="tracking-arrow">→</span>
            <span v-if="overview?.upstream" class="tracking-remote">{{ overview.upstream }}</span>
            <ChevronDown :size="12" class="tracking-chevron" />
          </div>
        </div>

        <Transition name="popover-fade">
          <div v-if="branchMenuOpen" class="git-branch-menu-popover" role="dialog" aria-label="分支切换与管理" @click.stop>
            <div class="git-branch-menu-header">
              <div class="git-branch-search-box">
                <Search :size="13" class="branch-search-icon" />
                <input
                  ref="branchSearchInputRef"
                  v-model="branchSearchQuery"
                  type="text"
                  class="branch-search-input"
                  placeholder="搜索分支..."
                  @keydown.esc="closeBranchMenu"
                />
                <button
                  v-if="branchSearchQuery"
                  type="button"
                  class="branch-clear-btn"
                  title="清空"
                  @click="branchSearchQuery = ''"
                >
                  <X :size="12" />
                </button>
              </div>

              <button
                v-if="!creatingBranch"
                type="button"
                class="git-new-branch-btn"
                title="新建分支"
                @click="startCreateBranch"
              >
                <Plus :size="13" />
                <span>新建分支</span>
              </button>
            </div>

            <!-- Inline create branch form -->
            <div v-if="creatingBranch" class="git-create-branch-row">
              <GitBranch :size="13" class="new-branch-icon" />
              <input
                ref="newBranchInputRef"
                v-model="newBranchName"
                type="text"
                class="new-branch-input"
                placeholder="输入新分支名称并按回车..."
                :disabled="switchingBranch"
                @keydown.enter="handleCreateBranch"
                @keydown.esc="creatingBranch = false"
              />
              <button
                type="button"
                class="mini-action-btn"
                title="确认创建"
                :disabled="!newBranchName.trim() || switchingBranch"
                @click="handleCreateBranch"
              >
                <Check :size="13" />
              </button>
              <button
                type="button"
                class="mini-action-btn"
                title="取消"
                :disabled="switchingBranch"
                @click="creatingBranch = false; newBranchName = ''"
              >
                <X :size="13" />
              </button>
            </div>

            <div v-if="branchesLoading || switchingBranch" class="git-branch-menu-loading">
              <LoaderCircle :size="15" class="spin" />
              <span>{{ switchingBranch ? '正在切换分支…' : '正在加载分支…' }}</span>
            </div>

            <div v-else class="git-branch-menu-list">
              <!-- Local Branches -->
              <div v-if="localBranches.length" class="branch-group">
                <div class="branch-group-title">
                  <span>本地分支</span>
                  <span class="branch-group-count">{{ localBranches.length }}</span>
                </div>
                <button
                  v-for="b in localBranches"
                  :key="b.name"
                  type="button"
                  class="branch-menu-item"
                  :class="{ active: b.current }"
                  :disabled="switchingBranch"
                  @click="handleCheckoutBranch(b.name)"
                >
                  <GitBranch :size="13" class="branch-icon" />
                  <span class="branch-name" :title="b.name">{{ b.name }}</span>
                  <span v-if="b.upstream" class="branch-upstream" :title="`追踪: ${b.upstream}`">
                    → {{ b.upstream }}
                  </span>
                  <Check v-if="b.current" :size="13" class="branch-current-check" />
                </button>
              </div>

              <!-- Remote Branches -->
              <div v-if="remoteBranches.length" class="branch-group">
                <div class="branch-group-title">
                  <span>远程分支</span>
                  <span class="branch-group-count">{{ remoteBranches.length }}</span>
                </div>
                <button
                  v-for="b in remoteBranches"
                  :key="b.name"
                  type="button"
                  class="branch-menu-item"
                  :class="{ active: b.current }"
                  :disabled="switchingBranch"
                  @click="handleCheckoutBranch(b.name)"
                >
                  <Cloud :size="13" class="branch-icon remote-icon" />
                  <span class="branch-name" :title="b.name">{{ b.name }}</span>
                  <Check v-if="b.current" :size="13" class="branch-current-check" />
                </button>
              </div>

              <div v-if="localBranches.length === 0 && remoteBranches.length === 0" class="branch-empty-hint">
                没有找到匹配的分支
              </div>
            </div>
          </div>
        </Transition>
      </div>

      <div v-if="!diffOverride" class="git-panel-tabs" role="tablist">
        <button :class="{ active: tab === 'diff' }" role="tab" @click="selectTab('diff')">
          <Braces :size="15" />
          <span>更改 ({{ parsedFiles.length }})</span>
        </button>
        <button :class="{ active: tab === 'history' }" role="tab" @click="selectTab('history')">
          <History :size="15" />
          <span>提交记录</span>
        </button>
      </div>

      <div class="git-panel-workbench-body">
        <template v-if="diffOverride">
          <div class="git-diff-scrollable full-width">
            <div class="git-repository-summary tool-diff-summary">
              <span><Braces :size="15" />本次修改</span>
              <small>{{ diffOverride.path }}</small>
            </div>
            <div
              v-for="file in parsedFiles"
              :key="file.id"
              class="diff-file-card"
              :class="{ 'is-collapsed': isFileCollapsed(file.id) }"
            >
              <div class="diff-file-card-header" @click="toggleFileCollapse(file.id)">
                <div class="diff-file-card-title">
                  <component :is="isFileCollapsed(file.id) ? ChevronRight : ChevronDown" :size="15" class="diff-arrow" />
                  <svg v-if="file.fileName.endsWith('.vue')" width="14" height="14" viewBox="0 0 24 24" class="file-badge-icon vue-svg">
                    <path d="M12 22L0 3h4.8L12 15.2 19.2 3H24z" fill="#42b883" />
                    <path d="M12 22L4.8 3H9.6L12 7.2 14.4 3h4.8z" fill="#35495e" />
                  </svg>
                  <span v-else-if="file.fileName.endsWith('.css')" class="file-badge-icon code-tag css">css</span>
                  <span v-else-if="file.fileName.endsWith('.ts')" class="file-badge-icon code-tag ts">TS</span>
                  <FileCode v-else :size="14" class="diff-file-type-icon" />
                  <span class="diff-card-filename">{{ file.fileName }}</span>
                  <span class="diff-card-stats">
                    <span v-if="file.additions > 0" class="stat-add">+{{ file.additions }}</span>
                    <span v-if="file.deletions > 0" class="stat-del">-{{ file.deletions }}</span>
                  </span>
                </div>
              </div>
              <div v-show="!isFileCollapsed(file.id)" class="diff-file-card-body">
                <div v-for="(hunk, hIdx) in file.hunks" :key="hIdx" class="diff-hunk-wrap">
                  <div v-if="hunk.unmodifiedBefore > 0" class="diff-unmodified-pill">
                    {{ hunk.unmodifiedBefore }} unmodified lines
                  </div>
                  <div class="diff-hunk-lines">
                    <div
                      v-for="(line, lIdx) in hunk.lines"
                      :key="lIdx"
                      class="diff-code-line"
                      :class="['diff-type-' + line.type, { 'is-wrapped': wrapLines }]"
                    >
                      <div class="diff-line-gutter">
                        <span class="line-num">{{ line.oldLineNumber || line.newLineNumber || '' }}</span>
                        <span class="gutter-color-bar" />
                      </div>
                      <div class="diff-line-text">
                        <template v-if="wordDiffEnabled && line.wordDiff && line.wordDiff.length">
                          <span
                            v-for="(part, pIdx) in line.wordDiff"
                            :key="pIdx"
                            :class="{
                              'word-diff-highlight': part.changed,
                              'word-add': part.changed && line.type === 'addition',
                              'word-del': part.changed && line.type === 'deletion'
                            }"
                          >{{ part.text }}</span>
                        </template>
                        <template v-else>
                          <span>{{ line.content }}</span>
                        </template>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </template>

        <div v-else-if="loading" class="git-panel-state">
          <RefreshCw :size="18" class="spin" />
          <span>正在读取 Git 数据…</span>
        </div>

        <template v-else-if="tab === 'diff'">
          <div v-if="overview?.error" class="git-panel-state is-error">
            {{ overview.error }}
          </div>
          <div v-else-if="!overview?.isRepository" class="git-panel-state">
            当前项目不是 Git 仓库。
          </div>
          <div v-else-if="parsedFiles.length === 0" class="git-panel-state">
            工作区是干净的，没有待查看的更改。
          </div>
          <template v-else>
            <div class="git-diff-scrollable" :class="{ 'with-sidebar': showFileTree }">
              <div
                v-for="file in parsedFiles"
                :id="`diff-file-${file.id}`"
                :key="file.id"
                class="diff-file-card"
                :class="{ active: activeFileId === file.id, 'is-collapsed': isFileCollapsed(file.id) }"
              >
                <div class="diff-file-card-header" @click="toggleFileCollapse(file.id)">
                  <div class="diff-file-card-title">
                    <component :is="isFileCollapsed(file.id) ? ChevronRight : ChevronDown" :size="15" class="diff-arrow" />
                    <svg v-if="file.fileName.endsWith('.vue')" width="14" height="14" viewBox="0 0 24 24" class="file-badge-icon vue-svg">
                      <path d="M12 22L0 3h4.8L12 15.2 19.2 3H24z" fill="#42b883" />
                      <path d="M12 22L4.8 3H9.6L12 7.2 14.4 3h4.8z" fill="#35495e" />
                    </svg>
                    <span v-else-if="file.fileName.endsWith('.css')" class="file-badge-icon code-tag css">css</span>
                    <span v-else-if="file.fileName.endsWith('.ts')" class="file-badge-icon code-tag ts">TS</span>
                    <FileCode v-else :size="14" class="diff-file-type-icon" />
                    <span class="diff-card-filename" :title="file.displayPath">{{ file.fileName }}</span>
                    <span class="diff-card-stats">
                      <span v-if="file.additions > 0" class="stat-add">+{{ file.additions }}</span>
                      <span v-if="file.deletions > 0" class="stat-del">-{{ file.deletions }}</span>
                    </span>
                  </div>

                  <div class="diff-file-card-actions" @click.stop>
                    <button
                      type="button"
                      class="diff-card-action-btn"
                      :title="isFileCollapsed(file.id) ? '展开代码' : '折叠代码'"
                      @click="toggleFileCollapse(file.id)"
                    >
                      <EyeOff v-if="isFileCollapsed(file.id)" :size="14" />
                      <Eye v-else :size="14" />
                    </button>
                    <button
                      type="button"
                      class="diff-card-action-btn"
                      title="在文件管理器中打开"
                      @click="openFileInFolder(file.displayPath)"
                    >
                      <ExternalLink :size="14" />
                    </button>
                    <button
                      type="button"
                      class="diff-card-action-btn"
                      title="复制文件路径"
                      @click="copyFilePath(file.displayPath)"
                    >
                      <Copy :size="14" />
                    </button>
                  </div>
                </div>

                <div v-show="!isFileCollapsed(file.id)" class="diff-file-card-body">
                  <div v-for="(hunk, hIdx) in file.hunks" :key="hIdx" class="diff-hunk-wrap">
                    <div v-if="hunk.unmodifiedBefore > 0" class="diff-unmodified-pill">
                      <span>{{ hunk.unmodifiedBefore }} unmodified lines</span>
                    </div>

                    <div class="diff-hunk-lines">
                      <div
                        v-for="(line, lIdx) in hunk.lines"
                        :key="lIdx"
                        class="diff-code-line"
                        :class="['diff-type-' + line.type, { 'is-wrapped': wrapLines }]"
                      >
                        <div class="diff-line-gutter">
                          <span class="line-num">{{ line.oldLineNumber || line.newLineNumber || '' }}</span>
                          <span class="gutter-color-bar" />
                        </div>
                        <div class="diff-line-text">
                          <template v-if="wordDiffEnabled && line.wordDiff && line.wordDiff.length">
                            <span
                              v-for="(part, pIdx) in line.wordDiff"
                              :key="pIdx"
                              :class="{
                                'word-diff-highlight': part.changed,
                                'word-add': part.changed && line.type === 'addition',
                                'word-del': part.changed && line.type === 'deletion'
                              }"
                            >{{ part.text }}</span>
                          </template>
                          <template v-else>
                            <span>{{ line.content }}</span>
                          </template>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <button
              v-if="!showFileTree && parsedFiles.length > 0"
              type="button"
              class="git-tree-floating-toggle"
              title="展开文件列表"
              @click="showFileTree = true"
            >
              <PanelRightOpen :size="13" />
              <span>文件 ({{ parsedFiles.length }})</span>
            </button>

            <div
              v-if="showFileTree"
              class="git-tree-resize-handle"
              role="separator"
              aria-label="调整文件列表宽度"
              aria-orientation="vertical"
              @pointerdown="startTreeResize"
              @dblclick="resetTreeWidth"
            />

            <aside
              v-if="showFileTree"
              class="git-files-tree-panel"
              :style="{ width: `var(--git-tree-width, ${fileTreeWidth}px)` }"
            >
              <div class="git-file-filter-bar">
                <Search :size="13" class="filter-search-icon" />
                <input
                  v-model="fileFilterQuery"
                  type="text"
                  class="filter-search-input"
                  placeholder="筛选文件..."
                />
                <button
                  type="button"
                  class="git-filter-bar-close-btn"
                  title="隐藏文件列表"
                  @click="showFileTree = false"
                >
                  <PanelRightClose :size="14" />
                </button>
              </div>

              <div class="git-tree-content">
                <template v-for="node in fileTree" :key="node.fullPath">
                  <div v-if="node.isFolder" class="git-tree-folder-group">
                    <div class="git-tree-folder-row" @click="toggleFolderExpand(node.fullPath)">
                      <component :is="isFolderExpanded(node.fullPath) ? ChevronDown : ChevronRight" :size="13" class="tree-chevron" />
                      <FolderOpen v-if="isFolderExpanded(node.fullPath)" :size="14" class="tree-folder-icon" />
                      <Folder v-else :size="14" class="tree-folder-icon" />
                      <span class="tree-folder-name">{{ node.name }}</span>
                    </div>

                    <div v-show="isFolderExpanded(node.fullPath)" class="git-tree-folder-children">
                      <template v-for="subNode in node.children" :key="subNode.fullPath">
                        <div v-if="subNode.isFolder" class="git-tree-subfolder">
                          <div class="git-tree-folder-row nested" @click="toggleFolderExpand(subNode.fullPath)">
                            <component :is="isFolderExpanded(subNode.fullPath) ? ChevronDown : ChevronRight" :size="13" class="tree-chevron" />
                            <span class="tree-folder-name">{{ subNode.name }}</span>
                          </div>
                          <div v-show="isFolderExpanded(subNode.fullPath)" class="git-tree-folder-children">
                            <div
                              v-for="leaf in subNode.children"
                              :key="leaf.fullPath"
                              class="git-tree-file-row"
                              :class="{ active: activeFileId === leaf.file?.id }"
                              @click="leaf.file && scrollToFile(leaf.file)"
                            >
                              <svg v-if="leaf.name.endsWith('.vue')" width="13" height="13" viewBox="0 0 24 24" class="file-badge-icon vue-svg">
                                <path d="M12 22L0 3h4.8L12 15.2 19.2 3H24z" fill="#42b883" />
                                <path d="M12 22L4.8 3H9.6L12 7.2 14.4 3h4.8z" fill="#35495e" />
                              </svg>
                              <span v-else-if="leaf.name.endsWith('.css')" class="file-badge-icon code-tag css">css</span>
                              <span v-else-if="leaf.name.endsWith('.ts')" class="file-badge-icon code-tag ts">TS</span>
                              <span v-else class="tree-file-status" :class="leaf.file?.status">
                                <Check v-if="leaf.file?.status === 'modified'" :size="11" />
                                <span v-else>{{ leaf.file?.status === 'added' ? '+' : '-' }}</span>
                              </span>
                              <span class="tree-file-name" :title="leaf.file?.displayPath">{{ leaf.name }}</span>
                              <span v-if="leaf.file" class="tree-file-stats">
                                <span v-if="leaf.file.additions > 0" class="stat-add">+{{ leaf.file.additions }}</span>
                                <span v-if="leaf.file.deletions > 0" class="stat-del">-{{ leaf.file.deletions }}</span>
                              </span>
                            </div>
                          </div>
                        </div>

                        <div
                          v-else
                          class="git-tree-file-row"
                          :class="{ active: activeFileId === subNode.file?.id }"
                          @click="subNode.file && scrollToFile(subNode.file)"
                        >
                          <svg v-if="subNode.name.endsWith('.vue')" width="13" height="13" viewBox="0 0 24 24" class="file-badge-icon vue-svg">
                            <path d="M12 22L0 3h4.8L12 15.2 19.2 3H24z" fill="#42b883" />
                            <path d="M12 22L4.8 3H9.6L12 7.2 14.4 3h4.8z" fill="#35495e" />
                          </svg>
                          <span v-else-if="subNode.name.endsWith('.css')" class="file-badge-icon code-tag css">css</span>
                          <span v-else-if="subNode.name.endsWith('.ts')" class="file-badge-icon code-tag ts">TS</span>
                          <span v-else class="tree-file-status" :class="subNode.file?.status">
                            <Check v-if="subNode.file?.status === 'modified'" :size="11" />
                            <span v-else>{{ subNode.file?.status === 'added' ? '+' : '-' }}</span>
                          </span>
                          <span class="tree-file-name" :title="subNode.file?.displayPath">{{ subNode.name }}</span>
                          <span v-if="subNode.file" class="tree-file-stats">
                            <span v-if="subNode.file.additions > 0" class="stat-add">+{{ subNode.file.additions }}</span>
                            <span v-if="subNode.file.deletions > 0" class="stat-del">-{{ subNode.file.deletions }}</span>
                          </span>
                        </div>
                      </template>
                    </div>
                  </div>

                  <div
                    v-else
                    class="git-tree-file-row root-file"
                    :class="{ active: activeFileId === node.file?.id }"
                    @click="node.file && scrollToFile(node.file)"
                  >
                    <svg v-if="node.name.endsWith('.vue')" width="13" height="13" viewBox="0 0 24 24" class="file-badge-icon vue-svg">
                      <path d="M12 22L0 3h4.8L12 15.2 19.2 3H24z" fill="#42b883" />
                      <path d="M12 22L4.8 3H9.6L12 7.2 14.4 3h4.8z" fill="#35495e" />
                    </svg>
                    <span v-else-if="node.name.endsWith('.css')" class="file-badge-icon code-tag css">css</span>
                    <span v-else-if="node.name.endsWith('.ts')" class="file-badge-icon code-tag ts">TS</span>
                    <span v-else class="tree-file-status" :class="node.file?.status">
                      <Check v-if="node.file?.status === 'modified'" :size="11" />
                      <span v-else>{{ node.file?.status === 'added' ? '+' : '-' }}</span>
                    </span>
                    <span class="tree-file-name" :title="node.file?.displayPath">{{ node.name }}</span>
                    <span v-if="node.file" class="tree-file-stats">
                      <span v-if="node.file.additions > 0" class="stat-add">+{{ node.file.additions }}</span>
                      <span v-if="node.file.deletions > 0" class="stat-del">-{{ node.file.deletions }}</span>
                    </span>
                  </div>
                </template>
              </div>
            </aside>
          </template>
        </template>

        <template v-else>
          <div v-if="history?.error" class="git-panel-state">{{ history.error }}</div>
          <ol v-else-if="history?.commits.length" class="git-history-list">
            <li v-for="commit in history.commits" :key="commit.hash">
              <span class="git-history-dot" />
              <div>
                <strong>{{ commit.subject }}</strong>
                <p><code>{{ commit.shortHash }}</code> · {{ commit.author }}</p>
              </div>
              <time :datetime="commit.authoredAt">{{ formatDate(commit.authoredAt) }}</time>
            </li>
          </ol>
          <div v-else class="git-panel-state">没有可显示的提交记录。</div>
        </template>
      </div>

      <Transition name="fade">
        <div v-if="copyToast" class="git-action-toast">
          <Check :size="14" />
          <span>{{ copyToast }}</span>
        </div>
      </Transition>
    </aside>
  </Transition>
</template>

