using System.Net.Http;

namespace CluadeX.Services;

public class WebFetchService
{
    private readonly HttpClient _httpClient;

    public WebFetchService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX/2.0");
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

    private static string StripHtml(string html)
    {
        // Remove script and style blocks
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Remove tags
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        // Collapse whitespace
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ");
        // Decode entities
        html = System.Net.WebUtility.HtmlDecode(html);
        return html.Trim();
    }
}
