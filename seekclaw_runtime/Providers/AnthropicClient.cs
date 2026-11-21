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
        ApplyHeaders(message, request);

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
            {
                var status = (int)response.StatusCode;
                var body = "";
                try { body = await response.Content.ReadAsStringAsync(headerCts.Token).ConfigureAwait(false); } catch { }
                var detail = OpenAiCompatibleClient.ExtractErrorMessage(body);
                throw new LlmException(
                    $"{request.Provider.Id} returned HTTP {status}: {detail}",
                    status,
                    retryable: status is 408 or 409 or 429 or 529 or >= 500)
                {
                    RetryAfter = DeepSeekOptimizationPolicy.ExtractRetryAfter(response),
                    ErrorCode = DeepSeekOptimizationPolicy.ClassifyErrorCode(status, detail),
                };
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
                try { body = await response.Content.ReadAsStringAsync(headerCts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    throw LlmTransportErrors.ReadingResponse(request.Provider.Id, ex);
                }
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
                    ParseCompletion(node, request.Provider.Id), request);
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
            var sawMessageStop = false;
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw LlmTransportErrors.ReadingResponse(request.Provider.Id, ex);
            }
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
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                    {
                        throw LlmTransportErrors.ReadingResponse(request.Provider.Id, ex);
                    }
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
                    JsonNode? node;
                    try { node = JsonNode.Parse(sse.Data); }
                    catch (JsonException ex)
                    {
                        throw new LlmException(
                            $"{request.Provider.Id} returned a malformed streaming event.",
                            retryable: true,
                            inner: ex);
                    }

                    var type = node?["type"]?.GetValue<string>() ?? sse.Event ?? "";
                    if (type == "error")
                        throw new LlmException(
                            $"{request.Provider.Id} stream error: {node?["error"]?["message"]?.GetValue<string>() ?? "unknown"}");

                    foreach (var evt in acc.Consume(type, node)) yield return evt;
                    if (type == "message_stop")
                    {
                        sawMessageStop = true;
                        break;
                    }
                }
            }

            if (!sawMessageStop)
            {
                throw new LlmException(
                    $"{request.Provider.Id} stream ended before message_stop.",
                    retryable: true);
            }

            yield return new LlmCompleted(DeepSeekOptimizationPolicy.ValidateCompletion(acc.Build(), request));
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
        var thinkingSignature = new StringBuilder();
        var usedIds = new HashSet<string>(
            content.OfType<JsonObject>()
                .Select(c => c["id"]?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))!,
            StringComparer.Ordinal);
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
                    thinkingSignature.Append(item["signature"]?.GetValue<string>() ?? "");
                    break;
                case "tool_use":
                    var name = item["name"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var rawId = item["id"]?.GetValue<string>();
                        string id;
                        if (!string.IsNullOrWhiteSpace(rawId))
                        {
                            id = rawId;
                        }
                        else
                        {
                            var n = calls.Count;
                            do { id = $"toolu_{n++}"; } while (!usedIds.Add(id));
                        }
                        calls.Add(new ToolCallRequest(
                            id,
                            name,
                            item["input"]?.ToJsonString() ?? "{}"));
                    }
                    break;
            }
        }

        var usage = obj["usage"] as JsonObject;
        var inputTokens = usage?["input_tokens"]?.GetValue<long>() ?? 0;
        var outputTokens = usage?["output_tokens"]?.GetValue<long>() ?? 0;
        var cachedTokens = usage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
        var cacheCreationTokens = usage?["cache_creation_input_tokens"]?.GetValue<long>() ?? 0;
        return new LlmCompletion
        {
            Text = text.ToString(),
            Thinking = thinking.ToString(),
            ThinkingSignature = thinkingSignature.Length == 0 ? null : thinkingSignature.ToString(),
            ToolCalls = calls,
            FinishReason = obj["stop_reason"]?.GetValue<string>() ?? "",
            Usage = new TokenUsage(inputTokens, outputTokens)
            {
                TotalInputTokens = inputTokens + cachedTokens + cacheCreationTokens,
                CachedInputTokens = cachedTokens,
                CacheCreationInputTokens = cacheCreationTokens,
            },
        };
    }

    private static void ApplyHeaders(HttpRequestMessage message, LlmRequest request)
    {
        var provider = request.Provider;
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            message.Headers.TryAddWithoutValidation("x-api-key", key);
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (provider.Headers is not null)
            foreach (var (name, value) in provider.Headers)
                message.Headers.TryAddWithoutValidation(name, value);
        DeepSeekOptimizationPolicy.ApplyHeaders(message, request);
    }

    internal static JsonObject BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();
        var optimizeDeepSeek = DeepSeekOptimizationPolicy.Applies(request);

        for (var index = 0; index < request.Messages.Count; index++)
        {
            var msg = request.Messages[index];
            switch (msg.Role)
            {
                case ChatRole.User:
                    if (optimizeDeepSeek)
                    {
                        var blocks = UserBlocks(msg);
                        if (blocks.Count > 0)
                            AddOrMergeUserMessage(messages, blocks);
                    }
                    else
                    {
                        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = UserContent(msg) });
                    }
                    break;

                case ChatRole.Assistant:
                    var content = new JsonArray();
                    if (DeepSeekOptimizationPolicy.ShouldPassBackAnthropicThinking(request, msg))
                    {
                        var thinkingBlock = new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = msg.Thinking,
                        };
                        if (DeepSeekOptimizationPolicy.ShouldKeepThinkingSignature(request, msg))
                            thinkingBlock["signature"] = msg.ThinkingSignature;
                        content.Add((JsonNode)thinkingBlock);
                    }
                    var historyText = msg.AssistantHistoryText();
                    if (!string.IsNullOrEmpty(historyText))
                        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = historyText });
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
                        JsonNode contentNode;
                        if (result.Images is { Count: > 0 })
                        {
                            var contentBlocks = new JsonArray();
                            if (!optimizeDeepSeek || !string.IsNullOrEmpty(result.Text))
                                contentBlocks.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = result.Text });
                            foreach (var image in result.Images)
                            {
                                contentBlocks.Add((JsonNode)new JsonObject
                                {
                                    ["type"] = "image",
                                    ["source"] = new JsonObject
                                    {
                                        ["type"] = "base64",
                                        ["media_type"] = image.MediaType,
                                        ["data"] = image.Data,
                                    },
                                });
                            }
                            contentNode = contentBlocks;
                        }
                        else if (optimizeDeepSeek && string.IsNullOrEmpty(result.Text))
                        {
                            contentNode = new JsonArray();
                        }
                        else
                        {
                            contentNode = JsonValue.Create(result.Text)!;
                        }

                        toolResults.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.ToolCallId,
                            ["content"] = contentNode,
                            ["is_error"] = !result.ToolSuccess,
                        });
                        index++;
                    }
                    index--;
                    if (optimizeDeepSeek)
                    {
                        AddOrMergeUserMessage(messages, toolResults);
                    }
                    else
                    {
                        messages.Add((JsonNode)new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = toolResults,
                        });
                    }
                    break;
            }
        }

        // Checkpoint the multi-turn conversation history prefix with cache_control so
        // follow-up turns achieve 98-99% prompt cache hit rates on Anthropic.
        if (request.Provider.PromptCaching && messages.Count > 0)
        {
            var targetIndex = messages.Count > 1 ? messages.Count - 2 : messages.Count - 1;
            ApplyMessageCacheControl(messages[targetIndex]);
        }

        var body = new JsonObject
        {
            ["model"] = request.Model.Id,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens ?? request.Model.EffectiveMaxOutput,
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

        if (optimizeDeepSeek)
        {
            if (DeepSeekOptimizationPolicy.ThinkingWire(request) is { } thinkingWire)
                body["thinking"] = thinkingWire;
            if (DeepSeekOptimizationPolicy.AnthropicOutputConfigWire(request) is { } outputConfig)
                body["output_config"] = outputConfig;
            if (request.Temperature is { } temp)
                body["temperature"] = temp;
        }
        else
        {
            var thinkingBudget = request.EnableThinking
                && (request.Model.Capabilities.Thinking || request.Model.Capabilities.SupportedReasoningLevels is not null)
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
        }

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

    private static void ApplyMessageCacheControl(JsonNode? messageNode)
    {
        if (messageNode is not JsonObject messageObj) return;

        var contentNode = messageObj["content"];
        if (contentNode is JsonValue value && value.TryGetValue<string>(out var text))
        {
            messageObj["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = CacheControl(),
            });
        }
        else if (contentNode is JsonArray contentArray && contentArray.Count > 0)
        {
            if (contentArray[^1] is JsonObject lastBlock)
            {
                lastBlock["cache_control"] = CacheControl();
            }
        }
    }

    private static JsonArray UserBlocks(ChatMessage message)
    {
        var content = new JsonArray();
        if (message.Images is { Count: > 0 })
        {
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
        }
        if (!string.IsNullOrEmpty(message.Text))
            content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = message.Text });
        return content;
    }

    private static void AddOrMergeUserMessage(JsonArray messages, JsonArray newBlocks)
    {
        if (newBlocks.Count == 0) return;

        if (messages.Count > 0
            && messages[^1] is JsonObject prev
            && string.Equals(prev["role"]?.GetValue<string>(), "user", StringComparison.Ordinal))
        {
            var combined = new List<JsonNode>();
            if (prev["content"] is JsonValue prevVal && prevVal.TryGetValue<string>(out var prevText))
            {
                if (!string.IsNullOrEmpty(prevText))
                    combined.Add(new JsonObject { ["type"] = "text", ["text"] = prevText });
            }
            else if (prev["content"] is JsonArray prevArr)
            {
                foreach (var node in prevArr)
                {
                    if (node is not null) combined.Add(node.DeepClone());
                }
            }

            foreach (var node in newBlocks)
            {
                if (node is not null) combined.Add(node.DeepClone());
            }

            var ordered = new JsonArray();
            foreach (var node in combined.Where(n => n["type"]?.GetValue<string>() == "tool_result"))
                ordered.Add(node);
            foreach (var node in combined.Where(n => n["type"]?.GetValue<string>() != "tool_result"))
                ordered.Add(node);
            prev["content"] = ordered;
            return;
        }

        if (newBlocks.Count == 1
            && newBlocks[0] is JsonObject single
            && single["type"]?.GetValue<string>() == "text"
            && single["text"] is JsonValue textVal
            && textVal.TryGetValue<string>(out var singleText))
        {
            messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = singleText });
            return;
        }

        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = newBlocks });
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
        try { return JsonNode.Parse(json) is JsonObject obj ? obj : new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static JsonObject CacheControl() => new() { ["type"] = "ephemeral" };

    private sealed class Accumulator
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private readonly StringBuilder _thinkingSignature = new();
        private readonly Dictionary<int, (string Id, string Name, StringBuilder Args)> _toolBlocks = [];
        private readonly HashSet<string> _usedToolIds = new(StringComparer.Ordinal);
        private long _inputTokens;
        private long _cachedInputTokens;
        private long _cacheCreationInputTokens;
        private long _outputTokens;
        private string _finishReason = "";

        private string AllocateToolId(int startIndex)
        {
            var n = startIndex;
            string candidate;
            do { candidate = $"toolu_{n++}"; } while (!_usedToolIds.Add(candidate));
            return candidate;
        }

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
                    var blockType = block?["type"]?.GetValue<string>();
                    if (blockType == "thinking")
                    {
                        _thinkingSignature.Append(block?["signature"]?.GetValue<string>() ?? "");
                    }
                    else if (blockType == "tool_use")
                    {
                        var rawId = block?["id"]?.GetValue<string>();
                        var id = !string.IsNullOrWhiteSpace(rawId) ? rawId : AllocateToolId(index);
                        _usedToolIds.Add(id);
                        var name = block?["name"]?.GetValue<string>() ?? "";
                        _toolBlocks[index] = (id, name, new StringBuilder());
                        if (!string.IsNullOrWhiteSpace(name))
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
                        case "signature_delta" when delta["signature"]?.GetValue<string>() is { } signature:
                            _thinkingSignature.Append(signature);
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
            ThinkingSignature = _thinkingSignature.Length == 0 ? null : _thinkingSignature.ToString(),
            ToolCalls = _toolBlocks
                .OrderBy(kv => kv.Key)
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Name))
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
