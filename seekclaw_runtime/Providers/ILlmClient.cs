namespace SeekClaw.Runtime.Providers;

/// <summary>Streams a chat completion for one provider protocol (openai / anthropic).</summary>
public interface ILlmClient
{
    string Kind { get; }
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken ct);
}

public interface ILlmClientFactory
{
    ILlmClient GetClient(string kind);
}

public sealed class LlmClientFactory(IEnumerable<ILlmClient> clients) : ILlmClientFactory
{
    private readonly Dictionary<string, ILlmClient> _clients =
        clients.ToDictionary(c => c.Kind, StringComparer.OrdinalIgnoreCase);

    public ILlmClient GetClient(string kind) =>
        _clients.TryGetValue(kind, out var client)
            ? client
            : throw new LlmException($"No LLM client registered for provider kind '{kind}'.", retryable: false);
}
