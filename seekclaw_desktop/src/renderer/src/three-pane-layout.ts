export interface ThreePaneLayoutInput {
  /** Available width of `.app-body` (viewport width minus ActivityBar). */
  availableWidth: number
  sidebarOpen: boolean
  preferredSidebarWidth: number
  rightPanelOpen: boolean
  preferredRightWidth: number
  minSidebarWidth?: number
  minRightWidth?: number
}

export interface ThreePaneLayoutResult {
  autoCollapseSidebar: boolean
  sidebarWidth: number
  rightPanelWidth: number
  mainWidth: number
}

export const DEFAULT_MIN_SIDEBAR_WIDTH = 188
export const DEFAULT_MIN_RIGHT_WIDTH = 300
export const DEFAULT_MIN_MAIN_WIDTH = 360

export function computeThreePaneLayout(input: ThreePaneLayoutInput): ThreePaneLayoutResult {
  const availableWidth = Math.max(320, Math.round(input.availableWidth || 0))
  const minSidebar = Math.max(120, Math.round(input.minSidebarWidth ?? DEFAULT_MIN_SIDEBAR_WIDTH))
  const minRight = Math.max(160, Math.round(input.minRightWidth ?? DEFAULT_MIN_RIGHT_WIDTH))
  const preferredSidebar = Math.max(minSidebar, Math.round(input.preferredSidebarWidth || minSidebar))
  const preferredRight = Math.max(minRight, Math.round(input.preferredRightWidth || minRight))

  if (!input.rightPanelOpen) {
    if (!input.sidebarOpen) {
      return {
        autoCollapseSidebar: false,
        sidebarWidth: 0,
        rightPanelWidth: 0,
        mainWidth: availableWidth
      }
    }
    const maxSidebar = Math.max(minSidebar, availableWidth - DEFAULT_MIN_MAIN_WIDTH)
    const sidebarWidth = Math.min(preferredSidebar, maxSidebar)
    return {
      autoCollapseSidebar: false,
      sidebarWidth,
      rightPanelWidth: 0,
      mainWidth: Math.max(0, availableWidth - sidebarWidth)
    }
  }

  const minMain = Math.min(440, Math.max(340, Math.round(availableWidth * 0.38)))

  if (!input.sidebarOpen) {
    if (availableWidth < minMain + minRight) {
      const rightPanelWidth = Math.round(availableWidth * 0.5)
      return {
        autoCollapseSidebar: false,
        sidebarWidth: 0,
        rightPanelWidth,
        mainWidth: Math.max(0, availableWidth - rightPanelWidth)
      }
    }
    const maxRight = Math.max(minRight, availableWidth - minMain)
    const rightPanelWidth = Math.min(preferredRight, maxRight)
    return {
      autoCollapseSidebar: false,
      sidebarWidth: 0,
      rightPanelWidth,
      mainWidth: Math.max(0, availableWidth - rightPanelWidth)
    }
  }

  // Case 1: All three panes comfortably fit at preferred widths.
  if (preferredSidebar + minMain + preferredRight <= availableWidth) {
    return {
      autoCollapseSidebar: false,
      sidebarWidth: preferredSidebar,
      rightPanelWidth: preferredRight,
      mainWidth: Math.max(0, availableWidth - preferredSidebar - preferredRight)
    }
  }

  // Case 2: All three panes fit when compacting sidebar & capping right panel cleanly.
  if (minSidebar + 340 + minRight <= availableWidth) {
    const compactedSidebarTarget = Math.min(preferredSidebar, Math.max(minSidebar, Math.round(availableWidth * 0.19)))
    // Only shrink the left sidebar as much as needed if the right panel doesn't need all that extra room.
    const neededByMainAndRight = minMain + preferredRight
    const sidebarWidth = availableWidth - neededByMainAndRight >= minSidebar
      ? Math.min(preferredSidebar, Math.max(compactedSidebarTarget, availableWidth - neededByMainAndRight))
      : compactedSidebarTarget

    const rem = availableWidth - sidebarWidth
    const effectiveMinMain = Math.min(minMain, Math.max(340, rem - minRight))
    const maxRight = Math.max(minRight, rem - effectiveMinMain)
    const rightPanelWidth = Math.min(preferredRight, maxRight)
    return {
      autoCollapseSidebar: false,
      sidebarWidth,
      rightPanelWidth,
      mainWidth: Math.max(0, rem - rightPanelWidth)
    }
  }

  // Case 3: Too narrow for 3 columns simultaneously — auto-collapse left sidebar.
  if (availableWidth < 340 + minRight) {
    const rightPanelWidth = Math.round(availableWidth * 0.5)
    return {
      autoCollapseSidebar: true,
      sidebarWidth: 0,
      rightPanelWidth,
      mainWidth: Math.max(0, availableWidth - rightPanelWidth)
    }
  }

  const maxRight = Math.max(minRight, availableWidth - 340)
  const rightPanelWidth = Math.min(
    preferredRight,
    Math.max(minRight, Math.round(availableWidth * 0.52)),
    maxRight
  )
  return {
    autoCollapseSidebar: true,
    sidebarWidth: 0,
    rightPanelWidth,
    mainWidth: Math.max(0, availableWidth - rightPanelWidth)
  }
}
