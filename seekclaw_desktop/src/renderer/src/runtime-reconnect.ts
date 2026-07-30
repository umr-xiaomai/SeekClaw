import type { DaemonState } from '../../shared/ipc'

export const RUNTIME_RECONNECT_ATTEMPTS = 5

interface RetryOptions {
  attempts?: number
  delayMs?: number
  onAttempt?: (attempt: number, maximum: number) => void
  wait?: (milliseconds: number) => Promise<void>
}

const defaultWait = (milliseconds: number): Promise<void> =>
  new Promise((resolve) => globalThis.setTimeout(resolve, milliseconds))

export async function retryRuntimeConnection(
  connect: () => Promise<DaemonState>,
  options: RetryOptions = {}
): Promise<DaemonState> {
  const maximum = options.attempts ?? RUNTIME_RECONNECT_ATTEMPTS
  const delayMs = options.delayMs ?? 700
  const wait = options.wait ?? defaultWait
  let lastState: DaemonState = { connected: false, endpoint: '' }

  for (let attempt = 1; attempt <= maximum; attempt++) {
    options.onAttempt?.(attempt, maximum)
    try {
      lastState = await connect()
    } catch (error) {
      lastState = {
        connected: false,
        endpoint: lastState.endpoint,
        error: error instanceof Error ? error.message : String(error)
      }
    }
    if (lastState.connected) return lastState
    if (attempt < maximum) await wait(delayMs)
  }

  return lastState
}
