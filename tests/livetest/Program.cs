using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Voice;

namespace LiveTest;

/// <summary>
/// Voice mode, proven offline and for free — no OpenAI key, no paid call, no microphone, no speaker.
///
/// The real <see cref="LiveVoiceSession"/> and the real <see cref="ChromeDictationService"/> bridge (from the built
/// CluadeX.dll) run against a localhost copy of OpenAI's Live API (<c>POST /v1/live/sessions</c>, <c>/hangup</c>),
/// with a fake key only the mock ever sees. The mic page is played by HTTP calls made exactly as the page makes them
/// (token, origin, long poll); a last section runs the page's own script in a headless Google Chrome with a fake
/// microphone to prove it produces a real WebRTC offer and hands it to CluadeX instead of calling OpenAI itself.
///
/// What it proves: the request shape (gpt-live-1, client delegation, WebRTC transport, the key in the header and
/// nowhere else); the key never reaches the page; a page from another origin, a wrong token or a dictation-only window
/// cannot start a paid session, and a live window can start only one; a delegation is rebuilt from the transcript,
/// runs through the agent one at a time, and comes back as session.commentary.append chunks for that delegation id;
/// the meter (per second, 15 s minimum); the minutes cap (warning, session.close, hang-up from CluadeX when the page
/// does not confirm); a window that dies is hung up; reading replies aloud (the spoken text, say/hush on the bridge).
/// </summary>
internal static class Program
{
    const string Key = "sk-test-LIVE-not-a-real-key-0123456789";
    static int _pass, _fail;
    static readonly StringBuilder Report = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    // The live session the bridge's events go to — what ChatViewModel.Voice wires in the app.
    static LiveVoiceSession? CurLive;
    static int CurSession = -1;

    static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        LiveVoiceSession.CloseGrace = TimeSpan.FromSeconds(1.5);
        LiveVoiceSession.TranscriptSettle = TimeSpan.FromMilliseconds(300);
        LiveVoiceSession.TranscriptWaitMax = TimeSpan.FromSeconds(2);
        Log("# livetest — voice mode (read aloud + GPT-Live) against a localhost Live API mock");
        Log($"CluadeX.dll: {typeof(LiveVoiceSession).Assembly.GetName().Version}  ·  {DateTime.Now:yyyy-MM-dd HH:mm}");

        using var mock = new MockLiveApi();
        var bridge = ChromeDictationService.TryStart(out var err);
        if (bridge == null) { Log("FAIL  the bridge did not start: " + err); return 1; }
        bridge.LiveEvent += (s, ev) => { if (CurLive is { } l && s == CurSession) l.OnServerEvent(ev); };
        bridge.Closed += (s, neverOpened) => { if (CurLive is { } l && s == CurSession) _ = l.PageGoneAsync(); };
        bridge.PageEvent += (s, type, detail) =>
        {
            // ChatViewModel.Voice.OnLivePageError: a window that could not set the call up is hung up from here.
            if (CurLive is { } l && s == CurSession && type == "error" && detail.StartsWith("live")) _ = l.PageGoneAsync("page-error");
        };
        try
        {
            await TestWire(mock);
            await TestBridgeSecurity(bridge, mock);
            await TestDelegation(bridge, mock);
            await TestMeterAndCap(bridge, mock);
            TestSpeechText();
            await TestSpeakBridge(bridge);
            TestPageScript(bridge);
            await TestRealChrome(bridge, mock);
        }
        catch (Exception ex)
        {
            Check("the harness ran to the end", false, ex.ToString());
        }
        finally
        {
            bridge.Dispose();
        }

