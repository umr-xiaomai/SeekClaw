import type { DaemonMessage } from '../../shared/ipc'
import { hydrateMessages, type RuntimeSession } from './app-helpers'
import type { ChatMessage, SubAgentActivity } from './types'

export interface SubAgentConversationSelection {
  agent: SubAgentActivity
  parentSessionId: string
  workspace?: string
}

export interface SubAgentTaskSnapshot {
  id: string
  parent?: string
  sessionId: string
  role?: string
  model?: string | null
  status: SubAgentActivity['status']
  output?: string
  error?: string
}

export interface SubAgentConversationState {
  loading: boolean
  error: string
  task: SubAgentTaskSnapshot | null
  session: RuntimeSession | null
  messages: ChatMessage[]
  model?: string
  progress?: string
}

interface ConversationApi {
  request(method: string, params: Record<string, unknown>): Promise<{ data: string }>
  onEvent(listener: (event: DaemonMessage) => void): () => void
}

interface LiveMessage {
  message: ChatMessage
  /** The first possible raw message index for this uncommitted response. */
  after: number
}

export const emptySubAgentConversation = (): SubAgentConversationState => ({
  loading: false, error: '', task: null, session: null, messages: []
})

const terminal = (status?: string): boolean => ['completed', 'failed', 'cancelled', 'interrupted'].includes(status ?? '')
const stringValue = (value: unknown): string | undefined => typeof value === 'string' && value ? value : undefined

