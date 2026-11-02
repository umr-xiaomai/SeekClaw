import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Sidebar from './Sidebar.vue'
import type { ThreadItem } from '../types'

afterEach(() => vi.unstubAllGlobals())

function renderSidebar(stored: Record<string, string>, threads: ThreadItem[] = []) {
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => stored[key] ?? null,
    setItem: (key: string, value: string) => { stored[key] = value }
  })
  return renderToString(createSSRApp({
    render: () => h(Sidebar, {
      projects: [{ id: 'p1', name: 'Project', path: 'E:/project', loaded: true }],
      projectsReady: true,
      threads,
      activeThreadId: ''
    })
  }))
}

describe('sidebar saved group rendering', () => {
  it('defaults empty and draft-only groups to collapsed', async () => {
    const html = await renderSidebar({}, [
      { id: 'draft', projectId: 'p1', title: 'New task', messages: [], updatedAt: 1 },
      { id: 'archived', title: 'Archived', sessionId: 'old', archived: true, messages: [], updatedAt: 1 }
    ])
    expect(html.match(/aria-expanded="false"/g)).toHaveLength(2)
    expect(html).not.toContain('class="project-tasks"')
    expect(html).not.toContain('class="task-list"')
    expect(html).not.toContain('无任务')
  })

  it('defaults populated groups to collapsed too, without inferring expansion from their sessions', async () => {
    const html = await renderSidebar({}, [
      { id: 'project', projectId: 'p1', title: 'Project task', sessionId: 'saved-project', messages: [], updatedAt: 1 },
      { id: 'global', title: 'Global task', sessionId: 'saved-global', messages: [], updatedAt: 1 }
    ])
    expect(html.match(/aria-expanded="false"/g)).toHaveLength(2)
    expect(html).not.toContain('class="project-tasks"')
    expect(html).not.toContain('class="task-list"')
  })

  it('restores saved collapse for a populated project and saved expansion for global tasks', async () => {
    const html = await renderSidebar({
      'seekclaw-sidebar-expansion-v2': '{"projects":{"p1":false},"tasks":true}'
    }, [
      { id: 'project', projectId: 'p1', title: 'Project task', sessionId: 'saved-project', messages: [], updatedAt: 1 },
      { id: 'global', title: 'Global task', sessionId: 'saved-global', messages: [], updatedAt: 1 }
    ])
    expect(html).not.toContain('class="project-tasks"')
    expect(html).toContain('class="task-list"')
    expect(html).toContain('Global task')
    expect(html).not.toContain('Project task')
  })

  it('loads legacy expansion instead of discarding it', async () => {
    const html = await renderSidebar({
      'seekclaw-sidebar-expanded-projects-v1': '["p1"]',
      'seekclaw-sidebar-known-projects-v1': '["p1"]',
      'seekclaw-sidebar-task-section-expanded-v1': 'true'
    })
    expect(html.match(/aria-expanded="true"/g)).toHaveLength(2)
  })

  it('renders empty lists when the user explicitly chose to expand them', async () => {
    const html = await renderSidebar({
      'seekclaw-sidebar-expansion-v2': '{"projects":{"p1":true},"tasks":true}'
    })
    expect(html.match(/aria-expanded="true"/g)).toHaveLength(2)
    expect(html.match(/无任务/g)).toHaveLength(2)
  })
})
