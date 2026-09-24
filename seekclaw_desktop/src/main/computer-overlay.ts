import { BrowserWindow, globalShortcut, screen } from 'electron'

let overlayWindow: BrowserWindow | null = null
let hideTimer: NodeJS.Timeout | null = null
let onCancelCallback: (() => void) | null = null
let isShowing = false

const OVERLAY_HTML = `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  * {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
    user-select: none;
    -webkit-user-select: none;
  }
  html, body {
    width: 100vw;
    height: 100vh;
    background: transparent;
    overflow: hidden;
    pointer-events: none;
  }
  /* Glowing perimeter frame */
  .halo-frame {
    position: fixed;
    inset: 0;
    pointer-events: none;
    border: 3.5px solid #0090ff;
    box-shadow: inset 0 0 22px rgba(0, 144, 255, 0.45), inset 0 0 6px rgba(0, 175, 255, 0.6);
    animation: halo-pulse 2.2s ease-in-out infinite alternate;
  }
  @keyframes halo-pulse {
    0% {
      border-color: rgba(0, 144, 255, 0.82);
      box-shadow: inset 0 0 16px rgba(0, 144, 255, 0.35), inset 0 0 5px rgba(0, 144, 255, 0.5);
    }
    100% {
      border-color: rgba(0, 190, 255, 1);
      box-shadow: inset 0 0 32px rgba(0, 190, 255, 0.65), inset 0 0 10px rgba(0, 210, 255, 0.75);
    }
  }

  /* Top floating status capsule */
  .status-pill-container {
    position: fixed;
    top: 18px;
    left: 50%;
    transform: translateX(-50%);
    pointer-events: none;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 20px;
    background: rgba(12, 22, 38, 0.88);
    color: #f8fafc;
    border: 1px solid rgba(0, 144, 255, 0.55);
    border-radius: 9999px;
    box-shadow: 0 6px 24px rgba(0, 0, 0, 0.45), 0 0 18px rgba(0, 144, 255, 0.3);
    backdrop-filter: blur(16px);
    -webkit-backdrop-filter: blur(16px);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Microsoft YaHei", sans-serif;
    font-size: 13px;
    font-weight: 500;
    letter-spacing: 0.2px;
    animation: pill-slide 0.35s cubic-bezier(0.16, 1, 0.3, 1) forwards;
  }

  @keyframes pill-slide {
    from {
      opacity: 0;
      transform: translate(-50%, -12px);
    }
    to {
      opacity: 1;
      transform: translate(-50%, 0);
    }
  }

  .status-dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: #00e5ff;
    box-shadow: 0 0 9px #00e5ff;
    animation: dot-pulse 1.3s ease-in-out infinite alternate;
  }
  @keyframes dot-pulse {
    from { opacity: 0.45; transform: scale(0.8); }
    to { opacity: 1; transform: scale(1.2); }
  }

  .pill-esc-chip {
    display: inline-flex;
    align-items: center;
    padding: 1px 7px;
    font-size: 11px;
    font-weight: 600;
    background: rgba(255, 255, 255, 0.15);
    border: 1px solid rgba(255, 255, 255, 0.28);
    border-radius: 4px;
    margin-left: 3px;
    color: #f1f5f9;
  }
</style>
</head>
<body>
  <div class="halo-frame"></div>
  <div class="status-pill-container">
    <div class="status-dot"></div>
    <span>SeekClaw 正在操作电脑</span>
    <span style="opacity: 0.35;">·</span>
    <span style="font-size: 12px; color: #cbd5e1;">按 <span class="pill-esc-chip">Esc</span> 取消</span>
  </div>
</body>
</html>`

function getOverlayWindow(): BrowserWindow {
  if (overlayWindow && !overlayWindow.isDestroyed()) {
    return overlayWindow
  }

  const primaryDisplay = screen.getPrimaryDisplay()
  const { x, y, width, height } = primaryDisplay.bounds

  overlayWindow = new BrowserWindow({
    x,
    y,
    width,
    height,
    transparent: true,
    frame: false,
    alwaysOnTop: true,
    focusable: false,
    skipTaskbar: true,
    hasShadow: false,
    resizable: false,
    movable: false,
    show: false,
    enableLargerThanScreen: true,
    webPreferences: {
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false
    }
  })

  overlayWindow.setAlwaysOnTop(true, 'screen-saver')
  overlayWindow.setIgnoreMouseEvents(true, { forward: true })
  overlayWindow.setVisibleOnAllWorkspaces(true)

  void overlayWindow.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(OVERLAY_HTML)}`)

  overlayWindow.on('closed', () => {
    overlayWindow = null
    isShowing = false
  })

  return overlayWindow
}

export function isComputerOverlayActive(): boolean {
  return isShowing
}

export function showComputerOverlay(onCancel?: () => void): void {
  if (hideTimer) {
    clearTimeout(hideTimer)
    hideTimer = null
  }

  if (onCancel) {
    onCancelCallback = onCancel
  }

  const win = getOverlayWindow()
  const primaryDisplay = screen.getPrimaryDisplay()
  win.setBounds(primaryDisplay.bounds)

  if (!win.isVisible()) {
    win.showInactive()
    win.setAlwaysOnTop(true, 'screen-saver')
    isShowing = true
  }

  // Register global shortcut for Escape to allow immediate abort
  try {
    if (!globalShortcut.isRegistered('Escape')) {
      globalShortcut.register('Escape', () => {
        hideComputerOverlay(true)
        onCancelCallback?.()
      })
    }
  } catch (err) {
    console.warn('Failed to register global Escape shortcut:', err)
  }
}

export function scheduleHideComputerOverlay(graceMs = 900): void {
  if (hideTimer) clearTimeout(hideTimer)
  hideTimer = setTimeout(() => {
    hideTimer = null
    hideComputerOverlay(false)
  }, graceMs)
}

export function hideComputerOverlay(immediate = true): void {
  if (hideTimer) {
    clearTimeout(hideTimer)
    hideTimer = null
  }

  isShowing = false

  if (overlayWindow && !overlayWindow.isDestroyed() && overlayWindow.isVisible()) {
    overlayWindow.hide()
  }

  try {
    if (globalShortcut.isRegistered('Escape')) {
      globalShortcut.unregister('Escape')
    }
  } catch {}
}

export function destroyComputerOverlay(): void {
  hideComputerOverlay(true)
  if (overlayWindow && !overlayWindow.isDestroyed()) {
    overlayWindow.destroy()
    overlayWindow = null
  }
}
