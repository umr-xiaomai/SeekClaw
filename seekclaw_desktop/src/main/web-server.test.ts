import { createServer } from 'node:net'
import { EventEmitter } from 'node:events'
import { mkdtemp, rm, mkdir, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import type { AppInfo } from '../shared/ipc.js'
import type { DaemonClient } from './daemon-client.js'
import { PendingApprovalCache } from './pending-approval-cache.js'
import { LanWebServer } from './web-server.js'

const cleanups: Array<() => Promise<void>> = []

async function pickFreePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer()
    server.once('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const addr = server.address()
      const port = typeof addr === 'object' && addr ? addr.port : 19420
      server.close(() => resolve(port))
    })
  })
}

class FakeDaemon extends EventEmitter {
  state = { connected: true, endpoint: 'mock://daemon' }
  requests: Array<{ method: string; params?: Record<string, unknown> }> = []
  async connect() {
    this.state = { connected: true, endpoint: 'mock://daemon' }
    return this.state
  }
  disconnect() {
    this.state = { connected: false, endpoint: 'mock://daemon' }
  }
  async request(method: string, params?: Record<string, unknown>) {
    this.requests.push({ method, params })
    if (method === 'project.list') {
      return { id: '1', event: 'result', data: JSON.stringify({ projects: [] }) }
    }
    return { id: '1', event: 'result', data: JSON.stringify({ ok: true, method }) }
  }
}

afterEach(async () => {
  while (cleanups.length > 0) {
    const fn = cleanups.pop()!
    await fn().catch(() => undefined)
  }
})

