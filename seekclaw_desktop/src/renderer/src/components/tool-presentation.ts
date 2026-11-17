import type { ToolActivity } from '../types'

export function toolKind(tool: ToolActivity): 'shell' | 'edit' | 'read' | 'search' | 'image' | 'browser' | 'plan' | 'other' {
  const name = tool.name.toLowerCase()
  if (['bash', 'cmd', 'powershell', 'sh', 'terminal', 'exec_command', 'command_session'].includes(name)) return 'shell'
  if (tool.diff || ['write_file', 'edit_file', 'apply_patch'].includes(name)) return 'edit'
  if (['read_file', 'cat', 'view_file'].includes(name)) return 'read'
  if (['glob', 'grep', 'list_dir', 'search', 'search_code', 'find', 'web_search'].includes(name)) return 'search'
  if (['view_image', 'capture_screen'].includes(name)) return 'image'
  if (['browser', 'web_fetch'].includes(name)) return 'browser'
  if (['update_plan', 'plan'].includes(name)) return 'plan'
  return 'other'
}

export function toolTarget(tool: ToolActivity): string {
  if (tool.widgets?.length) return tool.widgets[0]!.title
  if (tool.filePath) return tool.filePath
  const text = tool.detail?.trim() || ''
  try {
    const args = JSON.parse(text)
    for (const key of ['command', 'cmd', 'path', 'file_path', 'file', 'directory', 'dir', 'pattern', 'query', 'url', 'action']) {
      if (typeof args?.[key] === 'string') return args[key]
    }
  } catch { /* Older sessions store a plain text summary. */ }
  return text
}

export function toolLabel(tool: ToolActivity): string {
  const verbs = {
    shell: ['正在运行', '已运行', '命令失败'], edit: ['正在编辑', '已编辑', '编辑失败'],
    read: ['正在读取', '已读取', '读取失败'], search: ['正在搜索', '已搜索', '搜索失败'],
    image: ['正在查看图像', '已查看图像', '查看失败'], browser: ['正在浏览', '已浏览', '浏览失败'],
    plan: ['正在更新计划', '已更新计划', '计划更新失败'], other: ['正在调用', '已调用', '调用失败']
  }
  return verbs[toolKind(tool)][tool.state === 'running' ? 0 : tool.state === 'error' ? 2 : 1]!
}

export function preparingToolLabel(toolName?: string): string {
  const name = toolName?.trim()
  if (!name || name === 'tool') return '正在准备工具调用…'
  const kind = toolKind({ id: '', name, state: 'running' })
  switch (kind) {
    case 'shell': return '正在准备运行命令…'
    case 'edit': return '正在准备编辑文件…'
    case 'read': return '正在准备读取文件…'
    case 'search': return '正在准备搜索…'
    case 'image': return '正在准备查看图像…'
    case 'browser': return '正在准备浏览操作…'
    case 'plan': return '正在准备更新计划…'
    default: return `正在准备调用 ${name}…`
  }
}

export function phaseStatusText(phase?: string): string | null {
  const p = phase?.trim()
  if (!p) return null
  if (p.includes('重试') || p.includes('压缩') || p.includes('验证') || p.includes('续写') || p.includes('续步') || p.includes('总结')) {
    return p
  }
  return null
}
