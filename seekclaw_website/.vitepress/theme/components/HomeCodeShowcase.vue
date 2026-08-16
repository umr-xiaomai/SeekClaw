<template>
  <div class="code-showcase-container seek-glass-card">
    <div class="showcase-header">
      <div class="header-text">
        <h3>
          <Code2 class="header-icon" :size="22" />
          {{ isEn ? 'Desktop release and npm CLI workflows' : 'Desktop 发布与 npm CLI 工作流' }}
        </h3>
        <p>
          {{ isEn 
            ? 'Build a portable Desktop folder with one command, or install the CLI from npm and use JSON configuration and strongly typed extension points.'
            : '一条命令构建可分发 Desktop 文件夹，也可通过 npm 安装 CLI，并继续使用 JSON 配置与强类型扩展接口。'
          }}
        </p>
      </div>

      <!-- Tab Buttons -->
      <div class="tab-buttons">
        <button 
          v-for="t in tabs" 
          :key="t.id" 
          class="tab-btn" 
          :class="{ active: activeTab === t.id }"
          @click="activeTab = t.id"
        >
          <component :is="t.icon" :size="14" />
          <span>{{ t.label }}</span>
        </button>
      </div>
    </div>

    <!-- Tab Code Content Area -->
    <div class="showcase-code-box">
      <div class="code-toolbar">
        <span class="file-name">{{ currentTabData.filename }}</span>
        <span class="lang-tag">{{ currentTabData.lang }}</span>
      </div>
      <pre class="code-pre"><code>{{ currentTabData.code }}</code></pre>
    </div>
  </div>
</template>

<script setup>
import { ref, computed } from 'vue'
import { useData } from 'vitepress'
import { Code2, Terminal, FileCode, Settings, Boxes, PackageCheck } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const activeTab = ref('release')

const tabs = computed(() => [
  { id: 'release', label: isEn.value ? 'Desktop Release' : 'Desktop 发布', icon: PackageCheck },
  { id: 'cli', label: isEn.value ? 'CLI Commands' : 'CLI 命令', icon: Terminal },
  { id: 'tool', label: isEn.value ? 'Custom ITool' : '自定义 ITool', icon: FileCode },
  { id: 'config', label: 'config.json', icon: Settings },
  { id: 'mcp', label: 'MCP Servers', icon: Boxes }
])

const zhSnippets = {
  release: {
    filename: 'build.cmd',
    lang: 'batch',
    code: `:: Windows 双击或从终端运行
build.cmd

:: 构建结果（分发整个文件夹）
publish\\SeekClaw-win-x64\\SeekClaw.exe`
  },
  cli: {
    filename: 'terminal.sh',
    lang: 'bash',
    code: `# 通过 npm 安装 CLI（无需 .NET SDK）
npm install -g seekclaw-cli

# 启动交互式 Agent 聊天循环
seekclaw chat

# 执行单次重构任务并触发自动验证修复
seekclaw "重构 UserService.cs 引入依赖注入"

# 切换激活模型为 Claude Opus
seekclaw model use anthropic/claude-opus

# 诊断本地运行状况
seekclaw doctor`
  },
  tool: {
    filename: 'CustomTool.cs',
    lang: 'csharp',
    code: `public class CustomTool : ITool
{
    public string Name => "custom_tool";
    public string Description => "执行底层自定义诊断操作";
    public bool Mutating => false;
    public string StatusLabel => "正在运行自定义诊断...";
    public JsonObject ParameterSchema => /* JSON Schema */;

    public async Task<ToolResult> ExecuteAsync(
        JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        // 核心逻辑与权限隔离
        return ToolResult.Ok("操作完成");
    }
}`
  },
  config: {
    filename: '~/.seekclaw/config.json',
    lang: 'json',
    code: `{
  "activeProfile": "default",
  "providers": [
    {
      "id": "openai",
      "kind": "openai",
      "apiKey": "sk-proj-xxxxxxxx",
      "baseUrl": "https://api.openai.com/v1",
      "models": [{ "id": "gpt-5.5" }]
    }
  ],
  "profiles": {
    "default": { "provider": "openai", "model": "gpt-5.5" }
  }
}`
  },
  mcp: {
    filename: '.seekclaw/mcp/servers.json',
    lang: 'json',
    code: `{
  "servers": {
    "git-mcp": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-git"],
      "transport": "stdio"
    }
  }
}`
  }
}

