<script setup lang="ts">
import { ArrowUp, FileText, Image as ImageIcon, LoaderCircle, Paperclip, Plus, Slash, Sparkles, Square, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { FileAttachment, ImageAttachment, ReasoningLevel } from '../types'
import type { WorkspaceFileItem } from '../../../shared/ipc'
import { confirmAction } from '../confirmation'
import { fileBadgeText, fileExtClass, getFileExtension } from '../app-helpers'
import { clearTaskDraft, getTaskDraft, saveTaskDraft, type ComposerDraft } from '../composer-drafts'
import {
  applyMentionReplacement,
  detectComposerTrigger,
  filterSlashCommands,
  type ComposerCommandId,
  type ComposerTriggerMatch,
  type SlashCommandItem
} from '../composer-triggers'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import ApprovalModeMenu from './ApprovalModeMenu.vue'
import ModelReasoningMenu from './ModelReasoningMenu.vue'

const props = defineProps<{
  busy: boolean
  obscured?: boolean
  disabled?: boolean
  disabledPlaceholder?: string
  taskId?: string
  workspacePath?: string
  hasActiveSession?: boolean
  model: string
  models: string[]
  mode: string
  reasoningLevel: ReasoningLevel
  reasoningLevels?: ReasoningLevel[]
  supportsImages: boolean
  networkEnabled?: boolean
  optimizePrompt?: (text: string) => Promise<string>
}>()

const emit = defineEmits<{
  send: [message: string, images: ImageAttachment[], files?: FileAttachment[]]
  stop: []
  changeModel: [model: string]
  changeMode: [mode: string]
  changeReasoningLevel: [level: ReasoningLevel]
  changeNetwork: [enabled: boolean]
  command: [commandId: ComposerCommandId]
}>()

const maxImageCount = 10
const maxImageBytes = 10 * 1024 * 1024
const maxTotalImageBytes = 40 * 1024 * 1024
const supportedImageTypes = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif'])
const value = ref('')
const images = ref<ImageAttachment[]>([])
const attachedFiles = ref<FileAttachment[]>([])
const isDragOver = ref(false)
const selectingFiles = ref(false)
const imageNotice = ref('')
const optimizing = ref(false)
const previewImage = ref<ImageAttachment | null>(null)
const textarea = ref<HTMLTextAreaElement | null>(null)

function imageUrl(image: ImageAttachment): string {
  return `data:${image.mediaType};base64,${image.data}`
}

function resize(): void {
  if (!textarea.value) return
  textarea.value.style.height = '0'
  textarea.value.style.height = `${Math.min(176, Math.max(30, textarea.value.scrollHeight))}px`
}

async function optimizeCurrentPrompt(): Promise<void> {
  const text = value.value.trim()
  if (props.disabled || props.busy || optimizing.value || !text || !props.optimizePrompt) return
  if (!props.model) {
    imageNotice.value = '尚未配置模型，请先在设置中新建 Provider 和模型。'
    return
  }
  optimizing.value = true
  imageNotice.value = '正在使用当前模型优化提示词…'
  try {
    const optimized = (await props.optimizePrompt(text)).trim()
    if (!optimized) {
      imageNotice.value = '模型没有返回优化结果，请重试。'
      return
    }

    const confirmed = await confirmAction({
      title: '确认替换提示词',
      message: `优化后的提示词：\n\n${optimized}\n\n是否替换当前输入框内容？`,
      confirmLabel: '替换'
    })
    if (!confirmed) {
      imageNotice.value = '已取消替换，输入框内容未改变。'
      return
    }

    value.value = optimized
    imageNotice.value = '提示词已优化。'
    void nextTick(() => { resize(); focus() })
  } catch (reason) {
    imageNotice.value = reason instanceof Error ? reason.message : '优化提示词失败，请重试。'
  } finally {
    optimizing.value = false
  }
}

function isImageFile(name: string, mediaType?: string): boolean {
  if (mediaType && mediaType.startsWith('image/')) return true
  const ext = getFileExtension(name).toLowerCase()
  return ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'svg'].includes(ext)
}

