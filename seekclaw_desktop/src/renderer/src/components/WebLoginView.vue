<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { KeyRound, ShieldCheck } from '@lucide/vue'
import { loginWebWithToken } from '../web-bridge'

const emit = defineEmits<{
  authenticated: []
}>()

const tokenInput = ref('')
const submitting = ref(false)
const errorMessage = ref('')
const inputRef = ref<HTMLInputElement | null>(null)

onMounted(() => {
  inputRef.value?.focus()
})

async function handleSubmit(): Promise<void> {
  const token = tokenInput.value.trim()
  if (!token || submitting.value) return
  submitting.value = true
  errorMessage.value = ''
  try {
    const result = await loginWebWithToken(token)
    if (result.ok) {
      tokenInput.value = ''
      emit('authenticated')
      return
    }
    errorMessage.value = result.error || '访问口令无效，请重试'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '连接服务器失败'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="web-login-shell">
    <form class="web-login-card" @submit.prevent="handleSubmit">
      <div class="web-login-brand">
        <span class="web-login-icon">
          <ShieldCheck :size="24" />
        </span>
        <div>
          <h1>SeekClaw 局域网访问</h1>
          <p>已连接到宿主机桌面端，请输入访问口令继续使用全部功能。</p>
        </div>
      </div>

      <label class="web-login-field">
        <span>访问口令 (Access Token)</span>
        <div class="web-login-input-wrap">
          <KeyRound :size="16" />
          <input
            ref="inputRef"
            v-model="tokenInput"
            type="password"
            autocomplete="current-password"
            placeholder="在桌面端「设置 → 局域网访问」中查看"
            :disabled="submitting"
          />
        </div>
      </label>

      <div v-if="errorMessage" class="web-login-error" role="alert">
        {{ errorMessage }}
      </div>

      <button type="submit" class="web-login-submit" :disabled="submitting || !tokenInput.trim()">
        {{ submitting ? '正在验证...' : '进入工作台' }}
      </button>
    </form>
  </div>
</template>
