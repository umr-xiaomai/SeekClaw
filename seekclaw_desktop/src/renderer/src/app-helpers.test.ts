import { describe, expect, it } from 'vitest'
import { formatTokenCount, hydrateMessages, normalizePath, pathName, samePath } from './app-helpers'

describe('interrupted session hydration', () => {
  it('restores partial content and failure status while preserving legacy completed messages', () => {
    const messages = hydrateMessages({
      id: 'session', createdAt: '2026-11-03T00:00:00Z', updatedAt: '2026-11-03T00:00:00Z',
      messages: [
        { role: 'assistant', text: 'legacy answer' },
        { role: 'assistant', text: 'partial answer', thinking: 'partial thought', finishReason: 'error',
          errorMessage: 'ResponseEnded' },
        { role: 'assistant', text: '', finishReason: 'error', errorMessage: 'Connection failed' }
      ]
    })
    expect(messages[0]).toMatchObject({ content: 'legacy answer', state: 'done' })
    expect(messages[1]).toMatchObject({ content: 'partial answer', thinking: 'partial thought',
      state: 'error', errorMessage: 'ResponseEnded' })
    expect(messages[2]).toMatchObject({ content: '', state: 'error', errorMessage: 'Connection failed' })
  })
})

describe('formatTokenCount', () => {
  it('formats millions with M suffix correctly without unnecessary trailing zeros', () => {
    expect(formatTokenCount(1_280_000)).toBe('1.28M')
    expect(formatTokenCount(1_000_000)).toBe('1M')
    expect(formatTokenCount(2_000_000)).toBe('2M')
    expect(formatTokenCount(1_500_000)).toBe('1.5M')
    expect(formatTokenCount(10_000_000)).toBe('10M')
  })

  it('formats thousands with k suffix correctly', () => {
    expect(formatTokenCount(128_000)).toBe('128k')
    expect(formatTokenCount(64_000)).toBe('64k')
    expect(formatTokenCount(32_000)).toBe('32k')
    expect(formatTokenCount(8_192)).toBe('8k')
    expect(formatTokenCount(1_000)).toBe('1k')
  })

  it('falls back to 1M default when undefined', () => {
    expect(formatTokenCount(undefined)).toBe('1M')
    expect(formatTokenCount(0)).toBe('1M')
  })

  it('formats values under 1000 as plain numbers', () => {
    expect(formatTokenCount(512)).toBe('512')
  })
})

describe('path helpers', () => {
  it('normalizes and compares paths correctly', () => {
    expect(normalizePath('C:\\Project\\SeekClaw\\')).toBe('c:/project/seekclaw')
    expect(samePath('C:\\Project\\SeekClaw', 'c:/project/seekclaw/')).toBe(true)
    expect(pathName('C:\\Project\\SeekClaw')).toBe('SeekClaw')
  })
})

describe('attachment helpers', () => {
  it('formats prompt with attached files including filenames and absolute paths', async () => {
    const { formatPromptWithFiles, parseAttachmentsFromText } = await import('./app-helpers')
    const files = [
      { id: '1', name: '简历.pdf', path: 'C:\\Users\\zhang\\Documents\\简历.pdf', sizeBytes: 1024, extension: 'pdf' },
      { id: '2', name: 'config.ts', path: 'E:\\Project\\SeekClaw\\config.ts', sizeBytes: 2048, extension: 'ts' }
    ]
    const prompt = formatPromptWithFiles('帮我分析简历', files)
    expect(prompt).toContain('简历.pdf')
    expect(prompt).toContain('C:\\Users\\zhang\\Documents\\简历.pdf')
    expect(prompt).toContain('config.ts')
    expect(prompt).toContain('帮我分析简历')

    const parsed = parseAttachmentsFromText(prompt)
    expect(parsed.content).toBe('帮我分析简历')
    expect(parsed.files).toHaveLength(2)
    expect(parsed.files[0]!.name).toBe('简历.pdf')
    expect(parsed.files[0]!.path).toBe('C:\\Users\\zhang\\Documents\\简历.pdf')
    expect(parsed.files[0]!.extension).toBe('pdf')
    expect(parsed.files[1]!.name).toBe('config.ts')
    expect(parsed.files[1]!.extension).toBe('ts')
  })

  it('handles empty content with default instruction', async () => {
    const { formatPromptWithFiles, parseAttachmentsFromText } = await import('./app-helpers')
    const files = [
      { id: '1', name: 'doc.txt', path: 'C:\\doc.txt', sizeBytes: 100, extension: 'txt' }
    ]
    const prompt = formatPromptWithFiles('', files)
    expect(prompt).toContain('请直接查看、分析并处理以上附加的文件。')
    const parsed = parseAttachmentsFromText(prompt)
    expect(parsed.content).toBe('')
    expect(parsed.files).toHaveLength(1)
  })
})

