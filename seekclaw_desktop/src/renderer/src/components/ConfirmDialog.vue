<script setup lang="ts">
import { CircleAlert, X } from '@lucide/vue'
import { confirmationState, settleConfirmation } from '../confirmation'
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="confirmationState.open" class="modal-backdrop confirmation-backdrop" @mousedown.self="settleConfirmation(false)">
      <section class="confirmation-dialog" role="alertdialog" aria-modal="true" aria-labelledby="confirmation-title">
        <header>
          <span class="confirmation-icon" :class="{ danger: confirmationState.danger }"><CircleAlert :size="21" /></span>
          <div>
            <h2 id="confirmation-title">{{ confirmationState.title }}</h2>
            <p>{{ confirmationState.message }}</p>
          </div>
          <button class="icon-button" title="关闭" @click="settleConfirmation(false)"><X :size="18" /></button>
        </header>
        <footer>
          <button class="secondary-button" @click="settleConfirmation(false)">取消</button>
          <button
            class="secondary-button primary-action"
            :class="{ 'confirm-danger': confirmationState.danger }"
            @click="settleConfirmation(true)"
          >{{ confirmationState.confirmLabel }}</button>
        </footer>
      </section>
    </div>
  </Transition>
</template>
