<script setup lang="ts">
import {
  Archive,
  Blocks,
  CalendarClock,
  Home,
  Settings2,
  Sparkles
} from '@lucide/vue'

withDefaults(defineProps<{
  activeItem?: 'chat' | 'archived' | 'scheduled' | 'plugins' | 'skills' | 'settings'
  sidebarOpen?: boolean
}>(), {
  activeItem: 'chat',
  sidebarOpen: true
})

const emit = defineEmits<{
  openChat: []
  openArchived: []
  openScheduledTasks: []
  openPlugins: []
  openSkills: []
  openSettings: []
}>()
</script>

<template>
  <nav class="activity-bar" aria-label="主要导航">
    <div class="activity-bar-top">
      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'chat' && sidebarOpen }"
        title="聊天"
        aria-label="聊天"
        @click="emit('openChat')"
      >
        <Home :size="19" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'archived' }"
        title="已归档"
        aria-label="已归档"
        @click="emit('openArchived')"
      >
        <Archive :size="19" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'scheduled' }"
        title="计划任务"
        aria-label="计划任务"
        @click="emit('openScheduledTasks')"
      >
        <CalendarClock :size="19" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'plugins' }"
        title="插件"
        aria-label="插件"
        @click="emit('openPlugins')"
      >
        <Blocks :size="19" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'skills' }"
        title="技能"
        aria-label="技能"
        @click="emit('openSkills')"
      >
        <Sparkles :size="19" />
      </button>
    </div>

    <div class="activity-bar-bottom">
      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'settings' }"
        title="设置"
        aria-label="设置"
        @click="emit('openSettings')"
      >
        <Settings2 :size="19" />
      </button>
    </div>
  </nav>
</template>

<style scoped>
.activity-bar {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: space-between;
  width: 56px;
  height: 100%;
  flex: 0 0 56px;
  padding: 10px 0 14px;
  background: var(--sidebar);
  border-right: none;
  user-select: none;
  z-index: 10;
}

:root[data-material="mica"] .activity-bar {
  background: color-mix(in srgb, var(--sidebar) 75%, transparent);
  backdrop-filter: blur(34px) saturate(110%);
  -webkit-backdrop-filter: blur(34px) saturate(110%);
}

.activity-bar-top,
.activity-bar-bottom {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  width: 100%;
}

.activity-item {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  padding: 0;
  border: none;
  border-radius: 10px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: color 150ms ease, background-color 150ms ease;
}

.activity-item:hover:not(.active) {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.activity-item.active {
  background: color-mix(in srgb, var(--text) 8.5%, transparent);
  color: var(--text);
  box-shadow: none;
}
</style>
