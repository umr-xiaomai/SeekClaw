import type { Ref } from 'vue'
import type { DaemonMessage } from '../../shared/ipc'
import type { ChatMessage, ProjectItem, ThreadItem, WorkflowKind } from './types'
import { finalizeAssistantBubbles } from './conversation-state'
import { makeId, phaseLabel } from './app-helpers'

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
    if (message.content.includes(normalized)) return
    const indentedDetail = normalized.split(/\r?\n/).map((line) => `    ${line}`).join('\n')
    message.content += `${message.content ? '\n\n' : ''}模型调用失败：\n\`\`\`text\n${indentedDetail}\n\`\`\``
    message.state = 'error'
  }

  return function handleDaemonEvent(event: DaemonMessage): void {
    if (event.event === 'schedule.upcoming') return
    if (event.event === 'schedule.updated') {
      void handleScheduleUpdated()
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

    // Guidance is drained by the Agent and forwarded under the chat request id, so it
    // can arrive after the turn's terminal response already resolved (IPC race). It
    // must be applied before the stale-request guards below: the optimistic guidance
    // message is already rendered, and its pending counter always needs releasing.
    if (event.event === 'steer') {
      thread.pendingGuidance = Math.max(0, (thread.pendingGuidance ?? 1) - 1)
      const currentAssistant = thread.messages.find((item) => item.id === thread.assistantId)
      if (currentAssistant) {
        currentAssistant.state = 'done'
        const nextAssistant: ChatMessage = {
          id: makeId(),
          role: 'assistant',
          content: '',
          thinking: '',
          tools: [],
          state: 'thinking',
          createdAt: Date.now()
        }
        thread.messages.push(nextAssistant)
        thread.assistantId = nextAssistant.id
        if (thread.id === activeThreadId.value) void scrollToBottom(true, true)
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
    if (isChatRequest && isFinishedRequest(thread, event.id)) {
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
    const findTool = () => eventCallId
      ? message?.tools?.find((tool) => tool.callId === eventCallId || tool.id === eventCallId)
      : message?.tools?.findLast((tool) => tool.state === 'running')

    switch (event.event) {
      case 'thinking':
        if (!message) break
        message.thinking = (message.thinking ?? '') + event.data
        message.state = 'thinking'
        break
      case 'delta':
        if (!message) break
        message.content += event.data
        message.state = 'streaming'
        break
      case 'status': {
        if (message && event.data.toLocaleLowerCase().includes('thinking')) message.state = 'thinking'
        thread.phase = phaseLabel(event.data)
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
        thread.phase = '执行工具'
        message.tools ??= []
        message.tools.push({
          id: eventCallId ?? `${event.id}-${message.tools.length}`,
          callId: eventCallId,
          name: event.data,
          detail: typeof event.details?.summary === 'string' ? event.details.summary : undefined,
          state: 'running'
        })
        break
      }
      case 'tool_done': {
        if (!message) break
        const running = findTool()
        if (running) {
          running.state = event.details?.success === false ? 'error' : 'done'
          running.detail = event.data || running.detail
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
        const previousHasOutput = Boolean(
          message?.content || message?.thinking || (message?.tools?.length ?? 0))
        if (message && step > 1 && previousHasOutput) {
          message.state = 'done'
          const nextAssistant: ChatMessage = {
            id: makeId(),
            role: 'assistant',
            content: '',
            thinking: '',
            tools: [],
            state: 'thinking',
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
        const label = event.data || String(event.details?.label ?? '')
        const detail = typeof event.details?.detail === 'string' ? event.details.detail : undefined
        if (kind === 'start') {
          thread.workflow = { nodes: [], activeId: null }
          thread.turnStepHighWater = 0
        } else if (step > (thread.turnStepHighWater ?? 0)) {
          thread.stats ??= {}
          thread.stats.executionSteps = (thread.stats.executionSteps ?? 0)
            + step - (thread.turnStepHighWater ?? 0)
          thread.turnStepHighWater = step
        }
        thread.workflow ??= { nodes: [], activeId: null }
        if (thread.workflow.activeId) {
          const previous = thread.workflow.nodes.find((node) => node.id === thread.workflow?.activeId)
          if (previous && previous.state === 'running') previous.state = 'done'
        }
        const nodeKind = (['start', 'think', 'tool', 'verify', 'repair', 'compact', 'done', 'error'] as const)
          .includes(kind as never) ? kind as WorkflowKind : 'think'
        const node = {
          id: `${event.id}:${thread.workflow.nodes.length}:${kind}`,
          step,
          kind: nodeKind,
          label,
          detail,
          state: (kind === 'done' || kind === 'error' ? kind : 'running') as 'running' | 'done' | 'error'
        }
        thread.workflow.nodes.push(node)
        thread.workflow.activeId = node.id
        if (kind === 'verify'
          && message?.content
          && (message.state === 'thinking' || message.state === 'streaming')) {
          message.state = 'done'
        }
        break
      }
      case 'done':
      case 'cancelled':
        if (message) {
          message.state = 'done'
          if (!message.content && event.data) message.content = event.data
        }
        finalizeAssistantBubbles(thread.messages, 'done')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.activeTurnToken = undefined
        thread.pendingGuidance = 0
        thread.running = false
        thread.requestId = undefined
        thread.assistantId = undefined
        thread.phase = undefined
        if (thread.workflow?.activeId) {
          const last = thread.workflow.nodes.find((node) => node.id === thread.workflow?.activeId)
          if (last && last.state === 'running') last.state = 'done'
        }
        scheduleQueuedDrain(thread)
        if (isBackgroundThread) {
          void window.seekclaw.notify('后台任务完成', `「${thread.title}」已完成`)
        }
        if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
        break
      case 'error':
        if (message) {
          message.state = 'error'
          appendModelError(message, event.data)
        }
        finalizeAssistantBubbles(thread.messages, 'error')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.activeTurnToken = undefined
        thread.pendingGuidance = 0
        thread.running = false
        thread.requestId = undefined
        thread.assistantId = undefined
        thread.phase = undefined
        if (thread.workflow?.activeId) {
          const last = thread.workflow.nodes.find((node) => node.id === thread.workflow?.activeId)
          if (last && last.state === 'running') last.state = 'error'
        }
        scheduleQueuedDrain(thread)
        if (isBackgroundThread) {
          void window.seekclaw.notify('后台任务执行失败', `「${thread.title}」执行失败`)
        }
        if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
        break
    }
    if (thread.id === activeThreadId.value) void scrollToBottom()
  }
}
