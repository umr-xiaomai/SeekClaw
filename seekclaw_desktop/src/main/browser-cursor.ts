export type VirtualCursorMode = 'arrow' | 'text' | 'scroll' | 'drag'
export type VirtualCursorEffect =
  | 'none'
  | 'click'
  | 'double_click'
  | 'right_click'
  | 'hover'
  | 'scroll_up'
  | 'scroll_down'
  | 'drag_start'
  | 'drag_end'

export interface VirtualCursorMoveOptions {
  x: number
  y: number
  fromX?: number
  fromY?: number
  mode?: VirtualCursorMode
  effect?: VirtualCursorEffect
  durationMs?: number
}

// Hand-crafted minimalist outline cursor SVGs matching reference style (#2563eb stroke, white fill)
const CURSOR_SVGS: Record<VirtualCursorMode, { svg: string; hotspotX: number; hotspotY: number }> = {
  arrow: {
    hotspotX: 3,
    hotspotY: 2,
    svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M3 2 L3 21 L8.5 15.5 L16 15.5 Z" fill="#ffffff" stroke="#2563eb" stroke-width="2.1" stroke-linejoin="round" stroke-miterlimit="6"/></svg>`
  },
  text: {
    hotspotX: 12,
    hotspotY: 12,
    svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M7.5 4.5 H16.5 M12 4.5 V19.5 M7.5 19.5 H16.5" stroke="#ffffff" stroke-width="4.2" stroke-linecap="round"/><path d="M7.5 4.5 H16.5 M12 4.5 V19.5 M7.5 19.5 H16.5" stroke="#2563eb" stroke-width="2.1" stroke-linecap="round"/></svg>`
  },
  scroll: {
    hotspotX: 12,
    hotspotY: 12,
    svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M12 3.5 V20.5 M8 7.5 L12 3.5 L16 7.5 M8 16.5 L12 20.5 L16 16.5" stroke="#ffffff" stroke-width="4.2" stroke-linecap="round" stroke-linejoin="round"/><path d="M12 3.5 V20.5 M8 7.5 L12 3.5 L16 7.5 M8 16.5 L12 20.5 L16 16.5" stroke="#2563eb" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"/></svg>`
  },
  drag: {
    hotspotX: 12,
    hotspotY: 12,
    svg: `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M12 3 V21 M3 12 H21 M9 6 L12 3 L15 6 M9 18 L12 21 L15 18 M6 9 L3 12 L6 15 M18 9 L21 12 L18 15" stroke="#ffffff" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/><path d="M12 3 V21 M3 12 H21 M9 6 L12 3 L15 6 M9 18 L12 21 L15 18 M6 9 L3 12 L6 15 M18 9 L21 12 L18 15" stroke="#2563eb" stroke-width="2.0" stroke-linecap="round" stroke-linejoin="round"/></svg>`
  }
}

export function getVirtualCursorSvg(mode: VirtualCursorMode): string {
  return CURSOR_SVGS[mode].svg
}

export function browserCursorMoveScript(options: VirtualCursorMoveOptions): string {
  const payload = {
    x: Number.isFinite(options.x) ? options.x : 100,
    y: Number.isFinite(options.y) ? options.y : 100,
    fromX: options.fromX,
    fromY: options.fromY,
    mode: options.mode ?? 'arrow',
    effect: options.effect ?? 'none',
    durationMs: options.durationMs,
    svgs: CURSOR_SVGS
  }
  return `(() => { try { return (${runCursorMove.toString()})(${JSON.stringify(payload)}); } catch { return { ok: false }; } })()`
}

export function browserCursorHideScript(): string {
  return `(() => { try { return (${runCursorHide.toString()})(); } catch { return { ok: false }; } })()`
}

