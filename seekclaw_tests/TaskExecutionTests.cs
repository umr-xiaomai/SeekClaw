using System.Text.Json.Nodes;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Skills;
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class TaskExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-task-tests", Guid.NewGuid().ToString("N"));
    public TaskExecutionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private ExecutionRecordStore Records() => new(new SeekClawDatabase(Path.Combine(_root, "data.db")));

    [Fact]
    public async Task Worktree_IsolatesTasks_AndRestoresTrackedAndUntrackedChanges()
    {
        await Git("init");
        File.WriteAllText(Path.Combine(_root, "source.txt"), "original");
        await Git("add", ".");
        await Git("-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "initial");
        var manager = new WorktreeManager(Records(), new WorkspaceManager(), Path.Combine(_root, "managed"));
        var first = await manager.CreateAsync(_root, "first");
        var second = await manager.CreateAsync(_root, "second");
        var path = first["path"]!.GetValue<string>();
        File.WriteAllText(Path.Combine(path, "source.txt"), "edited");
        File.WriteAllText(Path.Combine(path, "new.txt"), "untracked");
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "source.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(second["path"]!.GetValue<string>(), "source.txt")));
        Assert.Equal(path, manager.Resolve(new WorkspaceInfo { Root = _root, ProjectKinds = [] }, "first").Root);

        var archived = await manager.ArchiveAsync(_root, "first");
        Assert.Equal("archived", archived["status"]!.GetValue<string>());
        Assert.False(Directory.Exists(path));
        Assert.NotNull(archived["snapshotCommit"]);
        await manager.RestoreAsync(_root, "first");
        Assert.Equal("edited", File.ReadAllText(Path.Combine(path, "source.txt")));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(path, "new.txt")));
        await manager.ArchiveAsync(_root, "first");
        await manager.ArchiveAsync(_root, "second");
    }

    [Fact]
    public async Task Worktree_RefusesToDiscardIgnoredFiles_AndWaitsForExecution()
    {
        await Git("init");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "secret.local\n");
        await Git("add", ".gitignore");
        await Git("-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "initial");
        var manager = new WorktreeManager(Records(), new WorkspaceManager(), Path.Combine(_root, "managed"));
        var record = await manager.CreateAsync(_root, "task");
        var path = record["path"]!.GetValue<string>();
        File.WriteAllText(Path.Combine(path, "secret.local"), "preserve");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ArchiveAsync(_root, "task"));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(path, "secret.local")));
        File.Delete(Path.Combine(path, "secret.local"));
        using (var lease = await manager.AcquireExecutionAsync(_root, "task", CancellationToken.None))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ArchiveAsync(_root, "task", cancel.Token));
        }
        await manager.ArchiveAsync(_root, "task");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_RoundTripStdinAcrossCalls_AndEnforceOwnership(bool pty)
    {
        if (pty && !OperatingSystem.IsWindows() && !File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) return;
        await using var manager = new CommandSessionManager();
        var command = OperatingSystem.IsWindows() ? "Write-Output 'ready'; $line = [Console]::ReadLine(); Write-Output ('received:' + $line)" : "echo ready; read line; printf 'received:%s\\n' \"$line\"";
        var started = await manager.StartAsync("owner", command, _root, 20, pty);
        var id = started["sessionId"]!.GetValue<string>();
        if (pty)
        {
            using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            JsonObject ready;
            do { ready = await manager.ReadAsync("owner", id, null, 1000, readyDeadline.Token); }
            while (!ready["output"]!.GetValue<string>().Contains("ready", StringComparison.Ordinal) && ready["status"]!.GetValue<string>() == "running");
            Assert.True(ready["output"]!.GetValue<string>().Contains("ready", StringComparison.Ordinal), ready.ToJsonString());
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.WriteAsync("other", id, "bad\n", false, CancellationToken.None));
        await manager.WriteAsync("owner", id, "hello\n", false, CancellationToken.None);
        var output = new System.Text.StringBuilder();
        long cursor = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JsonObject read;
        do
        {
            read = await manager.ReadAsync("owner", id, cursor, 1000, deadline.Token);
            output.Append(read["output"]!.GetValue<string>());
            cursor = read["cursor"]!.GetValue<long>();
        } while (read["status"]!.GetValue<string>() == "running");
        Assert.True(output.ToString().Contains("received:hello", StringComparison.Ordinal), output.ToString());
        Assert.Equal(0, read["exitCode"]!.GetValue<int>());
        var empty = await manager.ReadAsync("owner", id, cursor);
        Assert.Equal("", empty["output"]!.GetValue<string>());
        await manager.StopAsync("owner", id, true, CancellationToken.None);
        Assert.Empty(manager.List("owner"));
    }

    [Fact]
    public async Task Worktree_PreservesSubprojectScopeAndLocalSettings_AndProtectsActiveCommands()
    {
        await Git("init");
        var project = Path.Combine(_root, "app");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), ".seekclaw/\n");
        await Git("add", ".");
        await Git("-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "initial");
        var prompts = Path.Combine(project, ".seekclaw", "prompts");
        Directory.CreateDirectory(prompts);
        File.WriteAllText(Path.Combine(prompts, "system.txt"), "local settings");
        var records = Records();
        var manager = new WorktreeManager(records, new WorkspaceManager(), Path.Combine(_root, "managed"));
        var record = await manager.CreateAsync(project, "task");
        var execution = manager.Resolve(new WorkspaceManager().Detect(project), "task");
        Assert.Equal(Path.Combine(record["path"]!.GetValue<string>(), "app"), execution.Root);
        Assert.Equal(prompts, execution.PromptsDir);
        Assert.Equal("local settings", File.ReadAllText(Path.Combine(execution.PromptsDir, "system.txt")));
        await using var commands = new CommandSessionManager(records);
        var command = await commands.StartAsync("task", OperatingSystem.IsWindows() ? "Start-Sleep 60" : "sleep 60", execution.Root, 60);
        // A different manager represents a CLI process consulting daemon-owned metadata.
        var other = new WorktreeManager(records, new WorkspaceManager(), Path.Combine(_root, "managed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ArchiveAsync(project, "task"));
        await commands.StopAsync("task", command["sessionId"]!.GetValue<string>(), true, CancellationToken.None);
        await other.ArchiveAsync(project, "task");
    }

    [Fact]
    public async Task Commands_CancelAndBoundOutput()
    {
        await using var manager = new CommandSessionManager();
        var command = OperatingSystem.IsWindows() ? "Write-Output ('x' * 200000); Start-Sleep 60" : "head -c 200000 /dev/zero | tr '\\0' x; sleep 60";
        var record = await manager.StartAsync("owner", command, _root, 60);
        var id = record["sessionId"]!.GetValue<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do { record = await manager.ReadAsync("owner", id, record["cursor"]!.GetValue<long>(), 1000, deadline.Token); }
        while (record["cursor"]!.GetValue<long>() < 200000);
        record = await manager.ReadAsync("owner", id, 0);
        Assert.True(record["truncated"]!.GetValue<bool>());
        Assert.InRange(record["output"]!.GetValue<string>().Length, 1, 128 * 1024);
        record = await manager.StopAsync("owner", id, false, deadline.Token);
        Assert.Equal("cancelled", record["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Skills_AdvertiseMetadata_LoadInstructionsOnDemand_AndPreserveReferencesOnImport()
    {
        var source = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: sample\ndescription: A sample workflow\nmetadata:\n  version: 1\n---\nPRIVATE INSTRUCTIONS");
        File.WriteAllText(Path.Combine(source, "references", "guide.md"), "REFERENCE");
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        var registry = new PromptRegistry();
        var skills = new SkillManager(config, registry, Path.Combine(_root, "installed"));
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        skills.Attach(workspace);
        skills.ImportGlobal(Path.Combine(source, "SKILL.md"), workspace);
        using var prompts = new FilePromptProvider([_root]);
        var prompt = await new PromptComposer(prompts, registry).ComposeAsync(new PromptRenderContext { WorkspaceRoot = _root, Variables = new Dictionary<string, string>() });
        Assert.Contains("sample: A sample workflow", prompt);
        Assert.DoesNotContain("PRIVATE INSTRUCTIONS", prompt);
        Assert.Contains("PRIVATE INSTRUCTIONS", skills.Load(workspace, "sample"));
        Assert.Contains("REFERENCE", skills.Load(workspace, "sample", "references/guide.md"));
        Assert.Throws<UnauthorizedAccessException>(() => skills.Load(workspace, "sample", "../config.json"));
        skills.SetEnabled("sample", false);
        Assert.Throws<InvalidOperationException>(() => skills.Load(workspace, "sample"));
    }

    [Fact]
    public void Skills_CompatibleDirectoriesRespectNativeOverrides_AndFailedReplacementKeepsInstalledFiles()
    {
        var compatible = Path.Combine(_root, ".agents", "skills", "sample");
        Directory.CreateDirectory(compatible);
        File.WriteAllText(Path.Combine(compatible, "SKILL.md"), "---\nname: sample\ndescription: compatible\n---\ncompatible body");
        var native = Path.Combine(_root, "skills", "sample");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "SKILL.md"), "---\nname: sample\ndescription: native\n---\nnative body");
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        var installed = Path.Combine(_root, "installed");
        var skills = new SkillManager(config, new PromptRegistry(), installed);
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        Assert.Equal("native", Assert.Single(skills.Discover(workspace)).Manifest.Description);
        skills.ImportGlobal(Path.Combine(native, "SKILL.md"), workspace);
        var old = File.ReadAllText(Path.Combine(installed, "sample", "SKILL.md"));
        if (OperatingSystem.IsWindows())
        {
            var reference = Path.Combine(native, "reference.txt");
            File.WriteAllText(reference, "locked");
            using var locked = new FileStream(reference, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => skills.ImportGlobal(Path.Combine(native, "SKILL.md"), workspace, overwrite: true));
            Assert.Equal(old, File.ReadAllText(Path.Combine(installed, "sample", "SKILL.md")));
        }
        Assert.DoesNotContain(Directory.GetDirectories(installed), path => Path.GetFileName(path).StartsWith('.'));
    }

    [Fact]
    public async Task SubAgents_CanWaitCancelResume_AndPersistAcrossManagerRecreation()
    {
        var records = Records();
        await using var manager = new SubAgentTaskManager(records);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var record = new JsonObject { ["id"] = "child", ["sessionId"] = "conversation" };
        await manager.StartAsync("parent", record, async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentTurnResult("", false, null);
        }, CancellationToken.None);
        await entered.Task;
        Assert.Throws<KeyNotFoundException>(() => manager.Get("other", "child"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync("parent", record, _ => Task.FromResult(new AgentTurnResult("", false, null)), CancellationToken.None));
        Assert.Equal("running", (await manager.WaitAsync("parent", "child", 1, CancellationToken.None))["status"]!.GetValue<string>());
        Assert.Equal("cancelled", (await manager.CancelAsync("parent", "child", CancellationToken.None))["status"]!.GetValue<string>());
        await manager.StartAsync("parent", manager.Get("parent", "child"), _ => Task.FromResult(new AgentTurnResult("resumed", false, null)), CancellationToken.None);
        var completed = await manager.WaitAsync("parent", "child", 1000, CancellationToken.None);
        Assert.Equal("completed", completed["status"]!.GetValue<string>());
        await using var recreated = new SubAgentTaskManager(records);
        Assert.Equal("resumed", recreated.Get("parent", "child")["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task SubAgents_ConcurrencyLimitIsAtomic_AndShutdownDrainsTasks()
    {
        var manager = new SubAgentTaskManager(Records());
        async Task<bool> Start(int index)
        {
            try
            {
                await manager.StartAsync("parent", new JsonObject { ["id"] = index.ToString() }, async ct =>
                { await Task.Delay(Timeout.Infinite, ct); return new AgentTurnResult("", false, null); }, CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() => Start(index))));
        Assert.Equal(8, results.Count(x => x));
        await manager.DisposeAsync();
        Assert.All(manager.List("parent").OfType<JsonObject>(), x => Assert.Equal("cancelled", x["status"]!.GetValue<string>()));
    }

    private Task<string> Git(params string[] args) => WorktreeManager.GitAsync(_root, args, CancellationToken.None);
}
