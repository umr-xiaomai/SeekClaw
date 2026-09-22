<script setup lang="ts">
import { ArrowUp, ImagePlus, LoaderCircle, Paperclip, Sparkles, Square, X } from '@lucide/vue'
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
const selectingImages = ref(false)
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

async function selectImages(): Promise<void> {
  if (props.disabled || !props.supportsImages || selectingImages.value) return
  selectingImages.value = true
  imageNotice.value = ''
  try {
    const result = await window.seekclaw.selectImages()
    const warning = addImages(result.images)
    imageNotice.value = [result.warning, warning].filter(Boolean).join(' ')
  } catch (error) {
    imageNotice.value = error instanceof Error ? error.message : '无法读取所选图片。'
  } finally {
    selectingImages.value = false
  }
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

function addImages(candidates: Array<Omit<ImageAttachment, 'id'>>): string {
  const warnings: string[] = []
  let totalBytes = images.value.reduce((total, image) => total + image.sizeBytes, 0)
  for (const candidate of candidates) {
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
  if (attachedFiles.value.some((f) => f.path.toLowerCase() === path.toLowerCase())) return false
  const name = path.split(/[/\\]/).pop() || 'file'
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
      addFileByPath(path)
    }
  } finally {
    selectingFiles.value = false
  }
}

async function handlePaste(event: ClipboardEvent): Promise<void> {
  const clipboardFiles = Array.from(event.clipboardData?.files ?? [])
  let hasAttachment = false
  for (const file of clipboardFiles) {
    const nativePath = window.seekclaw?.getPathForFile?.(file) || (file as unknown as { path?: string }).path || ''
    if (nativePath) {
      if (addFileByPath(nativePath, file.size)) hasAttachment = true
    }
  }

  const imageFiles = Array.from(event.clipboardData?.items ?? [])
    .filter((item) => item.kind === 'file' && item.type.startsWith('image/'))
    .map((item) => item.getAsFile())
    .filter((file): file is File => file !== null)

  if (imageFiles.length > 0 && props.supportsImages) {
    try {
      const candidates = await Promise.all(imageFiles.map(async (file, index) => ({
        name: file.name && file.name !== 'image.png' ? file.name : clipboardImageName(file.type, index),
        mediaType: file.type,
        data: await readClipboardFile(file),
        sizeBytes: file.size
      })))
      imageNotice.value = addImages(candidates)
      hasAttachment = true
    } catch (error) {
      imageNotice.value = error instanceof Error ? error.message : '无法读取剪贴板图片。'
    }
  }

  if (hasAttachment) {
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
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (files.length === 0) return

  let addedCount = 0
  for (const file of files) {
    const nativePath = window.seekclaw?.getPathForFile?.(file) || (file as unknown as { path?: string }).path || ''
    if (nativePath) {
      if (addFileByPath(nativePath, file.size)) addedCount++
    }

    // If image and model supports images, also add to vision images
    if (file.type.startsWith('image/') && props.supportsImages) {
      const reader = new FileReader()
      reader.onload = () => {
        const data = String(reader.result).split(',')[1] ?? ''
        addImages([{
          name: file.name,
          mediaType: file.type || 'image/png',
          data,
          sizeBytes: file.size
        }])
      }
      reader.readAsDataURL(file)
    }
  }

  if (addedCount > 0) {
    dropNotice.value = `已附加 ${addedCount} 个文件`
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
    sizeBytes: image.sizeBytes
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
watch(() => props.supportsImages, (supported) => {
  if (!supported && images.value.length > 0)
    imageNotice.value = '当前模型不支持图片理解，请切换支持视觉的模型或移除图片。'
  else if (supported && imageNotice.value.startsWith('当前模型不支持图片理解'))
    imageNotice.value = ''
})
</script>

<template>
  <div
    class="composer-shell"
    :class="{ 'drag-over': isDragOver }"
    @dragenter="handleDragEnter"
    @dragover="handleDragOver"
    @dragleave="handleDragLeave"
    @drop="handleDrop"
  >
    <p v-if="dropNotice" class="composer-image-notice">{{ dropNotice }}</p>
    <div v-if="attachedFiles.length" class="composer-files-strip" aria-label="待发送文件附件">
      <div v-for="file in attachedFiles" :key="file.id" class="composer-file-chip" :title="file.path">
        <span class="file-ext-badge" :class="fileExtClass(file.extension)">
          {{ fileBadgeText(file.extension) }}
        </span>
        <span class="file-chip-name">{{ file.name }}</span>
        <button
          type="button"
          class="file-chip-remove"
          :title="`移除 ${file.name}`"
          @click="removeAttachedFile(file.id)"
        >
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
    <textarea
      ref="textarea"
      v-model="value"
      :disabled="disabled"
      rows="1"
      :placeholder="disabled ? '恢复任务后可继续对话' : '交给 SeekClaw'"
      aria-label="消息"
      @keydown="handleKeydown"
      @paste="handlePaste"
    />
    <div class="composer-toolbar">
      <button
        class="icon-button composer-icon"
        type="button"
        title="添加文件附件（可直接拖拽任意文件或文件夹）"
        :disabled="disabled || selectingFiles"
        @click="selectFiles"
      >
        <Paperclip :size="17" />
      </button>
      <button
        class="icon-button composer-icon"
        :title="supportsImages ? '添加图片（最多 10 张）' : '当前模型不支持图片理解'"
        :disabled="disabled || !supportsImages || selectingImages || images.length >= maxImageCount"
        @click="selectImages"
      >
        <ImagePlus :size="18" />
      </button>
      <button
        class="icon-button composer-icon"
        type="button"
        :title="optimizing ? '正在优化提示词' : '优化提示词'"
        :disabled="disabled || busy || optimizing || !value.trim()"
        @click="optimizeCurrentPrompt"
      >
        <LoaderCircle v-if="optimizing" class="spin" :size="16" />
        <Sparkles v-else :size="16" />
      </button>
      <SelectMenu
        class="composer-select mode-control"
        :model-value="mode"
        :options="modeOptions"
        label="Agent 模式"
        :disabled="busy || disabled"
        :menu-min-width="220"
        @update:model-value="emit('changeMode', $event)"
      />
      <SelectMenu
        class="composer-select model-control"
        :model-value="model"
        :options="models.length > 0 ? models.map((item) => ({ value: item, label: item })) : [{ value: '', label: '未配置模型' }]"
        label="模型"
        :disabled="busy || disabled || models.length === 0"
        :menu-min-width="300"
        searchable
        @update:model-value="emit('changeModel', $event)"
      />
      <span class="toolbar-spacer" />
      <ReasoningDepthMenu
        :model-value="reasoningLevel"
        :disabled="busy || disabled"
        @update:model-value="emit('changeReasoningLevel', $event)"
      />
      <button v-if="busy && !value.trim() && images.length === 0 && attachedFiles.length === 0" class="send-button" title="停止" @click="emit('stop')">
        <Square :size="14" fill="currentColor" />
      </button>
      <button
        v-else
        class="send-button"
        :title="busy ? '排队发送（本轮结束后自动发送）' : '发送'"
        :disabled="disabled || (!value.trim() && images.length === 0 && attachedFiles.length === 0) || (images.length > 0 && !supportsImages)"
        @click="submit"
      >
        <ArrowUp :size="19" />
      </button>
    </div>
  </div>

  <ImagePreviewDialog
    :src="previewImage ? imageUrl(previewImage) : undefined"
    :name="previewImage?.name"
    @close="previewImage = null"
  />
</template>
