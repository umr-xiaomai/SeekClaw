import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import {
  computeParentDirectory,
  createServerDirectory,
  listServerDirectory,
  readServerImages,
  saveUploadedFile
} from './web-fs.js'

const tempDirs: string[] = []

async function makeTempDir(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'seekclaw-web-fs-'))
  tempDirs.push(dir)
  return dir
}

afterEach(async () => {
  for (const dir of tempDirs.splice(0)) {
    await rm(dir, { recursive: true, force: true }).catch(() => undefined)
  }
})

describe('web-fs server filesystem helpers', () => {
  it('filters directory listing by picker kind and hidden flag', async () => {
    const dir = await makeTempDir()
    await mkdir(join(dir, 'sub-dir'))
    await writeFile(join(dir, '.hidden.txt'), 'secret')
    await writeFile(join(dir, 'readme.md'), '# skill')
    await writeFile(join(dir, 'photo.png'), Buffer.from([137, 80, 78, 71]))
    await writeFile(join(dir, 'notes.txt'), 'plain text')

    const onlyDirs = await listServerDirectory({ path: dir, kind: 'directory', fallbackPath: dir })
    expect(onlyDirs.entries.map((e) => e.name)).toEqual(['sub-dir'])

    const onlyImages = await listServerDirectory({ path: dir, kind: 'images', fallbackPath: dir })
    expect(onlyImages.entries.map((e) => e.name)).toEqual(['sub-dir', 'photo.png'])

    const onlySkills = await listServerDirectory({ path: dir, kind: 'skills', fallbackPath: dir })
    expect(onlySkills.entries.map((e) => e.name)).toEqual(['sub-dir', 'readme.md'])

    const withHidden = await listServerDirectory({ path: dir, kind: 'files', showHidden: true, fallbackPath: dir })
    expect(withHidden.entries.some((e) => e.name === '.hidden.txt')).toBe(true)
  })

  it('creates directories safely and rejects invalid names', async () => {
    const dir = await makeTempDir()
    const created = await createServerDirectory(dir, 'workspace-a')
    expect(created.path).toBe(join(dir, 'workspace-a'))
    expect(computeParentDirectory(created.path)).toBe(dir)
    await expect(createServerDirectory(dir, '../escape')).rejects.toThrow()
  })

  it('saves uploaded files and reads images in base64', async () => {
    const dir = await makeTempDir()
    const pngBytes = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])
    const saved = await saveUploadedFile({
      name: 'sample.png',
      dataBase64: pngBytes.toString('base64'),
      fallbackTempDir: dir
    })
    expect(saved.path).toBe(join(dir, 'sample.png'))

    const loaded = await readServerImages([saved.path])
    expect(loaded.images).toHaveLength(1)
    expect(loaded.images[0]?.mediaType).toBe('image/png')
    expect(loaded.images[0]?.data).toBe(pngBytes.toString('base64'))
  })
})
