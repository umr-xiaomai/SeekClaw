import { createHash } from 'node:crypto'
import { EventEmitter } from 'node:events'
import type { IncomingMessage } from 'node:http'
import type { Duplex } from 'node:stream'

const WS_MAGIC = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
const MAX_FRAME_BYTES = 16 * 1024 * 1024

export class MinimalWebSocket extends EventEmitter {
  private buffer: Buffer<ArrayBufferLike> = Buffer.alloc(0)
  private closed = false
  private fragmentChunks: Buffer[] = []
  private fragmentOpcode = 0

  constructor(private readonly socket: Duplex) {
    super()
    socket.on('data', (chunk: Buffer) => this.handleData(chunk))
    socket.on('close', () => this.handleClose())
    socket.on('end', () => this.handleClose())
    socket.on('error', (error: Error) => {
      if (!this.closed && this.listenerCount('error') > 0) this.emit('error', error)
      this.handleClose()
    })
  }

  get isOpen(): boolean {
    return !this.closed && !this.socket.destroyed && this.socket.writable
  }

  sendJson(payload: unknown): void {
    this.sendText(JSON.stringify(payload))
  }

  sendText(text: string): void {
    this.sendFrame(0x1, Buffer.from(text, 'utf8'))
  }

  ping(): void {
    this.sendFrame(0x9, Buffer.alloc(0))
  }

  close(code = 1000, reason = ''): void {
    if (this.closed) return
    const reasonBuf = reason ? Buffer.from(reason, 'utf8') : Buffer.alloc(0)
    const body = Buffer.alloc(2 + reasonBuf.length)
    body.writeUInt16BE(code, 0)
    if (reasonBuf.length > 0) reasonBuf.copy(body, 2)
    try {
      this.sendFrame(0x8, body)
    } catch {
      // ignore write error during close
    }
    this.closed = true
    try {
      this.socket.end()
    } catch {
      // ignore end error on aborted socket
    }
    this.emit('close')
  }

  private sendFrame(opcode: number, payload: Buffer): void {
    if (!this.isOpen) return
    const len = payload.length
    let header: Buffer
    if (len < 126) {
      header = Buffer.alloc(2)
      header[0] = 0x80 | opcode
      header[1] = len
    } else if (len < 65536) {
      header = Buffer.alloc(4)
      header[0] = 0x80 | opcode
      header[1] = 126
      header.writeUInt16BE(len, 2)
    } else {
      header = Buffer.alloc(10)
      header[0] = 0x80 | opcode
      header[1] = 127
      header.writeBigUInt64BE(BigInt(len), 2)
    }
    try {
      this.socket.write(Buffer.concat([header, payload]), (error) => {
        if (error) this.handleClose()
      })
    } catch {
      this.handleClose()
    }
  }

  private handleClose(): void {
    if (this.closed) return
    this.closed = true
    this.buffer = Buffer.alloc(0)
    this.fragmentChunks = []
    this.emit('close')
  }

  private handleData(chunk: Buffer): void {
    if (this.closed) return
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk])

    while (this.buffer.length >= 2) {
      const b0 = this.buffer[0]!
      const b1 = this.buffer[1]!
      const fin = (b0 & 0x80) !== 0
      const opcode = b0 & 0x0f
      const masked = (b1 & 0x80) !== 0
      let payloadLen = b1 & 0x7f
      let offset = 2

      if (payloadLen === 126) {
        if (this.buffer.length < 4) return
        payloadLen = this.buffer.readUInt16BE(2)
        offset = 4
      } else if (payloadLen === 127) {
        if (this.buffer.length < 10) return
        const bigLen = this.buffer.readBigUInt64BE(2)
        if (bigLen > BigInt(MAX_FRAME_BYTES)) {
          this.close(1009, 'Payload too large')
          return
        }
        payloadLen = Number(bigLen)
        offset = 10
      }

      if (payloadLen > MAX_FRAME_BYTES) {
        this.close(1009, 'Payload too large')
        return
      }

      const maskLen = masked ? 4 : 0
      if (this.buffer.length < offset + maskLen + payloadLen) return

      let payload = this.buffer.subarray(offset + maskLen, offset + maskLen + payloadLen)
      if (masked) {
        const mask = this.buffer.subarray(offset, offset + 4)
        const unmasked = Buffer.allocUnsafe(payloadLen)
        for (let i = 0; i < payloadLen; i++) {
          unmasked[i] = payload[i]! ^ mask[i & 3]!
        }
        payload = unmasked
      } else {
        payload = Buffer.from(payload)
      }

      this.buffer = this.buffer.subarray(offset + maskLen + payloadLen)

      if (opcode === 0x8) {
        this.close(1000)
        return
      }
      if (opcode === 0x9) {
        this.sendFrame(0xA, payload)
        continue
      }
      if (opcode === 0xA) {
        continue
      }

      if (opcode === 0x1 || opcode === 0x2) {
        if (fin) {
          if (opcode === 0x1) this.emit('message', payload.toString('utf8'))
        } else {
          this.fragmentOpcode = opcode
          this.fragmentChunks = [payload]
        }
        continue
      }

      if (opcode === 0x0 && this.fragmentChunks.length > 0) {
        const currentTotal = this.fragmentChunks.reduce((sum, item) => sum + item.length, 0)
        if (currentTotal + payload.length > MAX_FRAME_BYTES) {
          this.close(1009, 'Fragmented payload too large')
          return
        }
        this.fragmentChunks.push(payload)
        if (fin) {
          const joined = Buffer.concat(this.fragmentChunks)
          const origOpcode = this.fragmentOpcode
          this.fragmentChunks = []
          this.fragmentOpcode = 0
          if (origOpcode === 0x1) this.emit('message', joined.toString('utf8'))
        }
      }
    }
  }
}

export function upgradeToWebSocket(
  req: IncomingMessage,
  socket: Duplex,
  head: Buffer
): MinimalWebSocket | null {
  const preUpgradeErrorGuard = (): void => {
    try {
      socket.destroy()
    } catch {
      // ignore destroy error
    }
  }
  socket.on('error', preUpgradeErrorGuard)

  const key = req.headers['sec-websocket-key']
  const upgrade = req.headers.upgrade
  if (
    typeof key !== 'string'
    || !key.trim()
    || typeof upgrade !== 'string'
    || upgrade.toLowerCase() !== 'websocket'
  ) {
    try {
      socket.write('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n')
    } catch {
      // ignore write error during rejected upgrade
    }
    socket.destroy()
    return null
  }

  const accept = createHash('sha1').update(`${key.trim()}${WS_MAGIC}`).digest('base64')
  try {
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n'
      + 'Upgrade: websocket\r\n'
      + 'Connection: Upgrade\r\n'
      + `Sec-WebSocket-Accept: ${accept}\r\n\r\n`
    )
  } catch {
    socket.destroy()
    return null
  }
  const ws = new MinimalWebSocket(socket)
  socket.removeListener('error', preUpgradeErrorGuard)
  if (head.length > 0) socket.emit('data', head)
  return ws
}
