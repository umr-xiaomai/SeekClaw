import type { DaemonMessage } from '../shared/ipc.js'

const TERMINAL_EVENTS = new Set(['done', 'cancelled', 'error'])

/**
 * Keeps undecided approval requests available across renderer reloads. The
 * renderer subscription is process-local, so an approval event emitted while
 * the window is reloading would otherwise be lost until the runtime timeout.
 */
export class PendingApprovalCache {
  private readonly entries = new Map<string, DaemonMessage>()

  remember(message: DaemonMessage): void {
    if (message.event !== 'approval_request') return
    const approvalId = message.details?.approvalId
    if (typeof approvalId !== 'string' || approvalId.length === 0) return
    this.entries.set(approvalId, message)
  }

  acknowledge(approvalId: unknown): void {
    if (typeof approvalId === 'string' && approvalId.length > 0) this.entries.delete(approvalId)
  }

  finish(message: DaemonMessage): void {
    if (!TERMINAL_EVENTS.has(message.event)) return
    for (const [approvalId, pending] of this.entries) {
      if (pending.id === message.id) this.entries.delete(approvalId)
    }
  }

  snapshot(): DaemonMessage[] {
    return [...this.entries.values()]
  }

  clear(): void {
    this.entries.clear()
  }
}
