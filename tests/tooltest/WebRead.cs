using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Browser;
using Microsoft.Extensions.DependencyInjection;

namespace ToolTest;

/// <summary>
/// web_fetch reads a long page in parts (offset / max_chars) instead of the first 4,000 characters only: the pure
/// slicer (no split emoji, no word cut in two, the next offset), then the real ExecuteWebFetchAsync against the
/// loopback mock's /long page — the envelope, the "read on" hint, part 2 cut from the same text without a second
/// download, a fresh first read, an offset past the end (localized), max_chars clamped, the cache's lifetime.
/// </summary>
internal static partial class Program
{
    static async Task WebRead(IServiceProvider provider, LocalizationService loc)
    {
        // ── the slicer alone ──
        await Case("WR1", async () =>
        {
            string words = string.Concat(Enumerable.Repeat("abcdefghi ", 10));   // a space at 9, 19, … 99
            var p = WebFetchService.Slice(words, 0, 95);
            Check("WR1", "a part that would cut a word ends at the last space in its final tenth, and End is where the next part starts",
                p.Text.EndsWith("abcdefghi ") && p.Start == 0 && p.End == 90 && p.Total == 100, $"[{p.Text}] {p.Start}-{p.End}/{p.Total}");
            string s = "aaaa bbbb cccc dddd";

            string emoji = new string('x', 9) + "\U0001F600" + "yyyy";   // the pair sits at 9 and 10
            var e = WebFetchService.Slice(emoji, 0, 10);
            var e2 = WebFetchService.Slice(emoji, 10, 10);
            Check("WR1b", "the cut never splits a surrogate pair — at the end of a part or at the start of the next",
                e.End == 9 && !char.IsHighSurrogate(e.Text[^1]) && e2.Start == 9 && char.IsHighSurrogate(e2.Text[0]),
                $"end={e.End} start2={e2.Start}");

            var last = WebFetchService.Slice(s, 15, 100);
            var past = WebFetchService.Slice(s, 999, 100);
            Check("WR1c", "the last part runs to the end (End == Total); an offset past the end gives an empty part",
                last.Text == "dddd" && last.End == s.Length && past.Text == "" && past.Start == s.Length && past.End == s.Length,
                $"last=[{last.Text}] past=[{past.Text}] {past.Start}");
            await Task.CompletedTask;
        });

        // ── through the tool ──
        await Case("WR2", async () =>
        {
            using var mock = new RedirectMock();
            string url = $"http://127.0.0.1:{mock.Port}/long";
            int LongHits() => mock.Seen.Count(s => s.EndsWith("/long", StringComparison.Ordinal));
            var exec = typeof(AgentToolService).GetMethod("ExecuteWebFetchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            async Task<ToolResult> Fetch(params (string k, string v)[] args)
            {
                var call = new ToolCall { Type = ToolType.WebFetch, ToolName = "web_fetch", Arguments = args.ToDictionary(a => a.k, a => a.v) };
                return await (Task<ToolResult>)exec.Invoke(Svc, new object[] { call, CancellationToken.None })!;
            }

            var web = provider.GetRequiredService<WebFetchService>();
            var (def, most) = Svc.WebFetchReadBudget();
            string total = RedirectMock.LongBody.Length.ToString("N0");

            var first = await Fetch(("url", url));
            string body1 = UrlPolicy.EnvelopeBody(first.Output ?? "") ?? "";
            Check("WR2", $"a long page: the first part is {def:N0} characters (not 4,000), inside the page-content envelope, and says how to read on",
                first.Success && body1.Length > 4_000 && body1.Length <= def && body1.StartsWith("w00001 ")
                && first.Output.Contains($"of {total}") && first.Output.Contains("offset="),
                $"len={body1.Length} def={def} out=[{Trunc(first.Output, 200)} … {Trunc(first.Output?[^Math.Min(160, first.Output?.Length ?? 0)..], 160)}]");

            int next = int.Parse(System.Text.RegularExpressions.Regex.Match(first.Output ?? "", @"offset=(\d+)").Groups[1].Value);
            var second = await Fetch(("url", url), ("offset", next.ToString()));
            string body2 = UrlPolicy.EnvelopeBody(second.Output ?? "") ?? "";
            Check("WR2b", "part 2 starts exactly where part 1 ended — and the page was downloaded once for both",
                second.Success && RedirectMock.LongBody.Substring(next).StartsWith(body2) && body2.Length > 0 && LongHits() == 1,
                $"next={next} hits={LongHits()} part2=[{Trunc(body2, 60)}]");

            // Read to the end the way a model does — each time with the offset the reply names: every word comes back
            // once, in order (the envelope trims a part's trailing space, so words are compared, not characters).
            static string NextOffset(string output) => System.Text.RegularExpressions.Regex.Match(output ?? "", @"offset=(\d+)").Groups[1].Value;
            var got = new List<string>();
            void Words(string body) => got.AddRange(body.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            Words(body1); Words(body2);
            string lastOut = second.Output ?? "";
            for (int guard = 0; NextOffset(lastOut) is { Length: > 0 } o && guard < 30; guard++)
            {
                var r = await Fetch(("url", url), ("offset", o));
                Words(UrlPolicy.EnvelopeBody(r.Output ?? "") ?? "");
                lastOut = r.Output ?? "";
            }
            var want0 = RedirectMock.LongBody.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Check("WR2c", "reading on with the named offsets gives back every word of the page once, in order (Thai and the emoji intact); the last part has no read-on hint",
                got.SequenceEqual(want0) && !lastOut.Contains("to read on") && got[^1] == "END-OF-LONG" && LongHits() == 1,
                $"got={got.Count} words want={want0.Length} last=[{(got.Count > 0 ? got[^1] : "")}] hits={LongHits()}");

            var fresh = await Fetch(("url", url));
            Check("WR2d", "a first read (no offset) always downloads again — the page may have changed",
                fresh.Success && LongHits() == 2, $"hits={LongHits()}");

            var pastEnd = await Fetch(("url", url), ("offset", "999999"));
            string want = string.Format(loc.T("chat.web.offsetPastEnd"), 999999, url, RedirectMock.LongBody.Length);
            Check("WR2e", "an offset past the end is refused in the UI language, naming the page's length",
                !pastEnd.Success && Seen(pastEnd).Contains(want), $"[{Seen(pastEnd)}] want [{want}]");

            var tiny = await Fetch(("url", url), ("offset", "7"), ("max_chars", "10"));
            var huge = await Fetch(("url", url), ("offset", "7"), ("max_chars", "999999"));
            int tinyLen = (UrlPolicy.EnvelopeBody(tiny.Output ?? "") ?? "").Length, hugeLen = (UrlPolicy.EnvelopeBody(huge.Output ?? "") ?? "").Length;
            Check("WR2f", $"max_chars is clamped: 10 → at least 500 (≈ 450–500 after the word cut), 999999 → at most {most:N0}",
                tinyLen is >= 450 and <= 500 && hugeLen <= most && hugeLen > def - 10,
                $"tiny={tinyLen} huge={hugeLen} most={most} def={def}");

            // The cache lives 10 minutes: an older page is downloaded again even when reading on.
            var keepClock = web.Now;
            try
            {
                var later = DateTime.UtcNow + WebFetchService.PageCacheLife + TimeSpan.FromMinutes(1);
                web.Now = () => later;
                int before = LongHits();
                var aged = await Fetch(("url", url), ("offset", next.ToString()));
                Check("WR2g", "after the cache's lifetime, reading on downloads the page again",
                    aged.Success && LongHits() == before + 1, $"hits {before} → {LongHits()}");
            }
            finally { web.Now = keepClock; }

            // A page that failed is never kept: reading on asks the site again.
            mock.Clear();
            string missing = $"http://127.0.0.1:{mock.Port}/missing";
            await Fetch(("url", missing));
            await Fetch(("url", missing), ("offset", "5"));
            Check("WR2h", "a failed page is not cached — reading on asks again", mock.Seen.Count(s => s.EndsWith("/missing")) == 2,
                string.Join(", ", mock.Seen));
        });

        await Case("WR3", async () =>
        {
            // The web tools are switched off in this harness's settings: switch them on for the look, then back.
            var features = provider.GetRequiredService<SettingsService>().Settings.Features;
            bool keep = features.WebFetch;
            features.WebFetch = true;
            CluadeX.Services.Providers.ToolSchema schema;
            try { schema = Svc.BuildNativeToolSchemas().FirstOrDefault(s => s.Name == "web_fetch"); }
            finally { features.WebFetch = keep; }
            if (schema == null)
            {
                GapIf("WR3", "web_fetch's schema advertises offset and max_chars (not offered here even with WebFetch on — the license gate)", false);
                return;
            }
            string json = schema.InputSchema.GetRawText();
            Check("WR3", "web_fetch's schema advertises offset and max_chars as integers",
                json.Contains("\"offset\"") && json.Contains("\"max_chars\"") && json.Contains("\"integer\""),
                Trunc(json, 300));
            await Task.CompletedTask;
        });
    }
}
