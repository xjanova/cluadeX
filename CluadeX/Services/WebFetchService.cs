using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

public class WebFetchService
{
    private readonly HttpClient _httpClient;

    public WebFetchService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        // Validate URL
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid URL");
        if (uri.Scheme != "http" && uri.Scheme != "https")
            throw new ArgumentException("Only HTTP/HTTPS URLs are supported");

        var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();

        string content = await response.Content.ReadAsStringAsync(ct);

        // If HTML, try to strip tags for readability
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("html"))
            content = StripHtml(content);

        // Limit content size
        if (content.Length > 100_000)
            content = content[..100_000] + "\n\n[Content truncated at 100KB]";

        return content;
    }

    /// <summary>
    /// Search the web using DuckDuckGo HTML (no API key required).
    /// Returns a formatted list of search results with title, URL, and snippet.
    /// </summary>
    public async Task<string> SearchAsync(string query, int maxResults = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query cannot be empty");

        string url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "text/html");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        string html = await response.Content.ReadAsStringAsync(ct);
        return ParseDuckDuckGoResults(html, maxResults);
    }

    private static string ParseDuckDuckGoResults(string html, int maxResults)
    {
        var sb = new StringBuilder();
        int count = 0;

        // Parse result blocks: <a class="result__a" href="...">title</a>
        var resultPattern = new Regex(
            @"<a[^>]+class=""result__a""[^>]*href=""([^""]*?)""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Parse snippets: <a class="result__snippet" ...>snippet</a>
        var snippetPattern = new Regex(
            @"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var results = resultPattern.Matches(html);
        var snippets = snippetPattern.Matches(html);

        for (int i = 0; i < results.Count && count < maxResults; i++)
        {
            string rawUrl = results[i].Groups[1].Value;
            string title = StripHtmlTags(results[i].Groups[2].Value).Trim();

            // DuckDuckGo wraps URLs in a redirect; extract the real URL
            string actualUrl = rawUrl;
            if (rawUrl.Contains("uddg="))
            {
                var match = Regex.Match(rawUrl, @"uddg=([^&]+)");
                if (match.Success)
                    actualUrl = Uri.UnescapeDataString(match.Groups[1].Value);
            }

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(actualUrl))
                continue;

            string snippet = i < snippets.Count
                ? StripHtmlTags(snippets[i].Groups[1].Value).Trim()
                : "";

            count++;
            sb.AppendLine($"{count}. {title}");
            sb.AppendLine($"   URL: {actualUrl}");
            if (!string.IsNullOrEmpty(snippet))
                sb.AppendLine($"   {snippet}");
            sb.AppendLine();
        }

        if (count == 0)
            return "No search results found.";

        return $"Search results for \"{sb.ToString().Split('\n').FirstOrDefault()?.Trim() ?? "query"}\":\n\n{sb.ToString().TrimEnd()}";
    }

    private static string StripHtmlTags(string html)
    {
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        return html.Trim();
    }

    private static string StripHtml(string html)
    {
        // Remove script and style blocks
        html = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        // Remove tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Collapse whitespace
        html = Regex.Replace(html, @"\s+", " ");
        // Decode entities
        html = System.Net.WebUtility.HtmlDecode(html);
        return html.Trim();
    }
}