function addImages(candidates: Array<Omit<ImageAttachment, 'id'>>): string {
  const warnings: string[] = []
  let totalBytes = images.value.reduce((total, image) => total + image.sizeBytes, 0)
  for (const candidate of candidates) {
    // 联动互斥：如果在 attachedFiles 里存在，立即移除，绝不重复展示
    if (candidate.path) {
      attachedFiles.value = attachedFiles.value.filter(
        (f) => f.path.toLowerCase() !== candidate.path!.toLowerCase()
      )
    } else {
      attachedFiles.value = attachedFiles.value.filter(
        (f) => f.name.toLowerCase() !== candidate.name.toLowerCase()
      )
    }

    const isDup = images.value.some((img) =>
      (candidate.path && img.path && img.path.toLowerCase() === candidate.path.toLowerCase()) ||
      img.name === candidate.name
    )
    if (isDup) continue

    if (images.value.length >= maxImageCount) {
      warnings.push(`每条消息最多添加 ${maxImageCount} 张图片。`)
      break
    }
    if (!supportedImageTypes.has(candidate.mediaType)) {
      warnings.push(`${candidate.name} 的格式暂不支持。`)
      continue
    }
    if (candidate.sizeBytes > maxImageBytes) {
      warnings.push(`${candidate.name} 超过 10 MB，未添加。`)
      continue
    }
    if (totalBytes + candidate.sizeBytes > maxTotalImageBytes) {
      warnings.push(`图片合计不能超过 40 MB，${candidate.name} 未添加。`)
      continue
    }
    images.value.push({ id: crypto.randomUUID(), ...candidate })
    totalBytes += candidate.sizeBytes
  }
  return [...new Set(warnings)].join(' ')
}

function readClipboardFile(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onerror = () => reject(reader.error ?? new Error('无法读取剪贴板图片。'))
    reader.onload = () => {
      const result = typeof reader.result === 'string' ? reader.result : ''
      const comma = result.indexOf(',')
      if (comma < 0) reject(new Error('剪贴板图片格式无效。'))
      else resolve(result.slice(comma + 1))
    }
    reader.readAsDataURL(file)
  })
}

function clipboardImageName(mediaType: string, index: number): string {
  const extension = mediaType === 'image/jpeg' ? 'jpg' : mediaType.split('/')[1] || 'png'
  return `截图-${new Date().toLocaleString('sv-SE').replace(/[\s:]/g, '-')}-${index + 1}.${extension}`
}

function addFileByPath(filePath: string, sizeBytes = 0): boolean {
  const path = filePath.trim()
  if (!path) return false
  const name = path.split(/[/\\]/).pop() || 'file'

  // 联动互斥：如果已在图片预览列表中，绝不重复作为文件展示
  if (images.value.some((img) => img.path?.toLowerCase() === path.toLowerCase() || img.name.toLowerCase() === name.toLowerCase())) {
    return false
  }

  if (attachedFiles.value.some((f) => f.path.toLowerCase() === path.toLowerCase())) return false
  const extension = getFileExtension(name)
  attachedFiles.value.push({
    id: crypto.randomUUID(),
    name,
    path,
    sizeBytes,
    extension
  })
  return true
}

function removeAttachedFile(id: string): void {
  attachedFiles.value = attachedFiles.value.filter((file) => file.id !== id)
}

async function selectFiles(): Promise<void> {
  if (props.disabled || selectingFiles.value) return
  selectingFiles.value = true
  try {
    const paths = await window.seekclaw.selectFiles()
    for (const path of paths) {
      const name = path.split(/[/\\]/).pop() || 'file'
      const isImg = isImageFile(name)
      if (isImg && props.supportsImages) {
        const fileData = await window.seekclaw.readFileBase64?.(path)
        if (fileData) {
          addImages([{
            name,
            mediaType: fileData.mediaType,
            data: fileData.data,
            sizeBytes: fileData.sizeBytes,
            path
          }])
          continue
        }
      }
      addFileByPath(path)
    }
  } finally {
    selectingFiles.value = false
  }
}

async function selectImages(): Promise<void> {
  if (props.disabled || selectingFiles.value) return
  selectingFiles.value = true
  try {
    const selection = await window.seekclaw.selectImages()
    if (selection) {
      if (selection.images && selection.images.length > 0) {
        addImages(selection.images)
      }
      if (selection.warning) {
        imageNotice.value = selection.warning
      }
    }
  } catch (error) {
    imageNotice.value = error instanceof Error ? error.message : '选择图片失败。'
  } finally {
    selectingFiles.value = false
  }
}

const plusMenuOpen = ref(false)
const plusTrigger = ref<HTMLButtonElement | null>(null)
const plusMenu = ref<HTMLElement | null>(null)
const plusMenuStyle = ref<Record<string, string>>({})
const plusPlacedAbove = ref(true)

function positionPlusMenu(): void {
  if (!plusTrigger.value || !plusMenuOpen.value) return
  const rect = plusTrigger.value.getBoundingClientRect()
  const edge = 10
  const gap = 8
  const width = 160
  const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))
  const placeAbove = window.innerHeight - rect.bottom < 170
  plusPlacedAbove.value = placeAbove
  plusMenuStyle.value = placeAbove
    ? { width: `${width}px`, left: `${left}px`, bottom: `${window.innerHeight - rect.top + gap}px` }
    : { width: `${width}px`, left: `${left}px`, top: `${rect.bottom + gap}px` }
}

