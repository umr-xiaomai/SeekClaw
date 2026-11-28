import { existsSync } from 'node:fs'
import { mkdir, readdir, readFile, stat, writeFile } from 'node:fs/promises'
import { basename, dirname, extname, join, parse, resolve } from 'node:path'
import type { DesktopImageSelection } from '../shared/ipc.js'
import type { ServerFilePickerKind, ServerFsEntry, ServerFsListResult, ServerFsRoot } from '../shared/web-server.js'

const IMAGE_MEDIA_TYPES: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif'
}

const SKILL_EXTENSIONS = new Set(['.md', '.zip'])
const MAX_IMAGE_COUNT = 10
const MAX_IMAGE_BYTES = 10 * 1024 * 1024
const MAX_TOTAL_IMAGE_BYTES = 40 * 1024 * 1024
const MAX_UPLOAD_BYTES = 50 * 1024 * 1024

export interface ServerRootsInput {
  userProfile: string
  documentsPath: string
  desktopPath?: string
  projects?: Array<{ id: string; name: string; path: string }>
}

export function listServerRoots(input: ServerRootsInput): ServerFsRoot[] {
  const roots: ServerFsRoot[] = []
  const seenPaths = new Set<string>()
  const addRoot = (root: ServerFsRoot): void => {
    const normalized = resolve(root.path)
    const key = process.platform === 'win32' ? normalized.toLowerCase() : normalized
    if (seenPaths.has(key) || !existsSync(normalized)) return
    seenPaths.add(key)
    roots.push({ ...root, path: normalized })
  }

  for (const project of input.projects ?? []) {
    if (!project.path) continue
    addRoot({
      id: `project:${project.id}`,
      label: project.name || basename(project.path),
      path: project.path,
      kind: 'project'
    })
  }

  addRoot({ id: 'home', label: '主目录', path: input.userProfile, kind: 'home' })
  if (input.desktopPath) {
    addRoot({ id: 'desktop', label: '桌面', path: input.desktopPath, kind: 'desktop' })
  } else {
    addRoot({ id: 'desktop', label: '桌面', path: join(input.userProfile, 'Desktop'), kind: 'desktop' })
  }
  addRoot({ id: 'documents', label: '文档', path: input.documentsPath, kind: 'documents' })

  if (process.platform === 'win32') {
    for (let code = 67; code <= 90; code++) {
      const letter = String.fromCharCode(code)
      const drive = `${letter}:\\`
      try {
        if (existsSync(drive)) {
          addRoot({ id: `drive:${letter}`, label: `本地磁盘 (${letter}:)`, path: drive, kind: 'drive' })
        }
      } catch {
        // skip inaccessible drives
      }
    }
  } else {
    addRoot({ id: 'root', label: '根目录 (/)', path: '/', kind: 'drive' })
  }

  return roots
}

export function computeParentDirectory(targetPath: string): string | null {
  const resolved = resolve(targetPath)
  const parsed = parse(resolved)
  if (resolved === parsed.root) return null
  const parent = dirname(resolved)
  return parent && parent !== resolved ? parent : null
}

function isAllowedExtension(name: string, kind: ServerFilePickerKind): boolean {
  const ext = extname(name).toLowerCase()
  if (kind === 'images') return Boolean(IMAGE_MEDIA_TYPES[ext])
  if (kind === 'skills') return SKILL_EXTENSIONS.has(ext)
  return true
}

export async function listServerDirectory(options: {
  path?: string
  kind?: ServerFilePickerKind
  showHidden?: boolean
  fallbackPath: string
}): Promise<ServerFsListResult> {
  const kind = options.kind ?? 'files'
  const showHidden = options.showHidden === true
  const rawPath = options.path?.trim() || options.fallbackPath
  const resolvedPath = resolve(rawPath)
  const dirStat = await stat(resolvedPath)
  if (!dirStat.isDirectory()) {
    throw new Error(`不是有效的文件夹路径: ${resolvedPath}`)
  }

  const dirents = await readdir(resolvedPath, { withFileTypes: true })
  const entries: ServerFsEntry[] = []

  for (const dirent of dirents) {
    if (!showHidden && dirent.name.startsWith('.')) continue
    const fullPath = join(resolvedPath, dirent.name)
    let isDirectory = dirent.isDirectory()
    let sizeBytes = 0
    let modifiedAt = 0

    try {
      if (dirent.isSymbolicLink() || !isDirectory) {
        const info = await stat(fullPath)
        isDirectory = info.isDirectory()
        sizeBytes = isDirectory ? 0 : info.size
        modifiedAt = info.mtimeMs
      } else {
        const info = await stat(fullPath)
        sizeBytes = 0
        modifiedAt = info.mtimeMs
      }
    } catch {
      continue
    }

    if (kind === 'directory' && !isDirectory) continue
    if (!isDirectory && !isAllowedExtension(dirent.name, kind)) continue

    const ext = extname(dirent.name).toLowerCase()
    entries.push({
      name: dirent.name,
      path: fullPath,
      isDirectory,
      sizeBytes,
      modifiedAt,
      isImage: !isDirectory && Boolean(IMAGE_MEDIA_TYPES[ext])
    })
  }

  entries.sort((left, right) => {
    if (left.isDirectory !== right.isDirectory) return left.isDirectory ? -1 : 1
    return left.name.localeCompare(right.name, undefined, { numeric: true, sensitivity: 'base' })
  })

  return {
    path: resolvedPath,
    parentPath: computeParentDirectory(resolvedPath),
    entries
  }
}

