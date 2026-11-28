import { describe, expect, it } from 'vitest'
import { computeThreePaneLayout } from './three-pane-layout'

describe('computeThreePaneLayout', () => {
  it('preserves preferred widths on wide 1920px+ desktop', () => {
    const res = computeThreePaneLayout({
      availableWidth: 1872,
      sidebarOpen: true,
      preferredSidebarWidth: 260,
      rightPanelOpen: true,
      preferredRightWidth: 560,
      minRightWidth: 340
    })

    expect(res).toEqual({
      autoCollapseSidebar: false,
      sidebarWidth: 260,
      rightPanelWidth: 560,
      mainWidth: 1052
    })
  })

  it('keeps comfortable three-pane proportions on 1440px screen', () => {
    const res = computeThreePaneLayout({
      availableWidth: 1392,
      sidebarOpen: true,
      preferredSidebarWidth: 260,
      rightPanelOpen: true,
      preferredRightWidth: 560,
      minRightWidth: 340
    })

    expect(res.autoCollapseSidebar).toBe(false)
    expect(res.sidebarWidth).toBe(260)
    expect(res.rightPanelWidth).toBe(560)
    expect(res.mainWidth).toBe(572)
  })

  it('compacts left sidebar and caps right panel on 1120px laptop so conversation stays readable', () => {
    const res = computeThreePaneLayout({
      availableWidth: 1072,
      sidebarOpen: true,
      preferredSidebarWidth: 260,
      rightPanelOpen: true,
      preferredRightWidth: 560,
      minRightWidth: 340
    })

    expect(res.autoCollapseSidebar).toBe(false)
    expect(res.sidebarWidth).toBe(204)
    expect(res.mainWidth).toBeGreaterThanOrEqual(400)
    expect(res.rightPanelWidth).toBeLessThan(560)
    expect(res.sidebarWidth + res.mainWidth + res.rightPanelWidth).toBe(1072)
  })

  it('auto-collapses left sidebar when window is too narrow for three columns (820px)', () => {
    const res = computeThreePaneLayout({
      availableWidth: 772,
      sidebarOpen: true,
      preferredSidebarWidth: 260,
      rightPanelOpen: true,
      preferredRightWidth: 560,
      minRightWidth: 340
    })

    expect(res.autoCollapseSidebar).toBe(true)
    expect(res.sidebarWidth).toBe(0)
    expect(res.rightPanelWidth).toBeGreaterThanOrEqual(340)
    expect(res.mainWidth).toBeGreaterThanOrEqual(340)
    expect(res.mainWidth + res.rightPanelWidth).toBe(772)
  })

  it('does not shrink left sidebar unnecessarily when a narrower Git panel fits comfortably', () => {
    const res = computeThreePaneLayout({
      availableWidth: 1072,
      sidebarOpen: true,
      preferredSidebarWidth: 260,
      rightPanelOpen: true,
      preferredRightWidth: 280,
      minRightWidth: 200
    })

    expect(res.autoCollapseSidebar).toBe(false)
    expect(res.sidebarWidth).toBe(260)
    expect(res.rightPanelWidth).toBe(280)
    expect(res.mainWidth).toBe(532)
  })

  it('handles 2-column mode cleanly when no right panel is open', () => {
    const open = computeThreePaneLayout({
      availableWidth: 1072,
      sidebarOpen: true,
      preferredSidebarWidth: 260,
      rightPanelOpen: false,
      preferredRightWidth: 560
    })
    expect(open).toEqual({
      autoCollapseSidebar: false,
      sidebarWidth: 260,
      rightPanelWidth: 0,
      mainWidth: 812
    })

    const collapsed = computeThreePaneLayout({
      availableWidth: 1072,
      sidebarOpen: false,
      preferredSidebarWidth: 260,
      rightPanelOpen: false,
      preferredRightWidth: 560
    })
    expect(collapsed).toEqual({
      autoCollapseSidebar: false,
      sidebarWidth: 0,
      rightPanelWidth: 0,
      mainWidth: 1072
    })
  })
})
