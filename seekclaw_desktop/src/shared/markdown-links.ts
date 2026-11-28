export type OpenLinkResult =
  | { ok: true; kind: 'external' | 'path' | 'folder'; target?: string }
  | { ok: false; error: string }

export type ResolvedLinkTarget =
  | { kind: 'external'; url: string }
  | { kind: 'fragment'; fragment: string }
  | { kind: 'local'; path: string; line?: number; endLine?: number; column?: number }
  | { kind: 'unsupported'; reason: string }

export interface FilePreviewDirectoryEntry {
  name: string
  path: string
  isDirectory: boolean
  sizeBytes: number
  ext: string
}

export type FilePreviewPayload =
  | {
      ok: true
      kind: 'text' | 'markdown' | 'html'
      path: string
      name: string
      ext: string
      language: string
      content: string
      sizeBytes: number
      truncated?: boolean
      line?: number
      endLine?: number
    }
  | {
      ok: true
      kind: 'image'
      path: string
      name: string
      ext: string
      mediaType: string
      dataBase64: string
      sizeBytes: number
    }
  | {
      ok: true
      kind: 'pdf'
      path: string
      name: string
      ext: string
      dataBase64: string
      sizeBytes: number
    }
  | {
      ok: true
      kind: 'directory'
      path: string
      name: string
      entries: FilePreviewDirectoryEntry[]
    }
  | {
      ok: true
      kind: 'binary'
      path: string
      name: string
      ext: string
      sizeBytes: number
    }
  | {
      ok: false
      error: string
      path?: string
    }

export type LinkVisualKind = 'file' | 'folder' | 'web' | 'mail' | 'fragment'
export type LinkIconVariant = 'text' | 'vue' | 'md' | 'image' | 'folder' | 'web' | 'mail' | 'file'

export interface LinkVisualMeta {
  kind: LinkVisualKind
  variant: LinkIconVariant
  label: string
  color: string
  ext: string
}

const EXECUTABLE_EXTENSIONS = new Set([
  '.exe', '.bat', '.cmd', '.com', '.msi', '.msp', '.ps1', '.psm1',
  '.vbs', '.vbe', '.js', '.jse', '.wsf', '.wsh', '.scr', '.lnk',
  '.url', '.reg', '.hta', '.pif', '.cpl', '.msc', '.jar', '.app',
  '.command', '.sh'
])

