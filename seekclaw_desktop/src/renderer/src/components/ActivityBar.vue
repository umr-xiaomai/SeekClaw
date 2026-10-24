<script setup lang="ts">
import {
  CalendarClock,
  Clock,
  Home,
  Settings2,
  SlidersHorizontal
} from "@lucide/vue"

withDefaults(defineProps<{
  activeItem?: "chat" | "archived" | "scheduled" | "customization" | "settings"
  sidebarOpen?: boolean
}>(), {
  activeItem: "chat",
  sidebarOpen: true
})

const emit = defineEmits<{
  openChat: []
  openArchived: []
  openScheduledTasks: []
  openCustomization: []
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
        <svg
          v-if="activeItem === 'chat' && sidebarOpen"
          class="activity-icon-solid"
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="currentColor"
          aria-hidden="true"
        >
          <path d="M11.0872 2.976C11.6867 2.8275 12.3134 2.8275 12.913 2.976C13.6261 3.1527 14.2602 3.6077 15.1934 4.2557L19.0337 6.9229C19.7482 7.4191 20.2439 7.7542 20.6063 8.2072C20.9119 8.5894 21.1406 9.0273 21.2801 9.4963C21.4456 10.0524 21.4384 10.6501 21.4384 11.5201V16.7994C21.4384 17.3464 21.4384 17.8052 21.4079 18.1787C21.3766 18.5612 21.3092 18.9248 21.1337 19.2697C20.8654 19.7963 20.4367 20.2249 19.9102 20.4932C19.5653 20.6689 19.2018 20.7373 18.8192 20.7685C18.4457 20.7991 17.9868 20.7979 17.4398 20.7979H15C14.5594 20.7978 14.202 20.4406 14.202 19.9998V15.8045C14.202 14.5884 13.2161 13.6015 12 13.6014C10.7839 13.6014 9.7981 14.5884 9.7981 15.8045V19.9998C9.7981 20.4404 9.4407 20.7978 9 20.7979H6.5602C6.0133 20.7979 5.5544 20.7991 5.1809 20.7685C4.7983 20.7373 4.4349 20.6689 4.0899 20.4932C3.5634 20.2249 3.1347 19.7962 2.8665 19.2697C2.6908 18.9248 2.6235 18.5612 2.5922 18.1787C2.5617 17.8052 2.5618 17.3464 2.5618 16.7994V11.5201C2.5618 10.6501 2.5545 10.0524 2.72 9.4963C2.8595 9.0272 3.0881 8.5895 3.3938 8.2072C3.7562 7.7541 4.2518 7.4191 4.9665 6.9229L8.8067 4.2557C9.7399 3.6076 10.3739 3.1527 11.0872 2.976Z" />
        </svg>
        <Home v-else :size="20" :stroke-width="1.8" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'archived' }"
        title="已归档"
        aria-label="已归档"
        @click="emit('openArchived')"
      >
        <svg
          v-if="activeItem === 'archived'"
          class="activity-icon-solid"
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="currentColor"
          aria-hidden="true"
        >
          <path
            fill-rule="evenodd"
            clip-rule="evenodd"
            d="M12 2C6.47715 2 2 6.47715 2 12C2 17.5228 6.47715 22 12 22C17.5228 22 22 17.5228 22 12C22 6.47715 17.5228 2 12 2ZM13 6.5C13 5.94772 12.5523 5.5 12 5.5C11.4477 5.5 11 5.94772 11 6.5V12C11 12.2652 11.1054 12.5196 11.2929 12.7071L14.7929 16.2071C15.1834 16.5976 15.8166 16.5976 16.2071 16.2071C16.5976 15.8166 16.5976 15.1834 16.2071 14.7929L13 11.5858V6.5Z"
          />
        </svg>
        <Clock v-else :size="20" :stroke-width="1.8" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'scheduled' }"
        title="计划任务"
        aria-label="计划任务"
        @click="emit('openScheduledTasks')"
      >
        <svg
          v-if="activeItem === 'scheduled'"
          class="activity-icon-solid"
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="currentColor"
          aria-hidden="true"
        >
          <path d="M8 2a1 1 0 0 0-1 1v1H5a2 2 0 0 0-2 2v2h18V6a2 2 0 0 0-2-2h-2V3a1 1 0 0 0-2 0v1h-6V3a1 1 0 0 0-1-1z" />
          <path
            fill-rule="evenodd"
            clip-rule="evenodd"
            d="M3 10v9a2 2 0 0 0 2 2h4.5a6.5 6.5 0 0 1-.5-2.5c0-1.8.74-3.44 1.93-4.62A6.47 6.47 0 0 1 15.5 12c.52 0 1.02.06 1.5.18V10H3zm13.5 3a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11zm.75 3a.75.75 0 0 0-1.5 0v2.75c0 .2.08.39.22.53l1.5 1.5a.75.75 0 1 0 1.06-1.06l-1.28-1.28V16z"
          />
        </svg>
        <CalendarClock v-else :size="20" :stroke-width="1.8" />
      </button>

      <button
        type="button"
        class="activity-item"
        :class="{ active: activeItem === 'customization' }"
        title="自定义"
        aria-label="自定义"
        @click="emit('openCustomization')"
      >
        <svg
          v-if="activeItem === 'customization'"
          class="activity-icon-solid"
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2.3"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M10 5H3" /><path d="M12 19H3" /><path d="M14 2v6" /><path d="M16 16v6" /><path d="M21 12h-9" /><path d="M21 19h-5" /><path d="M21 5h-7" /><path d="M8 9v6" /><path d="M8 12H3" />
        </svg>
        <SlidersHorizontal v-else :size="20" :stroke-width="1.8" />
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
        <svg
          v-if="activeItem === 'settings'"
          class="activity-icon-solid"
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2.2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M14 17H5" />
          <path d="M19 7h-9" />
          <circle cx="17" cy="17" r="3" fill="currentColor" stroke="none" />
          <circle cx="7" cy="7" r="3" fill="currentColor" stroke="none" />
        </svg>
        <Settings2 v-else :size="20" :stroke-width="1.8" />
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
  width: 52px;
  height: 100%;
  flex: 0 0 52px;
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
  width: 38px;
  height: 38px;
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

.activity-icon-solid {
  display: block;
  flex-shrink: 0;
}
</style>
