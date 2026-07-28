using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

/// <summary>Fetches web page content, strips HTML markup, and returns readable markdown-like text.</summary>
public sealed class WebFetchTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public override string Name => "web_fetch";
    public override string StatusLabel => "Fetching web page";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("url", ToolSchema.String("HTTP or HTTPS URL to fetch content from"), true),
        ("max_chars", ToolSchema.Integer("Maximum characters of text to extract, default 10000"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var url = GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("url is required.");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        var maxChars = Math.Clamp(GetInt(arguments, "max_chars") ?? 10_000, 500, 50_000);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SeekClaw/1.0");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"HTTP request to {url} failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}).");

            var rawContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var extractedText = ExtractMainText(rawContent);

            if (extractedText.Length > maxChars)
                extractedText = extractedText[..maxChars] + $"\n… [Truncated remaining {extractedText.Length - maxChars} chars]";

            var summary = $"Fetched {extractedText.Length} characters from {url}";
            return ToolResult.Ok(extractedText, summary);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail($"Network error fetching {url}: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ToolResult.Fail($"Request to {url} timed out.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to fetch {url}: {ex.Message}");
        }
    }

    private static string ExtractMainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "(empty response)";

        // Remove script and style tags completely
        var clean = Regex.Replace(html, @"<(script|style)[^>]*?>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Replace common block breaks with newlines
        clean = Regex.Replace(clean, @"</(p|h[1-6]|li|tr|div)>", "\n", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        // Strip remaining HTML tags
        clean = Regex.Replace(clean, @"<[^>]+>", "");
        // Decode HTML entities
        clean = System.Net.WebUtility.HtmlDecode(clean);
        // Clean up excessive blank lines
        clean = Regex.Replace(clean, @"\n\s*\n+", "\n\n");

        return clean.Trim();
    }
}
