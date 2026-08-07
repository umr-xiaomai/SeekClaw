using Cronos;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Scheduling;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class ScheduleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-tests", Guid.NewGuid().ToString("N"));

    public ScheduleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ScheduleStore NewStore() => new(new SeekClawDatabase(Path.Combine(_dir, "schedule.db")));

    [Fact]
    public void Store_CrudAndRunRecord_RoundTrips()
    {
        var store = NewStore();
        var task = store.Upsert(null, "每日整理", null, "整理今日工作日志", "0 18 * * *", true);

        Assert.False(string.IsNullOrWhiteSpace(task.Id));
        Assert.Equal("每日整理", task.Name);
        Assert.NotNull(task.NextRunAt);
        Assert.Null(task.LastRunAt);
        Assert.Single(store.List());
        Assert.NotNull(store.Get(task.Id));

        store.RecordRun(task.Id, ScheduleRunStatus.Success, null, "ok");
        var ran = store.Get(task.Id)!;
        Assert.Equal(ScheduleRunStatus.Success, ran.LastStatus);
        Assert.NotNull(ran.LastRunAt);
        Assert.NotNull(ran.NextRunAt);

        store.RecordRun(task.Id, ScheduleRunStatus.Error, "boom");
        var failed = store.Get(task.Id)!;
        Assert.Equal(ScheduleRunStatus.Error, failed.LastStatus);
        Assert.Equal("boom", failed.LastError);

        store.Remove(task.Id);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Store_RejectsInvalidCron()
    {
        var store = NewStore();
        Assert.Throws<CronFormatException>(() => store.Upsert(null, "x", null, "p", "not a cron", true));
        Assert.Throws<CronFormatException>(() => store.Upsert(null, "x", null, "p", "* * * * * * *", true));
        Assert.Throws<CronFormatException>(() => store.Upsert(null, "x", null, "p", "", true));
    }

    [Fact]
    public void Store_DisablingTask_ClearsNextRun()
    {
        var store = NewStore();
        var task = store.Upsert(null, "备份", null, "备份", "0 9 * * *", true);
        Assert.NotNull(task.NextRunAt);

        var disabled = store.SetEnabled(task.Id, false);
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.NextRunAt);

        var enabled = store.SetEnabled(task.Id, true);
        Assert.True(enabled.Enabled);
        Assert.NotNull(enabled.NextRunAt);
    }

    [Fact]
    public async Task Service_Tick_RunsDueTaskAndRecordsResult()
    {
        var store = NewStore();
        var runtime = SeekClawRuntime.Create(
            _dir,
            new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json")),
            Path.Combine(_dir, "runtime.db"));
        var task = store.Upsert(null, "夜间检查", null, "检查数据库备份", "0 9 * * *", true);

        var clock = new StubClock(task.NextRunAt!.Value.AddMinutes(1));
        var service = new ScheduleService(
            store, runtime, new FileLockCoordinator(), new LlmHttpFactory(),
            new CircuitBreaker(new RetryConfig()), StubRunner, clock);

        await service.TickAsync(CancellationToken.None);

        var ran = store.Get(task.Id)!;
        Assert.Equal(ScheduleRunStatus.Success, ran.LastStatus);
        Assert.NotNull(ran.LastRunAt);
        Assert.NotNull(ran.NextRunAt);
    }

    [Fact]
    public async Task Service_Tick_SkipsTaskThatIsNotDueYet()
    {
        var store = NewStore();
        var runtime = SeekClawRuntime.Create(
            _dir,
            new ConfigStore(Path.Combine(_dir, "config2.json"), Path.Combine(_dir, "state2.json")),
            Path.Combine(_dir, "runtime2.db"));
        var task = store.Upsert(null, "夜间检查", null, "检查数据库备份", "0 9 * * *", true);

        var clock = new StubClock(task.NextRunAt!.Value.AddMinutes(-1)); // before the due time
        var service = new ScheduleService(
            store, runtime, new FileLockCoordinator(), new LlmHttpFactory(),
            new CircuitBreaker(new RetryConfig()), StubRunner, clock);

        await service.TickAsync(CancellationToken.None);

        Assert.Null(store.Get(task.Id)!.LastRunAt);
    }

    [Fact]
    public async Task Service_RunNow_ExecutesAndRecordsOutcome()
    {
        var store = NewStore();
        var runtime = SeekClawRuntime.Create(
            _dir,
            new ConfigStore(Path.Combine(_dir, "config3.json"), Path.Combine(_dir, "state3.json")),
            Path.Combine(_dir, "runtime3.db"));
        var task = store.Upsert(null, "手动任务", null, "跑一遍", "0 9 * * *", false); // disabled: manual run still executes

        var service = new ScheduleService(
            store, runtime, new FileLockCoordinator(), new LlmHttpFactory(),
            new CircuitBreaker(new RetryConfig()), StubRunner);

        await service.RunNowAsync(task.Id, CancellationToken.None);

        var ran = store.Get(task.Id)!;
        Assert.Equal(ScheduleRunStatus.Success, ran.LastStatus);
        Assert.NotNull(ran.LastRunAt);
    }

    private static Task<AgentTurnResult> StubRunner(
        WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct) =>
        Task.FromResult(new AgentTurnResult("完成", false, null));

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
