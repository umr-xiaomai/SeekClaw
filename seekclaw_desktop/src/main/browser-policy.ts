/** Browser pages never inherit a preload, local file access, or external protocol handlers. */
export function browserUrl(value: unknown): string {
  if (value === 'about:blank') return value
  if (typeof value !== 'string' || value.length > 8192) throw new Error('请输入有效的 HTTP(S) 地址')
  const url = new URL(value)
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password)
    throw new Error('浏览器仅支持 HTTP(S) 地址，且地址中不能包含凭据')
  return url.href
}

export function browserBounds(bounds: { x: number; y: number; width: number; height: number }, zoom: number, window: { width: number; height: number }) {
  if (![bounds.x, bounds.y, bounds.width, bounds.height, zoom].every(Number.isFinite)) throw new Error('Invalid browser bounds')
  const x = Math.min(window.width, Math.max(0, Math.round(bounds.x * zoom)))
  const y = Math.min(window.height, Math.max(48, Math.round(bounds.y * zoom)))
  return { x, y, width: Math.max(0, Math.min(window.width - x, Math.round(bounds.width * zoom))), height: Math.max(0, Math.min(window.height - y, Math.round(bounds.height * zoom))) }
}
