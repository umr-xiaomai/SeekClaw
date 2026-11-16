using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class SessionTitleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-session-titles", Guid.NewGuid().ToString("N"));
    private WorkspaceInfo Workspace => new() { Root = _root, ProjectKinds = [], Config = new WorkspaceConfig { Model = "title-model" } };

    public SessionTitleTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("  “优化工作区菜单。补充更多解释。”  ", "优化工作区菜单")]
    [InlineData("### **Fix C# rendering**\nThis is an explanation", "Fix C# rendering")]
    [InlineData("Fix renderer! Then add tests.", "Fix renderer")]
    [InlineData("Inspect v2.1.1 output? Explain the result.", "Inspect v2.1.1 output")]
    [InlineData("Fix .NET integration. Include regression tests.", "Fix .NET integration")]
    [InlineData("  `Improve C#`  ", "Improve C#")]
    [InlineData("\r\n  \t", null)]
    public void NormalizationKeepsOnlyTheCleanFirstSentence(string raw, string? expected) =>
        Assert.Equal(expected, SessionTitleGenerator.Normalize(raw));

    [Theory]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("e\u0301")]
    public void LongTitlesAreTruncatedAtTextElementBoundaries(string element)
    {
        var title = SessionTitleGenerator.Normalize(string.Concat(Enumerable.Repeat(element, 66)));
        Assert.Equal(string.Concat(Enumerable.Repeat(element, 64)) + "…", title);
        Assert.Equal(65, StringInfo.ParseCombiningCharacters(title!).Length);
    }

    [Theory]
    [InlineData(128, 128)]
    [InlineData(4096, 256)]
    public async Task GenerationUsesASmallToolFreeRequestWithoutThinking(int modelLimit, int expectedLimit)
    {
        var provider = new StubProviders(new LlmTextDelta("修复浏览器侧栏"), new LlmCompleted(new LlmCompletion { Text = "修复浏览器侧栏。额外解释" }));
        provider.Model.Model.MaxOutput = modelLimit;
        provider.Model.Model.MaxOutputAuto = false;
        var workspace = Workspace;

        var title = await SessionTitleGenerator.GenerateAsync(provider, workspace, "请修复浏览器侧栏布局", CancellationToken.None);

        Assert.Equal("修复浏览器侧栏", title);
        var request = Assert.IsType<LlmRequest>(provider.Request);
        Assert.Same(workspace.Config, provider.WorkspaceConfig);
        Assert.Same(provider.Model.Model, request.Model);
        Assert.Equal(expectedLimit, request.MaxTokens);
        Assert.Empty(request.Tools);
        Assert.False(request.EnableThinking);
        Assert.Equal(ReasoningLevel.None, request.ReasoningLevel);
        Assert.Equal("请修复浏览器侧栏布局", Assert.Single(request.Messages).Text);
        Assert.Equal(ChatRole.User, request.Messages[0].Role);
        Assert.True(provider.CandidateFilter!(provider.Model));
        Assert.False(provider.CandidateFilter(new ModelInfo(new ProviderConfig { Id = "other" }, new ModelConfig { Id = "other" })));
    }

    [Fact]
    public async Task CompletionOnlyResponsesAreAcceptedAndRetryOutputIsDiscarded()
    {
        var provider = new StubProviders(new LlmTextDelta("Discard this title"), new LlmStreamReset("provider/title-model"),
            new LlmTextDelta("Correct title"));
        Assert.Equal("Correct title", await SessionTitleGenerator.GenerateAsync(provider, Workspace, "Task", CancellationToken.None));
        var completionOnly = new StubProviders(new LlmCompleted(new LlmCompletion { Text = "A completed title" }));
        Assert.Equal("A completed title", await SessionTitleGenerator.GenerateAsync(completionOnly, Workspace, "Task", CancellationToken.None));
        var failed = new StubProviders(new LlmTextDelta("Partial"), new LlmCompleted(new LlmCompletion { Text = "Partial", ErrorMessage = "Upstream disconnected" }));
        Assert.Null(await SessionTitleGenerator.GenerateAsync(failed, Workspace, "Task", CancellationToken.None));
    }

    [Fact]
    public async Task PromptBudgetDoesNotSplitEmojiOrCombiningSequences()
    {
        foreach (var suffix in new[] { "👩‍💻", "e\u0301" })
        {
            var provider = new StubProviders(new LlmCompleted(new LlmCompletion { Text = "Title" }));
            var prefix = new string('a', 5999);
            await SessionTitleGenerator.GenerateAsync(provider, Workspace, prefix + suffix + "tail", CancellationToken.None);
            Assert.Equal(prefix, Assert.Single(provider.Request!.Messages).Text);
        }
    }

    [Fact]
    public async Task CancellationStopsGenerationAndDisposesTheProviderStream()
    {
        var provider = new StubProviders(new LlmTextDelta("ignored"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeOutput = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var cancellation = new CancellationTokenSource();
        var task = SessionTitleGenerator.GenerateAsync(provider, Workspace, "Task", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(provider.Disposed);
        var alreadyCancelled = new StubProviders();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionTitleGenerator.GenerateAsync(alreadyCancelled, Workspace, "Task", cancellation.Token));
        Assert.Null(alreadyCancelled.Request);
    }

    [Theory]
    [InlineData("User-selected title")]
    [InlineData("Original user task")]
    public async Task AManualRenameWinsOverAnInFlightTitleEvenWhenItEqualsThePrompt(string manualTitle)
    {
        var database = new SeekClawDatabase(Path.Combine(_root, "data.db"));
        var store = new SessionStore(database);
        var session = store.Create(Workspace);
        store.Append(session, ChatMessage.User("Original user task"));
        Assert.Equal("input", store.Load(Workspace, session.Header.Id)!.Header.TitleSource);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubProviders(new LlmCompleted(new LlmCompletion { Text = "Automatically generated title" }))
        {
            BeforeOutput = async _ => { started.SetResult(); await release.Task; }
        };
        async Task<SessionHeader?> GenerateAndStore()
        {
            var title = await SessionTitleGenerator.GenerateAsync(provider, Workspace, "Original user task", CancellationToken.None);
            return store.TrySetGeneratedTitle(Workspace, session.Header.Id, title!);
        }
        var pending = GenerateAndStore();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.UpdateMetadata(Workspace, session.Header.Id, title: manualTitle);
        release.SetResult();

        Assert.Null(await pending);
        var loaded = store.Load(Workspace, session.Header.Id)!;
        Assert.Equal(manualTitle, loaded.Header.Title);
        Assert.Equal("manual", loaded.Header.TitleSource);
        Assert.Single(loaded.Messages);
    }

    [Fact]
    public void GeneratedTitlesUseCompareAndSetWithoutReorderingOrChangingConversationHistory()
    {
        var database = new SeekClawDatabase(Path.Combine(_root, "data.db"));
        var store = new SessionStore(database);
        var older = store.Create(Workspace);
        store.Append(older, ChatMessage.User("Original task"));
        var newer = store.Create(Workspace);
        store.Append(newer, ChatMessage.User("Newer task"));
        SetUpdatedAt(database, older.Header.Id, "2026-11-15T00:00:00.0000000+00:00");
        SetUpdatedAt(database, newer.Header.Id, "2026-11-16T00:00:00.0000000+00:00");
        var original = store.Load(Workspace, older.Header.Id)!;

        var updated = store.TrySetGeneratedTitle(Workspace, older.Header.Id, "  Summarized task  ");

        Assert.NotNull(updated);
        Assert.Equal("Summarized task", updated.Title);
        Assert.Equal("generated", updated.TitleSource);
        Assert.Equal(original.Header.UpdatedAt, updated.UpdatedAt);
        Assert.Equal([newer.Header.Id, older.Header.Id], store.List(Workspace).Select(header => header.Id));
        Assert.Equal("Original task", Assert.Single(store.Load(Workspace, older.Header.Id)!.Messages).Text);
        Assert.Null(store.TrySetGeneratedTitle(Workspace, older.Header.Id, "A second generated title"));
        Assert.Equal("Summarized task", store.Load(Workspace, older.Header.Id)!.Header.Title);
        Assert.Null(store.TrySetGeneratedTitle(new WorkspaceInfo { Root = _root, ProjectKinds = [], IsGlobal = true }, older.Header.Id, "Wrong scope"));
    }

    [Fact]
    public void ClearingAManualTitleThenAppendingDoesNotMakeItEligibleForAutomaticNaming()
    {
        var store = new SessionStore(Path.Combine(_root, "data.db"));
        var session = store.Create(Workspace);
        store.Append(session, ChatMessage.User("Original task"));
        store.UpdateMetadata(Workspace, session.Header.Id, title: "  ");
        var renamed = store.Load(Workspace, session.Header.Id)!;

        store.Append(renamed, ChatMessage.User("Another task instruction"));

        Assert.Null(renamed.Header.Title);
        Assert.Equal("manual", renamed.Header.TitleSource);
        var loaded = store.Load(Workspace, session.Header.Id)!;
        Assert.Null(loaded.Header.Title);
        Assert.Equal("manual", loaded.Header.TitleSource);
        Assert.Null(store.TrySetGeneratedTitle(Workspace, session.Header.Id, "Automatic replacement"));
        Assert.Equal(2, loaded.Messages.Count);
    }

    [Fact]
    public void LateGenerationCannotResurrectADeletedSession()
    {
        var store = new SessionStore(Path.Combine(_root, "data.db"));
        var session = store.Create(Workspace);
        store.Append(session, ChatMessage.User("Deleted task"));
        store.Delete(Workspace, session.Header.Id);

        Assert.Null(store.TrySetGeneratedTitle(Workspace, session.Header.Id, "Late generated title"));
        Assert.Null(store.Load(Workspace, session.Header.Id));
        Assert.Empty(store.List(Workspace));
    }

    [Fact]
    public void MigratingAnOldDatabaseTreatsExistingTitlesAsManual()
    {
        var file = Path.Combine(_root, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE sessions (
                    scope TEXT NOT NULL, id TEXT NOT NULL, workspace TEXT NULL, title TEXT NULL,
                    archived INTEGER NOT NULL DEFAULT 0, reasoning_level INTEGER NOT NULL,
                    network_enabled INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    PRIMARY KEY(scope, id)
                );
                INSERT INTO sessions(scope, id, workspace, title, reasoning_level, created_at, updated_at)
                VALUES($scope, 'legacy-session', $workspace, 'My existing title', 3,
                    '2026-10-01T00:00:00+00:00', '2026-10-01T00:00:00+00:00');
                """;
            schema.Parameters.AddWithValue("$scope", SeekClawDatabase.ScopeKey(Workspace));
            schema.Parameters.AddWithValue("$workspace", _root);
            schema.ExecuteNonQuery();
        }
        var store = new SessionStore(file);
        var existing = store.Load(Workspace, "legacy-session")!;
        Assert.Equal("manual", existing.Header.TitleSource);
        Assert.Equal("My existing title", existing.Header.Title);
        Assert.Null(store.TrySetGeneratedTitle(Workspace, "legacy-session", "Replacement title"));
        Assert.Equal("My existing title", store.Load(Workspace, "legacy-session")!.Header.Title);
    }

    private void SetUpdatedAt(SeekClawDatabase database, string id, string timestamp)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET updated_at=$timestamp WHERE scope=$scope AND id=$id;";
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$scope", SeekClawDatabase.ScopeKey(Workspace));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class StubProviders(params LlmStreamEvent[] events) : IProviderManager
    {
        public ModelInfo Model { get; } = new(new ProviderConfig { Id = "provider" }, new ModelConfig { Id = "title-model" });
        public LlmRequest? Request { get; private set; }
        public WorkspaceConfig? WorkspaceConfig { get; private set; }
        public Func<ModelInfo, bool>? CandidateFilter { get; private set; }
        public Func<CancellationToken, Task>? BeforeOutput { get; set; }
        public bool Disposed { get; private set; }

        public ModelInfo ResolveActive(WorkspaceConfig? workspace = null) => Model;
        public IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null) => [Model];
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(Func<ModelInfo, LlmRequest> requestFactory, WorkspaceConfig? workspace,
            [EnumeratorCancellation] CancellationToken ct, Func<ModelInfo, bool>? candidateFilter = null)
        {
            Request = requestFactory(Model);
            WorkspaceConfig = workspace;
            CandidateFilter = candidateFilter;
            try
            {
                if (BeforeOutput is not null) await BeforeOutput(ct);
                foreach (var item in events) { ct.ThrowIfCancellationRequested(); yield return item; }
            }
            finally { Disposed = true; }
        }
        public Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FetchModelsAsync(ProviderConfig provider, string? url = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
