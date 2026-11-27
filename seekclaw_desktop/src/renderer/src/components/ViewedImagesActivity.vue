<script setup lang="ts">
import { ChevronRight, Images } from '@lucide/vue'
import { ref } from 'vue'
import type { ImageReference } from '../types'
import ImagePreviewDialog from './ImagePreviewDialog.vue'

const props = defineProps<{
  images: readonly ImageReference[]
  imageSources: Record<string, string>
  expanded: boolean
}>()
const emit = defineEmits<{ 'update:expanded': [expanded: boolean] }>()
const preview = ref<{ src: string; name: string } | null>(null)

function openPreview(image: ImageReference): void {
  const src = props.imageSources[image.id]
  if (src) preview.value = { src, name: image.name }
}

function onToggle(event: Event): void {
  if (event.target !== event.currentTarget) return
  const open = (event.currentTarget as HTMLDetailsElement).open
  if (open !== props.expanded) emit('update:expanded', open)
}
</script>

<template>
  <details class="image-activity" :open="expanded" @toggle="onToggle">
    <summary>
      <Images :size="14" aria-hidden="true" />
      <span>已查看 {{ images.length }} 张图像</span>
      <ChevronRight :size="13" class="image-activity-chevron" aria-hidden="true" />
    </summary>
    <div v-if="expanded" class="image-activity-thumbnails">
      <button v-for="image in images" :key="image.id" type="button" :disabled="!imageSources[image.id]"
        :title="image.name" @click="openPreview(image)">
        <img v-if="imageSources[image.id]" :src="imageSources[image.id]" :alt="image.name" loading="lazy">
        <span v-else>{{ image.name }}</span>
      </button>
    </div>
  </details>
  <ImagePreviewDialog :src="preview?.src" :name="preview?.name" @close="preview = null" />
</template>

<style scoped>
.image-activity { min-width: 0; color: var(--text-muted); font-size: 13px; line-height: 1.45; }
.image-activity > summary { display: flex; align-items: center; gap: 8px; min-height: 27px; width: fit-content; max-width: 100%; padding: 2px 0; border-radius: 5px; cursor: pointer; list-style: none; }
.image-activity > summary:hover, .image-activity[open] > summary { color: var(--text); }
.image-activity > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.image-activity > summary::-webkit-details-marker { display: none; }
.image-activity-chevron { transition: transform 130ms ease; }
.image-activity[open] .image-activity-chevron { transform: rotate(90deg); }
.image-activity-thumbnails { display: flex; flex-wrap: wrap; gap: 8px; margin: 2px 0 6px 7px; padding: 4px 0 4px 12px; border-left: 1px solid var(--border); }
.image-activity-thumbnails button { width: min(96px, 100%); aspect-ratio: 1; padding: 0; overflow: hidden; border: 1px solid var(--border); border-radius: 8px; background: transparent; overflow-wrap: anywhere; }
.image-activity-thumbnails img { width: 100%; height: 100%; object-fit: cover; }
@media (prefers-reduced-motion: reduce) {
  .image-activity-chevron { transition: none; }
}
</style>
