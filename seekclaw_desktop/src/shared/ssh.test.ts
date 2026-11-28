import { describe, expect, it } from 'vitest'
import { formatSshWorkspacePath, isSshWorkspacePath, normalizeRemotePosixPath, parseSshWorkspacePath } from './ssh.js'

describe('shared ssh path helpers', () => {
  it('formats and parses ssh workspace URIs consistently', () => {
    expect(isSshWorkspacePath('ssh://srv1/home/ubuntu/app')).toBe(true)
    expect(isSshWorkspacePath('E:\\Project\\SeekClaw')).toBe(false)

    expect(formatSshWorkspacePath('SRV1', '~')).toBe('ssh://srv1/~')
    expect(formatSshWorkspacePath('srv1', '/')).toBe('ssh://srv1/')
    expect(formatSshWorkspacePath('srv1', '/var/www/app/')).toBe('ssh://srv1/var/www/app')

    expect(parseSshWorkspacePath('ssh://srv1/~')).toEqual({ connectionId: 'srv1', remotePath: '~' })
    expect(parseSshWorkspacePath('ssh://srv1/~/repo')).toEqual({ connectionId: 'srv1', remotePath: '~/repo' })
    expect(parseSshWorkspacePath('ssh://srv1/var/www/app')).toEqual({ connectionId: 'srv1', remotePath: '/var/www/app' })
    expect(normalizeRemotePosixPath('/home/u/a/../b/')).toBe('/home/u/b')
  })
})
