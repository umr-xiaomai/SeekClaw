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

function expectGroupExpanded(html: string, controls: string, expanded: boolean): void {
  const toggle = html.match(/<button\b[^>]*>/g)?.find((button) =>
    button.includes(`aria-controls="${controls}"`))
  expect(toggle, `Missing toggle for ${controls}`).toBeDefined()
  expect(toggle).toContain(`aria-expanded="${expanded}"`)
}

describe('sidebar saved group rendering', () => {
  it('shows an accessible skeleton until the AI title is ready without exposing the prompt', async () => {
    const thread: ThreadItem = { id: 'pending', projectId: 'p1', title: 'raw user prompt',
      titleGenerating: true, titleSource: 'pending', sessionId: 'saved', messages: [], updatedAt: 1 }
    const stored = { 'seekclaw-sidebar-expansion-v2': '{"projects":{"p1":true}}' }
    const pending = await renderSidebar(stored, [thread])
    expect(pending).toContain('task-title-skeleton')
    expect(pending).toContain('aria-busy="true"')
    expect(pending).toContain('正在生成任务标题')
    expect(pending).not.toContain('raw user prompt')
    thread.title = 'AI 总结任务目标'
    thread.titleGenerating = false
    const ready = await renderSidebar(stored, [thread])
    expect(ready).not.toContain('task-title-skeleton')
    expect(ready).toContain('AI 总结任务目标')
  })

  it('defaults empty and draft-only groups to collapsed', async () => {
    const html = await renderSidebar({}, [
      { id: 'draft', projectId: 'p1', title: 'New task', messages: [], updatedAt: 1 },
      { id: 'archived', title: 'Archived', sessionId: 'old', archived: true, messages: [], updatedAt: 1 }
    ])
    expectGroupExpanded(html, 'sidebar-global-tasks', false)
    expectGroupExpanded(html, 'project-tasks-p1', false)
    expectGroupExpanded(html, 'sidebar-projects', true)
    expect(html).not.toContain('class="project-tasks"')
    expect(html).not.toContain('class="task-list"')
    expect(html).not.toContain('无任务')
  })

  it('defaults populated groups to collapsed too, without inferring expansion from their sessions', async () => {
    const html = await renderSidebar({}, [
      { id: 'project', projectId: 'p1', title: 'Project task', sessionId: 'saved-project', messages: [], updatedAt: 1 },
      { id: 'global', title: 'Global task', sessionId: 'saved-global', messages: [], updatedAt: 1 }
    ])
    expectGroupExpanded(html, 'sidebar-global-tasks', false)
    expectGroupExpanded(html, 'project-tasks-p1', false)
    expectGroupExpanded(html, 'sidebar-projects', true)
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
    expectGroupExpanded(html, 'sidebar-global-tasks', true)
    expectGroupExpanded(html, 'project-tasks-p1', false)
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
    expectGroupExpanded(html, 'sidebar-global-tasks', true)
    expectGroupExpanded(html, 'project-tasks-p1', true)
    expectGroupExpanded(html, 'sidebar-projects', true)
  })

  it('renders empty lists when the user explicitly chose to expand them', async () => {
    const html = await renderSidebar({
      'seekclaw-sidebar-expansion-v2': '{"projects":{"p1":true},"tasks":true}'
    })
    expectGroupExpanded(html, 'sidebar-global-tasks', true)
    expectGroupExpanded(html, 'project-tasks-p1', true)
    expectGroupExpanded(html, 'sidebar-projects', true)
    expect(html.match(/无任务/g)).toHaveLength(2)
  })

  it('hides a saved collapsed project section without losing expanded child project choices', async () => {
    const key = 'seekclaw-sidebar-expansion-v2'
    const preferences = { projects: { p1: true }, tasks: true, projectSectionExpanded: false }
    const stored = { [key]: JSON.stringify(preferences) }
    const threads: ThreadItem[] = [
      { id: 'project', projectId: 'p1', title: 'Project task', sessionId: 'saved-project', messages: [], updatedAt: 1 },
      { id: 'global', title: 'Global task', sessionId: 'saved-global', messages: [], updatedAt: 1 }
    ]

    const collapsed = await renderSidebar(stored, threads)
    expectGroupExpanded(collapsed, 'sidebar-projects', false)
    expectGroupExpanded(collapsed, 'sidebar-global-tasks', true)
    expect(collapsed).not.toContain('id="sidebar-projects"')
    expect(collapsed).not.toContain('aria-controls="project-tasks-p1"')
    expect(collapsed).not.toContain('Project task')
    expect(collapsed).toContain('Global task')
    expect(JSON.parse(stored[key])).toEqual(preferences)

    stored[key] = JSON.stringify({ ...preferences, projectSectionExpanded: true })
    const expanded = await renderSidebar(stored, threads)
    expectGroupExpanded(expanded, 'sidebar-projects', true)
    expectGroupExpanded(expanded, 'project-tasks-p1', true)
    expectGroupExpanded(expanded, 'sidebar-global-tasks', true)
    expect(expanded).toContain('Project task')
    expect(expanded).toContain('Global task')
  })
})
