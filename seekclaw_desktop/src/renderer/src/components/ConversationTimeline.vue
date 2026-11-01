<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import type { ConversationTurn } from './conversation-turns'

const props = defineProps<{
  turns: ConversationTurn[]
  activeTurnId?: string
}>()

const emit = defineEmits<{
  selectTurn: [turn: ConversationTurn]
}>()

const timelineEl = ref<HTMLElement | null>(null)
const isNarrow = ref(false)
const isHoveredNear = ref(false)
const hoveredTurn = ref<ConversationTurn | null>(null)
const popoverStyle = ref<{ top: string; left: string } | null>(null)

let leaveTimer: ReturnType<typeof setTimeout> | undefined
let hideNearTimer: ReturnType<typeof setTimeout> | undefined
let resizeObserver: ResizeObserver | null = null

function updateWidth(width: number): void {
  // When conversation area is narrower than 820px, switch to auto-hide drawer mode
  isNarrow.value = width < 820
}

onMounted(() => {
  const parent = timelineEl.value?.parentElement
  if (parent) {
    updateWidth(parent.clientWidth)
    if (typeof ResizeObserver !== 'undefined') {
      resizeObserver = new ResizeObserver((entries) => {
        for (const entry of entries) {
          updateWidth(entry.contentRect.width)
        }
      })
      resizeObserver.observe(parent)
    }
  }
})

function onTriggerEnter(): void {
  if (hideNearTimer) {
    clearTimeout(hideNearTimer)
    hideNearTimer = undefined
  }
  isHoveredNear.value = true
}

function onTriggerLeave(): void {
  if (hideNearTimer) clearTimeout(hideNearTimer)
  hideNearTimer = setTimeout(() => {
    if (!hoveredTurn.value) {
      isHoveredNear.value = false
    }
  }, 220)
}

function onHoverTurn(turn: ConversationTurn, event: MouseEvent): void {
  if (leaveTimer) {
    clearTimeout(leaveTimer)
    leaveTimer = undefined
  }
  if (hideNearTimer) {
    clearTimeout(hideNearTimer)
    hideNearTimer = undefined
  }
  isHoveredNear.value = true

  const target = event.currentTarget as HTMLElement | null
  if (!target) return

  const rect = target.getBoundingClientRect()
  // Vertically align popover with dash, clamped to window bounds
  const cardEstimateHeight = 110
  const top = Math.max(
    60,
    Math.min(rect.top + rect.height / 2 - 28, window.innerHeight - cardEstimateHeight - 20)
  )
  const left = rect.right + 10

  popoverStyle.value = {
    top: `${Math.round(top)}px`,
    left: `${Math.round(left)}px`
  }
  hoveredTurn.value = turn
}

function onLeaveTurn(): void {
  if (leaveTimer) clearTimeout(leaveTimer)
  leaveTimer = setTimeout(() => {
    hoveredTurn.value = null
    popoverStyle.value = null
    onTriggerLeave()
  }, 140)
}

function onPopoverEnter(): void {
  if (leaveTimer) {
    clearTimeout(leaveTimer)
    leaveTimer = undefined
  }
  if (hideNearTimer) {
    clearTimeout(hideNearTimer)
    hideNearTimer = undefined
  }
  isHoveredNear.value = true
}

function onSelectTurn(turn: ConversationTurn): void {
  if (leaveTimer) clearTimeout(leaveTimer)
  if (hideNearTimer) clearTimeout(hideNearTimer)
  hoveredTurn.value = null
  popoverStyle.value = null
  isHoveredNear.value = false
  emit('selectTurn', turn)
}

onBeforeUnmount(() => {
  if (leaveTimer) clearTimeout(leaveTimer)
  if (hideNearTimer) clearTimeout(hideNearTimer)
  resizeObserver?.disconnect()
})
</script>

<template>
  <div
    v-if="turns.length > 0"
    ref="timelineEl"
    class="conversation-timeline-wrapper"
    :class="{
      'is-narrow': isNarrow,
      'is-revealed': isHoveredNear || hoveredTurn !== null
    }"
    @mouseenter="onTriggerEnter"
    @mouseleave="onTriggerLeave"
  >
    <!-- Left edge invisible trigger strip for narrow screen hover -->
    <div
      v-if="isNarrow"
      class="timeline-edge-trigger"
      aria-hidden="true"
      @mouseenter="onTriggerEnter"
    />

    <nav class="conversation-timeline" aria-label="对话回溯进度">
      <div class="timeline-track">
        <button
          v-for="turn in turns"
          :key="turn.id"
          type="button"
          class="timeline-node-btn"
          :class="{
            active: turn.id === activeTurnId,
            hovered: hoveredTurn?.id === turn.id
          }"
          :title="turn.userText"
          :aria-label="`回溯到消息: ${turn.userText}`"
          @mouseenter="onHoverTurn(turn, $event)"
          @mouseleave="onLeaveTurn"
          @click.stop="onSelectTurn(turn)"
        >
          <span class="timeline-dash" />
        </button>
      </div>

      <Teleport to="body">
        <Transition name="timeline-popover-fade">
          <div
            v-if="hoveredTurn && popoverStyle"
            class="timeline-popover-card"
            :style="popoverStyle"
            @mouseenter="onPopoverEnter"
            @mouseleave="onLeaveTurn"
            @click.stop="onSelectTurn(hoveredTurn)"
          >
            <header class="timeline-popover-header">
              <span class="timeline-popover-title">
                {{ hoveredTurn.userText }}
              </span>
            </header>
            <p v-if="hoveredTurn.assistantPreview" class="timeline-popover-preview">
              {{ hoveredTurn.assistantPreview }}
            </p>
          </div>
        </Transition>
      </Teleport>
    </nav>
  </div>
</template>