/** Read-only, parent-scoped projection. It never registers child sessions as sidebar tasks. */
export function createSubAgentConversationSource(api: ConversationApi, changed: (state: SubAgentConversationState) => void) {
  let state = emptySubAgentConversation()
  let selection: SubAgentConversationSelection | null = null
  let generation = 0
  let serial = 0
  let disposed = false
  let history: ChatMessage[] = []
  let live: LiveMessage[] = []
  let active: LiveMessage | undefined
  let streamModel: string | undefined
  let pending: Promise<void> | undefined
  let trailing = false
  let timer: ReturnType<typeof setTimeout> | undefined

  function publish(): void {
    state = { ...state, messages: [...history, ...live.map((item) => ({ ...item.message }))] }
    changed(state)
  }

  function reconcile(session: RuntimeSession): void {
    const consumed = new Set<number>()
    live = live.filter((item) => {
      const index = session.messages.findIndex((saved, index) =>
        index >= item.after && !consumed.has(index) && saved.role === 'assistant'
        && (Boolean(item.message.content) || Boolean(item.message.thinking))
        && saved.text === item.message.content
        && (!item.message.thinking || saved.thinking === item.message.thinking))
      if (index < 0) return true
      consumed.add(index)
      return false
    })
    if (active && !live.includes(active)) active = undefined
  }

  function newLive(): LiveMessage {
    const item: LiveMessage = {
      after: state.session?.messages.length ?? 0,
      message: { id: `subagent-live:${generation}:${++serial}`, role: 'assistant', content: '',
        thinking: '', modelRef: state.model, state: 'thinking', createdAt: Date.now() }
    }
    live.push(item)
    active = item
    return item
  }

  function schedule(): void {
    if (timer || disposed || !selection) return
    timer = setTimeout(() => { timer = undefined; void refresh() }, 180)
  }

  async function refresh(): Promise<void> {
    if (disposed || !selection) return
    if (pending) { trailing = true; return pending }
    const current = selection
    const token = generation
    const scope = current.workspace ? { workspace: current.workspace } : { global: true }
    state = { ...state, loading: !state.session, error: '' }
    publish()
    const work = (async () => {
      try {
        const response = await api.request('subagent.status', {
          ...scope, sessionId: current.parentSessionId, taskId: current.agent.id
        })
        const task = JSON.parse(response.data) as SubAgentTaskSnapshot
        if (!task || task.id !== current.agent.id || !task.sessionId
          || (task.parent && task.parent !== current.parentSessionId))
          throw new Error('子 Agent 记录与当前对话不匹配。')
        if (disposed || token !== generation) return
        const saved = await api.request('session.get', { ...scope, id: task.sessionId })
        const session = JSON.parse(saved.data) as RuntimeSession
        if (!session || session.id !== task.sessionId || !Array.isArray(session.messages)
          || (session.parentSessionId && session.parentSessionId !== current.parentSessionId))
          throw new Error('无法读取子 Agent 的对话记录。')
        if (disposed || token !== generation) return
        reconcile(session)
        if (terminal(task.status)) { live = []; active = undefined }
        const model = [...session.messages].reverse().find((message) => message.role === 'assistant' && message.modelRef)?.modelRef
        state = { ...state, task, session, model: streamModel ?? model ?? task.model ?? current.agent.model, error: '' }
        // Hydrate only when a persisted snapshot changes; token events update the
        // small live tail without reparsing the entire child tool history.
        history = hydrateMessages(session)
        if (!terminal(task.status)) {
          const completedCalls = new Set(session.messages.filter((message) => message.role === 'tool').map((message) => message.toolCallId))
          for (const message of history) for (const tool of message.tools ?? []) {
            if (tool.callId && !completedCalls.has(tool.callId)) tool.state = 'running'
          }
        }
      } catch (reason) {
        if (disposed || token !== generation) return
        state = { ...state, error: reason instanceof Error ? reason.message : String(reason) }
      } finally {
        if (!disposed && token === generation) {
          pending = undefined
          state = { ...state, loading: false }
          publish()
          if (trailing) { trailing = false; schedule() }
        }
      }
    })()
    pending = work
    return work
  }

  const unsubscribe = api.onEvent((event) => {
    if (disposed || !selection) return
    const details = event.details ?? {}
    const parent = event.event === 'subagent.updated' ? details.parentSessionId : event.sessionId
    const id = event.event === 'subagent.updated' ? details.taskId : details.subAgentId
    if (parent !== selection.parentSessionId || id !== selection.agent.id) return
    if (event.event !== 'subagent.updated' && selection.agent.requestId !== undefined
      && event.id !== selection.agent.requestId) return
    if (event.event === 'subagent_progress') {
      const kind = stringValue(details.kind)
      const delta = typeof details.delta === 'string' ? details.delta : ''
      if (kind === 'model') {
        if (active) active.message.state = 'done'
        active = undefined
        streamModel = delta || streamModel
        state = { ...state, model: delta || state.model }
        schedule()
      } else if (kind === 'stream_reset') {
        if (active) live = live.filter((item) => item !== active)
        active = undefined
        streamModel = delta || streamModel
        state = { ...state, model: delta || state.model }
      } else if (kind === 'text' || kind === 'thinking') {
        const item = active ?? newLive()
        if (kind === 'text') { item.message.content += delta; item.message.state = 'streaming' }
        else { item.message.thinking = (item.message.thinking ?? '') + delta; item.message.state = 'thinking' }
      } else if (kind === 'message_done') {
        if (active || delta) {
          const item = active ?? newLive()
          item.message.content = delta
          item.message.state = 'done'
        }
        schedule()
      } else {
        if (kind === 'tool_start' && active) { active.message.state = 'done'; active = undefined }
        state = { ...state, progress: event.data }
        schedule()
      }
      publish()
    } else if (['subagent_start', 'subagent_done', 'subagent.updated'].includes(event.event)) {
      if (event.event === 'subagent_done' && active) active.message.state = 'done'
      schedule()
    }
  })

  return {
    select(next: SubAgentConversationSelection): Promise<void> {
      generation++
      if (timer) clearTimeout(timer)
      timer = undefined
      pending = undefined
      trailing = false
      selection = next
      history = []
      live = []
      active = undefined
      streamModel = undefined
      state = { ...emptySubAgentConversation(), model: next.agent.model, progress: next.agent.progress }
      publish()
      return refresh()
    },
    refresh,
    dispose(): void {
      disposed = true
      generation++
      if (timer) clearTimeout(timer)
      unsubscribe()
    }
  }
}
