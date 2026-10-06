# 定时任务与后台自动化工作流

SeekClaw Runtime 内核内置了基于 SQLite 的持久化调度引擎（`ScheduleService` 与 `ScheduleStore`），支持通过标准 Cron 表达式与一次性定时器自动触发 Agent 执行后台巡检、代码扫描、健康检查与自动化运维。

---

## 1. 核心应用场景

- **夜间代码库健康巡检**：每天凌晨自动拉取最新代码，运行构建与全量单元测试，发现失败时自动生成诊断报告。
- **依赖漏洞与更新扫描**：每周定期检查 NuGet / npm / pip 依赖版本，自动开启只读模式生成依赖升级建议。
- **自动化日报与指标汇总**：定时聚合代码变更与测试覆盖率数据。

---

## 2. 调度任务配置与数据结构

定时任务保存在系统数据库中，支持以下核心字段：

```json
{
  "id": "task-nightly-check",
  "name": "每日凌晨构建验证",
  "prompt": "执行全量单元测试并检查有无构建警告，如有错误请尝试修复",
  "cron": "0 2 * * *",
  "workspaceRoot": "D:\\Projects\\SeekClaw",
  "enabled": true,
  "maxIterations": 0,
  "mode": "auto"
}
```

### 字段说明
- `cron`：标准 5 段式 Cron 表达式（`分 时 日 月 星期`），如 `*/30 * * * *` 表示每 30 分钟。
- `workspaceRoot`：执行任务时挂载的工作区目录，Agent 将自动继承该工作区的 `AGENTS.md` 规范。
- `mode`：建议后台无人值守任务配置为 `auto`（自主执行修复）或 `readonly`（仅巡检输出报告）。

---

## 3. 计划任务管理方式

计划任务由后台 Daemon 进程自动常驻执行，管理方式包括 **Desktop 图形化界面** 与 **Daemon IPC 协议接口**：

### Desktop 桌面端管理（推荐）

1. 在 Desktop 客户端点击顶栏或侧边栏的 **“计划任务”** 图标打开管理对话框；
2. **新建任务**：填写任务名称、Cron 表达式（5 段式）、目标工作区目录与执行提示词；
3. **状态开关**：一键切换任务的“启用/暂停”状态；
4. **立即触发**：点击“立即运行”可不必等待到期时间，后台立即为该任务生成独立 Session 并启动执行；
5. **历史日志**：查看每次执行的开始时间、耗时、状态（成功/失败/超时）及截断输出摘要。

### Daemon IPC 协议管理

客户端或自动化运维程序可通过 JSONL IPC 管道直接向 Daemon 发送调度指令：

```json
{"id":1,"method":"schedule.list","params":{}}
{"id":2,"method":"schedule.create","params":{"name":"代码巡检","prompt":"运行单元测试","cron":"0 2 * * *","workspace":"D:\\Projects\\App"}}
{"id":3,"method":"schedule.toggle","params":{"id":"task-123","enabled":false}}
{"id":4,"method":"schedule.run","params":{"id":"task-123"}}
{"id":5,"method":"schedule.delete","params":{"id":"task-123"}}
```

---

## 4. 容错与并发控制

- **非重叠执行保护**：如果上一次定时任务由于长时间运行未结束，调度引擎会自动顺延下一轮，防止同工作区多 Agent 并发写入冲突。
- **文件锁协调器联动**：定时任务执行时自动接入 `FileLockCoordinator`，保证跨进程访问工作区资产的绝对安全。
- **事件总线广播**：每一次定时触发、开始、执行中与完成状态均通过 EventBus 实时广播，Desktop 端可在通知中心接收即时状态提醒。
