<script setup lang="ts">
import { CircleHelp } from '@lucide/vue'

defineProps<{
  zh: string
  en: string
  help: string
  required?: boolean
}>()
</script>

<template>
  <span class="bilingual-field-label">
    <span><strong>{{ zh }}</strong><em v-if="required">*</em></span>
    <span class="field-tooltip" tabindex="0" :aria-label="`${zh}：${help}`">
      <CircleHelp :size="14" />
      <span class="field-tooltip-bubble" role="tooltip">{{ help }}</span>
    </span>
  </span>
</template>

<style scoped>
.bilingual-field-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text-secondary);
  line-height: 18px;
}

.bilingual-field-label strong {
  color: var(--text);
  font-weight: 600;
}

.field-label-divider {
  margin: 0 4px;
  color: var(--text-muted);
}

.bilingual-field-label em {
  margin-left: 3px;
  color: var(--danger);
  font-style: normal;
}

.field-tooltip {
  position: relative;
  display: inline-grid;
  flex: 0 0 auto;
  width: 18px;
  height: 18px;
  place-items: center;
  color: var(--text-muted);
  border-radius: 50%;
  outline: 0;
}

.field-tooltip:hover,
.field-tooltip:focus-visible {
  color: var(--text);
  background: var(--surface-hover);
}

.field-tooltip-bubble {
  position: absolute;
  z-index: 260;
  top: calc(100% + 7px);
  left: -8px;
  width: max-content;
  max-width: min(280px, 70vw);
  padding: 8px 10px;
  color: #f4f6f4;
  font-size: 12px;
  font-weight: 400;
  line-height: 1.55;
  pointer-events: none;
  visibility: hidden;
  background: #252a27;
  border: 1px solid rgb(255 255 255 / 9%);
  border-radius: 7px;
  box-shadow: 0 8px 24px rgb(0 0 0 / 22%);
  opacity: 0;
  transform: translateY(-3px);
  transition: opacity 120ms ease, transform 140ms ease, visibility 120ms;
}

.field-tooltip:hover .field-tooltip-bubble,
.field-tooltip:focus-visible .field-tooltip-bubble {
  visibility: visible;
  opacity: 1;
  transform: translateY(0);
}
</style>
