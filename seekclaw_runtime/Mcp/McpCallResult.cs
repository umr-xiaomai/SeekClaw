using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Mcp;

/// <summary>Lossless MCP result envelope, plus provider-neutral text and vision projections.</summary>
public sealed record McpCallResult(bool Success, JsonArray Content, JsonNode? StructuredContent, JsonObject? Metadata)
{
    public static McpCallResult Parse(JsonObject result)
    {
        if (result.ToJsonString().Length > IO.ProtocolText.MaximumMessageChars)
            throw new McpException("MCP result exceeds the 16 MiB text limit.");
        return new(!(result["isError"]?.GetValue<bool>() ?? false), result["content"]?.DeepClone() as JsonArray ?? [],
            result["structuredContent"]?.DeepClone(), result["_meta"]?.DeepClone() as JsonObject);
    }

    public string Text
    {
        get
        {
            var text = new StringBuilder();
            foreach (var item in Content.OfType<JsonObject>())
            {
                switch (item["type"]?.GetValue<string>())
                {
                    case "text": text.AppendLine(item["text"]?.GetValue<string>()); break;
                    case "resource_link":
                        text.AppendLine($"Resource: {item["name"]} ({item["uri"]}) {item["description"]}"); break;
                    case "resource":
                        var resource = item["resource"];
                        text.AppendLine($"Resource: {resource?["uri"]} ({resource?["mimeType"]})");
                        if (resource?["text"] is { } body) text.AppendLine(body.GetValue<string>());
                        else text.AppendLine("[Binary resource retained in tool content]");
                        break;
                    case "image": text.AppendLine($"[Image: {item["mimeType"]}]"); break;
                    case "audio": text.AppendLine($"[Audio: {item["mimeType"]}; retained in tool content]"); break;
                    default: text.AppendLine(item.ToJsonString()); break;
                }
            }
            if (StructuredContent is not null) text.AppendLine(StructuredContent.ToJsonString());
            return text.ToString().TrimEnd();
        }
    }

    public IReadOnlyList<ChatImageAttachment> Images()
    {
        var images = new List<ChatImageAttachment>();
        long totalBytes = 0;
        foreach (var item in Content.OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "image"))
        {
            var data = item["data"]?.GetValue<string>() ?? "";
            var media = item["mimeType"]?.GetValue<string>() ?? "application/octet-stream";
            if (media is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp") || data.Length > 14 * 1024 * 1024) continue;
            try
            {
                var bytes = Convert.FromBase64String(data);
                totalBytes += bytes.Length;
                if (totalBytes > 10 * 1024 * 1024 || images.Count >= 8) break;
                images.Add(new ChatImageAttachment(Guid.NewGuid().ToString("N"), "MCP image", media, data, bytes.Length));
            }
            catch (FormatException) { }
        }
        return images;
    }
}
