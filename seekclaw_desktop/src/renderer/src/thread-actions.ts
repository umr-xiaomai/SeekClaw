import type { Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'
import { confirmAction } from './confirmation'
import { sessionScope } from './app-helpers'
import { clearTaskDraft } from './composer-drafts'

export interface ThreadActionsContext {
  projects: Ref<ProjectItem[]>
  threads: Ref<ThreadItem[]>
  activeThreadId: Ref<string>
  selectedProjectId: Ref<string>
  taskSettingsThreadId: Ref<string>
  daemonState: Ref<DaemonState>
  conversationLoading: Ref<boolean>
  conversationLoadError: Ref<string>
  conversationSelectionToken: { value: number }
  selectThread: (id: string) => Promise<void> | void
  refreshProjectSessions: (project: ProjectItem) => Promise<void>
  reconnectDaemon: () => Promise<void>
}

export function createThreadActions(context: ThreadActionsContext) {
  const {
    projects,
    threads,
    activeThreadId,
    selectedProjectId,
    taskSettingsThreadId,
    daemonState,
    conversationLoading,
    conversationLoadError,
    conversationSelectionToken,
    selectThread,
    refreshProjectSessions,
    reconnectDaemon
  } = context

  function chooseAfterRemoval(projectId?: string): void {
    // The selected task is about to change; stale session reads must not update
    // the loading state for the replacement task.
    conversationSelectionToken.value++
    conversationLoading.value = false
    conversationLoadError.value = ''
    const fallback = threads.value
      .filter((thread) => thread.projectId === projectId && !thread.archived && Boolean(thread.sessionId || thread.messages.length > 0 || thread.running))
      .sort((left, right) => right.updatedAt - left.updatedAt)[0]
    if (fallback) {
      void selectThread(fallback.id)
      return
    }
    activeThreadId.value = ''
    selectedProjectId.value = projectId ?? ''
  }

  async function archiveTask(thread: ThreadItem): Promise<void> {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if ((thread.projectId && !project) || thread.running) return
    if (thread.sessionId) {
      await window.seekclaw.daemon.request('session.archive', {
        id: thread.sessionId,
        ...sessionScope(thread, project),
        archived: true
      })
      thread.archived = true
    } else {
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeThreadId.value === thread.id) chooseAfterRemoval(project?.id)
  }

  async function restoreTask(thread: ThreadItem): Promise<void> {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if ((thread.projectId && !project) || !thread.sessionId || thread.running) return
    await window.seekclaw.daemon.request('session.archive', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      archived: false
    })
    thread.archived = false
    thread.updatedAt = Date.now()
    taskSettingsThreadId.value = ''
  }

  async function archiveProjectTasks(project: ProjectItem): Promise<void> {
    if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
    const targets = threads.value.filter((thread) => thread.projectId === project.id && !thread.archived)
    if (targets.length === 0 || targets.some((thread) => thread.running)) return
    if (!await confirmAction({
      title: '归档聊天',
      message: `归档项目“${project.name}”的全部 ${targets.length} 个会话？`,
      confirmLabel: '全部归档'
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    for (const thread of targets) {
      if (thread.sessionId) {
        await window.seekclaw.daemon.request('session.archive', {
          id: thread.sessionId,
          workspace: project.path,
          archived: true
        })
        thread.archived = true
      } else {
        threads.value = threads.value.filter((item) => item.id !== thread.id)
      }
    }
    if (activeAffected) chooseAfterRemoval(project.id)
  }

  async function archiveGlobalTasks(): Promise<void> {
    const targets = threads.value.filter((thread) => !thread.projectId && !thread.archived)
    if (targets.length === 0 || targets.some((thread) => thread.running)) return
    if (!await confirmAction({
      title: '归档任务',
      message: `归档全部 ${targets.length} 个任务？`,
      confirmLabel: '全部归档'
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    for (const thread of targets) {
      if (thread.sessionId) {
        await window.seekclaw.daemon.request('session.archive', {
          id: thread.sessionId,
          global: true,
          archived: true
        })
        thread.archived = true
      } else {
        threads.value = threads.value.filter((item) => item.id !== thread.id)
      }
    }
    if (activeAffected) chooseAfterRemoval()
  }

  async function deleteTask(thread: ThreadItem): Promise<void> {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if ((thread.projectId && !project) || thread.running) return
    if (!await confirmAction({
      title: '删除任务',
      message: `永久删除任务“${thread.title}”？此操作无法撤销。`,
      confirmLabel: '永久删除',
      danger: true
    })) return
    if (thread.sessionId) {
      await window.seekclaw.daemon.request('session.delete', {
        id: thread.sessionId,
        ...sessionScope(thread, project)
      })
    }
    clearTaskDraft(thread.id)
    threads.value = threads.value.filter((item) => item.id !== thread.id)
    taskSettingsThreadId.value = ''
    if (activeThreadId.value === thread.id) chooseAfterRemoval(project?.id)
  }

  async function deleteGlobalTasks(): Promise<void> {
    const targets = threads.value.filter((thread) => !thread.projectId)
    if (targets.length === 0 || targets.some((thread) => thread.running)) return
    if (!await confirmAction({
      title: '删除全部任务',
      message: `永久删除全部 ${targets.length} 个任务？此操作无法撤销。`,
      confirmLabel: '全部删除',
      danger: true
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    for (const thread of targets) {
      if (thread.sessionId) {
        await window.seekclaw.daemon.request('session.delete', { id: thread.sessionId, global: true })
      }
      clearTaskDraft(thread.id)
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeAffected) chooseAfterRemoval()
  }

  async function deleteProjectTasks(project: ProjectItem): Promise<void> {
    if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
    const targets = threads.value.filter((thread) => thread.projectId === project.id)
    if (targets.length === 0 || targets.some((thread) => thread.running)) return
    if (!await confirmAction({
      title: '删除所有会话',
      message: `永久删除项目“${project.name}”的全部 ${targets.length} 个会话？此操作无法撤销。`,
      confirmLabel: '全部删除',
      danger: true
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    for (const thread of targets) {
      if (thread.sessionId) {
        await window.seekclaw.daemon.request('session.delete', {
          id: thread.sessionId,
          workspace: project.path
        })
      }
      clearTaskDraft(thread.id)
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeAffected) chooseAfterRemoval(project.id)
  }

  async function deleteArchivedTasks(): Promise<void> {
    const targets = threads.value.filter((thread) => thread.archived)
    if (targets.length === 0 || targets.some((thread) => thread.running)) return
    if (!await confirmAction({
      title: '清空已归档任务',
      message: `永久删除全部 ${targets.length} 个已归档任务？此操作无法撤销。`,
      confirmLabel: '全部删除',
      danger: true
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    for (const thread of targets) {
      const project = projects.value.find((item) => item.id === thread.projectId)
      if (thread.sessionId && (project || !thread.projectId)) {
        await window.seekclaw.daemon.request('session.delete', {
          id: thread.sessionId,
          ...sessionScope(thread, project)
        })
      }
      clearTaskDraft(thread.id)
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeAffected) {
      activeThreadId.value = ''
      const project = projects.value.find((project) => project.id === selectedProjectId.value)
      chooseAfterRemoval(project?.id)
    }
  }

  async function deleteProject(project: ProjectItem): Promise<void> {
    const runningError = '请先停止该项目中正在运行的任务，再移除项目。'
    if (threads.value.some((thread) => thread.projectId === project.id && thread.running)) {
      throw new Error(runningError)
    }
    if (!daemonState.value.connected) {
      await reconnectDaemon()
      if (!daemonState.value.connected) {
        throw new Error(daemonState.value.error || '无法连接 SeekClaw Runtime，请重新连接后再移除项目。')
      }
    }
    await refreshProjectSessions(project)
    const targets = threads.value.filter((thread) => thread.projectId === project.id)
    if (targets.some((thread) => thread.running)) {
      throw new Error(runningError)
    }
    if (!await confirmAction({
      title: '移除项目',
      message: `从 SeekClaw 中移除项目“${project.name}”？本地项目文件、会话和记忆都会保留。`,
      confirmLabel: '移除项目',
      danger: true
    })) return
    if (threads.value.some((thread) => thread.projectId === project.id && thread.running)) {
      throw new Error(runningError)
    }

    try {
      await window.seekclaw.daemon.request('project.remove', { id: project.id, keepSessions: true })
    } catch (error) {
      await refreshProjectSessions(project).catch(() => undefined)
      throw error
    }

    const removedThreads = threads.value.filter((thread) => thread.projectId === project.id)
    const activeAffected = removedThreads.some((thread) => thread.id === activeThreadId.value)
    projects.value = projects.value.filter((item) => item.id !== project.id)
    threads.value = threads.value.filter((thread) => thread.projectId !== project.id)
    if (removedThreads.some((thread) => thread.id === taskSettingsThreadId.value)) taskSettingsThreadId.value = ''
    if (activeAffected) activeThreadId.value = ''
    if (selectedProjectId.value === project.id) selectedProjectId.value = projects.value[0]?.id ?? ''
    if (!threads.value.some((thread) => thread.id === activeThreadId.value)) {
      activeThreadId.value = ''
      chooseAfterRemoval(selectedProjectId.value || undefined)
    }
  }

  return {
    chooseAfterRemoval,
    archiveTask,
    restoreTask,
    archiveProjectTasks,
    archiveGlobalTasks,
    deleteTask,
    deleteGlobalTasks,
    deleteProjectTasks,
    deleteArchivedTasks,
    deleteProject
  }
}
