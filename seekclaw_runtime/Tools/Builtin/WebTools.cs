using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

internal sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>Searches the public web through Google, Bing, or Baidu and returns concise result links.</summary>
public sealed class WebSearchTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override bool RequiresWorkspace => false;
    public override bool RequiresNetwork => true;
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Lock GoogleProbeGate = new();
    private static DateTimeOffset _googleProbeAt = DateTimeOffset.MinValue;
    private static bool _googleProbeAvailable;

    public override string Name => "web_search";
    public override string StatusLabel => "Searching web";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.String("Search query"), true),
        ("engine", ToolSchema.String("Search engine: auto, google, bing, or baidu. Default auto."), false),
        ("max_results", ToolSchema.Integer("Maximum results to return, default 8"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("query is required.");

        var requestedEngine = (GetString(arguments, "engine") ?? "auto").Trim().ToLowerInvariant();
        var maxResults = Math.Clamp(GetInt(arguments, "max_results") ?? 8, 1, 20);
        var engines = ResolveEngineOrder(requestedEngine);
        if (engines.Count == 0)
            return ToolResult.Fail("engine must be one of: auto, google, bing, baidu.");

        var notes = new List<string>();
        foreach (var engine in engines)
        {
            ct.ThrowIfCancellationRequested();

            if (engine == "google" && !await IsGoogleAvailableAsync(ct).ConfigureAwait(false))
            {
                notes.Add("google: unavailable or blocked from this network");
                continue;
            }

            try
            {
                var results = await SearchAsync(engine, query, maxResults, ct).ConfigureAwait(false);
                if (results.Count == 0)
                {
                    notes.Add($"{engine}: no parseable results");
                    continue;
                }

                var output = FormatResults(engine, query, results, notes);
                return ToolResult.Ok(context.Truncate(output, "search results"), $"Found {results.Count} results via {engine}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                notes.Add($"{engine}: timed out");
            }
            catch (HttpRequestException ex)
            {
                notes.Add($"{engine}: {ex.Message}");
            }
            catch (Exception ex) when (ex is UriFormatException or RegexMatchTimeoutException)
            {
                notes.Add($"{engine}: {ex.Message}");
            }
        }

        var detail = notes.Count == 0 ? "No search engines were tried." : string.Join("\n", notes.Select(n => "- " + n));
        return ToolResult.Fail($"No web search results found for '{query}'.\n{detail}");
    }

    private static IReadOnlyList<string> ResolveEngineOrder(string requestedEngine) => requestedEngine switch
    {
        "auto" or "" => ["google", "bing", "baidu"],
        "google" => ["google", "bing", "baidu"],
        "bing" => ["bing", "baidu"],
        "baidu" => ["baidu", "bing"],
        _ => [],
    };

    private static async Task<bool> IsGoogleAvailableAsync(CancellationToken ct)
    {
        lock (GoogleProbeGate)
        {
            if (DateTimeOffset.UtcNow - _googleProbeAt < TimeSpan.FromMinutes(10))
                return _googleProbeAvailable;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var available = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.google.com/generate_204");
            AddBrowserHeaders(request);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            available = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (HttpRequestException) { }

        lock (GoogleProbeGate)
        {
            _googleProbeAt = DateTimeOffset.UtcNow;
            _googleProbeAvailable = available;
        }
        return available;
    }

    private static async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string engine, string query, int maxResults, CancellationToken ct)
    {
        var url = engine switch
        {
            "google" => $"https://www.google.com/search?q={Uri.EscapeDataString(query)}&num={maxResults}&hl=zh-CN",
            "bing" => $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={maxResults}&setlang=zh-CN",
            "baidu" => $"https://www.baidu.com/s?wd={Uri.EscapeDataString(query)}&rn={maxResults}",
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unsupported search engine."),
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(request);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return engine switch
        {
            "google" => ExtractGoogleResults(html, maxResults),
            "bing" => ExtractBingResults(html, maxResults),
            "baidu" => ExtractBaiduResults(html, maxResults),
            _ => [],
        };
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 SeekClaw/1.0");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
    }

    internal static IReadOnlyList<WebSearchResult> ExtractBingResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        foreach (Match match in Regex.Matches(html, @"<li[^>]*class=""[^""]*\bb_algo\b[^""]*""[^>]*>(?<block>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
        {
            var block = match.Groups["block"].Value;
            var link = Regex.Match(block, @"<h2[^>]*>\s*<a[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (!link.Success) continue;

            var snippet = Regex.Match(block, @"<p[^>]*>(?<snippet>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            AddResult(results, link.Groups["title"].Value, link.Groups["url"].Value, snippet.Groups["snippet"].Value, maxResults);
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    internal static IReadOnlyList<WebSearchResult> ExtractGoogleResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        foreach (Match match in Regex.Matches(html, @"<a[^>]*href=""(?<url>(?:/url\?q=|https?://)[^""]+)""[^>]*>\s*(?:<br\s*/?>)?\s*<h3[^>]*>(?<title>.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
        {
            var afterLink = html[Math.Min(match.Index + match.Length, html.Length)..];
            var snippet = Regex.Match(afterLink, @"<div[^>]*class=""[^""]*(?:VwiC3b|IsZvec|GI74Re)[^""]*""[^>]*>(?<snippet>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            AddResult(results, match.Groups["title"].Value, match.Groups["url"].Value, snippet.Groups["snippet"].Value, maxResults);
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    internal static IReadOnlyList<WebSearchResult> ExtractBaiduResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        foreach (Match match in Regex.Matches(html, @"<h3[^>]*>\s*<a[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>\s*</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
        {
            var afterLink = html[Math.Min(match.Index + match.Length, html.Length)..];
            var snippetEnd = afterLink.IndexOf("<h3", StringComparison.OrdinalIgnoreCase);
            var sliceLen = snippetEnd > 0 ? Math.Min(snippetEnd, 8000) : Math.Min(afterLink.Length, 8000);
            var snippetBlock = afterLink[..sliceLen];
            AddResult(results, match.Groups["title"].Value, match.Groups["url"].Value, snippetBlock, maxResults);
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    private static void AddResult(List<WebSearchResult> results, string rawTitle, string rawUrl, string rawSnippet, int maxResults)
    {
        if (results.Count >= maxResults) return;

        var url = NormalizeUrl(rawUrl);
        if (url is null) return;
        if (results.Any(r => string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase))) return;

        var title = CleanInlineText(rawTitle);
        if (title.Length == 0) return;

        var snippet = CleanInlineText(rawSnippet);
        if (snippet.Length > 260) snippet = snippet[..260] + "...";
        results.Add(new WebSearchResult(title, url, snippet));
    }

    private static string? NormalizeUrl(string rawUrl)
    {
        var url = System.Net.WebUtility.HtmlDecode(rawUrl).Trim();
        if (url.StartsWith("/url?", StringComparison.OrdinalIgnoreCase))
        {
            var query = url[(url.IndexOf('?') + 1)..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split('=', 2);
                if (pieces.Length == 2 && pieces[0] == "q")
                {
                    url = Uri.UnescapeDataString(pieces[1].Replace('+', ' '));
                    break;
                }
            }
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }

    private static string CleanInlineText(string html)
    {
        var text = WebFetchTool.ExtractMainText(html).ReplaceLineEndings(" ");
        return Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }

    private static string FormatResults(string engine, string query, IReadOnlyList<WebSearchResult> results, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        sb.Append("Search query: ").AppendLine(query);
        sb.Append("Engine: ").AppendLine(engine);
        if (notes.Count > 0)
        {
            sb.AppendLine("Notes:");
            foreach (var note in notes) sb.Append(" - ").AppendLine(note);
        }
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            sb.Append(i + 1).Append(". ").AppendLine(result.Title);
            sb.Append("   URL: ").AppendLine(result.Url);
            if (result.Snippet.Length > 0)
                sb.Append("   Snippet: ").AppendLine(result.Snippet);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Fetches web page content, strips HTML markup, and returns readable markdown-like text.</summary>
public sealed class WebFetchTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;

    public override bool RequiresWorkspace => false;
    public override bool RequiresNetwork => true;
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
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

        if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri)
            || (currentUri.Scheme != Uri.UriSchemeHttp && currentUri.Scheme != Uri.UriSchemeHttps))
        {
            return ToolResult.Fail($"Invalid HTTP/HTTPS URL: {url}");
        }

        try
        {
            HttpResponseMessage? response = null;
            for (var redirects = 0; redirects <= MaxRedirects; redirects++)
            {
                if (!await IsSafePublicUriAsync(currentUri, ct).ConfigureAwait(false))
                    return ToolResult.Fail($"Blocked access to restricted host: {currentUri.Host}");

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SeekClaw/1.0");

                response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    response.Dispose();
                    response = null;
                    if (nextUri.Scheme != Uri.UriSchemeHttp && nextUri.Scheme != Uri.UriSchemeHttps)
                        return ToolResult.Fail($"Blocked redirect to unsupported scheme: {nextUri.Scheme}");
                    currentUri = nextUri;
                    continue;
                }
                break;
            }

            if (response is null)
                return ToolResult.Fail($"Too many redirects fetching {url}.");

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return ToolResult.Fail($"HTTP request to {currentUri} failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}).");

                var rawContent = await ReadLimitedStringAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
                var extractedText = ExtractMainText(rawContent);

                if (extractedText.Length > maxChars)
                    extractedText = extractedText[..maxChars] + $"\n… [Truncated remaining {extractedText.Length - maxChars} chars]";

                var summary = $"Fetched {extractedText.Length} characters from {currentUri}";
                return ToolResult.Ok(extractedText, summary);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

    private static async Task<bool> IsSafePublicUriAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.DnsSafeHost.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || uri.IsLoopback
            || host is "localhost" or "169.254.169.254" or "metadata.google.internal")
            return false;

        if (IPAddress.TryParse(host, out var literal))
            return !IsRestrictedAddress(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.All(addr => !IsRestrictedAddress(addr));
        }
        catch (SocketException)
        {
            return true; // Let HttpClient produce its normal DNS failure error.
        }
    }

    private static bool IsRestrictedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork && bytes.Length == 4)
            return bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254);

        return address.IsIPv6LinkLocal;
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var remaining = maxBytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            remaining -= read;
        }
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(content.Headers.ContentType?.CharSet))
        {
            try { encoding = Encoding.GetEncoding(content.Headers.ContentType.CharSet.Trim('"')); }
            catch (ArgumentException) { }
        }
        return encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    internal static string ExtractMainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "(empty response)";

        var timeout = TimeSpan.FromSeconds(2);
        // Strip HTML comments first (including unclosed trailing comments) so embedded
        // JSON/SSR payloads containing '>' do not leak past tag stripping.
        var clean = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline, timeout);
        clean = Regex.Replace(clean, @"<!--.*$", "", RegexOptions.Singleline, timeout);
        // Remove script, style, noscript, template, and svg tags (including unclosed trailing ones).
        clean = Regex.Replace(clean, @"<(script|style|noscript|template|svg)\b[^>]*?>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, timeout);
        clean = Regex.Replace(clean, @"<(script|style|noscript|template|svg)\b.*$", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, timeout);
        // Replace common block breaks with newlines
        clean = Regex.Replace(clean, @"</(p|h[1-6]|li|tr|div)>", "\n", RegexOptions.IgnoreCase, timeout);
        clean = Regex.Replace(clean, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase, timeout);
        // Strip remaining HTML tags and any trailing unclosed tag fragment (e.g. "<div cl").
        clean = Regex.Replace(clean, @"<[^>]+>", "", RegexOptions.None, timeout);
        clean = Regex.Replace(clean, @"<[^>]*$", "", RegexOptions.None, timeout);
        // Decode HTML entities
        clean = System.Net.WebUtility.HtmlDecode(clean);
        // Strip leaked Baidu/SSR JSON fragments if any escaped outside comments/tags.
        clean = Regex.Replace(clean, @"(?:""\}\]\s*,\s*""clamp""\s*:|\{""isPc""\s*:\s*true\s*,\s*""summarySpan""\s*:|""\]\s*,\s*""hasBottom""\s*:).*$", "", RegexOptions.Singleline, timeout);
        // Clean up excessive blank lines
        clean = Regex.Replace(clean, @"\n\s*\n+", "\n\n", RegexOptions.None, timeout);

        return clean.Trim();
    }
}