const EXT_VISUAL_MAP: Record<string, { variant: LinkIconVariant; label: string; color: string }> = {
  '.ts': { variant: 'text', label: 'TS', color: '#3178c6' },
  '.tsx': { variant: 'text', label: 'TSX', color: '#3178c6' },
  '.mts': { variant: 'text', label: 'TS', color: '#3178c6' },
  '.cts': { variant: 'text', label: 'TS', color: '#3178c6' },
  '.js': { variant: 'text', label: 'JS', color: '#c88a04' },
  '.jsx': { variant: 'text', label: 'JSX', color: '#c88a04' },
  '.mjs': { variant: 'text', label: 'JS', color: '#c88a04' },
  '.cjs': { variant: 'text', label: 'JS', color: '#c88a04' },
  '.vue': { variant: 'vue', label: 'V', color: '#41b883' },
  '.md': { variant: 'md', label: 'MD', color: '#2563eb' },
  '.mdx': { variant: 'md', label: 'MDX', color: '#2563eb' },
  '.py': { variant: 'text', label: 'PY', color: '#3776ab' },
  '.pyw': { variant: 'text', label: 'PY', color: '#3776ab' },
  '.ipynb': { variant: 'text', label: 'NB', color: '#f37626' },
  '.cs': { variant: 'text', label: 'C#', color: '#7c3aed' },
  '.csproj': { variant: 'text', label: 'C#', color: '#7c3aed' },
  '.sln': { variant: 'text', label: 'SLN', color: '#7c3aed' },
  '.cpp': { variant: 'text', label: 'C++', color: '#2563eb' },
  '.cc': { variant: 'text', label: 'C++', color: '#2563eb' },
  '.cxx': { variant: 'text', label: 'C++', color: '#2563eb' },
  '.hpp': { variant: 'text', label: 'H++', color: '#2563eb' },
  '.c': { variant: 'text', label: 'C', color: '#2563eb' },
  '.h': { variant: 'text', label: 'H', color: '#2563eb' },
  '.rs': { variant: 'text', label: 'RS', color: '#ce422b' },
  '.go': { variant: 'text', label: 'GO', color: '#00add8' },
  '.java': { variant: 'text', label: 'JV', color: '#e76f00' },
  '.kt': { variant: 'text', label: 'KT', color: '#7f52ff' },
  '.kts': { variant: 'text', label: 'KT', color: '#7f52ff' },
  '.json': { variant: 'text', label: '{}', color: '#b45309' },
  '.jsonc': { variant: 'text', label: '{}', color: '#b45309' },
  '.jsonl': { variant: 'text', label: '{}', color: '#b45309' },
  '.yaml': { variant: 'text', label: 'YML', color: '#e11d48' },
  '.yml': { variant: 'text', label: 'YML', color: '#e11d48' },
  '.toml': { variant: 'text', label: 'TML', color: '#9a3412' },
  '.html': { variant: 'text', label: 'HTML', color: '#e34f26' },
  '.htm': { variant: 'text', label: 'HTML', color: '#e34f26' },
  '.css': { variant: 'text', label: 'CSS', color: '#264de4' },
  '.scss': { variant: 'text', label: 'SCSS', color: '#c6538c' },
  '.less': { variant: 'text', label: 'LESS', color: '#1d365d' },
  '.xml': { variant: 'text', label: 'XML', color: '#ea580c' },
  '.sql': { variant: 'text', label: 'SQL', color: '#0d9488' },
  '.sh': { variant: 'text', label: 'SH', color: '#475569' },
  '.bash': { variant: 'text', label: 'SH', color: '#475569' },
  '.zsh': { variant: 'text', label: 'SH', color: '#475569' },
  '.ps1': { variant: 'text', label: 'PS1', color: '#2563eb' },
  '.bat': { variant: 'text', label: 'BAT', color: '#475569' },
  '.cmd': { variant: 'text', label: 'CMD', color: '#475569' },
  '.tex': { variant: 'text', label: 'TEX', color: '#047857' },
  '.bib': { variant: 'text', label: 'BIB', color: '#047857' },
  '.pdf': { variant: 'text', label: 'PDF', color: '#dc2626' },
  '.csv': { variant: 'text', label: 'CSV', color: '#16a34a' },
  '.tsv': { variant: 'text', label: 'TSV', color: '#16a34a' },
  '.xlsx': { variant: 'text', label: 'XLS', color: '#16a34a' },
  '.xls': { variant: 'text', label: 'XLS', color: '#16a34a' },
  '.txt': { variant: 'text', label: 'TXT', color: '#64748b' },
  '.log': { variant: 'text', label: 'LOG', color: '#64748b' },
  '.png': { variant: 'image', label: 'IMG', color: '#7c3aed' },
  '.jpg': { variant: 'image', label: 'IMG', color: '#7c3aed' },
  '.jpeg': { variant: 'image', label: 'IMG', color: '#7c3aed' },
  '.gif': { variant: 'image', label: 'IMG', color: '#7c3aed' },
  '.webp': { variant: 'image', label: 'IMG', color: '#7c3aed' },
  '.svg': { variant: 'image', label: 'SVG', color: '#ea580c' },
  '.bmp': { variant: 'image', label: 'IMG', color: '#7c3aed' },
  '.ico': { variant: 'image', label: 'IMG', color: '#7c3aed' }
}

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value)
  } catch {
    return value
  }
}

function splitHrefParts(raw: string): { pathPart: string; fragment: string } {
  const hashIndex = raw.indexOf('#')
  const withoutHash = hashIndex >= 0 ? raw.slice(0, hashIndex) : raw
  const fragment = hashIndex >= 0 ? raw.slice(hashIndex + 1) : ''
  const queryIndex = withoutHash.indexOf('?')
  const pathPart = queryIndex >= 0 ? withoutHash.slice(0, queryIndex) : withoutHash
  return { pathPart, fragment }
}

function parseLineFromFragment(fragment: string): { line?: number; endLine?: number; column?: number } {
  const decoded = safeDecode(fragment).trim()
  const match = decoded.match(/^L(\d+)(?:[C:](\d+))?(?:-L?(\d+)(?:[C:]\d+)?)?$/i)
  if (!match) return {}
  const line = Number(match[1])
  const column = match[2] ? Number(match[2]) : undefined
  const endLine = match[3] ? Number(match[3]) : undefined
  return {
    line: Number.isFinite(line) && line > 0 ? line : undefined,
    endLine: endLine !== undefined && Number.isFinite(endLine) && endLine > 0 ? endLine : undefined,
    column: column !== undefined && Number.isFinite(column) && column > 0 ? column : undefined
  }
}

