using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Tools;

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
        var hasImages = request.Messages.Any(message => message.Images is { Count: > 0 });

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(message, request.Provider);

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.Provider.TimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(
                $"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.",
                statusCode: 408,
                retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Cannot reach {request.Provider.Id}: {ex.Message}", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await ApiError(response, request.Provider.Id, headerCts.Token).ConfigureAwait(false);

            // A few compatible gateways ignore stream=false and still return SSE. In that
            // case use the normal streaming parser instead of waiting for the event stream
            // to close and then trying to parse the whole payload as JSON.
            var isEventStream = string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase);
            if (hasImages && !isEventStream)
            {
                string body;
                try { body = await response.Content.ReadAsStringAsync(headerCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LlmException(
                        $"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.",
                        statusCode: 408,
                        retryable: true);
                }
                JsonNode? node;
                try { node = JsonNode.Parse(body); }
                catch (JsonException ex)
                {
                    throw new LlmException($"{request.Provider.Id} returned an invalid non-streaming response.", inner: ex);
                }

                var completion = DeepSeekOptimizationPolicy.ValidateCompletion(
                    ParseCompletion(node, request.Provider.Id, request), request);
                if (completion.Thinking.Length > 0) yield return new LlmThinkingDelta(completion.Thinking);
                foreach (var call in completion.ToolCalls)
                    yield return new LlmToolCallStarted(call.Id, call.Name);
                if (completion.Text.Length > 0) yield return new LlmTextDelta(completion.Text);
                yield return new LlmCompleted(completion);
                yield break;
            }

            var idleTimeout = DeepSeekOptimizationPolicy.Applies(request)
                ? DeepSeekOptimizationPolicy.GetStreamIdleTimeout(request.Provider)
                : TimeSpan.FromSeconds(Math.Max(1, request.Provider.TimeoutSeconds));

            var acc = new Accumulator();
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException(
                    $"Request to {request.Provider.Id} timed out after {request.Provider.TimeoutSeconds}s.",
                    statusCode: 408,
                    retryable: true);
            }

            await using (stream)
            await using (var events = SseReader.ReadAsync(stream, idleTimeout, ct).GetAsyncEnumerator())
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = await events.MoveNextAsync().ConfigureAwait(false); }
                    catch (TimeoutException ex)
                    {
                        throw new LlmException(
                            $"Request to {request.Provider.Id} timed out after {idleTimeout.TotalSeconds:0}s idle without activity.",
                            statusCode: 408,
                            retryable: true,
                            inner: ex);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new LlmException(
                            $"Request to {request.Provider.Id} timed out after {idleTimeout.TotalSeconds:0}s idle without activity.",
                            statusCode: 408,
                            retryable: true);
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

            yield return new LlmCompleted(DeepSeekOptimizationPolicy.ValidateCompletion(acc.Build(request), request));
        }
    }

    internal static LlmCompletion ParseCompletion(JsonNode? root, string providerId, LlmRequest? request = null)
    {
        // Some gateways answer with a JSON array or scalar (e.g. a raw error body); the
        // indexer below would otherwise throw "The node must be of type 'JsonObject'.".
        if (root is not JsonObject obj)
            throw new LlmException($"{providerId} returned an invalid non-streaming response.", retryable: false);
        if (obj["error"] is JsonNode error)
            throw new LlmException($"{providerId} returned an error: {ExtractErrorMessage(error.ToJsonString())}", retryable: false);

        var choice = obj["choices"]?.AsArray().FirstOrDefault();
        var message = choice?["message"];
        if (message is null)
            throw new LlmException($"{providerId} returned no completion choices.", retryable: false);

        var text = ExtractContentText(message["content"]);
        var thinking = message["reasoning_content"]?.GetValue<string>()
                       ?? message["reasoning"]?.GetValue<string>()
                       ?? "";
        var calls = new List<ToolCallRequest>();
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var item in toolCalls)
            {
                if (item is not JsonObject call) continue;
                var function = call["function"] as JsonObject;
                var name = function?["name"]?.GetValue<string>() ?? "";
                if (name.Length == 0) continue;
                calls.Add(new ToolCallRequest(
                    call["id"]?.GetValue<string>() ?? $"call_{calls.Count}",
                    name,
                    function?["arguments"]?.GetValue<string>() ?? "{}"));
            }
        }

        var usage = root?["usage"] as JsonObject;
        var inputTokens = usage?["prompt_tokens"]?.GetValue<long>() ?? 0;
        var outputTokens = usage?["completion_tokens"]?.GetValue<long>() ?? 0;
        var cachedTokens = usage?["prompt_cache_hit_tokens"]?.GetValue<long>()
                           ?? usage?["cached_tokens"]?.GetValue<long>()
                           ?? (usage?["prompt_tokens_details"] as JsonObject)?["cached_tokens"]?.GetValue<long>()
                           ?? 0;
        var disjointInputTokens = request is not null
            ? DeepSeekOptimizationPolicy.DisjointInputTokens(inputTokens, cachedTokens, request)
            : inputTokens;

        return new LlmCompletion
        {
            Text = text,
            Thinking = thinking,
            ToolCalls = calls,
            FinishReason = choice?["finish_reason"]?.GetValue<string>() ?? "",
            Usage = new TokenUsage(disjointInputTokens, outputTokens)
            {
                TotalInputTokens = inputTokens,
                CachedInputTokens = cachedTokens,
            },
        };
    }

    private static string ExtractContentText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is not JsonArray parts) return "";
        return string.Concat(parts
            .OfType<JsonObject>()
            .Where(part => part["type"]?.GetValue<string>() is "text" or "output_text")
            .Select(part => part["text"]?.GetValue<string>() ?? ""));
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
                    // DeepSeek reasoners continue a truncated thinking phase only when the
                    // previous reasoning_content is passed back; the opt-in policy drops it for
                    // pure text turns to avoid paying for ignored tokens.
                    if (DeepSeekOptimizationPolicy.ShouldPassBackReasoning(request, msg))
                        assistant["reasoning_content"] = msg.Thinking;
                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        var calls = new JsonArray();
                        foreach (var call in msg.ToolCalls)
                            calls.Add((JsonNode)new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = ToolArguments.Sanitize(call.ArgumentsJson) },
                            });
                        assistant["tool_calls"] = calls;
                    }
                    messages.Add((JsonNode)assistant);
                    break;

                case ChatRole.Tool:
                    if (msg.Images is { Count: > 0 })
                    {
                        var parts = new JsonArray
                        {
                            (JsonNode)new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = DeepSeekOptimizationPolicy.ToolResultContent(msg.Text, request),
                            }
                        };
                        foreach (var image in msg.Images)
                        {
                            parts.Add((JsonNode)new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject
                                {
                                    ["url"] = $"data:{image.MediaType};base64,{image.Data}",
                                    ["detail"] = "auto",
                                },
                            });
                        }
                        messages.Add((JsonNode)new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = msg.ToolCallId,
                            ["content"] = parts,
                        });
                    }
                    else
                    {
                        messages.Add((JsonNode)new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = msg.ToolCallId,
                            ["content"] = DeepSeekOptimizationPolicy.ToolResultContent(msg.Text, request),
                        });
                    }
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model.Id,
            ["messages"] = messages,
            // Vision gateways are not consistent about emitting a complete SSE response.
            // Use a normal JSON response for image turns; text-only turns retain streaming.
            ["stream"] = !request.Messages.Any(message => message.Images is { Count: > 0 }),
        };

        if (body["stream"]?.GetValue<bool>() == true)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };

        if (DeepSeekOptimizationPolicy.ThinkingWire(request) is { } thinkingWire)
            body["thinking"] = thinkingWire;

        // Reasoning models count thinking toward the completion budget; the modern
        // max_completion_tokens parameter covers it (max_tokens is rejected by OpenAI
        // reasoning endpoints). DeepSeek and other compatible gateways keep max_tokens.
        var useMaxCompletionTokens = request.Model.Capabilities.Reasoning
            && !ReasoningLevelAdapter.IsDeepSeek(request.Provider, request.Model);
        if (request.MaxTokens is { } maxTokens)
            body[useMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens;
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
            if (JsonNode.Parse(body) is not JsonObject root) return Truncate(body);
            // Gateways differ: {"error":{"message":"..."}} or {"error":"plain text"} or {"message":"..."}.
            if (root["error"] is JsonObject errorObject
                && errorObject["message"] is JsonValue errorMessage
                && errorMessage.TryGetValue<string>(out var nested))
                return nested;
            if (root["error"] is JsonValue flatError && flatError.TryGetValue<string>(out var flat))
                return flat;
            if (root["message"] is JsonValue message && message.TryGetValue<string>(out var topLevel))
                return topLevel;
        }
        catch (JsonException) { }
        return Truncate(body);
    }

    private static string Truncate(string body) => body.Length > 400 ? body[..400] : body;

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
            // Non-object SSE payloads (arrays, scalars, blank lines) are not valid
            // chat.completion.chunk frames; skip them instead of throwing.
            if (chunk is not JsonObject frame) yield break;
            if (frame["usage"] is JsonObject usage)
            {
                _inputTokens = usage["prompt_tokens"]?.GetValue<long>() ?? _inputTokens;
                _outputTokens = usage["completion_tokens"]?.GetValue<long>() ?? _outputTokens;
                _cachedInputTokens = usage["prompt_cache_hit_tokens"]?.GetValue<long>()
                    ?? usage["cached_tokens"]?.GetValue<long>()
                    ?? (usage["prompt_tokens_details"] as JsonObject)?["cached_tokens"]?.GetValue<long>()
                    ?? _cachedInputTokens;
            }

            if (frame["choices"] is not JsonArray { Count: > 0 } choices) yield break;
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

        public LlmCompletion Build(LlmRequest? request = null)
        {
            var disjointInputTokens = request is not null
                ? DeepSeekOptimizationPolicy.DisjointInputTokens(_inputTokens, _cachedInputTokens, request)
                : _inputTokens;

            return new LlmCompletion
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
                Usage = new TokenUsage(disjointInputTokens, _outputTokens)
                {
                    // OpenAI-compatible usage.prompt_tokens already includes cached tokens.
                    TotalInputTokens = _inputTokens,
                    CachedInputTokens = _cachedInputTokens,
                },
                FinishReason = _finishReason,
            };
        }
    }
}
