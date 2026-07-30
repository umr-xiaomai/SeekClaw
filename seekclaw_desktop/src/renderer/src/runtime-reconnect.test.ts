import { describe, expect, it, vi } from 'vitest'
import type { DaemonState } from '../../shared/ipc'
import { retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'

const offline = (error = 'offline'): DaemonState => ({ connected: false, endpoint: 'test', error })

describe('retryRuntimeConnection', () => {
  it('stops after five failed attempts', async () => {
    const connect = vi.fn(async () => offline())
    const wait = vi.fn(async () => undefined)

    const result = await retryRuntimeConnection(connect, { wait })

    expect(result.connected).toBe(false)
    expect(connect).toHaveBeenCalledTimes(RUNTIME_RECONNECT_ATTEMPTS)
    expect(wait).toHaveBeenCalledTimes(RUNTIME_RECONNECT_ATTEMPTS - 1)
  })

  it('returns immediately after a successful attempt', async () => {
    const states = [offline(), offline(), { connected: true, endpoint: 'test' } satisfies DaemonState]
    const connect = vi.fn(async () => states.shift() ?? offline())

    const result = await retryRuntimeConnection(connect, { wait: async () => undefined })

    expect(result.connected).toBe(true)
    expect(connect).toHaveBeenCalledTimes(3)
  })
})
