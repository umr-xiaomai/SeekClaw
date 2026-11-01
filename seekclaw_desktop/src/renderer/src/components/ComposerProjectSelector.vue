<script setup lang="ts">
import { Folder, Plus, Search } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { ProjectItem } from '../types'
import { pathName } from '../app-helpers'

const props = withDefaults(defineProps<{
  projects: ProjectItem[]
  activeProject?: ProjectItem | null
  isGlobalExplicit?: boolean
  disabled?: boolean
}>(), {
  activeProject: null,
  isGlobalExplicit: false,
  disabled: false
})

const emit = defineEmits<{
  selectProject: [project: ProjectItem]
  selectGlobal: []
  createProject: []
}>()

const isOpen = ref(false)
const searchQuery = ref('')
const triggerRef = ref<HTMLButtonElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)
const searchInputRef = ref<HTMLInputElement | null>(null)
const menuStyle = ref<Record<string, string>>({})

const buttonLabel = computed(() => {
  if (props.activeProject) return props.activeProject.name
  if (props.isGlobalExplicit) return '无工作目录'
  return '选择项目'
})

const filteredProjects = computed(() => {
  const q = searchQuery.value.trim().toLowerCase()
  if (!q) return props.projects
  return props.projects.filter((p) =>
    p.name.toLowerCase().includes(q) ||
    p.path.toLowerCase().includes(q)
  )
})

const showGlobalOption = computed(() => {
  const q = searchQuery.value.trim().toLowerCase()
  if (!q) return true
  return '无工作目录'.includes(q) || '全局环境'.includes(q) || 'none'.includes(q) || 'global'.includes(q)
})

const isGlobalActive = computed(() => {
  return !props.activeProject && props.isGlobalExplicit
})

function updatePosition(): void {
  if (!triggerRef.value) return
  const rect = triggerRef.value.getBoundingClientRect()
  const gap = 8
  const width = Math.min(300, Math.max(260, window.innerWidth - 24))
  const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12))
  const bottom = window.innerHeight - rect.top + gap
  menuStyle.value = {
    position: 'fixed',
    left: `${left}px`,
    bottom: `${bottom}px`,
    width: `${width}px`,
    zIndex: '1000'
  }
}

function openMenu(): void {
  if (props.disabled) return
  isOpen.value = true
  searchQuery.value = ''
  void nextTick(() => {
    updatePosition()
    searchInputRef.value?.focus()
  })
}

function closeMenu(restoreFocus = false): void {
  isOpen.value = false
  if (restoreFocus) {
    void nextTick(() => triggerRef.value?.focus())
  }
}

function toggleMenu(): void {
  if (isOpen.value) closeMenu()
  else openMenu()
}

function onSelectProject(project: ProjectItem): void {
  emit('selectProject', project)
  closeMenu(true)
}

function onSelectGlobal(): void {
  emit('selectGlobal')
  closeMenu(true)
}

function onCreateProject(): void {
  emit('createProject')
  closeMenu(true)
}

function handleOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node | null
  if (!target) return
  if (triggerRef.value?.contains(target)) return
  if (menuRef.value?.contains(target)) return
  closeMenu()
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    closeMenu(true)
  }
}

function handleWindowResize(): void {
  if (isOpen.value) updatePosition()
}

