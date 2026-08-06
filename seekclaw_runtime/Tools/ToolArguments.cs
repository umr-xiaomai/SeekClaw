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
