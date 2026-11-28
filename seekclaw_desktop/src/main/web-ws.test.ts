import { PassThrough } from 'node:stream'
import type { IncomingMessage } from 'node:http'
import { describe, expect, it } from 'vitest'
import { MinimalWebSocket, upgradeToWebSocket } from './web-ws.js'

function buildMaskedFrame(opcode: number, payload: Buffer, fin = true): Buffer {
  const mask = Buffer.from([0x12, 0x34, 0x56, 0x78])
  const len = payload.length
  const header =
    len < 126
      ? Buffer.from([fin ? 0x80 | opcode : opcode, 0x80 | len])
      : Buffer.from([fin ? 0x80 | opcode : opcode, 0x80 | 126, (len >> 8) & 0xff, len & 0xff])
  const masked = Buffer.allocUnsafe(len)
  for (let i = 0; i < len; i++) {
    masked[i] = payload[i]! ^ mask[i & 3]!
  }
  return Buffer.concat([header, mask, masked])
}

describe('MinimalWebSocket', () => {
  it('upgrades a valid HTTP WebSocket request with expected Sec-WebSocket-Accept', () => {
    const socket = new PassThrough() as PassThrough & { writable: boolean }
    const written: Buffer[] = []
    socket.on('data', (chunk: Buffer) => written.push(chunk))

    const req = {
      headers: {
        upgrade: 'websocket',
        'sec-websocket-key': 'dGhlIHNhbXBsZSBub25jZQ=='
      }
    } as unknown as IncomingMessage

    const ws = upgradeToWebSocket(req, socket, Buffer.alloc(0))
    expect(ws).toBeInstanceOf(MinimalWebSocket)
    const response = Buffer.concat(written).toString('utf8')
    expect(response).toContain('HTTP/1.1 101 Switching Protocols')
    expect(response).toContain('Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=')
    ws?.close()
  })

  it('decodes masked text frames, handles fragmented frames, and answers ping with pong', () => {
    const socket = new PassThrough()
    const outChunks: Buffer[] = []
    const origWrite = socket.write.bind(socket)
    socket.write = ((chunk: unknown, ...rest: unknown[]) => {
      outChunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(String(chunk)))
      return origWrite(chunk as string, ...(rest as []))
    }) as typeof socket.write

    const ws = new MinimalWebSocket(socket)
    const messages: string[] = []
    ws.on('message', (msg: string) => messages.push(msg))

    socket.emit('data', buildMaskedFrame(0x1, Buffer.from('hello world', 'utf8'), true))
    expect(messages).toEqual(['hello world'])

    socket.emit('data', buildMaskedFrame(0x1, Buffer.from('part1-', 'utf8'), false))
    socket.emit('data', buildMaskedFrame(0x0, Buffer.from('part2', 'utf8'), true))
    expect(messages).toEqual(['hello world', 'part1-part2'])

    socket.emit('data', buildMaskedFrame(0x9, Buffer.from('hi', 'utf8'), true))
    const pong = outChunks.find((buf) => (buf[0]! & 0x0f) === 0x0a)
    expect(pong).toBeDefined()
    expect(pong!.subarray(2).toString('utf8')).toBe('hi')
  })

  it('closes cleanly without throwing uncaught exceptions on ECONNABORTED during write', () => {
    const socket1 = new PassThrough()
    socket1.write = ((_chunk: unknown, cb?: (err?: Error | null) => void) => {
      const err = Object.assign(new Error('write ECONNABORTED'), { code: 'ECONNABORTED' })
      if (typeof cb === 'function') cb(err)
      socket1.emit('error', err)
      return false
    }) as typeof socket1.write

    // Intentionally attach NO 'error' listener on MinimalWebSocket
    const ws1 = new MinimalWebSocket(socket1)
    let closedCount1 = 0
    ws1.on('close', () => {
      closedCount1++
    })

    expect(() => ws1.sendText('hello')).not.toThrow()
    expect(ws1.isOpen).toBe(false)
    expect(closedCount1).toBe(1)

    const socket2 = new PassThrough()
    socket2.write = (() => {
      throw Object.assign(new Error('write ECONNABORTED'), { code: 'ECONNABORTED' })
    }) as typeof socket2.write

    const ws2 = new MinimalWebSocket(socket2)
    let closedCount2 = 0
    ws2.on('close', () => {
      closedCount2++
    })

    expect(() => ws2.sendText('hello')).not.toThrow()
    expect(ws2.isOpen).toBe(false)
    expect(closedCount2).toBe(1)
  })
})
