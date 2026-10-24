import { reactive } from 'vue'

export interface ConfirmationOptions {
  title: string
  message: string
  confirmLabel?: string
  danger?: boolean
}

export const confirmationState = reactive({
  open: false,
  title: '',
  message: '',
  confirmLabel: '确认',
  danger: false
})

interface PendingConfirmation {
  options: ConfirmationOptions
  resolve: (result: boolean) => void
}

const queue: PendingConfirmation[] = []
let active: PendingConfirmation | undefined

function showNext(): void {
  if (active || queue.length === 0) return
  active = queue.shift()
  if (!active) return
  Object.assign(confirmationState, {
    open: true,
    title: active.options.title,
    message: active.options.message,
    confirmLabel: active.options.confirmLabel ?? '确认',
    danger: active.options.danger ?? false
  })
}

export function confirmAction(options: ConfirmationOptions): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    queue.push({ options, resolve })
    showNext()
  })
}

export function settleConfirmation(result: boolean): void {
  const current = active
  if (!current) return
  active = undefined
  confirmationState.open = false
  current.resolve(result)
  showNext()
}
