import { computed, onBeforeUnmount, onMounted, shallowRef, watch } from 'vue'
import { createStreamingText } from './streaming-text'

/** Keep animation state local to the view; the conversation always retains the full text. */
export function useStreamingText(content: () => string, streaming: () => boolean) {
  const displayed = shallowRef(content())
  const reducedMotion = shallowRef(false)
  const hidden = shallowRef(false)
  const animate = computed(() => streaming() && !reducedMotion.value && !hidden.value)
  let controller: ReturnType<typeof createStreamingText> | undefined
  let motionQuery: MediaQueryList | undefined

  function updateMotion(): void {
    reducedMotion.value = motionQuery?.matches ?? false
  }

  function updateVisibility(): void {
    hidden.value = document.visibilityState === 'hidden'
  }

  watch([content, animate], ([text, enabled]) => {
    if (controller) controller.update(text, enabled)
    else displayed.value = text
  }, { flush: 'sync' })

  onMounted(() => {
    motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
    updateMotion()
    updateVisibility()
    controller = createStreamingText({
      initialText: content(),
      onText: (text) => { displayed.value = text },
      now: () => performance.now(),
      requestFrame: (callback) => requestAnimationFrame(callback),
      cancelFrame: (handle) => cancelAnimationFrame(handle)
    })
    motionQuery.addEventListener('change', updateMotion)
    document.addEventListener('visibilitychange', updateVisibility)
  })

  onBeforeUnmount(() => {
    controller?.dispose()
    motionQuery?.removeEventListener('change', updateMotion)
    document.removeEventListener('visibilitychange', updateVisibility)
  })

  return displayed
}