        Log($"\n**{_pass} passed, {_fail} failed** — paid calls made: 0 (every request went to {mock.Base})");
        string path = Path.Combine(AppContext.BaseDirectory, "live-report.md");
        File.WriteAllText(path, Report.ToString());
        Console.WriteLine("report: " + path);
        return _fail == 0 ? 0 : 1;
    }

    // ───────────────────────── A. the request and the answer ─────────────────────────

    static async Task TestWire(MockLiveApi mock)
    {
        Log("\n## A — creating the session: what CluadeX sends, what it reads back");
        var live = new LiveVoiceSession(Http, mock.Base, Key, new LiveVoiceOptions { Voice = "marin", Thai = true });
        using (var doc = JsonDocument.Parse(live.BuildCreateBody(Sdp.Offer)))
        {
            var r = doc.RootElement;
            var s = r.GetProperty("session");
            Check("A1 session.model is gpt-live-1, delegation is {type: client}, voice marin",
                s.GetProperty("model").GetString() == "gpt-live-1"
                && s.GetProperty("delegation").GetProperty("type").GetString() == "client"
                && s.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString() == "marin");
            Check("A1 transport is {type: webrtc, sdp: the page's offer}; no audio.format (WebRTC negotiates it)",
                r.GetProperty("transport").GetProperty("type").GetString() == "webrtc"
                && r.GetProperty("transport").GetProperty("sdp").GetString() == Sdp.Offer
                && !s.GetProperty("audio").TryGetProperty("format", out _));
            string instr = s.GetProperty("instructions").GetString() ?? "";
            Check("A1 the instructions tell GPT-Live to delegate the work, not invent results, and speak Thai",
                instr.Contains("Delegate") && instr.Contains("never invent") && instr.Contains("Thai") && instr.Length < 16384 * 3);
        }

        mock.Reset("ok");
        var res = await live.CreateAsync(Sdp.Offer);
        var req = mock.Requests.SingleOrDefault(x => x.Path == "/v1/live/sessions");
        Check("A2 one POST /v1/live/sessions with the key as a Bearer header and a JSON body",
            req != null && req.Method == "POST" && req.Auth == "Bearer " + Key && req.ContentType.StartsWith("application/json"),
            req == null ? "(no request)" : $"{req.Method} {req.Path} auth={(req.Auth == "Bearer " + Key ? "ok" : req.Auth)}");
        Check("A2 the 201 answer is read: transport.sdp and session.id; the session is live",
            res.AnswerSdp == MockLiveApi.AnswerSdp && res.SessionId == "live_test_1" && live.State == "live" && res.Error == null,
            $"{res.SessionId} / {live.State} / {res.Error}");
        Check("A2 billing starts at creation: at least 15 s ($0.0125) at once",
            live.Seconds >= 15 && live.CostUsd == 0.0125m, $"{live.Seconds:0.0}s ${live.CostUsd}");
        var again = await live.CreateAsync(Sdp.Offer);
        Check("A3 a second start of the same session is refused without calling the API",
            again.AnswerSdp == null && mock.Requests.Count(x => x.Path == "/v1/live/sessions") == 1);
        live.Close("user");
        await WaitUntil(() => live.IsEnded, 5000);
        live.Dispose();

        foreach (var (code, word) in new[] { (401, "key"), (403, "permission"), (429, "") })
        {
            mock.Reset(code.ToString());
            string? ended = null;
            var bad = new LiveVoiceSession(Http, mock.Base, Key, new LiveVoiceOptions());
            bad.Ended += r => ended = r;
            var br = await bad.CreateAsync(Sdp.Offer);
            Check($"A4 HTTP {code} from the Live API: a plain error, the key never in it, nothing billed, the session ends",
                br.AnswerSdp == null && br.Error != null && !br.Error.Contains(Key) && br.Error.Contains(code.ToString())
                && bad.State == "failed" && bad.CostUsd == 0 && ended == "failed",
                br.Error?.Replace("\n", " ") ?? "(no error)");
        }

        mock.Reset("ok");
        var none = new LiveVoiceSession(Http, mock.Base, Key, new LiveVoiceOptions());
        var nr = await none.CreateAsync("not an sdp");
        Check("A5 a page that sends no real SDP offer never causes an API call", nr.Error != null && mock.Requests.Count == 0);
    }

    // ───────────────────────── B. who may start a paid session ─────────────────────────

    static async Task TestBridgeSecurity(ChromeDictationService bridge, MockLiveApi mock)
    {
        Log("\n## B — the bridge: only the live window, once, from its own origin; the key never reaches it");
        mock.Reset("ok");
        var live = NewLive(bridge, mock, out var page);
        page.StartPolling();

        var html = await Http.GetAsync(page.Url);
        string body = await html.Content.ReadAsStringAsync();
        string csp = html.Headers.TryGetValues("Content-Security-Policy", out var c) ? string.Join(";", c) : "";
        Check("B1 the live page is served with WebRTC + the oai-events data channel, and talks only to its own origin",
            html.IsSuccessStatusCode && body.Contains("RTCPeerConnection") && body.Contains("'oai-events'") && body.Contains("/live/offer")
            && csp.Contains("connect-src 'self'") && !body.Contains("api.openai.com") && !body.Contains("Authorization"), csp);
        Check("B1 the page URL carries the session token but no key", page.Url.Contains("mode=live") && !page.Url.Contains(Key));

        var wrongOrigin = await page.Post("/live/offer", new { sdp = Sdp.Offer }, origin: "https://evil.example");
        var noOrigin = await page.Post("/live/offer", new { sdp = Sdp.Offer }, origin: null);
        var wrongToken = await page.Post("/live/offer", new { sdp = Sdp.Offer }, token: "0000");
        var rebound = await page.Raw(HttpMethod.Post, "/live/offer", JsonSerializer.Serialize(new { t = page.T, s = page.S, sdp = Sdp.Offer }), page.Origin, host: "evil.example");
        Check("B2 another web page cannot start a paid session: foreign/missing origin, wrong token, rebinding Host → 403, no API call",
            wrongOrigin.Code == 403 && noOrigin.Code == 403 && wrongToken.Code == 403 && rebound.Code == 403 && mock.Requests.Count == 0,
            $"{wrongOrigin.Code}/{noOrigin.Code}/{wrongToken.Code}/{rebound.Code} calls={mock.Requests.Count}");

        var ok = await page.Post("/live/offer", new { sdp = Sdp.Offer });
        Check("B3 the live window's offer: CluadeX calls the API and hands back only the answer SDP",
            ok.Code == 200 && ok.Body.Contains("\"sdp\"") && !ok.Body.Contains(Key) && !ok.Body.Contains("Bearer")
            && mock.Requests.Count(r => r.Path == "/v1/live/sessions") == 1 && live.State == "live", $"{ok.Code} {Trim(ok.Body)}");
        var second = await page.Post("/live/offer", new { sdp = Sdp.Offer });
        Check("B4 a second offer from the same window is refused (409) — one billed session per window",
            second.Code == 409 && mock.Requests.Count(r => r.Path == "/v1/live/sessions") == 1, second.Code.ToString());
        var unknown = await page.Post("/live/event", new { ev = new { type = "session.update", foo = 1 } });
        var notObj = await page.Post("/live/event", new { ev = "x" });
        Check("B5 only Live API server events are relayed (anything else → 400)", unknown.Code == 400 && notObj.Code == 400);

        await EndLive(live, page);

        // A dictation-only window (the free mic) may never start a paid session.
        string url = bridge.BeginSession("th-TH", "th", "google");
        var dict = new FakePage(url);
        bridge.LiveOfferHandler = (_, sdp, ct) => throw new InvalidOperationException("must not be called");
        var fromDictation = await dict.Post("/live/offer", new { sdp = Sdp.Offer });
        var evFromDictation = await dict.Post("/live/event", new { ev = new { type = "session.started" } });
        Check("B6 the free dictation window cannot start a live session or post live events (403)",
            fromDictation.Code == 403 && evFromDictation.Code == 403 && mock.Requests.Count(r => r.Path == "/v1/live/sessions") == 1,
            $"{fromDictation.Code}/{evFromDictation.Code}");
        Check("B6 and nothing is queued for a dictation window's data channel", !(await dict.State()).Contains("\"m\""));
        bridge.LiveOfferHandler = null;
        bridge.EndSession();
    }

    // ───────────────────────── C. a spoken request through the agent ─────────────────────────

    static async Task TestDelegation(ChromeDictationService bridge, MockLiveApi mock)
    {
        Log("\n## C — client delegation: rebuilt from the transcript, run by CluadeX one at a time, spoken back");
        mock.Reset("ok");
        var asked = new ConcurrentQueue<string>();
        int running = 0, maxRunning = 0;
        var release = new TaskCompletionSource();
        string longReply = "สรุปไฟล์ main.cs: มีคลาส Program และเมธอด Main. ดูเพิ่มที่ [เอกสาร](https://example.com/docs).\n\n```csharp\nstatic void Main() { Console.WriteLine(\"hi\"); }\n```\n\n"
            + string.Join(" ", Enumerable.Repeat("ไฟล์นี้อ่านค่าตั้งค่าจาก appsettings.json แล้วเริ่มเซิร์ฟเวอร์.", 30))
            + " จบ https://example.com/end";
        var live = NewLive(bridge, mock, out var page, run: async (text, ct) =>
        {
            asked.Enqueue(text);
            int now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            try
            {
                if (text.Contains("ช้า")) await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return text.Contains("พัง") ? new LiveTaskResult("Build failed: CS1002 ; expected", false)
                     : text.Contains("ช้า") ? new LiveTaskResult("ทำงานช้าเสร็จแล้ว", true)
                     : new LiveTaskResult(longReply, true);
            }
            finally { Interlocked.Decrement(ref running); }
        });
        page.StartPolling();
        await page.Post("/live/offer", new { sdp = Sdp.Offer });
        await page.Event(new { type = "session.started" });

        await page.Event(new { type = "session.input_transcript.delta", delta = "เปิดไฟล์ ", start_ms = 100, end_ms = 600 });
        await page.Event(new { type = "session.input_transcript.delta", delta = "main.cs แล้วสรุปให้ฟัง", start_ms = 600, end_ms = 1500 });
        await page.Event(new { type = "session.output_transcript.delta", delta = "ได้เลย กำลังดูให้ค่ะ" });
        await page.Event(new { type = "session.delegation.created", event_id = "e1", offset_ms = 1600, delegation = new { id = "del_1", type = "delegation", target = "client" } });
        await WaitUntil(() => page.Out.Any(m => Is(m, "session.commentary.append", "del_1")), 6000);
        Check("C1 the request handed to CluadeX is what the user said, joined exactly as transcribed",
            asked.TryPeek(out var first) && first == "เปิดไฟล์ main.cs แล้วสรุปให้ฟัง", first ?? "(none)");
        Check("C1 the meter line shows the last words of each side",
            live.UserLine.Contains("สรุปให้ฟัง") && live.AssistantLine.Contains("กำลังดูให้"), $"{live.UserLine} | {live.AssistantLine}");
        var started = page.Out.FirstOrDefault(m => Is(m, "session.thinking.append", "del_1"));
        var parts = page.Out.Where(m => Is(m, "session.commentary.append", "del_1")).Select(m => m.GetProperty("content").GetString()!).ToList();
        Check("C2 'Started' goes quietly as session.thinking.append for that delegation", started.ValueKind == JsonValueKind.Object);
        Check("C2 the answer comes back as session.commentary.append {event_id, delegation_id: del_1, content}, 'Completed:' first",
            parts.Count >= 2 && parts[0].StartsWith("Completed: ") && page.Out.Where(m => Is(m, "session.commentary.append", "del_1"))
                .All(m => m.GetProperty("event_id").GetString()!.Length > 0), $"{parts.Count} parts");
        Check("C2 each append stays well under 500 tokens (≤ 450 chars), a handful at most",
            parts.All(p => p.Length <= LiveVoiceSession.ChunkChars) && parts.Count <= 4, string.Join(",", parts.Select(p => p.Length)));
        string spoken = string.Join(" ", parts);
        Check("C2 code, URLs and Markdown are not handed over to be read aloud",
            !spoken.Contains("Console.WriteLine") && !spoken.Contains("```") && !spoken.Contains("https://") && spoken.Contains("มีโค้ดในแชท")
            && spoken.Contains("เอกสาร"), Trim(spoken));

        await Task.Delay(300);   // the page's next poll carries the acknowledgement
        long lastN = page.LastOut;
        string raw = await page.State(ack: false);
        using (var d = JsonDocument.Parse(raw))
            Check("C3 messages the page has sent are dropped from the queue (the poll acknowledges them)",
                d.RootElement.GetProperty("out").EnumerateArray().All(m => m.GetProperty("n").GetInt64() > lastN), Trim(raw));

        // A failure, then two at once: the second waits for the first.
        await page.Event(new { type = "session.input_transcript.delta", delta = "build แล้วมันพัง" });
        await page.Event(new { type = "session.delegation.created", delegation = new { id = "del_2", target = "client" } });
        await WaitUntil(() => page.Out.Any(m => Is(m, "session.commentary.append", "del_2")), 6000);
        var failed = page.Out.Where(m => Is(m, "session.commentary.append", "del_2")).Select(m => m.GetProperty("content").GetString()).FirstOrDefault();
        Check("C4 a failed task is handed back as 'Failed: …' so GPT-Live says it did not work", failed?.StartsWith("Failed: Build failed") == true, failed);

        await page.Event(new { type = "session.input_transcript.delta", delta = "งานช้าหนึ่ง" });
        await page.Event(new { type = "session.delegation.created", delegation = new { id = "del_3", target = "client" } });
        await Task.Delay(500);
        await page.Event(new { type = "session.input_transcript.delta", delta = "งานที่สอง" });
        await page.Event(new { type = "session.delegation.created", delegation = new { id = "del_4", target = "client" } });
        await WaitUntil(() => page.Out.Any(m => Is(m, "session.thinking.append", "del_4") && m.GetProperty("content").GetString()!.StartsWith("Queued")), 4000);
        bool queued = page.Out.Any(m => Is(m, "session.thinking.append", "del_4") && m.GetProperty("content").GetString()!.StartsWith("Queued"));
        release.TrySetResult();
        await WaitUntil(() => page.Out.Any(m => Is(m, "session.commentary.append", "del_4")), 8000);
        var order = page.Out.Where(m => m.GetProperty("type").GetString() == "session.commentary.append")
            .Select(m => m.GetProperty("delegation_id").GetString()).Distinct().ToList();
        Check("C5 requests run one at a time: the second is told 'Queued' and answered after the first",
            queued && maxRunning == 1 && order.IndexOf("del_3") < order.IndexOf("del_4") && order.IndexOf("del_3") >= 0,
            $"queued={queued} max={maxRunning} order={string.Join(",", order)}");
        Check("C5 each request carries only its own words", asked.Contains("งานช้าหนึ่ง") && asked.Contains("งานที่สอง"), string.Join(" | ", asked));

        int before = asked.Count;
        await page.Event(new { type = "session.delegation.created", delegation = new { id = "del_5", target = "client" } });
        await WaitUntil(() => page.Out.Any(m => Is(m, "session.commentary.append", "del_5")), 4000);
        var unheard = page.Out.FirstOrDefault(m => Is(m, "session.commentary.append", "del_5"));
        Check("C6 a delegation with nothing heard is not run: GPT-Live is asked to have the user say it again",
            asked.Count == before && unheard.ValueKind == JsonValueKind.Object && unheard.GetProperty("content").GetString()!.StartsWith("Failed:"));

        // The delegation can arrive before the last words are transcribed.
        await page.Event(new { type = "session.input_transcript.delta", delta = "รัน test" });
        await page.Event(new { type = "session.delegation.created", delegation = new { id = "del_6", target = "client" } });
        await Task.Delay(120);
        await page.Event(new { type = "session.input_transcript.delta", delta = " ทั้งหมด" });
        await WaitUntil(() => page.Out.Any(m => Is(m, "session.commentary.append", "del_6")), 6000);
        Check("C7 words transcribed just after the delegation are still part of the request",
            asked.Contains("รัน test ทั้งหมด"), string.Join(" | ", asked));
        await page.Event(new { type = "session.delegation.created", delegation = new { id = "del_x", target = "responses", response_id = "resp_1" } });
        await Task.Delay(400);
        Check("C8 a Responses-mode delegation is not CluadeX's to run", !live.DelegationIds.Contains("del_x"));

        await EndLive(live, page);
    }

    // ───────────────────────── D. money: the meter, the cap, hanging up ─────────────────────────

    static async Task TestMeterAndCap(ChromeDictationService bridge, MockLiveApi mock)
    {
        Log("\n## D — the meter, the minutes cap, and hanging up when the window cannot");
        mock.Reset("ok");
        var live = NewLive(bridge, mock, out var page, maxMinutes: 2);
        page.StartPolling();
        await page.Post("/live/offer", new { sdp = Sdp.Offer });
        await page.Event(new { type = "session.usage.updated", usage = new { seconds = 60 }, context_window = new { usage_ratio = 0.1 } });
        Check("D1 the meter follows usage.seconds: 60 s = $0.05", live.Seconds >= 60 && live.CostUsd == 0.05m, $"{live.Seconds} ${live.CostUsd}");
        await page.Event(new { type = "session.usage.updated", usage = new { seconds = 95 } });
        await WaitUntil(() => page.Out.Any(m => m.GetProperty("type").GetString() == "session.instructions.append"), 3000);
        var warn = page.Out.FirstOrDefault(m => m.GetProperty("type").GetString() == "session.instructions.append");
        Check("D2 30 s before the cap GPT-Live is told to warn the user (session-wide: delegation_id null)",
            warn.ValueKind == JsonValueKind.Object && warn.GetProperty("delegation_id").ValueKind == JsonValueKind.Null
            && warn.GetProperty("content").GetString()!.Contains("30 seconds"));
        string? ended = null;
        live.Ended += r => ended = r;
        await page.Event(new { type = "session.usage.updated", usage = new { seconds = 121 } });
        await WaitUntil(() => page.Out.Any(m => m.GetProperty("type").GetString() == "session.close"), 3000);
        Check("D3 at the cap CluadeX sends session.close", page.Out.Any(m => m.GetProperty("type").GetString() == "session.close"));
        // The window confirms: session.closed carries the final usage.
        await page.Event(new { type = "session.closed", reason = "close_requested", usage = new { seconds = 122 } });
        await WaitUntil(() => ended != null, 3000);
        Check("D3 session.closed ends it with the final usage; no hang-up needed",
            ended == "cap" && live.State == "closed" && live.CostUsd == Math.Round(122m / 60m * 0.05m, 4)
            && !mock.Requests.Any(r => r.Path.EndsWith("/hangup")), $"{ended} {live.State} ${live.CostUsd}");
        bridge.EndSession();
        page.StopPolling();

        // The cap with a window that never confirms: CluadeX hangs up itself.
        mock.Reset("ok");
        var capped = NewLive(bridge, mock, out var page2, maxMinutes: 0.3);   // 18 s
        page2.StartPolling();
        await page2.Post("/live/offer", new { sdp = Sdp.Offer });
        string? why = null;
        capped.Ended += r => why = r;
        var sw = Stopwatch.StartNew();
        await WaitUntil(() => why != null, 30000);
        Check("D4 the clock alone closes the session at the cap (no usage events needed)",
            why == "cap" && sw.Elapsed.TotalSeconds is > 15 and < 25, $"{why} after {sw.Elapsed.TotalSeconds:0.0}s");
        var hang = mock.Requests.FirstOrDefault(r => r.Path == "/v1/live/sessions/live_test_1/hangup");
        Check("D4 no session.closed within the grace period → POST /v1/live/sessions/{id}/hangup with the key",
            hang != null && hang.Auth == "Bearer " + Key, hang?.Path ?? "(no hang-up)");
        Check("D4 the cost stops at the cap: about 18 s = $0.015", capped.CostUsd is >= 0.015m and < 0.0175m, $"${capped.CostUsd}");
        bridge.EndSession();
        page2.StopPolling();

        // The window dies (closed, crashed): hung up from CluadeX at once.
        mock.Reset("ok");
        var gone = NewLive(bridge, mock, out var page3);
        page3.StartPolling();
        await page3.Post("/live/offer", new { sdp = Sdp.Offer });
        string? goneWhy = null;
        gone.Ended += r => goneWhy = r;
        await page3.Raw(HttpMethod.Post, "/bye", JsonSerializer.Serialize(new { t = page3.T, s = page3.S }), origin: null);   // sendBeacon
        await WaitUntil(() => goneWhy != null, 5000);
        Check("D5 the window closing (sendBeacon /bye) hangs the session up from CluadeX at once",
            goneWhy == "page-closed" && mock.Requests.Any(r => r.Path.EndsWith("/hangup")), goneWhy ?? "(not ended)");
        page3.StopPolling();

        // The window could not use the answer: it reports it, and the call is hung up rather than left billing.
        mock.Reset("ok");
        var badAnswer = NewLive(bridge, mock, out var page4);
        page4.StartPolling();
        await page4.Post("/live/offer", new { sdp = Sdp.Offer });
        await page4.Post("/event", new { type = "error", detail = "live-answer" });
        await WaitUntil(() => badAnswer.IsEnded, 5000);
        Check("D6 a call the window could not set up is hung up at once (page-error)",
            badAnswer.CloseReason == "page-error" && mock.Requests.Any(r => r.Path.EndsWith("/hangup")), badAnswer.CloseReason);
        bridge.EndSession();
        page4.StopPolling();
    }

    // ───────────────────────── E. reading replies aloud ─────────────────────────

    static void TestSpeechText()
    {
        Log("\n## E — reading replies aloud: what is spoken");
        string md = "## ผลลัพธ์\n\nแก้ **บั๊ก** ใน `Program.cs` แล้ว:\n\n```csharp\nint x = 1;\n```\n\n- เพิ่ม test\n- รัน build ผ่าน\n\n"
            + "| ไฟล์ | สถานะ |\n|---|---|\n| a.cs | ok |\n\nดู [PR](https://github.com/x/y/pull/1) หรือ https://example.com/x\n<think>internal</think>";
        string s = SpeechText.ForSpeech(md, thai: true);
        Check("E1 code blocks are replaced by a short word; inline code keeps its text",
            !s.Contains("int x = 1") && s.Contains("มีโค้ดในแชท") && s.Contains("Program.cs") && !s.Contains('`'), s);
        Check("E1 Markdown marks, link URLs, bare URLs and thinking are not read",
            !s.Contains("**") && !s.Contains("##") && !s.Contains("https://") && s.Contains("PR") && !s.Contains("internal") && !s.Contains('|'), s);
        Check("E1 list items and table rows become short spoken pieces",
            s.Contains("เพิ่ม test.") && s.Contains("a.cs, ok"), s);
        string longText = string.Join(" ", Enumerable.Repeat("This sentence is part of a long reply.", 80));
        string cut = SpeechText.ForSpeech(longText, thai: false);
        Check("E2 a long reply is cut at a sentence end, with a pointer to the chat",
            cut.Length <= SpeechText.DefaultMaxChars + 40 && cut.EndsWith("(more in the chat)") && cut.Contains("reply. …"), $"{cut.Length} chars");
        var chunks = SpeechText.Chunks(longText, 450);
        Check("E3 Chunks splits on sentence ends, never over the limit, and loses nothing",
            chunks.All(c => c.Length <= 450) && string.Join(" ", chunks) == longText.Trim(), chunks.Count.ToString());
        Check("E4 empty or code-only replies give nothing to read", SpeechText.ForSpeech("", true) == ""
            && SpeechText.ForSpeech("```\ncode\n```", false).TrimEnd('.') == "(code in the chat)");
    }

    static async Task TestSpeakBridge(ChromeDictationService bridge)
    {
        Log("\n## F — reading aloud over the bridge (the free path)");
        bridge.Speak("ก่อนเปิดหน้าต่าง", "th-TH");   // no session: ignored
        string url = bridge.BeginSession("th-TH", "th", "google", speak: true);
        var page = new FakePage(url);
        Check("F1 the window is opened with speak=1", url.Contains("speak=1") && !url.Contains("mode=live"));
        using (var d = JsonDocument.Parse(await page.State()))
            Check("F1 a new window has nothing to read (a reply from before it opened is not read)",
                d.RootElement.GetProperty("say").GetProperty("text").GetString() == "");
        bridge.Speak("แก้บั๊กเสร็จแล้วค่ะ", "th-TH");
        long id;
        using (var d = JsonDocument.Parse(await page.State()))
        {
            var say = d.RootElement.GetProperty("say");
            id = say.GetProperty("id").GetInt64();
            Check("F2 a finished reply reaches the window with its language", say.GetProperty("text").GetString() == "แก้บั๊กเสร็จแล้วค่ะ"
                && say.GetProperty("lang").GetString() == "th-TH");
        }
        int hushBefore;
        using (var d = JsonDocument.Parse(await page.State())) hushBefore = d.RootElement.GetProperty("hush").GetInt32();
        bridge.Hush();
        using (var d = JsonDocument.Parse(await page.State()))
            Check("F3 sending a new message hushes it: hush counts up and the text is cleared (it is not read again)",
                d.RootElement.GetProperty("hush").GetInt32() == hushBefore + 1 && d.RootElement.GetProperty("say").GetProperty("text").GetString() == ""
                && d.RootElement.GetProperty("say").GetProperty("id").GetInt64() == id);
        bridge.SendToPage("{\"type\":\"session.close\"}");
        Check("F4 nothing is queued for a data channel in a reading-aloud window", !(await page.State()).Contains("session.close"));
        bridge.EndSession();
    }

    static void TestPageScript(ChromeDictationService bridge)
    {
        Log("\n## G — the page script (static checks)");
        string html = Http.GetStringAsync(bridge.BeginSession("th-TH", "th", "google", speak: true)).Result;
        bridge.EndSession();
        Check("G1 reading aloud uses the browser's own speechSynthesis and prefers a voice of the reply's language",
            html.Contains("speechSynthesis.speak(") && html.Contains("SpeechSynthesisUtterance") && html.Contains("pickVoice"));
        Check("G1 recognition waits while a reply is read (the mic would type the reply into the box)",
            html.Contains("if (closing || !want || busy || talking || LIVE) return;"));
        Check("G2 live: session.close is sent and session.closed awaited before the call is dropped",
            html.Contains("type: 'session.close'") && html.Contains("while (!closedSeen"));
        Check("G2 live: the data-channel events relayed are exactly the bridge's list",
            ChromeDictationService.LiveEventTypes.All(t => html.Contains("'" + t + "'")));
    }

    // ───────────────────────── H. the real page in a real Chrome ─────────────────────────

    /// <summary>The page's own script in headless Google Chrome: a throwaway profile, a fake microphone, an
    /// auto-answered permission prompt — the user's Chrome and microphone are never touched. Proves the page builds a
    /// real WebRTC offer (audio + data channel), hands it to CluadeX (which calls the mock with the key), and reports
    /// back when the answer is unusable so the call is hung up.</summary>
    static async Task TestRealChrome(ChromeDictationService bridge, MockLiveApi mock)
    {
        Log("\n## H — the live page's script in a real (headless) Google Chrome");
        var browser = ChromeDictationService.FindBrowser();
        if (browser is not { Engine: "google" }) { Log("SKIP  H Google Chrome is not installed"); return; }
        mock.Reset("ok");
        var live = NewLive(bridge, mock, out var page);
        string profile = Path.Combine(AppContext.BaseDirectory, "chrome-live-profile-" + Guid.NewGuid().ToString("N")[..6]);
        Process? chrome = null;
        try
        {
            chrome = StartHeadless(browser.Path, profile, page.Url);
            await WaitUntil(() => mock.Requests.Any(r => r.Path == "/v1/live/sessions"), 25000);
            var create = mock.Requests.FirstOrDefault(r => r.Path == "/v1/live/sessions");
            string sdp = "";
            if (create != null)
                using (var d = JsonDocument.Parse(create.Body)) sdp = d.RootElement.GetProperty("transport").GetProperty("sdp").GetString() ?? "";
            Check("H1 real Chrome builds a WebRTC offer with the microphone and the data channel, and CluadeX sends it on",
                sdp.StartsWith("v=0") && sdp.Contains("m=audio") && sdp.Contains("m=application") && create!.Auth == "Bearer " + Key,
                create == null ? "(the page never posted an offer)" : $"{sdp.Length} chars of SDP");
            // The mock's answer is not a real peer: the page must say so, and the call is hung up rather than billed on.
            await WaitUntil(() => live.IsEnded, 15000);
            Check("H2 an answer the browser cannot use: the page reports it and CluadeX hangs the call up",
                live.IsEnded && live.CloseReason == "page-error" && mock.Requests.Any(r => r.Path.EndsWith("/hangup")),
                $"{live.State} {live.CloseReason}");
            bridge.EndSession();
            bool exited = chrome != null && await Task.Run(() => chrome.WaitForExit(20000));
            Log("      headless Chrome closed its window by itself after the session ended: " + (exited ? "yes" : "no (killed by the test)"));
        }
        finally
        {
            await KillChrome(chrome, profile);
        }

        // H3 — reading aloud: the dictation window with speak=1 picks a finished reply up from the bridge and speaks it.
        var events = new ConcurrentQueue<string>();
        void OnEvent(int s, string type, string detail) => events.Enqueue(type + ":" + detail);
        bridge.PageEvent += OnEvent;
        string profile2 = Path.Combine(AppContext.BaseDirectory, "chrome-speak-profile-" + Guid.NewGuid().ToString("N")[..6]);
        Process? chrome2 = null;
        try
        {
            string url = bridge.BeginSession("en-US", "en", "google", speak: true);
            chrome2 = StartHeadless(browser.Path, profile2, url);
            await WaitUntil(() => events.Any(e => e.StartsWith("listening") || e.StartsWith("error")), 20000);
            bridge.Speak("Hello from CluadeX. The build passed.", "en-US");
            bool speaking = await WaitUntil(() => events.Any(e => e.StartsWith("speaking:")), 10000);
            bool spoke = await WaitUntil(() => events.Any(e => e.StartsWith("spoke:") || e == "error:no-tts"), 15000);
            Check("H3 real Chrome reads a reply aloud: the page picks it up from the bridge and starts speechSynthesis",
                speaking && !events.Contains("error:no-tts"), string.Join(" | ", events));
            Log("      the page then said it finished speaking: " + (spoke ? "yes" : "no (headless Chrome may have no audio output)"));
            bridge.EndSession();
        }
        finally
        {
            bridge.PageEvent -= OnEvent;
            await KillChrome(chrome2, profile2);
        }
    }

    static Process? StartHeadless(string chromePath, string profile, string url)
    {
        var psi = new ProcessStartInfo(chromePath) { UseShellExecute = false };
        foreach (var a in new[] { "--headless=new", "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                                  "--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream",
                                  "--autoplay-policy=no-user-gesture-required", url })
            psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    static async Task KillChrome(Process? chrome, string profile)
    {
        try { if (chrome is { HasExited: false }) chrome.Kill(entireProcessTree: true); } catch { }
        chrome?.Dispose();
        for (int i = 0; i < 10 && Directory.Exists(profile); i++)
        {
            try { Directory.Delete(profile, true); } catch { await Task.Delay(500); }
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    static LiveVoiceSession NewLive(ChromeDictationService bridge, MockLiveApi mock, out FakePage page, double maxMinutes = 10,
        Func<string, CancellationToken, Task<LiveTaskResult>>? run = null)
    {
        var live = new LiveVoiceSession(Http, mock.Base, Key, new LiveVoiceOptions { MaxMinutes = maxMinutes, Thai = true });
        live.Send = bridge.SendToPage;
        live.RunTask = run;
        string url = bridge.BeginSession("th-TH", "th", "google", live: true) + "&cap=" + maxMinutes;
        int s = bridge.Session;
        CurLive = live;
        CurSession = s;
        bridge.LiveOfferHandler = (sess, sdp, ct) => sess == s ? live.CreateAsync(sdp, ct) : Task.FromResult(new LiveOfferResult(null, null, "ended"));
        page = new FakePage(url);
        return live;
    }

    static async Task EndLive(LiveVoiceSession live, FakePage page)
    {
        live.Close("user");
        await WaitUntil(() => page.Out.Any(m => m.GetProperty("type").GetString() == "session.close"), 3000);
        await page.Event(new { type = "session.closed", reason = "close_requested", usage = new { seconds = 20 } });
        await WaitUntil(() => live.IsEnded, 3000);
        page.StopPolling();
        CurLive = null;
    }

    static bool Is(JsonElement m, string type, string delegationId) =>
        m.GetProperty("type").GetString() == type && m.TryGetProperty("delegation_id", out var d)
        && d.ValueKind == JsonValueKind.String && d.GetString() == delegationId;

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }

    static string Trim(string? s) => s == null ? "" : s.Length > 160 ? s[..160] + "…" : s;

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++; else _fail++;
        Log($"{(ok ? "PASS" : "FAIL")}  {name}" + (!ok && detail != null ? $"\n      → {detail}" : ""));
    }

    static void Log(string line)
    {
        Console.WriteLine(line);
        Report.AppendLine(line);
    }
}

