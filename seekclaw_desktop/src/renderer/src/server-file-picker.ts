import { reactive } from 'vue'
import type { ServerFilePickerKind } from '@shared/web-server'

export interface ServerFilePickerRequest {
  id: number
  kind: ServerFilePickerKind
  title: string
  multiple: boolean
  initialPath?: string
  resolve: (paths: string[] | null) => void
}

const DEFAULT_TITLES: Record<ServerFilePickerKind, string> = {
  directory: '选择服务器目录作为工作区',
  files: '选择服务器文件',
  images: '选择服务器图片',
  skills: '从服务器导入技能 (.md / .zip)'
}

export const serverFilePickerState = reactive<{
  active: ServerFilePickerRequest | null
}>({
  active: null
})

let nextPickerId = 1

export function openServerFilePicker(options: {
  kind: ServerFilePickerKind
  title?: string
  multiple?: boolean
  initialPath?: string
}): Promise<string[] | null> {
  if (serverFilePickerState.active) {
    serverFilePickerState.active.resolve(null)
    serverFilePickerState.active = null
  }
  return new Promise<string[] | null>((resolve) => {
    serverFilePickerState.active = {
      id: nextPickerId++,
      kind: options.kind,
      title: options.title || DEFAULT_TITLES[options.kind],
      multiple: options.multiple ?? options.kind !== 'directory',
      initialPath: options.initialPath,
      resolve
    }
  })
}

export function finishServerFilePicker(paths: string[] | null): void {
  const current = serverFilePickerState.active
  if (!current) return
  serverFilePickerState.active = null
  current.resolve(paths)
}
