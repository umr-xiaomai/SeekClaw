import type { DaemonMessage } from '../../shared/ipc'
import type { ChatMessage, SubAgentActivity, SubAgentLifecycleEvent, ToolActivity } from './types'

function object(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown> : undefined
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function json(value?: string): unknown {
  if (!value) return undefined
  try { return JSON.parse(value) } catch { return undefined }
}

export function subAgentArguments(tool: ToolActivity): Record<string, unknown> | undefined {
  return object(json(tool.input)) ?? object(json(tool.detail))
}

export function isSubAgentTool(tool: ToolActivity): boolean {
  return ['subagent', 'invoke_subagent'].includes(tool.name.toLowerCase())
}

export function subAgentStatus(value: unknown): SubAgentActivity['status'] {
  switch (value) {
    case 'running': case 'completed': case 'failed': case 'cancelled': case 'interrupted': return value
    default: return 'unknown'
  }
}

/** Read durable task results without treating a historical running snapshot as live. */
export function subAgentsFromTool(tool: ToolActivity): SubAgentActivity[] {
  if (!isSubAgentTool(tool)) return []
  const args = subAgentArguments(tool)
  const value = tool.structuredContent ?? json(tool.output)
  const records = Array.isArray(value) ? value : [value]
  const agents = records.flatMap((item): SubAgentActivity[] => {
    const record = object(item)
    const id = text(record?.id)
    if (!record || !id) return []
    return [{
      id,
      role: text(record.role) ?? text(args?.role) ?? 'general',
      name: text(record.name) ?? text(args?.name),
      status: subAgentStatus(record.status),
      task: text(record.task) ?? text(args?.task),
      model: text(record.model) ?? text(args?.model),
      childSessionId: text(record.childSessionId) ?? text(record.sessionId),
      workspace: text(record.workspace),
      output: text(record.error) ?? text(record.output),
      sourceToolId: tool.id,
      live: false
    }]
  })
  if (agents.length) return agents
  const action = text(args?.action)
  if (tool.name.toLowerCase() !== 'invoke_subagent' && action !== 'spawn' && action !== 'send') return []
  return [{
    id: text(args?.id) ?? `tool:${tool.id}`,
    role: text(args?.role) ?? 'general',
    name: text(args?.name),
    status: tool.state === 'error' ? 'failed'
      : tool.name.toLowerCase() === 'invoke_subagent' && tool.state === 'done' ? 'completed'
        : tool.state === 'running' ? 'starting' : 'unknown',
    task: text(args?.task),
    model: text(args?.model),
    output: text(tool.output),
    sourceToolId: tool.id,
    live: false
  }]
}

/** Keep known event state ahead of launch snapshots and avoid a duplicate raw tool row. */
export function messageSubAgents(message: ChatMessage): SubAgentActivity[] {
  const agents = [...new Map((message.subAgents ?? []).map((agent) => [agent.id, agent])).values()]
  for (const tool of message.tools ?? []) {
    for (const fallback of subAgentsFromTool(tool)) {
      if (!agents.some((agent) => agent.id === fallback.id
        || (fallback.id.startsWith('tool:') && agent.sourceToolId === tool.id))) agents.push(fallback)
    }
  }
  return agents
}

function findActivity(messages: ChatMessage[], id: string, requestId?: number): SubAgentActivity | undefined {
  // Prefer lifecycle events over later status/list snapshots of the same child.
  const known = messages.flatMap((message) => message.subAgents ?? []).filter((agent) => agent.id === id)
  if (requestId !== undefined) {
    const matching = known.findLast((agent) => agent.requestId === requestId)
    if (matching) return matching
    if (known.length && known.every((agent) => agent.requestId !== undefined)) return undefined
  }
  for (let index = messages.length - 1; index >= 0; index--) {
    const message = messages[index]!
    const existing = message.subAgents?.findLast((agent) => agent.id === id && (requestId === undefined || agent.requestId === undefined))
    if (existing) return existing
  }
  for (let index = messages.length - 1; index >= 0; index--) {
    const message = messages[index]!
    const recovered = message.tools?.flatMap(subAgentsFromTool).find((agent) => agent.id === id)
    if (recovered) {
      message.subAgents ??= []
      message.subAgents.push(recovered)
      return recovered
    }
  }
  return undefined
}

function sourceTool(message: ChatMessage, id: string, role?: string): ToolActivity | undefined {
  const tools = message.tools?.filter((tool) => {
    if (!isSubAgentTool(tool)) return false
    if (message.subAgents?.some((agent) => agent.sourceToolId === tool.id && agent.id !== id)) return false
    return true
  }) ?? []
  return tools.findLast((tool) => subAgentsFromTool(tool).some((agent) => agent.id === id)
    || text(subAgentArguments(tool)?.id) === id) ?? tools.findLast((tool) => {
    const args = subAgentArguments(tool)
    return tool.state === 'running' && (!role || args?.role === role)
      && (tool.name === 'invoke_subagent' || args?.action === 'spawn' || args?.action === 'send')
  })
}

function lifecycleId(activity: SubAgentActivity, kind: SubAgentLifecycleEvent['kind']): string {
  return `subagent:${activity.id}:${activity.runId ?? activity.sourceToolId ?? 'task'}:${kind}`
}

/** Snapshot events are append-only; summary progress is free to keep changing. */
function appendLifecycle(
  messages: ChatMessage[], activity: SubAgentActivity, kind: SubAgentLifecycleEvent['kind'], current?: ChatMessage
): void {
  const id = lifecycleId(activity, kind)
  if (messages.some((message) => message.subAgentEvents?.some((event) => event.id === id))) return
  const target = current ?? messages.findLast((message) => message.role === 'assistant')
  if (!target) return
  target.subAgentEvents ??= []
  target.subAgentEvents.push({ id, agentId: activity.id, kind, agent: { ...activity }, createdAt: Date.now(), contentOffset: target.content.length })
}

export function applySubAgentEvent(
  messages: ChatMessage[], event: DaemonMessage, current?: ChatMessage, allowCreate = true
): boolean {
  const broadcast = event.event === 'subagent.updated'
  if (!broadcast && !['subagent_start', 'subagent_progress', 'subagent_done'].includes(event.event)) return false
  const id = text(broadcast ? event.details?.taskId : event.details?.subAgentId)
  if (!id) return false
  const role = text(event.details?.role)
  let activity = findActivity(messages, id, broadcast || event.event === 'subagent_start' ? undefined : event.id)
  const source = current ? sourceTool(current, id, role) : undefined
  const sourceId = source?.callId ?? source?.id ?? `request:${event.id}`
  // A duplicate launch can arrive while a second same-role spawn is pending.
  // Only an explicit send can start another instruction for the same real child.
  const followUp = source && subAgentArguments(source)?.action === 'send'
  const runId = activity?.requestId === event.id && !followUp
    ? activity.runId ?? sourceId : sourceId
  if (event.event === 'subagent_start' && messages.some((message) => message.subAgentEvents?.some((entry) =>
    entry.agentId === id && entry.kind === 'started' && entry.agent.runId === runId))) return true
  // A persistent child can receive a new instruction in a later assistant turn.
  if (event.event === 'subagent_start' && allowCreate && current
    && activity && activity.runId !== runId) activity = undefined
  if (!activity && allowCreate && current) {
    activity = { id, role: role ?? 'general', status: 'running', sourceToolId: source?.id }
    current.subAgents ??= []
    current.subAgents.push(activity)
  }
  if (!activity) return false
  if (role) activity.role = role
  activity.childSessionId = text(event.details?.childSessionId) ?? activity.childSessionId
  activity.workspace = text(event.details?.workspace) ?? activity.workspace
  activity.name = text(event.details?.name) ?? activity.name ?? (source ? text(subAgentArguments(source)?.name) : undefined)
  if (broadcast) {
    const status = subAgentStatus(event.details?.status)
    if (status === 'unknown') return false
    // A queued running broadcast must not revive an already settled instruction.
    if (status === 'running' && !['starting', 'running', 'unknown'].includes(activity.status)) return true
    activity.status = status
    activity.live = activity.status === 'running'
    activity.output = text(event.details?.output) ?? activity.output
    if (status !== 'running' && status !== 'starting') appendLifecycle(messages, activity, status, current)
  } else if (event.event === 'subagent_start') {
    activity.runId = runId
    activity.requestId = event.id
    activity.sourceToolId = source?.id ?? activity.sourceToolId
    activity.status = 'running'
    activity.live = true
    activity.task = text(event.data)
    activity.progress = undefined
    activity.detail = undefined
    activity.output = undefined
    activity.elapsedMs = undefined
    activity.model = text(event.details?.model)
    appendLifecycle(messages, activity, 'started', current)
  } else if (event.event === 'subagent_progress') {
    // Forwarded progress can arrive after its task's terminal event.
    const kind = text(event.details?.kind)
    if (activity.status === 'running' && (!kind || kind === 'tool_start' || kind === 'tool_done')) {
      activity.live = true
      activity.progress = text(event.data)
      activity.detail = text(event.details?.detail)
    }
  } else {
    activity.status = event.details?.success === true ? 'completed'
      : /cancelled|canceled/i.test(event.data) ? 'cancelled' : 'failed'
    activity.live = false
    activity.output = text(event.data)
    const elapsedMs = event.details?.elapsedMs
    activity.elapsedMs = typeof elapsedMs === 'number' && Number.isFinite(elapsedMs) && elapsedMs >= 0 ? elapsedMs : undefined
    appendLifecycle(messages, activity, activity.status, current)
  }
  return true
}

export function subAgentRoleKind(agent: SubAgentActivity): 'researcher' | 'coder' | 'tester' | 'general' {
  const role = agent.role.toLowerCase()
  return role === 'researcher' || role === 'coder' || role === 'tester' ? role : 'general'
}

export function subAgentLifecycleLabel(event: SubAgentLifecycleEvent): string {
  const action = { started: '开始工作', completed: '已完成', failed: '执行失败', cancelled: '已取消', interrupted: '已中断' }
  return `${subAgentName(event.agent)}${action[event.kind]}`
}

export function subAgentGroupStartLabel(agents: SubAgentActivity[]): string {
  if (agents.length === 0) return ''
  if (agents.length === 1) return `${subAgentName(agents[0]!)}开始工作`
  if (agents.length === 2) return `${subAgentName(agents[0]!)}和${subAgentName(agents[1]!)}开始工作`
  return `${agents.slice(0, 2).map(subAgentName).join('、')}等 ${agents.length} 个子智能体开始工作`
}

export function subAgentName(agent: SubAgentActivity): string {
  const names: Record<string, string> = { researcher: '研究员', coder: '代码工程师', tester: '测试工程师', general: '通用助手' }
  return agent.name || names[agent.role] || agent.role || '子智能体'
}

export function subAgentStatusLabel(agent: SubAgentActivity): string {
  const labels = { starting: '正在启动', running: agent.live ? '正在工作' : '已启动', completed: '已完成', failed: '执行失败', cancelled: '已取消', interrupted: '已中断', unknown: '子任务' }
  return labels[agent.status]
}

export function subAgentSummary(agents: SubAgentActivity[]): string {
  const names = agents.slice(0, 2).map(subAgentName).join('和')
  const subject = agents.length > 2 ? `${names}等 ${agents.length} 个子智能体` : names
  if (agents.some((agent) => agent.status === 'starting')) return `${subject}正在启动`
  if (agents.some((agent) => agent.status === 'running' && agent.live)) return `${subject}正在工作`
  if (agents.every((agent) => agent.status === 'completed')) return `${subject}已完成工作`
  if (agents.some((agent) => agent.status === 'failed')) return agents.length === 1 ? `${subject}执行失败` : `${subject}有任务执行失败`
  if (agents.some((agent) => agent.status === 'interrupted')) return agents.length === 1 ? `${subject}的工作已中断` : `${subject}有任务已中断`
  if (agents.every((agent) => agent.status === 'cancelled')) return `${subject}已取消工作`
  if (agents.every((agent) => agent.status === 'running' && !agent.live)) return `${subject}已启动`
  if (agents.every((agent) => ['completed', 'cancelled'].includes(agent.status))) return `${subject}已结束工作`
  return `${subject}的子任务`
}

export interface ThreadSubAgentEntry {
  agent: SubAgentActivity
  startedAt?: number
  updatedAt?: number
  active: boolean
}

function isSettledSubAgentStatus(status: SubAgentActivity['status']): boolean {
  return status === 'completed' || status === 'failed' || status === 'cancelled' || status === 'interrupted'
}

/** Collect every unique sub-agent mentioned across a thread's messages with latest status & timestamps. */
export function collectThreadSubAgents(messages: readonly ChatMessage[]): ThreadSubAgentEntry[] {
  const latest = new Map<string, ThreadSubAgentEntry>()

  const merge = (agent: SubAgentActivity, timestamp?: number): void => {
    const existing = latest.get(agent.id)
    if (existing && isSettledSubAgentStatus(existing.agent.status)
      && !agent.live && (agent.status === 'running' || agent.status === 'starting')) {
      return
    }
    const active = agent.status === 'starting' || agent.status === 'running'
    latest.set(agent.id, {
      agent,
      startedAt: existing?.startedAt ?? timestamp,
      updatedAt: timestamp ?? existing?.updatedAt,
      active
    })
  }

  for (const message of messages) {
    for (const agent of messageSubAgents(message)) merge(agent, message.createdAt)
  }
  for (const message of messages) {
    const eventTimeById = new Map<string, number>()
    for (const ev of message.subAgentEvents ?? []) {
      const prev = eventTimeById.get(ev.agentId)
      if (!prev || ev.createdAt > prev) eventTimeById.set(ev.agentId, ev.createdAt)
    }
    for (const agent of message.subAgents ?? []) {
      merge(agent, eventTimeById.get(agent.id) ?? message.createdAt)
    }
  }

  const representedTools = new Set(
    [...latest.values()]
      .filter((entry) => !entry.agent.id.startsWith('tool:'))
      .map((entry) => entry.agent.sourceToolId)
      .filter(Boolean)
  )
  for (const [id, entry] of latest) {
    if (id.startsWith('tool:') && entry.agent.sourceToolId && representedTools.has(entry.agent.sourceToolId)) {
      latest.delete(id)
    }
  }

  return [...latest.values()].sort((a, b) => {
    if (a.active !== b.active) return a.active ? -1 : 1
    return (b.updatedAt ?? 0) - (a.updatedAt ?? 0)
  })
}
