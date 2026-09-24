import { BrowserWindow, globalShortcut, screen } from 'electron'

let overlayWindow: BrowserWindow | null = null
let hideTimer: NodeJS.Timeout | null = null
let onCancelCallback: (() => void) | null = null
let isShowing = false
let isPageReady = false

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

  /* 
   * 【光环向内漫延距离配置】
   * 通过多层 inset 阴影实现由屏幕边缘向屏幕内部深层漫延的体积光效果：
   * - 12px: 紧贴物理边框的高亮核心
   * - 40px: 中距离高饱和泛光
   * - 95px ~ 115px: 向屏幕内部漫延的光晕过渡层
   * - 140px ~ 165px: 最深层柔和环境光衰减（若想延伸更远可继续加大此项）
   */
  .halo-ambient {
    position: fixed;
    inset: 0;
    pointer-events: none;
    opacity: 0;
    box-shadow: inset 0 0 0 0 rgba(0, 144, 255, 0);
    transition: opacity 0.32s ease-out, box-shadow 0.38s cubic-bezier(0.16, 1, 0.3, 1);
  }
  body.active .halo-ambient {
    opacity: 1;
    box-shadow:
      inset 0 0 12px rgba(0, 210, 255, 0.75),
      inset 0 0 40px rgba(0, 150, 255, 0.52),
      inset 0 0 95px rgba(0, 120, 255, 0.32),
      inset 0 0 140px rgba(0, 90, 255, 0.16);
    animation: ambient-breathe 2.4s ease-in-out infinite alternate;
  }
  @keyframes ambient-breathe {
    0% {
      box-shadow:
        inset 0 0 10px rgba(0, 200, 255, 0.7),
        inset 0 0 32px rgba(0, 140, 255, 0.45),
        inset 0 0 80px rgba(0, 110, 255, 0.25),
        inset 0 0 125px rgba(0, 80, 255, 0.12);
    }
    100% {
      box-shadow:
        inset 0 0 14px rgba(0, 225, 255, 0.85),
        inset 0 0 48px rgba(0, 160, 255, 0.6),
        inset 0 0 115px rgba(0, 130, 255, 0.38),
        inset 0 0 165px rgba(0, 95, 255, 0.22);
    }
  }

  /* The 4 perimeter edge bars that slide inward from the physical screen edges */
  .edge-bar {
    position: fixed;
    background: #0090ff;
    pointer-events: none;
    transition: transform 0.36s cubic-bezier(0.16, 1, 0.3, 1), opacity 0.28s ease-out;
    opacity: 0;
    z-index: 10;
  }

  .edge-top {
    top: 0;
    left: 0;
    right: 0;
    height: 3.5px;
    transform: translateY(-100%);
    box-shadow: 0 0 16px rgba(0, 144, 255, 0.85), 0 0 32px rgba(0, 144, 255, 0.5);
  }
  .edge-bottom {
    bottom: 0;
    left: 0;
    right: 0;
    height: 3.5px;
    transform: translateY(100%);
    box-shadow: 0 0 16px rgba(0, 144, 255, 0.85), 0 0 32px rgba(0, 144, 255, 0.5);
  }
  .edge-left {
    top: 0;
    bottom: 0;
    left: 0;
    width: 3.5px;
    transform: translateX(-100%);
    box-shadow: 0 0 16px rgba(0, 144, 255, 0.85), 0 0 32px rgba(0, 144, 255, 0.5);
  }
  .edge-right {
    top: 0;
    bottom: 0;
    right: 0;
    width: 3.5px;
    transform: translateX(100%);
    box-shadow: 0 0 16px rgba(0, 144, 255, 0.85), 0 0 32px rgba(0, 144, 255, 0.5);
  }

  body.active .edge-bar {
    opacity: 1;
    transform: translate(0, 0);
  }

  /* Top floating status capsule */
  .status-pill-container {
    position: fixed;
    top: 18px;
    left: 50%;
    transform: translateX(-50%) translateY(-26px);
    opacity: 0;
    pointer-events: none;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 20px;
    background: rgba(12, 22, 38, 0.9);
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
    transition: opacity 0.3s ease-out, transform 0.36s cubic-bezier(0.16, 1, 0.3, 1);
    z-index: 20;
  }

  body.active .status-pill-container {
    opacity: 1;
    transform: translateX(-50%) translateY(0);
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
  <div class="halo-ambient"></div>
  <div class="edge-bar edge-top"></div>
  <div class="edge-bar edge-bottom"></div>
  <div class="edge-bar edge-left"></div>
  <div class="edge-bar edge-right"></div>
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
    type: 'toolbar', // Suppresses Windows DWM window zoom/scale animations
    enableLargerThanScreen: true,
    webPreferences: {
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false
    }
  })

  overlayWindow.setAlwaysOnTop(true, 'screen-saver')
  overlayWindow.setIgnoreMouseEvents(true)
  overlayWindow.setVisibleOnAllWorkspaces(true)

  isPageReady = false
  overlayWindow.webContents.once('did-finish-load', () => {
    isPageReady = true
    if (isShowing) {
      void overlayWindow?.webContents.executeJavaScript("document.body.classList.add('active')").catch(() => undefined)
    }
  })

  void overlayWindow.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(OVERLAY_HTML)}`)

  overlayWindow.on('closed', () => {
    overlayWindow = null
    isShowing = false
    isPageReady = false
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

  isShowing = true
  const win = getOverlayWindow()
  const primaryDisplay = screen.getPrimaryDisplay()
  win.setBounds(primaryDisplay.bounds)

  if (!win.isVisible()) {
    win.showInactive()
    win.setAlwaysOnTop(true, 'screen-saver')
    win.setIgnoreMouseEvents(true)
  }

  if (isPageReady) {
    void win.webContents.executeJavaScript("document.body.classList.add('active')").catch(() => undefined)
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

export function hideComputerOverlay(immediate = false): void {
  if (hideTimer) {
    clearTimeout(hideTimer)
    hideTimer = null
  }

  isShowing = false

  try {
    if (globalShortcut.isRegistered('Escape')) {
      globalShortcut.unregister('Escape')
    }
  } catch {}

  if (overlayWindow && !overlayWindow.isDestroyed()) {
    if (isPageReady) {
      void overlayWindow.webContents.executeJavaScript("document.body.classList.remove('active')").catch(() => undefined)
    }
    if (immediate) {
      if (overlayWindow.isVisible()) {
        overlayWindow.hide()
      }
    } else {
      // Allow the 340ms slide-out animation to complete smoothly before hiding the underlying window
      setTimeout(() => {
        if (!isShowing && overlayWindow && !overlayWindow.isDestroyed() && overlayWindow.isVisible()) {
          overlayWindow.hide()
        }
      }, 340)
    }
  }
}

export function destroyComputerOverlay(): void {
  hideComputerOverlay(true)
  if (overlayWindow && !overlayWindow.isDestroyed()) {
    overlayWindow.destroy()
    overlayWindow = null
  }
}
