// Settings › Voice (2026-11-09): replies read aloud by ElevenLabs, OpenAI (the shared AI Provider key) or the free
// voices, each girl in her own voice and mood. Offline against a stand-in ElevenLabs / OpenAI server, the mic window's
// audio channel on the real loopback bridge, and the key's encryption on disk. With ELEVENLABS_API_KEY set it also
// makes one short real ElevenLabs call.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Voice;
using CluadeX.Services.Voice.Tts;

namespace ApiTest;

internal static class VoiceWire
{
    static readonly List<(string Method, string Path, string Body, string Key, string Auth)> Calls = new();
    static readonly byte[] FakeMp3 = [0x49, 0x44, 0x33, 4, 0, 0, 1, 2, 3, 4, 5, 6];
    const string Key = "sk_test_eleven_1234567890";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Voice — ElevenLabs / OpenAI / free voices, each girl in her own voice");

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        string elBase = $"http://localhost:{port}/el";
        string oaBase = $"http://localhost:{port}/oa/v1";

        // ─── ElevenLabs: models and what they can do ───
        var el = new ElevenLabsClient(Key, elBase);
        var models = await el.ModelsAsync();
        check("voice", "models: speech models first, newest family first, the unknown future one kept",
            models.Count == 5 && models[0].Id == "eleven_v4" && models[1].Id == "eleven_v3" && models.Any(m => m.Id == "eleven_v9_future")
            && !models.Last().CanTts, string.Join(",", models.Select(m => m.Id)));
        var v3 = models.First(m => m.Id == "eleven_v3");
        var ml2 = models.First(m => m.Id == "eleven_multilingual_v2");
        check("voice", "capabilities read: v3 Thai + audio tags + 3-step stability, no style; multilingual v2 style + boost, no Thai",
            v3.Thai && v3.AudioTags && v3.StabilitySteps && !v3.CanUseStyle && v3.MaxChars == 5000
            && !ml2.Thai && ml2.CanUseStyle && ml2.CanUseSpeakerBoost && !ml2.AudioTags, "");
        check("voice", "the key goes in xi-api-key, nowhere else", Calls.Any(c => c.Path == "/el/v1/models" && c.Key == Key && c.Auth == ""), "");

        // ─── voices, every page ───
        var voices = await el.VoicesAsync();
        check("voice", "voices: two pages joined (next_page_token followed)", voices.Count == 3 && voices.Any(v => v.Id == "v_clone"),
            string.Join(",", voices.Select(v => v.Id)));
        check("voice", "a cloned voice is the user's own (deletable); premade is not",
            voices.First(v => v.Id == "v_clone").IsOwn && !voices.First(v => v.Id == "v_pre").IsOwn
            && voices.First(v => v.Id == "v_pre").Languages.Contains("th") && voices.First(v => v.Id == "v_pre").Labels.Contains("young"), "");

        var account = await el.AccountAsync();
        check("voice", "account: tier, usage, clone permission, reset date",
            account.Tier == "creator" && account.CharactersUsed == 1234 && account.CharacterLimit == 100000 && account.CanClone == true
            && account.ResetsUtc?.Year == 2026, $"{account.Tier} {account.CharactersUsed}/{account.CharacterLimit}");

