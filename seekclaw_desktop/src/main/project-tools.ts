import { execFile, spawn } from 'node:child_process'
import { existsSync, realpathSync, statSync, unlinkSync } from 'node:fs'
import { readdir, stat } from 'node:fs/promises'
import { basename, isAbsolute, join, relative, resolve, sep } from 'node:path'
import { promisify } from 'node:util'
import type { CheckoutBranchResult, CreateBranchResult, GitBranchInfo, GitCommit, GitHistory, GitOverview, RevertDiffItem, RevertDiffsResult, WorkspaceFileItem } from '../shared/ipc.js'

const execFileAsync = promisify(execFile)

async function workspaceDirectory(path: string): Promise<string> {
  const directory = resolve(path)
  const info = await stat(directory)
  if (!info.isDirectory()) throw new Error(`Workspace is not a directory: ${directory}`)
  return directory
}

async function git(directory: string, args: string[]): Promise<string> {
  const result = await execFileAsync('git', args, {
    cwd: directory,
    encoding: 'utf8',
    maxBuffer: 8 * 1024 * 1024,
    windowsHide: true
  })
  return result.stdout.trimEnd()
}

function detail(error: unknown): string {
  if (!(error instanceof Error)) return String(error)
  const candidate = error as Error & { stderr?: string }
  return candidate.stderr?.trim() || error.message
}

function friendlyGitError(error: unknown): string {
  const message = detail(error)
  return /not a git repository/i.test(message) ? '当前目录没有 Git 仓库信息' : message
}

export function parseGitLog(output: string): GitCommit[] {
  return output
    .split('\x1e')
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => {
      const [hash = '', shortHash = '', author = '', authoredAt = '', ...subject] = entry.split('\x1f')
      return { hash, shortHash, author, authoredAt, subject: subject.join('\x1f') }
    })
    .filter((commit) => commit.hash.length > 0)
}

export async function getGitOverview(path: string): Promise<GitOverview> {
  const directory = await workspaceDirectory(path)
  try {
    const [root, branch, upstream, status, unstaged, staged] = await Promise.all([
      git(directory, ['rev-parse', '--show-toplevel']),
      git(directory, ['branch', '--show-current']),
      git(directory, ['rev-parse', '--abbrev-ref', '@{u}']).catch(() => ''),
      git(directory, ['status', '--short']),
      git(directory, ['diff', '--no-ext-diff', '--no-color', '--unified=3']),
      git(directory, ['diff', '--cached', '--no-ext-diff', '--no-color', '--unified=3'])
    ])
    return {
      isRepository: true,
      root,
      branch: branch || 'detached HEAD',
      upstream: upstream.trim() || undefined,
      status: status ? status.split(/\r?\n/) : [],
      diff: [staged && '# Staged changes\n' + staged, unstaged && '# Working tree changes\n' + unstaged]
        .filter(Boolean).join('\n\n')
    }
  } catch (error) {
    return { isRepository: false, root: directory, branch: '', status: [], diff: '', error: friendlyGitError(error) }
  }
}

export async function getGitHistory(path: string): Promise<GitHistory> {
  const directory = await workspaceDirectory(path)
  try {
    const output = await git(directory, [
      'log', '-n', '100', '--date=iso-strict', '--pretty=format:%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1e'
    ])
    return { commits: parseGitLog(output) }
  } catch (error) {
    return { commits: [], error: friendlyGitError(error) }
  }
}

export async function getGitCommitDiff(path: string, commitHash: string): Promise<string> {
  const directory = await workspaceDirectory(path)
  try {
    const output = await git(directory, [
      'show',
      '--no-ext-diff',
      '--no-color',
      '--unified=3',
      commitHash
    ])
    return output
  } catch (error) {
    throw new Error(friendlyGitError(error))
  }
}

async function spawnDetached(command: string, args: string[], cwd: string): Promise<void> {
  await new Promise<void>((resolveSpawn, reject) => {
    const child = spawn(command, args, { cwd, detached: true, stdio: 'ignore', windowsHide: false })
    child.once('spawn', () => {
      child.unref()
      resolveSpawn()
    })
    child.once('error', reject)
  })
}

async function isCommandAvailableWindows(cmd: string): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    try {
      const child = spawn('where.exe', [cmd], { stdio: 'ignore', windowsHide: true })
      child.once('close', (code) => resolve(code === 0))
      child.once('error', () => resolve(false))
    } catch {
      resolve(false)
    }
  })
}

