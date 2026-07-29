<template>
  <div class="arch-overview seek-glass-card">
    <div class="arch-header">
      <h3>
        <Building2 class="icon-header" :size="22" /> Runtime First 清洁架构理念
      </h3>
      <p>核心业务逻辑完全与 UI 界面解耦。所有功能均封装于 <code>seekclaw_runtime</code> 核心库，前端 CLI / GUI / Web / IDE 插件均通过统一 Facade 或 Daemon IPC 协议接入。</p>
    </div>

    <div class="layers-container">
      <div 
        v-for="layer in layers" 
        :key="layer.id" 
        class="layer-card" 
        :class="{ active: activeLayer === layer.id }"
        @mouseenter="activeLayer = layer.id"
      >
        <div class="layer-top">
          <component :is="layer.icon" class="layer-icon" :size="18" />
          <span class="layer-badge">{{ layer.tag }}</span>
        </div>
        <h4>{{ layer.title }}</h4>
        <p class="layer-sub">{{ layer.subtitle }}</p>
        <ul class="detail-list">
          <li v-for="item in layer.details" :key="item">{{ item }}</li>
        </ul>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref } from 'vue'
import { Building2, Terminal, Cpu, Boxes, RefreshCw } from 'lucide-vue-next'

const activeLayer = ref('runtime')

const layers = [
  {
    id: 'frontend',
    icon: Terminal,
    tag: '前端表示层 (Frontends)',
    title: 'seekclaw_cli & 未来 GUI/Web',
    subtitle: '专注于高帧率流式交互与终端 ANSI 双缓冲渲染',
    details: [
      'System.CommandLine 命令解析引擎',
      'Spectre.Console 游戏式 30-60 FPS 渲染循环',
      '纯事件驱动订阅，不侵入任何 Agent 业务逻辑'
    ]
  },
  {
    id: 'runtime',
    icon: Cpu,
    tag: '核心运行时 (seekclaw_runtime)',
    title: 'SeekClawRuntime 组合根 & Agent 循环',
    subtitle: '基于 .NET 10.0 的高性能核心控制中枢',
    details: [
      'ContextPlanner 上下文窗口智能剪裁',
      'PromptComposer 多层提示模板引擎 (FileSystemWatcher 热加载)',
      'SessionStore JSONL 会话持久化与断点恢复',
      'WorkspaceManager 项目自动识别与 Memory 系统'
    ]
  },
  {
    id: 'plugins',
    icon: Boxes,
    tag: '可扩展插件生态 (Plugins & MCP)',
    title: 'Tools + Skills + MCP Protocol',
    subtitle: '原生工具库与开放模型上下文协议 (MCP)',
    details: [
      'ToolRegistry 原生 Tool 接口与入参校验',
      'SkillManager YAML + Prompt.txt 目录化技能管理',
      'McpManager stdio 与 SSE 传输支持 (JSON-RPC 2.0)'
    ]
  },
  {
    id: 'verifier',
    icon: RefreshCw,
    tag: '自我修复循环 (Build Verification)',
    title: 'BuildVerifier 容错自愈',
    subtitle: '多语言构建检测与错误再注入修复机制',
    details: [
      '自动检测 Git / .NET / Node / Rust / Go / Python 构建命令',
      '编译/检查失败时捕获错误日志反馈回 Agent 循环',
      '配置上限内自动调整重试，无需人工干预补错'
    ]
  }
]
</script>

<style scoped>
.arch-overview {
  margin: 3rem 0;
  border-radius: 16px;
  padding: 2rem;
}

.arch-header h3 {
  font-size: 1.5rem;
  color: var(--seek-text-primary);
  font-weight: 800;
  margin-bottom: 0.5rem;
  display: flex;
  align-items: center;
  gap: 10px;
}

.icon-header {
  color: #7c3aed;
}

.arch-header p {
  color: var(--seek-text-secondary);
  margin-bottom: 1.5rem;
  line-height: 1.6;
}

.layers-container {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 1.25rem;
}

.layer-card {
  background: var(--vp-c-bg-soft);
  border: 1px solid var(--seek-card-border);
  border-radius: 12px;
  padding: 1.25rem;
  transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
  position: relative;
  overflow: hidden;
}

.layer-card:hover, .layer-card.active {
  border-color: rgba(139, 92, 246, 0.5);
  transform: translateY(-4px);
  box-shadow: 0 10px 25px -5px rgba(139, 92, 246, 0.18);
}

.layer-top {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.layer-icon {
  color: #7c3aed;
}

.layer-badge {
  font-size: 0.7rem;
  font-weight: 700;
  color: #7c3aed;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.dark .layer-badge, .dark .layer-icon {
  color: #a78bfa;
}

.layer-card h4 {
  font-size: 1.1rem;
  color: var(--seek-text-primary);
  margin-bottom: 4px;
  font-weight: 700;
}

.layer-sub {
  font-size: 0.8rem;
  color: var(--seek-text-muted);
  margin-bottom: 12px;
}

.detail-list {
  padding-left: 1.2rem;
  margin: 0;
}

.detail-list li {
  font-size: 0.8rem;
  color: var(--seek-text-secondary);
  margin-bottom: 6px;
  line-height: 1.4;
}
</style>
