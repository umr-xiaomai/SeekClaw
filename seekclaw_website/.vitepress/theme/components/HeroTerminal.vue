<template>
  <div class="terminal-wrapper">
    <div class="terminal-header">
      <div class="dots">
        <span class="dot red"></span>
        <span class="dot yellow"></span>
        <span class="dot green"></span>
      </div>
      <div class="terminal-title">
        <span class="badge">SeekClaw CLI / Runtime (.NET 10)</span>
        <span class="fps">
          <Zap class="icon-inline" :size="13" /> 60 FPS Double-Buffered Live Region
        </span>
      </div>
      <div class="controls">
        <button @click="resetTerminal" class="btn-sm">
          <Play class="icon-inline" :size="12" /> {{ isEn ? 'Replay Demo' : '重新播放演示' }}
        </button>
      </div>
    </div>

    <div class="terminal-body" ref="bodyRef">
      <div class="line prompt">
        <span class="ps1">$ seekclaw</span> "{{ isEn ? 'Refactor UserService.cs for JWT key rotation and add tests' : '将 UserService.cs 重构为支持 JWT 密钥轮转与单元测试' }}"
      </div>

      <div v-for="(log, idx) in visibleLogs" :key="idx" class="log-entry" :class="log.type">
        <div v-if="log.type === 'status'" class="status-line">
          <Loader2 class="icon-spin" :size="14" />
          <span class="tag">[ProviderManager]</span> {{ log.text }}
        </div>

        <div v-else-if="log.type === 'route'" class="route-line">
          <span class="tag cyan">[ModelRegistry]</span> {{ isEn ? 'Candidate route:' : '推荐候选链：' }}
          <span class="model-badge primary">OpenAI / gpt-5.5 (Primary)</span>
          <span class="arrow">➔</span>
          <span class="model-badge secondary">Anthropic / claude-opus-5 (Fallback)</span>
        </div>

        <div v-else-if="log.type === 'thinking'" class="thinking-block">
          <div class="thinking-title">
            <Brain class="icon-inline" :size="14" /> {{ isEn ? 'SeekClaw Agent reasoning stream...' : 'SeekClaw Agent 实时思考推理流…' }}
          </div>
          <p class="thinking-content">{{ log.text }}</p>
        </div>

        <div v-else-if="log.type === 'tool'" class="tool-call">
          <div class="tool-header">
            <Wrench class="icon-inline" :size="14" />
            <span class="tool-name">{{ log.name }}</span>
            <span class="status-badge" :class="log.status">{{ log.status }}</span>
          </div>
          <div class="tool-args" v-if="log.args">
            <code>{{ log.args }}</code>
          </div>
          <div class="tool-output" v-if="log.output">
            <pre>{{ log.output }}</pre>
          </div>
        </div>

        <div v-else-if="log.type === 'verification'" class="verification-box" :class="log.status">
          <CheckCircle2 v-if="log.status === 'success'" class="icon-inline" :size="15" />
          <AlertCircle v-else class="icon-inline" :size="15" />
          <span>[BuildVerifier] {{ log.text }}</span>
        </div>

        <div v-else-if="log.type === 'text'" class="text-output">
          {{ log.text }}
        </div>
      </div>

      <div class="line active" v-if="isTyping">
        <span class="ps1">></span>
        <span class="current-text">{{ currentText }}</span>
        <span class="seek-cursor"></span>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, onMounted, computed, nextTick } from 'vue'
import { useData } from 'vitepress'
import { Zap, Play, Loader2, Brain, Wrench, CheckCircle2, AlertCircle } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const bodyRef = ref(null)
const visibleLogs = ref([])
const currentText = ref('')
const isTyping = ref(false)

const zhScriptSteps = [
  { type: 'status', text: '正在识别工作区配置: Git / .NET 10.0 (C# / ASP.NET Core)...' },
  { type: 'route', text: '' },
  { type: 'thinking', text: '首先分析当前 UserService.cs 的实现。调用 read_file 工具读取 Services/UserService.cs，检查现有的 GenerateToken 方法和配置依赖。' },
  { 
    type: 'tool', 
    name: 'read_file', 
    status: 'COMPLETED',
    args: '{"path": "Services/UserService.cs"}',
    output: 'public class UserService {\n  public string IssueToken(User u) => JwtHandler.Create(u, "legacy-secret-123");\n}'
  },
  { type: 'thinking', text: '检测到硬编码字符串 "legacy-secret-123"。准备使用 edit_file 引入 IKeyRotationProvider 并注入最新的密钥列表，然后创建测试。' },
  { 
    type: 'tool', 
    name: 'edit_file', 
    status: 'COMPLETED',
    args: '{"path": "Services/UserService.cs", "old_string": "JwtHandler.Create(u, \\"legacy-secret-123\\")", "new_string": "keyRotation.SignToken(u)"}',
    output: '+ public class UserService(IKeyRotationService keyRotation) {\n+     public string IssueToken(User u) => keyRotation.SignToken(u);\n+ }'
  },
  { type: 'verification', status: 'running', text: '触发代码构建自动验证: dotnet build seekclaw_tests...' },
  { type: 'verification', status: 'success', text: '构建验证成功！0 Errors, 0 Warnings. 单元测试全部通过 (42/42).' },
  { type: 'text', text: '已重构 UserService.cs 并实现动态 JWT 密钥轮转服务，增加单元测试防护。' }
]

