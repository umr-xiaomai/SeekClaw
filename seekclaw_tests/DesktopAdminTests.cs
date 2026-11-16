using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Browser;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Daemon;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class DesktopAdminTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-desktop-admin", Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _config;
    private readonly SeekClawRuntime _runtime;
    private readonly DaemonAdminApi _admin;
    private readonly ModelHttpFactory _http = new();

    public DesktopAdminTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        _config.Config.Providers.Clear();
        var workspace = new WorkspaceInfo { Root = Path.Combine(_root, "workspace"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);
        _runtime = SeekClawRuntime.CreateIsolated(workspace, configureServices: services => services
            .AddSingleton<IConfigStore>(_config)
            .AddSingleton<ILlmHttpFactory>(_http)
            .AddSingleton(new SeekClawDatabase(Path.Combine(_root, "test.db"))));
        _admin = new DaemonAdminApi(_runtime, new WorkspaceManager().CreateGlobal(Path.Combine(_root, "global")), new FileLockCoordinator());
    }

    [Fact]
    public async Task PreviewUsesUnsavedConnection_WithoutImportingOrSaving()
    {
        var saved = new ProviderConfig { Id = "saved", BaseUrl = "https://saved.test/v1", ApiKey = "saved-example" };
        _config.Config.Providers.Add(saved);
        _config.Save();
        var before = File.ReadAllText(Path.Combine(_root, "config.json"));
        var result = JsonNode.Parse(await _admin.PreviewProviderModelsAsync(new JsonObject
        {
            ["editingId"] = "saved", ["draft"] = new JsonObject
            {
                ["baseUrl"] = "https://draft.test/v1", ["apiKey"] = "draft-example", ["kind"] = "openai",
            },
        }, CancellationToken.None))!.AsArray();
        Assert.Equal("https://draft.test/v1/models", _http.LastUrl);
        Assert.Equal("draft-example", _http.LastToken);
        Assert.Equal("example", Assert.Single(result)!["id"]!.GetValue<string>());
        Assert.Empty(saved.Models);
        Assert.Equal("https://saved.test/v1", saved.BaseUrl);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "config.json")));
    }

    [Fact]
    public void RejectedProviderEdit_DoesNotMutateLiveConfiguration()
    {
        var saved = new ProviderConfig { Id = "saved", BaseUrl = "https://saved.test/v1", Models = [new ModelConfig { Id = "old" }] };
        _config.Config.Providers.Add(saved);
        Assert.Throws<DaemonRequestException>(() => _admin.UpsertProvider(new JsonObject
        {
            ["id"] = saved.Id, ["baseUrl"] = "https://draft.test/v1", ["models"] = new JsonArray(),
        }));
        Assert.Same(saved, Assert.Single(_config.Config.Providers));
        Assert.Equal("https://saved.test/v1", saved.BaseUrl);
        Assert.Equal("old", Assert.Single(saved.Models).Id);
    }

    [Fact]
    public void BrowserScope_RequiresSessionInRequestedWorkspace_AndCanonicalizesTrailingSeparator()
    {
        var session = _runtime.Sessions.Create(_runtime.Workspace);
        var scope = JsonNode.Parse(_admin.BrowserScope(new JsonObject { ["sessionId"] = session.Header.Id }))!["scope"]!.GetValue<string>();
        Assert.Equal(BrowserTool.Scope(_runtime.Workspace.Root + Path.DirectorySeparatorChar, session.Header.Id), scope);
        Assert.Throws<DaemonRequestException>(() => _admin.BrowserScope(new JsonObject
        {
            ["sessionId"] = session.Header.Id, ["global"] = true,
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await _runtime.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class ModelHttpFactory : ILlmHttpFactory, IDisposable
    {
        private readonly HttpClient _client;
        public string? LastUrl { get; private set; }
        public string? LastToken { get; private set; }
        public ModelHttpFactory() => _client = new HttpClient(new Handler(this));
        public HttpClient GetClient(ProviderConfig provider) => _client;
        public void Dispose() => _client.Dispose();
        private sealed class Handler(ModelHttpFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.LastUrl = request.RequestUri!.AbsoluteUri;
                owner.LastToken = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"example","context_length":262144}]}"""),
                });
            }
        }
    }
}