function togglePlusMenu(): void {
  if (plusMenuOpen.value) {
    plusMenuOpen.value = false
  } else {
    plusMenuOpen.value = true
    void nextTick(() => {
      positionPlusMenu()
      plusMenu.value?.querySelector<HTMLButtonElement>('.plus-popover-item:not(:disabled)')?.focus({ preventScroll: true })
    })
  }
}

function handlePlusMenuKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    plusMenuOpen.value = false
    void nextTick(() => plusTrigger.value?.focus())
    return
  }
  const items = Array.from(plusMenu.value?.querySelectorAll<HTMLButtonElement>('.plus-popover-item:not(:disabled)') ?? [])
  if (items.length === 0) return
  const currentIndex = items.findIndex((el) => el === document.activeElement)
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    items[(currentIndex + 1 + items.length) % items.length]?.focus()
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    items[(currentIndex - 1 + items.length) % items.length]?.focus()
  } else if (event.key === 'Home') {
    event.preventDefault()
    items[0]?.focus()
  } else if (event.key === 'End') {
    event.preventDefault()
    items[items.length - 1]?.focus()
  }
}

function handlePlusSelectFiles(): void {
  plusMenuOpen.value = false
  void selectFiles()
}

function handlePlusSelectImages(): void {
  plusMenuOpen.value = false
  void selectImages()
}

function handlePlusOptimizePrompt(): void {
  plusMenuOpen.value = false
  void optimizeCurrentPrompt()
}

function handlePlusOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node | null
  if (!target) return
  if (plusTrigger.value?.contains(target)) return
  if (plusMenu.value?.contains(target)) return
  plusMenuOpen.value = false
}

function addPlusListeners(): void {
  document.addEventListener('mousedown', handlePlusOutsidePointer, true)
  window.addEventListener('resize', positionPlusMenu)
  window.addEventListener('scroll', positionPlusMenu, true)
}

function removePlusListeners(): void {
  document.removeEventListener('mousedown', handlePlusOutsidePointer, true)
  window.removeEventListener('resize', positionPlusMenu)
  window.removeEventListener('scroll', positionPlusMenu, true)
}

watch(plusMenuOpen, (isOpen) => isOpen ? addPlusListeners() : removePlusListeners())
onBeforeUnmount(removePlusListeners)

async function handlePaste(event: ClipboardEvent): Promise<void> {
  const clipboardFiles = Array.from(event.clipboardData?.files ?? [])
  const clipboardItems = Array.from(event.clipboardData?.items ?? [])

  // 1. 优先提取剪贴板图片（截图或复制的图片对象）
  const imageItemFiles = clipboardItems
    .filter((item) => item.kind === 'file' && item.type.startsWith('image/'))
    .map((item) => item.getAsFile())
    .filter((file): file is File => file !== null)

  let handledImage = false

  if (imageItemFiles.length > 0 && props.supportsImages) {
    try {
      const candidates = await Promise.all(imageItemFiles.map(async (file, index) => {
        const nativePath = window.seekclaw?.getPathForFile?.(file) || (file as unknown as { path?: string }).path || ''
        return {
          name: file.name && file.name !== 'image.png' ? file.name : clipboardImageName(file.type, index),
          mediaType: file.type,
          data: await readClipboardFile(file),
          sizeBytes: file.size,
          path: nativePath || undefined
        }
      }))
      imageNotice.value = addImages(candidates)
      handledImage = true
    } catch (error) {
      imageNotice.value = error instanceof Error ? error.message : '无法读取剪贴板图片。'
    }
  }

  // 2. 提取剪贴板中的文件列表（如在文件管理器中复制的文件）
  let pastedFileCount = 0
  for (const file of clipboardFiles) {
    const isImg = isImageFile(file.name, file.type)

    // 如果此图片在步骤 1 中已经作为图片处理过，绝不重复作为文件添加
    if (isImg && props.supportsImages && handledImage) {
      continue
    }

    const nativePath = window.seekclaw?.getPathForFile?.(file) || (file as unknown as { path?: string }).path || ''
    if (nativePath) {
      if (isImg && props.supportsImages) {
        try {
          const data = await readClipboardFile(file)
          addImages([{
            name: file.name,
            mediaType: file.type || 'image/png',
            data,
            sizeBytes: file.size,
            path: nativePath
          }])
          handledImage = true
        } catch {
          if (addFileByPath(nativePath, file.size)) pastedFileCount++
        }
      } else {
        if (addFileByPath(nativePath, file.size)) pastedFileCount++
      }
    }
  }

  if (handledImage || pastedFileCount > 0) {
    event.preventDefault()
    const notices: string[] = []
    if (handledImage && imageItemFiles.length > 0) notices.push(`已粘贴 ${imageItemFiles.length} 张图片`)
    if (pastedFileCount > 0) notices.push(`已附加 ${pastedFileCount} 个文件`)
    if (notices.length > 0) showDropNotice(notices.join('，'))
  }
}

