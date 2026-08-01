import { EventEmitter } from 'node:events'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createConnection, type Socket } from 'node:net'
import type { DaemonMessage, DaemonRequestOptions, DaemonState } from '../shared/ipc.js'

interface PendingRequest {
  method: string
  resolve: (message: DaemonMessage) => void
  reject: (error: Error) => void
  /** Safety net that rejects the request if the daemon never answers at all. */
  idleTimer?: NodeJS.Timeout
}

const TERMINAL_EVENTS = new Set(['pong', 'result', 'done', 'cancelled', 'error', 'bye'])

export class DaemonClient extends EventEmitter {
  private socket: Socket | null = null
  private buffer = ''
  private nextId = 1
  private connecting: Promise<DaemonState> | null = null
  private readonly pending = new Map<number, PendingRequest>()

  constructor(readonly endpoint = process.platform === 'win32'
    ? String.raw`\\.\pipe\seekclaw`
    : join(homedir(), '.seekclaw', 'daemon.sock')) {
    super()
  }

  get state(): DaemonState {
    return { connected: this.socket?.readyState === 'open', endpoint: this.endpoint }
  }

  async connect(): Promise<DaemonState> {
    if (this.socket?.readyState === 'open') return this.state
    if (this.connecting) return this.connecting

    this.connecting = new Promise<DaemonState>((resolve) => {
      const socket = createConnection(this.endpoint)
      let settled = false

      const finish = (state: DaemonState): void => {
        if (settled) return
        settled = true
        this.connecting = null
        this.emit('state', state)
        resolve(state)
      }

      socket.setEncoding('utf8')
      socket.setTimeout(1600)
      socket.once('connect', () => {
        socket.setTimeout(0)
        this.socket = socket
        this.bindSocket(socket)
        finish({ connected: true, endpoint: this.endpoint })
      })
      socket.once('timeout', () => {
        socket.destroy()
        finish({ connected: false, endpoint: this.endpoint, error: 'Connection timed out' })
      })
      socket.once('error', (error) => {
        finish({ connected: false, endpoint: this.endpoint, error: error.message })
      })
    })

    return this.connecting
  }

  disconnect(): void {
    this.socket?.destroy()
    this.socket = null
    this.rejectPending(new Error('Daemon disconnected'))
    this.emit('state', { connected: false, endpoint: this.endpoint } satisfies DaemonState)
  }

  async request(
    method: string,
    params: Record<string, unknown> = {},
    options: DaemonRequestOptions = {}
  ): Promise<DaemonMessage> {
    const state = await this.connect()
    if (!state.connected || !this.socket)
      throw new Error(state.error ?? `Unable to connect to ${this.endpoint}`)

    const id = this.nextId++
    return new Promise<DaemonMessage>((resolve, reject) => {
      const pending: PendingRequest = { method, resolve, reject }
      this.pending.set(id, pending)
      // A request that never produces any event (for example a chat turn the
      // daemon never started) must not leave the UI waiting forever. Any first
      // event or terminal response clears the timer; the turn is then confirmed
      // to be running and may legitimately take minutes.
      if (options.timeoutMs) {
        pending.idleTimer = setTimeout(() => {
          if (this.pending.delete(id) === false) return
          reject(new Error(
            `请求超时：Runtime 在 ${Math.round(options.timeoutMs! / 1000)} 秒内未响应`))
        }, options.timeoutMs)
      }
      this.socket!.write(`${JSON.stringify({ id, method, params })}\n`, (error) => {
        if (!error) return
        if (pending.idleTimer) clearTimeout(pending.idleTimer)
        this.pending.delete(id)
        reject(error)
      })
    })
  }

  private bindSocket(socket: Socket): void {
    socket.on('data', (chunk: string) => this.consume(chunk))
    socket.on('close', () => {
      if (this.socket !== socket) return
      this.socket = null
      this.buffer = ''
      this.rejectPending(new Error('Daemon connection closed'))
      this.emit('state', { connected: false, endpoint: this.endpoint } satisfies DaemonState)
    })
    socket.on('error', (error) => {
      if (this.socket !== socket) return
      this.socket = null
      this.buffer = ''
      socket.destroy()
      this.rejectPending(new Error(`Daemon connection failed: ${error.message}`))
      this.emit('state', {
        connected: false,
        endpoint: this.endpoint,
        error: error.message
      } satisfies DaemonState)
    })
  }

  private consume(chunk: string): void {
    this.buffer += chunk
    let newline = this.buffer.indexOf('\n')
    while (newline >= 0) {
      const line = this.buffer.slice(0, newline).trim()
      this.buffer = this.buffer.slice(newline + 1)
      if (line) this.consumeLine(line)
      newline = this.buffer.indexOf('\n')
    }
  }

  private consumeLine(line: string): void {
    let message: DaemonMessage
    try {
      message = JSON.parse(line) as DaemonMessage
    } catch {
      return
    }

    const request = this.pending.get(message.id)
    if (request?.idleTimer) {
      clearTimeout(request.idleTimer)
      request.idleTimer = undefined
    }
    const event = request ? { ...message, requestMethod: request.method } : message
    this.emit('event', event)
    if (!TERMINAL_EVENTS.has(message.event)) return

    if (!request) return
    this.pending.delete(message.id)
    if (message.event === 'error') request.reject(new Error(message.data))
    else request.resolve(event)
  }

  private rejectPending(error: Error): void {
    for (const request of this.pending.values()) {
      if (request.idleTimer) clearTimeout(request.idleTimer)
      request.reject(error)
    }
    this.pending.clear()
  }
}