describe('LanWebServer', () => {
  it('enforces auth cookie, locks out repeated bad tokens, serves API routes, and supports token regeneration', async () => {
    const userData = await mkdtemp(join(tmpdir(), 'seekclaw-web-srv-'))
    const distDir = join(userData, 'dist')
    await mkdir(distDir, { recursive: true })
    await writeFile(join(distDir, 'index.html'), '<!doctype html><html><body>SeekClaw Web</body></html>')
    const port = await pickFreePort()
    const daemon = new FakeDaemon()
    const syncReasons: string[] = []

    const server = new LanWebServer({
      userDataPath: userData,
      daemon: daemon as unknown as DaemonClient,
      pendingApprovals: new PendingApprovalCache(),
      getBrowser: () => null,
      ensureBrowserAttached: async () => undefined,
      getAppInfo: () => ({
        version: '0.1.0',
        platform: process.platform as AppInfo['platform'],
        supportsMica: false,
        defaultWorkspace: userData,
        documentsPath: userData,
        userProfilePath: userData
      }),
      getRendererSource: () => ({ distDir }),
      onDesktopSyncRefresh: (reason) => syncReasons.push(reason)
    })

    cleanups.push(async () => {
      await server.stop()
      await rm(userData, { recursive: true, force: true })
    })

    await server.init()
    const status = await server.updateConfig({ enabled: true, host: '127.0.0.1', port, token: 'test-token-1234' })
    expect(status.running).toBe(true)
    const baseUrl = `http://127.0.0.1:${port}`

    // Serves static index.html
    const htmlRes = await fetch(`${baseUrl}/`)
    expect(htmlRes.status).toBe(200)
    expect(await htmlRes.text()).toContain('SeekClaw Web')

    // Rejects unauthenticated API call
    const unauthInfo = await fetch(`${baseUrl}/api/app/info`)
    expect(unauthInfo.status).toBe(401)

    // Rate limit locks out after 5 wrong tokens
    for (let i = 0; i < 4; i++) {
      const bad = await fetch(`${baseUrl}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: 'wrong-token' })
      })
      expect(bad.status).toBe(401)
    }
    const lockout = await fetch(`${baseUrl}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: 'wrong-token' })
    })
    expect(lockout.status).toBe(429)

    // Still accepts Bearer token auth cleanly on API routes & propagates mutating sync
    const infoRes = await fetch(`${baseUrl}/api/app/info`, {
      headers: { Authorization: 'Bearer test-token-1234' }
    })
    expect(infoRes.status).toBe(200)
    const info = (await infoRes.json()) as { runtimeMode: string }
    expect(info.runtimeMode).toBe('web')

    for (const readMethod of ['project.list', 'config.status', 'provider.list', 'session.list', 'mcp.list', 'skill.list', 'memory.list']) {
      const readRes = await fetch(`${baseUrl}/api/daemon/request`, {
        method: 'POST',
        headers: {
          Authorization: 'Bearer test-token-1234',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ method: readMethod, params: {} })
      })
      expect(readRes.status).toBe(200)
    }
    expect(syncReasons).toEqual([])

    const reqRes = await fetch(`${baseUrl}/api/daemon/request`, {
      method: 'POST',
      headers: {
        Authorization: 'Bearer test-token-1234',
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ method: 'config.save', params: { k: 1 } })
    })
    expect(reqRes.status).toBe(200)
    const upsertRes = await fetch(`${baseUrl}/api/daemon/request`, {
      method: 'POST',
      headers: {
        Authorization: 'Bearer test-token-1234',
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ method: 'project.upsert', params: { id: 'p1' } })
    })
    expect(upsertRes.status).toBe(200)
    expect(syncReasons).toEqual(['config.save', 'project.upsert'])

    // Regenerating token invalidates previous token immediately
    const regenStatus = await server.regenerateToken()
    expect(regenStatus.config.token).not.toBe('test-token-1234')
    const staleBearer = await fetch(`${baseUrl}/api/app/info`, {
      headers: { Authorization: 'Bearer test-token-1234' }
    })
    expect(staleBearer.status).toBe(401)
  })

  it('keeps enabled synced with running on restart and honors autoStart', async () => {
    const userData = await mkdtemp(join(tmpdir(), 'seekclaw-web-restart-'))
    const distDir = join(userData, 'dist')
    await mkdir(distDir, { recursive: true })
    await writeFile(join(distDir, 'index.html'), '<!doctype html><html><body>SeekClaw Restart</body></html>')
    const port = await pickFreePort()
    const daemon = new FakeDaemon()

    const createServerInstance = (): LanWebServer =>
      new LanWebServer({
        userDataPath: userData,
        daemon: daemon as unknown as DaemonClient,
        pendingApprovals: new PendingApprovalCache(),
        getBrowser: () => null,
        ensureBrowserAttached: async () => undefined,
        getAppInfo: () => ({
          version: '0.1.0',
          platform: process.platform as AppInfo['platform'],
          supportsMica: false,
          defaultWorkspace: userData,
          documentsPath: userData,
          userProfilePath: userData
        }),
        getRendererSource: () => ({ distDir })
      })

    const servers: LanWebServer[] = []
    cleanups.push(async () => {
      for (const instance of servers) {
        await instance.stop()
      }
      await rm(userData, { recursive: true, force: true })
    })

    // 1. Start with enabled=true, autoStart=false, then simulate app restart
    const first = createServerInstance()
    servers.push(first)
    await first.init()
    const started = await first.updateConfig({
      enabled: true,
      host: '127.0.0.1',
      port,
      token: 'restart-token-1234',
      autoStart: false
    })
    expect(started.running).toBe(true)
    expect(started.config.enabled).toBe(true)
    await first.stop()

    // On restart without autoStart, enabled must reflect running=false
    const second = createServerInstance()
    servers.push(second)
    const afterRestartNoAuto = await second.init()
    expect(afterRestartNoAuto.running).toBe(false)
    expect(afterRestartNoAuto.config.enabled).toBe(false)
    expect(afterRestartNoAuto.config.autoStart).toBe(false)

    // Updating host while stopped must keep running=false
    const patchedStopped = await second.updateConfig({ host: '127.0.0.1' })
    expect(patchedStopped.running).toBe(false)
    expect(patchedStopped.config.enabled).toBe(false)

    // 2. Enable autoStart (even while currently stopped), then simulate restart
    await second.updateConfig({ autoStart: true })
    await second.stop()

    const third = createServerInstance()
    servers.push(third)
    const afterRestartAuto = await third.init()
    expect(afterRestartAuto.running).toBe(true)
    expect(afterRestartAuto.config.enabled).toBe(true)
    expect(afterRestartAuto.config.autoStart).toBe(true)
    expect(afterRestartAuto.urls).toContain(`http://127.0.0.1:${port}`)

    const htmlRes = await fetch(`http://127.0.0.1:${port}/`)
    expect(htmlRes.status).toBe(200)
  })
})
