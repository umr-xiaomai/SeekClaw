using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

public sealed partial class Agent
{
    private async Task CaptureCrossSessionMemoryAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        ModelInfo model,
        string userInput,
        string finalText,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return;
        var instruction = promptProvider.TryGet("builtin/memory_extract");
        if (instruction is null)
        {
            events.Publish(new Events.WarningEvent("Cross-session memory prompt 'builtin/memory_extract' is missing; no memory was saved."));
            return;
        }

        try
        {
            var existing = memoryStore.ListRecent(40).Reverse().Select(entry => $"- {entry.Content}");
            var input = $"""
                <existing-memory>
                {string.Join("\n", existing)}
                </existing-memory>
                <current-user-message>
                {BoundedMemoryInput(userInput)}
                </current-user-message>
                <current-assistant-answer>
                {BoundedMemoryInput(finalText)}
                </current-assistant-answer>
                """;
            var completion = await CollectCompletionAsync(
                candidate => new LlmRequest
                {
                    Provider = candidate.Provider,
                    Model = candidate.Model,
                    Messages = [ChatMessage.User(input)],
                    System = instruction,
                    Temperature = 0,
                    MaxTokens = Math.Min(candidate.Model.MaxOutput, 1_200),
                    EnableThinking = false,
                    ReasoningLevel = ReasoningLevel.None,
                },
                workspace, ct).ConfigureAwait(false);

            foreach (var content in ParseCrossSessionMemories(completion?.Text))
                memoryStore.Save(content, workspace, session.Header.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Memory maintenance is optional; cancellation must not invalidate the finished turn.
        }
        catch (Exception ex)
        {
            // A memory extraction failure is never allowed to turn a successful task into an error.
            events.Publish(new Events.WarningEvent($"Cross-session memory was not updated: {ex.Message}"));
        }
    }

    internal static IReadOnlyList<string> ParseCrossSessionMemories(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var text = output.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                text = text[(firstNewLine + 1)..lastFence].Trim();
        }

        var candidates = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                candidates.AddRange(document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? ""));
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object
                     && document.RootElement.TryGetProperty("memories", out var memoryArray)
                     && memoryArray.ValueKind == JsonValueKind.Array)
            {
                candidates.AddRange(memoryArray.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? ""));
            }
        }
        catch (JsonException)
        {
            candidates.Clear();
        }

        // Compatible fallback for models that ignore the JSON-only instruction.
        if (candidates.Count == 0 && !text.StartsWith('[') && !text.StartsWith('{'))
            candidates.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var memories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var content = StripListMarker(candidate);
            if (content.Length == 0 || content.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
            content = string.Join(' ', content.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (content.Length == 0 || SensitiveMemoryRegex().IsMatch(content)) continue;
            if (content.Length > 500) content = content[..500].Trim();
            if (seen.Add(content)) memories.Add(content);
            if (memories.Count == 6) break;
        }
        return memories;
    }

    private static string StripListMarker(string value)
    {
        var text = value.Trim().TrimStart('-', '*', '•').TrimStart();
        var dot = text.IndexOf('.');
        if (dot > 0 && dot <= 3 && text[..dot].All(char.IsDigit) && dot + 1 < text.Length)
            text = text[(dot + 1)..].TrimStart();
        return text.Trim().Trim('"').Trim();
    }

    private static string BoundedMemoryInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        text = text.Trim();
        return text.Length <= 12_000 ? text : text[..12_000] + "\n...(truncated)";
    }

    [GeneratedRegex(@"(?:-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\b(?:api[_-]?key|access[_-]?token|secret|password|passwd)\b\s*[:=]\s*\S+|\bBearer\s+[A-Za-z0-9._~+/=-]{16,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveMemoryRegex();
}