function stripTrailingLineColumn(pathStr: string): { path: string; line?: number; column?: number } {
  const match = pathStr.match(/^(.{2,}?):(\d+)(?::(\d+))?$/)
  if (!match) return { path: pathStr }
  const prefix = match[1]!
  const line = Number(match[2])
  const column = match[3] ? Number(match[3]) : undefined
  return {
    path: prefix,
    line: Number.isFinite(line) && line > 0 ? line : undefined,
    column: column !== undefined && Number.isFinite(column) && column > 0 ? column : undefined
  }
}

function normalizeSegments(segments: string[]): string[] {
  const out: string[] = []
  for (const seg of segments) {
    if (!seg || seg === '.') continue
    if (seg === '..') {
      if (out.length > 0 && out[out.length - 1] !== '..') {
        out.pop()
      }
      continue
    }
    out.push(seg)
  }
  return out
}

function normalizePath(rawPath: string, isWin: boolean): string {
  if (isWin) {
    let p = rawPath.replace(/\//g, '\\')
    if (p.startsWith('\\\\')) {
      const rest = p.slice(2).replace(/\\+/g, '\\')
      const parts = normalizeSegments(rest.split('\\').filter(Boolean))
      if (parts.length >= 2) {
        return `\\\\${parts.join('\\')}`
      }
      return `\\\\${rest}`
    }
    const driveMatch = p.match(/^([A-Za-z]:)(?:\\(.*))?$/)
    if (driveMatch) {
      const drive = driveMatch[1]!
      const rest = (driveMatch[2] ?? '').split('\\').filter(Boolean)
      const normalized = normalizeSegments(rest)
      return normalized.length ? `${drive}\\${normalized.join('\\')}` : `${drive}\\`
    }
    p = p.replace(/\\+/g, '\\')
    return p
  }

  const p = rawPath.replace(/\\/g, '/')
  const isAbs = p.startsWith('/')
  const normalized = normalizeSegments(p.split('/').filter(Boolean))
  if (isAbs) {
    return `/${normalized.join('/')}`
  }
  return normalized.join('/') || '.'
}

function joinAndNormalizeWorkspacePath(workspace: string, relPath: string, isWin: boolean): string {
  if (isWin) {
    const base = workspace.replace(/\//g, '\\').replace(/\\+$/, '')
    const rel = relPath.replace(/\//g, '\\')
    if (base.startsWith('\\\\')) {
      const baseParts = base.slice(2).split('\\').filter(Boolean)
      const serverShare = baseParts.slice(0, 2)
      const restBase = baseParts.slice(2)
      const relParts = rel.split('\\').filter(Boolean)
      const resolved = normalizeSegments([...restBase, ...relParts])
      return `\\\\${[...serverShare, ...resolved].join('\\')}`
    }
    const driveMatch = base.match(/^([A-Za-z]:)(?:\\(.*))?$/)
    if (driveMatch) {
      const drive = driveMatch[1]!
      const baseParts = (driveMatch[2] ?? '').split('\\').filter(Boolean)
      const relParts = rel.split('\\').filter(Boolean)
      const resolved = normalizeSegments([...baseParts, ...relParts])
      return resolved.length ? `${drive}\\${resolved.join('\\')}` : `${drive}\\`
    }
    return normalizePath(`${base}\\${rel}`, true)
  }

  const base = workspace.replace(/\\/g, '/').replace(/\/+$/, '')
  const rel = relPath.replace(/\\/g, '/')
  const isAbs = base.startsWith('/')
  const baseParts = base.split('/').filter(Boolean)
  const relParts = rel.split('/').filter(Boolean)
  const resolved = normalizeSegments([...baseParts, ...relParts])
  return isAbs ? `/${resolved.join('/')}` : resolved.join('/')
}

export function resolveMarkdownLinkTarget(
  rawHref: string,
  workspace?: string,
  platform: string = 'win32'
): ResolvedLinkTarget {
  const trimmed = rawHref.trim()
  if (!trimmed) {
    return { kind: 'unsupported', reason: '无效的空链接' }
  }

  if (trimmed.startsWith('#')) {
    return { kind: 'fragment', fragment: safeDecode(trimmed.slice(1)) }
  }

  if (/^(?:https?:\/\/|mailto:)/i.test(trimmed)) {
    return { kind: 'external', url: trimmed }
  }

  const isWin = platform === 'win32'

  if (/^file:\/\//i.test(trimmed)) {
    const { pathPart, fragment } = splitHrefParts(trimmed)
    const locFromFrag = parseLineFromFragment(fragment)
    let withoutScheme = pathPart.replace(/^file:\/\//i, '')
    if (/^localhost\//i.test(withoutScheme)) {
      withoutScheme = withoutScheme.slice('localhost'.length)
    } else if (!withoutScheme.startsWith('/')) {
      withoutScheme = `//${withoutScheme}`
    }
    let decoded = safeDecode(withoutScheme)
    if (isWin) {
      decoded = decoded.replace(/^\/+([A-Za-z]:[\\/])/, '$1')
    }
    const stripped = stripTrailingLineColumn(decoded)
    return {
      kind: 'local',
      path: normalizePath(stripped.path, isWin),
      line: locFromFrag.line ?? stripped.line,
      endLine: locFromFrag.endLine,
      column: locFromFrag.column ?? stripped.column
    }
  }

  const { pathPart, fragment } = splitHrefParts(trimmed)
  const locFromFrag = parseLineFromFragment(fragment)
  let decodedPath = safeDecode(pathPart)
  if (!decodedPath.trim()) {
    return { kind: 'unsupported', reason: '无效的链接路径' }
  }

  const stripped = stripTrailingLineColumn(decodedPath)
  decodedPath = stripped.path
  const line = locFromFrag.line ?? stripped.line
  const endLine = locFromFrag.endLine
  const column = locFromFrag.column ?? stripped.column

  if (isWin) {
    decodedPath = decodedPath.replace(/^\/+([A-Za-z]:[\\/])/, '$1')
  }

  if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(decodedPath) && !/^[A-Za-z]:[\\/]/.test(decodedPath)) {
    return { kind: 'unsupported', reason: '不支持的链接协议' }
  }

  if (/^[A-Za-z]:[\\/]/.test(decodedPath) || /^(\\\\|\/\/)/.test(decodedPath)) {
    return {
      kind: 'local',
      path: normalizePath(decodedPath, true),
      line,
      endLine,
      column
    }
  }

  if (decodedPath.startsWith('/')) {
    if (!isWin) {
      return {
        kind: 'local',
        path: normalizePath(decodedPath, false),
        line,
        endLine,
        column
      }
    }
    decodedPath = decodedPath.replace(/^\/+/, '')
    if (!decodedPath) {
      return { kind: 'unsupported', reason: '无效的链接路径' }
    }
  }

  const trimmedWorkspace = workspace?.trim() ?? ''
  if (!trimmedWorkspace || (/^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(trimmedWorkspace) && !/^file:\/\//i.test(trimmedWorkspace))) {
    return { kind: 'unsupported', reason: '当前会话未关联本地工作区，无法打开相对路径' }
  }

  const baseWorkspace = /^file:\/\//i.test(trimmedWorkspace)
    ? resolveMarkdownLinkTarget(trimmedWorkspace, undefined, platform)
    : null
  const workspacePath = baseWorkspace && baseWorkspace.kind === 'local' ? baseWorkspace.path : trimmedWorkspace

  return {
    kind: 'local',
    path: joinAndNormalizeWorkspacePath(workspacePath, decodedPath, isWin),
    line,
    endLine,
    column
  }
}

export function isExecutableLinkPath(filePath: string): boolean {
  const normalized = filePath.trim().replace(/[\\/]+$/, '')
  const lastSep = Math.max(normalized.lastIndexOf('/'), normalized.lastIndexOf('\\'))
  const base = lastSep >= 0 ? normalized.slice(lastSep + 1) : normalized
  const dot = base.lastIndexOf('.')
  if (dot <= 0) return false
  return EXECUTABLE_EXTENSIONS.has(base.slice(dot).toLowerCase())
}

function extractFileExtension(text: string): string {
  const clean = text.trim().replace(/[\\/]+$/, '')
  const lastSep = Math.max(clean.lastIndexOf('/'), clean.lastIndexOf('\\'))
  const base = lastSep >= 0 ? clean.slice(lastSep + 1) : clean
  const match = base.match(/(\.[A-Za-z0-9]{1,10})$/)
  return match ? match[1]!.toLowerCase() : ''
}

export function getLinkVisualMeta(rawHref: string, linkText?: string): LinkVisualMeta {
  const trimmed = rawHref.trim()
  if (/^https?:\/\//i.test(trimmed)) {
    return { kind: 'web', variant: 'web', label: '', color: '#3b82f6', ext: '' }
  }
  if (/^mailto:/i.test(trimmed)) {
    return { kind: 'mail', variant: 'mail', label: '', color: '#3b82f6', ext: '' }
  }
  if (trimmed.startsWith('#')) {
    return { kind: 'fragment', variant: 'file', label: '', color: '#64748b', ext: '' }
  }

  const { pathPart } = splitHrefParts(trimmed)
  const decoded = stripTrailingLineColumn(safeDecode(pathPart)).path
  if (/[\\/]$/.test(decoded) && !/^[A-Za-z]:[\\/]$/.test(decoded)) {
    return { kind: 'folder', variant: 'folder', label: '', color: '#d97706', ext: '' }
  }

  let ext = extractFileExtension(decoded)
  if (!ext && linkText) {
    const cleanText = stripTrailingLineColumn(splitHrefParts(linkText.trim()).pathPart).path
    ext = extractFileExtension(cleanText)
  }

  const mapped = ext ? EXT_VISUAL_MAP[ext] : undefined
  if (mapped) {
    return {
      kind: 'file',
      variant: mapped.variant,
      label: mapped.label,
      color: mapped.color,
      ext
    }
  }

  return {
    kind: 'file',
    variant: 'file',
    label: '',
    color: '#64748b',
    ext
  }
}

export function renderLinkIconHtml(meta: LinkVisualMeta): string {
  let inner = ''
  switch (meta.variant) {
    case 'text':
      inner = `<span class="md-file-badge" style="color:${meta.color}">${meta.label}</span>`
      break
    case 'vue':
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none"><path d="M1.2 2.4h3.2L8 8.7l3.6-6.3h3.2L8 14.1 1.2 2.4Z" fill="#41b883"/><path d="M4.4 2.4h2.3L8 4.8l1.3-2.4h2.3L8 8.7 4.4 2.4Z" fill="#35495e"/></svg>`
      break
    case 'md':
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none"><rect x="1" y="2.5" width="14" height="11" rx="2" stroke="#2563eb" stroke-width="1.3"/><path d="M3.4 10.6V5.4h1.3l1.5 2 1.5-2H9v5.2H7.7V7.4L6.2 9.3 4.7 7.4v3.2H3.4Zm7.9 0-1.7-2h1.1V5.4h1.2v3.2H13l-1.7 2Z" fill="#2563eb"/></svg>`
      break
    case 'image':
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="${meta.color}" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><rect x="1.8" y="2.5" width="12.4" height="11" rx="2"/><circle cx="5.4" cy="6" r="1.1" fill="${meta.color}" stroke="none"/><path d="m2.4 12 3.4-3.3a.8.8 0 0 1 1.1 0l2.1 2.1 1.6-1.5a.8.8 0 0 1 1.1 0l2 2"/></svg>`
      break
    case 'folder':
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="${meta.color}" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M1.8 4.2a1.4 1.4 0 0 1 1.4-1.4h3l1.3 1.5h5.3a1.4 1.4 0 0 1 1.4 1.4v6.1a1.4 1.4 0 0 1-1.4 1.4H3.2a1.4 1.4 0 0 1-1.4-1.4V4.2Z"/></svg>`
      break
    case 'web':
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="${meta.color}" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><circle cx="8" cy="8" r="6"/><path d="M2.2 8h11.6"/><path d="M8 2a9 9 0 0 1 2.4 6A9 9 0 0 1 8 14a9 9 0 0 1-2.4-6A9 9 0 0 1 8 2Z"/></svg>`
      break
    case 'mail':
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="${meta.color}" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><rect x="1.8" y="3.2" width="12.4" height="9.6" rx="1.6"/><path d="m2.3 4.2 5.7 4.2 5.7-4.2"/></svg>`
      break
    default:
      inner = `<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="${meta.color}" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><path d="M4 2h5.2L12.8 5.6V13a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1Z"/><path d="M9 2v3.8h3.8"/></svg>`
      break
  }
  return `<span class="md-link-icon" aria-hidden="true">${inner}</span>`
}
