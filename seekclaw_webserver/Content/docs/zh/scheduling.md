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

## 3. CLI 任务调度管理

```bash
# 查看当前所有调度任务与下一次触发时间
seekclaw schedule list

# 添加一个每小时运行一次的代码格式检查任务
seekclaw schedule add --name "代码格式检查" --cron "0 * * * *" --prompt "检查代码格式规范并自动修复"

# 暂停或恢复指定调度任务
seekclaw schedule toggle <task-id>

# 查看历史执行日志与结果
seekclaw schedule logs <task-id>
```

---

## 4. 容错与并发控制

- **非重叠执行保护**：如果上一次定时任务由于长时间运行未结束，调度引擎会自动顺延下一轮，防止同工作区多 Agent 并发写入冲突。
- **文件锁协调器联动**：定时任务执行时自动接入 `FileLockCoordinator`，保证跨进程访问工作区资产的绝对安全。
- **事件总线广播**：每一次定时触发、开始、执行中与完成状态均通过 EventBus 实时广播，Desktop 端可在通知中心接收即时状态提醒。
