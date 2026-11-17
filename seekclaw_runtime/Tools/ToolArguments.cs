using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Tools;

/// <summary>
/// Normalizes model-produced tool call arguments. Some models emit arguments that are
/// truncated (token cap) or carry trailing text / syntax errors. Those must be repaired
/// before the JSON is sent back to a provider: strict local servers reject the whole
/// request with HTTP 500 when a tool call's arguments string is not valid JSON.
/// </summary>
internal static class ToolArguments
{
    /// <summary>Returns a valid JSON object string for the raw arguments ("{}" when unrecoverable).</summary>
    public static string Sanitize(string raw)
    {
        var (_, json) = Parse(raw);
        return json;
    }

    /// <summary>
    /// Parses raw tool arguments, attempting a best-effort repair for truncated or
    /// trailing-garbage JSON. <c>Obj</c> is null when the arguments cannot be recovered.
    /// </summary>
    public static (JsonObject? Obj, string Json) Parse(string raw)
    {
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0) return (new JsonObject(), "{}");
        try
        {
            if (JsonNode.Parse(trimmed) is JsonObject valid) return (valid, trimmed);
        }
        catch (JsonException) { }

        var start = trimmed.IndexOf('{');
        if (start < 0) return (null, "{}");

        var scan = Scan(trimmed, start);
        if (scan.Balanced is not null)
        {
            try
            {
                if (JsonNode.Parse(scan.Balanced) is JsonObject repaired)
                    return (repaired, scan.Balanced);
            }
            catch (JsonException) { }
        }

        // Unterminated object (cut off at the token cap): try closing it. A trailing
        // comma, an open string, or missing braces are all handled by trying a few
        // sensible suffixes in order.
        var truncated = trimmed[start..];
        var closeBraces = Math.Max(scan.OpenDepth, 1);
        var suffixes = new[]
        {
            new string('}', closeBraces),
            new string('}', closeBraces + 1),
            "\"" + new string('}', closeBraces),
            "\"" + new string('}', closeBraces + 1),
            "}" + new string('}', closeBraces),
        };
        foreach (var suffix in suffixes)
        {
            try
            {
                if (JsonNode.Parse(truncated + suffix) is JsonObject repaired)
                    return (repaired, truncated + suffix);
            }
            catch (JsonException) { }
        }

        return (null, "{}");
    }

    /// <summary>
    /// Returns true when <paramref name="raw"/> starts with a complete, balanced JSON object.
    /// Used to detect relay-station <c>tool_calls</c> index collisions during streaming.
    /// </summary>
    public static bool StartsCompleteJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var start = raw.IndexOf('{');
        if (start < 0) return false;
        return Scan(raw, start).Balanced is not null;
    }

    /// <summary>
    /// Splits back-to-back JSON objects (such as <c>{"a":1}{"b":2}</c>) emitted by gateways
    /// that merge multiple parallel tool calls onto a single <c>index</c>.
    /// Returns <c>[raw]</c> unchanged unless 2 or more valid JSON objects are found.
    /// </summary>
    public static IReadOnlyList<string> SplitConcatenatedJsonObjects(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [raw ?? ""];

        var objects = new List<string>();
        var pos = 0;
        while (pos < raw.Length)
        {
            while (pos < raw.Length && char.IsWhiteSpace(raw[pos])) pos++;
            if (pos >= raw.Length) break;
            if (raw[pos] != '{') return [raw];

            var scan = Scan(raw, pos);
            if (scan.Balanced is null) return [raw];

            try
            {
                if (JsonNode.Parse(scan.Balanced) is not JsonObject) return [raw];
            }
            catch (JsonException)
            {
                return [raw];
            }

            objects.Add(scan.Balanced);
            pos += scan.Balanced.Length;
        }

        return objects.Count > 1 ? objects : [raw];
    }

    private static (string? Balanced, int OpenDepth) Scan(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return (text[start..(i + 1)], 0);
                    break;
            }
        }
        return (null, Math.Max(depth, 0));
    }
}
