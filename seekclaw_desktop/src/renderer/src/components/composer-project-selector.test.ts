import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'
import { describe, expect, it } from 'vitest'
import type { SshConnectionInfo } from '../../../shared/ssh'
import type { ProjectItem } from '../types'
import { pathName } from '../app-helpers'
import ComposerProjectSelector from './ComposerProjectSelector.vue'

function computeProjectButtonLabel(activeProject?: ProjectItem | null): string {
  if (activeProject) return activeProject.name
  return '选择项目'
}

function filterProjects(projects: ProjectItem[], query: string): ProjectItem[] {
  const q = query.trim().toLowerCase()
  if (!q) return projects
  return projects.filter(
    (p) => p.name.toLowerCase().includes(q) || p.path.toLowerCase().includes(q)
  )
}

async function renderSelector(props: {
  projects: ProjectItem[]
  activeProject?: ProjectItem | null
  sshConnections?: SshConnectionInfo[]
  activeSshId?: string | null
  isGlobalExplicit?: boolean
  disabled?: boolean
}): Promise<string> {
  return renderToString(
    createSSRApp({
      render: () => h(ComposerProjectSelector, props)
    })
  )
}

describe('ComposerProjectSelector', () => {
  const sampleProjects: ProjectItem[] = [
    { id: '1', name: 'KA Music', path: 'D:/Projects/kgka_Music_hl' },
    { id: '2', name: 'StarLife AI', path: 'D:/Projects/starlife' },
    { id: '3', name: 'SeekClaw', path: 'E:/Project/SeekClaw' },
    { id: '4', name: '花瀚官网', path: 'D:/Projects/wwwcom' }
  ]

  it('renders "此计算机" before the pill trigger button without chevron arrow', async () => {
    const unselectedHtml = await renderSelector({
      projects: sampleProjects,
      activeProject: null,
      isGlobalExplicit: true
    })
    const envIdx = unselectedHtml.indexOf('此计算机')
    const pillIdx = unselectedHtml.indexOf('选择项目')
    expect(envIdx).toBeGreaterThan(-1)
    expect(pillIdx).toBeGreaterThan(envIdx)
    expect(unselectedHtml).toMatch(/<button[^>]*class="[^"]*\bcomposer-env-btn\b[^"]*"[^>]*aria-haspopup="dialog"[^>]*aria-expanded="false"/)
    expect(unselectedHtml).not.toContain('lucide-chevron-down')
    expect(unselectedHtml).not.toContain('无工作目录')
    expect(unselectedHtml).not.toMatch(/class="[^"]*\bselected\b[^"]*"/)

    const selectedHtml = await renderSelector({
      projects: sampleProjects,
      activeProject: sampleProjects[1],
      isGlobalExplicit: false
    })
    expect(selectedHtml).toContain('StarLife AI')
    expect(selectedHtml).toMatch(/class="[^"]*\bselected\b[^"]*\bcomposer-project-btn\b[^"]*"/)

    const sshHtml = await renderSelector({
      projects: [
        ...sampleProjects,
        { id: '5', name: 'remote-api', path: 'ssh://srv1/var/www/remote-api', loaded: true }
      ],
      sshConnections: [
        { id: 'srv1', name: 'Prod Server', host: 'root@10.22.245.9', port: 22, authMode: 'none' }
      ],
      activeSshId: 'srv1',
      activeProject: null
    })
    expect(sshHtml).toContain('Prod Server')
    expect(sshHtml).toContain('lucide-globe')
  })

  it('computes pill button label cleanly regardless of isGlobalExplicit', () => {
    expect(computeProjectButtonLabel(null)).toBe('选择项目')
    expect(computeProjectButtonLabel(undefined)).toBe('选择项目')
    expect(computeProjectButtonLabel(sampleProjects[0])).toBe('KA Music')
    expect(computeProjectButtonLabel(sampleProjects[2])).toBe('SeekClaw')
  })

  it('filters project list by name or path', () => {
    expect(filterProjects(sampleProjects, '')).toHaveLength(4)

    const byName = filterProjects(sampleProjects, 'Music')
    expect(byName).toHaveLength(1)
    expect(byName[0]?.name).toBe('KA Music')

    const byPath = filterProjects(sampleProjects, 'starlife')
    expect(byPath).toHaveLength(1)
    expect(byPath[0]?.name).toBe('StarLife AI')

    expect(filterProjects(sampleProjects, 'notfound')).toHaveLength(0)
  })

  it('determines subtitle path name correctly', () => {
    expect(pathName(sampleProjects[0]!.path)).toBe('kgka_Music_hl')
    expect(pathName(sampleProjects[0]!.path)).not.toBe(sampleProjects[0]!.name)

    expect(pathName(sampleProjects[2]!.path)).toBe('SeekClaw')
    expect(pathName(sampleProjects[2]!.path)).toBe(sampleProjects[2]!.name)
  })
})
