import type { FileAttachment, ImageAttachment } from './types'

export interface ComposerDraft {
  value: string
  images: ImageAttachment[]
  files: FileAttachment[]
}

export const taskDrafts = new Map<string, ComposerDraft>()

export function saveTaskDraft(
  taskId: string,
  value: string,
  images: ImageAttachment[],
  files: FileAttachment[]
): void {
  if (!taskId) return
  if (value.trim() || images.length > 0 || files.length > 0) {
    taskDrafts.set(taskId, {
      value,
      images: [...images],
      files: [...files]
    })
  } else {
    taskDrafts.delete(taskId)
  }
}

export function getTaskDraft(taskId: string): ComposerDraft | undefined {
  if (!taskId) return undefined
  return taskDrafts.get(taskId)
}

export function clearTaskDraft(taskId: string): void {
  if (!taskId) return
  taskDrafts.delete(taskId)
}
