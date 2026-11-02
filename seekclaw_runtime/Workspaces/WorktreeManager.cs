using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.Workspaces;

/// <summary>Managed, detached worktrees keyed by source workspace and conversation ID.</summary>
public sealed class WorktreeManager(ExecutionRecordStore records, IWorkspaceManager workspaces, string? storageRoot = null)
{
    private readonly string _root = Path.GetFullPath(storageRoot ?? Path.Combine(SeekClawPaths.Home, "worktrees"));
    private readonly SessionTurnCoordinator _operations = new(
        Path.GetFullPath(storageRoot ?? Path.Combine(SeekClawPaths.Home, "worktrees")) + ".locks");
    private static string Key(string source, string taskId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(SeekClawDatabase.PathKey(source) + "|" + taskId))).ToLowerInvariant();

    public IReadOnlyList<JsonObject> List(string? source = null) => records.List("worktree",
        source is null ? null : SeekClawDatabase.PathKey(source));

    public JsonObject? Get(string source, string taskId) => records.Get("worktree", Key(source, taskId));

    public Task<IDisposable> AcquireExecutionAsync(string source, string taskId, CancellationToken ct) =>
        _operations.AcquireAsync(Key(source, taskId), ct);

    public async Task<JsonObject> CreateAsync(string source, string taskId, string revision = "HEAD", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var id = Key(source, taskId);
        using var lease = await _operations.AcquireAsync(id, ct).ConfigureAwait(false);
        if (records.Get("worktree", id) is { } existing)
        {
            if (existing["status"]?.GetValue<string>() == "active" && Directory.Exists(existing["path"]!.GetValue<string>())) return existing;
            throw new InvalidOperationException("This task already has a worktree; restore it or repair its registration first.");
        }
        var repository = (await GitAsync(source, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim();
        var commit = (await GitAsync(repository, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], ct).ConfigureAwait(false)).Trim();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, id);
        ValidatePath(path);
        var record = new JsonObject
        {
            ["id"] = id, ["taskId"] = taskId, ["source"] = Path.GetFullPath(source),
            ["repository"] = repository, ["path"] = path, ["baseCommit"] = commit,
            ["workspacePath"] = WorkspacePathGuard.Resolve(path, Path.GetRelativePath(repository, Path.GetFullPath(source))),
            ["status"] = "preparing", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        Save(record);
        try
        {
            await GitAsync(repository, ["worktree", "add", "--detach", "--", path, commit], ct).ConfigureAwait(false);
            record["status"] = "active";
            Save(record);
            return record;
        }
        catch (Exception ex)
        {
            record["status"] = "error"; record["error"] = ex.Message; Save(record);
            throw;
        }
    }

    public WorkspaceInfo Resolve(WorkspaceInfo source, string taskId)
    {
        var record = Get(source.Root, taskId);
        if (record is null) return source;
        if (record["status"]?.GetValue<string>() != "active")
            throw new InvalidOperationException("Task worktree is unavailable; restore it before continuing.");
        var path = record["path"]!.GetValue<string>();
        ValidatePath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Task worktree directory is missing.");
        var executionRoot = record["workspacePath"]?.GetValue<string>() ?? path;
        WorkspacePathGuard.Resolve(path, executionRoot);
        if (!Directory.Exists(executionRoot)) throw new DirectoryNotFoundException("The project directory is absent from the selected Git revision.");
        var detected = workspaces.Detect(executionRoot);
        // Local project settings are often ignored by Git. Carry configuration forward without copying secrets to disk.
        return new WorkspaceInfo { Root = executionRoot, ProjectKinds = detected.ProjectKinds, Config = source.Config ?? detected.Config,
            ConfigurationRoot = source.ConfigurationRoot ?? source.Root };
    }

    public async Task<JsonObject> ArchiveAsync(string source, string taskId, CancellationToken ct = default)
    {
        var id = Key(source, taskId);
        using var lease = await _operations.AcquireAsync(id, ct).ConfigureAwait(false);
        var record = records.Get("worktree", id) ?? throw new KeyNotFoundException("Managed worktree not found.");
        if (record["status"]?.GetValue<string>() == "archived") return record;
        var path = record["path"]!.GetValue<string>();
        ValidatePath(path);
        await ValidateRegistrationAsync(record, ct).ConfigureAwait(false);
        var activeCommand = records.List("command").Any(command => command["status"]?.GetValue<string>() == "running"
            && command["workspace"]?.GetValue<string>() is { } directory && IsWithin(path, directory) && ProcessIdentity.IsAlive(command));
        var activeChild = records.List("subagent", taskId).Any(child => child["status"]?.GetValue<string>() == "running" && ProcessIdentity.IsAlive(child));
        if (activeCommand || activeChild)
            throw new InvalidOperationException("Stop active command sessions and sub-agents before archiving this worktree.");
        var ignored = await GitAsync(path, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
        if (ignored.Length > 0)
            throw new InvalidOperationException("Worktree contains ignored files. Move or remove them before archiving; no files were removed.");
        var gitDir = (await GitAsync(path, ["rev-parse", "--absolute-git-dir"], ct).ConfigureAwait(false)).Trim();
        var index = Path.Combine(gitDir, "seekclaw-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            await GitAsync(path, ["read-tree", "HEAD"], ct, env).ConfigureAwait(false);
            await GitAsync(path, ["add", "-A", "--", "."], ct, env).ConfigureAwait(false);
            var entries = await GitAsync(path, ["ls-files", "--stage"], ct, env).ConfigureAwait(false);
            if (entries.Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal)))
                throw new InvalidOperationException("Archiving worktrees containing submodules or embedded repositories is unsupported; no files were removed.");
            var tree = (await GitAsync(path, ["write-tree"], ct, env).ConfigureAwait(false)).Trim();
            var snapshot = (await GitAsync(path, ["-c", "user.name=SeekClaw", "-c", "user.email=seekclaw@localhost",
                "commit-tree", tree, "-p", "HEAD", "-m", "SeekClaw task snapshot"], ct, env).ConfigureAwait(false)).Trim();
            var reference = "refs/seekclaw/worktrees/" + id;
            await GitAsync(path, ["update-ref", reference, snapshot], ct).ConfigureAwait(false);
            record["snapshotRef"] = reference; record["snapshotCommit"] = snapshot;
            // Persist the recovery point before removal; an interrupted archive remains recoverable.
            Save(record);
            await GitAsync(record["repository"]!.GetValue<string>(), ["worktree", "remove", "--force", "--", path], ct).ConfigureAwait(false);
            record["status"] = "archived"; Save(record);
            return record;
        }
        finally { if (File.Exists(index)) File.Delete(index); }
    }

    public async Task<JsonObject> RestoreAsync(string source, string taskId, CancellationToken ct = default)
    {
        var id = Key(source, taskId);
        using var lease = await _operations.AcquireAsync(id, ct).ConfigureAwait(false);
        var record = records.Get("worktree", id) ?? throw new KeyNotFoundException("Managed worktree not found.");
        var path = record["path"]!.GetValue<string>();
        ValidatePath(path);
        if (Directory.Exists(path)) throw new InvalidOperationException("Restore target already exists; no files were replaced.");
        var revision = record["snapshotRef"]?.GetValue<string>() ?? record["baseCommit"]!.GetValue<string>();
        await GitAsync(record["repository"]!.GetValue<string>(), ["worktree", "add", "--detach", "--", path, revision], ct).ConfigureAwait(false);
        record["status"] = "active"; record.Remove("error"); Save(record);
        return record;
    }

    private void ValidatePath(string path)
    {
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(path));
        if (relative.Length != 64 || relative.Any(c => !char.IsAsciiHexDigit(c)))
            throw new UnauthorizedAccessException("Worktree path is outside the managed directory.");
        // A replaced storage directory must never redirect archive/remove to arbitrary user data.
        WorkspacePathGuard.Resolve(_root, relative);
    }

    private async Task ValidateRegistrationAsync(JsonObject record, CancellationToken ct)
    {
        var actual = (await GitAsync(record["path"]!.GetValue<string>(), ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim();
        if (SeekClawDatabase.PathKey(actual) != SeekClawDatabase.PathKey(record["path"]!.GetValue<string>()))
            throw new InvalidOperationException("Worktree registration does not match the managed directory.");
        var common = (await GitAsync(actual, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct).ConfigureAwait(false)).Trim();
        var expected = (await GitAsync(record["repository"]!.GetValue<string>(), ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct).ConfigureAwait(false)).Trim();
        if (SeekClawDatabase.PathKey(common) != SeekClawDatabase.PathKey(expected))
            throw new InvalidOperationException("Worktree belongs to a different repository.");
    }

    private void Save(JsonObject record) => records.Put("worktree", record["id"]!.GetValue<string>(),
        SeekClawDatabase.PathKey(record["source"]!.GetValue<string>()), record);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static async Task<string> GitAsync(string directory, string[] args, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (env is not null) foreach (var pair in env) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException((await error.ConfigureAwait(false)).Trim());
            return await output.ConfigureAwait(false);
        }
        finally { if (!process.HasExited) try { process.Kill(true); } catch (InvalidOperationException) { } }
    }
}
