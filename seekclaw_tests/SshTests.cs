using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Projects;
using SeekClaw.Runtime.Ssh;
using SeekClaw.Runtime.Workspaces;
using Xunit;

namespace SeekClaw.Tests;

public sealed class SshTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "seekclaw-ssh-tests-" + Guid.NewGuid().ToString("N"));

    public SshTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SshPath_Formats_Parses_And_Resolves_Posix_Paths()
    {
        Assert.True(SshPath.IsSshPath("ssh://abc123/home/ubuntu/app"));
        Assert.False(SshPath.IsSshPath("E:\\Project\\SeekClaw"));

        Assert.Equal("ssh://abc123/home/ubuntu/app", SshPath.Format("ABC123", "/home/ubuntu/app/"));
        Assert.Equal("ssh://abc123/~", SshPath.Format("abc123", "~"));
        Assert.Equal("ssh://abc123/", SshPath.Format("abc123", "/"));

        Assert.True(SshPath.TryParse("ssh://abc123/~/projects/demo", out var id1, out var path1));
        Assert.Equal("abc123", id1);
        Assert.Equal("~/projects/demo", path1);

        Assert.True(SshPath.TryParse("ssh://abc123/var/www", out var id2, out var path2));
        Assert.Equal("abc123", id2);
        Assert.Equal("/var/www", path2);

        Assert.Equal("/var/www/src/index.ts", SshPath.ResolveRemotePath("/var/www", "./src/index.ts"));
        Assert.Equal("/etc/nginx/nginx.conf", SshPath.ResolveRemotePath("/var/www", "/etc/nginx/nginx.conf"));
        Assert.Equal("~/apps/api", SshPath.ResolveRemotePath("~/apps", "./api"));
        Assert.Equal("app", SshPath.GetDisplayName("/home/ubuntu/app", "prod"));
        Assert.Equal("prod", SshPath.GetDisplayName("~", "prod"));
        Assert.Equal("'hello '\\''world'\\'''", SshPath.ShellQuote("hello 'world'"));
    }

    [Fact]
    public void Database_PathKey_And_ProjectStore_Preserve_Ssh_Uris()
    {
        var uri = "ssh://srv1/home/root/backend";
        Assert.Equal(uri, SeekClawDatabase.PathKey(uri + "/"));
        Assert.False(SeekClawPaths.IsForbiddenProjectPath(uri));

        var db = new SeekClawDatabase(Path.Combine(_tempDir, "test.db"));
        var store = new ProjectStore(db);
        var saved = store.Upsert(null, uri, null);

        Assert.Equal(uri, saved.Path);
        Assert.Equal("backend", saved.Name);
        Assert.Single(store.List());

        var wm = new WorkspaceManager(_tempDir);
        var ws = wm.CreateSsh(new SshConnectionProfile
        {
            Id = "srv1",
            Name = "Prod Server",
            Host = "root@10.0.0.5",
            Port = 22,
            AuthMode = SshAuthMode.None
        }, "/home/root/backend");

        Assert.True(ws.IsSsh);
        Assert.Equal(uri, ws.Root);
        Assert.Equal("/home/root/backend", ws.WorkingDirectory);
        Assert.Equal("backend", ws.Name);
    }

    [Fact]
    public void SshConnectionStore_Persists_And_Preserves_Password_When_Omitted_On_Edit()
    {
        var storeFile = Path.Combine(_tempDir, "ssh-connections.json");
        var store = new SshConnectionStore(storeFile);

        var created = store.Upsert(new SshConnectionProfile
        {
            Name = "GPU Box",
            Host = "ubuntu@192.168.1.50",
            Port = 2222,
            AuthMode = SshAuthMode.Password,
            Password = "secret-value-1"
        });

        Assert.NotEmpty(created.Id);
        Assert.Equal("secret-value-1", store.Get(created.Id)?.Password);

        var updated = store.Upsert(new SshConnectionProfile
        {
            Id = created.Id,
            Name = "GPU Box Renamed",
            Host = "ubuntu@192.168.1.50",
            Port = 2222,
            AuthMode = SshAuthMode.Password,
            Password = null
        }, preserveExistingPassword: true);

        Assert.Equal("GPU Box Renamed", updated.Name);
        Assert.Equal("secret-value-1", store.Get(created.Id)?.Password);

        Assert.True(store.Remove(created.Id));
        Assert.Empty(store.List());
    }

    [Fact]
    public void ParseSshConfigHosts_Skips_Wildcards_And_Reads_Host_Entries()
    {
        var cfg = Path.Combine(_tempDir, "ssh_config");
        File.WriteAllText(cfg, """
            Host *
                ServerAliveInterval 60

            Host prod-api
                HostName 10.22.245.9
                User deploy
                Port 2202
                IdentityFile ~/.ssh/id_ed25519

            # comment line
            Host staging
                HostName staging.example.com
            """);

        var hosts = SshConnectionStore.ParseSshConfigHosts(cfg);
        Assert.Equal(2, hosts.Count);
        Assert.Equal("prod-api", hosts[0].Alias);
        Assert.Equal("10.22.245.9", hosts[0].HostName);
        Assert.Equal("deploy", hosts[0].User);
        Assert.Equal(2202, hosts[0].Port);
        Assert.Equal("~/.ssh/id_ed25519", hosts[0].IdentityFile);
        Assert.Equal("staging", hosts[1].Alias);
        Assert.Equal("staging.example.com", hosts[1].HostName);
    }
}
