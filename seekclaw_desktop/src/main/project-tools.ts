import { execFile, spawn } from 'node:child_process'
import { stat } from 'node:fs/promises'
import { resolve } from 'node:path'
import { promisify } from 'node:util'
import type { GitCommit, GitHistory, GitOverview } from '../shared/ipc.js'

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
    const [root, branch, status, unstaged, staged] = await Promise.all([
      git(directory, ['rev-parse', '--show-toplevel']),
      git(directory, ['branch', '--show-current']),
      git(directory, ['status', '--short']),
      git(directory, ['diff', '--no-ext-diff', '--no-color', '--unified=3']),
      git(directory, ['diff', '--cached', '--no-ext-diff', '--no-color', '--unified=3'])
    ])
    return {
      isRepository: true,
      root,
      branch: branch || 'detached HEAD',
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

export async function openProjectTerminal(path: string): Promise<void> {
  const directory = await workspaceDirectory(path)
  if (process.platform === 'win32') {
    try {
      await spawnDetached('wt.exe', ['-d', directory], directory)
    } catch {
      const escaped = directory.replaceAll("'", "''")
      await spawnDetached('powershell.exe', ['-NoExit', '-Command', `Set-Location -LiteralPath '${escaped}'`], directory)
    }
    return
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