function runCursorMove(input: {
  x: number
  y: number
  fromX?: number
  fromY?: number
  mode: 'arrow' | 'text' | 'scroll' | 'drag'
  effect: string
  durationMs?: number
  svgs: Record<'arrow' | 'text' | 'scroll' | 'drag', { svg: string; hotspotX: number; hotspotY: number }>
}): Promise<{ ok: boolean; x: number; y: number }> {
  const hostId = '__seekclaw_ai_cursor_host__'
  const rootGlobal = globalThis as typeof globalThis & {
    __seekclawCursor?: {
      shadow: ShadowRoot
      cursorEl: HTMLElement
      iconEl: HTMLElement
      x: number
      y: number
      rafId?: number
      idleTimer?: number
    }
  }

  let state = rootGlobal.__seekclawCursor
  let host = document.getElementById(hostId)
  if (!state || !host || !host.isConnected) {
    if (host && host.parentNode) host.parentNode.removeChild(host)
    host = document.createElement('div')
    host.id = hostId
    host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none;contain:layout style;margin:0;padding:0;border:0;background:transparent;'
    const shadow = host.attachShadow({ mode: 'closed' })
    const style = document.createElement('style')
    style.textContent = `
      .cursor {
        position: fixed;
        top: 0;
        left: 0;
        pointer-events: none;
        will-change: transform, opacity;
        filter: drop-shadow(0 2px 5px rgba(15, 23, 42, 0.28));
        transition: opacity 220ms ease;
        opacity: 1;
      }
      .cursor.idle {
        opacity: 0.45;
      }
      .icon {
        display: block;
        transform-origin: var(--hx, 3px) var(--hy, 2px);
        transition: transform 110ms cubic-bezier(0.22, 1, 0.36, 1);
      }
      .cursor.pressed .icon {
        transform: scale(0.84);
      }
      .cursor.dragging .icon {
        transform: scale(1.06);
      }
      .ripple {
        position: fixed;
        pointer-events: none;
        width: 8px;
        height: 8px;
        border-radius: 9999px;
        border: 2px solid #2563eb;
        background: rgba(37, 99, 235, 0.14);
        transform: translate(-50%, -50%) scale(0.6);
        opacity: 0.95;
        animation: sc-ripple 440ms cubic-bezier(0.16, 1, 0.3, 1) forwards;
      }
      .ripple.right {
        border-color: #7c3aed;
        border-style: dashed;
        background: rgba(124, 58, 237, 0.14);
      }
      .ripple.hover {
        border-color: #3b82f6;
        background: rgba(59, 130, 246, 0.1);
        animation: sc-ripple-soft 520ms ease-out forwards;
      }
      @keyframes sc-ripple {
        0% { width: 8px; height: 8px; opacity: 0.95; transform: translate(-50%, -50%) scale(0.6); }
        100% { width: 38px; height: 38px; opacity: 0; transform: translate(-50%, -50%) scale(1); }
      }
      @keyframes sc-ripple-soft {
        0% { width: 12px; height: 12px; opacity: 0.7; transform: translate(-50%, -50%) scale(0.8); }
        100% { width: 28px; height: 28px; opacity: 0; transform: translate(-50%, -50%) scale(1); }
      }
    `
    const cursorEl = document.createElement('div')
    cursorEl.className = 'cursor'
    const iconEl = document.createElement('div')
    iconEl.className = 'icon'
    cursorEl.appendChild(iconEl)
    shadow.appendChild(style)
    shadow.appendChild(cursorEl)
    ;(document.documentElement || document.body).appendChild(host)

    const initialX = typeof input.fromX === 'number' ? input.fromX : Math.max(24, Math.min(window.innerWidth - 24, input.x - 110))
    const initialY = typeof input.fromY === 'number' ? input.fromY : Math.max(24, Math.min(window.innerHeight - 24, input.y + 80))
    state = { shadow, cursorEl, iconEl, x: initialX, y: initialY }
    rootGlobal.__seekclawCursor = state
  }

  if (state.rafId) {
    cancelAnimationFrame(state.rafId)
    state.rafId = undefined
  }
  if (state.idleTimer) {
    clearTimeout(state.idleTimer)
    state.idleTimer = undefined
  }

  const asset = input.svgs[input.mode] || input.svgs.arrow
  state.iconEl.innerHTML = asset.svg
  state.iconEl.style.setProperty('--hx', `${asset.hotspotX}px`)
  state.iconEl.style.setProperty('--hy', `${asset.hotspotY}px`)
  state.cursorEl.classList.remove('idle')
  state.cursorEl.style.opacity = '1'
  state.cursorEl.classList.toggle('dragging', input.effect === 'drag_start')

  const startX = typeof input.fromX === 'number' ? input.fromX : state.x
  const startY = typeof input.fromY === 'number' ? input.fromY : state.y
  const targetX = input.x
  const targetY = input.y
  const dx = targetX - startX
  const dy = targetY - startY
  const dist = Math.hypot(dx, dy)

  // Slight natural human arc control point perpendicular to movement
  const arcOffset = dist > 40 ? Math.min(26, dist * 0.12) * (dx >= 0 ? -1 : 1) : 0
  const normX = dist > 0 ? -dy / dist : 0
  const normY = dist > 0 ? dx / dist : 0
  const ctrlX = (startX + targetX) / 2 + normX * arcOffset
  const ctrlY = (startY + targetY) / 2 + normY * arcOffset

  const duration = typeof input.durationMs === 'number'
    ? Math.max(0, input.durationMs)
    : dist < 4
      ? 0
      : Math.min(360, Math.max(150, Math.round(130 + dist * 0.28)))

  const applyPos = (px: number, py: number) => {
    state!.x = px
    state!.y = py
    state!.cursorEl.style.transform = `translate3d(${(px - asset.hotspotX).toFixed(2)}px, ${(py - asset.hotspotY).toFixed(2)}px, 0)`
  }

  const spawnRipple = (px: number, py: number, extraClass = '') => {
    const ring = document.createElement('div')
    ring.className = extraClass ? `ripple ${extraClass}` : 'ripple'
    ring.style.left = `${px}px`
    ring.style.top = `${py}px`
    state!.shadow.appendChild(ring)
    setTimeout(() => ring.remove(), 560)
  }

  const triggerEffect = () => {
    const eff = input.effect
    if (eff === 'click') {
      state!.cursorEl.classList.add('pressed')
      spawnRipple(targetX, targetY)
      setTimeout(() => state?.cursorEl.classList.remove('pressed'), 120)
    } else if (eff === 'double_click') {
      state!.cursorEl.classList.add('pressed')
      spawnRipple(targetX, targetY)
      setTimeout(() => {
        spawnRipple(targetX, targetY)
        state?.cursorEl.classList.remove('pressed')
      }, 110)
    } else if (eff === 'right_click') {
      state!.cursorEl.classList.add('pressed')
      spawnRipple(targetX, targetY, 'right')
      setTimeout(() => state?.cursorEl.classList.remove('pressed'), 120)
    } else if (eff === 'hover') {
      spawnRipple(targetX, targetY, 'hover')
    } else if (eff === 'drag_end') {
      state!.cursorEl.classList.remove('dragging')
      spawnRipple(targetX, targetY)
    }
    state!.idleTimer = window.setTimeout(() => {
      state?.cursorEl.classList.add('idle')
    }, 8000)
  }

  applyPos(startX, startY)

  if (duration === 0) {
    applyPos(targetX, targetY)
    triggerEffect()
    return Promise.resolve({ ok: true, x: targetX, y: targetY })
  }

  return new Promise((resolve) => {
    const startTime = performance.now()
    const step = (now: number) => {
      const rawT = Math.min(1, (now - startTime) / duration)
      const t = 1 - Math.pow(1 - rawT, 3)
      const omt = 1 - t
      const px = omt * omt * startX + 2 * omt * t * ctrlX + t * t * targetX
      const py = omt * omt * startY + 2 * omt * t * ctrlY + t * t * targetY
      applyPos(px, py)
      if (rawT < 1) {
        state!.rafId = requestAnimationFrame(step)
      } else {
        state!.rafId = undefined
        applyPos(targetX, targetY)
        triggerEffect()
        resolve({ ok: true, x: targetX, y: targetY })
      }
    }
    state!.rafId = requestAnimationFrame(step)
  })
}

function runCursorHide(): { ok: boolean } {
  const rootGlobal = globalThis as typeof globalThis & {
    __seekclawCursor?: { cursorEl: HTMLElement; rafId?: number; idleTimer?: number }
  }
  const state = rootGlobal.__seekclawCursor
  if (!state) return { ok: true }
  if (state.rafId) cancelAnimationFrame(state.rafId)
  if (state.idleTimer) clearTimeout(state.idleTimer)
  state.cursorEl.style.opacity = '0'
  return { ok: true }
}
