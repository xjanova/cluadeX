using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Media;
using CluadeX.Services.Tools;

namespace ToolTest;

/// <summary>
/// media_transcript (Services/Tools/MediaTranscriptToolModule) offline: the subtitle reader on YouTube-shaped VTT and on
/// SRT, yt-dlp's -J facts and the track choice, the argument lists (config ignored, the URL after "--"), then the module
/// with a stand-in yt-dlp (RunOverride) and a stand-in Whisper (HttpOverride) — the transcript saved and read on without a
/// second run, the "no subtitles → speech=true" answer with the price, the permission (free for a saved one, the paid
/// prompt even with permissions off), what a missing yt-dlp / key / web switch says, and real ffmpeg cutting a real tone
/// into pieces that reach the stand-in API with the key. No network, no yt-dlp install, nothing paid.
/// </summary>
internal static partial class Program
{
    private const string AutoVtt = "WEBVTT\nKind: captions\nLanguage: th\n\n"
        + "00:00:00.000 --> 00:00:02.500 align:start position:0%\n \nสวัสดี<00:00:00.500><c> ครับ</c>\n\n"
        + "00:00:02.500 --> 00:00:02.510 align:start position:0%\nสวัสดี ครับ\n \n\n"
        + "00:00:02.510 --> 00:00:05.000 align:start position:0%\nสวัสดี ครับ\nวันนี้<00:00:03.000><c> เรา</c><c> มา</c>\n\n"
        + "00:00:35.000 --> 00:00:37.000 align:start position:0%\nวันนี้ เรา มา\nคุย &amp; เล่น\n\n"
        + "01:02:03.000 --> 01:02:04.000\nตอนท้าย\n";

    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nHello <i>there</i>\n\n2\n00:00:40,500 --> 00:00:42,000\nSecond line\nstill second\n";

    private const string InfoJson = "{\"id\":\"dQw4w9WgXcQ\",\"title\":\"วิดีโอทดสอบ\",\"uploader\":\"ช่องทดสอบ\",\"duration\":754,"
        + "\"language\":\"th\",\"webpage_url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\","
        + "\"subtitles\":{\"live_chat\":[{\"ext\":\"json\"}]},"
        + "\"automatic_captions\":{\"th-orig\":[{\"ext\":\"vtt\"}],\"th\":[{\"ext\":\"vtt\"}],\"en\":[{\"ext\":\"vtt\"}]}}";