export async function openProjectTerminal(path: string): Promise<void> {
  const directory = await workspaceDirectory(path)
  if (process.platform === 'win32') {
    // 1. If Windows Terminal is available, open it via cmd /c start
    if (await isCommandAvailableWindows('wt.exe')) {
      try {
        const child = spawn('cmd.exe', ['/c', 'start', '', 'wt.exe', '-d', directory], {
          cwd: directory,
          detached: true,
          stdio: 'ignore',
          windowsHide: true
        })
        child.unref()
        return
      } catch {
        // Fallback to powershell below
      }
    }

    // 2. PowerShell via cmd /c start allocates a visible, interactive console window
    try {
      const escaped = directory.replaceAll("'", "''")
      const child = spawn('cmd.exe', [
        '/c',
        'start',
        '',
        '/d',
        directory,
        'powershell.exe',
        '-NoExit',
        '-Command',
        `Set-Location -LiteralPath '${escaped}'`
      ], {
        cwd: directory,
        detached: true,
        stdio: 'ignore',
        windowsHide: true
      })
      child.unref()
      return
    } catch {
      // 3. Fallback to Command Prompt
      const child = spawn('cmd.exe', ['/c', 'start', '', '/d', directory, 'cmd.exe', '/k'], {
        cwd: directory,
        detached: true,
        stdio: 'ignore',
        windowsHide: true
      })
      child.unref()
      return
    }
  }
  if (process.platform === 'darwin') {
    await spawnDetached('open', ['-a', 'Terminal', directory], directory)
    return
  }

  const terminals: Array<[string, string[]]> = [
    ['x-terminal-emulator', ['--working-directory', directory]],
    ['gnome-terminal', ['--working-directory', directory]],
    ['konsole', ['--workdir', directory]]
  ]
  let lastError: unknown
  for (const [command, args] of terminals) {
    try {
      await spawnDetached(command, args, directory)
      return
    } catch (error) {
      lastError = error
    }
  }
  throw lastError instanceof Error ? lastError : new Error(`No supported terminal found on ${process.platform}`)
}

export function normalizeDiffForGit(diff: string): string {
  let normalized = diff.replace(/\r\n/g, '\n')
  normalized = normalized.replace(/^([+-]{3} [ab])[/\\](.*)$/gm, (_match, prefix, filePath) => {
    return prefix + '/' + filePath.replace(/\\/g, '/')
  })
  if (!normalized.endsWith('\n')) {
    normalized += '\n'
  }
  return normalized
}

function cleanupNewFileIfEmpty(directory: string, patch: RevertDiffItem): void {
  if (/@@ -[01],0 \+/.test(patch.diff)) {
    const target = isAbsolute(patch.filePath) ? patch.filePath : resolve(directory, patch.filePath)
    if (existsSync(target)) {
      const root = realpathSync(directory)
      const physicalTarget = realpathSync(target)
      const targetRelative = relative(root, physicalTarget)
      if (targetRelative === '..'
        || targetRelative.startsWith(`..${sep}`)
        || isAbsolute(targetRelative)) return
      const stats = statSync(physicalTarget)
      if (stats.size === 0) {
        unlinkSync(physicalTarget)
      }
    }
  }
}

async function applyPatchReverse(directory: string, patch: RevertDiffItem): Promise<void> {
  const normalizedDiff = normalizeDiffForGit(patch.diff)
  await new Promise<void>((resolvePromise, rejectPromise) => {
    const child = spawn('git', ['apply', '--reverse', '--whitespace=nowarn'], {
      cwd: directory,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe']
    })

    let stderr = ''
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', (chunk) => {
      stderr += chunk
    })

    child.once('error', (err) => {
      rejectPromise(err)
    })

    child.once('close', (code) => {
      if (code === 0) {
        try {
          cleanupNewFileIfEmpty(directory, patch)
        } catch {
          // ignore cleanup error
        }
        resolvePromise()
      } else {
        const message = stderr.trim() || `git apply failed with exit code ${code}`
        rejectPromise(new Error(message))
      }
    })

    child.stdin.end(normalizedDiff, 'utf8')
  })
}

export async function revertFileDiffs(
  workspacePath: string,
  patches: RevertDiffItem[]
): Promise<RevertDiffsResult> {
  let directory: string
  try {
    directory = await workspaceDirectory(workspacePath)
  } catch (error) {
    return {
      reverted: [],
      failed: [{ filePath: workspacePath, reason: detail(error) }]
    }
  }

  // Reverse chronological order: newest edits reverted first
  const reversed = [...patches].reverse()
  const failedMap = new Map<string, string>()
  const revertedSet = new Set<string>()

  for (const patch of reversed) {
    if (!patch.diff || !patch.diff.trim()) continue
    try {
      await applyPatchReverse(directory, patch)
      revertedSet.add(patch.filePath)
    } catch (error) {
      const raw = detail(error)
      const reason = /ENOENT/i.test(raw) ? '系统未安装或未配置 Git' : raw
      failedMap.set(patch.filePath, reason)
    }
  }

  const reverted = Array.from(revertedSet).filter((filePath) => !failedMap.has(filePath))
  const failed = Array.from(failedMap.entries()).map(([filePath, reason]) => ({ filePath, reason }))

  return { reverted, failed }
}


