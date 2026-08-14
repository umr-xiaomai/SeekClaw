<script setup lang="ts">
import { ArrowUp, Globe, ImagePlus, Sparkles, Square, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
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

const dropNotice = ref('')

async function handleDrop(event: DragEvent): Promise<void> {
  event.preventDefault()
  if (props.disabled) return
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (files.length === 0) return
  const images = files.filter((file) => file.type.startsWith('image/'))
  const texts = files.filter((file) => !file.type.startsWith('image/'))
  let notice = ''
  if (images.length > 0) {
    const candidates = images.map((file) => ({
      name: file.name,
      mediaType: file.type || 'image/png',
      data: '',
      sizeBytes: file.size
    }))
    const decoded = await Promise.all(images.map((file) => new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
      reader.onerror = () => reject(new Error('无法读取图片'))
      reader.readAsDataURL(file)
    })))
    candidates.forEach((candidate, index) => { candidate.data = decoded[index] ?? '' })
    const warning = addImages(candidates)
    notice = [notice, warning].filter(Boolean).join(' ')
  }
  if (texts.length > 0) {
    const parts: string[] = []
    for (const file of texts.slice(0, 3)) {
      const text = (await file.text()).slice(0, 16_000)
      parts.push(`[附件：${file.name}]\n\`\`\`\n${text}\n\`\`\``)
    }
    if (parts.length > 0) {
      const block = parts.join('\n\n')
      value.value = value.value.trim() ? `${value.value.trim()}\n\n${block}` : block
      notice = [notice, '文本附件已加入输入框，可编辑后发送。'].filter(Boolean).join(' ')
      void nextTick(() => { resize(); focus() })
    }
  }
  dropNotice.value = notice
  window.setTimeout(() => { dropNotice.value = '' }, 4000)
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

const promptMenuOpen = ref(false)
const promptMenuStyle = ref<Record<string, string>>({})

const quickPrompts = [
  { label: '探索并理解当前项目', text: '请阅读当前项目并总结结构、技术栈与关键模块，方便后续开发。' },
  { label: '审查代码', text: '请审查当前代码，找出 bug、边界问题与改进点，并给出具体修改建议。' },
  { label: '修复构建/测试错误', text: '请运行构建与测试，修复所有报错，并确保验证通过。' },
  { label: '编写单元测试', text: '请为当前模块编写覆盖关键路径的单元测试，并运行验证。' }
]

function togglePrompts(event: MouseEvent): void {
  if (props.disabled) { promptMenuOpen.value = false; return }
  promptMenuOpen.value = !promptMenuOpen.value
  if (promptMenuOpen.value) {
    const trigger = event.currentTarget as HTMLElement
    const rect = trigger.getBoundingClientRect()
    const edge = 10
    const width = Math.min(320, window.innerWidth - edge * 2)
    const left = Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))
    const placeAbove = window.innerHeight - rect.bottom < 300
    promptMenuStyle.value = placeAbove
      ? { width: `${width}px`, left: `${left}px`, bottom: `${window.innerHeight - rect.top + 8}px` }
      : { width: `${width}px`, left: `${left}px`, top: `${rect.bottom + 8}px` }
  }
}

function insertPrompt(text: string): void {
  promptMenuOpen.value = false
  value.value = value.value.trim() ? `${value.value.trim()}\n\n${text}` : text
  void nextTick(() => { resize(); focus() })
}

function closePromptsOnOutside(event: MouseEvent): void {
  const target = event.target as Node
  if (!(target instanceof Element) || !target.closest('.prompt-menu, .prompt-menu-trigger'))
    promptMenuOpen.value = false
}

onBeforeUnmount(() => document.removeEventListener('mousedown', closePromptsOnOutside, true))

function insertPromptsListeners(): void {
  document.addEventListener('mousedown', closePromptsOnOutside, true)
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
watch(promptMenuOpen, (open) => {
  if (open) insertPromptsListeners()
})
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
  <div class="composer-shell" @drop.prevent="handleDrop" @dragover.prevent>
    <p v-if="dropNotice" class="composer-image-notice">{{ dropNotice }}</p>
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
      <div class="prompt-menu-root">
        <button
          class="icon-button composer-icon prompt-menu-trigger"
          type="button"
          :title="'快捷提示词'"
          :disabled="disabled"
          @click="togglePrompts"
        >
          <Sparkles :size="16" />
        </button>
        <Teleport to="body">
          <Transition name="select-popover">
            <section v-if="promptMenuOpen" class="prompt-menu" role="menu" :style="promptMenuStyle">
              <header class="prompt-menu-header"><strong>快捷提示词</strong></header>
              <button
                v-for="prompt in quickPrompts"
                :key="prompt.label"
                type="button"
                class="prompt-menu-item"
                role="menuitem"
                @click="insertPrompt(prompt.text)"
              >
                <span>{{ prompt.label }}</span>
                <small>{{ prompt.text }}</small>
              </button>
            </section>
          </Transition>
        </Teleport>
      </div>
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
<style scoped>
.prompt-menu-root {
  display: inline-flex;
}

.prompt-menu {
  position: fixed;
  z-index: 210;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--shadow);
}

.prompt-menu-header {
  padding: 11px 14px 8px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: .06em;
  text-transform: uppercase;
  border-bottom: 1px solid var(--border);
}

.prompt-menu-item {
  display: block;
  width: 100%;
  padding: 10px 14px;
  text-align: left;
  border-bottom: 1px solid var(--border);
}

.prompt-menu-item:last-child {
  border-bottom: 0;
}

.prompt-menu-item:hover {
  background: var(--surface-hover);
}

.prompt-menu-item span,
.prompt-menu-item small {
  display: block;
}

.prompt-menu-item span {
  color: var(--text);
  font-size: 12.5px;
  font-weight: 600;
}

.prompt-menu-item small {
  margin-top: 3px;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
