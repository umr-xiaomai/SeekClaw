import { once } from 'node:events'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer, type Server } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import type { DaemonMessage } from '../shared/ipc.js'
import { DaemonClient } from './daemon-client.js'

const servers: Server[] = []
const clients: DaemonClient[] = []

afterEach(async () => {
  for (const client of clients.splice(0)) client.disconnect()
  await Promise.all(servers.splice(0).map((server) => new Promise<void>((resolve) => server.close(() => resolve()))))
})

describe('DaemonClient', () => {
  it('streams JSONL events and resolves on the terminal event', async () => {
    const suffix = `${process.pid}-${Date.now()}`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\seekclaw-test-${suffix}`
      : join(tmpdir(), `seekclaw-test-${suffix}.sock`)

    const server = createServer((socket) => {
      socket.setEncoding('utf8')
      socket.once('data', (chunk: string) => {
        const request = JSON.parse(chunk.trim()) as { id: number; method: string }
        expect(request.method).toBe('chat')
        socket.write(`${JSON.stringify({ id: request.id, event: 'thinking', data: 'checking' })}\n`)
        socket.write(`${JSON.stringify({ id: request.id, event: 'delta', data: 'hello' })}\n`)
        socket.write(`${JSON.stringify({ id: request.id, event: 'done', data: 'hello' })}\n`)
      })
    })
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    const events: DaemonMessage[] = []
    client.on('event', (event: DaemonMessage) => events.push(event))

    const response = await client.request('chat', { message: 'hello' })

    expect(response.event).toBe('done')
    expect(response.data).toBe('hello')
    expect(events.map((event) => event.event)).toEqual(['thinking', 'delta', 'done'])
  })
})

