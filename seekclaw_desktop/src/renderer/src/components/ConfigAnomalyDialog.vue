<script setup lang="ts">
import { AlertTriangle, Database, FileText, LoaderCircle, RefreshCw, X } from '@lucide/vue'

defineProps<{
  open: boolean
  detail?: string
  configFile?: string
  backupFile?: string
  rebuilding?: boolean
}>()

const emit = defineEmits<{
  close: []
  rebuild: []
}>()
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div v-if="open" class="modal-backdrop config-anomaly-backdrop" @mousedown.self="!rebuilding && emit('close')">
        <section class="config-anomaly-dialog" role="alertdialog" aria-modal="true" aria-labelledby="config-anomaly-title">
          <header class="config-anomaly-header">
            <div class="config-anomaly-icon">
              <AlertTriangle :size="24" />
            </div>
            <div class="config-anomaly-title-area">
              <h2 id="config-anomaly-title">检测到配置文件异常</h2>
              <p>SeekClaw 无法正常读取主配置文件，现已自动切换至安全默认状态。</p>
            </div>
            <button
              v-if="!rebuilding"
              class="icon-button"
              type="button"
              title="稍后处理"
              @click="emit('close')"
            >
              <X :size="18" />
            </button>
          </header>

          <div class="config-anomaly-body">
            <div class="anomaly-notice-box">
              <p>为防止您的 API 密钥与自定义配置丢失，系统已对异常文件进行安全备份。建议立即重新初始化数据库与配置文件以恢复全部功能。</p>
            </div>

            <div v-if="detail" class="anomaly-error-detail">
              <span class="detail-label">异常原因：</span>
              <code>{{ detail }}</code>
            </div>

            <div class="anomaly-file-info">
              <div v-if="configFile" class="file-row">
                <FileText :size="14" />
                <span class="file-label">原配置文件：</span>
                <span class="file-path" :title="configFile">{{ configFile }}</span>
              </div>
              <div v-if="backupFile" class="file-row backup-row">
                <Database :size="14" />
                <span class="file-label">自动备份文件：</span>
                <span class="file-path" :title="backupFile">{{ backupFile }}</span>
              </div>
            </div>
          </div>

          <footer class="config-anomaly-footer">
            <button
              class="secondary-button"
              type="button"
              :disabled="rebuilding"
              @click="emit('close')"
            >
              稍后手动检查
            </button>
            <button
              class="secondary-button danger-button"
              type="button"
              :disabled="rebuilding"
              @click="emit('rebuild')"
            >
              <LoaderCircle v-if="rebuilding" class="spin" :size="15" />
              <RefreshCw v-else :size="15" />
              {{ rebuilding ? '正在重建…' : '重建数据库和配置文件' }}
            </button>
          </footer>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>
