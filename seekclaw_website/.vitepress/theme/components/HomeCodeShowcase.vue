<template>
  <div class="code-showcase-container seek-glass-card">
    <div class="showcase-header">
      <div class="header-text">
        <h3>
          <Code2 class="header-icon" :size="22" />
          {{ isEn ? 'Developer Friendly & Data-Driven' : '极致简洁的开发者体验与数据驱动' }}
        </h3>
        <p>
          {{ isEn 
            ? 'Declarative JSON configuration, clean C# interfaces, and instant CLI command workflows.'
            : '开箱即用的 CLI 交互、声明式 JSON 配置与强类型 C# 扩展接口'
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
import { Code2, Terminal, FileCode, Settings, Boxes } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

const activeTab = ref('cli')

const tabs = [
  { id: 'cli', label: 'CLI Commands', icon: Terminal },
  { id: 'tool', label: 'Custom ITool (C#)', icon: FileCode },
  { id: 'config', label: 'config.json', icon: Settings },
  { id: 'mcp', label: 'MCP Servers', icon: Boxes }
]

const codeSnippets = {
  cli: {
    filename: 'terminal.sh',
    lang: 'bash',
    code: `# 启动交互式 Agent 聊天循环
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
    public JsonElement ParameterSchema => /* JSON Schema */;

    public async Task<ToolResult> ExecuteAsync(
        JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        // 核心逻辑与权限隔离
        return ToolResult.Success("操作完成");
    }
}`
  },
  config: {
    filename: '~/.seekclaw/config.json',
    lang: 'json',
    code: `{
  "providers": {
    "openai": {
      "apiKey": "sk-proj-xxxxxxxx",
      "baseUrl": "https://api.openai.com/v1"
    }
  },
  "profiles": {
    "default": { "provider": "openai", "model": "gpt-5.5" }
  },
  "agent": {
    "autoVerify": true,
    "maxRepairAttempts": 3
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

const currentTabData = computed(() => codeSnippets[activeTab.value])
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
