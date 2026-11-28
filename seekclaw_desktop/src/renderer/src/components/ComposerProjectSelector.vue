<script setup lang="ts">
import { Check, Folder, Globe, Laptop, Plus, Search, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { isSshWorkspacePath, parseSshWorkspacePath, type SshConnectionInfo } from '../../../shared/ssh'
import type { ProjectItem } from '../types'
import { pathName } from '../app-helpers'

const props = withDefaults(defineProps<{
  projects: ProjectItem[]
  activeProject?: ProjectItem | null
  sshConnections?: SshConnectionInfo[]
  activeSshId?: string | null
  isGlobalExplicit?: boolean
  disabled?: boolean
}>(), {
  activeProject: null,
  sshConnections: () => [],
  activeSshId: null,
  isGlobalExplicit: false,
  disabled: false
})

const emit = defineEmits<{
  selectProject: [project: ProjectItem]
  selectGlobal: []
  createProject: []
  selectSsh: [id: string | null]
  addSsh: []
}>()

const isOpen = ref(false)
const searchQuery = ref('')
const triggerRef = ref<HTMLButtonElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)
const searchInputRef = ref<HTMLInputElement | null>(null)
const menuStyle = ref<Record<string, string>>({})

const isEnvOpen = ref(false)
const envSearchQuery = ref('')
const envTriggerRef = ref<HTMLButtonElement | null>(null)
const envMenuRef = ref<HTMLElement | null>(null)
const envSearchInputRef = ref<HTMLInputElement | null>(null)
const envMenuStyle = ref<Record<string, string>>({})
const envPlacement = ref<'above' | 'below'>('above')

const activeSsh = computed(() =>
  props.activeSshId ? props.sshConnections.find((item) => item.id === props.activeSshId) ?? null : null
)

const envLabel = computed(() =>
  activeSsh.value ? (activeSsh.value.name || activeSsh.value.host) : '此计算机'
)

const showLocalEnvOption = computed(() => {
  const q = envSearchQuery.value.trim().toLowerCase()
  if (!q) return true
  return '此计算机'.includes(q) || '本地'.includes(q) || 'local'.includes(q) || 'computer'.includes(q)
})

const filteredSshConnections = computed(() => {
  const q = envSearchQuery.value.trim().toLowerCase()
  if (!q) return props.sshConnections
  return props.sshConnections.filter((conn) =>
    conn.name.toLowerCase().includes(q) || conn.host.toLowerCase().includes(q)
  )
})

const buttonLabel = computed(() => {
  if (props.activeProject) return props.activeProject.name
  return '选择项目'
})

const filteredProjects = computed(() => {
  const q = searchQuery.value.trim().toLowerCase()
  const locationMatches = props.projects.filter((p) => {
    const ssh = parseSshWorkspacePath(p.path)
    if (props.activeSshId) return ssh?.connectionId === props.activeSshId
    return !ssh
  })
  if (!q) return locationMatches
  return locationMatches.filter((p) =>
    p.name.toLowerCase().includes(q) ||
    p.path.toLowerCase().includes(q)
  )
})

function updatePosition(): void {
  if (!triggerRef.value) return
  const rect = triggerRef.value.getBoundingClientRect()
  const gap = 8
  const width = Math.min(300, Math.max(260, window.innerWidth - 24))
  const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12))
  const bottom = window.innerHeight - rect.top + gap
  const maxHeight = Math.min(400, Math.max(160, rect.top - gap - 12))
  menuStyle.value = {
    position: 'fixed',
    left: `${left}px`,
    bottom: `${bottom}px`,
    width: `${width}px`,
    maxHeight: `${maxHeight}px`,
    zIndex: '1000'
  }
}

