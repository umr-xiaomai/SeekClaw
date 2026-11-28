export const SIDE_LINK_OPEN_REQUEST = 'seekclaw:side-link-open'

export interface SideLinkOpenRequest {
  href: string
  workspace?: string
  label?: string
  trigger?: HTMLElement
}

export function requestSideLinkOpen(request: SideLinkOpenRequest): void {
  window.dispatchEvent(new CustomEvent(SIDE_LINK_OPEN_REQUEST, { detail: request }))
}
