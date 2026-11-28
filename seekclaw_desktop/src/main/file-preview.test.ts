import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { readLocalLinkPreview } from './file-preview.js'

const tempDirs: string[] = []

afterEach(async () => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    await rm(dir, { recursive: true, force: true }).catch(() => undefined)
  }
})

describe('readLocalLinkPreview', () => {
  it('reads TypeScript code with line ranges, Markdown, images, directories, and binary files safely', async () => {
    const workspace = await mkdtemp(join(tmpdir(), 'seekclaw-preview-'))
    tempDirs.push(workspace)
    await mkdir(join(workspace, 'src'), { recursive: true })

    await writeFile(join(workspace, 'src', 'app.ts'), 'const a = 1\nconst b = 2\nconst c = a + b\n', 'utf8')
    await writeFile(join(workspace, 'README.md'), '# Title\n\nHello preview\n', 'utf8')
    await writeFile(join(workspace, 'pic.png'), Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
    await writeFile(join(workspace, 'blob.dat'), Buffer.from([0x01, 0x00, 0x02, 0x03]))

    const tsPreview = await readLocalLinkPreview('src/app.ts#L2-L3', workspace)
    expect(tsPreview).toMatchObject({
      ok: true,
      kind: 'text',
      name: 'app.ts',
      language: 'ts',
      line: 2,
      endLine: 3
    })
    if (tsPreview.ok && tsPreview.kind === 'text') {
      expect(tsPreview.content).toContain('const b = 2')
    }

    const mdPreview = await readLocalLinkPreview('README.md', workspace)
    expect(mdPreview).toMatchObject({
      ok: true,
      kind: 'markdown',
      name: 'README.md',
      language: 'markdown'
    })

    const imgPreview = await readLocalLinkPreview('pic.png', workspace)
    expect(imgPreview).toMatchObject({
      ok: true,
      kind: 'image',
      name: 'pic.png',
      mediaType: 'image/png'
    })

    const dirPreview = await readLocalLinkPreview(workspace, workspace)
    expect(dirPreview).toMatchObject({
      ok: true,
      kind: 'directory'
    })
    if (dirPreview.ok && dirPreview.kind === 'directory') {
      expect(dirPreview.entries[0]?.name).toBe('src')
      expect(dirPreview.entries[0]?.isDirectory).toBe(true)
    }

    const binPreview = await readLocalLinkPreview('blob.dat', workspace)
    expect(binPreview).toMatchObject({
      ok: true,
      kind: 'binary',
      name: 'blob.dat'
    })

    const missing = await readLocalLinkPreview('does-not-exist.ts', workspace)
    expect(missing.ok).toBe(false)
  })
})
