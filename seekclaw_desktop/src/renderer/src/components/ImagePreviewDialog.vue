<script setup lang="ts">
import { X } from '@lucide/vue'
import { onBeforeUnmount, watch } from 'vue'

const props = defineProps<{ src?: string; name?: string }>()
const emit = defineEmits<{ close: [] }>()

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && props.src) emit('close')
}

watch(() => props.src, (src) => {
  if (src) document.addEventListener('keydown', handleKeydown)
  else document.removeEventListener('keydown', handleKeydown)
})
onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="image-preview-fade">
      <div v-if="src" class="image-preview-backdrop" role="dialog" aria-modal="true" :aria-label="name || '图片预览'" @click.self="emit('close')">
        <figure class="image-preview-dialog">
          <button type="button" class="image-preview-close" title="关闭预览" @click="emit('close')"><X :size="18" /></button>
          <img :src="src" :alt="name || '图片预览'">
          <figcaption v-if="name">{{ name }}</figcaption>
        </figure>
      </div>
    </Transition>
  </Teleport>
</template>