const shellRef = ref<HTMLElement | null>(null)
const dropNotice = ref('')
let dropNoticeTimer: ReturnType<typeof setTimeout> | undefined

function showDropNotice(text: string): void {
  dropNotice.value = text
  if (dropNoticeTimer) clearTimeout(dropNoticeTimer)
  dropNoticeTimer = setTimeout(() => { dropNotice.value = '' }, 3000)
}

onBeforeUnmount(() => {
  if (dropNoticeTimer) clearTimeout(dropNoticeTimer)
})

function handleDragEnter(event: DragEvent): void {
  if (props.disabled) return
  isDragOver.value = true
}

function handleDragOver(event: DragEvent): void {
  if (props.disabled) return
  event.preventDefault()
  isDragOver.value = true
}

function handleDragLeave(event: DragEvent): void {
  const related = event.relatedTarget as Node | null
  if (!related || !(event.currentTarget as Node)?.contains(related)) {
    isDragOver.value = false
  }
}

async function handleDrop(event: DragEvent): Promise<void> {
  event.preventDefault()
  isDragOver.value = false
  if (props.disabled) return
  const droppedFiles = Array.from(event.dataTransfer?.files ?? [])
  if (droppedFiles.length === 0) return

  let addedFileCount = 0
  let addedImageCount = 0

  for (const file of droppedFiles) {
    const nativePath = window.seekclaw?.getPathForFile?.(file) || (file as unknown as { path?: string }).path || ''
    const isImg = isImageFile(file.name, file.type)

    if (isImg && props.supportsImages) {
      // 图片格式且当前模型支持视觉：只作为图片卡片添加，绝不添加到文件附件列表
      try {
        const data = await readClipboardFile(file)
        const mediaType = file.type || (file.name.toLowerCase().endsWith('.jpg') || file.name.toLowerCase().endsWith('.jpeg') ? 'image/jpeg' : 'image/png')
        const warning = addImages([{
          name: file.name,
          mediaType,
          data,
          sizeBytes: file.size,
          path: nativePath || undefined
        }])
        if (warning) imageNotice.value = warning
        else addedImageCount++
      } catch {
        // 读取图片内容失败时回退为普通文件
        if (nativePath && addFileByPath(nativePath, file.size)) addedFileCount++
      }
    } else {
      // 非图片文件，或模型不支持图片理解时作为普通文件附件
      if (nativePath) {
        if (addFileByPath(nativePath, file.size)) addedFileCount++
      }
    }
  }

  const notices: string[] = []
  if (addedImageCount > 0) notices.push(`已添加 ${addedImageCount} 张图片`)
  if (addedFileCount > 0) notices.push(`已附加 ${addedFileCount} 个文件`)
  if (notices.length > 0) {
    showDropNotice(notices.join('，'))
  }
}

function removeImage(id: string): void {
  images.value = images.value.filter((image) => image.id !== id)
  imageNotice.value = ''
}

export type { ComposerDraft }

function submit(): void {
  const message = value.value.trim()
  if ((!message && images.value.length === 0 && attachedFiles.value.length === 0) || props.disabled) return
  if (images.value.length > 0 && !props.supportsImages) {
    imageNotice.value = '当前模型不支持图片理解，请切换支持视觉的模型。'
    return
  }
  const outgoingImages = images.value.map((image) => ({
    id: image.id,
    name: image.name,
    mediaType: image.mediaType,
    data: image.data,
    sizeBytes: image.sizeBytes,
    path: image.path
  }))
  const outgoingFiles = attachedFiles.value.map((file) => ({ ...file }))
  emit('send', message, outgoingImages, outgoingFiles)
  triggerMatch.value = null
  triggerDismissed.value = false
  if (props.taskId) clearTaskDraft(props.taskId)
  value.value = ''
  images.value = []
  attachedFiles.value = []
  imageNotice.value = ''
  void nextTick(resize)
}

// ---- @ mentions and / slash command palette ----
const triggerMatch = ref<ComposerTriggerMatch | null>(null)
const triggerDismissed = ref(false)
const mentionFiles = ref<WorkspaceFileItem[]>([])
const mentionLoading = ref(false)
const triggerHighlighted = ref(0)
const triggerMenu = ref<HTMLElement | null>(null)
const triggerStyle = ref<Record<string, string>>({})
let mentionSearchSeq = 0
let mentionDebounceTimer: ReturnType<typeof setTimeout> | undefined

