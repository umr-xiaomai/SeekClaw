<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Check, Eye, EyeOff, FolderOpen, Globe, LoaderCircle, PlugZap, X } from '@lucide/vue'
import type { SshAuthMode, SshConfigHostEntry, SshConnectionInfo, SshTestResult } from '../../../shared/ssh'

const props = defineProps<{
  open: boolean
  editing?: SshConnectionInfo | null
}>()

const emit = defineEmits<{
  close: []
  saved: [connection: SshConnectionInfo, allConnections: SshConnectionInfo[]]
}>()

const name = ref('')
const host = ref('')
const portText = ref('')
const authMode = ref<SshAuthMode>('none')
const identityFile = ref('')
const password = ref('')
const showPassword = ref(false)
const configFile = ref('')
const defaultConfigPath = ref('~/.ssh/config')
const configHosts = ref<SshConfigHostEntry[]>([])
const loadingHosts = ref(false)
const testing = ref(false)
const saving = ref(false)
const errorMessage = ref('')
const testResult = ref<SshTestResult | null>(null)

const isEditing = computed(() => Boolean(props.editing?.id))
const configDisplay = computed(() => configFile.value.trim() || '~/.ssh/config (默认)')

async function loadConfigHosts(targetFile?: string): Promise<void> {
  loadingHosts.value = true
  try {
    const res = await window.seekclaw.daemon.request('ssh.config.hosts', targetFile ? { configFile: targetFile } : {})
    const parsed = JSON.parse(res.data) as { configFile?: string; hosts?: SshConfigHostEntry[] }
    if (!targetFile && parsed.configFile) defaultConfigPath.value = parsed.configFile
    configHosts.value = Array.isArray(parsed.hosts) ? parsed.hosts : []
  } catch {
    configHosts.value = []
  } finally {
    loadingHosts.value = false
  }
}

function resetFromProps(): void {
  errorMessage.value = ''
  testResult.value = null
  showPassword.value = false
  if (props.editing) {
    name.value = props.editing.name || ''
    host.value = props.editing.host || ''
    portText.value = props.editing.port && props.editing.port !== 22 ? String(props.editing.port) : ''
    authMode.value = props.editing.authMode || 'none'
    identityFile.value = props.editing.identityFile || ''
    password.value = ''
    configFile.value = props.editing.configFile || ''
  } else {
    name.value = ''
    host.value = ''
    portText.value = ''
    authMode.value = 'none'
    identityFile.value = ''
    password.value = ''
    configFile.value = ''
  }
  void loadConfigHosts(configFile.value.trim() || undefined)
}

watch(
  () => [props.open, props.editing?.id] as const,
  ([open]) => {
    if (open) resetFromProps()
  },
  { immediate: true }
)

function parsedPort(): number {
  const numeric = Number(portText.value.trim())
  if (Number.isInteger(numeric) && numeric >= 1 && numeric <= 65535) return numeric
  return 22
}

function applyConfigHost(entry: SshConfigHostEntry): void {
  if (!name.value.trim()) name.value = entry.alias
  host.value = entry.alias
  if (entry.port && entry.port !== 22) portText.value = String(entry.port)
  else portText.value = ''
  if (entry.identityFile) {
    authMode.value = 'identityFile'
    identityFile.value = entry.identityFile
  } else {
    authMode.value = 'none'
  }
  testResult.value = null
  errorMessage.value = ''
}

async function pickIdentityFile(): Promise<void> {
  const files = await window.seekclaw.selectFiles()
  if (files[0]) identityFile.value = files[0]
}

async function pickConfigFile(): Promise<void> {
  const files = await window.seekclaw.selectFiles()
  if (files[0]) {
    configFile.value = files[0]
    await loadConfigHosts(files[0])
  }
}

async function testConnection(): Promise<void> {
  if (!host.value.trim()) {
    errorMessage.value = '请先输入主机名，例如 root@10.0.0.1'
    return
  }
  testing.value = true
  errorMessage.value = ''
  testResult.value = null
  try {
    const res = await window.seekclaw.daemon.request('ssh.test', {
      id: props.editing?.id,
      name: name.value.trim() || host.value.trim(),
      host: host.value.trim(),
      port: parsedPort(),
      authMode: authMode.value,
      identityFile: authMode.value === 'identityFile' ? identityFile.value.trim() : undefined,
      password: authMode.value === 'password' ? password.value : undefined,
      configFile: configFile.value.trim() || undefined
    }, { timeoutMs: 25_000 })
    testResult.value = JSON.parse(res.data) as SshTestResult
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : '测试 SSH 连接失败'
  } finally {
    testing.value = false
  }
}

