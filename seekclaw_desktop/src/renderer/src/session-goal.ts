export interface SessionGoal {
  id: string
  objective: string
  criteria: string
  status: 'active' | 'paused' | 'complete'
  progress?: string
  pauseReason?: string
}

export function parseSessionGoal(value: unknown): SessionGoal | null {
  if (!value || typeof value !== 'object') return null
  const goal = value as Record<string, unknown>
  if (typeof goal.id !== 'string' || typeof goal.objective !== 'string' || typeof goal.criteria !== 'string'
    || (goal.status !== 'active' && goal.status !== 'paused' && goal.status !== 'complete')) return null
  return {
    id: goal.id,
    objective: goal.objective,
    criteria: goal.criteria,
    status: goal.status,
    progress: typeof goal.progress === 'string' ? goal.progress : undefined,
    pauseReason: typeof goal.pauseReason === 'string' ? goal.pauseReason : undefined
  }
}

interface GoalOwner { sessionId?: string }

interface SessionGoalSyncOptions<Owner extends GoalOwner> {
  activeOwner: () => Owner | undefined
  read: (owner: Owner) => Promise<unknown>
  changed: (goal: SessionGoal | null) => void
}

/** Turn events and management replies can arrive out of order. Only saved reads update the card. */
export function createSessionGoalSync<Owner extends GoalOwner>(options: SessionGoalSyncOptions<Owner>) {
  let version = 0

  async function refresh(owner = options.activeOwner()): Promise<void> {
    if (!owner?.sessionId || owner !== options.activeOwner()) return
    const readVersion = ++version
    try {
      const saved = await options.read(owner)
      if (readVersion === version && owner === options.activeOwner())
        options.changed(parseSessionGoal(saved))
    } catch { /* Reconnects and later invalidations retry the saved read. */ }
  }

  function clear(): void {
    version++
    options.changed(null)
  }

  async function changed(sessionId: string | undefined): Promise<void> {
    const owner = options.activeOwner()
    if (sessionId && owner?.sessionId === sessionId) await refresh(owner)
  }

  async function mutate(owner: Owner, action: () => Promise<unknown>): Promise<void> {
    // A read begun before the mutation must never overwrite its committed state.
    if (owner === options.activeOwner()) version++
    try { await action() }
    finally { await refresh(owner) }
  }

  return { refresh, clear, changed, mutate }
}