const filteredCommands = computed<SlashCommandItem[]>(() => {
  if (triggerMatch.value?.kind !== 'command') return []
  return filterSlashCommands(triggerMatch.value.query, {
    hasProject: Boolean(props.workspacePath),
    hasActiveSession: Boolean(props.hasActiveSession)
  })
})

const triggerItemsCount = computed(() => {
  if (!triggerMatch.value || triggerDismissed.value) return 0
  return triggerMatch.value.kind === 'command' ? filteredCommands.value.length : mentionFiles.value.length
})

const showTriggerPopover = computed(() => {
  if (props.disabled || !triggerMatch.value || triggerDismissed.value) return false
  return true
})

function positionTriggerPopover(): void {
  if (!shellRef.value || !showTriggerPopover.value) return
  const rect = shellRef.value.getBoundingClientRect()
  const edge = 12
  const gap = 8
  const width = Math.min(520, Math.max(280, rect.width - 16), window.innerWidth - edge * 2)
  const left = Math.max(edge, Math.min(rect.left + 8, window.innerWidth - width - edge))
  triggerStyle.value = {
    width: `${width}px`,
    left: `${left}px`,
    bottom: `${window.innerHeight - rect.top + gap}px`
  }
}

async function fetchMentionFiles(query: string): Promise<void> {
  const workspace = props.workspacePath
  const seq = ++mentionSearchSeq
  if (!workspace || !window.seekclaw?.project?.listFiles) {
    mentionFiles.value = []
    mentionLoading.value = false
    return
  }
  mentionLoading.value = true
  try {
    const items = await window.seekclaw.project.listFiles(workspace, query)
    if (seq !== mentionSearchSeq) return
    mentionFiles.value = items
    triggerHighlighted.value = 0
  } catch {
    if (seq !== mentionSearchSeq) return
    mentionFiles.value = []
  } finally {
    if (seq === mentionSearchSeq) mentionLoading.value = false
  }
}

function evaluateTriggers(resetDismiss = false): void {
  if (resetDismiss) triggerDismissed.value = false
  if (props.disabled) {
    triggerMatch.value = null
    return
  }
  const cursor = textarea.value?.selectionStart ?? value.value.length
  const next = detectComposerTrigger(value.value, cursor)
  const prev = triggerMatch.value
  triggerMatch.value = next
  if (!next) {
    triggerDismissed.value = false
    mentionFiles.value = []
    if (mentionDebounceTimer) clearTimeout(mentionDebounceTimer)
    return
  }
  if (!prev || prev.kind !== next.kind || prev.query !== next.query) {
    triggerHighlighted.value = 0
  }
  if (next.kind === 'mention') {
    if (mentionDebounceTimer) clearTimeout(mentionDebounceTimer)
    mentionDebounceTimer = setTimeout(() => void fetchMentionFiles(next.query), 70)
  }
  void nextTick(positionTriggerPopover)
}

function selectMentionFile(item: WorkspaceFileItem): void {
  if (!triggerMatch.value || triggerMatch.value.kind !== 'mention') return
  const applied = applyMentionReplacement(value.value, triggerMatch.value, item.relativePath)
  value.value = applied.nextText
  addFileByPath(item.path, item.sizeBytes)
  triggerMatch.value = null
  triggerDismissed.value = false
  void nextTick(() => {
    resize()
    if (textarea.value) {
      textarea.value.focus()
      textarea.value.setSelectionRange(applied.nextCursor, applied.nextCursor)
    }
  })
}

function selectSlashCommand(item: SlashCommandItem): void {
  triggerMatch.value = null
  triggerDismissed.value = false
  if (item.id === 'clear') {
    value.value = ''
    images.value = []
    attachedFiles.value = []
    imageNotice.value = ''
    triggerMatch.value = null
  triggerDismissed.value = false
  if (props.taskId) clearTaskDraft(props.taskId)
    void nextTick(() => { resize(); focus() })
    return
  }
  if (item.id === 'optimize') {
    value.value = value.value.replace(/^\s*\/\S*\s*/, '').trim()
    void nextTick(() => {
      resize()
      focus()
      void optimizeCurrentPrompt()
    })
    return
  }
  value.value = ''
  triggerMatch.value = null
  triggerDismissed.value = false
  if (props.taskId) clearTaskDraft(props.taskId)
  void nextTick(resize)
  emit('command', item.id)
}

