import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import { saveTaskDraft, taskDrafts } from './composer-drafts'
import { createThreadActions } from './thread-actions'
import type { ProjectItem, ThreadItem } from './types'

const confirmAction = vi.hoisted(() => vi.fn())

vi.mock('./confirmation', () => ({ confirmAction }))

const project: ProjectItem = {
  id: 'project-a',
  name: 'Project A',
  path: 'C:\\work\\project-a',
  loaded: true
}

function thread(overrides: Partial<ThreadItem> = {}): ThreadItem {
  return {
    id: 'thread-a',
    title: 'Thread A',
    projectId: project.id,
    updatedAt: 1,
    messages: [],
    sessionId: 'session-a',
    ...overrides
  }
}

function setup(projectThreads: ThreadItem[]) {
  const request = vi.fn().mockResolvedValue({ data: project.id })
  vi.stubGlobal('window', { seekclaw: { daemon: { request } } })

  const projects = ref<ProjectItem[]>([project])
  const threads = ref<ThreadItem[]>(projectThreads)
  const activeThreadId = ref(projectThreads[0]?.id ?? '')
  const selectedProjectId = ref(project.id)
  const taskSettingsThreadId = ref(projectThreads[0]?.id ?? '')
  const daemonState = ref<DaemonState>({ connected: true, endpoint: 'test' })
  const refreshProjectSessions = vi.fn().mockResolvedValue(undefined)

  const actions = createThreadActions({
    projects,
    threads,
    activeThreadId,
    selectedProjectId,
    taskSettingsThreadId,
    daemonState,
    conversationLoading: ref(false),
    conversationLoadError: ref(''),
    conversationSelectionToken: { value: 0 },
    selectThread: vi.fn(),
    refreshProjectSessions,
    reconnectDaemon: vi.fn().mockResolvedValue(undefined)
  })

  return { actions, projects, threads, request, refreshProjectSessions }
}

describe('project removal', () => {
  beforeEach(() => {
    confirmAction.mockReset()
    taskDrafts.clear()
  })

  it('removes only the project registration and preserves its sessions, memory, and drafts', async () => {
    const projectThread = thread()
    const { actions, projects, threads, request, refreshProjectSessions } = setup([projectThread])
    saveTaskDraft(projectThread.id, 'unfinished message', [], [])
    confirmAction.mockResolvedValue(true)

    await actions.deleteProject(project)

    expect(refreshProjectSessions).toHaveBeenCalledWith(project)
    expect(confirmAction).toHaveBeenCalledWith({
      title: '移除项目',
      message: '从 SeekClaw 中移除项目“Project A”？本地项目文件、会话和记忆都会保留。',
      confirmLabel: '移除项目',
      danger: true
    })
    expect(request).toHaveBeenCalledWith('project.remove', {
      id: project.id,
      keepSessions: true
    })
    expect(projects.value).toEqual([])
    expect(threads.value).toEqual([])
    expect(taskDrafts.get(projectThread.id)?.value).toBe('unfinished message')
  })

  it('rechecks running threads after confirmation before removing the project', async () => {
    const projectThread = thread()
    const { actions, projects, request } = setup([projectThread])
    confirmAction.mockImplementation(async () => {
      projectThread.running = true
      return true
    })

    await expect(actions.deleteProject(project)).rejects.toThrow(
      '请先停止该项目中正在运行的任务，再移除项目。'
    )

    expect(request).not.toHaveBeenCalled()
    expect(projects.value).toEqual([project])
  })
})
