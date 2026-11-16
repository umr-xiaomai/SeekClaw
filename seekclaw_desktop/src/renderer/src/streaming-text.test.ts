import { describe, expect, it, vi } from 'vitest'
import { createStreamingText } from './streaming-text'

function setup(initialText = '') {
  let time = 0
  let sequence = 0
  let text = initialText
  const frames = new Map<number, FrameRequestCallback>()
  const outputs: Array<{ text: string; time: number }> = []
  const onText = vi.fn((next: string) => { text = next; outputs.push({ text, time }) })
  const controller = createStreamingText({
    initialText, onText, now: () => time,
    requestFrame: (callback) => { const id = ++sequence; frames.set(id, callback); return id },
    cancelFrame: (id) => { frames.delete(id) }
  })
  return {
    ...controller, onText, outputs,
    text: () => text,
    pendingFrames: () => frames.size,
    advance: (duration: number) => {
      const end = time + duration
      while (time < end) {
        time = Math.min(end, time + 16)
        const current = [...frames.values()]
        frames.clear()
        for (const callback of current) callback(time)
      }
    }
  }
}

describe('streaming text playback', () => {
  it('leaves the initial answer visible and animates only appended text', () => {
    const stream = setup('Earlier answer. ')
    expect(stream.text()).toBe('Earlier answer. ')
    expect(stream.pendingFrames()).toBe(0)
    expect(stream.onText).not.toHaveBeenCalled()

    stream.update('Earlier answer. More words arrive.', true)
    stream.advance(64)
    expect(stream.text().startsWith('Earlier answer. ')).toBe(true)
    expect(stream.text().length).toBeGreaterThan('Earlier answer. '.length)
    expect(stream.text().length).toBeLessThan('Earlier answer. More words arrive.'.length)
    stream.advance(200)
    expect(stream.text()).toBe('Earlier answer. More words arrive.')
    expect(stream.pendingFrames()).toBe(0)
  })

  it('releases a single character without waiting for another chunk', () => {
    const stream = setup()
    stream.update('字', true)
    stream.advance(240)

    expect(stream.text()).toBe('字')
    expect(stream.pendingFrames()).toBe(0)
    expect(stream.onText).toHaveBeenCalledExactlyOnceWith('字')
  })

  it('keeps earlier progress when another chunk arrives before playback finishes', () => {
    const stream = setup()
    stream.update('abcdefghij', true)
    stream.advance(64)
    const before = stream.text()
    stream.update('abcdefghijklmnopqrst', true)
    stream.advance(96)

    expect(stream.text().startsWith(before)).toBe(true)
    expect(stream.text().length).toBeGreaterThanOrEqual(10)
    stream.advance(240)
    expect(stream.text()).toBe('abcdefghijklmnopqrst')
  })

  it('does not starve under uninterrupted chunks and publishes at most every 32ms', () => {
    const stream = setup()
    let target = ''
    for (let index = 0; index < 80; index++) {
      target += `${index}: `
      stream.update(target, true)
      stream.advance(16)
      expect(target.startsWith(stream.text())).toBe(true)
    }

    expect(stream.outputs.length).toBeGreaterThan(20)
    expect(stream.text().length).toBeGreaterThan(target.length / 2)
    for (let index = 1; index < stream.outputs.length; index++) {
      expect(stream.outputs[index]!.time - stream.outputs[index - 1]!.time).toBeGreaterThanOrEqual(32)
    }
    stream.advance(240)
    expect(stream.text()).toBe(target)
    expect(stream.pendingFrames()).toBe(0)
  })

  it('adapts to slow chunks but keeps their display delay bounded', () => {
    const stream = setup()
    stream.update('First ', true)
    stream.advance(1000)
    stream.update('First slower chunk.', true)
    stream.advance(240)

    expect(stream.text()).toBe('First slower chunk.')
    expect(stream.pendingFrames()).toBe(0)
  })

  it('catches up quickly when a very large chunk arrives', () => {
    const stream = setup()
    const target = 'a'.repeat(10000)
    stream.update(target, true)
    stream.advance(32)

    expect(target.length - stream.text().length).toBeLessThanOrEqual(512)
    stream.advance(240)
    expect(stream.text()).toBe(target)
    expect(stream.pendingFrames()).toBe(0)
  })

  it('accepts a whole long response without exceeding call-argument limits', () => {
    const stream = setup()
    const target = 'x'.repeat(250000)

    expect(() => stream.update(target, true)).not.toThrow()
    stream.advance(240)
    expect(stream.text()).toBe(target)
    expect(stream.pendingFrames()).toBe(0)
  })

  it('immediately applies replacements, truncations and disabled animation', () => {
    const stream = setup('prefix ')
    stream.update('prefix pending content', true)
    stream.update('Replacement', true)
    expect(stream.text()).toBe('Replacement')
    expect(stream.pendingFrames()).toBe(0)

    stream.update('Re', true)
    expect(stream.text()).toBe('Re')
    stream.update('Re full content', false)
    expect(stream.text()).toBe('Re full content')
    expect(stream.pendingFrames()).toBe(0)
  })

  it('flushes immediately, avoids duplicate output and cancels disposal without publishing stale text', () => {
    const stream = setup()
    stream.update('Pending answer', true)
    stream.flush()
    expect(stream.text()).toBe('Pending answer')
    expect(stream.pendingFrames()).toBe(0)
    stream.flush()
    stream.update('Pending answer', false)
    expect(stream.onText).toHaveBeenCalledTimes(1)

    stream.update('Pending answer with stale tail', true)
    stream.dispose()
    stream.advance(1000)
    stream.flush()
    stream.update('Ignored', false)
    expect(stream.text()).toBe('Pending answer')
    expect(stream.pendingFrames()).toBe(0)
  })

  it('publishes only complete graphemes, including emoji, accents, flags and CRLF', () => {
    const stream = setup()
    const target = 'A😀e\u0301👩🏽‍💻🇨🇳\r\nZ'
    const boundaries = new Set(Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(target),
      (part) => part.index + part.segment.length))
    stream.update(target, true)
    stream.advance(240)

    expect(stream.text()).toBe(target)
    for (const output of stream.outputs) {
      expect(boundaries.has(output.text.length)).toBe(true)
      expect(output.text.isWellFormed()).toBe(true)
      expect(() => encodeURIComponent(output.text)).not.toThrow()
    }
  })

  it('waits without spinning on a split surrogate, then releases the completed emoji', () => {
    const stream = setup()
    stream.update('A\ud83d', true)
    stream.advance(240)
    expect(stream.text()).toBe('A')
    expect(stream.pendingFrames()).toBe(0)

    stream.update('A😀B', true)
    stream.advance(240)
    expect(stream.text()).toBe('A😀B')
    for (const output of stream.outputs) expect(() => encodeURIComponent(output.text)).not.toThrow()
  })

  it('holds a trailing joiner until it is completed, and sanitizes malformed text when flushed', () => {
    const stream = setup()
    stream.update('👩', true)
    stream.advance(240)
    expect(stream.text()).toBe('👩')
    stream.update('👩\u200d', true)
    stream.advance(240)
    expect(stream.text()).toBe('👩')
    expect(stream.pendingFrames()).toBe(0)

    stream.update('👩‍💻', true)
    stream.advance(240)
    expect(stream.text()).toBe('👩‍💻')

    stream.update('👩‍💻\ud83d', true)
    stream.flush()
    expect(stream.text()).toBe('👩‍💻\ufffd')
    expect(() => encodeURIComponent(stream.text())).not.toThrow()
  })

  it('re-segments a chunk boundary that gains a combining suffix', () => {
    const stream = setup()
    stream.update('e', true)
    stream.advance(48)
    stream.update('e\u0301!', true)
    stream.advance(240)

    expect(stream.text()).toBe('e\u0301!')
    expect(stream.outputs.some((output) => output.text === 'e')).toBe(false)
    for (const output of stream.outputs) expect(output.text.endsWith('\r')).toBe(false)
  })
})
