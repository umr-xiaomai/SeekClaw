using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Providers;

/// <summary>
/// Chat Completions client for every OpenAI-compatible API
/// (OpenAI, OpenRouter, Azure OpenAI, Ollama, LM Studio, DeepSeek…).
/// </summary>
public sealed class OpenAiCompatibleClient(ILlmHttpFactory httpFactory) : ILlmClient
{
    public string Kind => "openai";

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpFactory.GetClient(request.Provider);
        var url = LlmUrl.Join(request.Provider.BaseUrl, "chat/completions");

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(message, request.Provider);

        using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        responseCts.CancelAfter(TimeSpan.FromSeconds(request.Provider.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, responseCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Cannot reach {request.Provider.Id}: {ex.Message}", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await ApiError(response, request.Provider.Id, responseCts.Token).ConfigureAwait(false);

            var acc = new Accumulator();
            // Keep the provider timeout active while reading the stream too. Without
            // this, an accepted vision request that never emits its first SSE event can
            // leave an Agent turn in "thinking" forever.
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(responseCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException($"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.");
            }

            await using (stream)
            await using (var events = SseReader.ReadAsync(stream, responseCts.Token).GetAsyncEnumerator())
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = await events.MoveNextAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new LlmException($"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.");
                    }
                    if (!hasNext) break;

                    var sse = events.Current;
                    if (sse.Data == "[DONE]") break;

                    JsonNode? node;
                    try { node = JsonNode.Parse(sse.Data); }
                    catch (JsonException) { continue; }

                    foreach (var evt in acc.Consume(node)) yield return evt;
                }
            }

            yield return new LlmCompleted(acc.Build());
        }
    }

    private static void ApplyHeaders(HttpRequestMessage message, Configuration.ProviderConfig provider)
    {
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (!string.IsNullOrWhiteSpace(provider.Organization))
            message.Headers.TryAddWithoutValidation("OpenAI-Organization", provider.Organization);
        if (provider.Headers is not null)
            foreach (var (name, value) in provider.Headers)
                message.Headers.TryAddWithoutValidation(name, value);
    }

    internal static JsonObject BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
            messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = request.System });

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case ChatRole.User:
                    messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = UserContent(msg) });
                    break;

                case ChatRole.Assistant:
                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = msg.Text };
                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        var calls = new JsonArray();
                        foreach (var call in msg.ToolCalls)
                            calls.Add((JsonNode)new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                            });
                        assistant["tool_calls"] = calls;
                    }
                    messages.Add((JsonNode)assistant);
                    break;

                case ChatRole.Tool:
                    messages.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = msg.ToolCallId,
                        ["content"] = msg.Text,
                    });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model.Id,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        if (request.MaxTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (ReasoningLevelAdapter.OpenAiEffort(request) is { } effort)
            body["reasoning_effort"] = effort;

        if (request.Model.Capabilities.ToolCalling && request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters.DeepClone(),
                    },
                });
            body["tools"] = tools;
        }

        return body;
    }

    private static JsonNode UserContent(ChatMessage message)
    {
        if (message.Images is not { Count: > 0 }) return JsonValue.Create(message.Text)!;

        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(message.Text))
            content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = message.Text });
        foreach (var image in message.Images)
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{image.MediaType};base64,{image.Data}",
                    ["detail"] = "auto",
                },
            });
        return content;
    }

    private static async Task<LlmException> ApiError(HttpResponseMessage response, string providerId, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = "";
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { /* body unavailable */ }

        var detail = ExtractErrorMessage(body);
        var retryable = status is 408 or 409 or 429 or >= 500;
        return new LlmException($"{providerId} returned HTTP {status}: {detail}", status, retryable);
    }

    internal static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.GetValue<string>() ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException) { }
        return body.Length > 400 ? body[..400] : body;
    }

    /// <summary>Accumulates streaming deltas into a full completion.</summary>
    private sealed class Accumulator
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private readonly Dictionary<int, (string Id, string Name, StringBuilder Args)> _toolCalls = [];
        private long _inputTokens;
        private long _cachedInputTokens;
        private long _outputTokens;
        private string _finishReason = "";

        public IEnumerable<LlmStreamEvent> Consume(JsonNode? chunk)
        {
            if (chunk?["usage"] is JsonObject usage)
            {
                _inputTokens = usage["prompt_tokens"]?.GetValue<long>() ?? _inputTokens;
                _outputTokens = usage["completion_tokens"]?.GetValue<long>() ?? _outputTokens;
                _cachedInputTokens = usage["prompt_cache_hit_tokens"]?.GetValue<long>()
                    ?? usage["cached_tokens"]?.GetValue<long>()
                    ?? (usage["prompt_tokens_details"] as JsonObject)?["cached_tokens"]?.GetValue<long>()
                    ?? _cachedInputTokens;
            }

            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices) yield break;
            var choice = choices[0];

            if (choice?["finish_reason"]?.GetValue<string>() is { Length: > 0 } finish)
                _finishReason = finish;

            if (choice?["delta"] is not JsonObject delta) yield break;

            // DeepSeek-style reasoning channel on OpenAI-compatible APIs.
            if (delta["reasoning_content"]?.GetValue<string>() is { Length: > 0 } reasoning)
            {
                _thinking.Append(reasoning);
                yield return new LlmThinkingDelta(reasoning);
            }

            if (delta["content"]?.GetValue<string>() is { Length: > 0 } content)
            {
                _text.Append(content);
                yield return new LlmTextDelta(content);
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var callNode in toolCalls)
                {
                    if (callNode is not JsonObject call) continue;
                    var index = call["index"]?.GetValue<int>() ?? 0;

                    if (!_toolCalls.TryGetValue(index, out var entry))
                    {
                        entry = ("", "", new StringBuilder());
                        _toolCalls[index] = entry;
                    }

                    var id = call["id"]?.GetValue<string>();
                    var name = call["function"]?["name"]?.GetValue<string>();
                    var args = call["function"]?["arguments"]?.GetValue<string>();

                    var announce = entry.Name.Length == 0 && !string.IsNullOrEmpty(name);
                    _toolCalls[index] = (
                        string.IsNullOrEmpty(id) ? entry.Id : id,
                        string.IsNullOrEmpty(name) ? entry.Name : name,
                        entry.Args.Append(args ?? ""));

                    if (announce)
                        yield return new LlmToolCallStarted(_toolCalls[index].Id, name!);
                }
            }
        }

        public LlmCompletion Build() => new()
        {
            Text = _text.ToString(),
            Thinking = _thinking.ToString(),
            ToolCalls = _toolCalls
                .OrderBy(kv => kv.Key)
                .Select(kv => new ToolCallRequest(
                    string.IsNullOrEmpty(kv.Value.Id) ? $"call_{kv.Key}" : kv.Value.Id,
                    kv.Value.Name,
                    kv.Value.Args.Length == 0 ? "{}" : kv.Value.Args.ToString()))
                .ToList(),
            Usage = new TokenUsage(_inputTokens, _outputTokens)
            {
                // OpenAI-compatible usage.prompt_tokens already includes cached tokens.
                TotalInputTokens = _inputTokens,
                CachedInputTokens = _cachedInputTokens,
            },
            FinishReason = _finishReason,
        };
    }
}