describe('formatMessageTime', () => {
  it('formats timestamp into M月d日 H:mm format', async () => {
    const { formatMessageTime } = await import('./app-helpers')
    // 2026-09-15 02:11:00 UTC -> Local time
    const date = new Date(2026, 8, 15, 2, 11) // September is month index 8
    expect(formatMessageTime(date.getTime())).toBe('9月15日 2:11')

    const date2 = new Date(2026, 8, 26, 14, 5)
    expect(formatMessageTime(date2.getTime())).toBe('9月26日 14:05')
  })

  it('returns empty string for invalid or missing timestamp', async () => {
    const { formatMessageTime } = await import('./app-helpers')
    expect(formatMessageTime(undefined)).toBe('')
    expect(formatMessageTime(0)).toBe('')
    expect(formatMessageTime(NaN)).toBe('')
  })
})

describe('hydrateMessages', () => {
  it('keeps live turn keys and raw transcript positions after saving tool and internal records', () => {
    const hydrated = hydrateMessages({
      id: 'session', createdAt: '2026-11-24T00:00:00Z', updatedAt: '2026-11-24T00:00:03Z',
      messages: [
        { role: 'user', text: '检查项目' },
        { role: 'user', text: '>>> [System Directive: continue working]' },
        { role: 'assistant', text: '检查文件。', toolCalls: [{ id: 'read-1', name: 'read_file' }] },
        { role: 'tool', text: 'Contents', toolCallId: 'read-1', toolName: 'read_file' },
        { role: 'assistant', text: '已经核对。' },
        { role: 'user', text: '再检查一次' },
        { role: 'assistant', text: '再次核对完成。' }
      ]
    }, [
      { id: 'live-user', role: 'user', content: '检查项目', createdAt: 1 },
      { id: 'live-tool', role: 'assistant', content: '检查文件。', createdAt: 2,
        tools: [{ id: 'read-1', name: 'read_file', state: 'done' }] },
      { id: 'live-answer', role: 'assistant', content: '已经核对。', createdAt: 3 },
      { id: 'live-followup', role: 'user', content: '再检查一次', createdAt: 4 },
      { id: 'live-followup-answer', role: 'assistant', content: '再次核对完成。', createdAt: 5 }
    ])
    expect(hydrated.map(({ id, transcriptIndex }) => [id, transcriptIndex])).toEqual([
      ['live-user', 0], ['session:1', 1], ['live-tool', 2], ['live-answer', 4],
      ['live-followup', 5], ['live-followup-answer', 6]
    ])
  })

  it('preserves message timestamp from backend', async () => {
    const { hydrateMessages } = await import('./app-helpers')
    const ts1 = 1757890000000
    const ts2 = 1757890060000
    const hydrated = hydrateMessages({
      id: 'session-1',
      createdAt: '2026-09-15T02:11:00Z',
      updatedAt: '2026-09-15T02:12:00Z',
      messages: [
        { role: 'user', text: 'hello', timestamp: ts1 },
        { role: 'assistant', text: 'hi', timestamp: ts2 }
      ]
    })
    expect(hydrated).toHaveLength(2)
    expect(hydrated[0]!.createdAt).toBe(ts1)
    expect(hydrated[1]!.createdAt).toBe(ts2)
  })

  it('preserves errorMessage when hydrating messages', async () => {
    const { hydrateMessages } = await import('./app-helpers')
    const hydrated = hydrateMessages({
      id: 'session-err',
      createdAt: '2026-09-15T02:11:00Z',
      updatedAt: '2026-09-15T02:12:00Z',
      messages: [
        { role: 'assistant', text: '', errorMessage: '503 Service Unavailable' } as any
      ]
    })
    expect(hydrated[0]!.errorMessage).toBe('503 Service Unavailable')
  })
})