function scrollTriggerHighlighted(): void {
  void nextTick(() => {
    triggerMenu.value?.querySelector<HTMLElement>(`[data-trigger-index="${triggerHighlighted.value}"]`)?.scrollIntoView({ block: 'nearest' })
  })
}

watch(showTriggerPopover, (open) => {
  if (open) {
    window.addEventListener('resize', positionTriggerPopover)
    void nextTick(positionTriggerPopover)
  } else {
    window.removeEventListener('resize', positionTriggerPopover)
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', positionTriggerPopover)
  if (mentionDebounceTimer) clearTimeout(mentionDebounceTimer)
})

function handleKeydown(event: KeyboardEvent): void {
  if (event.isComposing) return
  if (showTriggerPopover.value) {
    const count = triggerItemsCount.value
    if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      triggerDismissed.value = true
      return
    }
    if (count > 0) {
      if (event.key === 'ArrowDown') {
        event.preventDefault()
        triggerHighlighted.value = (triggerHighlighted.value + 1) % count
        scrollTriggerHighlighted()
        return
      }
      if (event.key === 'ArrowUp') {
        event.preventDefault()
        triggerHighlighted.value = (triggerHighlighted.value - 1 + count) % count
        scrollTriggerHighlighted()
        return
      }
      if ((event.key === 'Enter' && !event.shiftKey) || event.key === 'Tab') {
        event.preventDefault()
        if (triggerMatch.value?.kind === 'command') {
          const cmd = filteredCommands.value[triggerHighlighted.value]
          if (cmd) selectSlashCommand(cmd)
        } else if (triggerMatch.value?.kind === 'mention') {
          const file = mentionFiles.value[triggerHighlighted.value]
          if (file) selectMentionFile(file)
        }
        return
      }
    }
  }
  if (event.key !== 'Enter' || event.shiftKey) return
  event.preventDefault()
  submit()
}

function focus(): void {
  textarea.value?.focus()
}

function setValue(nextValue: string): void {
  value.value = nextValue
  if (props.taskId) {
    saveTaskDraft(props.taskId, nextValue, images.value, attachedFiles.value)
  }
  void nextTick(() => {
    resize()
    focus()
  })
}

function getValue(): string {
  return value.value
}

function populate(nextValue: string, nextImages?: ImageAttachment[], nextFiles?: FileAttachment[]): void {
  value.value = nextValue
  images.value = nextImages ? [...nextImages] : []
  attachedFiles.value = nextFiles ? [...nextFiles] : []
  triggerMatch.value = null
  triggerDismissed.value = false
  if (props.taskId) {
    saveTaskDraft(props.taskId, nextValue, images.value, attachedFiles.value)
  }
  void nextTick(() => {
    resize()
    focus()
  })
}

function clearDraft(taskId?: string): void {
  const targetId = taskId || props.taskId
  if (targetId) clearTaskDraft(targetId)
}

defineExpose({ focus, setValue, getValue, populate, clearDraft })
watch(value, resize)

watch(() => props.taskId, (newTaskId, oldTaskId) => {
  // 1. 如果此前有活动的会话，暂存该会话当前未发送的草稿内容（文本及附件）
  if (oldTaskId) {
    saveTaskDraft(oldTaskId, value.value, images.value, attachedFiles.value)
  }

  // 2. 切换到新会话：如果新会话有暂存草稿则恢复，否则呈现全新的空输入框
  const draft = newTaskId ? getTaskDraft(newTaskId) : undefined
  if (draft) {
    value.value = draft.value
    images.value = [...draft.images]
    attachedFiles.value = [...draft.files]
  } else {
    value.value = ''
    images.value = []
    attachedFiles.value = []
  }

  imageNotice.value = ''
  previewImage.value = null
  triggerMatch.value = null
  triggerDismissed.value = false
  void nextTick(() => {
    resize()
    focus()
  })
})

watch(() => props.supportsImages, async (supported) => {
  if (!supported) {
    // 切换为不支持图片的模型：将已附带本地路径的图片自动平滑转换为文件附件
    if (images.value.length > 0) {
      const remainingImages: ImageAttachment[] = []
      for (const img of images.value) {
        if (img.path) {
          addFileByPath(img.path, img.sizeBytes)
        } else {
          remainingImages.push(img)
        }
      }
      images.value = remainingImages
      if (remainingImages.length > 0) {
        imageNotice.value = '当前模型不支持图片理解，请切换支持视觉的模型或移除纯截图。'
      } else {
        imageNotice.value = '当前模型不支持视觉理解，已自动将图片转为文件附件供模型调用处理。'
        window.setTimeout(() => { if (imageNotice.value.startsWith('当前模型不支持视觉理解')) imageNotice.value = '' }, 3500)
      }
    }
  } else {
    // 切换为支持视觉的模型：检查文件附件中是否有图片，如有则自动升格为图片卡片
    if (attachedFiles.value.length > 0) {
      const remainingFiles: FileAttachment[] = []
      for (const file of attachedFiles.value) {
        if (isImageFile(file.name)) {
          const fileData = await window.seekclaw?.readFileBase64?.(file.path)
          if (fileData) {
            addImages([{
              name: file.name,
              mediaType: fileData.mediaType,
              data: fileData.data,
              sizeBytes: fileData.sizeBytes,
              path: file.path
            }])
            continue
          }
        }
        remainingFiles.push(file)
      }
      attachedFiles.value = remainingFiles
    }
    if (imageNotice.value.startsWith('当前模型不支持图片理解')) {
      imageNotice.value = ''
    }
  }
})
</script>

