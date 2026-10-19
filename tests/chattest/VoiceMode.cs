using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>VM — voice mode in the chat itself (Settings › Voice, off by default). Reading replies aloud: a finished
    /// reply reaches the mic window as speakable text. Live voice: the Live button, the OpenAI key check, and a GPT-Live
    /// call whose spoken request runs as a message of this chat through the agent and goes back as commentary — against
    /// a localhost Live API mock with a fake key (no money), the window played over the real bridge.</summary>
    static async Task TestVoiceMode()
    {
        Log("\n## VM — voice mode: replies read aloud, a Live call whose requests run through this chat");
        await FreshChat();
        const string key = "sk-test-chattest-live-0000";
        string? url = null;
        Vm.DictationLauncher = u => { url = u; return true; };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var mock = new LiveMock();
        var pollStop = new CancellationTokenSource();
        try
        {
            // ── reading replies aloud ──
            Settings.UpdateSettings(s => s.VoiceSpeakReplies = true);
            Vm.UserInput = "";
            Vm.ToggleDictationCommand.Execute(null);
            Check("VM1 with Read replies aloud on, the mic window opens with speak=1", Vm.IsDictating && url?.Contains("speak=1") == true, url ?? "");
            var (origin, t, s) = Parse(url!);
            Fake.ScriptFor["[SAY]"] = new Queue<FakeStep>([FakeStep.Reply("Fixed it in **Program.cs**.\n\n```cs\nint x = 1;\n```")]);
            Vm.UserInput = "[SAY] fix it";
            Vm.SendMessageCommand.Execute(null);
            string sayText = "";
            await WaitUntil(() =>
            {
                if (Vm.IsGenerating) return false;
                using var d = JsonDocument.Parse(http.GetStringAsync($"{origin}/state?t={t}&s={s}&since=-1").Result);
                sayText = d.RootElement.GetProperty("say").GetProperty("text").GetString() ?? "";
                return sayText.Length > 0;
            }, 10000);
            Check("VM1 the finished reply reaches the mic window as speakable text (no code, no Markdown)",
                sayText.Contains("Fixed it in Program.cs") && !sayText.Contains("int x") && !sayText.Contains("**"), sayText);
            Vm.ToggleDictationCommand.Execute(null);
            Settings.UpdateSettings(s => s.VoiceSpeakReplies = false);

            // ── Live voice ──
            Settings.UpdateSettings(s => { s.VoiceLiveEnabled = false; s.ProviderConfigs.Remove("OpenAI"); });
            await WaitUntil(() => !Vm.ShowLiveButton, 3000);
            Check("VM2 the Live button is hidden while Settings › Voice › Live voice is off (the default)", !Vm.ShowLiveButton);
            Settings.UpdateSettings(s => s.VoiceLiveEnabled = true);
            await WaitUntil(() => Vm.ShowLiveButton, 3000);
            url = null;
            Vm.ToggleLiveVoiceCommand.Execute(null);
            Check("VM2 turned on but no OpenAI key: no window opens, the status says where to add one",
                Vm.ShowLiveButton && !Vm.IsLiveVoice && url == null && Vm.StatusText == L("chat.live.noKey"), Vm.StatusText);

            Settings.UpdateSettings(s => s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = key, BaseUrl = mock.Base });
            Vm.ToggleLiveVoiceCommand.Execute(null);
            Check("VM3 Live opens the window in live mode with the minutes cap; the key is not in its URL",
                Vm.IsLiveVoice && url?.Contains("mode=live") == true && url.Contains("cap=10") && !url.Contains(key), url ?? "");
            (origin, t, s) = Parse(url!);

            async Task<(int Code, string Body)> Post(string path, object body)
            {
                var json = JsonSerializer.SerializeToElement(body);
                var dict = new Dictionary<string, object?> { ["t"] = t, ["s"] = s };
                foreach (var p in json.EnumerateObject()) dict[p.Name] = p.Value;
                var req = new HttpRequestMessage(HttpMethod.Post, origin + path)
                {
                    Content = new StringContent(JsonSerializer.Serialize(dict), Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("Origin", origin);
                var resp = await http.SendAsync(req);
                return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
            }

            // The window's long poll, collecting what CluadeX queues for the data channel.
            var outbox = new ConcurrentQueue<JsonElement>();
            long lastOut = 0;
            _ = Task.Run(async () =>
            {
                long since = -1;
                while (!pollStop.IsCancellationRequested)
                {
                    try
                    {
                        string raw = await http.GetStringAsync($"{origin}/state?t={t}&s={s}&since={since}&out={lastOut}", pollStop.Token);
                        using var d = JsonDocument.Parse(raw);
                        if (!d.RootElement.GetProperty("active").GetBoolean()) return;
                        since = d.RootElement.GetProperty("v").GetInt64();
                        foreach (var m in d.RootElement.GetProperty("out").EnumerateArray())
                        {
                            long n = m.GetProperty("n").GetInt64();
                            if (n <= lastOut) continue;
                            using var msg = JsonDocument.Parse(m.GetProperty("m").GetString()!);
                            outbox.Enqueue(msg.RootElement.Clone());
                            lastOut = n;
                        }
                    }
                    catch { await Task.Delay(200); }
                }
            });

            var offer = await Post("/live/offer", new { sdp = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n" });
            Check("VM3 the window's offer: CluadeX creates the session with the key from Settings and returns only the answer",
                offer.Code == 200 && !offer.Body.Contains(key) && mock.Creates == 1 && mock.LastAuth == "Bearer " + key, $"{offer.Code} creates={mock.Creates}");

            Fake.ScriptFor["[LIVE]"] = new Queue<FakeStep>([FakeStep.Reply("README อธิบายวิธี build ค่ะ")]);
            int usersBefore = Vm.Messages.Count(m => m.Role == MessageRole.User);
            await Post("/live/event", new { ev = new { type = "session.started" } });
            await Post("/live/event", new { ev = new { type = "session.input_transcript.delta", delta = "[LIVE] สรุป README" } });
            await Post("/live/event", new { ev = new { type = "session.delegation.created", delegation = new { id = "del_vm", type = "delegation", target = "client" } } });
            await WaitUntil(() => outbox.Any(m => m.GetProperty("type").GetString() == "session.commentary.append"), 15000);
            Check("VM4 the spoken request ran as a message of this chat, through the agent on the active provider",
                Vm.Messages.Count(m => m.Role == MessageRole.User) == usersBefore + 1
                && Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == "[LIVE] สรุป README")
                && Vm.Messages.Any(m => m.Role == MessageRole.Assistant && (m.Content ?? "").Contains("README อธิบายวิธี build")));
            var back = outbox.FirstOrDefault(m => m.GetProperty("type").GetString() == "session.commentary.append");
            Check("VM4 its answer went back to GPT-Live as commentary for that delegation",
                back.ValueKind == JsonValueKind.Object && back.GetProperty("delegation_id").GetString() == "del_vm"
                && back.GetProperty("content").GetString()!.StartsWith("Completed: README"), back.ToString());
            Check("VM4 the user's own draft in the box was left alone", Vm.UserInput == "");
            await WaitUntil(() => Vm.LiveVoiceMeter.Contains('$'), 3000);
            Check("VM5 the meter shows the call's cost while it runs (15 s minimum = $0.0125)",
                Vm.LiveVoiceMeter.Contains("$0.01"), Vm.LiveVoiceMeter);

            Vm.ToggleLiveVoiceCommand.Execute(null);
            await WaitUntil(() => outbox.Any(m => m.GetProperty("type").GetString() == "session.close"), 5000);
            await Post("/live/event", new { ev = new { type = "session.closed", reason = "close_requested", usage = new { seconds = 20 } } });
            await WaitUntil(() => !Vm.IsLiveVoice, 5000);
            Check("VM6 hanging up: session.close, then the call ends and the status line says what it cost",
                !Vm.IsLiveVoice && Vm.StatusText.StartsWith(L("chat.live.ended").Split('{')[0]) && Vm.StatusText.Contains("0.017")
                && mock.Hangups == 0, Vm.StatusText);
        }
        finally
        {
            pollStop.Cancel();
            if (Vm.IsLiveVoice) Vm.StopLiveVoice();
            if (Vm.IsDictating) Vm.ToggleDictationCommand.Execute(null);
            Vm.DictationLauncher = null;
            Vm.UserInput = "";
            Settings.UpdateSettings(s => { s.VoiceLiveEnabled = false; s.VoiceSpeakReplies = false; s.ProviderConfigs.Remove("OpenAI"); });
        }
    }

    static (string Origin, string T, int S) Parse(string url)
    {
        var u = new Uri(url);
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        return ($"{u.Scheme}://{u.Authority}", q["t"]!, int.Parse(q["s"]!));
    }

    /// <summary>A localhost stand-in for POST /v1/live/sessions (201 + an answer SDP) and /hangup.</summary>
    sealed class LiveMock : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Base { get; }
        public int Creates, Hangups;
        public string LastAuth = "";

        public LiveMock()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Base = $"http://localhost:{port}/v1";
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    string path = ctx.Request.Url!.AbsolutePath;
                    LastAuth = ctx.Request.Headers["Authorization"] ?? "";
                    string reply = "{}";
                    if (path.EndsWith("/hangup")) Interlocked.Increment(ref Hangups);
                    else if (path == "/v1/live/sessions")
                    {
                        Interlocked.Increment(ref Creates);
                        ctx.Response.StatusCode = 201;
                        reply = "{\"session\":{\"id\":\"live_chat_1\"},\"transport\":{\"type\":\"webrtc\",\"sdp\":\"v=0\\r\\n\"}}";
                    }
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            _listener.Close();
        }
    }
}
