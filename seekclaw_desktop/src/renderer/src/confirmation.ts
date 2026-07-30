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

let settle: ((result: boolean) => void) | undefined

export function confirmAction(options: ConfirmationOptions): Promise<boolean> {
  settle?.(false)
  Object.assign(confirmationState, {
    open: true,
    title: options.title,
    message: options.message,
    confirmLabel: options.confirmLabel ?? '确认',
    danger: options.danger ?? false
  })
  return new Promise<boolean>((resolve) => {
    settle = resolve
  })
}

export function settleConfirmation(result: boolean): void {
  confirmationState.open = false
  settle?.(result)
  settle = undefined
}