        // ─── one reply, CluadeX's girl on eleven_v3 ───
        VoiceSpeaker.FetchedModels = models.Where(m => m.CanTts).ToList();
        var settings = new AppSettings
        {
            Voice = new VoiceSettings
            {
                Engine = VoiceSettings.ElevenLabs, ElevenLabsApiKey = Key, ElevenLabsModel = "eleven_v3", ElevenLabsVoiceId = "v_main",
                Stability = 0.5, Similarity = 0.8, Style = 0.3, Speed = 1.0, PersonaVoices = true, AudioTags = true,
                Characters = { ["cluadex"] = new CharacterVoice { ElevenLabsVoiceId = "v_clone" } },
            },
        };
        var speaker = new VoiceSpeaker(() => settings, elBase);
        Calls.Clear();
        var audio = await speaker.SynthesizeAsync("สวัสดีค่ะ", thai: true, girl: "cluadex");
        var tts = Calls.Single(c => c.Path.StartsWith("/el/v1/text-to-speech/"));
        using (var doc = JsonDocument.Parse(tts.Body))
        {
            var b = doc.RootElement;
            var vs = b.GetProperty("voice_settings");
            check("voice", "CluadeX speaks in her own voice (her cloned one), soft: [softly] first, Thai language code",
                tts.Path.StartsWith("/el/v1/text-to-speech/v_clone") && b.GetProperty("text").GetString() == "[softly] สวัสดีค่ะ"
                && b.GetProperty("model_id").GetString() == "eleven_v3" && b.GetProperty("language_code").GetString() == "th", tts.Body);
            check("voice", "v3: stability snapped to a step (0.5+0.1 → Natural 0.5), no style / boost sent, speed × her 0.92",
                vs.GetProperty("stability").GetDouble() == 0.5 && !vs.TryGetProperty("style", out _) && !vs.TryGetProperty("use_speaker_boost", out _)
                && Math.Abs(vs.GetProperty("speed").GetDouble() - 0.92) < 0.001 && Math.Abs(vs.GetProperty("similarity_boost").GetDouble() - 0.8) < 0.001,
                vs.ToString());
        }
        check("voice", "the audio comes back as mp3", audio.Mime == "audio/mpeg" && audio.Bytes.SequenceEqual(FakeMp3), audio.Mime);

        // Grok on multilingual v2: style + boost sent (with her shift), no language_code, no audio tag.
        settings.Voice.ElevenLabsModel = "eleven_multilingual_v2";
        var (gText, gVoice, gModel, gOpt) = VoiceSpeaker.ElevenRequest(settings.Voice, "hey boss", false, "grok");
        check("voice", "Grok on multilingual v2: main voice, style 0.3+0.5, boost on, no tag, no language code",
            gVoice == "v_main" && gText == "hey boss" && gOpt.SendStyle && gOpt.SendBoost && Math.Abs(gOpt.Style - 0.8) < 0.001
            && gOpt.LanguageCode == null && Math.Abs(gOpt.Stability - 0.35) < 0.001, $"{gText} {gOpt}");
        settings.Voice.PersonaVoices = false;
        var (nText, nVoice, _, nOpt) = VoiceSpeaker.ElevenRequest(settings.Voice, "hey", false, "grok");
        check("voice", "persona off: everyone uses the main voice and the sliders as they are",
            nVoice == "v_main" && Math.Abs(nOpt.Style - 0.3) < 0.001 && Math.Abs(nOpt.Speed - 1) < 0.001 && nText == "hey", "");
        settings.Voice.PersonaVoices = true;
        check("voice", "retired model ids read as their successors; a new model only gets the safe fields",
            ElevenLabsClient.Successor("eleven_turbo_v2_5") == "eleven_flash_v2_5" && ElevenLabsClient.Successor("") == "eleven_v3"
            && VoiceSpeaker.ModelInfo("eleven_v9_new") is { CanUseStyle: false, CanUseSpeakerBoost: false }, "");
        // The built-in list is what a user without a key sees: every model must have its languages (a static-order slip
        // once left them null, and Settings › Voice crashed on open).
        var known = ElevenLabsClient.KnownModels;
        check("voice", "the built-in model list knows each model's languages (v3/v4 speak Thai, v2 does not)",
            known.All(m => m.Languages is { Count: > 0 }) && known.Single(m => m.Id == "eleven_v3").Thai
            && known.Single(m => m.Id == "eleven_v4").Thai && !known.Single(m => m.Id == "eleven_multilingual_v2").Thai,
            string.Join(",", known.Where(m => m.Languages == null).Select(m => m.Id)));
        var longText = new string('ก', 6000);
        settings.Voice.ElevenLabsModel = "eleven_v3";
        check("voice", "text is cut to the model's own limit", VoiceSpeaker.ElevenRequest(settings.Voice, longText, true, null).Text.Length == 5000, "");

