import type { Ref } from 'vue'
import type { DaemonMessage } from '../../shared/ipc'
import type { ChatMessage, ProjectItem, SubAgentActivity, ThreadItem } from './types'
import { finalizeAssistantBubbles } from './conversation-state'
import { makeId, phaseLabel } from './app-helpers'
import { parseConversationWidgets } from './conversation-widgets'
import { applySubAgentEvent, messageSubAgents } from './subagent-activity'

export interface SubAgentStatusSnapshot {
  status: SubAgentActivity['status']
  output?: string
  childSessionId?: string
}

export interface DaemonEventContext {
  threads: Ref<ThreadItem[]>
  activeThreadId: Ref<string>
  projects: Ref<ProjectItem[]>
  handleScheduleUpdated: () => Promise<void> | void
  scrollToBottom: (smooth?: boolean, force?: boolean) => Promise<void>
  reloadThreadSession: (thread: ThreadItem, project?: ProjectItem) => Promise<void>
  rememberFinishedRequest: (thread: ThreadItem, requestId: number) => void
  isFinishedRequest: (thread: ThreadItem, requestId: number) => boolean
  scheduleQueuedDrain: (thread: ThreadItem) => void
  reloadBackgroundThreadIfIdle: (thread: ThreadItem) => void
  /** Broadcasts have no run identity; verify the current durable child before applying them. */
  readSubAgentStatus?: (thread: ThreadItem, taskId: string) => Promise<SubAgentStatusSnapshot>
}