describe('messageMatches', () => {
  it('matches search query in errorMessage', async () => {
    const { messageMatches } = await import('./app-helpers')
    const msg = {
      id: 'm1',
      role: 'assistant' as const,
      content: '',
      errorMessage: 'unexpected status 503 Service Unavailable',
      createdAt: 0
    }
    expect(messageMatches(msg, '503')).toBe(true)
    expect(messageMatches(msg, 'Unavailable')).toBe(true)
    expect(messageMatches(msg, '404')).toBe(false)
  })
})

describe('loadStoredSidebarWidth', () => {
  it('returns default 240 when storage is empty', async () => {
    const { loadStoredSidebarWidth, DEFAULT_SIDEBAR_WIDTH } = await import('./app-helpers')
    const mockStorage = {
      getItem: () => null,
      setItem: () => {}
    }
    expect(loadStoredSidebarWidth(mockStorage)).toBe(DEFAULT_SIDEBAR_WIDTH)
    expect(DEFAULT_SIDEBAR_WIDTH).toBe(240)
  })

  it('reads valid v2 width from storage', async () => {
    const { loadStoredSidebarWidth } = await import('./app-helpers')
    const mockStorage = {
      getItem: (key: string) => key === 'seekclaw-sidebar-width-v2' ? '220' : null,
      setItem: () => {}
    }
    expect(loadStoredSidebarWidth(mockStorage)).toBe(220)
  })

  it('migrates user custom v1 width if valid and not old 300 default', async () => {
    const { loadStoredSidebarWidth } = await import('./app-helpers')
    let savedKey = ''
    let savedVal = ''
    const mockStorage = {
      getItem: (key: string) => key === 'seekclaw-sidebar-width-v1' ? '210' : null,
      setItem: (key: string, val: string) => {
        savedKey = key
        savedVal = val
      }
    }
    expect(loadStoredSidebarWidth(mockStorage)).toBe(210)
    expect(savedKey).toBe('seekclaw-sidebar-width-v2')
    expect(savedVal).toBe('210')
  })

  it('replaces old bloated 300 v1 width with new 240 default', async () => {
    const { loadStoredSidebarWidth } = await import('./app-helpers')
    const mockStorage = {
      getItem: (key: string) => key === 'seekclaw-sidebar-width-v1' ? '300' : null,
      setItem: () => {}
    }
    expect(loadStoredSidebarWidth(mockStorage)).toBe(240)
  })
})

describe('hydrated tool diff line counts and queued attachment summary', () => {
  it('hydrates addedLines and removedLines from persisted toolDiff', () => {
    const messages = hydrateMessages({
      id: 'session-diff',
      createdAt: '2026-11-17T00:00:00Z',
      updatedAt: '2026-11-17T00:00:00Z',
      messages: [
        { role: 'assistant', text: 'Updated file' },
        {
          role: 'tool',
          toolCallId: 'c1',
          toolName: 'edit_file',
          text: 'ok',
          toolFilePath: 'src/a.ts',
          toolDiff: '--- a/src/a.ts\n+++ b/src/a.ts\n@@ -1,2 +1,3 @@\n keep\n-old\n+new1\n+new2'
        }
      ]
    })

    const tool = messages[0]?.tools?.[0]
    expect(tool).toMatchObject({
      filePath: 'src/a.ts',
      addedLines: 2,
      removedLines: 1
    })
  })

  it('builds queued attachment badge summary only when text coexists with attachments', async () => {
    const { queuedMessageAttachmentSummary } = await import('./app-helpers')
    expect(queuedMessageAttachmentSummary({
      id: 'q1',
      content: '看看这两个文件',
      images: [{ id: 'i1', name: 'a.png', mediaType: 'image/png', data: '', sizeBytes: 10 }],
      files: [{ id: 'f1', name: 'b.ts', path: '/b.ts', sizeBytes: 20, extension: 'ts' }],
      createdAt: 1
    })).toBe('1 图 · 1 文件')
    expect(queuedMessageAttachmentSummary({
      id: 'q2',
      content: '',
      images: [{ id: 'i1', name: 'a.png', mediaType: 'image/png', data: '', sizeBytes: 10 }],
      createdAt: 1
    })).toBe('')
  })
})

