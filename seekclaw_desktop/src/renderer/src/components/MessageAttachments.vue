<script setup lang="ts">
import { fileBadgeText, fileExtClass } from '../app-helpers'
import type { FileAttachment, ImageAttachment } from '../types'

defineProps<{
  files?: FileAttachment[]
  images?: ImageAttachment[]
  imageSources: Record<string, string>
}>()

const emit = defineEmits<{ previewImage: [id: string, name: string] }>()

function revealFile(path: string): void {
  void window.seekclaw.showItemInFolder(path)
}
</script>

<template>
  <div v-if="files?.length || images?.length" class="message-attachments" aria-label="附件">
    <button v-for="file in files" :key="file.id" type="button" class="user-file-chip"
      :title="`在文件夹中显示: ${file.path}`" @click="revealFile(file.path)">
      <span class="file-ext-badge" :class="fileExtClass(file.extension)" aria-hidden="true">
        {{ fileBadgeText(file.extension) }}
      </span>
      <span class="file-chip-name">{{ file.name }}</span>
    </button>
    <button v-for="image in images" :key="image.id" type="button" class="image-attachment"
      :title="`预览 ${image.name}`" :aria-label="`预览 ${image.name}`" :disabled="!imageSources[image.id]"
      @click="emit('previewImage', image.id, image.name)">
      <img v-if="imageSources[image.id]" :src="imageSources[image.id]" alt="" loading="lazy">
      <span v-else class="image-attachment-placeholder" aria-hidden="true" />
      <span class="image-attachment-label">
        <strong>{{ image.name }}</strong>
        <small>图片</small>
      </span>
    </button>
  </div>
</template>

<style scoped>
.message-attachments {
  display: flex;
  width: 100%;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
  min-width: 0;
}

.image-attachment {
  display: inline-grid;
  grid-template-columns: 36px minmax(0, 1fr);
  align-items: center;
  gap: 8px;
  width: min(100%, 158px);
  min-width: 0;
  padding: 7px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  text-align: left;
}

.image-attachment:hover:not(:disabled) { background: var(--surface-hover); }

.image-attachment img,
.image-attachment-placeholder {
  width: 36px;
  height: 36px;
  object-fit: cover;
  border-radius: 5px;
  background: var(--surface-hover);
}

.image-attachment-label {
  display: grid;
  min-width: 0;
  gap: 3px;
  font-size: 12px;
  line-height: 1.35;
}

.image-attachment-label strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 600;
}

.image-attachment-label small { color: var(--text-muted); }
</style>
