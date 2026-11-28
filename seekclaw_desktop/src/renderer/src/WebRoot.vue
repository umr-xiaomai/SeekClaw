<script setup lang="ts">
import { onMounted, ref } from 'vue'
import App from './App.vue'
import WebLoginView from './components/WebLoginView.vue'
import {
  checkWebAuthSession,
  installWebBridge,
  resetWebBridgeConnection,
  setWebUnauthorizedHandler,
  tryConsumeUrlToken
} from './web-bridge'

const isDesktopEnv = typeof window.seekclaw !== 'undefined'
const authReady = ref(isDesktopEnv)
const authenticated = ref(isDesktopEnv)

async function initWebMode(): Promise<void> {
  if (isDesktopEnv) return
  setWebUnauthorizedHandler(() => {
    resetWebBridgeConnection()
    authenticated.value = false
  })
  await tryConsumeUrlToken().catch(() => false)
  const session = await checkWebAuthSession().catch(() => ({ authenticated: false }))
  if (session.authenticated) {
    installWebBridge()
    authenticated.value = true
  } else {
    authenticated.value = false
  }
  authReady.value = true
}

function handleAuthenticated(): void {
  installWebBridge()
  authenticated.value = true
}

onMounted(() => {
  if (!isDesktopEnv) {
    void initWebMode()
  }
})
</script>

<template>
  <App v-if="authenticated" />
  <WebLoginView v-else-if="authReady" @authenticated="handleAuthenticated" />
  <div v-else class="web-boot-loading">正在连接 SeekClaw...</div>
</template>
