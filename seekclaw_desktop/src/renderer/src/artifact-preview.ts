import type { ArtifactWidget } from './types'

export const ARTIFACT_PREVIEW_REQUEST = 'seekclaw:artifact-preview-open'

export interface ArtifactPreviewRequest {
  artifact: ArtifactWidget
  sessionId: string
  workspace?: string
  trigger?: HTMLElement
}

export function requestArtifactPreview(request: ArtifactPreviewRequest): void {
  window.dispatchEvent(new CustomEvent(ARTIFACT_PREVIEW_REQUEST, { detail: request }))
}
