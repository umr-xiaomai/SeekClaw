using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Providers;

/// <summary>Anthropic Messages API client (streaming, tool use, extended thinking).</summary>
public sealed class AnthropicClient(ILlmHttpFactory httpFactory) : ILlmClient
{
    private const string ApiVersion = "2023-06-01";

    public string Kind => "anthropic";

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpFactory.GetClient(request.Provider);
        var url = LlmUrl.JoinV1(request.Provider.BaseUrl, "messages");
        var hasImages = request.Messages.Any(message => message.Images is { Count: > 0 });

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
            {
                var status = (int)response.StatusCode;
                var body = "";
                try { body = await response.Content.ReadAsStringAsync(responseCts.Token).ConfigureAwait(false); } catch { }
                throw new LlmException(
                    $"{request.Provider.Id} returned HTTP {status}: {OpenAiCompatibleClient.ExtractErrorMessage(body)}",
                    status,
                    retryable: status is 408 or 409 or 429 or 529 or >= 500);
            }

            // Compatible gateways do not always honour stream=false. If one answers with
            // SSE anyway, keep using the streaming parser instead of buffering forever.
            var isEventStream = string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase);
            if (hasImages && !isEventStream)
            {
                string body;
                try { body = await response.Content.ReadAsStringAsync(responseCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LlmException($"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.");
                }
                JsonNode? node;
                try { node = JsonNode.Parse(body); }
                catch (JsonException ex)
                {
                    throw new LlmException($"{request.Provider.Id} returned an invalid non-streaming response.", inner: ex);
                }

                var completion = ParseCompletion(node, request.Provider.Id);
                if (completion.Thinking.Length > 0) yield return new LlmThinkingDelta(completion.Thinking);
                foreach (var call in completion.ToolCalls)
                    yield return new LlmToolCallStarted(call.Id, call.Name);
                if (completion.Text.Length > 0) yield return new LlmTextDelta(completion.Text);
                yield return new LlmCompleted(completion);
                yield break;
            }

            var acc = new Accumulator();
            // Apply the same timeout to SSE consumption as to response headers. Vision
            // requests can otherwise wait indefinitely while the provider processes data.
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
                    JsonNode? node;
                    try { node = JsonNode.Parse(sse.Data); }
                    catch (JsonException) { continue; }

                    var type = node?["type"]?.GetValue<string>() ?? sse.Event ?? "";
                    if (type == "error")
                        throw new LlmException(
                            $"{request.Provider.Id} stream error: {node?["error"]?["message"]?.GetValue<string>() ?? "unknown"}");

                    foreach (var evt in acc.Consume(type, node)) yield return evt;
                    if (type == "message_stop") break;
                }
            }

            yield return new LlmCompleted(acc.Build());
        }
    }

    internal static LlmCompletion ParseCompletion(JsonNode? root, string providerId)
    {
        // Non-object payloads must not reach the indexer below ("The node must be of
        // type 'JsonObject'."); report them as a clean provider error instead.
        if (root is not JsonObject obj)
            throw new LlmException($"{providerId} returned an invalid non-streaming response.", retryable: false);
        if (obj["error"] is JsonNode error)
            throw new LlmException($"{providerId} returned an error: {OpenAiCompatibleClient.ExtractErrorMessage(error.ToJsonString())}", retryable: false);

        if (obj["content"] is not JsonArray content)
            throw new LlmException($"{providerId} returned no completion content.", retryable: false);

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<ToolCallRequest>();
        foreach (var item in content.OfType<JsonObject>())
        {
            switch (item["type"]?.GetValue<string>())
            {
                case "text":
                    text.Append(item["text"]?.GetValue<string>() ?? "");
                    break;
                case "thinking":
                    thinking.Append(item["thinking"]?.GetValue<string>() ?? "");
                    break;
                case "tool_use":
                    var name = item["name"]?.GetValue<string>() ?? "";
                    if (name.Length > 0)
                        calls.Add(new ToolCallRequest(
                            item["id"]?.GetValue<string>() ?? $"toolu_{calls.Count}",
                            name,
                            item["input"]?.ToJsonString() ?? "{}"));
                    break;
            }
        }

        var usage = obj["usage"] as JsonObject;
        var inputTokens = usage?["input_tokens"]?.GetValue<long>() ?? 0;
        var outputTokens = usage?["output_tokens"]?.GetValue<long>() ?? 0;
        return new LlmCompletion
        {
            Text = text.ToString(),
            Thinking = thinking.ToString(),
            ToolCalls = calls,
            FinishReason = obj["stop_reason"]?.GetValue<string>() ?? "",
            Usage = new TokenUsage(inputTokens, outputTokens)
            {
                TotalInputTokens = inputTokens,
                CachedInputTokens = usage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0,
                CacheCreationInputTokens = usage?["cache_creation_input_tokens"]?.GetValue<long>() ?? 0,
            },
        };
    }

    private static void ApplyHeaders(HttpRequestMessage message, Configuration.ProviderConfig provider)
    {
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            message.Headers.TryAddWithoutValidation("x-api-key", key);
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (provider.Headers is not null)
            foreach (var (name, value) in provider.Headers)
                message.Headers.TryAddWithoutValidation(name, value);
    }

    internal static JsonObject BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();

        for (var index = 0; index < request.Messages.Count; index++)
        {
            var msg = request.Messages[index];
            switch (msg.Role)
            {
                case ChatRole.User:
                    messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = UserContent(msg) });
                    break;

                case ChatRole.Assistant:
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(msg.Text))
                        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = msg.Text });
                    if (msg.ToolCalls is { Count: > 0 })
                        foreach (var call in msg.ToolCalls)
                            content.Add((JsonNode)new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = call.Id,
                                ["name"] = call.Name,
                                ["input"] = ParseOrEmpty(call.ArgumentsJson),
                            });
                    if (content.Count > 0)
                        messages.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;

                case ChatRole.Tool:
                    // Anthropic requires every tool_result for an assistant tool_use turn to
                    // appear in one immediately following user message. One user message per
                    // result is rejected when the assistant requested multiple tools.
                    var toolResults = new JsonArray();
                    while (index < request.Messages.Count && request.Messages[index].Role == ChatRole.Tool)
                    {
                        var result = request.Messages[index];
                        toolResults.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.ToolCallId,
                            ["content"] = result.Text,
                            ["is_error"] = !result.ToolSuccess,
                        });
                        index++;
                    }
                    index--;
                    messages.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = toolResults,
                    });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model.Id,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens ?? request.Model.MaxOutput,
            // Some Anthropic-compatible vision gateways keep an image SSE request open
            // without emitting message_stop. Use the complete response path for image turns.
            ["stream"] = !request.Messages.Any(message => message.Images is { Count: > 0 }),
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            body["system"] = request.Provider.PromptCaching
                ? new JsonArray((JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.System,
                    ["cache_control"] = CacheControl(),
                })
                : request.System;
        }

        var thinkingBudget = request.EnableThinking && request.Model.Capabilities.Thinking
            ? ReasoningLevelAdapter.AnthropicBudget(request)
            : 0;
        if (thinkingBudget > 0)
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = thinkingBudget,
            };
        else if (request.Temperature is { } temperature)
            body["temperature"] = temperature; // temperature must stay default when thinking is on

        if (request.Model.Capabilities.ToolCalling && request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add((JsonNode)new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = tool.Parameters.DeepClone(),
                });
            // Tools precede system/messages in Anthropic's cache hierarchy. A checkpoint on
            // the last definition keeps the usually-large, stable schema prefix reusable even
            // when a custom system prompt changes.
            if (request.Provider.PromptCaching && tools[^1] is JsonObject lastTool)
                lastTool["cache_control"] = CacheControl();
            body["tools"] = tools;
        }

        return body;
    }

    private static JsonNode UserContent(ChatMessage message)
    {
        if (message.Images is not { Count: > 0 }) return JsonValue.Create(message.Text)!;

        var content = new JsonArray();
        foreach (var image in message.Images)
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = image.Data,
                },
            });
        if (!string.IsNullOrWhiteSpace(message.Text))
            content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = message.Text });
        return content;
    }

    private static JsonNode ParseOrEmpty(string json)
    {
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static JsonObject CacheControl() => new() { ["type"] = "ephemeral" };

    private sealed class Accumulator
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private readonly Dictionary<int, (string Id, string Name, StringBuilder Args)> _toolBlocks = [];
        private long _inputTokens;
        private long _cachedInputTokens;
        private long _cacheCreationInputTokens;
        private long _outputTokens;
        private string _finishReason = "";

        public IEnumerable<LlmStreamEvent> Consume(string type, JsonNode? node)
        {
            switch (type)
            {
                case "message_start":
                {
                    var usage = node?["message"]?["usage"];
                    _inputTokens = usage?["input_tokens"]?.GetValue<long>() ?? 0;
                    _cachedInputTokens = usage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
                    _cacheCreationInputTokens = usage?["cache_creation_input_tokens"]?.GetValue<long>() ?? 0;
                    break;
                }

                case "content_block_start":
                {
                    var index = node?["index"]?.GetValue<int>() ?? 0;
                    var block = node?["content_block"];
                    if (block?["type"]?.GetValue<string>() == "tool_use")
                    {
                        var id = block["id"]?.GetValue<string>() ?? $"toolu_{index}";
                        var name = block["name"]?.GetValue<string>() ?? "";
                        _toolBlocks[index] = (id, name, new StringBuilder());
                        yield return new LlmToolCallStarted(id, name);
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var index = node?["index"]?.GetValue<int>() ?? 0;
                    var delta = node?["delta"];
                    switch (delta?["type"]?.GetValue<string>())
                    {
                        case "text_delta" when delta["text"]?.GetValue<string>() is { Length: > 0 } text:
                            _text.Append(text);
                            yield return new LlmTextDelta(text);
                            break;
                        case "thinking_delta" when delta["thinking"]?.GetValue<string>() is { Length: > 0 } thought:
                            _thinking.Append(thought);
                            yield return new LlmThinkingDelta(thought);
                            break;
                        case "input_json_delta" when delta["partial_json"]?.GetValue<string>() is { } partial:
                            if (_toolBlocks.TryGetValue(index, out var entry))
                                entry.Args.Append(partial);
                            break;
                    }
                    break;
                }

                case "message_delta":
                {
                    _finishReason = node?["delta"]?["stop_reason"]?.GetValue<string>() ?? _finishReason;
                    var deltaUsage = node?["usage"];
                    _outputTokens = deltaUsage?["output_tokens"]?.GetValue<long>() ?? _outputTokens;
                    _cachedInputTokens = deltaUsage?["cache_read_input_tokens"]?.GetValue<long>() ?? _cachedInputTokens;
                    _cacheCreationInputTokens = deltaUsage?["cache_creation_input_tokens"]?.GetValue<long>() ?? _cacheCreationInputTokens;
                    break;
                }
            }
        }

        public LlmCompletion Build() => new()
        {
            Text = _text.ToString(),
            Thinking = _thinking.ToString(),
            ToolCalls = _toolBlocks
                .OrderBy(kv => kv.Key)
                .Select(kv => new ToolCallRequest(
                    kv.Value.Id,
                    kv.Value.Name,
                    kv.Value.Args.Length == 0 ? "{}" : kv.Value.Args.ToString()))
                .ToList(),
            Usage = new TokenUsage(_inputTokens, _outputTokens)
            {
                // Anthropic reports uncached, cache-read and cache-write input separately.
                TotalInputTokens = _inputTokens + _cachedInputTokens + _cacheCreationInputTokens,
                CachedInputTokens = _cachedInputTokens,
                CacheCreationInputTokens = _cacheCreationInputTokens,
            },
            FinishReason = _finishReason,
        };
    }
}
