<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { Check, ChevronDown, Folder, FolderPlus, Globe, Laptop, Plus, X } from '@lucide/vue'
import { formatSshWorkspacePath, type SshConnectionInfo } from '../../../shared/ssh'
import { pathName } from '../app-helpers'
import SshFolderPickerDialog from './SshFolderPickerDialog.vue'

const props = defineProps<{
  open: boolean
  sshConnections: SshConnectionInfo[]
  initialSshId?: string | null
}>()

const emit = defineEmits<{
  close: []
  create: [payload: { name: string; path: string }]
  addSsh: []
}>()

const projectName = ref('')
const selectedHostId = ref<string | null>(null)
const selectedFolderPath = ref('')
const hostMenuOpen = ref(false)
const hostTriggerRef = ref<HTMLButtonElement | null>(null)
const hostMenuRef = ref<HTMLElement | null>(null)
const sshFolderPickerOpen = ref(false)

const activeSsh = computed(() =>
  selectedHostId.value ? props.sshConnections.find((item) => item.id === selectedHostId.value) ?? null : null
)

const hostDisplayName = computed(() =>
  activeSsh.value ? (activeSsh.value.name || activeSsh.value.host) : '此电脑'
)

watch(
  () => props.open,
  (open) => {
    if (!open) {
      hostMenuOpen.value = false
      sshFolderPickerOpen.value = false
      return
    }
    projectName.value = ''
    selectedFolderPath.value = ''
    selectedHostId.value = props.initialSshId ?? null
    hostMenuOpen.value = false
  }
)

watch(
  () => props.sshConnections,
  (list) => {
    if (selectedHostId.value && !list.some((item) => item.id === selectedHostId.value)) {
      selectedHostId.value = null
      selectedFolderPath.value = ''
    }
  }
)

function selectHost(id: string | null): void {
  if (selectedHostId.value !== id) {
    selectedHostId.value = id
    selectedFolderPath.value = ''
  }
  hostMenuOpen.value = false
}

function handleOutsidePointer(event: MouseEvent): void {
  const target = event.target as Node | null
  if (!target || !hostMenuOpen.value) return
  if (hostTriggerRef.value?.contains(target) || hostMenuRef.value?.contains(target)) return
  hostMenuOpen.value = false
}

watch(hostMenuOpen, (open) => {
  if (open) window.addEventListener('pointerdown', handleOutsidePointer)
  else window.removeEventListener('pointerdown', handleOutsidePointer)
})

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', handleOutsidePointer)
})

async function handlePickFolder(): Promise<void> {
  if (activeSsh.value) {
    sshFolderPickerOpen.value = true
    return
  }
  const picked = await window.seekclaw.selectWorkspace()
  if (!picked) return
  selectedFolderPath.value = picked
  if (!projectName.value.trim()) {
    projectName.value = pathName(picked)
  }
}

function handleSshFolderPicked(remotePath: string): void {
  sshFolderPickerOpen.value = false
  if (!activeSsh.value) return
  const uri = formatSshWorkspacePath(activeSsh.value.id, remotePath)
  selectedFolderPath.value = uri
  if (!projectName.value.trim()) {
    const inferred = pathName(uri)
    projectName.value = inferred === '~' || inferred === '/' ? activeSsh.value.name : inferred
  }
}

function submitProject(): void {
  if (!selectedFolderPath.value) return
  const finalName = projectName.value.trim() || pathName(selectedFolderPath.value)
  emit('create', { name: finalName, path: selectedFolderPath.value })
}
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="modal-backdrop ssh-modal-backdrop" @click.self="emit('close')">
      <form class="create-project-dialog-card" role="dialog" aria-modal="true" aria-label="创建项目" @submit.prevent="submitProject">
        <header class="ssh-dialog-header">
          <h3>创建项目</h3>
          <button type="button" class="icon-button" aria-label="关闭" @click="emit('close')">
            <X :size="16" />
          </button>
        </header>

        <div class="create-project-body">
          <div class="ssh-prefixed-input">
            <span class="ssh-input-prefix"><Folder :size="15" /></span>
            <input v-model="projectName" type="text" placeholder="项目名称" autofocus />
          </div>

          <div class="create-project-field-label">源文件夹</div>

          <div class="create-project-source-card">
            <div class="create-project-host-anchor">
              <button
                ref="hostTriggerRef"
                type="button"
                class="create-project-host-trigger"
                :aria-expanded="hostMenuOpen"
                @click="hostMenuOpen = !hostMenuOpen"
              >
                <span>在</span>
                <strong>{{ hostDisplayName }}</strong>
                <span>上添加文件夹</span>
                <ChevronDown :size="14" />
              </button>

              <div v-if="hostMenuOpen" ref="hostMenuRef" class="create-project-host-menu" role="menu">
                <button
                  type="button"
                  class="create-project-host-item"
                  :class="{ active: !selectedHostId }"
                  @click="selectHost(null)"
                >
                  <Laptop :size="15" />
                  <span>此电脑</span>
                  <Check v-if="!selectedHostId" :size="14" class="create-project-host-check" />
                </button>

                <div class="create-project-host-group">远程设备</div>

                <div v-if="sshConnections.length === 0" class="create-project-host-empty">
                  未连接任何远程主机
                </div>
                <button
                  v-for="conn in sshConnections"
                  :key="conn.id"
                  type="button"
                  class="create-project-host-item"
                  :class="{ active: selectedHostId === conn.id }"
                  @click="selectHost(conn.id)"
                >
                  <Globe :size="15" />
                  <span>{{ conn.name || conn.host }}</span>
                  <Check v-if="selectedHostId === conn.id" :size="14" class="create-project-host-check" />
                </button>

                <button
                  type="button"
                  class="create-project-host-item add-remote"
                  @click="hostMenuOpen = false; emit('addSsh')"
                >
                  <Plus :size="15" />
                  <span>添加远程主机</span>
                </button>
              </div>
            </div>

            <div v-if="selectedFolderPath" class="create-project-selected-path">
              <code>{{ selectedFolderPath }}</code>
              <button type="button" class="ssh-ghost-btn compact" @click="handlePickFolder">更换</button>
            </div>
            <button v-else type="button" class="create-project-add-btn" @click="handlePickFolder">
              <FolderPlus :size="15" />
              <span>添加</span>
            </button>
          </div>
        </div>

        <footer class="create-project-footer">
          <button type="button" class="ssh-text-btn" @click="emit('close')">取消</button>
          <button type="submit" class="ssh-dark-primary-btn" :disabled="!selectedFolderPath">创建项目</button>
        </footer>
      </form>
    </div>

    <SshFolderPickerDialog
      :open="sshFolderPickerOpen"
      :connection="activeSsh"
      @close="sshFolderPickerOpen = false"
      @select="handleSshFolderPicked"
    />
  </Teleport>
</template>