watch(isOpen, (open) => {
  if (open) {
    window.addEventListener('pointerdown', handleOutsidePointer)
    window.addEventListener('resize', handleWindowResize)
  } else {
    window.removeEventListener('pointerdown', handleOutsidePointer)
    window.removeEventListener('resize', handleWindowResize)
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', handleOutsidePointer)
  window.removeEventListener('resize', handleWindowResize)
})
</script>

<template>
  <div class="composer-project-bar">
    <button
      ref="triggerRef"
      type="button"
      class="composer-project-btn"
      :disabled="disabled"
      title="选择项目或无工作目录"
      @click="toggleMenu"
    >
      <Folder :size="15" class="composer-project-icon" />
      <span class="composer-project-name">{{ buttonLabel }}</span>
    </button>
  </div>

  <Teleport to="body">
    <Transition name="select-popover">
      <div
        v-if="isOpen"
        ref="menuRef"
        class="composer-project-popover"
        :style="menuStyle"
        role="dialog"
        aria-label="选择工作区项目"
        @keydown="handleKeydown"
      >
        <div class="project-search-row">
          <Search :size="14" class="project-search-icon" />
          <input
            ref="searchInputRef"
            v-model="searchQuery"
            type="text"
            class="project-search-input"
            placeholder="搜索项目"
            aria-label="搜索项目"
          />
        </div>

        <div class="project-list-scroll">
          <button
            v-if="showGlobalOption"
            type="button"
            class="project-menu-item"
            :class="{ active: isGlobalActive }"
            @click="onSelectGlobal"
          >
            <Folder :size="15" class="project-item-icon" />
            <span class="project-item-name">无工作目录</span>
            <span class="project-item-sub">全局环境</span>
          </button>

          <button
            v-for="project in filteredProjects"
            :key="project.id"
            type="button"
            class="project-menu-item"
            :class="{ active: activeProject?.id === project.id }"
            @click="onSelectProject(project)"
          >
            <Folder :size="15" class="project-item-icon" />
            <span class="project-item-name">{{ project.name }}</span>
            <span
              v-if="pathName(project.path) !== project.name"
              class="project-item-sub"
            >
              {{ pathName(project.path) }}
            </span>
          </button>

          <div v-if="filteredProjects.length === 0 && !showGlobalOption" class="project-empty-item">
            未找到匹配项目
          </div>
        </div>

        <div class="project-popover-divider" />

        <button
          type="button"
          class="project-menu-item project-create-item"
          @click="onCreateProject"
        >
          <Plus :size="15" class="project-item-icon" />
          <span class="project-item-name">新建项目</span>
        </button>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.composer-project-bar {
  display: flex;
  align-items: center;
  width: min(100%, 940px);
  margin: 0 auto -12px auto;
  padding: 6px 14px 18px 14px;
  background: var(--sidebar-panel);
  border: 1px solid var(--border);
  border-bottom: none;
  border-radius: 20px 20px 0 0;
  position: relative;
  z-index: 1;
}

.composer-project-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  font-size: 13px;
  font-weight: 500;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
  user-select: none;
}

.composer-project-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text);
}

.composer-project-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.composer-project-icon {
  flex-shrink: 0;
}

.composer-project-name {
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.composer-project-popover {
  position: fixed;
  width: 280px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 14px;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.12), 0 2px 6px rgba(0, 0, 0, 0.04);
  z-index: 1000;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

:root[data-theme="dark"] .composer-project-popover {
  background: var(--surface-raised);
  box-shadow: 0 12px 36px rgba(0, 0, 0, 0.45);
}

.project-search-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 9px 12px;
  border-bottom: 1px solid var(--border);
}

.project-search-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.project-search-input {
  flex: 1;
  min-width: 0;
  border: none;
  background: transparent;
  font-size: 13px;
  color: var(--text);
  outline: none;
  padding: 0;
}

.project-search-input::placeholder {
  color: var(--text-muted);
}

.project-list-scroll {
  max-height: 240px;
  overflow-y: auto;
  padding: 6px;
  display: flex;
  flex-direction: column;
  gap: 2px;
  scrollbar-width: thin;
}

.project-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 7px 10px;
  font-size: 13px;
  border: none;
  background: transparent;
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  transition: background-color 120ms ease;
  color: var(--text);
  user-select: none;
}

.project-menu-item:hover {
  background: var(--surface-hover);
}

.project-menu-item.active {
  background: color-mix(in srgb, var(--accent) 12%, var(--surface));
  color: var(--accent);
}

.project-item-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.project-menu-item.active .project-item-icon {
  color: var(--accent);
}

.project-item-name {
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.project-item-sub {
  color: var(--text-muted);
  font-size: 12px;
  margin-left: 2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.project-menu-item.active .project-item-sub {
  color: color-mix(in srgb, var(--accent) 70%, var(--text-muted));
}

.project-popover-divider {
  height: 1px;
  background: var(--border);
  margin: 0;
}

.project-create-item {
  margin: 6px;
  width: calc(100% - 12px);
  color: var(--text);
}

.project-empty-item {
  padding: 16px 12px;
  font-size: 12px;
  color: var(--text-muted);
  text-align: center;
}

.select-popover-enter-active,
.select-popover-leave-active {
  transition: opacity 120ms ease, transform 120ms ease;
}

.select-popover-enter-from,
.select-popover-leave-to {
  opacity: 0;
  transform: translateY(4px) scale(0.98);
}
</style>