        // ─── errors: the API's own words, never the key ───
        var bad = new ElevenLabsClient("bad-key", elBase);
        string err = "";
        try { await bad.ModelsAsync(); } catch (ElevenLabsException ex) { err = ex.Message; }
        check("voice", "a refused key shows ElevenLabs' message (HTTP 401), not the key", err.Contains("401") && err.Contains("Invalid API key") && !err.Contains("bad-key"), err);
        settings.Voice.ElevenLabsApiKey = "";
        bool missing = false;
        try { await speaker.SynthesizeAsync("x", false, null); } catch (VoiceKeyMissingException) { missing = true; }
        check("voice", "no key → a 'no key' error, nothing sent", missing, "");
        settings.Voice.ElevenLabsApiKey = Key;

        // ─── cloning ───
        string sample = Path.Combine(Path.GetTempPath(), $"cx-voice-sample-{Environment.ProcessId}.mp3");
        File.WriteAllBytes(sample, FakeMp3);
        try
        {
            Calls.Clear();
            string id = await el.CloneAsync("เสียงฉัน", [sample], "test", removeNoise: true);
            var add = Calls.Single(c => c.Path == "/el/v1/voices/add");
            add.Body = add.Body.Replace("\"", "").Replace("; ", "=");
            check("voice", "instant clone: name, noise removal and the sample uploaded, new voice id back",
                id == "v_new" && add.Body.Contains("name=name") && add.Body.Contains("เสียงฉัน") && add.Body.Contains("remove_background_noise")
                && add.Body.Contains(Path.GetFileName(sample)), id);
            await el.DeleteVoiceAsync("v_new");
            check("voice", "delete goes to DELETE /v1/voices/{id}", Calls.Any(c => c.Method == "DELETE" && c.Path == "/el/v1/voices/v_new"), "");
        }
        finally { try { File.Delete(sample); } catch { } }

        // ─── OpenAI with the AI Provider key ───
        settings.Voice.Engine = VoiceSettings.OpenAI;
        settings.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-openai-test", BaseUrl = oaBase };
        settings.Voice.OpenAiModel = "gpt-4o-mini-tts";
        settings.Voice.OpenAiVoice = "marin";
        settings.Voice.OpenAiInstructions = "Thai accent.";
        Calls.Clear();
        var oaAudio = await speaker.SynthesizeAsync("hello", false, "codex");
        var sp = Calls.Single(c => c.Path == "/oa/v1/audio/speech");
        using (var doc = JsonDocument.Parse(sp.Body))
        {
            var b = doc.RootElement;
            check("voice", "OpenAI: the AI Provider key (Bearer), Codex's cool instructions + the user's, marin",
                sp.Auth == "Bearer sk-openai-test" && b.GetProperty("voice").GetString() == "marin"
                && b.GetProperty("instructions").GetString()!.StartsWith("Speak cool") && b.GetProperty("instructions").GetString()!.EndsWith("Thai accent.")
                && oaAudio.Mime == "audio/mpeg", sp.Body);
        }
        settings.Voice.OpenAiModel = "tts-1";
        var (m1, v1, s1, i1) = VoiceSpeaker.OpenAiRequest(settings.Voice, "grok");
        check("voice", "tts-1: marin is not offered → nova; Grok's mood is her speed (×1.1)",
            m1 == "tts-1" && v1 == "nova" && Math.Abs(s1 - 1.1) < 0.001, $"{v1} {s1}");
        var oaModels = await new OpenAiTtsClient("sk-openai-test", oaBase).ModelsAsync();
        check("voice", "OpenAI speech models from /models (tts only, gpt-* first)", oaModels.SequenceEqual(new[] { "gpt-4o-mini-tts", "tts-1-hd", "tts-1" }),
            string.Join(",", oaModels));