export async function createServerDirectory(parentPath: string, name: string): Promise<{ path: string; name: string }> {
  const cleanName = name.trim()
  if (!cleanName || cleanName === '.' || cleanName === '..') {
    throw new Error('请输入有效的文件夹名称')
  }
  if (cleanName.includes('/') || cleanName.includes('\\') || /[<>:"|?*]/.test(cleanName)) {
    throw new Error('文件夹名称包含不支持的字符')
  }
  const resolvedParent = resolve(parentPath)
  const target = join(resolvedParent, cleanName)
  await mkdir(target, { recursive: false })
  return { path: target, name: cleanName }
}

export async function readServerImages(paths: string[]): Promise<DesktopImageSelection> {
  const images: DesktopImageSelection['images'] = []
  const warnings: string[] = []
  let totalBytes = 0
  if (paths.length > MAX_IMAGE_COUNT) {
    warnings.push(`一次最多添加 ${MAX_IMAGE_COUNT} 张图片，已保留前 ${MAX_IMAGE_COUNT} 张。`)
  }
  for (const filePath of paths.slice(0, MAX_IMAGE_COUNT)) {
    const name = basename(filePath)
    const mediaType = IMAGE_MEDIA_TYPES[extname(filePath).toLowerCase()]
    if (!mediaType) {
      warnings.push(`${name} 的格式暂不支持。`)
      continue
    }
    let data: Buffer
    try {
      data = await readFile(filePath)
    } catch {
      warnings.push(`${name} 无法读取，未添加。`)
      continue
    }
    if (data.byteLength > MAX_IMAGE_BYTES) {
      warnings.push(`${name} 超过 10 MB，未添加。`)
      continue
    }
    if (totalBytes + data.byteLength > MAX_TOTAL_IMAGE_BYTES) {
      warnings.push(`图片合计不能超过 40 MB，${name} 未添加。`)
      continue
    }
    totalBytes += data.byteLength
    images.push({
      name,
      mediaType,
      data: data.toString('base64'),
      sizeBytes: data.byteLength
    })
  }
  return { images, warning: warnings.join(' ') || undefined }
}

export async function readServerFileBase64(
  filePath: string
): Promise<{ data: string; mediaType: string; sizeBytes: number } | null> {
  try {
    const data = await readFile(resolve(filePath))
    const ext = extname(filePath).toLowerCase()
    const mediaType = IMAGE_MEDIA_TYPES[ext] || 'image/png'
    return {
      data: data.toString('base64'),
      mediaType,
      sizeBytes: data.byteLength
    }
  } catch {
    return null
  }
}

export async function saveUploadedFile(options: {
  name: string
  dataBase64: string
  fallbackTempDir: string
  targetDir?: string
}): Promise<{ path: string; name: string; sizeBytes: number }> {
  const rawName = basename(options.name.trim() || 'upload.bin').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_')
  const cleanName = rawName && rawName !== '.' && rawName !== '..' ? rawName : `upload-${Date.now()}.bin`
  const buffer = Buffer.from(options.dataBase64, 'base64')
  if (buffer.byteLength > MAX_UPLOAD_BYTES) {
    throw new Error('上传文件超过 50 MB 限制')
  }

  let destinationDir: string
  if (options.targetDir?.trim()) {
    destinationDir = resolve(options.targetDir.trim())
    const info = await stat(destinationDir)
    if (!info.isDirectory()) throw new Error('目标路径不是有效目录')
  } else {
    destinationDir = resolve(options.fallbackTempDir)
    await mkdir(destinationDir, { recursive: true })
  }

  const destination = join(destinationDir, cleanName)
  await writeFile(destination, buffer)
  return { path: destination, name: cleanName, sizeBytes: buffer.byteLength }
}
