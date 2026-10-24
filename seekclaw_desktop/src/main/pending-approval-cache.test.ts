import { describe, expect, it } from 'vitest'
import type { DaemonMessage } from '../shared/ipc.js'
import { PendingApprovalCache } from './pending-approval-cache.js'

function approval(approvalId: string, requestId = 7): DaemonMessage {
  return {
    id: requestId,
    event: 'approval_request',
    data: 'bash',
    sessionId: 'session-1',
    details: { approvalId }
  }
}

describe('PendingApprovalCache', () => {
  it('replays undecided approvals after a renderer reload', () => {
    const cache = new PendingApprovalCache()
    cache.remember(approval('7:call-1'))

    expect(cache.snapshot()).toEqual([approval('7:call-1')])
  })

  it('removes an approval as soon as the renderer answers it', () => {
    const cache = new PendingApprovalCache()
    cache.remember(approval('7:call-1'))
    cache.acknowledge('7:call-1')

    expect(cache.snapshot()).toEqual([])
  })

  it('drops approvals when their turn reaches a terminal event', () => {
    const cache = new PendingApprovalCache()
    cache.remember(approval('7:call-1'))
    cache.remember(approval('8:call-2', 8))
    cache.finish({ id: 7, event: 'cancelled', data: '' })

    expect(cache.snapshot()).toEqual([approval('8:call-2', 8)])
  })

  it('ignores malformed and unrelated events', () => {
    const cache = new PendingApprovalCache()
    cache.remember({ id: 1, event: 'approval_request', data: '' })
    cache.remember({ id: 2, event: 'status', data: 'Thinking' })

    expect(cache.snapshot()).toEqual([])
  })
})