        // ─── the free voices ───
        settings.Voice.Engine = VoiceSettings.Browser;
        settings.Voice.BrowserRate = 1.0; settings.Voice.BrowserPitch = 1.0;
        settings.Voice.Characters["cluadex"].BrowserVoice = "Microsoft Premwadee Online (Natural) - Thai (Thailand) | th-TH";
        var bs = speaker.BrowserStyle("cluadex");
        check("voice", "browser voice: CluadeX's own voice, slower and higher (soft)",
            bs.Voice!.StartsWith("Microsoft Premwadee") && bs.Rate < 1 && bs.Pitch > 1, $"{bs}");
        check("voice", "PCM from ElevenLabs is wrapped as a WAV", VoiceSpeaker.WavOfPcm(new byte[100], 24000) is { Length: 144 } w
            && Encoding.ASCII.GetString(w, 0, 4) == "RIFF" && BitConverter.ToInt32(w, 24) == 24000, "");
        var winVoices = WindowsTts.Voices();
        if (winVoices.Count > 0)
        {
            var wav = await WindowsTts.SpeakAsync("Hello", "en-US", null, 0);
            check("voice", "Windows voice (offline) speaks into a WAV", wav.Length > 1000 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF", $"{wav.Length} B, {winVoices.Count} voices");
        }
        check("voice", "every girl with her own sound has a style; the secretary answers to both names",
            VoicePersona.Girls.All(g => VoicePersona.For(g) != VoicePersona.Neutral) && VoicePersona.For("mind").Girl == "mai"
            && VoicePersona.For("nobody") == VoicePersona.Neutral, "");

        await MicWindow(check);
        Keys(check);
        await Live(check);
        listener.Stop();
    }

