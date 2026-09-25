import { describe, expect, it } from 'vitest'
import { formatTokenCount, normalizePath, pathName, samePath } from './app-helpers'

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
})