async function handleSave(): Promise<void> {
  const cleanHost = host.value.trim()
  if (!cleanHost) {
    errorMessage.value = '请输入主机名，例如 host.com 或 user@host.com'
    return
  }
  if (authMode.value === 'identityFile' && !identityFile.value.trim()) {
    errorMessage.value = '请选择或输入 SSH 私钥身份文件路径'
    return
  }
  if (authMode.value === 'password' && !password.value && !props.editing?.passwordConfigured) {
    errorMessage.value = '请输入 SSH 登录密码'
    return
  }

  saving.value = true
  errorMessage.value = ''
  try {
    const res = await window.seekclaw.daemon.request('ssh.upsert', {
      id: props.editing?.id,
      name: name.value.trim() || cleanHost,
      host: cleanHost,
      port: parsedPort(),
      authMode: authMode.value,
      identityFile: authMode.value === 'identityFile' ? identityFile.value.trim() : undefined,
      password: authMode.value === 'password' && password.value ? password.value : undefined,
      configFile: configFile.value.trim() || undefined
    })
    const data = JSON.parse(res.data) as { connection: SshConnectionInfo; connections: SshConnectionInfo[] }
    emit('saved', data.connection, data.connections)
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : '保存 SSH 连接失败'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="modal-backdrop ssh-modal-backdrop" @click.self="emit('close')">
      <form class="ssh-dialog-card" role="dialog" aria-modal="true" :aria-label="isEditing ? '编辑 SSH 连接' : '添加 SSH 连接'" @submit.prevent="handleSave">
        <header class="ssh-dialog-header">
          <h3>{{ isEditing ? '编辑 SSH 连接' : '添加 SSH 连接' }}</h3>
          <button type="button" class="icon-button" aria-label="关闭" @click="emit('close')">
            <X :size="16" />
          </button>
        </header>

        <div class="ssh-dialog-body">
          <div class="ssh-field">
            <label>显示名称</label>
            <div class="ssh-prefixed-input">
              <span class="ssh-input-prefix"><Globe :size="15" /></span>
              <input v-model="name" type="text" placeholder="例如：生产服务器（留空默认使用主机名）" />
            </div>
          </div>

          <div class="ssh-field">
            <label>主机名</label>
            <input v-model="host" type="text" class="ssh-plain-input" placeholder="host.com 或 user@host.com" />
          </div>

          <div class="ssh-field">
            <label>SSH 端口 <span class="ssh-label-opt">(可选)</span></label>
            <input v-model="portText" type="text" inputmode="numeric" class="ssh-plain-input" placeholder="22" />
          </div>

          <div class="ssh-auth-segmented" role="tablist" aria-label="身份验证方式">
            <button
              type="button"
              class="ssh-auth-tab"
              :class="{ active: authMode === 'none' }"
              @click="authMode = 'none'"
            >
              无身份验证
            </button>
            <button
              type="button"
              class="ssh-auth-tab"
              :class="{ active: authMode === 'identityFile' }"
              @click="authMode = 'identityFile'"
            >
              身份文件
            </button>
            <button
              type="button"
              class="ssh-auth-tab"
              :class="{ active: authMode === 'password' }"
              @click="authMode = 'password'"
            >
              密码登录
            </button>
          </div>

          <div v-if="authMode === 'identityFile'" class="ssh-field">
            <label>私钥路径 (Identity File)</label>
            <div class="ssh-inline-row">
              <input v-model="identityFile" type="text" class="ssh-plain-input" placeholder="~/.ssh/id_ed25519 或选择本地密钥文件" />
              <button type="button" class="ssh-ghost-btn" @click="pickIdentityFile">
                <FolderOpen :size="14" />
                浏览...
              </button>
            </div>
          </div>

          <div v-else-if="authMode === 'password'" class="ssh-field">
            <label>登录密码</label>
            <div class="ssh-password-wrap">
              <input
                v-model="password"
                :type="showPassword ? 'text' : 'password'"
                class="ssh-plain-input"
                :placeholder="editing?.passwordConfigured ? '已保存密码（留空保持不变）' : '输入 SSH 登录密码'"
              />
              <button type="button" class="icon-button-subtle" @click="showPassword = !showPassword">
                <EyeOff v-if="showPassword" :size="14" />
                <Eye v-else :size="14" />
              </button>
            </div>
          </div>

          <div class="ssh-config-section">
            <div class="ssh-config-header">
              <span>SSH 配置：<code>{{ configDisplay }}</code></span>
              <button type="button" class="ssh-link-btn" @click="pickConfigFile">选择文件...</button>
            </div>
            <div v-if="loadingHosts" class="ssh-config-empty">正在读取 SSH 配置文件...</div>
            <div v-else-if="configHosts.length === 0" class="ssh-config-empty">
              <code>{{ configFile || '~/.ssh/config' }}</code> 中尚无主机，请在上方直接输入。
            </div>
            <div v-else class="ssh-config-hosts">
              <button
                v-for="item in configHosts"
                :key="item.alias"
                type="button"
                class="ssh-config-chip"
                :class="{ active: host === item.alias }"
                @click="applyConfigHost(item)"
              >
                <strong>{{ item.alias }}</strong>
                <span v-if="item.hostName">{{ item.user ? `${item.user}@${item.hostName}` : item.hostName }}</span>
              </button>
            </div>
          </div>

          <div v-if="testResult" class="ssh-feedback" :class="{ ok: testResult.ok, err: !testResult.ok }">
            <Check v-if="testResult.ok" :size="14" />
            <span>
              {{ testResult.ok ? `连接成功${testResult.remoteOs ? ` · ${testResult.remoteOs}` : ''}${testResult.remoteHome ? ` (${testResult.remoteHome})` : ''}` : testResult.message }}
            </span>
          </div>
          <div v-if="errorMessage" class="ssh-feedback err" role="alert">
            <span>{{ errorMessage }}</span>
          </div>
        </div>

        <footer class="ssh-dialog-footer">
          <button type="button" class="ssh-ghost-btn" :disabled="testing || saving" @click="testConnection">
            <LoaderCircle v-if="testing" :size="14" class="spin" />
            <PlugZap v-else :size="14" />
            {{ testing ? '测试中...' : '测试连接' }}
          </button>
          <div class="ssh-footer-right">
            <button type="button" class="ssh-text-btn" @click="emit('close')">取消</button>
            <button type="submit" class="ssh-dark-primary-btn" :disabled="saving || !host.trim()">
              {{ saving ? '保存中...' : '保存' }}
            </button>
          </div>
        </footer>
      </form>
    </div>
  </Teleport>
</template>