    /// <summary>The mic window gets the audio at /audio, its own voice, rate and pitch in /state, and reports its voices.</summary>
    static async Task MicWindow(Action<string, string, bool, string> check)
    {
        using var bridge = ChromeDictationService.TryStart(out var error);
        if (bridge == null) { check("voice", "mic bridge starts", false, error ?? "?"); return; }
        string url = bridge.BeginSession("th-TH", "th", "edge", speak: true);
        int session = bridge.Session;
        using var http = new HttpClient();
        string State() => http.GetStringAsync($"{bridge.Origin}/state?t={bridge.Token}&s={session}&since=-1").Result;

        bridge.Speak("สวัสดี", "th-TH", "Premwadee | th-TH", 0.9, 1.2);
        using (var st = JsonDocument.Parse(State()))
        {
            var say = st.RootElement.GetProperty("say");
            check("voice", "browser speech carries the voice, rate and pitch to the page",
                !say.GetProperty("audio").GetBoolean() && say.GetProperty("voice").GetString() == "Premwadee | th-TH"
                && Math.Abs(say.GetProperty("rate").GetDouble() - 0.9) < 0.001 && Math.Abs(say.GetProperty("pitch").GetDouble() - 1.2) < 0.001, say.ToString());
        }
        check("voice", "SpeakAudio hands over the audio", bridge.SpeakAudio(FakeMp3, "audio/mpeg", "สวัสดี", "th-TH"), "");
        long id;
        using (var st = JsonDocument.Parse(State()))
        {
            var say = st.RootElement.GetProperty("say");
            id = say.GetProperty("id").GetInt64();
            check("voice", "state says this reply is audio", say.GetProperty("audio").GetBoolean(), say.ToString());
        }
        var resp = await http.GetAsync($"{bridge.Origin}/audio?t={bridge.Token}&id={id}");
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        check("voice", "/audio serves the bytes with their type", resp.IsSuccessStatusCode && bytes.SequenceEqual(FakeMp3)
            && resp.Content.Headers.ContentType?.MediaType == "audio/mpeg", $"{(int)resp.StatusCode}");
        var wrongToken = await http.GetAsync($"{bridge.Origin}/audio?t=nope&id={id}");
        var oldId = await http.GetAsync($"{bridge.Origin}/audio?t={bridge.Token}&id={id - 1}");
        check("voice", "/audio refuses a wrong token and an old reply", !wrongToken.IsSuccessStatusCode && !oldId.IsSuccessStatusCode,
            $"{(int)wrongToken.StatusCode} {(int)oldId.StatusCode}");
        bridge.Hush();
        var hushed = await http.GetAsync($"{bridge.Origin}/audio?t={bridge.Token}&id={id}");
        check("voice", "after Hush the audio is gone", !hushed.IsSuccessStatusCode, $"{(int)hushed.StatusCode}");
        check("voice", "audio over the limit is refused", !bridge.SpeakAudio(new byte[ChromeDictationService.MaxAudioBytes + 1], "audio/mpeg", "x", "en-US"), "");

        var page = await http.GetAsync(url);
        string csp = page.Headers.TryGetValues("Content-Security-Policy", out var v) ? string.Join(";", v) : "";
        string html = await page.Content.ReadAsStringAsync();
        check("voice", "the page may play audio from its own origin only, and has the player",
            csp.Contains("media-src 'self'") && csp.Contains("default-src 'none'") && html.Contains("function playAudio") && html.Contains("type: 'voices'"), csp);

        IReadOnlyList<string>? reported = null;
        bridge.BrowserVoicesChanged += n => reported = n;
        var post = new HttpRequestMessage(HttpMethod.Post, $"{bridge.Origin}/event")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { t = bridge.Token, s = session, type = "voices",
                detail = "Microsoft Premwadee Online (Natural) - Thai (Thailand) | th-TH\nGoogle US English | en-US\n" }), Encoding.UTF8, "application/json"),
        };
        post.Headers.TryAddWithoutValidation("Origin", bridge.Origin);
        var pr = await http.SendAsync(post);
        check("voice", "the page's voice list reaches CluadeX", pr.IsSuccessStatusCode && reported?.Count == 2 && bridge.BrowserVoices.Count == 2,
            $"{(int)pr.StatusCode} {reported?.Count}");
        bridge.EndSession();
    }

    /// <summary>The ElevenLabs key is encrypted on disk like the provider keys.</summary>
    static void Keys(Action<string, string, bool, string> check)
    {
        var svc = new SettingsService();   // portable — never the user's settings
        const string secret = "sk_eleven_secret_for_disk_test";
        svc.UpdateSettings(s => s.Voice.ElevenLabsApiKey = secret);
        string json = File.ReadAllText(Path.Combine(svc.DataRoot, "settings.json"));
        var again = new SettingsService();
        check("voice", "the ElevenLabs key is DPAPI-encrypted in settings.json and reads back",
            !json.Contains(secret) && json.Contains("\"ElevenLabsApiKey\": \"ENC:") && again.Settings.Voice.ElevenLabsApiKey == secret, "");
        svc.UpdateSettings(s => s.Voice.ElevenLabsApiKey = null);
    }

    /// <summary>One short real call when the owner put a key in ELEVENLABS_API_KEY.</summary>
    static async Task Live(Action<string, string, bool, string> check)
    {
        string? key = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        var el = new ElevenLabsClient(key);
        var models = await el.ModelsAsync();
        check("voice-live", "ElevenLabs lists real models", models.Any(m => m.CanTts), string.Join(",", models.Select(m => m.Id).Take(8)));
        var s = new AppSettings { Voice = new VoiceSettings { Engine = VoiceSettings.ElevenLabs, ElevenLabsApiKey = key, ElevenLabsModel = "eleven_v3" } };
        var audio = await new VoiceSpeaker(() => s).SynthesizeAsync("สวัสดีค่ะ", true, "cluadex");
        check("voice-live", "ElevenLabs speaks Thai (eleven_v3)", audio.Bytes.Length > 1000, $"{audio.Bytes.Length} B");
    }

    // ─── the stand-in server ───

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            try { Handle(ctx); } catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string path = req.Url!.AbsolutePath;
        string body;
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
        string key = req.Headers["xi-api-key"] ?? "";
        lock (Calls) Calls.Add((req.HttpMethod, path, body, key, req.Headers["Authorization"] ?? ""));

        if (path.StartsWith("/el/"))
        {
            if (key != Key) { Json(ctx, 401, "{\"detail\":{\"status\":\"invalid_api_key\",\"message\":\"Invalid API key\"}}"); return; }
            if (path == "/el/v1/models")
            {
                Json(ctx, 200, JsonSerializer.Serialize(new object[]
                {
                    Model("eleven_multilingual_v2", "Eleven Multilingual v2", style: true, boost: true, langs: ["en", "ja"], max: 10000),
                    Model("eleven_v3", "Eleven v3", style: false, boost: false, langs: ["en", "th"], max: 5000),
                    Model("eleven_v9_future", "Eleven v9", style: false, boost: false, langs: ["en"], max: 5000),
                    Model("eleven_v4", "Eleven v4", style: false, boost: false, langs: ["en", "th"], max: 10000),
                    new { model_id = "eleven_multilingual_sts_v2", name = "STS", can_do_text_to_speech = false },
                }));
                return;
            }
            if (path == "/el/v2/voices")
            {
                bool second = (req.QueryString["next_page_token"] ?? "") == "p2";
                Json(ctx, 200, second
                    ? JsonSerializer.Serialize(new { voices = new[] { new { voice_id = "v_clone", name = "My voice", category = "cloned" } }, has_more = false })
                    : JsonSerializer.Serialize(new
                    {
                        voices = new object[]
                        {
                            new { voice_id = "v_pre", name = "Sarah", category = "premade", labels = new { age = "young", accent = "american" },
                                  preview_url = "https://example.com/sarah.mp3", verified_languages = new[] { new { language = "th", model_id = "eleven_v3" } } },
                            new { voice_id = "v_main", name = "Main", category = "premade" },
                        },
                        has_more = true, next_page_token = "p2",
                    }));
                return;
            }
            if (path == "/el/v1/user/subscription")
            {
                Json(ctx, 200, "{\"tier\":\"creator\",\"character_count\":1234,\"character_limit\":100000,\"can_use_instant_voice_cloning\":true,\"voice_limit\":30,\"next_character_count_reset_unix\":1796000000}");
                return;
            }
            if (path.StartsWith("/el/v1/text-to-speech/")) { Bytes(ctx, "audio/mpeg", FakeMp3); return; }
            if (path == "/el/v1/voices/add") { Json(ctx, 200, "{\"voice_id\":\"v_new\",\"requires_verification\":false}"); return; }
            if (req.HttpMethod == "DELETE" && path.StartsWith("/el/v1/voices/")) { Json(ctx, 200, "{\"status\":\"ok\"}"); return; }
        }
        if (path.StartsWith("/oa/"))
        {
            if (req.Headers["Authorization"] != "Bearer sk-openai-test") { Json(ctx, 401, "{\"error\":{\"message\":\"Incorrect API key\"}}"); return; }
            if (path == "/oa/v1/audio/speech") { Bytes(ctx, "audio/mpeg", FakeMp3); return; }
            if (path == "/oa/v1/models")
            {
                Json(ctx, 200, "{\"data\":[{\"id\":\"gpt-5\"},{\"id\":\"tts-1\"},{\"id\":\"gpt-4o-mini-tts\"},{\"id\":\"tts-1-hd\"},{\"id\":\"gpt-realtime-tts-x\"}]}");
                return;
            }
        }
        Json(ctx, 404, "{\"detail\":\"not found\"}");
    }

    static object Model(string id, string name, bool style, bool boost, string[] langs, int max) => new
    {
        model_id = id, name, description = name + " model", can_do_text_to_speech = true, can_use_style = style, can_use_speaker_boost = boost,
        can_be_finetuned = true, requires_alpha_access = false, maximum_text_length_per_request = max,
        languages = langs.Select(l => new { language_id = l, name = l }).ToArray(),
        model_rates = new { character_cost_multiplier = 1.0 },
    };

    static void Json(HttpListenerContext ctx, int code, string json) => Bytes(ctx, "application/json", Encoding.UTF8.GetBytes(json), code);

    static void Bytes(HttpListenerContext ctx, string type, byte[] data, int code = 200)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = data.Length;
        ctx.Response.OutputStream.Write(data);
        ctx.Response.Close();
    }
}
