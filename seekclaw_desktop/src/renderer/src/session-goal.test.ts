import { describe, expect, it, vi } from 'vitest'
import { createSessionGoalSync, parseSessionGoal, type SessionGoal } from './session-goal'

const active: SessionGoal = { id: 'goal', objective: 'Verify parser', criteria: 'Tests pass', status: 'active' }
const paused: SessionGoal = { ...active, status: 'paused', pauseReason: 'Paused by the user' }
const complete: SessionGoal = { ...active, status: 'complete', progress: 'Tests passed' }

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: Error) => void
  const promise = new Promise<T>((done, failed) => { resolve = done; reject = failed })
  return { promise, resolve, reject }
}

function setup() {
  const owner = { sessionId: 'session' }
  let selected: typeof owner | undefined = owner
  let state: SessionGoal | null = null
  const read = vi.fn<(_: typeof owner) => Promise<unknown>>().mockResolvedValue(active)
  const changed = vi.fn((value: SessionGoal | null) => { state = value })
  const sync = createSessionGoalSync({ activeOwner: () => selected, read, changed })
  return { sync, read, changed, owner, state: () => state,
    select: (next: typeof owner | undefined) => { selected = next; sync.clear() } }
}

describe('saved conversation goal synchronization', () => {
  it('re-reads a delayed old turn notification after pause instead of restoring its old active state', async () => {
    const test = setup()
    await test.sync.refresh()
    test.read.mockResolvedValue(paused)
    await test.sync.mutate(test.owner, async () => paused)
    expect(test.state()).toEqual(paused)

    // An earlier goal.updated(active) was queued by the turn before goal.pause committed.
    // The payload is deliberately not consumed; its session only invalidates the saved read.
    await test.sync.changed('session')

    expect(test.read).toHaveBeenCalledTimes(3)
    expect(test.state()).toEqual(paused)
    expect(test.changed.mock.calls.slice(1)).toEqual([[paused], [paused]])
  })

  it('ignores an active read that finishes after the paused state has been loaded', async () => {
    const test = setup()
    const old = deferred<unknown>()
    test.read.mockReturnValueOnce(old.promise)
    const initial = test.sync.refresh()
    test.read.mockResolvedValue(paused)
    await test.sync.mutate(test.owner, async () => paused)
    old.resolve(active)
    await initial

    expect(test.state()).toEqual(paused)
    expect(test.changed).toHaveBeenCalledExactlyOnceWith(paused)
  })

  it('ignores a delayed management reply when a newer goal is already persisted', async () => {
    const test = setup()
    const pauseReply = deferred<unknown>()
    const pause = test.sync.mutate(test.owner, () => pauseReply.promise)
    test.read.mockResolvedValue(complete)
    await test.sync.changed('session')
    expect(test.state()).toEqual(complete)
    pauseReply.resolve(paused)
    await pause

    expect(test.state()).toEqual(complete)
    expect(test.changed.mock.calls).toEqual([[complete], [complete]])
  })

  it('invalidates old reads even when the user switches away and back to the same owner', async () => {
    const test = setup()
    const old = deferred<unknown>()
    test.read.mockReturnValueOnce(old.promise)
    const pending = test.sync.refresh()
    test.select({ sessionId: 'other' })
    test.select(test.owner)
    test.read.mockResolvedValue(paused)
    await test.sync.refresh()
    old.resolve(active)
    await pending

    expect(test.state()).toEqual(paused)
    expect(test.changed.mock.calls).toEqual([[null], [null], [paused]])
  })

  it('does not let background notifications or a background mutation supersede the selected session read', async () => {
    const test = setup()
    const saved = deferred<unknown>()
    test.read.mockReturnValueOnce(saved.promise)
    const pending = test.sync.refresh()
    const other = { sessionId: 'other' }
    await test.sync.changed(other.sessionId)
    await test.sync.changed(undefined)
    await test.sync.mutate(other, async () => paused)
    await test.sync.refresh(other)
    saved.resolve(active)
    await pending

    expect(test.read).toHaveBeenCalledExactlyOnceWith(test.owner)
    expect(test.state()).toEqual(active)
  })

  it('refreshes after a failed mutation, reports its error and retains known state when a read fails', async () => {
    const test = setup()
    await test.sync.refresh()
    test.read.mockResolvedValue(paused)
    const failure = new Error('Goal changed')
    await expect(test.sync.mutate(test.owner, async () => { throw failure })).rejects.toBe(failure)
    expect(test.state()).toEqual(paused)

    test.read.mockRejectedValue(new Error('Disconnected'))
    await test.sync.changed('session')
    expect(test.state()).toEqual(paused)

    test.read.mockResolvedValue(null)
    await test.sync.refresh()
    expect(test.state()).toBeNull()
  })

  it('rejects incomplete goals and preserves only supported optional fields', () => {
    expect(parseSessionGoal({ ...active, status: 'blocked' })).toBeNull()
    expect(parseSessionGoal({ id: 'goal', status: 'active' })).toBeNull()
    expect(parseSessionGoal({ ...paused, progress: 12 })).toEqual(paused)
  })
})
