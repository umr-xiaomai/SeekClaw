<script setup lang="ts">
import { ArrowLeft, ArrowRight, PanelLeft } from '@lucide/vue'
import { onBeforeUnmount, onMounted, ref } from 'vue'

type MenuName = 'file' | 'edit' | 'view' | 'help'
type MenuAction =
  | 'newTask'
  | 'openWorkspace'
  | 'showProject'
  | 'openSettings'
  | 'focusComposer'
  | 'toggleSidebar'
  | 'openTerminal'
  | 'openGitChanges'
  | 'openGitHistory'
  | 'openDiagnostics'
  | 'openAbout'

interface MenuItem {
  label?: string
  shortcut?: string
  action?: MenuAction
  href?: string
  separator?: boolean
  requiresProject?: boolean
}

const props = defineProps<{
  sidebarOpen: boolean
  projectPath?: string
}>()
const emit = defineEmits<{
  toggleSidebar: []
  newTask: []
  openWorkspace: []
  showProject: []
  openSettings: []
  focusComposer: []
  openTerminal: []
  openGitChanges: []
  openGitHistory: []
  openDiagnostics: []
  openAbout: []
}>()

const menuRoot = ref<HTMLElement | null>(null)
const activeMenu = ref<MenuName | null>(null)
const menus: Array<{ id: MenuName; label: string; items: MenuItem[] }> = [
  {
    id: 'file',
    label: '文件',
    items: [
      { label: '新建任务', shortcut: 'Ctrl+N', action: 'newTask' },
      { label: '打开文件夹…', shortcut: 'Ctrl+O', action: 'openWorkspace' },
      { separator: true },
      { label: '在资源管理器中显示', action: 'showProject', requiresProject: true },
      { separator: true },
      { label: '设置', shortcut: 'Ctrl+,', action: 'openSettings' }
    ]
  },
  {
    id: 'edit',
    label: '编辑',
    items: [
      { label: '聚焦输入框', shortcut: 'Ctrl+L', action: 'focusComposer' }
    ]
  },
  {
    id: 'view',
    label: '视图',
    items: [
      { label: '切换侧栏', shortcut: 'Ctrl+B', action: 'toggleSidebar' },
      { label: '打开终端', shortcut: 'Ctrl+`', action: 'openTerminal', requiresProject: true },
      { separator: true },
      { label: 'Git 变更', action: 'openGitChanges', requiresProject: true },
      { label: 'Git 历史', action: 'openGitHistory', requiresProject: true }
    ]
  },
  {
    id: 'help',
    label: '帮助',
    items: [
      { label: '文档', href: 'https://seekclaw.hoilai.com/doc/' },
      { label: '故障排查', action: 'openDiagnostics' },
      { separator: true },
      { label: '关于 SeekClaw', action: 'openAbout' }
    ]
  }
]

function toggleMenu(menu: MenuName): void {
  activeMenu.value = activeMenu.value === menu ? null : menu
}

function switchOpenMenu(menu: MenuName): void {
  if (activeMenu.value) activeMenu.value = menu
}

function runAction(action?: MenuAction): void {
  activeMenu.value = null
  switch (action) {
    case 'newTask': emit('newTask'); break
    case 'openWorkspace': emit('openWorkspace'); break
    case 'showProject': emit('showProject'); break
    case 'openSettings': emit('openSettings'); break
    case 'focusComposer': emit('focusComposer'); break
    case 'toggleSidebar': emit('toggleSidebar'); break
    case 'openTerminal': emit('openTerminal'); break
    case 'openGitChanges': emit('openGitChanges'); break
    case 'openGitHistory': emit('openGitHistory'); break
    case 'openDiagnostics': emit('openDiagnostics'); break
    case 'openAbout': emit('openAbout'); break
  }
}

function activateItem(item: MenuItem): void {
  if (item.requiresProject && !props.projectPath) return
  activeMenu.value = null
  if (item.href) {
    window.open(item.href, '_blank', 'noopener,noreferrer')
    return
  }
  runAction(item.action)
}

function handlePointerDown(event: PointerEvent): void {
  if (activeMenu.value && !menuRoot.value?.contains(event.target as Node)) activeMenu.value = null
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    activeMenu.value = null
    return
  }
  if (document.querySelector('[aria-modal="true"]')) return
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return

  const shortcuts: Record<string, MenuAction> = {
    n: 'newTask',
    o: 'openWorkspace',
    ',': 'openSettings',
    l: 'focusComposer',
    b: 'toggleSidebar',
    '`': 'openTerminal'
  }
  const action = shortcuts[event.key.toLocaleLowerCase()]
  if (!action || (action === 'openTerminal' && !props.projectPath)) return
  event.preventDefault()
  runAction(action)
}

onMounted(() => {
  document.addEventListener('pointerdown', handlePointerDown)
  document.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', handlePointerDown)
  document.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <header class="titlebar">
    <div class="titlebar-actions no-drag">
      <button class="icon-button" title="切换侧栏" @click="emit('toggleSidebar')">
        <PanelLeft :size="18" />
      </button>
      <button class="icon-button is-muted" title="后退" disabled>
        <ArrowLeft :size="18" />
      </button>
      <button class="icon-button is-muted" title="前进" disabled>
        <ArrowRight :size="18" />
      </button>
    </div>
    <nav ref="menuRoot" class="app-menu no-drag" aria-label="应用菜单">
      <div
        v-for="menu in menus"
        :key="menu.id"
        class="app-menu-group"
        @mouseenter="switchOpenMenu(menu.id)"
      >
        <button
          class="app-menu-trigger"
          :class="{ active: activeMenu === menu.id }"
          :aria-expanded="activeMenu === menu.id"
          aria-haspopup="menu"
          @click="toggleMenu(menu.id)"
        >
          {{ menu.label }}
        </button>
        <Transition name="app-menu-popover">
          <div v-if="activeMenu === menu.id" class="app-menu-popup" role="menu">
            <template v-for="(item, index) in menu.items" :key="`${menu.id}-${index}`">
              <div v-if="item.separator" class="app-menu-separator" />
              <button
                v-else
                class="app-menu-item"
                role="menuitem"
                :disabled="item.requiresProject && !projectPath"
                @click="activateItem(item)"
              >
                <span>{{ item.label }}</span>
                <kbd v-if="item.shortcut">{{ item.shortcut }}</kbd>
              </button>
            </template>
          </div>
        </Transition>
      </div>
    </nav>
    <div class="drag-fill" />
    <span v-if="projectPath" class="titlebar-name" :title="projectPath">{{ projectPath }}</span>
    <div class="window-controls-space" />
  </header>
</template>