const enSnippets = {
  ...zhSnippets,
  release: {
    filename: 'build.cmd',
    lang: 'batch',
    code: `:: Double-click on Windows or run from a terminal
build.cmd

:: Output (distribute the complete folder)
publish\\SeekClaw-win-x64\\SeekClaw.exe`
  },
  cli: {
    filename: 'terminal.sh',
    lang: 'bash',
    code: `# Install the CLI from npm (no .NET SDK required)
npm install -g seekclaw-cli

# Start the interactive Agent loop
seekclaw chat

# Run a one-shot refactor with automatic verification
seekclaw "Refactor UserService.cs to use dependency injection"

# Switch the active model
seekclaw model use anthropic/claude-opus-5

# Diagnose the local Runtime
seekclaw doctor`
  },
  tool: {
    filename: 'CustomTool.cs',
    lang: 'csharp',
    code: `public class CustomTool : ITool
{
    public string Name => "custom_tool";
    public string Description => "Run a custom diagnostic";
    public bool Mutating => false;
    public string StatusLabel => "Running diagnostics...";
    public JsonObject ParameterSchema => /* JSON Schema */;

    public async Task<ToolResult> ExecuteAsync(
        JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        return ToolResult.Ok("Done");
    }
}`
  }
}

const codeSnippets = computed(() => isEn.value ? enSnippets : zhSnippets)
const currentTabData = computed(() => codeSnippets.value[activeTab.value])
</script>

<style scoped>
.code-showcase-container {
  margin: 3rem 0;
  border-radius: 12px;
  padding: 1.75rem;
}

.showcase-header {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  margin-bottom: 1.25rem;
}

@media (min-width: 768px) {
  .showcase-header {
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
  }
}

.header-text h3 {
  font-size: 1.35rem;
  color: var(--seek-text-primary);
  font-weight: 800;
  margin-bottom: 0.25rem;
  display: flex;
  align-items: center;
  gap: 8px;
}

.header-icon {
  color: var(--vp-c-brand-1);
}

.header-text p {
  font-size: 0.9rem;
  color: var(--seek-text-secondary);
  margin: 0;
}

.tab-buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.tab-btn {
  background: var(--vp-c-bg-soft);
  color: var(--seek-text-secondary);
  border: 1px solid var(--seek-card-border);
  border-radius: 6px;
  padding: 6px 12px;
  font-size: 0.8rem;
  font-weight: 500;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  transition: all 0.2s ease;
}

.tab-btn:hover {
  color: var(--seek-text-primary);
  border-color: var(--seek-text-secondary);
}

.tab-btn.active {
  background: var(--vp-c-brand-1);
  color: #ffffff;
  border-color: var(--vp-c-brand-1);
}

.showcase-code-box {
  background: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  overflow: hidden;
}

.code-toolbar {
  background: #161b22;
  border-bottom: 1px solid #30363d;
  padding: 8px 14px;
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.file-name {
  font-family: 'Fira Code', monospace;
  font-size: 0.8rem;
  color: #c9d1d9;
}

.lang-tag {
  font-size: 0.7rem;
  color: #8b949e;
  text-transform: uppercase;
}

.code-pre {
  padding: 16px;
  margin: 0;
  background: transparent;
  font-family: 'Fira Code', JetBrains Mono, Monaco, Consolas, monospace;
  font-size: 0.85rem;
  line-height: 1.6;
  color: #e6edf3;
  overflow-x: auto;
}
</style>