function updateEnvPosition(): void {
  if (!envTriggerRef.value) return
  const rect = envTriggerRef.value.getBoundingClientRect()
  const gap = 6
  const width = Math.min(280, Math.max(240, window.innerWidth - 24))
  const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12))
  const measuredHeight = envMenuRef.value?.offsetHeight || 220
  const spaceBelow = window.innerHeight - rect.bottom - gap - 12
  const spaceAbove = rect.top - gap - 12
  const placeBelow = spaceBelow >= measuredHeight || spaceBelow >= spaceAbove
  envPlacement.value = placeBelow ? 'below' : 'above'

  if (placeBelow) {
    const maxHeight = Math.min(360, Math.max(140, spaceBelow))
    envMenuStyle.value = {
      position: 'fixed',
      left: `${left}px`,
      top: `${rect.bottom + gap}px`,
      width: `${width}px`,
      maxHeight: `${maxHeight}px`,
      zIndex: '1000'
    }
  } else {
    const maxHeight = Math.min(360, Math.max(140, spaceAbove))
    envMenuStyle.value = {
      position: 'fixed',
      left: `${left}px`,
      bottom: `${window.innerHeight - rect.top + gap}px`,
      width: `${width}px`,
      maxHeight: `${maxHeight}px`,
      zIndex: '1000'
    }
  }
}

function openMenu(): void {
  if (props.disabled) return
  isEnvOpen.value = false
  isOpen.value = true
  searchQuery.value = ''
  void nextTick(() => {
    updatePosition()
    searchInputRef.value?.focus()
    requestAnimationFrame(() => {
      if (isOpen.value) updatePosition()
    })
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

function openEnvMenu(): void {
  if (props.disabled) return
  isOpen.value = false
  isEnvOpen.value = true
  envSearchQuery.value = ''
  void nextTick(() => {
    updateEnvPosition()
    envSearchInputRef.value?.focus()
    requestAnimationFrame(() => {
      if (isEnvOpen.value) updateEnvPosition()
    })
  })
}

watch([envSearchQuery, () => props.sshConnections?.length ?? 0], () => {
  if (isEnvOpen.value) {
    void nextTick(updateEnvPosition)
  }
})

function closeEnvMenu(restoreFocus = false): void {
  isEnvOpen.value = false
  if (restoreFocus) {
    void nextTick(() => envTriggerRef.value?.focus())
  }
}

function toggleEnvMenu(): void {
  if (isEnvOpen.value) closeEnvMenu()
  else openEnvMenu()
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

function onSelectSsh(id: string | null): void {
  emit('selectSsh', id)
  closeEnvMenu(true)
}

function onAddSsh(): void {
  closeEnvMenu(false)
  emit('addSsh')
}

function handleOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node | null
  if (!target) return
  if (isOpen.value && !triggerRef.value?.contains(target) && !menuRef.value?.contains(target)) {
    closeMenu()
  }
  if (isEnvOpen.value && !envTriggerRef.value?.contains(target) && !envMenuRef.value?.contains(target)) {
    closeEnvMenu()
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    closeMenu(true)
    return
  }
  const items = Array.from(menuRef.value?.querySelectorAll<HTMLButtonElement>('.project-menu-item') ?? [])
  if (items.length === 0) return
  const currentIndex = items.findIndex((el) => el === document.activeElement)
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    const nextIdx = currentIndex >= 0 ? (currentIndex + 1) % items.length : 0
    items[nextIdx]?.focus()
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    if (currentIndex <= 0 && event.target !== searchInputRef.value) {
      searchInputRef.value?.focus()
    } else {
      const prevIdx = currentIndex >= 0 ? (currentIndex - 1 + items.length) % items.length : items.length - 1
      items[prevIdx]?.focus()
    }
  } else if (event.key === 'Home') {
    event.preventDefault()
    items[0]?.focus()
  } else if (event.key === 'End') {
    event.preventDefault()
    items[items.length - 1]?.focus()
  }
}

function handleEnvKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    closeEnvMenu(true)
    return
  }
  const items = Array.from(envMenuRef.value?.querySelectorAll<HTMLButtonElement>('.project-menu-item') ?? [])
  if (items.length === 0) return
  const currentIndex = items.findIndex((el) => el === document.activeElement)
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    const nextIdx = currentIndex >= 0 ? (currentIndex + 1) % items.length : 0
    items[nextIdx]?.focus()
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    if (currentIndex <= 0 && event.target !== envSearchInputRef.value) {
      envSearchInputRef.value?.focus()
    } else {
      const prevIdx = currentIndex >= 0 ? (currentIndex - 1 + items.length) % items.length : items.length - 1
      items[prevIdx]?.focus()
    }
  }
}

function handleWindowResize(): void {
  if (isOpen.value) updatePosition()
  if (isEnvOpen.value) updateEnvPosition()
}