/// <summary>A WebRTC offer shaped like Chrome's (audio + the data channel).</summary>
internal static class Sdp
{
    public const string Offer = "v=0\r\no=- 4611731400430051336 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0 1\r\n"
        + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sendrecv\r\na=rtpmap:111 opus/48000/2\r\n"
        + "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na=mid:1\r\na=sctp-port:5000\r\n";
}

/// <summary>The mic page, played over HTTP exactly as its script does it: token and origin on every POST, the long
/// poll on /state with the data-channel acknowledgement.</summary>
internal sealed class FakePage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    public string Url { get; }
    public string Origin { get; }
    public string T { get; }
    public int S { get; }
    public ConcurrentQueue<JsonElement> OutQ { get; } = new();
    public List<JsonElement> Out => OutQ.ToList();
    public long LastOut;
    private CancellationTokenSource? _poll;

    public FakePage(string url)
    {
        Url = url;
        var u = new Uri(url);
        Origin = $"{u.Scheme}://{u.Authority}";
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        T = q["t"]!;
        S = int.Parse(q["s"]!);
    }

    public record Resp(int Code, string Body);

    public Task<Resp> Post(string path, object body, string? origin = "self", string? token = null)
    {
        var json = JsonSerializer.SerializeToElement(body);
        var dict = new Dictionary<string, object?> { ["t"] = token ?? T, ["s"] = S };
        foreach (var p in json.EnumerateObject()) dict[p.Name] = p.Value;
        return Raw(HttpMethod.Post, path, JsonSerializer.Serialize(dict), origin == "self" ? Origin : origin);
    }

    public Task<Resp> Event(object ev) => Post("/live/event", new { ev });

    public async Task<Resp> Raw(HttpMethod method, string path, string body, string? origin, string? host = null)
    {
        var req = new HttpRequestMessage(method, Origin + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (origin != null) req.Headers.Add("Origin", origin);
        if (host != null) req.Headers.Host = host;
        var resp = await Http.SendAsync(req);
        return new Resp((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>One immediate /state read (since=-1). <paramref name="ack"/> false: no out= acknowledgement.</summary>
    public Task<string> State(bool ack = true) =>
        Http.GetStringAsync($"{Origin}/state?t={T}&s={S}&since=-1" + (ack ? $"&out={LastOut}" : ""));

    /// <summary>The page's watch() loop: held polls, collecting what CluadeX queues for the data channel.</summary>
    public void StartPolling()
    {
        _poll = new CancellationTokenSource();
        var ct = _poll.Token;
        _ = Task.Run(async () =>
        {
            long since = -1;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string raw = await Http.GetStringAsync($"{Origin}/state?t={T}&s={S}&since={since}&out={LastOut}", ct);
                    using var d = JsonDocument.Parse(raw);
                    var r = d.RootElement;
                    if (!r.GetProperty("active").GetBoolean()) return;
                    since = r.GetProperty("v").GetInt64();
                    foreach (var m in r.GetProperty("out").EnumerateArray())
                    {
                        long n = m.GetProperty("n").GetInt64();
                        if (n <= LastOut) continue;
                        using var msg = JsonDocument.Parse(m.GetProperty("m").GetString()!);
                        OutQ.Enqueue(msg.RootElement.Clone());
                        LastOut = n;
                    }
                }
                catch { if (!ct.IsCancellationRequested) await Task.Delay(200); }
            }
        });
    }

    public void StopPolling() => _poll?.Cancel();
}

/// <summary>A localhost stand-in for OpenAI's Live API: POST /v1/live/sessions (201 with an answer SDP, or an
/// error) and POST /v1/live/sessions/{id}/hangup. Records every request.</summary>
internal sealed class MockLiveApi : IDisposable
{
    public const string AnswerSdp = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n";
    public sealed record Req(string Method, string Path, string Auth, string ContentType, string Body, string Host);

    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<Req> _requests = new();
    private volatile string _mode = "ok";
    public string Base { get; }
    public List<Req> Requests => _requests.ToList();

    public MockLiveApi()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        Base = $"http://localhost:{port}/v1";
        _ = Task.Run(Loop);
    }

    public void Reset(string mode)
    {
        _mode = mode;
        _requests.Clear();
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        string body;
        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
        string path = ctx.Request.Url!.AbsolutePath;
        _requests.Enqueue(new Req(ctx.Request.HttpMethod, path, ctx.Request.Headers["Authorization"] ?? "",
            ctx.Request.ContentType ?? "", body, ctx.Request.Url.Host));
        int code;
        string reply;
        if (path.EndsWith("/hangup")) { code = 200; reply = "{}"; }
        else if (path == "/v1/live/sessions" && _mode == "ok")
        {
            code = 201;
            reply = JsonSerializer.Serialize(new
            {
                session = new { id = "live_test_1", @object = "live.session", model = "gpt-live-1" },
                transport = new { type = "webrtc", sdp = AnswerSdp },
            });
        }
        else
        {
            code = int.TryParse(_mode, out int c) ? c : 404;
            string msg = code switch
            {
                401 => "Incorrect API key provided: sk-test-****789.",
                403 => "Your organization does not have access to gpt-live-1 (the free tier is not supported).",
                429 => "Rate limit reached for concurrent live sessions.",
                _ => "not found",
            };
            reply = JsonSerializer.Serialize(new { error = new { message = msg, type = "invalid_request_error", code = (string?)null } });
        }
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(reply);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
