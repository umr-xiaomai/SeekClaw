import type { ToolActivity } from '../types'
import { countDiffLines } from '../diff-parser'

type ToolKind = 'shell' | 'edit' | 'read' | 'search' | 'image' | 'browser' | 'plan' | 'mcp' | 'artifact' | 'other'

const commandSessionNames = new Set(['exec_command', 'command_session', 'write_stdin'])
const shellNames = new Set(['bash', 'cmd', 'powershell', 'sh', 'terminal', ...commandSessionNames])
const fileToolNames = new Set(['write_file', 'edit_file', 'apply_patch'])
const readToolNames = new Set(['read_file', 'cat', 'view_file'])
const searchToolNames = new Set(['glob', 'grep', 'list_dir', 'search', 'search_code', 'find', 'web_search'])
const browserActions: Record<string, string> = {
  open: '打开标签页', tabs: '查看标签页', select: '切换标签页', navigate: '打开页面',
  snapshot: '读取页面', screenshot: '截取页面', click: '点击元素',
  right_click: '右键点击', double_click: '双击元素', hover: '悬停元素', drag: '拖拽元素',
  fill: '填写字段', select_option: '选择下拉项', press: '按键',
  scroll: '滚动页面', evaluate: '执行页面脚本', console: '读取控制台日志', network: '读取网络请求',
  request_permission: '申请浏览器权限', download: '下载文件', downloads: '查看下载记录',
  back: '返回上一页', forward: '前进', reload: '刷新页面', close: '关闭标签页'
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined
}

function jsonObject(value?: string): Record<string, unknown> | undefined {
  if (!value?.trimStart().startsWith('{')) return undefined
  try {
    return asRecord(JSON.parse(value))
  } catch { return undefined }
}

function firstText(value: Record<string, unknown> | undefined, keys: readonly string[]): string {
  for (const key of keys) {
    const text = value?.[key]
    if (typeof text === 'string' && text.trim()) return text.trim()
  }
  return ''
}

export function toolKind(tool: ToolActivity): ToolKind {
  const name = tool.name.toLowerCase()
  if (shellNames.has(name)) return 'shell'
  if (tool.diff || fileToolNames.has(name)) return 'edit'
  if (readToolNames.has(name)) return 'read'
  if (searchToolNames.has(name)) return 'search'
  if (['view_image', 'capture_screen'].includes(name)) return 'image'
  if (['browser', 'web_fetch'].includes(name)) return 'browser'
  if (['update_plan', 'plan'].includes(name)) return 'plan'
  if (name.startsWith('mcp__') || name === 'tool_search') return 'mcp'
  if (name === 'present_artifact') return 'artifact'
  return 'other'
}

export function toolTarget(tool: ToolActivity): string {
  if (tool.filePath) return tool.filePath
  if (tool.widgets?.length) return tool.widgets[0]!.title
  const name = tool.name.toLowerCase()
  const args = jsonObject(tool.input) ?? jsonObject(tool.detail)
  if (name === 'write_stdin' || name === 'command_session' && args?.action === 'write') {
    return firstText(args, ['session_id', 'sessionId'])
  }
  if (name === 'browser') {
    const action = firstText(args, ['action'])
    if (action && Object.hasOwn(browserActions, action)) {
      return firstText(args, ['url']) || browserActions[action]!
    }
  }
  const searchTerm = searchToolNames.has(name) ? firstText(args, ['pattern', 'query']) : ''
  if (searchTerm) return searchTerm
  const target = firstText(args, ['command', 'cmd', 'path', 'file_path', 'file', 'directory', 'dir', 'url', 'topic', 'explanation', 'title', 'session_id', 'action'])
  if (target) return target
  const detail = tool.detail?.trim() ?? ''
  if (name.startsWith('mcp__')) {
    const [, server, method] = name.split('__')
    return [server, method].filter(Boolean).join(' / ')
  }
  return detail.startsWith('{') || detail === tool.output ? '' : detail
}

export function toolInput(tool: ToolActivity): string {
  const name = tool.name.toLowerCase()
  const rawInput = tool.input || tool.detail
  const args = jsonObject(rawInput)
  // Keep terminal and browser input fields out of the transcript. Older
  // records without structured arguments cannot separate those values.
  if (!args) {
    if (['write_stdin', 'command_session', 'browser'].includes(name)) return ''
    return rawInput !== tool.output ? rawInput ?? '' : ''
  }
  if (name === 'write_stdin') delete args.chars
  if (name === 'command_session' && args.action === 'write') delete args.text
  if (name === 'browser' && args.action === 'fill') delete args.text
  const command = firstText(args, ['command', 'cmd'])
  if (shellNames.has(name) && command) return `$ ${command}`
  return JSON.stringify(args, null, 2)
}

type PlanStepStatus = 'pending' | 'running' | 'done'