    private sealed class StubWhisper : HttpMessageHandler
    {
        public int Calls;
        public string LastAuth = "", LastBody = "";
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastAuth = request.Headers.Authorization?.ToString() ?? "";
            LastBody = request.Content == null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(ct));
            string json = Status == HttpStatusCode.OK
                ? "{\"text\":\"ทั้งหมด\",\"segments\":[{\"start\":0.0,\"text\":\" เสียงทดสอบ หนึ่ง\"},{\"start\":31.5,\"text\":\" เสียงทดสอบ สอง\"}]}"
                : "{\"error\":{\"message\":\"Incorrect API key provided\"}}";
            return new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static async Task MediaTranscript(IServiceProvider provider, LocalizationService loc)
    {
        // ── the subtitle reader ──
        await Case("MT1", async () =>
        {
            var lines = Subtitles.Parse(AutoVtt);
            string text = Subtitles.Format(lines);
            Check("MT1", "YouTube automatic captions: rolling repeats and the space-only first line dropped, inline <c>/timing tags gone, entities decoded",
                lines.Select(l => l.Text).SequenceEqual(new[] { "สวัสดี ครับ", "วันนี้ เรา มา", "คุย & เล่น", "ตอนท้าย" }),
                string.Join(" | ", lines.Select(l => $"{l.Start} {l.Text}")));
            Check("MT1b", "a [m:ss] mark starts the text and a new line once 30 s have passed; an hour shows as h:mm:ss",
                text == "[0:00] สวัสดี ครับ วันนี้ เรา มา\n[0:35] คุย & เล่น\n[1:02:03] ตอนท้าย", text.Replace("\n", "⏎"));
            var srt = Subtitles.Parse(Srt);
            Check("MT1c", "SRT: cue numbers and comma milliseconds read, a two-line cue kept as two lines",
                srt.Count == 3 && srt[0].Text == "Hello there" && srt[1].Start == TimeSpan.FromSeconds(40.5) && srt[2].Text == "still second",
                string.Join(" | ", srt.Select(l => $"{l.Start} {l.Text}")));
            await Task.CompletedTask;
        });

        // ── yt-dlp's facts, the track, the arguments ──
        await Case("MT2", async () =>
        {
            var info = YtDlp.ParseInfo(InfoJson);
            Check("MT2", "-J facts: title, uploader, length, language; live_chat is not a subtitle track",
                info != null && info.Title == "วิดีโอทดสอบ" && info.Uploader == "ช่องทดสอบ" && info.Duration == TimeSpan.FromSeconds(754)
                && info.Subtitles.Count == 0 && info.AutoCaptions.Count == 3, info == null ? "null" : $"{info.Title} subs={info.Subtitles.Count} auto={info.AutoCaptions.Count}");
            var t1 = YtDlp.ChooseTrack(info!, null);
            Check("MT2b", "no human subtitles: the automatic captions in the spoken language (th-orig), not a machine translation",
                t1 is { Language: "th-orig", Automatic: true, Translated: false }, $"{t1}");
            var human = info! with { Subtitles = new[] { "en", "th" } };
            Check("MT2c", "human subtitles in the wanted language beat automatic ones; asking for English gets English",
                YtDlp.ChooseTrack(human, null) is { Language: "th", Automatic: false } && YtDlp.ChooseTrack(human, "en") is { Language: "en", Automatic: false },
                $"{YtDlp.ChooseTrack(human, null)} / {YtDlp.ChooseTrack(human, "en")}");
            var enVideo = info with { Language = "en", AutoCaptions = new[] { "en", "th" } };
            Check("MT2d", "an English video with only translated automatic Thai: the English (spoken) captions are chosen first",
                YtDlp.ChooseTrack(enVideo, null) is { Language: "en", Translated: false }, $"{YtDlp.ChooseTrack(enVideo, null)}");
            var none = info with { AutoCaptions = Array.Empty<string>() };
            Check("MT2e", "a video with no track at all → null (the tool then offers speech=true)", YtDlp.ChooseTrack(none, null) == null);

            const string url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            var probe = YtDlp.ProbeArgs(url);
            var subs = YtDlp.SubtitleArgs(url, new SubtitleTrack("th-orig", true, false), @"C:\x");
            var audio = YtDlp.AudioArgs("-oops", @"C:\x");
            Check("MT2f", "every yt-dlp run: --ignore-config and --no-playlist first, the URL last after \"--\" (a URL like -oops is never an option)",
                new[] { probe, subs, audio }.All(a => a[0] == "--ignore-config" && a.Contains("--no-playlist") && a[^2] == "--")
                && probe[^1] == url && audio[^1] == "-oops" && subs.Contains("--write-auto-subs") && audio.Contains("--max-filesize"),
                string.Join(" ", audio));

            Check("MT2g", "YouTube ids from every link form (watch, youtu.be, shorts, live, embed, m./music., bare id); other sites → null",
                MediaTranscriptToolModule.YouTubeId("https://youtu.be/dQw4w9WgXcQ?t=3") == "dQw4w9WgXcQ"
                && MediaTranscriptToolModule.YouTubeId("https://m.youtube.com/watch?feature=x&v=dQw4w9WgXcQ") == "dQw4w9WgXcQ"
                && MediaTranscriptToolModule.YouTubeId("https://www.youtube.com/shorts/dQw4w9WgXcQ") == "dQw4w9WgXcQ"
                && MediaTranscriptToolModule.YouTubeId("https://www.youtube.com/live/dQw4w9WgXcQ") == "dQw4w9WgXcQ"
                && MediaTranscriptToolModule.YouTubeId("https://music.youtube.com/watch?v=dQw4w9WgXcQ") == "dQw4w9WgXcQ"
                && MediaTranscriptToolModule.YouTubeId("dQw4w9WgXcQ") == "dQw4w9WgXcQ"
                && MediaTranscriptToolModule.YouTubeId("https://vimeo.com/123456789") == null
                && MediaTranscriptToolModule.YouTubeId("https://notyoutube.com/watch?v=dQw4w9WgXcQ") == null);

            var p = SpeechToText.ParseVerbose("{\"segments\":[{\"start\":1.5,\"text\":\" a \"},{\"start\":2,\"text\":\"\"}]}");
            Check("MT2h", "Whisper verbose_json: segments with times, empty ones dropped; plain text when there are none; junk → null",
                p is { Count: 1 } && p[0].Start == TimeSpan.FromSeconds(1.5) && p[0].Text == "a"
                && SpeechToText.ParseVerbose("{\"text\":\"only\"}") is { Count: 1 } && SpeechToText.ParseVerbose("<html>") == null);
            Check("MT2i", "price: per started minute, written invariant ($0.078 for 12:34; $0.006 a minute)", SpeechToText.Usd(SpeechToText.CostUsd(TimeSpan.FromSeconds(754))) == "$0.078" && SpeechToText.Usd(SpeechToText.UsdPerMinute) == "$0.006" && SpeechToText.Usd(2.5) == "$2.50",
                SpeechToText.Usd(SpeechToText.CostUsd(TimeSpan.FromSeconds(754))));
            await Task.CompletedTask;
        });

        // ── the module, with stand-ins ──
        string root = Path.Combine(Path.GetTempPath(), "cluadex-mt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var keepPath = YtDlp.PathOverride;
        var keepRun = YtDlp.RunOverride;
        var keepHttp = SpeechToText.HttpOverride;
        try
        {
            var settings = new AppSettings();
            bool web = true;
            var module = new MediaTranscriptToolModule(() => settings, () => root, () => web);
            var ctx = new ToolModuleContext { SessionKey = "mt", OutputBudgetChars = 30_000 };
            ToolCall C(params (string k, string v)[] a) => new() { Type = ToolType.MediaTranscript, ToolName = "media_transcript", Arguments = a.ToDictionary(x => x.k, x => x.v) };
            var runs = new List<string>();
            string infoJson = InfoJson;
            YtDlp.PathOverride = @"C:\fake\yt-dlp.exe";
            YtDlp.RunOverride = (exe, args, timeout, ct) =>
            {
                runs.Add(string.Join(" ", args));
                if (args.Contains("-J")) return Task.FromResult(new ProcRun(0, infoJson, ""));
                if (args.Contains("--skip-download"))
                {
                    string o = args[args.ToList().IndexOf("-o") + 1];
                    File.WriteAllText(o.Replace("%(ext)s", "th-orig.vtt"), AutoVtt, new UTF8Encoding(false));
                    return Task.FromResult(new ProcRun(0, "", ""));
                }
                return Task.FromResult(new ProcRun(1, "", "ERROR: [youtube] not in this stand-in"));
            };

            await Case("MT3", async () =>
            {
                const string url = "https://youtu.be/dQw4w9WgXcQ";
                var perm = module.DescribePermission(C(("source", url)), ctx);
                Check("MT3", "a link's subtitles: asked as a web read of that link (browse scope), once per site in this chat",
                    perm.Scope == "browse" && perm.Resource.Contains("dQw4w9WgXcQ") && perm.OnceKey == "media:youtu.be" && !perm.AskEvenWhenPermissionsOff, $"{perm}");

                var r = await module.ExecuteAsync(C(("source", url)), ctx, CancellationToken.None);
                string body = UrlPolicy.EnvelopeBody(r.Output ?? "") ?? "";
                Check("MT3b", "the transcript comes back with title, uploader, length, where it came from and the saved file, inside the envelope",
                    r.Success && r.Output.Contains("วิดีโอทดสอบ") && r.Output.Contains("ช่องทดสอบ") && r.Output.Contains("12:34")
                    && r.Output.Contains("automatic captions (th-orig)") && body.StartsWith("[0:00] สวัสดี ครับ") && r.Output.Contains("Saved as:"),
                    Trunc(r.Output, 500));
                string saved = System.Text.RegularExpressions.Regex.Match(r.Output ?? "", @"Saved as: (.+)").Groups[1].Value.Trim();
                Check("MT3c", "…saved under the data folder's transcripts\\, as the transcript text", saved.StartsWith(Path.Combine(root, "transcripts")) && File.Exists(saved)
                    && File.ReadAllText(saved).Contains("ตอนท้าย"), saved);

                int before = runs.Count;
                var again = await module.ExecuteAsync(C(("source", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10"), ("offset", "5"), ("max_chars", "600")), ctx, CancellationToken.None);
                Check("MT3d", "the same video by another link form, reading on: from the saved transcript — yt-dlp not run again, the reply says so",
                    again.Success && runs.Count == before && again.Output.Contains("saved earlier") && again.Output.Contains("Characters 5"), $"runs {before}→{runs.Count} {Trunc(again.Output, 200)}");
                var free = module.DescribePermission(C(("source", "dQw4w9WgXcQ")), ctx);
                Check("MT3e", "a bare id of a saved video: the permission is free (nothing sent, nothing paid)", free.IsFree, $"{free}");
                var past = await module.ExecuteAsync(C(("source", url), ("offset", "999999")), ctx, CancellationToken.None);
                Check("MT3f", "an offset past the end is refused in the UI language", !past.Success && Seen(past).Contains(string.Format(loc.T("media.tr.offsetPastEnd"), 999999, 0).Split('—')[0].Trim()), Seen(past));
            });

            await Case("MT4", async () =>
            {
                infoJson = InfoJson.Replace("\"th-orig\":[{\"ext\":\"vtt\"}],\"th\":[{\"ext\":\"vtt\"}],\"en\":[{\"ext\":\"vtt\"}]", "");
                var r = await module.ExecuteAsync(C(("source", "https://www.youtube.com/watch?v=aaaaaaaaaaa")), ctx, CancellationToken.None);
                Check("MT4", "no subtitles: the answer says to call again with speech=true, with the length, the price and that the sound goes to OpenAI",
                    !r.Success && Seen(r).Contains("speech=true") && Seen(r).Contains("13") && Seen(r).Contains("$0.078") && Seen(r).Contains("OpenAI"), Seen(r));
                var perm = module.DescribePermission(C(("source", "https://www.youtube.com/watch?v=aaaaaaaaaaa"), ("speech", "true")), ctx);
                Check("MT4b", "speech=true on a link: the paid prompt, asked even with permissions off, naming Whisper and the price a minute",
                    perm.AskEvenWhenPermissionsOff && !perm.IsFree && perm.Detail.Contains("whisper-1") && perm.Detail.Contains("$0.006"), $"{perm.Detail}");
                var noKey = await module.ExecuteAsync(C(("source", "https://www.youtube.com/watch?v=aaaaaaaaaaa"), ("speech", "true")), ctx, CancellationToken.None);
                Check("MT4c", "speech with no OpenAI key: says where to add it, and nothing is downloaded",
                    !noKey.Success && noKey.Error == loc.T("media.tr.noKey") && !runs.Any(a => a.Contains("bestaudio")), Seen(noKey));
                infoJson = InfoJson;
            });

            await Case("MT5", async () =>
            {
                YtDlp.PathOverride = "";
                var r = await module.ExecuteAsync(C(("source", "https://www.youtube.com/watch?v=bbbbbbbbbbb")), ctx, CancellationToken.None);
                Check("MT5", "yt-dlp missing: the answer names the winget command, nothing is run",
                    !r.Success && Seen(r).Contains(YtDlp.InstallCommand), Seen(r));
                YtDlp.PathOverride = @"C:\fake\yt-dlp.exe";
                web = false;
                var off = await module.ExecuteAsync(C(("source", "https://www.youtube.com/watch?v=bbbbbbbbbbb")), ctx, CancellationToken.None);
                Check("MT5b", "web tools switched off: a link is refused with where to switch them on", !off.Success && off.Error == loc.T("media.tr.webOff"), Seen(off));
                web = true;
                var bad = await module.ExecuteAsync(C(("source", "ftp://x/y.mp4")), ctx, CancellationToken.None);
                Check("MT5c", "neither a web link nor a media file: a worded refusal", !bad.Success && Seen(bad).Contains("ftp://x/y.mp4"), Seen(bad));
                Check("MT5d", "the name a model writes resolves through the real tool service (youtube_transcript, transcribe)",
                    Svc.ResolveToolTypePublic("youtube_transcript") == ToolType.MediaTranscript && Svc.ResolveToolTypePublic("transcribe") == ToolType.MediaTranscript);
            });

            // Real ffmpeg cuts a real tone; the pieces reach the stand-in API with the key.
            await Case("MT6", async () =>
            {
                string? ffmpeg = Ffmpeg.Locate();
                if (ffmpeg == null) { GapIf("MT6", "speech to text with real ffmpeg (ffmpeg is not installed here)", false); return; }
                string wav = Path.Combine(root, "tone.wav");
                var made = await Ffmpeg.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", wav }, wav, TimeSpan.FromSeconds(60), CancellationToken.None);
                if (made.Outcome != FfmpegOutcome.Done) { Check("MT6", "made a 3 s test tone with ffmpeg", false, made.Detail); return; }
                settings.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test-harness", BaseUrl = "http://127.0.0.1:9/v1" };
                var stub = new StubWhisper();
                SpeechToText.HttpOverride = new HttpClient(stub);

                var perm = module.DescribePermission(C(("source", wav)), ctx);
                Check("MT6", "a file: the paid prompt with its length and price, asked even with permissions off",
                    perm.AskEvenWhenPermissionsOff && perm.Detail.Contains("0:03") && perm.Detail.Contains("$0.006"), perm.Detail);
                var r = await module.ExecuteAsync(C(("source", wav), ("language", "th")), ctx, CancellationToken.None);
                Check("MT6b", "ffmpeg's piece reaches the API: model whisper-1, verbose_json, language th, the key as Bearer",
                    stub.Calls == 1 && stub.LastAuth == "Bearer sk-test-harness" && stub.LastBody.Contains("whisper-1") && stub.LastBody.Contains("verbose_json")
                    && stub.LastBody.Contains("chunk_000.m4a"), $"calls={stub.Calls} auth=[{stub.LastAuth}]");
                string body = UrlPolicy.EnvelopeBody(r.Output ?? "") ?? "";
                Check("MT6c", "the segments come back with their times: [0:00] … and a new mark at [0:31]",
                    r.Success && body == "[0:00] เสียงทดสอบ หนึ่ง\n[0:31] เสียงทดสอบ สอง" && r.Output.Contains("speech to text (OpenAI whisper-1, 1 piece)"), Trunc(r.Output, 400));
                var again = await module.ExecuteAsync(C(("source", wav), ("language", "th")), ctx, CancellationToken.None);
                Check("MT6d", "asked again: the saved transcript, the API is not called (not paid twice)", again.Success && stub.Calls == 1 && again.Output.Contains("saved earlier"), $"calls={stub.Calls}");

                stub.Status = HttpStatusCode.Unauthorized;
                File.SetLastWriteTimeUtc(wav, DateTime.UtcNow.AddMinutes(-5));   // an edited file is a new recording
                var refused = await module.ExecuteAsync(C(("source", wav), ("language", "th")), ctx, CancellationToken.None);
                Check("MT6e", "a refused key: the localized key message, the piece number, nothing saved, the key not in the text",
                    !refused.Success && Seen(refused).Contains(loc.T("media.tr.keyRefused")) && Seen(refused).Contains("1") && !Seen(refused).Contains("sk-test-harness"), Seen(refused));
                Check("MT6f", "the work folder is removed after every run (no audio left in %TEMP%)",
                    !Directory.EnumerateDirectories(Path.GetTempPath(), "cluadex-transcript-*").Any(d => Directory.GetLastWriteTimeUtc(d) > DateTime.UtcNow.AddMinutes(-2)));
            });
        }
        finally
        {
            YtDlp.PathOverride = keepPath;
            YtDlp.RunOverride = keepRun;
            SpeechToText.HttpOverride = keepHttp;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
