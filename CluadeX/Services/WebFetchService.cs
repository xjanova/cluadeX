using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

public class WebFetchService
{
    /// <summary>Redirects followed for one fetch before it stops.</summary>
    public const int MaxRedirects = 5;

    /// <summary>Characters of page text handed back; the rest is cut with a note.</summary>
    public const int MaxContentChars = 100_000;

    /// <summary>
    /// Raw bytes read from one response body, at most. HTML is stripped to text AFTER the read, so this is
    /// well above <see cref="MaxContentChars"/> (markup and scripts are most of a page's bytes); anything
    /// past it is never downloaded. Unbounded, a server that never ends its body was read into memory forever.
    /// </summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The time one fetch may spend on the network — every hop's connect, headers AND body together, as the
    /// 30 s HttpClient.Timeout once covered the whole (auto-redirected, buffered) fetch. The client's own
    /// timeout only covers the headers when the body is streamed (ResponseHeadersRead), so a server that sent
    /// its headers and then trickled the body held the fetch — and the agent turn — open indefinitely; and a
    /// budget per hop let a chain of slow redirects take (MaxRedirects + 1) × 30 s. Time spent in the redirect
    /// permission prompt is not counted: a user reading it does not make the site slow.
    /// Settable so the contract harness can prove it without waiting 30 s.
    /// </summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly DebugLogService? _debugLog;

    public WebFetchService(DebugLogService? debugLog = null)
    {
        _debugLog = debugLog;
        // Redirects are followed by hand (FetchAsync) so every hop can be checked. Followed automatically, a
        // host the user allowed could bounce the request — and whatever the model put in its URL — to any
        // other host, unseen.
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// GET <paramref name="url"/>, following up to <see cref="MaxRedirects"/> redirects. Each hop is put to
    /// <paramref name="allowRedirect"/> (from, to) first — the caller's permission check; without one only a
    /// redirect within the same site is followed. A hop that is not followed ends the fetch with its target
    /// named (<see cref="WebFetchPage.RedirectTarget"/>) and nothing sent to it. Never throws for an HTTP
    /// status or a network failure — the outcome says what happened, details go to the Debug Log; only a
    /// cancellation by <paramref name="ct"/> propagates.
    /// </summary>
    public async Task<WebFetchPage> FetchAsync(string url, Func<Uri, Uri, Task<bool>>? allowRedirect = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new WebFetchPage { Outcome = WebFetchOutcome.InvalidUrl, Url = null };

        var current = uri;
        // One budget for the whole fetch (see FetchTimeout): each hop gets what the hops before it left.
        var spent = TimeSpan.Zero;
        CancellationTokenSource? hop = null;
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                var left = FetchTimeout - spent;
                if (left <= TimeSpan.Zero)
                {
                    _debugLog?.Warn("WebFetch", $"Timed out fetching {current}: the {FetchTimeout.TotalSeconds:0} s budget was used by the redirects before it");
                    return new WebFetchPage { Outcome = WebFetchOutcome.TimedOut, Url = current };
                }
                hop?.Dispose();
                hop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hop.CancelAfter(left);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var response = await _httpClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, hop.Token);
                int status = (int)response.StatusCode;

                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                        return new WebFetchPage { Outcome = WebFetchOutcome.HttpError, Url = current, StatusCode = status, Reason = response.ReasonPhrase ?? "" };
                    Uri next;
                    try { next = location.IsAbsoluteUri ? location : new Uri(current, location); }
                    catch (UriFormatException) { return new WebFetchPage { Outcome = WebFetchOutcome.HttpError, Url = current, StatusCode = status, Reason = response.ReasonPhrase ?? "" }; }

                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        return new WebFetchPage { Outcome = WebFetchOutcome.RedirectNotWeb, Url = current, RedirectTarget = next };
                    if (redirects >= MaxRedirects)
                        return new WebFetchPage { Outcome = WebFetchOutcome.TooManyRedirects, Url = current, RedirectTarget = next };

                    spent += clock.Elapsed;   // before the prompt: the user's time is not the site's
                    bool follow = allowRedirect != null ? await allowRedirect(current, next) : IsSameSite(current, next);
                    if (!follow)
                        return new WebFetchPage { Outcome = WebFetchOutcome.RedirectNotFollowed, Url = current, RedirectTarget = next };
                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return new WebFetchPage { Outcome = WebFetchOutcome.HttpError, Url = current, StatusCode = status, Reason = response.ReasonPhrase ?? "" };

                var (content, cutAtBytes) = await ReadBodyAsync(response.Content, MaxBodyBytes, hop.Token);

                // If HTML, try to strip tags for readability
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.Contains("html"))
                    content = StripHtml(content);