watch([isOpen, isEnvOpen], ([projOpen, envOpen]) => {
  if (projOpen || envOpen) {
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
      ref="envTriggerRef"
      type="button"
      class="composer-context-pill composer-env-btn"
      :disabled="disabled"
      :title="activeSsh ? `SSH 远程服务器 · ${activeSsh.host}` : '在当前电脑上执行'"
      :aria-label="`选择工作位置：${envLabel}`"
      aria-haspopup="dialog"
      :aria-expanded="isEnvOpen"
      @click="toggleEnvMenu"
    >
      <Globe v-if="activeSsh" :size="15" aria-hidden="true" />
      <Laptop v-else :size="15" aria-hidden="true" />
      <span>{{ envLabel }}</span>
    </button>
    <button
      ref="triggerRef"
      type="button"
      class="composer-context-pill composer-project-btn"
      :class="{ selected: Boolean(activeProject) }"
      :disabled="disabled"
      :title="activeProject ? `${activeProject.name} · ${activeProject.path}` : '选择项目'"
      :aria-label="`选择项目：${buttonLabel}`"
      aria-haspopup="dialog"
      :aria-expanded="isOpen"
      @click="toggleMenu"
    >
      <Folder :size="15" class="composer-project-icon" />
      <span class="composer-project-name">{{ buttonLabel }}</span>
    </button>
  </div>

  <Teleport to="body">
    <Transition name="select-popover">
      <div
        v-if="isEnvOpen"
        ref="envMenuRef"
        class="composer-project-popover composer-env-popover"
        :data-placement="envPlacement"
        :style="envMenuStyle"
        role="dialog"
        aria-label="选择工作位置"
        @keydown="handleEnvKeydown"
      >
        <div class="project-search-row">
          <Search :size="14" class="project-search-icon" />
          <input
            ref="envSearchInputRef"
            v-model="envSearchQuery"
            type="text"
            class="project-search-input"
            placeholder="工作位置"
            aria-label="工作位置"
          />
        </div>

        <div class="project-list-scroll">
          <button
            v-if="showLocalEnvOption"
            type="button"
            class="project-menu-item"
            :class="{ active: !activeSshId }"
            :aria-pressed="!activeSshId"
            @click="onSelectSsh(null)"
          >
            <Laptop :size="15" class="project-item-icon" />
            <span class="project-item-name">此计算机</span>
            <Check v-if="!activeSshId" :size="15" class="project-item-check" />
          </button>

          <div class="env-group-label">远程设备</div>

          <button
            v-for="conn in filteredSshConnections"
            :key="conn.id"
            type="button"
            class="project-menu-item"
            :class="{ active: activeSshId === conn.id }"
            :aria-pressed="activeSshId === conn.id"
            :title="conn.host"
            @click="onSelectSsh(conn.id)"
          >
            <Globe :size="15" class="project-item-icon" />
            <span class="project-item-name">{{ conn.name || conn.host }}</span>
            <span v-if="conn.name && conn.name !== conn.host" class="project-item-sub">{{ conn.host }}</span>
            <Check v-if="activeSshId === conn.id" :size="15" class="project-item-check" />
          </button>

          <div v-if="filteredSshConnections.length === 0 && !envSearchQuery.trim()" class="env-empty-note">
            未连接任何远程主机
          </div>
          <div v-else-if="filteredSshConnections.length === 0 && !showLocalEnvOption" class="project-empty-item">
            未找到匹配位置
          </div>
        </div>

        <div class="project-popover-divider" />

        <div class="project-popover-footer">
          <button
            type="button"
            class="project-menu-item project-action-item"
            @click="onAddSsh"
          >
            <Plus :size="16" class="project-item-icon" />
            <span class="project-item-name">添加远程主机</span>
          </button>
        </div>
      </div>
    </Transition>
  </Teleport>

  <Teleport to="body">
    <Transition name="select-popover">
      <div
        v-if="isOpen"
        ref="menuRef"
        class="composer-project-popover"
        data-placement="above"
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

        <div class="project-list-wrap" :class="{ 'has-overflow': filteredProjects.length > 4 }">
          <div class="project-list-scroll">
            <button
              v-for="project in filteredProjects"
              :key="project.id"
              type="button"
              class="project-menu-item"
              :class="{ active: activeProject?.id === project.id }"
              :aria-pressed="activeProject?.id === project.id"
              :title="project.path"
              @click="onSelectProject(project)"
            >
              <Globe v-if="isSshWorkspacePath(project.path)" :size="15" class="project-item-icon" />
              <Folder v-else :size="15" class="project-item-icon" />
              <span class="project-item-name">{{ project.name }}</span>
              <span
                v-if="pathName(project.path) !== project.name"
                class="project-item-sub"
              >
                {{ pathName(project.path) }}
              </span>
              <Check v-if="activeProject?.id === project.id" :size="15" class="project-item-check" />
            </button>

            <div v-if="filteredProjects.length === 0" class="project-empty-item">
              未找到匹配项目
            </div>
          </div>
        </div>

        <div class="project-popover-divider" />

        <div class="project-popover-footer">
          <button
            type="button"
            class="project-menu-item project-action-item"
            @click="onCreateProject"
          >
            <Plus :size="16" class="project-item-icon" />
            <span class="project-item-name">新建项目</span>
          </button>
          <button
            v-if="activeProject"
            type="button"
            class="project-menu-item project-action-item project-clear-item"
            @click="onSelectGlobal"
          >
            <X :size="16" class="project-item-icon" />
            <span class="project-item-name">不在项目中工作</span>
          </button>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.composer-project-bar {
  display: flex;
  align-items: center;
  width: 100%;
  min-width: 0;
  gap: 6px;
  position: relative;
  z-index: 1;
}

.composer-context-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  min-height: 28px;
  padding: 4px 12px;
  font-size: 13px;
  font-weight: 500;
  background: transparent;
  border: none;
  border-radius: 999px;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
  user-select: none;
  white-space: nowrap;
}

.composer-context-pill:hover:not(:disabled),
.composer-context-pill[aria-expanded="true"] {
  background: var(--surface-hover);
  color: var(--text);
}

.composer-context-pill:focus-visible,
.project-menu-item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.composer-context-pill:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.composer-env-btn {
  flex: none;
  color: var(--text);
}

.composer-project-btn {
  flex: 0 1 auto;
  max-width: 100%;
  color: var(--text-muted);
}

.composer-project-btn.selected {
  color: var(--text);
}

.composer-project-icon {
  flex-shrink: 0;
}

.composer-project-name {
  min-width: 0;
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.composer-project-popover {
  position: fixed;
  width: 300px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 18px;
  box-shadow: 0 12px 32px rgb(0 0 0 / 10%), 0 2px 8px rgb(0 0 0 / 5%);
  z-index: 1000;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.project-search-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 44px;
  padding: 10px 14px;
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

.project-list-wrap {
  position: relative;
  min-height: 0;
}

.project-list-wrap.has-overflow::after {
  content: '';
  position: absolute;
  right: 12px;
  bottom: 0;
  left: 0;
  height: 22px;
  pointer-events: none;
  background: linear-gradient(to bottom, transparent, var(--surface-raised));
}

.project-list-scroll {
  flex: 1 1 auto;
  min-height: 0;
  max-height: 204px;
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
  min-height: 36px;
  width: 100%;
  padding: 8px 12px;
  font-size: 13.5px;
  border: none;
  background: transparent;
  border-radius: 11px;
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
  color: var(--text);
}

.project-item-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.project-menu-item.active .project-item-icon {
  color: var(--text-secondary);
}

.project-item-name {
  min-width: 0;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.project-item-sub {
  min-width: 0;
  flex: 0 1 auto;
  color: var(--text-muted);
  font-size: 12.5px;
  margin-left: 2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.project-menu-item.active .project-item-sub {
  color: var(--text-muted);
}

.project-item-check {
  flex-shrink: 0;
  margin-left: auto;
  color: var(--text);
}

.project-popover-divider {
  height: 1px;
  background: var(--border);
  margin: 0 12px;
}

.project-popover-footer {
  padding: 6px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.project-action-item {
  color: var(--text);
}

.project-empty-item {
  padding: 16px 12px;
  font-size: 12px;
  color: var(--text-muted);
  text-align: center;
}

.env-group-label {
  padding: 6px 12px 2px;
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-muted);
}

.env-empty-note {
  padding: 6px 12px 8px;
  font-size: 12.5px;
  color: var(--text-muted);
}
</style>
