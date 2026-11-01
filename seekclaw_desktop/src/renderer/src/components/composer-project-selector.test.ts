import { describe, expect, it } from 'vitest'
import type { ProjectItem } from '../types'
import { pathName } from '../app-helpers'

function computeProjectButtonLabel(
  activeProject?: ProjectItem | null,
  isGlobalExplicit = false
): string {
  if (activeProject) return activeProject.name
  if (isGlobalExplicit) return '无工作目录'
  return '选择项目'
}

function filterProjects(projects: ProjectItem[], query: string): ProjectItem[] {
  const q = query.trim().toLowerCase()
  if (!q) return projects
  return projects.filter(
    (p) => p.name.toLowerCase().includes(q) || p.path.toLowerCase().includes(q)
  )
}

function shouldShowGlobalOption(query: string): boolean {
  const q = query.trim().toLowerCase()
  if (!q) return true
  return '无工作目录'.includes(q) || '全局环境'.includes(q) || 'none'.includes(q) || 'global'.includes(q)
}

describe('ComposerProjectSelector logic', () => {
  const sampleProjects: ProjectItem[] = [
    { id: '1', name: 'KA Music', path: 'D:/Projects/kgka_Music_hl' },
    { id: '2', name: 'StarLife AI', path: 'D:/Projects/starlife' },
    { id: '3', name: 'SeekClaw', path: 'E:/Project/SeekClaw' },
    { id: '4', name: '花瀚官网', path: 'D:/Projects/wwwcom' }
  ]

  it('computes correct button label for various project states', () => {
    // 1. Initial state (no active project, not explicitly global): shows "选择项目"
    expect(computeProjectButtonLabel(null, false)).toBe('选择项目')
    expect(computeProjectButtonLabel(undefined, false)).toBe('选择项目')

    // 2. Explicitly selected "无工作目录": shows "无工作目录"
    expect(computeProjectButtonLabel(null, true)).toBe('无工作目录')

    // 3. Project is selected: shows project name
    expect(computeProjectButtonLabel(sampleProjects[0], false)).toBe('KA Music')
    expect(computeProjectButtonLabel(sampleProjects[2], true)).toBe('SeekClaw')
  })

  it('filters project list by name or path', () => {
    // Empty search returns all
    expect(filterProjects(sampleProjects, '')).toHaveLength(4)

    // Search by name
    const byName = filterProjects(sampleProjects, 'Music')
    expect(byName).toHaveLength(1)
    expect(byName[0]?.name).toBe('KA Music')

    // Search by folder / path
    const byPath = filterProjects(sampleProjects, 'starlife')
    expect(byPath).toHaveLength(1)
    expect(byPath[0]?.name).toBe('StarLife AI')

    // Non-matching search
    expect(filterProjects(sampleProjects, 'notfound')).toHaveLength(0)
  })

  it('determines whether to show the "无工作目录" option based on search query', () => {
    expect(shouldShowGlobalOption('')).toBe(true)
    expect(shouldShowGlobalOption('无')).toBe(true)
    expect(shouldShowGlobalOption('全局')).toBe(true)
    expect(shouldShowGlobalOption('global')).toBe(true)
    expect(shouldShowGlobalOption('Seek')).toBe(false)
  })

  it('determines subtitle path name correctly', () => {
    // When name != folder, subtitle is shown
    expect(pathName(sampleProjects[0]!.path)).toBe('kgka_Music_hl')
    expect(pathName(sampleProjects[0]!.path)).not.toBe(sampleProjects[0]!.name)

    // When name == folder, folder matches name
    expect(pathName(sampleProjects[2]!.path)).toBe('SeekClaw')
    expect(pathName(sampleProjects[2]!.path)).toBe(sampleProjects[2]!.name)
  })
})
