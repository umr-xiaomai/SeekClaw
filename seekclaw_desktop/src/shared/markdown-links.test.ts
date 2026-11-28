import { describe, expect, it } from 'vitest'
import {
  getLinkVisualMeta,
  isExecutableLinkPath,
  renderLinkIconHtml,
  resolveMarkdownLinkTarget
} from './markdown-links.js'

describe('markdown link resolution', () => {
  it('resolves external URLs and in-page fragments', () => {
    expect(resolveMarkdownLinkTarget('https://example.com/docs?q=1#top')).toEqual({
      kind: 'external',
      url: 'https://example.com/docs?q=1#top'
    })
    expect(resolveMarkdownLinkTarget('http://localhost:8080/api')).toEqual({
      kind: 'external',
      url: 'http://localhost:8080/api'
    })
    expect(resolveMarkdownLinkTarget('mailto:team@example.com')).toEqual({
      kind: 'external',
      url: 'mailto:team@example.com'
    })
    expect(resolveMarkdownLinkTarget('#section-%E4%B8%80')).toEqual({
      kind: 'fragment',
      fragment: 'section-一'
    })
  })

  it('resolves file:// URLs with line ranges on Windows and POSIX', () => {
    expect(resolveMarkdownLinkTarget('file:///E:/Project/paper/design.md#L12-L20', undefined, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\paper\\design.md',
      line: 12,
      endLine: 20,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('file:///Users/a/design.md#L5', undefined, 'darwin')).toEqual({
      kind: 'local',
      path: '/Users/a/design.md',
      line: 5,
      endLine: undefined,
      column: undefined
    })
  })

  it('resolves Windows absolute paths including encoded backslashes and line suffixes', () => {
    expect(resolveMarkdownLinkTarget('E:/Project/foo.md', undefined, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\foo.md',
      line: undefined,
      endLine: undefined,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('E:/Project/foo.md#L10-L15', undefined, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\foo.md',
      line: 10,
      endLine: 15,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('E:/Project/foo.md:42:5', undefined, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\foo.md',
      line: 42,
      endLine: undefined,
      column: 5
    })
    expect(resolveMarkdownLinkTarget('E:%5CProject%5Cfoo.md', undefined, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\foo.md',
      line: undefined,
      endLine: undefined,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('/E:/Project/foo.md', undefined, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\foo.md',
      line: undefined,
      endLine: undefined,
      column: undefined
    })
  })

  it('resolves relative paths against a local workspace and rejects without one', () => {
    const workspace = 'E:\\Project\\SeekClaw\\docs'
    expect(resolveMarkdownLinkTarget('experiment.md', workspace, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\SeekClaw\\docs\\experiment.md',
      line: undefined,
      endLine: undefined,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('./sub/%E5%AE%9E%E9%AA%8C.md#L8', workspace, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\SeekClaw\\docs\\sub\\实验.md',
      line: 8,
      endLine: undefined,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('../shared/a.md', workspace, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\SeekClaw\\shared\\a.md',
      line: undefined,
      endLine: undefined,
      column: undefined
    })
    expect(resolveMarkdownLinkTarget('src/app.ts:42', workspace, 'win32')).toEqual({
      kind: 'local',
      path: 'E:\\Project\\SeekClaw\\docs\\src\\app.ts',
      line: 42,
      endLine: undefined,
      column: undefined
    })

    expect(resolveMarkdownLinkTarget('docs/experiment.md', undefined, 'win32')).toMatchObject({
      kind: 'unsupported'
    })
    expect(resolveMarkdownLinkTarget('docs/experiment.md', 'ssh://devbox/repo', 'win32')).toMatchObject({
      kind: 'unsupported'
    })
  })

  it('rejects unsafe or unknown URL schemes', () => {
    for (const url of ['javascript:alert(1)', 'vbscript:msgbox(1)', 'data:text/html,hi', 'ms-msdt:foo']) {
      expect(resolveMarkdownLinkTarget(url, 'E:\\Project\\SeekClaw', 'win32')).toMatchObject({
        kind: 'unsupported'
      })
    }
  })

  it('detects executable extensions safely', () => {
    expect(isExecutableLinkPath('E:\\tools\\run.bat')).toBe(true)
    expect(isExecutableLinkPath('E:\\tools\\installer.EXE')).toBe(true)
    expect(isExecutableLinkPath('C:\\scripts\\setup.ps1')).toBe(true)
    expect(isExecutableLinkPath('E:\\Project\\design.md')).toBe(false)
    expect(isExecutableLinkPath('E:\\Project\\paper.pdf')).toBe(false)
  })

  it('extracts file-type visual metadata and renders inline badges/icons', () => {
    expect(getLinkVisualMeta('E:/src/app.ts#L10-L20')).toMatchObject({ kind: 'file', variant: 'text', label: 'TS' })
    expect(getLinkVisualMeta('file:///E:/src/View.vue')).toMatchObject({ kind: 'file', variant: 'vue', label: 'V' })
    expect(getLinkVisualMeta('docs/README.md')).toMatchObject({ kind: 'file', variant: 'md', label: 'MD' })
    expect(getLinkVisualMeta('scripts/train.py')).toMatchObject({ kind: 'file', variant: 'text', label: 'PY' })
    expect(getLinkVisualMeta('paper.pdf')).toMatchObject({ kind: 'file', variant: 'text', label: 'PDF' })
    expect(getLinkVisualMeta('fig.png')).toMatchObject({ kind: 'file', variant: 'image', label: 'IMG' })
    expect(getLinkVisualMeta('E:/Project/src/')).toMatchObject({ kind: 'folder', variant: 'folder' })
    expect(getLinkVisualMeta('https://example.com')).toMatchObject({ kind: 'web', variant: 'web' })

    expect(renderLinkIconHtml(getLinkVisualMeta('app.ts'))).toContain('md-file-badge')
    expect(renderLinkIconHtml(getLinkVisualMeta('app.ts'))).toContain('>TS<')
    expect(renderLinkIconHtml(getLinkVisualMeta('View.vue'))).toContain('<svg')
    expect(renderLinkIconHtml(getLinkVisualMeta('https://example.com'))).toContain('<svg')
  })
})
