<script setup lang="ts">
import { ArrowUp, Globe, ImagePlus, Square, X } from '@lucide/vue'
import { nextTick, ref, watch } from 'vue'
import type { ImageAttachment, ReasoningLevel } from '../types'
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
  networkEnabled: boolean
}>()

const emit = defineEmits<{
  send: [message: string, images: ImageAttachment[]]
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
const imageNotice = ref('')
const selectingImages = ref(false)
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

async function handlePaste(event: ClipboardEvent): Promise<void> {
  const files = Array.from(event.clipboardData?.items ?? [])
    .filter((item) => item.kind === 'file' && item.type.startsWith('image/'))
    .map((item) => item.getAsFile())
    .filter((file): file is File => file !== null)
  if (files.length === 0) return
  if (props.disabled || !props.supportsImages) {
    imageNotice.value = props.supportsImages
      ? '当前任务无法添加图片。'
      : '当前模型不支持图片理解，请切换支持视觉的模型。'
    return
  }

  event.preventDefault()
  try {
    const candidates = await Promise.all(files.map(async (file, index) => ({
      name: file.name && file.name !== 'image.png' ? file.name : clipboardImageName(file.type, index),
      mediaType: file.type,
      data: await readClipboardFile(file),
      sizeBytes: file.size
    })))
    imageNotice.value = addImages(candidates)
  } catch (error) {
    imageNotice.value = error instanceof Error ? error.message : '无法读取剪贴板图片。'
  }
}

function removeImage(id: string): void {
  images.value = images.value.filter((image) => image.id !== id)
  imageNotice.value = ''
}

function submit(): void {
  const message = value.value.trim()
  if ((!message && images.value.length === 0) || props.disabled) return
  if (images.value.length > 0 && !props.supportsImages) {
    imageNotice.value = '当前模型不支持图片理解，请切换支持视觉的模型。'
    return
  }
  // Vue reactive proxies cannot be structured-cloned by Electron IPC, so hand
  // the renderer plain copies of the attachments before crossing the bridge.
  const outgoingImages = images.value.map((image) => ({
    id: image.id,
    name: image.name,
    mediaType: image.mediaType,
    data: image.data,
    sizeBytes: image.sizeBytes
  }))
  emit('send', message, outgoingImages)
  value.value = ''
  images.value = []
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

defineExpose({ focus, setValue })
watch(value, resize)
watch(() => props.taskId, () => {
  images.value = []
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
  <div class="composer-shell">
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
        :title="supportsImages ? '添加图片（最多 10 张）' : '当前模型不支持图片理解'"
        :disabled="disabled || !supportsImages || selectingImages || images.length >= maxImageCount"
        @click="selectImages"
      >
        <ImagePlus :size="18" />
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
        :options="models.length > 0 ? models.map((item) => ({ value: item, label: item })) : [{ value: model, label: model }]"
        label="模型"
        :disabled="busy || disabled"
        :menu-min-width="300"
        searchable
        @update:model-value="emit('changeModel', $event)"
      />
      <button
        class="network-toggle"
        :class="{ active: networkEnabled }"
        type="button"
        :disabled="disabled"
        :title="networkEnabled ? '联网已开启：网页搜索与访问网络可用' : '联网已关闭：网页搜索与访问网络不可用'"
        :aria-pressed="networkEnabled"
        @click="emit('changeNetwork', !networkEnabled)"
      >
        <Globe :size="15" />
        <span>联网</span>
      </button>
      <span class="toolbar-spacer" />
      <ReasoningDepthMenu
        :model-value="reasoningLevel"
        :disabled="busy || disabled"
        @update:model-value="emit('changeReasoningLevel', $event)"
      />
      <button v-if="busy && !value.trim() && images.length === 0" class="send-button" title="停止" @click="emit('stop')">
        <Square :size="14" fill="currentColor" />
      </button>
      <button
        v-else
        class="send-button"
        :title="busy ? '排队发送（本轮结束后自动发送）' : '发送'"
        :disabled="disabled || (!value.trim() && images.length === 0) || (images.length > 0 && !supportsImages)"
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