const enScriptSteps = [
  { type: 'status', text: 'Detecting workspace: Git / .NET 10 (C# / ASP.NET Core)...' },
  { type: 'route', text: '' },
  { type: 'thinking', text: 'Inspect UserService.cs first. Read Services/UserService.cs and review the existing token generation and configuration dependencies.' },
  {
    type: 'tool', name: 'read_file', status: 'COMPLETED',
    args: '{"path": "Services/UserService.cs"}',
    output: 'public class UserService {\n  public string IssueToken(User u) => JwtHandler.Create(u, "legacy-secret-123");\n}'
  },
  { type: 'thinking', text: 'A hard-coded secret was found. Introduce IKeyRotationService, inject the active key set, and add coverage.' },
  {
    type: 'tool', name: 'edit_file', status: 'COMPLETED',
    args: '{"path": "Services/UserService.cs", "old_string": "JwtHandler.Create(u, \\"legacy-secret-123\\")", "new_string": "keyRotation.SignToken(u)"}',
    output: '+ public class UserService(IKeyRotationService keyRotation) {\n+     public string IssueToken(User u) => keyRotation.SignToken(u);\n+ }'
  },
  { type: 'verification', status: 'running', text: 'Running automatic verification: dotnet build seekclaw_tests...' },
  { type: 'verification', status: 'success', text: 'Build passed with 0 errors and 0 warnings. All tests passed (42/42).' },
  { type: 'text', text: 'Refactored UserService.cs for dynamic JWT key rotation and added regression tests.' }
]

const scriptSteps = computed(() => isEn.value ? enScriptSteps : zhScriptSteps)

const runAnimation = async () => {
  visibleLogs.value = []
  isTyping.value = true
  
  for (const step of scriptSteps.value) {
    await new Promise(r => setTimeout(r, 600))
    visibleLogs.value.push(step)
    await nextTick()
    if (bodyRef.value) {
      bodyRef.value.scrollTop = bodyRef.value.scrollHeight
    }
  }
  
  isTyping.value = false
}

const resetTerminal = () => {
  runAnimation()
}

onMounted(() => {
  runAnimation()
})
</script>

<style scoped>
.terminal-wrapper {
  background: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  overflow: hidden;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.3);
  font-family: 'Fira Code', JetBrains Mono, Monaco, Consolas, monospace;
  margin: 2rem 0;
}

.terminal-header {
  background: #161b22;
  padding: 10px 16px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  border-bottom: 1px solid #30363d;
}

.dots {
  display: flex;
  gap: 8px;
}

.dot {
  width: 12px;
  height: 12px;
  border-radius: 50%;
}
.dot.red { background: #ff5f56; }
.dot.yellow { background: #ffbd2e; }
.dot.green { background: #27c93f; }

.terminal-title {
  display: flex;
  align-items: center;
  gap: 12px;
}

.badge {
  background: #21262d;
  color: #34d399;
  font-size: 0.75rem;
  padding: 2px 8px;
  border-radius: 4px;
  border: 1px solid #30363d;
}

.fps {
  color: #38bdf8;
  font-size: 0.75rem;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.icon-inline {
  display: inline-block;
  vertical-align: -2px;
}

.icon-spin {
  display: inline-block;
  animation: spin 1s linear infinite;
  vertical-align: -2px;
  color: #e5c07b;
  margin-right: 6px;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.btn-sm {
  background: #21262d;
  color: #c9d1d9;
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 4px 10px;
  font-size: 0.75rem;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  transition: all 0.2s;
}

.btn-sm:hover {
  background: #30363d;
  color: #fff;
  border-color: #10b981;
}

.terminal-body {
  padding: 16px;
  height: 380px;
  overflow-y: auto;
  font-size: 0.875rem;
  line-height: 1.6;
  color: #c9d1d9;
}

.line.prompt {
  color: #8b949e;
  margin-bottom: 12px;
}

.ps1 {
  color: #10b981;
  font-weight: bold;
  margin-right: 8px;
}

.log-entry {
  margin-bottom: 10px;
  animation: fadeIn 0.3s ease-out;
}

@keyframes fadeIn {
  from { opacity: 0; transform: translateY(4px); }
  to { opacity: 1; transform: translateY(0); }
}

.tag {
  color: #e5c07b;
  font-weight: 600;
  margin-right: 6px;
}
.tag.cyan { color: #38bdf8; }

.route-line {
  background: #161b22;
  padding: 8px 12px;
  border-radius: 6px;
  border-left: 3px solid #38bdf8;
}

.model-badge {
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 0.75rem;
  margin: 0 4px;
}
.model-badge.primary { background: rgba(16, 185, 129, 0.15); color: #34d399; }
.model-badge.secondary { background: rgba(168, 85, 247, 0.15); color: #c084fc; }

.thinking-block {
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 10px 14px;
}

.thinking-title {
  color: #c084fc;
  font-weight: bold;
  font-size: 0.8rem;
  margin-bottom: 4px;
  display: flex;
  align-items: center;
  gap: 6px;
}

.thinking-content {
  color: #d1d5db;
  margin: 0;
}

.tool-call {
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 8px 12px;
}

.tool-header {
  display: flex;
  align-items: center;
  gap: 8px;
}

.tool-name {
  color: #61afef;
  font-weight: bold;
}

.status-badge {
  margin-left: auto;
  font-size: 0.7rem;
  padding: 2px 6px;
  border-radius: 4px;
}
.status-badge.COMPLETED { background: rgba(39, 201, 63, 0.15); color: #27c93f; }

.tool-args code {
  color: #abb2bf;
  font-size: 0.8rem;
}

.tool-output pre {
  margin: 6px 0 0 0;
  color: #98c379;
  font-size: 0.8rem;
  white-space: pre-wrap;
}

.verification-box {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 6px;
  font-weight: 500;
}

.verification-box.success {
  background: rgba(16, 185, 129, 0.1);
  color: #34d399;
  border: 1px solid rgba(16, 185, 129, 0.3);
}

.text-output {
  color: #56b6c2;
  font-weight: bold;
  padding: 6px 0;
}
</style>
