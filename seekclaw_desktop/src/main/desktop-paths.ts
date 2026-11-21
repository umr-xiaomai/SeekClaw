import { mkdirSync } from 'node:fs'
import { homedir, platform } from 'node:os'
import { posix, win32 } from 'node:path'
import type { App } from 'electron'

export function configureDesktopPaths(app: Pick<App, 'getPath' | 'setPath' | 'setAppLogsPath'>): {
  userProfile: string
  daemonEndpoint: string
  dotnetBundle: string
} {
  const isWindows = platform() === 'win32'
  const { isAbsolute, join, normalize } = isWindows ? win32 : posix
  const configuredProfile = isWindows ? process.env.USERPROFILE : undefined
  const userProfile = configuredProfile && win32.isAbsolute(configuredProfile) && win32.parse(configuredProfile).root.length > 1
    ? normalize(configuredProfile)
    : isWindows ? app.getPath('home') : homedir()
  if (!isAbsolute(userProfile)) throw new Error('Could not resolve an absolute SeekClaw user profile path')

  const seekClawHome = join(userProfile, '.seekclaw')
  const userData = join(seekClawHome, 'desktop')
  const sessionData = join(userData, 'session')
  const logs = join(userData, 'logs')
  const crashDumps = join(userData, 'crash-dumps')
  const dotnetBundle = join(userData, 'dotnet_bundle')

  for (const directory of [userData, sessionData, logs, crashDumps, dotnetBundle]) {
    mkdirSync(directory, { recursive: true, mode: 0o700 })
  }
  app.setPath('userData', userData)
  app.setPath('sessionData', sessionData)
  app.setAppLogsPath(logs)
  app.setPath('crashDumps', crashDumps)

  return {
    userProfile,
    daemonEndpoint: isWindows ? String.raw`\\.\pipe\seekclaw` : join(seekClawHome, 'daemon.sock'),
    dotnetBundle
  }
}