interface ToolPlan {
  explanation: string
  steps: Array<{ title: string; status: PlanStepStatus; detail: string }>
}

const planStepStatuses = new Map<string, PlanStepStatus>([
  ['pending', 'pending'],
  ['in_progress', 'running'],
  ['running', 'running'],
  ['completed', 'done'],
  ['done', 'done']
])

export function toolPlan(tool: ToolActivity): ToolPlan | undefined {
  if (toolKind(tool) !== 'plan') return undefined
  const args = jsonObject(tool.input) ?? jsonObject(tool.detail)
  if (!Array.isArray(args?.steps) || !args.steps.length) return undefined

  const steps: ToolPlan['steps'] = []
  for (const input of args.steps) {
    const step = asRecord(input)
    const title = firstText(step, ['title'])
    const status = planStepStatuses.get(firstText(step, ['status']).toLowerCase())
    if (!title || !status) return undefined
    steps.push({ title, status, detail: firstText(step, ['detail']) })
  }
  return { steps, explanation: firstText(args, ['explanation']) }
}

export function commandResult(tool: ToolActivity): { output: string; status: string; exitCode?: number } | null {
  if (!commandSessionNames.has(tool.name.toLowerCase()) || !tool.output) return null
  const result = jsonObject(tool.output)
  if (typeof result?.sessionId !== 'string' || typeof result.status !== 'string') return null
  const output = typeof result.output === 'string' ? result.output : ''
  const error = typeof result.error === 'string' ? result.error : ''
  return {
    output: [output, error].filter(Boolean).join('\n'),
    status: result.status,
    exitCode: typeof result.exitCode === 'number' ? result.exitCode : undefined
  }
}

export function toolDisplayState(tool: ToolActivity): ToolActivity['state'] {
  if (tool.state !== 'done') return tool.state
  // A successful transport call can still report a failed terminal process.
  const command = commandResult(tool)
  return command && (command.status === 'error' || (command.exitCode !== undefined && command.exitCode !== 0)) ? 'error' : 'done'
}

export function toolDiffStats(tool: ToolActivity): { addedLines: number; removedLines: number } {
  if (typeof tool.addedLines === 'number' || typeof tool.removedLines === 'number') {
    return { addedLines: tool.addedLines ?? 0, removedLines: tool.removedLines ?? 0 }
  }
  return tool.diff ? countDiffLines(tool.diff) : { addedLines: 0, removedLines: 0 }
}

const labels: Record<ToolKind, readonly [string, string, string]> = {
  shell: ['正在运行', '已运行', '命令失败'],
  edit: ['正在编辑', '已编辑', '编辑失败'],
  read: ['正在读取', '已读取', '读取失败'],
  search: ['正在搜索', '已搜索', '搜索失败'],
  image: ['正在查看图像', '已查看图像', '查看失败'],
  browser: ['正在检查页面', '已检查页面', '浏览失败'],
  plan: ['正在更新计划', '已更新计划', '计划更新失败'],
  mcp: ['正在调用 MCP', '已调用 MCP', 'MCP 调用失败'],
  artifact: ['正在生成预览', '已生成预览', '预览失败'],
  other: ['正在调用', '已调用', '调用失败']
}

export function toolLabel(tool: ToolActivity, state = toolDisplayState(tool)): string {
  if (state === 'interrupted') return '未完成'
  const name = tool.name.toLowerCase()
  const args = jsonObject(tool.input) ?? jsonObject(tool.detail)
  if (name === 'write_stdin' || name === 'command_session' && args?.action === 'write') {
    const writing = typeof args?.chars === 'string' && args.chars.length > 0 || args?.action === 'write'
    if (writing) return state === 'running' ? '正在发送终端输入' : state === 'error' ? '终端输入失败' : '已发送终端输入'
    return state === 'running' ? '正在读取终端输出' : state === 'error' ? '命令失败' : '已读取终端输出'
  }
  if (name === 'web_search') {
    return state === 'running' ? '正在搜索网页' : state === 'error' ? '网页搜索失败' : '已搜索网页'
  }
  if (name === 'command_session') {
    if (args?.action === 'read') return state === 'running' ? '正在读取终端输出' : state === 'error' ? '命令失败' : '已读取终端输出'
    if (args?.action === 'list') return state === 'running' ? '正在查看命令会话' : state === 'error' ? '查看命令会话失败' : '已查看命令会话'
    if (args?.action === 'close' || args?.action === 'terminate') {
      return state === 'running' ? '正在关闭命令会话' : state === 'error' ? '关闭命令会话失败' : '已关闭命令会话'
    }
  }
  return labels[toolKind(tool)][state === 'running' ? 0 : state === 'error' ? 2 : 1]
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
    case 'mcp': return '正在准备调用 MCP…'
    case 'artifact': return '正在准备生成预览…'
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
