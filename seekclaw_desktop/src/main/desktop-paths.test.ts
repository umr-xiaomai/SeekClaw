import { mkdirSync } from 'node:fs'
import { homedir, platform } from 'node:os'
import { posix, win32 } from 'node:path'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { configureDesktopPaths } from './desktop-paths.js'

vi.mock('node:fs', () => ({ mkdirSync: vi.fn() }))
vi.mock('node:os', () => ({ homedir: vi.fn(), platform: vi.fn() }))
beforeEach(() => vi.clearAllMocks())
afterEach(() => vi.unstubAllEnvs())

it.each([
  ['win32', String.raw`C:\Test Users\override`, String.raw`C:\Test Users\override`],
  ['win32', String.raw`\not-fully-qualified`, String.raw`C:\Test Users\default`],
  ['linux', undefined, '/home/test-user'],
  ['darwin', undefined, '/Users/test-user']
] as const)('initializes the Desktop profile on %s with USERPROFILE=%s', (os, override, profile) => {
  const isWindows = os === 'win32'
  const { join } = isWindows ? win32 : posix
  vi.stubEnv('USERPROFILE', override)
  vi.mocked(platform).mockReturnValue(os)
  vi.mocked(homedir).mockReturnValue(isWindows ? '/unused' : profile)
  const seekClawHome = join(profile, '.seekclaw')
  const desktop = join(seekClawHome, 'desktop')
  const session = join(desktop, 'session')
  const logs = join(desktop, 'logs')
  const crashDumps = join(desktop, 'crash-dumps')
  const dotnetBundle = join(desktop, 'dotnet_bundle')
  const expectDirectoryCreated = (path: string): void => {
    expect(mkdirSync).toHaveBeenCalledWith(path, { recursive: true, mode: 0o700 })
  }
  const getPath = vi.fn((_name: string) => isWindows ? String.raw`C:\Test Users\default` : '/unused')
  const setPath = vi.fn((_name: string, path: string) => expectDirectoryCreated(path))
  const setAppLogsPath = vi.fn(expectDirectoryCreated)

  const paths = configureDesktopPaths({ getPath, setPath, setAppLogsPath })

  expect(getPath.mock.calls).toEqual(isWindows && override !== profile ? [['home']] : [])
  expect(paths.userProfile).toBe(profile)
  expect(setPath.mock.calls).toEqual([
    ['userData', desktop],
    ['sessionData', session],
    ['crashDumps', crashDumps]
  ])
  expect(setAppLogsPath.mock.calls).toEqual([[logs]])
  expectDirectoryCreated(dotnetBundle)
  expect(paths.dotnetBundle).toBe(dotnetBundle)
  expect(paths.daemonEndpoint).toBe(isWindows
    ? String.raw`\\.\pipe\seekclaw`
    : join(seekClawHome, 'daemon.sock'))
})
