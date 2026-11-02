import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { hydrateMessages, sessionScope, type RuntimeSession } from './app-helpers'
import { buildMcpServerPayload, mcpFormFromServer, type McpServerSummary } from './mcp-form'
import { reloadThreadSession } from './session-sync'
import type { ThreadItem } from './types'

afterEach(() => vi.unstubAllGlobals())

describe('task runtime data', () => {
  it('restores typed cards from saved tool results', () => {
    const widget = { version: 1, kind: 'artifact', id: 'artifact:demo', title: 'Demo', path: 'E:\\demo.html', mediaType: 'text/html' }
    const session: RuntimeSession = {
      id: 'session', createdAt: '2026-01-01', updatedAt: '2026-01-01', messages: [
        { role: 'assistant', text: '', toolCalls: [{ id: 'call', name: 'present_artifact' }] },
        { role: 'tool', text: 'Presented demo', toolCallId: 'call', toolName: 'present_artifact', toolWidgets: [widget] }
      ]
    }
    expect(hydrateMessages(session)[0]!.tools![0]!.widgets).toEqual([widget])
  })

  it.each(['bash', 'read_file'])('restores multiline %s output with null MCP fields', (name) => {
    const output = 'first line\nsecond line\n'
    const session: RuntimeSession = {
      id: 'session', createdAt: '2026-01-01', updatedAt: '2026-01-01',
      messages: [
        { role: 'assistant', text: '', toolCalls: [{ id: 'call', name, detail: 'original arguments' }] },
        { role: 'tool', text: output, toolCallId: 'call', toolName: name, toolSuccess: true,
          toolContent: null, toolStructuredContent: null, toolMetadata: null }
      ]
    }
    const tool = hydrateMessages(session)[0]!.tools![0]!
    expect(tool).toMatchObject({ callId: 'call', state: 'done', detail: 'original arguments', output })
    expect(tool.content).toBeUndefined()
    expect(tool.structuredContent).toBeUndefined()
    expect(tool.metadata).toBeUndefined()
  })

  it.each([false, 0])('preserves the structured result %j during hydration', (value) => {
    const session: RuntimeSession = {
      id: 'session', createdAt: '2026-01-01', updatedAt: '2026-01-01',
      messages: [
        { role: 'assistant', text: '', toolCalls: [{ id: 'call', name: 'mcp__test' }] },
        { role: 'tool', text: String(value), toolCallId: 'call', toolName: 'mcp__test', toolStructuredContent: value }
      ]
    }
    expect(hydrateMessages(session)[0]!.tools![0]!.structuredContent).toBe(value)
  })

  it('restores all MCP content and associates results with their original tool call', () => {
    const content = [
      { type: 'audio', mimeType: 'audio/wav', data: 'AQID' },
      { type: 'resource_link', uri: 'custom://report', name: 'report' },
      { type: 'future', value: { supportedLater: true } }
    ]
    const session: RuntimeSession = {
      id: 'session', createdAt: '2026-01-01', updatedAt: '2026-01-01',
      messages: [
        { role: 'assistant', text: '', toolCalls: [{ id: 'call', name: 'mcp__test' }] },
        { role: 'tool', text: 'result', toolCallId: 'call', toolName: 'mcp__test', toolSuccess: false,
          toolContent: content, toolStructuredContent: { answer: 42 }, toolMetadata: { vendor: 'test' } }
      ]
    }
    const result = hydrateMessages(session)
    expect(result).toHaveLength(1)
    expect(result[0]!.tools).toHaveLength(1)
    expect(result[0]!.tools![0]).toMatchObject({
      callId: 'call', state: 'error', content, structuredContent: { answer: 42 }, metadata: { vendor: 'test' }
    })
  })

  it('uses the source scope to load a worktree conversation and retains its execution path', async () => {
    const source = 'E:\\source'
    const checkout = 'E:\\worktrees\\task'
    const request = vi.fn().mockResolvedValue({ data: JSON.stringify({
      id: 'session', createdAt: '2026-01-01', updatedAt: '2026-01-01', messages: [],
      executionWorkspace: checkout, worktree: { path: checkout, status: 'active' }
    }) })
    vi.stubGlobal('window', { seekclaw: { daemon: { request } } })
    const thread: ThreadItem = { id: 'thread', sessionId: 'session', projectId: 'project', title: 'task', updatedAt: 0, messages: [] }
    const project = { id: 'project', name: 'source', path: source }
    await reloadThreadSession({ daemonState: ref({ connected: true, endpoint: 'test' }) }, thread, project)
    expect(request).toHaveBeenCalledWith('session.get', { id: 'session', workspace: source })
    expect(thread.executionWorkspace).toBe(checkout)
    expect(thread.worktree?.status).toBe('active')
    expect(sessionScope(thread, project)).toEqual({ workspace: source })
  })

  it('round trips credential references and OAuth settings through the editor without resolving secrets', () => {
    const server: McpServerSummary = {
      name: 'remote', scope: 'global', transport: 'http', url: 'https://example.test/mcp', args: [], envKeys: [],
      enabled: true, connected: false, toolCount: 0, bearerTokenEnv: 'MCP_TOKEN',
      envHeaders: { 'X-Api-Key': 'MCP_API_KEY' }, oauth: {
        clientId: 'client', authorizationEndpoint: 'https://example.test/authorize', tokenEndpoint: 'https://example.test/token',
        scope: 'tools', clientSecretEnv: 'MCP_CLIENT_SECRET'
      }
    }
    const payload = buildMcpServerPayload(mcpFormFromServer(server))
    expect(payload).toMatchObject({ bearerTokenEnv: 'MCP_TOKEN', envHeaders: server.envHeaders, oauth: server.oauth })
    expect(JSON.parse(JSON.stringify(payload))).toEqual(payload)
  })
})
