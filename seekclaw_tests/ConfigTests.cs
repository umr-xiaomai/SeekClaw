using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-tests", Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ConfigStore NewStore() => new(
        Path.Combine(_dir, "config.json"),
        Path.Combine(_dir, "state.json"));

    [Fact]
    public void MissingConfig_CreatesDefaults_AndPersists()
    {
        var store = NewStore();
        Assert.Equal("default", store.Config.ActiveProfile);
        Assert.True(File.Exists(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public void SaveAndReload_RoundTripsProvidersAndProfiles()
    {
        var store = NewStore();
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "test",
            Kind = "openai",
            BaseUrl = "https://example.test/v1",
            ApiKeyEnv = "TEST_KEY",
            Models = [new ModelConfig { Id = "m1", Alias = "one", ContextWindow = 9000, InputPricePerMTok = 1.5m }],
        });
        store.Config.Profiles["work"] = new ProfileConfig { Provider = "test", Model = "m1", Temperature = 0.3 };
        store.Save();

        var reloaded = NewStore();
        var provider = Assert.Single(reloaded.Config.Providers);
        Assert.Equal("test", provider.Id);
        Assert.Equal(9000, provider.Models[0].ContextWindow);
        Assert.Equal("one", provider.Models[0].Alias);
        Assert.Equal(1.5m, provider.Models[0].InputPricePerMTok);
        Assert.Equal(0.3, reloaded.Config.Profiles["work"].Temperature);
    }

    [Fact]
    public void CorruptConfig_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ not json !!");
        var store = NewStore();
        Assert.Equal("default", store.Config.ActiveProfile);
    }

    [Fact]
    public void ResolveApiKey_PrefersExplicit_ThenEnvironment()
    {
        var provider = new ProviderConfig { ApiKey = "direct", ApiKeyEnv = "SEEKCLAW_TEST_KEY" };
        Assert.Equal("direct", provider.ResolveApiKey());

        provider.ApiKey = null;
        Environment.SetEnvironmentVariable("SEEKCLAW_TEST_KEY", "from-env");
        try
        {
            Assert.Equal("from-env", provider.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEEKCLAW_TEST_KEY", null);
        }
    }
}
