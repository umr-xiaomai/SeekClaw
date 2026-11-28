import { existsSync } from 'node:fs'
import { open, readdir, readFile, stat } from 'node:fs/promises'
import { basename, extname, join } from 'node:path'
import {
  resolveMarkdownLinkTarget,
  type FilePreviewDirectoryEntry,
  type FilePreviewPayload
} from '../shared/markdown-links.js'

const IMAGE_MEDIA_TYPES: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.svg': 'image/svg+xml',
  '.bmp': 'image/bmp',
  '.ico': 'image/x-icon'
}

const BINARY_EXTENSIONS = new Set([
  '.zip', '.tar', '.gz', '.bz2', '.xz', '.7z', '.rar',
  '.exe', '.dll', '.so', '.dylib', '.bin', '.pdb', '.obj', '.o', '.a', '.lib',
  '.pyc', '.pyo', '.class', '.jar', '.war',
  '.docx', '.xlsx', '.pptx', '.doc', '.xls', '.ppt',
  '.mp4', '.mov', '.avi', '.mkv', '.webm', '.mp3', '.wav', '.flac', '.ogg',
  '.woff', '.woff2', '.ttf', '.otf', '.eot', '.sqlite', '.db'
])

const EXT_TO_LANGUAGE: Record<string, string> = {
  '.ts': 'ts',
  '.tsx': 'ts',
  '.mts': 'ts',
  '.cts': 'ts',
  '.js': 'js',
  '.jsx': 'js',
  '.mjs': 'js',
  '.cjs': 'js',
  '.vue': 'vue',
  '.py': 'python',
  '.pyw': 'python',
  '.cs': 'csharp',
  '.csproj': 'xml',
  '.sln': 'text',
  '.cpp': 'cpp',
  '.cc': 'cpp',
  '.cxx': 'cpp',
  '.hpp': 'cpp',
  '.c': 'c',
  '.h': 'c',
  '.rs': 'rust',
  '.go': 'go',
  '.java': 'java',
  '.json': 'json',
  '.jsonc': 'json',
  '.jsonl': 'json',
  '.yaml': 'yaml',
  '.yml': 'yaml',
  '.css': 'css',
  '.scss': 'scss',
  '.sql': 'sql',
  '.sh': 'bash',
  '.bash': 'bash',
  '.zsh': 'bash',
  '.xml': 'xml',
  '.html': 'xml',
  '.htm': 'xml',
  '.diff': 'diff',
  '.patch': 'diff',
  '.md': 'markdown',
  '.mdx': 'markdown'
}

const MAX_TEXT_PREVIEW_BYTES = 512 * 1024
const MAX_HARD_TEXT_SIZE_BYTES = 4 * 1024 * 1024
const MAX_IMAGE_BYTES = 20 * 1024 * 1024
const MAX_PDF_BYTES = 30 * 1024 * 1024
const MAX_DIR_ENTRIES = 500

export async function readLocalLinkPreview(rawHref: string, workspace?: string): Promise<FilePreviewPayload> {
  const resolved = resolveMarkdownLinkTarget(rawHref, workspace, process.platform)
  if (resolved.kind !== 'local') {
    return {
      ok: false,
      error: resolved.kind === 'unsupported' ? resolved.reason : '不支持在此面板预览该链接类型'
    }
  }

  const targetPath = resolved.path
  if (!existsSync(targetPath)) {
    return { ok: false, error: `找不到目标文件：${targetPath}`, path: targetPath }
  }

  const info = await stat(targetPath)
  const name = basename(targetPath) || targetPath
  const ext = extname(targetPath).toLowerCase()

  if (info.isDirectory()) {
    const dirents = await readdir(targetPath, { withFileTypes: true })
    const entries: FilePreviewDirectoryEntry[] = []
    for (const dirent of dirents) {
      const fullPath = join(targetPath, dirent.name)
      let isDirectory = dirent.isDirectory()
      let sizeBytes = 0
      try {
        if (dirent.isSymbolicLink() || !isDirectory) {
          const entryStat = await stat(fullPath)
          isDirectory = entryStat.isDirectory()
          sizeBytes = isDirectory ? 0 : entryStat.size
        }
      } catch {
        continue
      }
      entries.push({
        name: dirent.name,
        path: fullPath,
        isDirectory,
        sizeBytes,
        ext: isDirectory ? '' : extname(dirent.name).toLowerCase()
      })
    }
    entries.sort((a, b) => {
      if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1
      return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
    })
    return {
      ok: true,
      kind: 'directory',
      path: targetPath,
      name,
      entries: entries.slice(0, MAX_DIR_ENTRIES)
    }
  }

  const imageMediaType = IMAGE_MEDIA_TYPES[ext]
  if (imageMediaType) {
    if (info.size > MAX_IMAGE_BYTES) {
      return { ok: true, kind: 'binary', path: targetPath, name, ext, sizeBytes: info.size }
    }
    const buf = await readFile(targetPath)
    return {
      ok: true,
      kind: 'image',
      path: targetPath,
      name,
      ext,
      mediaType: imageMediaType,
      dataBase64: buf.toString('base64'),
      sizeBytes: info.size
    }
  }

  if (ext === '.pdf') {
    if (info.size > MAX_PDF_BYTES) {
      return { ok: true, kind: 'binary', path: targetPath, name, ext, sizeBytes: info.size }
    }
    const buf = await readFile(targetPath)
    return {
      ok: true,
      kind: 'pdf',
      path: targetPath,
      name,
      ext,
      dataBase64: buf.toString('base64'),
      sizeBytes: info.size
    }
  }

  if (BINARY_EXTENSIONS.has(ext) || info.size > MAX_HARD_TEXT_SIZE_BYTES) {
    return {
      ok: true,
      kind: 'binary',
      path: targetPath,
      name,
      ext,
      sizeBytes: info.size
    }
  }

  const bytesToRead = Math.min(info.size, MAX_TEXT_PREVIEW_BYTES)
  const buffer = Buffer.alloc(bytesToRead)
  let bytesRead = 0
  if (bytesToRead > 0) {
    const handle = await open(targetPath, 'r')
    try {
      const result = await handle.read(buffer, 0, bytesToRead, 0)
      bytesRead = result.bytesRead
    } finally {
      await handle.close()
    }
  }

  const slice = buffer.subarray(0, bytesRead)
  const probeLen = Math.min(slice.length, 8192)
  if (slice.subarray(0, probeLen).includes(0)) {
    return {
      ok: true,
      kind: 'binary',
      path: targetPath,
      name,
      ext,
      sizeBytes: info.size
    }
  }

  const content = slice.toString('utf8')
  const truncated = info.size > MAX_TEXT_PREVIEW_BYTES
  const kind = ext === '.md' || ext === '.mdx' ? 'markdown' : ext === '.html' || ext === '.htm' ? 'html' : 'text'

  return {
    ok: true,
    kind,
    path: targetPath,
    name,
    ext,
    language: EXT_TO_LANGUAGE[ext] ?? 'text',
    content,
    sizeBytes: info.size,
    truncated,
    line: resolved.line,
    endLine: resolved.endLine
  }
}