export async function getGitBranches(path: string): Promise<GitBranchInfo[]> {
  const directory = await workspaceDirectory(path)
  try {
    const output = await git(directory, [
      'branch', '-a', '--format=%(refname:short)%09%(refname)%09%(HEAD)%09%(upstream:short)'
    ])
    const lines = output.split(/\r?\n/).filter(Boolean)
    const branches: GitBranchInfo[] = []
    for (const line of lines) {
      const [shortName = '', refname = '', head = '', upstream = ''] = line.split('\t')
      if (refname.endsWith('/HEAD') || !shortName) continue
      const isRemote = refname.startsWith('refs/remotes/')
      const current = head.trim() === '*'
      branches.push({
        name: shortName,
        current,
        isRemote,
        upstream: upstream.trim() || undefined
      })
    }
    return branches
  } catch {
    return []
  }
}

export async function checkoutGitBranch(path: string, branch: string): Promise<CheckoutBranchResult> {
  const directory = await workspaceDirectory(path)
  try {
    let target = branch.trim()
    if (target.startsWith('origin/')) {
      const localName = target.replace(/^origin\//, '')
      await git(directory, ['checkout', '--track', target]).catch(async () => {
        await git(directory, ['checkout', localName])
      })
      return { success: true, branch: localName }
    }
    await git(directory, ['checkout', target])
    return { success: true, branch: target }
  } catch (error) {
    return { success: false, error: friendlyGitError(error) }
  }
}

export async function createGitBranch(path: string, branch: string): Promise<CreateBranchResult> {
  const directory = await workspaceDirectory(path)
  const name = branch.trim()
  if (!name) return { success: false, error: '分支名称不能为空' }
  try {
    await git(directory, ['checkout', '-b', name])
    return { success: true, branch: name }
  } catch (error) {
    return { success: false, error: friendlyGitError(error) }
  }
}

const IGNORED_DIRS = new Set([
  '.git',
  'node_modules',
  'dist',
  'out',
  'build',
  'bin',
  'obj',
  '.vs',
  '.idea',
  '.next',
  '.nuxt',
  'coverage',
  '__pycache__',
  '.venv',
  'venv'
])

/** Lists non-ignored files under a workspace directory for composer @-mention autocomplete. */
export async function listWorkspaceFiles(path: string, query = '', limit = 40): Promise<WorkspaceFileItem[]> {
  if (!path?.trim()) return []
  let directory: string
  try {
    directory = await workspaceDirectory(path)
  } catch {
    return []
  }

  const needle = query.trim().toLowerCase()
  const maxScannedEntries = 3000
  const maxCollected = 1500
  const candidates: Array<{ item: WorkspaceFileItem; score: number }> = []
  const queue: string[] = [directory]
  let scanned = 0

  while (queue.length > 0 && scanned < maxScannedEntries && candidates.length < maxCollected) {
    const current = queue.shift()!
    let entries
    try {
      entries = await readdir(current, { withFileTypes: true })
    } catch {
      continue
    }

    for (const entry of entries) {
      if (scanned++ >= maxScannedEntries) break
      if (entry.name.startsWith('.') && entry.name !== '.env' && entry.name !== '.gitignore') {
        if (entry.isDirectory()) continue
      }
      if (entry.isDirectory()) {
        if (!IGNORED_DIRS.has(entry.name)) {
          queue.push(join(current, entry.name))
        }
        continue
      }
      if (!entry.isFile()) continue

      const fullPath = join(current, entry.name)
      const rel = relative(directory, fullPath).replace(/\\/g, '/')
      const lowerRel = rel.toLowerCase()
      const lowerName = entry.name.toLowerCase()

      let score = 100
      if (needle) {
        if (lowerName === needle) score = 0
        else if (lowerName.startsWith(needle)) score = 1
        else if (lowerName.includes(needle)) score = 2
        else if (lowerRel.includes(needle)) score = 3
        else continue
      } else {
        score = rel.split('/').length
      }

      let sizeBytes = 0
      try {
        const info = await stat(fullPath)
        sizeBytes = info.size
      } catch {
        // Ignore stat failures on transient files
      }

      candidates.push({
        item: {
          name: basename(fullPath),
          relativePath: rel,
          path: fullPath,
          sizeBytes
        },
        score
      })
    }
  }

  candidates.sort((a, b) => a.score - b.score || a.item.relativePath.length - b.item.relativePath.length || a.item.relativePath.localeCompare(b.item.relativePath))
  return candidates.slice(0, Math.max(1, limit)).map((entry) => entry.item)
}
