using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Workspaces;
using Xunit;

namespace SeekClaw.Tests;

public sealed class FileLockCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-lock-tests", Guid.NewGuid().ToString("N"));

    public FileLockCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string FilePath(string dir, string name = "a.txt") => Path.Combine(dir, name);

    [Fact]
    public async Task AcquireAndRelease_AllowsAnotherOwnerToAcquire()
    {
        var coordinator = new FileLockCoordinator();
        var path = FilePath(_dir);

        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("task-a", coordinator.GetOwner(_dir, path));
        Assert.Single(coordinator.Snapshot());

        coordinator.Release(_dir, path, "task-a");
        Assert.Null(coordinator.GetOwner(_dir, path));
        Assert.Empty(coordinator.Snapshot());

        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-b", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("task-b", coordinator.GetOwner(_dir, path));
    }

    [Fact]
    public async Task Contention_SecondOwnerTimesOutWhileLockHeld()
    {
        var coordinator = new FileLockCoordinator();
        var path = FilePath(_dir);

        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));
        var acquired = await coordinator.TryAcquireAsync(_dir, path, "task-b", TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(acquired);
        Assert.Equal("task-a", coordinator.GetOwner(_dir, path));
    }

    [Fact]
    public async Task Contention_WaiterAcquiresOnceLockIsReleased()
    {
        var coordinator = new FileLockCoordinator();
        var path = FilePath(_dir);

        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));

        var waiter = coordinator.TryAcquireAsync(_dir, path, "task-b", TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiter.IsCompleted);

        coordinator.Release(_dir, path, "task-a");
        Assert.True(await waiter);
        Assert.Equal("task-b", coordinator.GetOwner(_dir, path));
    }

    [Fact]
    public async Task ReleaseAll_FreesEveryLockHeldByOwner()
    {
        var coordinator = new FileLockCoordinator();
        var pathA = FilePath(_dir, "a.txt");
        var pathB = FilePath(_dir, "b.txt");

        Assert.True(await coordinator.TryAcquireAsync(_dir, pathA, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.True(await coordinator.TryAcquireAsync(_dir, pathB, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.True(await coordinator.TryAcquireAsync(_dir, pathA, "task-b", TimeSpan.FromSeconds(1), CancellationToken.None) == false);

        coordinator.ReleaseAll("task-a");

        Assert.Null(coordinator.GetOwner(_dir, pathA));
        Assert.Null(coordinator.GetOwner(_dir, pathB));
        Assert.True(await coordinator.TryAcquireAsync(_dir, pathA, "task-b", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseByWrongOwner_IsNoOp()
    {
        var coordinator = new FileLockCoordinator();
        var path = FilePath(_dir);

        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));
        coordinator.Release(_dir, path, "someone-else");

        Assert.Equal("task-a", coordinator.GetOwner(_dir, path));
        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-b", TimeSpan.FromMilliseconds(200), CancellationToken.None) == false);
    }

    [Fact]
    public async Task Cancellation_AbortsTheWaitWithoutTakingTheLock()
    {
        var coordinator = new FileLockCoordinator();
        var path = FilePath(_dir);

        Assert.True(await coordinator.TryAcquireAsync(_dir, path, "task-a", TimeSpan.FromSeconds(1), CancellationToken.None));
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.TryAcquireAsync(_dir, path, "task-b", TimeSpan.FromSeconds(5), cts.Token));

        Assert.Equal("task-a", coordinator.GetOwner(_dir, path));
    }

    [Fact]
    public async Task WriteFileTool_AcquiresAndReleasesTheLock()
    {
        var coordinator = new FileLockCoordinator();
        var context = Context(_dir, coordinator, "task-1");
        var tool = new WriteFileTool(new NullPrompts());

        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["path"] = "a.txt",
            ["content"] = "hello",
        }, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(coordinator.Snapshot());
        Assert.Equal("hello", await File.ReadAllTextAsync(FilePath(_dir)));
    }

    [Fact]
    public async Task MutatingTools_ConcurrentMutationsOnSameFileSerializeWithoutLeaking()
    {
        var coordinator = new FileLockCoordinator();
        var write = new WriteFileTool(new NullPrompts());
        var edit = new EditFileTool(new NullPrompts());

        // Two concurrent writers of the same path serialize via the coordinator.
        var first = write.ExecuteAsync(new JsonObject
        {
            ["path"] = "a.txt",
            ["content"] = "first",
        }, Context(_dir, coordinator, "task-1"), CancellationToken.None);
        await Task.Delay(20);
        var second = write.ExecuteAsync(new JsonObject
        {
            ["path"] = "a.txt",
            ["content"] = "second",
        }, Context(_dir, coordinator, "task-2"), CancellationToken.None);

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Empty(coordinator.Snapshot());

        // A subsequent edit applies to the latest content and also releases.
        var editResult = await edit.ExecuteAsync(new JsonObject
        {
            ["path"] = "a.txt",
            ["old_string"] = "second",
            ["new_string"] = "second-edited",
        }, Context(_dir, coordinator, "task-3"), CancellationToken.None);

        Assert.True(editResult.Success);
        Assert.Empty(coordinator.Snapshot());
        Assert.Equal("second-edited", await File.ReadAllTextAsync(FilePath(_dir)));
    }

    private static ToolContext Context(string dir, IFileLockCoordinator coordinator, string owner) => new()
    {
        Workspace = new WorkspaceInfo { Root = dir, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
        Coordinator = coordinator,
        Owner = owner,
    };

    private sealed class NullPrompts : IPromptProvider
    {
        public string? TryGet(string key) => null;
        public string Get(string key) => "";
        public string Render(string template, IReadOnlyDictionary<string, string> variables) => template;
        public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables) => null;
        public void SetWorkspaceRoot(string? promptsDir) { }
    }
}
