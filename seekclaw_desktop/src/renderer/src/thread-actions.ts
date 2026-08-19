import type { Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'
import { confirmAction } from './confirmation'
import { sessionScope } from './app-helpers'

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
      .filter((thread) => thread.projectId === projectId && !thread.archived)
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
      title: '归档项目任务',
      message: `归档项目“${project.name}”的全部 ${targets.length} 个任务？`,
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
      title: '删除项目全部任务',
      message: `永久删除项目“${project.name}”的全部 ${targets.length} 个任务？此操作无法撤销。`,
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
    if (threads.value.some((thread) => thread.projectId === project.id && thread.running)) return
    if (!daemonState.value.connected) {
      await reconnectDaemon()
      if (!daemonState.value.connected) return
    }
    try {
      // Always refresh so sessions created or archived by another client are included.
      await refreshProjectSessions(project)
    } catch {
      return
    }
    const targets = threads.value.filter((thread) => thread.projectId === project.id)
    if (targets.some((thread) => thread.running)) return
    if (!await confirmAction({
      title: '删除项目',
      message: `删除项目“${project.name}”并永久删除其下全部 ${targets.length} 个会话？本地项目文件不会删除，此操作无法撤销。`,
      confirmLabel: '删除项目和会话',
      danger: true
    })) return

    try {
      await window.seekclaw.daemon.request('project.remove', { id: project.id })
    } catch {
      await refreshProjectSessions(project).catch(() => undefined)
      return
    }

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    projects.value = projects.value.filter((item) => item.id !== project.id)
    threads.value = threads.value.filter((thread) => thread.projectId !== project.id)
    if (targets.some((thread) => thread.id === taskSettingsThreadId.value)) taskSettingsThreadId.value = ''
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
