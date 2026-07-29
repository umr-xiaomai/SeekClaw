export interface ProjectItem {
  id: string
  name: string
  path: string
}

export interface ToolActivity {
  id: string
  name: string
  detail?: string
  state: 'running' | 'done' | 'error'
}

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  thinking?: string
  state?: 'thinking' | 'streaming' | 'done' | 'error'
  tools?: ToolActivity[]
  createdAt: number
}

export interface ThreadItem {
  id: string
  title: string
  projectId: string
  updatedAt: number
  messages: ChatMessage[]
  sessionId?: string
  sessionLoaded?: boolean
}
