export function createFallbackUuid(): string {
  const bytes = new Uint8Array(16)
  if (typeof globalThis.crypto?.getRandomValues === 'function') {
    globalThis.crypto.getRandomValues(bytes)
  } else {
    for (let i = 0; i < 16; i++) {
      bytes[i] = Math.floor(Math.random() * 256)
    }
  }
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export function installWebPolyfills(): void {
  const cryptoObj = globalThis.crypto as (Crypto & { randomUUID?: () => string }) | undefined
  if (cryptoObj && typeof cryptoObj.randomUUID !== 'function') {
    try {
      Object.defineProperty(cryptoObj, 'randomUUID', {
        value: createFallbackUuid,
        configurable: true,
        writable: true
      })
    } catch {
      try {
        cryptoObj.randomUUID = createFallbackUuid as unknown as typeof cryptoObj.randomUUID
      } catch {
        // ignore if host runtime locks Crypto prototype
      }
    }
  }

  if (typeof navigator !== 'undefined' && typeof document !== 'undefined') {
    const nav = navigator as Navigator & { clipboard?: { writeText?: (text: string) => Promise<void> } }
    if (!nav.clipboard?.writeText) {
      const writeTextFallback = async (text: string): Promise<void> => {
        const textarea = document.createElement('textarea')
        textarea.value = text
        textarea.setAttribute('readonly', '')
        textarea.style.position = 'fixed'
        textarea.style.top = '-9999px'
        textarea.style.left = '-9999px'
        document.body.appendChild(textarea)
        textarea.select()
        try {
          const ok = document.execCommand('copy')
          if (!ok) throw new Error('execCommand copy failed')
        } finally {
          document.body.removeChild(textarea)
        }
      }

      try {
        if (nav.clipboard) {
          Object.defineProperty(nav.clipboard, 'writeText', {
            value: writeTextFallback,
            configurable: true,
            writable: true
          })
        } else {
          Object.defineProperty(nav, 'clipboard', {
            value: { writeText: writeTextFallback },
            configurable: true
          })
        }
      } catch {
        // ignore if navigator.clipboard cannot be defined
      }
    }
  }
}