<template>
  <div v-show="!obscured" class="composer-frame">
    <div v-if="$slots.context" class="composer-context-strip">
      <slot name="context" />
    </div>
    <div ref="shellRef" class="composer-shell" :class="{ 'drag-over': isDragOver }" @dragenter="handleDragEnter"
      @dragover="handleDragOver" @dragleave="handleDragLeave" @drop="handleDrop">
      <p v-if="dropNotice" class="composer-image-notice" role="status">{{ dropNotice }}</p>
      <div v-if="attachedFiles.length" class="composer-files-strip" aria-label="待发送文件附件">
        <div v-for="file in attachedFiles" :key="file.id" class="composer-file-chip" :title="file.path">
          <span class="file-ext-badge" :class="fileExtClass(file.extension)">
            {{ fileBadgeText(file.extension) }}
          </span>
          <span class="file-chip-name">{{ file.name }}</span>
          <button type="button" class="file-chip-remove" :title="`移除 ${file.name}`" @click="removeAttachedFile(file.id)">
            <X :size="12" />
          </button>
        </div>
      </div>
      <div v-if="images.length" class="composer-image-strip" aria-label="待发送图片">
        <div v-for="image in images" :key="image.id" class="composer-image-card">
          <button type="button" class="composer-image-preview" :title="`预览 ${image.name}`" @click="previewImage = image">
            <img :src="imageUrl(image)" :alt="image.name">
          </button>
          <button type="button" class="composer-image-remove" :title="`移除 ${image.name}`" @click="removeImage(image.id)">
            <X :size="12" />
          </button>
        </div>
      </div>
      <p v-if="imageNotice" class="composer-image-notice" role="status">{{ imageNotice }}</p>
      <textarea ref="textarea" v-model="value" :disabled="disabled" rows="1"
        :placeholder="disabled ? (disabledPlaceholder || '恢复任务后可继续对话') : '随心输入（支持 @ 引用文件，/ 快捷命令）'"
        aria-label="消息" @input="evaluateTriggers(true)" @click="evaluateTriggers(false)"
        @keydown="handleKeydown" @paste="handlePaste" />
      <div class="composer-toolbar">
        <div class="composer-toolbar-left">
          <!-- + Action Button -->
          <button
            ref="plusTrigger"
            class="composer-plus-btn"
            type="button"
            title="添加附件或优化提示词"
            aria-label="添加附件或优化提示词"
            aria-haspopup="menu"
            :aria-expanded="plusMenuOpen"
            :disabled="disabled || selectingFiles"
            @click="togglePlusMenu"
            @keydown="handlePlusMenuKeydown"
          >
            <Plus :size="18" />
          </button>

          <!-- Approval Mode Selector -->
          <ApprovalModeMenu
            :model-value="mode"
            :disabled="disabled"
            @update:model-value="emit('changeMode', $event)"
          />
        </div>

        <div class="composer-toolbar-right">
          <!-- Unified Model & Reasoning Depth Menu -->
          <ModelReasoningMenu
            :model="model"
            :models="models"
            :reasoning-level="reasoningLevel"
            :reasoning-levels="reasoningLevels"
            :disabled="disabled"
            @update:model="emit('changeModel', $event)"
            @update:reasoning-level="emit('changeReasoningLevel', $event)"
          />

          <!-- Send / Stop Button -->
          <button
            v-if="busy && !value.trim() && images.length === 0 && attachedFiles.length === 0"
            class="send-button active"
            type="button"
            title="停止"
            aria-label="停止生成"
            @click="emit('stop')"
          >
            <Square :size="13" fill="currentColor" />
          </button>
          <button
            v-else
            class="send-button"
            type="button"
            :class="{ active: Boolean(value.trim() || images.length > 0 || attachedFiles.length > 0) }"
            :title="busy ? '排队发送（本轮结束后自动发送）' : '发送'"
            :aria-label="busy ? '排队发送消息' : '发送消息'"
            :disabled="disabled || (!value.trim() && images.length === 0 && attachedFiles.length === 0) || (images.length > 0 && !supportsImages)"
            @click="submit"
          >
            <ArrowUp :size="18" />
          </button>
        </div>
      </div>
    </div>
  </div>

  <!-- Plus Context Popover -->
  <Teleport to="body">
    <Transition name="select-popover">
      <section
        v-if="plusMenuOpen && !obscured"
        ref="plusMenu"
        class="composer-plus-popover"
        :data-placement="plusPlacedAbove ? 'above' : 'below'"
        role="menu"
        aria-label="添加附件与操作"
        :style="plusMenuStyle"
        @keydown="handlePlusMenuKeydown"
      >
        <button
          type="button"
          class="plus-popover-item"
          role="menuitem"
          @click="handlePlusSelectFiles"
        >
          <Paperclip :size="15" />
          <span>附加文件</span>
        </button>
        <button
          type="button"
          class="plus-popover-item"
          role="menuitem"
          @click="handlePlusSelectImages"
        >
          <ImageIcon :size="15" />
          <span>添加图片</span>
        </button>
        <button
          type="button"
          class="plus-popover-item"
          :class="{ disabled: !value.trim() || busy || optimizing }"
          :disabled="!value.trim() || busy || optimizing"
          role="menuitem"
          @click="handlePlusOptimizePrompt"
        >
          <LoaderCircle v-if="optimizing" class="spin" :size="15" />
          <Sparkles v-else :size="15" />
          <span>优化提示词</span>
        </button>
      </section>
    </Transition>
  </Teleport>

  <Teleport to="body">
    <Transition name="select-popover">
      <section
        v-if="showTriggerPopover && !obscured"
        ref="triggerMenu"
        class="composer-trigger-popover"
        data-placement="above"
        role="listbox"
        :aria-label="triggerMatch?.kind === 'command' ? '快捷斜杠命令' : '引用工作区文件'"
        :style="triggerStyle"
      >
        <template v-if="triggerMatch?.kind === 'command'">
          <header class="composer-trigger-header">
            <Slash :size="13" aria-hidden="true" />
            <span>快捷命令</span>
            <small>↑↓ 选择 · Enter 执行</small>
          </header>
          <div v-if="filteredCommands.length === 0" class="composer-trigger-empty">
            没有匹配的命令
          </div>
          <button
            v-for="(cmd, idx) in filteredCommands"
            :key="cmd.id"
            type="button"
            class="composer-trigger-item"
            :class="{ highlighted: idx === triggerHighlighted }"
            :data-trigger-index="idx"
            role="option"
            :aria-selected="idx === triggerHighlighted"
            @mouseenter="triggerHighlighted = idx"
            @mousedown.prevent="selectSlashCommand(cmd)"
          >
            <code class="composer-trigger-cmd">{{ cmd.command }}</code>
            <span class="composer-trigger-title">{{ cmd.title }}</span>
            <span class="composer-trigger-desc">{{ cmd.description }}</span>
          </button>
        </template>

        <template v-else-if="triggerMatch?.kind === 'mention'">
          <header class="composer-trigger-header">
            <FileText :size="13" aria-hidden="true" />
            <span>工作区文件</span>
            <small v-if="workspacePath">↑↓ 选择 · Enter 引用并附加</small>
          </header>
          <div v-if="!workspacePath" class="composer-trigger-empty">
            请先在左下角选择项目工作目录，以检索文件
          </div>
          <div v-else-if="mentionLoading && mentionFiles.length === 0" class="composer-trigger-empty">
            正在索引工作区文件…
          </div>
          <div v-else-if="mentionFiles.length === 0" class="composer-trigger-empty">
            未找到匹配的工作区文件
          </div>
          <button
            v-for="(file, idx) in mentionFiles"
            :key="file.path"
            type="button"
            class="composer-trigger-item"
            :class="{ highlighted: idx === triggerHighlighted }"
            :data-trigger-index="idx"
            role="option"
            :aria-selected="idx === triggerHighlighted"
            :title="file.relativePath"
            @mouseenter="triggerHighlighted = idx"
            @mousedown.prevent="selectMentionFile(file)"
          >
            <span class="composer-trigger-title">{{ file.name }}</span>
            <span class="composer-trigger-desc">{{ file.relativePath }}</span>
          </button>
        </template>
      </section>
    </Transition>
  </Teleport>

  <ImagePreviewDialog :src="previewImage ? imageUrl(previewImage) : undefined" :name="previewImage?.name"
    @close="previewImage = null" />
</template>