                // Limit content size
                if (content.Length > MaxContentChars)
                    content = content[..MaxContentChars] + "\n\n[Content truncated at 100KB]";
                else if (cutAtBytes)
                    content += $"\n\n[Content truncated: the page is larger than {MaxBodyBytes / (1024 * 1024)} MB]";

                return new WebFetchPage { Outcome = WebFetchOutcome.Ok, Url = current, StatusCode = status, Content = content };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // The hop's budget ran out — usually an OperationCanceledException, but an aborted body read can
        // surface as an IOException / HttpRequestException instead.
        catch (Exception ex) when (ex is OperationCanceledException || hop?.IsCancellationRequested == true)
        {
            _debugLog?.Warn("WebFetch", $"Timed out fetching {current}", ex);
            return new WebFetchPage { Outcome = WebFetchOutcome.TimedOut, Url = current };
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("WebFetch", $"Fetching {current} failed", ex);
            return new WebFetchPage { Outcome = WebFetchOutcome.Failed, Url = current };
        }
        finally
        {
            hop?.Dispose();
        }
    }

    /// <summary>
    /// The body as text, reading at most <paramref name="maxBytes"/> bytes — the rest is never downloaded
    /// (disposing the response closes the connection). Decoded like ReadAsStringAsync: a byte-order mark
    /// wins, else the Content-Type charset, else UTF-8 (an unknown charset falls back to UTF-8 instead of
    /// failing the fetch). Cut = there was more than <paramref name="maxBytes"/>.
    /// </summary>
    private static async Task<(string Text, bool Cut)> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        bool cut = false;
        await using (var stream = await content.ReadAsStreamAsync(ct))
        {
            var chunk = new byte[16 * 1024];
            while (true)
            {
                int n = await stream.ReadAsync(chunk.AsMemory(), ct);
                if (n == 0) break;
                int take = (int)Math.Min(n, maxBytes - buffer.Length);
                buffer.Write(chunk, 0, take);
                if (take < n) { cut = true; break; }
            }
        }

        Encoding encoding = Encoding.UTF8;
        string? charset = content.Headers.ContentType?.CharSet?.Trim().Trim('"', '\'');
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* unknown charset — UTF-8 */ }
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(ct), cut);
    }

    /// <summary>Same host, and the same port — or default ports on both sides, so http → https on one host
    /// counts as the same site.</summary>
    public static bool IsSameSite(Uri from, Uri to)
        => string.Equals(from.IdnHost, to.IdnHost, StringComparison.OrdinalIgnoreCase)
           && (from.Port == to.Port || (from.IsDefaultPort && to.IsDefaultPort));

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

    /// <summary>
    /// Page text from HTML: script / style blocks dropped, tags turned into spaces, whitespace collapsed,
    /// entities decoded — the same result the regexes <c>&lt;(script|style)[^&gt;]*&gt;[\s\S]*?&lt;/\1&gt;</c> → "" and
    /// <c>&lt;[^&gt;]+&gt;</c> → " " gave, in time linear in the page. Those regexes rescanned to the end of the page
    /// from every opening tag that has no closing one: a hostile 256 KB page took 39 s of CPU (that nothing can
    /// cancel), a 2 MB one — the body cap — would take about 40 minutes.
    /// </summary>
    internal static string StripHtml(string html)
    {
        // <script …>…</script>, <style …>…</style>: the opening tag runs to the first '>', the block to the first
        // closing tag of the same name; an opening tag with no closing tag stays (and goes as a tag below).
        var sb = new StringBuilder(html.Length);
        var tagEnd = new NextIndex(html, ">");
        var scriptEnd = new NextIndex(html, "</script>");
        var styleEnd = new NextIndex(html, "</style>");
        int copied = 0;
        for (int i = html.IndexOf('<'); i >= 0; i = html.IndexOf('<', i + 1))
        {
            int name = NameAt(html, i + 1, "script") ? 6 : NameAt(html, i + 1, "style") ? 5 : 0;
            if (name == 0) continue;
            int open = tagEnd.From(i + 1 + name);
            if (open < 0) break;   // no '>' left: no block can start from here on
            int close = (name == 6 ? scriptEnd : styleEnd).From(open + 1);
            if (close < 0) continue;
            sb.Append(html, copied, i - copied);
            copied = close + name + 3;   // past "</" + name + ">"
            i = copied - 1;
        }
        string text = sb.Append(html, copied, html.Length - copied).ToString();

        // Tags → a space: '<', at least one character that is not '>', then '>'.
        sb.Clear();
        tagEnd = new NextIndex(text, ">");
        copied = 0;
        for (int i = text.IndexOf('<'); i >= 0; i = text.IndexOf('<', i + 1))
        {
            if (i + 1 < text.Length && text[i + 1] == '>') continue;   // "<>" is not a tag
            int close = tagEnd.From(i + 1);
            if (close < 0) break;   // no '>' left: no tag from here on
            sb.Append(text, copied, i - copied).Append(' ');
            copied = close + 1;
            i = close;
        }
        text = sb.Append(text, copied, text.Length - copied).ToString();

        text = Regex.Replace(text, @"\s+", " ");   // collapse whitespace (linear)
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static bool NameAt(string s, int at, string name)
        => at + name.Length <= s.Length && string.Compare(s, at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>IndexOf (case-insensitive) that remembers: asked again from a later position it reuses its last
    /// answer while that still holds — asked from left to right it reads the text once.</summary>
    private sealed class NextIndex(string text, string value)
    {
        private int _from = int.MaxValue, _at = -1;
        public int From(int from)
        {
            if (from < _from || (_at >= 0 && _at < from))
            {
                _from = from;
                _at = from >= text.Length ? -1 : text.IndexOf(value, from, StringComparison.OrdinalIgnoreCase);
            }
            return _at;
        }
    }
}

/// <summary>How a <see cref="WebFetchService.FetchAsync"/> ended.</summary>
public enum WebFetchOutcome
{
    Ok,
    InvalidUrl,            // not an absolute http(s) URL
    HttpError,             // the server answered with an error status (or a redirect without a usable Location)
    RedirectNotFollowed,   // the redirect check said no — nothing was sent to the target
    RedirectNotWeb,        // a redirect to a non-http(s) address (file:, ftp:, …)
    TooManyRedirects,
    TimedOut,
    Failed,                // DNS, connection, TLS … — details in the Debug Log
}

/// <summary>The result of one fetch: the page, or why there is none.</summary>
public sealed class WebFetchPage
{
    public WebFetchOutcome Outcome { get; init; }
    /// <summary>The last URL requested — the page's own URL when <see cref="Outcome"/> is Ok.</summary>
    public Uri? Url { get; init; }
    /// <summary>Where a redirect that was not followed pointed.</summary>
    public Uri? RedirectTarget { get; init; }
    public int StatusCode { get; init; }
    public string Reason { get; init; } = "";
    public string Content { get; init; } = "";
}
