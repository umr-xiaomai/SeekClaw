import type { Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'
import {
  hydrateMessages,
  normalizeReasoningLevel,
  sessionScope,
  sessionStats,
  type RuntimeSession,
  type RuntimeSessionHeader
} from './app-helpers'

export interface SessionSyncContext {
  daemonState: Ref<DaemonState>
  threads: Ref<ThreadItem[]>
}

export async function refreshProjectSessions(context: SessionSyncContext, project: ProjectItem): Promise<void> {
  const { daemonState, threads } = context
  if (!daemonState.value.connected) return
  project.loaded = false
  try {
    const response = await window.seekclaw.daemon.request('session.list', {
      workspace: project.path,
      includeArchived: true
    })
    const savedSessions = JSON.parse(response.data) as RuntimeSessionHeader[]
    const sessionIds = new Set(savedSessions.map((session) => session.id))
    threads.value = threads.value.filter((thread) =>
      thread.projectId !== project.id || !thread.sessionId || sessionIds.has(thread.sessionId))

    for (const saved of savedSessions) {
      const existing = threads.value.find((thread) =>
        thread.projectId === project.id && thread.sessionId === saved.id)
      const fallbackTitle = `任务 ${new Date(saved.createdAt).toLocaleString()}`
      if (existing) {
        existing.title = saved.title || existing.title || fallbackTitle
        existing.updatedAt = new Date(saved.updatedAt).getTime()
        existing.archived = Boolean(saved.archived)
        existing.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
        existing.networkEnabled = saved.networkEnabled ?? true
      } else {
        threads.value.push({
          id: `${project.id}:session:${saved.id}`,
          title: saved.title || fallbackTitle,
          projectId: project.id,
          updatedAt: new Date(saved.updatedAt).getTime(),
          messages: [],
          sessionId: saved.id,
          sessionLoaded: false,
          reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
          networkEnabled: saved.networkEnabled ?? true,
          archived: Boolean(saved.archived)
        })
      }
    }
  } finally {
    project.loaded = true
  }
}

export async function refreshGlobalSessions(context: SessionSyncContext): Promise<void> {
  const { daemonState, threads } = context
  if (!daemonState.value.connected) return
  const response = await window.seekclaw.daemon.request('session.list', {
    global: true,
    includeArchived: true
  })
  const savedSessions = JSON.parse(response.data) as RuntimeSessionHeader[]
  const sessionIds = new Set(savedSessions.map((session) => session.id))
  threads.value = threads.value.filter((thread) =>
    thread.projectId || !thread.sessionId || sessionIds.has(thread.sessionId))

  for (const saved of savedSessions) {
    const existing = threads.value.find((thread) => !thread.projectId && thread.sessionId === saved.id)
    const fallbackTitle = `任务 ${new Date(saved.createdAt).toLocaleString()}`
    if (existing) {
      existing.title = saved.title || existing.title || fallbackTitle
      existing.updatedAt = new Date(saved.updatedAt).getTime()
      existing.archived = Boolean(saved.archived)
      existing.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
      existing.networkEnabled = saved.networkEnabled ?? true
    } else {
      threads.value.push({
        id: `global:session:${saved.id}`,
        title: saved.title || fallbackTitle,
        updatedAt: new Date(saved.updatedAt).getTime(),
        messages: [],
        sessionId: saved.id,
        sessionLoaded: false,
        reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
        networkEnabled: saved.networkEnabled ?? true,
        archived: Boolean(saved.archived)
      })
    }
  }
}

export async function refreshAllProjectSessions(
  context: SessionSyncContext,
  projects: ProjectItem[]
): Promise<void> {
  await Promise.all([
    refreshGlobalSessions(context).catch(() => undefined),
    ...projects.map((project) => refreshProjectSessions(context, project).catch(() => undefined))
  ])
}

export async function reloadThreadSession(
  context: Pick<SessionSyncContext, 'daemonState'>,
  thread: ThreadItem,
  project?: ProjectItem
): Promise<void> {
  if (!thread.sessionId || thread.running || !context.daemonState.value.connected) return
  try {
    const response = await window.seekclaw.daemon.request('session.get', {
      id: thread.sessionId,
      ...sessionScope(thread, project)
    })
    const saved = JSON.parse(response.data) as RuntimeSession
    thread.messages = hydrateMessages(saved)
    thread.sessionLoaded = true
    thread.title = saved.title || thread.title
    thread.archived = Boolean(saved.archived)
    thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
    thread.stats = sessionStats(saved)
  } catch {
    thread.sessionLoaded = false
  }
}
