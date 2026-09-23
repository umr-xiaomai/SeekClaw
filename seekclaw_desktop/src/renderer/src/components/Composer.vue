<script setup lang="ts">
import { ArrowUp, LoaderCircle, Paperclip, Sparkles, Square, X } from '@lucide/vue'
import { nextTick, ref, watch } from 'vue'
import type { FileAttachment, ImageAttachment, ReasoningLevel } from '../types'
import { confirmAction } from '../confirmation'
import { fileBadgeText, fileExtClass, getFileExtension } from '../app-helpers'
import ImagePreviewDialog from './ImagePreviewDialog.vue'
import ReasoningDepthMenu from './ReasoningDepthMenu.vue'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  busy: boolean
  disabled?: boolean
  taskId?: string
  model: string
  models: string[]
  mode: string
  reasoningLevel: ReasoningLevel
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
const modeOptions = [
  { value: 'edit', label: 'Edit', description: '可读取并修改文件' },
  { value: 'plan', label: 'Plan', description: '先分析并制定计划' },
  { value: 'readonly', label: 'Read', description: '仅分析，不修改文件' },
  { value: 'auto', label: 'Auto', description: '根据任务自动选择' }
]

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
  let handledFile = false
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
          if (addFileByPath(nativePath, file.size)) handledFile = true
        }
      } else {
        if (addFileByPath(nativePath, file.size)) handledFile = true
      }
    }
  }

  if (handledImage || handledFile) {
    event.preventDefault()
  }
}

const dropNotice = ref('')

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
    dropNotice.value = notices.join('，')
    window.setTimeout(() => { dropNotice.value = '' }, 3000)
  }
}

function removeImage(id: string): void {
  images.value = images.value.filter((image) => image.id !== id)
  imageNotice.value = ''
}

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
  value.value = ''
  images.value = []
  attachedFiles.value = []
  imageNotice.value = ''
  void nextTick(resize)
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  submit()
}

function focus(): void {
  textarea.value?.focus()
}

function setValue(nextValue: string): void {
  value.value = nextValue
  void nextTick(() => {
    resize()
    focus()
  })
}

function getValue(): string {
  return value.value
}

defineExpose({ focus, setValue, getValue })
watch(value, resize)
watch(() => props.taskId, () => {
  images.value = []
  attachedFiles.value = []
  imageNotice.value = ''
  previewImage.value = null
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
  <div class="composer-shell" :class="{ 'drag-over': isDragOver }" @dragenter="handleDragEnter"
    @dragover="handleDragOver" @dragleave="handleDragLeave" @drop="handleDrop">
    <!--  <p v-if="dropNotice" class="composer-image-notice">{{ dropNotice }}</p>-->
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
    <p v-if="imageNotice" class="composer-image-notice">{{ imageNotice }}</p>
    <textarea ref="textarea" v-model="value" :disabled="disabled" rows="1"
      :placeholder="disabled ? '恢复任务后可继续对话' : '交给 SeekClaw'" aria-label="消息" @keydown="handleKeydown"
      @paste="handlePaste" />
    <div class="composer-toolbar">
      <button class="icon-button composer-icon" type="button" title="添加文件附件（可直接拖拽任意文件或文件夹）"
        :disabled="disabled || selectingFiles" @click="selectFiles">
        <Paperclip :size="17" />
      </button>
      <button class="icon-button composer-icon" type="button" :title="optimizing ? '正在优化提示词' : '优化提示词'"
        :disabled="disabled || busy || optimizing || !value.trim()" @click="optimizeCurrentPrompt">
        <LoaderCircle v-if="optimizing" class="spin" :size="16" />
        <Sparkles v-else :size="16" />
      </button>
      <SelectMenu class="composer-select mode-control" :model-value="mode" :options="modeOptions" label="Agent 模式"
        :disabled="busy || disabled" :menu-min-width="220" @update:model-value="emit('changeMode', $event)" />
      <SelectMenu class="composer-select model-control" :model-value="model"
        :options="models.length > 0 ? models.map((item) => ({ value: item, label: item })) : [{ value: '', label: '未配置模型' }]"
        label="模型" :disabled="busy || disabled || models.length === 0" :menu-min-width="300" searchable
        @update:model-value="emit('changeModel', $event)" />
      <span class="toolbar-spacer" />
      <ReasoningDepthMenu :model-value="reasoningLevel" :disabled="busy || disabled"
        @update:model-value="emit('changeReasoningLevel', $event)" />
      <button v-if="busy && !value.trim() && images.length === 0 && attachedFiles.length === 0" class="send-button"
        title="停止" @click="emit('stop')">
        <Square :size="14" fill="currentColor" />
      </button>
      <button v-else class="send-button" :title="busy ? '排队发送（本轮结束后自动发送）' : '发送'"
        :disabled="disabled || (!value.trim() && images.length === 0 && attachedFiles.length === 0) || (images.length > 0 && !supportsImages)"
        @click="submit">
        <ArrowUp :size="19" />
      </button>
    </div>
  </div>

  <ImagePreviewDialog :src="previewImage ? imageUrl(previewImage) : undefined" :name="previewImage?.name"
    @close="previewImage = null" />
</template>
