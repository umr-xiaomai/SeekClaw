export type SshAuthMode = 'none' | 'identityFile' | 'password'

export interface SshConnectionInfo {
  id: string
  name: string
  host: string
  port: number
  authMode: SshAuthMode
  identityFile?: string | null
  passwordConfigured?: boolean
  configFile?: string | null
  createdAt?: string
  updatedAt?: string
}

export interface SshConnectionInput {
  id?: string
  name: string
  host: string
  port?: number
  authMode: SshAuthMode
  identityFile?: string
  password?: string
  configFile?: string
}

export interface SshConfigHostEntry {
  alias: string
  hostName?: string | null
  user?: string | null
  port?: number | null
  identityFile?: string | null
}

export interface SshTestResult {
  ok: boolean
  message: string
  remoteOs?: string | null
  remoteHome?: string | null
}

export interface SshFsEntry {
  name: string
  path: string
  isDirectory: boolean
  sizeBytes: number
}

export interface SshFsListResult {
  path: string
  parentPath: string | null
  entries: SshFsEntry[]
}

const SSH_PREFIX = 'ssh://'

export function isSshWorkspacePath(path?: string | null): boolean {
  return typeof path === 'string' && path.trimStart().toLowerCase().startsWith(SSH_PREFIX)
}

export function normalizeRemotePosixPath(path?: string | null): string {
  if (!path || !path.trim()) return '~'
  const raw = path.trim().replace(/\\/g, '/')
  if (raw === '/' || raw === '~') return raw
  const isAbs = raw.startsWith('/')
  const isHome = !isAbs && (raw === '~' || raw.startsWith('~/'))
  const body = isAbs ? raw.slice(1) : isHome ? (raw.length > 2 ? raw.slice(2) : '') : raw
  const stack: string[] = []
  for (const part of body.split('/')) {
    if (!part || part === '.') continue
    if (part === '..') {
      if (stack.length > 0 && stack[stack.length - 1] !== '..') stack.pop()
      else if (!isAbs && !isHome) stack.push('..')
      continue
    }
    stack.push(part)
  }
  if (isAbs) return stack.length === 0 ? '/' : `/${stack.join('/')}`
  if (isHome) return stack.length === 0 ? '~' : `~/${stack.join('/')}`
  return stack.length === 0 ? '.' : stack.join('/')
}

export function formatSshWorkspacePath(connectionId: string, remotePath = '~'): string {
  const cleanId = connectionId.trim().toLowerCase()
  const norm = normalizeRemotePosixPath(remotePath)
  if (norm === '/') return `${SSH_PREFIX}${cleanId}/`
  if (norm.startsWith('/')) return `${SSH_PREFIX}${cleanId}${norm}`
  return `${SSH_PREFIX}${cleanId}/${norm}`
}

export function parseSshWorkspacePath(uri?: string | null): { connectionId: string; remotePath: string } | null {
  if (!isSshWorkspacePath(uri)) return null
  const after = uri!.trim().slice(SSH_PREFIX.length)
  const slashIdx = after.indexOf('/')
  if (slashIdx < 0) {
    const connectionId = after.trim().toLowerCase()
    return connectionId ? { connectionId, remotePath: '~' } : null
  }
  const connectionId = after.slice(0, slashIdx).trim().toLowerCase()
  if (!connectionId) return null
  const rawRemote = after.slice(slashIdx)
  if (rawRemote.startsWith('/~') && (rawRemote.length === 2 || rawRemote[2] === '/')) {
    return { connectionId, remotePath: normalizeRemotePosixPath(rawRemote.slice(1)) }
  }
  return { connectionId, remotePath: normalizeRemotePosixPath(rawRemote) }
}
