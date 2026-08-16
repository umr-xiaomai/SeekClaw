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
    public void Detect_ProjectLevelSeekClawDir_StillResolvesToThatProject()
    {
        var project = CreateDir(Path.Combine(_dir, "projB"));
        Directory.CreateDirectory(Path.Combine(project, ".seekclaw"));

        Assert.Equal(Path.GetFullPath(project), new WorkspaceManager().Detect(project).Root);
    }

    [Fact]
    public void IsOnlySeekClawHomeMarker_MatchesOnlyTheGlobalStateDirectory()
    {
        var home = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(Path.Combine(home, ".seekclaw"));
        var dir = new DirectoryInfo(home);
        var stateDir = Path.Combine(home, ".seekclaw");

        // The profile's ~/.seekclaw is excluded only when it is exactly the global state dir.
        Assert.True(new WorkspaceManager(stateDir).IsOnlySeekClawHomeMarker(dir, [".seekclaw"]));
        Assert.False(new WorkspaceManager(Path.Combine(_dir, "other-home")).IsOnlySeekClawHomeMarker(dir, [".seekclaw"]));

        // Real project markers (git, or another marker beside .seekclaw) still resolve.
        Assert.False(new WorkspaceManager(stateDir).IsOnlySeekClawHomeMarker(dir, [".seekclaw", ".git"]));
        Assert.False(new WorkspaceManager(stateDir).IsOnlySeekClawHomeMarker(dir, [".git"]));
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
