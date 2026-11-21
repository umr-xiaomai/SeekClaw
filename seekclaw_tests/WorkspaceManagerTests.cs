using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-workspace-tests", Guid.NewGuid().ToString("N"));

    public WorkspaceManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    [Fact]
    public void Detect_PlainFolderInsideProfile_DoesNotResolveToProfile()
    {
        // On Windows the test temp directory lives under the real user profile, whose
        // ~/.seekclaw is SeekClaw's own state — it must not hijack a plain folder.
        var project = CreateDir(Path.Combine(_dir, "plain-project"));

        var info = new WorkspaceManager().Detect(project);

        Assert.Equal(Path.GetFullPath(project), info.Root);
    }

    [Fact]
    public void Detect_SiblingPlainFolders_GetIndependentWorkspaceRoots()
    {
        var projectA = CreateDir(Path.Combine(_dir, "projA"));
        var projectB = CreateDir(Path.Combine(_dir, "projB"));

        var manager = new WorkspaceManager();
        var rootA = manager.Detect(projectA).Root;
        var rootB = manager.Detect(projectB).Root;

        Assert.Equal(Path.GetFullPath(projectA), rootA);
        Assert.Equal(Path.GetFullPath(projectB), rootB);
        Assert.NotEqual(rootA, rootB);
    }

    [Fact]
    public void Detect_GitRepoInsideProfile_StillResolvesToRepoRoot()
    {
        var repo = CreateDir(Path.Combine(_dir, "repo"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var nested = CreateDir(Path.Combine(repo, "src"));

        Assert.Equal(Path.GetFullPath(repo), new WorkspaceManager().Detect(nested).Root);
    }

    [Fact]
    public void Detect_IgnoresProjectLevelSeekClawDirectoryAndReadsOnlyCentralConfig()
    {
        var project = CreateDir(Path.Combine(_dir, "project"));
        var nested = CreateDir(Path.Combine(project, "src"));
        var legacy = CreateDir(Path.Combine(project, ".seekclaw"));
        var legacyConfig = Path.Combine(legacy, "config.json");
        File.WriteAllText(legacyConfig, """{"mode":"edit"}""");
        var state = Path.Combine(_dir, "profile", ".seekclaw");
        var manager = new WorkspaceManager(state);

        Assert.Equal(nested, manager.Detect(nested).Root);
        var info = manager.Detect(project);
        Assert.Null(info.Config);
        Assert.StartsWith(Path.Combine(state, "workspaces") + Path.DirectorySeparatorChar, info.SeekClawDir);
        Assert.False(Directory.Exists(info.SeekClawDir));
        Directory.CreateDirectory(info.SeekClawDir);
        File.WriteAllText(info.ConfigFile, """{"mode":"readonly"}""");
        Assert.Equal("readonly", manager.Detect(project).Config?.Mode);
        Assert.Equal("""{"mode":"edit"}""", File.ReadAllText(legacyConfig));
    }

    [Fact]
    public void LoadAgentInstructions_ReadsRootAgentsMd_AndSkipsGlobal()
    {
        var project = CreateDir(Path.Combine(_dir, "instructions-project"));
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "use cargo check");
        var workspace = new WorkspaceManager().Detect(project);

        var instructions = new WorkspaceManager().LoadAgentInstructions(workspace);
        Assert.NotNull(instructions);
        Assert.Contains("use cargo check", instructions);
        Assert.Contains("AGENTS.md", instructions);

        var global = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global-state"));
        Assert.Null(new WorkspaceManager().LoadAgentInstructions(global));
    }
}