export function createDaemonEventHandler(context: DaemonEventContext): (event: DaemonMessage) => void {
  const {
    threads,
    activeThreadId,
    projects,
    handleScheduleUpdated,
    scrollToBottom,
    reloadThreadSession,
    rememberFinishedRequest,
    isFinishedRequest,
    scheduleQueuedDrain,
    reloadBackgroundThreadIfIdle
  } = context

  function appendModelError(message: ChatMessage, detail: string): void {
    const normalized = detail.trim() || 'Unknown model error'
    message.errorMessage = normalized
    message.state = 'error'
  }

  function latestChild(thread: ThreadItem, id: string): SubAgentActivity | undefined {
    return thread.messages.flatMap((message) => message.subAgents ?? []).findLast((agent) => agent.id === id)
      ?? thread.messages.flatMap(messageSubAgents).findLast((agent) => agent.id === id)
  }

  async function applyChildBroadcast(thread: ThreadItem, event: DaemonMessage, id: string): Promise<void> {
    const activity = latestChild(thread, id)
    if (!activity) return
    const sessionId = thread.sessionId
    const runId = activity.runId
    const requestId = activity.requestId
    const sourceToolId = activity.sourceToolId
    let verified = event
    if (context.readSubAgentStatus) {
      try {
        const snapshot = await context.readSubAgentStatus(thread, id)
        // A new send or a reloaded/switched session can supersede this lookup.
        const owner = threads.value.find((item) => item.id === thread.id && item.sessionId === sessionId)
        const current = owner && latestChild(owner, id)
        if (!owner || !current || current.runId !== runId || current.requestId !== requestId || current.sourceToolId !== sourceToolId) return
        thread = owner
        verified = { ...event, details: { ...event.details, status: snapshot.status, output: snapshot.output, childSessionId: snapshot.childSessionId } }
      } catch {
        // Keep the known state when the invalidation cannot be verified.
        return
      }
    } else if (activity.runId || activity.requestId !== undefined) {
      // Legacy consumers can update recovered snapshots, but an unversioned
      // terminal notification must not settle a live instruction.
      return
    }
    if (applySubAgentEvent(thread.messages, verified, undefined, false)
      && thread.id === activeThreadId.value) void scrollToBottom()
  }

  return function handleDaemonEvent(event: DaemonMessage): void {
    if (event.event === 'session.title') {
      const sessionId = event.details?.sessionId
      const thread = typeof sessionId === 'string' ? threads.value.find((item) => item.sessionId === sessionId) : undefined
      if (thread && thread.titleSource !== 'manual') {
        thread.titleGenerating = event.details?.generating === true
        const title = typeof event.details?.title === 'string' ? event.details.title.trim() : ''
        if (title) {
          thread.title = title
          thread.titleSource = 'generated'
        }
      } else if (thread && event.details?.generating !== true) thread.titleGenerating = false
      return
    }
    if (event.event === 'schedule.upcoming') return
    if (event.event === 'schedule.updated') {
      void handleScheduleUpdated()
      return
    }
    // Durable child tasks can finish after the parent chat request has ended.
    // Route their broadcast by the owning session, never by the active chat.
    if (event.event === 'subagent.updated') {
      const parentSessionId = event.details?.parentSessionId
      const owner = typeof parentSessionId === 'string'
        ? threads.value.find((item) => item.sessionId === parentSessionId) : undefined
      const id = event.details?.taskId
      if (owner && typeof id === 'string') void applyChildBroadcast(owner, event, id)
      return
    }
    const isChatRequest = event.requestMethod === 'chat'
      || event.requestMethod === 'agent.runTurn'
      || event.requestMethod === 'agent/runTurn'
    // The steer request only acknowledges enqueueing (or reports that the turn
    // just finished). Its error/result must never be interpreted as the active
    // chat turn's terminal state.
    if (event.requestMethod === 'agent.steer') return
    if (!isChatRequest && !event.sessionId) return
    const thread = (event.sessionId
      ? threads.value.find((item) => item.sessionId === event.sessionId)
      : undefined)
      ?? threads.value.find((item) => item.requestId === event.id)
      ?? (!event.sessionId && activeThreadId.value && threads.value.find((item) => item.id === activeThreadId.value)?.running
        ? threads.value.find((item) => item.id === activeThreadId.value)
        : undefined)
    if (!thread) return

    if (event.event === 'subagent_start' || event.event === 'subagent_progress' || event.event === 'subagent_done') {
      const currentRequest = !isFinishedRequest(thread, event.id)
        && (!isChatRequest || thread.requestId === undefined || thread.requestId === event.id)
      // Completion may race the parent's terminal response. It may update an
      // existing child, but a stale request must not create a new activity.
      if (!currentRequest && event.event !== 'subagent_done') return
      const current = currentRequest ? thread.messages.find((item) => item.id === thread.assistantId) : undefined
      if (applySubAgentEvent(thread.messages, event, current, currentRequest)
        && thread.id === activeThreadId.value) void scrollToBottom()
      return
    }

    // Guidance is drained by the Agent and forwarded under the chat request id, so it
    // can arrive after the turn's terminal response already resolved (IPC race). It
    // must be applied before the stale-request guards below: the optimistic guidance
    // message is already rendered, and its pending counter always needs releasing.
    if (event.event === 'steer') {
      thread.pendingGuidance = Math.max(0, (thread.pendingGuidance ?? 1) - 1)
      const currentAssistant = thread.messages.find((item) => item.id === thread.assistantId)
      if (currentAssistant) {
        currentAssistant.state = 'done'
        currentAssistant.thinkingActive = false
        currentAssistant.preparingTool = undefined
        const nextAssistant: ChatMessage = {
          id: makeId(),
          role: 'assistant',
          content: '',
          thinking: '',
          tools: [],
          state: 'thinking',
          thinkingActive: false,
          createdAt: Date.now()
        }
        thread.messages.push(nextAssistant)
        thread.assistantId = nextAssistant.id
        // The user may have scrolled away since submitting the guidance.
        if (thread.id === activeThreadId.value) void scrollToBottom()
      } else if (!thread.running && thread.sessionId) {
        void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
      }
      if (isChatRequest) thread.requestId ??= event.id
      return
    }

    // A terminal response can reach the request continuation before its renderer
    // event callback. If the next queued turn has already started, ignore all
    // delayed events belonging to the completed request — but still finalize any
    // leftover "..." placeholder bubbles, otherwise a dropped terminal event
    // leaves them on screen forever.
    // Timeout failures remove the pending request from DaemonClient, so late daemon
    // events no longer carry requestMethod. The finished-id guard must therefore be
    // independent of that metadata or a timed-out turn can still mutate the UI.
    if (isFinishedRequest(thread, event.id)) {
      if (event.event === 'done' || event.event === 'cancelled')
        finalizeAssistantBubbles(thread.messages, 'done')
      else if (event.event === 'error')
        finalizeAssistantBubbles(thread.messages, 'error')
      return
    }
    if (isChatRequest && thread.requestId !== undefined && thread.requestId !== event.id) return
    // Steering acknowledgements have their own request id and must never replace
    // the id of the active chat turn (used by cancellation and stale-event checks).
    if (isChatRequest) thread.requestId ??= event.id
    const message = thread.messages.find((item) => item.id === thread.assistantId)
    const terminalEvent = event.event === 'done' || event.event === 'cancelled' || event.event === 'error'
    if (!message && !terminalEvent) return
    const isBackgroundThread = thread.id !== activeThreadId.value
    const eventCallId = typeof event.details?.callId === 'string' ? event.details.callId : undefined
    const findTool = () => {
      if (eventCallId) {
        for (let i = thread.messages.length - 1; i >= 0; i--) {
          const m = thread.messages[i]
          if (m?.role === 'assistant' && m.tools) {
            const found = m.tools.find((tool) => tool.callId === eventCallId || tool.id === eventCallId)
            if (found) return found
          }
        }
      }
      return message?.tools?.findLast((tool) => tool.state === 'running')
        ?? thread.messages.findLast((m) => m.role === 'assistant')?.tools?.findLast((tool) => tool.state === 'running')
    }

    switch (event.event) {
      case 'provider_retry':
        thread.phase = `连接中断，正在重试（${Number(event.details?.attempt) || 1}）`
        break
      case 'stream_reset': {
        if (!message) break
        const textLength = Math.max(0, Number(event.details?.textLength) || 0)
        const thinkingLength = Math.max(0, Number(event.details?.thinkingLength) || 0)
        message.content = message.content.slice(0, Math.max(0, message.content.length - textLength))
        const thinking = message.thinking ?? ''
        message.thinking = thinking.slice(0, Math.max(0, thinking.length - thinkingLength))
        message.errorMessage = undefined
        message.modelRef = event.data || message.modelRef
        message.state = 'thinking'
        message.thinkingActive = false
        message.preparingTool = undefined
        break
      }
      case 'thinking':
        if (!message) break
        message.thinking = (message.thinking ?? '') + event.data
        message.state = 'thinking'
        message.thinkingActive = true
        message.preparingTool = undefined
        break
      case 'thinking_done':
        if (!message) break
        message.thinkingActive = false
        if (message.state === 'thinking') message.state = 'streaming'
        break
      case 'delta':
        if (!message) break
        message.content += event.data
        message.state = 'streaming'
        message.thinkingActive = false
        message.preparingTool = undefined
        break
      case 'status': {
        const lower = event.data.toLocaleLowerCase()
        if (lower.includes('preparing tool call')) {
          if (message) {
            const detail = typeof event.details?.detail === 'string' && event.details.detail.trim()
              ? event.details.detail.trim()
              : 'tool'
            message.preparingTool = detail
            message.thinkingActive = false
            if (message.state === 'thinking') message.state = 'streaming'
          }
        } else if (message && lower.includes('thinking') && message.state !== 'streaming') {
          message.state = 'thinking'
        }
        thread.phase = phaseLabel(event.data)
        break
      }
      case 'warning': {
        const warnText = event.data || '警告'
        if (message && !message.content.includes(warnText)) {
          message.content += `${message.content ? '\n\n' : ''}> [!WARNING]\n> ${warnText}`
        }
        break
      }
      case 'image_view': {
        if (!message) break
        const imageId = typeof event.details?.imageId === 'string' ? event.details.imageId : ''
        if (!imageId) break
        message.viewedImages ??= []
        if (!message.viewedImages.some((image) => image.id === imageId))
          message.viewedImages.push({ id: imageId, name: event.data || '图片' })
        break
      }
      case 'tool_start': {
        if (!message) break
        message.preparingTool = undefined
        message.thinkingActive = false
        thread.phase = '执行工具'
        message.tools ??= []
        message.tools.push({
          id: eventCallId ?? `${event.id}-${message.tools.length}`,
          callId: eventCallId,
          name: event.data,
          detail: typeof event.details?.summary === 'string' ? event.details.summary : undefined,
          input: typeof event.details?.argumentsJson === 'string' ? event.details.argumentsJson : undefined,
          state: 'running'
        })
        break
      }
      case 'tool_done': {
        if (!message) break
        const running = findTool()
        if (running) {
          running.state = event.details?.success === false ? 'error' : 'done'
          running.output = typeof event.details?.output === 'string' ? event.details.output : event.data
          running.content = Array.isArray(event.details?.content)
            ? event.details.content as Array<Record<string, unknown>> : undefined
          running.structuredContent = event.details?.structuredContent ?? undefined
          running.widgets = parseConversationWidgets(event.details?.widgets)
          const metadata = event.details?.metadata
          running.metadata = metadata != null && typeof metadata === 'object' && !Array.isArray(metadata)
            ? metadata as Record<string, unknown> : undefined
          if (!running.detail && event.data) {
            running.detail = event.data
          }
        }
        break
      }
      case 'file_diff': {
        if (!message) break
        const tool = findTool()
        if (tool) {
          const diff = typeof event.details?.diff === 'string' ? event.details.diff : ''
          tool.filePath = event.data
          tool.diff = diff
          tool.addedLines = diff.split(/\r?\n/).filter((line) => line.startsWith('+') && !line.startsWith('+++')).length
          tool.removedLines = diff.split(/\r?\n/).filter((line) => line.startsWith('-') && !line.startsWith('---')).length
        }
        break
      }
      case 'model_start': {
        thread.stats ??= {}
        thread.stats.llmRounds = (thread.stats.llmRounds ?? 0) + 1
        const step = Number(event.details?.step) || 0
        const modelRef = event.data || (event.details?.provider && event.details?.model ? `${event.details.provider}/${event.details.model}` : undefined)
        if (message && modelRef && !message.modelRef) message.modelRef = modelRef
        const previousHasOutput = Boolean(
          message?.content || message?.thinking || (message?.tools?.length ?? 0))
        if (message && step > 1 && previousHasOutput) {
          message.state = 'done'
          message.thinkingActive = false
          message.preparingTool = undefined
          const nextAssistant: ChatMessage = {
            id: makeId(),
            role: 'assistant',
            content: '',
            thinking: '',
            tools: [],
            modelRef,
            state: 'thinking',
            thinkingActive: false,
            createdAt: Date.now()
          }
          thread.messages.push(nextAssistant)
          thread.assistantId = nextAssistant.id
        }
        break
      }
      case 'usage': {
        thread.stats ??= {}
        const inputTokens = Number(event.details?.inputTokens) || 0
        const outputTokens = Number(event.details?.outputTokens) || 0
        const totalInputTokens = Number(event.details?.totalInputTokens) || inputTokens
        const cachedInputTokens = Number(event.details?.cachedInputTokens) || 0
        const elapsedMs = Number(event.details?.elapsedMs) || 0
        thread.stats.inputTokens = (thread.stats.inputTokens ?? 0) + inputTokens
        thread.stats.outputTokens = (thread.stats.outputTokens ?? 0) + outputTokens
        thread.stats.totalInputTokens = (thread.stats.totalInputTokens ?? 0) + totalInputTokens
        thread.stats.cachedInputTokens = (thread.stats.cachedInputTokens ?? 0) + cachedInputTokens
        thread.stats.outputElapsedMs = (thread.stats.outputElapsedMs ?? 0) + elapsedMs
        break
      }
      case 'workflow': {
        const kind = String(event.details?.kind ?? '')
        const step = Number(event.details?.step) || 0
        if (kind === 'start') {
          thread.customPlan = undefined
          thread.turnStepHighWater = 0
        } else if (step > (thread.turnStepHighWater ?? 0)) {
          thread.stats ??= {}
          thread.stats.executionSteps = (thread.stats.executionSteps ?? 0)
            + step - (thread.turnStepHighWater ?? 0)
          thread.turnStepHighWater = step
        }
        if (kind === 'verify'
          && message?.content
          && (message.state === 'thinking' || message.state === 'streaming')) {
          message.state = 'done'
        }
        break
      }
      case 'plan_update': {
        const rawSteps = event.details?.steps as Array<{ title?: string; status?: string; detail?: string }> | undefined
        if (Array.isArray(rawSteps) && rawSteps.length > 0) {
          thread.customPlan = rawSteps.map((s, index) => {
            const rawStatus = (s.status || '').toLowerCase()
            const state = (rawStatus === 'completed' || rawStatus === 'done')
              ? 'done'
              : (rawStatus === 'in_progress' || rawStatus === 'running')
                ? 'running'
                : 'pending'
            return {
              id: `plan-step-${index + 1}`,
              step: index + 1,
              title: s.title || `步骤 ${index + 1}`,
              detail: s.detail,
              state
            }
          })
        }
        break
      }
      case 'computer_step':
        break

      case 'done':

      case 'cancelled':
        if (message) {
          message.state = 'done'
          if (!message.content && event.data) message.content = event.data
        }
        finalizeAssistantBubbles(thread.messages, 'done')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.pendingGuidance = 0
        thread.phase = undefined
        if (isBackgroundThread && typeof window !== 'undefined') {
          void window.seekclaw?.notify?.('后台任务完成', `「${thread.title}」已完成`)
        }
        if (!thread.activeTurnToken) {
          thread.running = false
          thread.activeTurnStartId = undefined
          thread.requestId = undefined
          thread.assistantId = undefined
          scheduleQueuedDrain(thread)
          if (isBackgroundThread) {
            reloadBackgroundThreadIfIdle(thread)
          } else if (thread.sessionId) {
            void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
          }
        }
        break
      case 'error': {
        const targetMessage = message ?? thread.messages.findLast((m) => m.role === 'assistant')
        if (targetMessage) {
          targetMessage.state = 'error'
          appendModelError(targetMessage, event.data)
        }
        finalizeAssistantBubbles(thread.messages, 'error')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.pendingGuidance = 0
        thread.phase = undefined
        if (isBackgroundThread && typeof window !== 'undefined') {
          void window.seekclaw?.notify?.('后台任务执行失败', `「${thread.title}」执行失败`)
        }
        if (!thread.activeTurnToken) {
          thread.running = false
          thread.activeTurnStartId = undefined
          thread.requestId = undefined
          thread.assistantId = undefined
          scheduleQueuedDrain(thread)
          if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
        }
        break
      }
    }
    if (thread.id === activeThreadId.value) void scrollToBottom()
  }
}
