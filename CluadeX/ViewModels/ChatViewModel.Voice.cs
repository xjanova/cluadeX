using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Voice;
using CluadeX.Services.Voice.Tts;

namespace CluadeX.ViewModels;

/// <summary>
/// Voice mode (owner 2026-10-19: study OpenAI's "GPT-Live and realtime audio"), behind Settings › Voice, off by default.
/// 1. Reading replies aloud — free: the mic window reads each finished reply with the browser's own speech synthesis.
/// 2. Live voice — paid: a GPT-Live call in the mic window. GPT-Live listens and talks; every request that needs
///    work is delegated back here ("client" delegation) and runs as an ordinary message of this chat, on whatever
///    provider is active, with permission prompts in this window. A meter shows the cost by the second and the
///    session is closed at the minutes cap.
/// </summary>
public partial class ChatViewModel
{
    private LiveVoiceSession? _live;
    /// <summary>The bridge session number the live call's window runs under.</summary>
    private int _liveBridgeSession = -1;
    private bool _isLiveVoice;
    private string _liveVoiceMeter = "";

    /// <summary>The HTTP client the Live API is called with. A test points the OpenAI base URL at a localhost mock.</summary>
    internal static HttpClient LiveHttp { get; set; } = new() { Timeout = TimeSpan.FromSeconds(40) };

    public bool IsLiveVoice
    {
        get => _isLiveVoice;
        private set
        {
            if (!SetProperty(ref _isLiveVoice, value)) return;
            OnPropertyChanged(nameof(ShowLiveButton));
            // GPT-Live talks in its own window: replies read aloud wait until the call ends, then play in order.
            if (_voiceQueue != null)
            {
                if (value && _voiceQueue.IsActive) _voiceQueue.TogglePause();
                _voiceQueue.Hold = value;
            }
        }
    }

    /// <summary>"0:42 · $0.035 · limit 10 min" while a call runs.</summary>
    public string LiveVoiceMeter { get => _liveVoiceMeter; private set => SetProperty(ref _liveVoiceMeter, value); }

    /// <summary>The Live button shows only when Settings › Voice › Live voice is on.</summary>
    public bool ShowLiveButton => _settingsService.Settings.VoiceLiveEnabled && !IsLiveVoice;

    private void RefreshVoiceButtons() { OnPropertyChanged(nameof(ShowLiveButton)); OnPropertyChanged(nameof(SpeakReplies)); }

    private ICommand? _toggleLiveVoiceCommand;
    public ICommand ToggleLiveVoiceCommand => _toggleLiveVoiceCommand ??= new RelayCommand(ToggleLiveVoice);

    public void ToggleLiveVoice()
    {
        if (IsLiveVoice) StopLiveVoice();
        else StartLiveVoice();
    }

    private void StartLiveVoice()
    {
        var s = _settingsService.Settings;
        if (!s.VoiceLiveEnabled) { StatusText = Loc("chat.live.off"); return; }
        s.ProviderConfigs.TryGetValue("OpenAI", out var openAi);
        string key = openAi?.ApiKey?.Trim() ?? "";
        if (key.Length == 0) { StatusText = Loc("chat.live.noKey"); return; }

        ChromeDictationService.Browser? browser = null;
        if (DictationLauncher == null)
        {
            browser = ChromeDictationService.FindBrowser();
            if (browser == null) { StatusText = Loc("chat.mic.noBrowser"); return; }
        }
        if (!EnsureMicBridge()) return;
        if (IsDictating) StopDictation(null);   // one mic window at a time

        bool thai = s.Language == "th";
        int minutes = Math.Clamp(s.VoiceLiveMaxMinutes, 1, 60);
        string apiBase = string.IsNullOrWhiteSpace(openAi?.BaseUrl) ? "https://api.openai.com/v1" : openAi!.BaseUrl!;
        var session = new LiveVoiceSession(LiveHttp, apiBase, key, new LiveVoiceOptions
        {
            Voice = string.IsNullOrWhiteSpace(s.VoiceLiveVoice) ? "marin" : s.VoiceLiveVoice,
            MaxMinutes = minutes,
            Thai = thai,
        });
        var bridge = _dictation!;
        session.Send = bridge.SendToPage;
        session.RunTask = RunVoiceRequestAsync;
        session.Changed += () => OnUi(() => { if (ReferenceEquals(_live, session)) UpdateLiveMeter(session); });
        session.Ended += reason => OnUi(() => OnLiveEnded(session, reason));
        _live = session;

        string ui = thai ? "th" : "en";
        string url = bridge.BeginSession(thai ? "th-TH" : "en-US", ui, browser?.Engine ?? "google", live: true) + "&cap=" + minutes;
        int bridgeSession = bridge.Session;
        _liveBridgeSession = bridgeSession;
        bridge.LiveOfferHandler = (sess, sdp, ct) => sess == bridgeSession && ReferenceEquals(_live, session)
            ? session.CreateAsync(sdp, ct)
            : Task.FromResult(new LiveOfferResult(null, null, "this voice session has ended"));
        IsLiveVoice = true;
        LiveVoiceMeter = Loc("chat.live.connecting");

        bool opened;
        string? launchError = null;
        if (DictationLauncher != null) opened = DictationLauncher(url);
        else
        {
            var (x, y) = MicWindowSpot();
            opened = ChromeDictationService.Launch(browser!, url, Path.Combine(_settingsService.DataRoot, "voice", browser!.Engine), x, y, out launchError);
        }
        if (!opened)
        {
            session.Close("not-opened");
            StatusText = LocF("chat.mic.failed", launchError ?? "?");
            return;
        }
        StatusText = LocF("chat.live.opening", minutes);
    }

    /// <summary>Hang up: <c>session.close</c> goes to the window, then the call ends when GPT-Live confirms it (or is
    /// hung up from here). A second press while it is closing hangs up at once.</summary>
    public void StopLiveVoice()
    {
        if (_live is not { } live) { IsLiveVoice = false; return; }
        if (live.State == "closing") { _ = live.PageGoneAsync(); return; }
        LiveVoiceMeter = Loc("chat.live.closing");
        live.Close("user");
    }

    private void UpdateLiveMeter(LiveVoiceSession live)
    {
        if (live.State is "new" or "connecting") { LiveVoiceMeter = Loc("chat.live.connecting"); return; }
        if (live.State == "closing") { LiveVoiceMeter = Loc("chat.live.closing"); return; }
        LiveVoiceMeter = LocF("chat.live.meter", Clock(live.Seconds), live.CostUsd.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            Math.Round(live.Options.MaxMinutes));
    }

    private static string Clock(double seconds)
    {
        int s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    private void OnLiveEnded(LiveVoiceSession session, string reason)
    {
        if (!ReferenceEquals(_live, session)) return;
        _live = null;
        if (_dictation is { } bridge)
        {
            bridge.LiveOfferHandler = null;
            if (bridge.Session == _liveBridgeSession) bridge.EndSession();
        }
        _liveBridgeSession = -1;
        IsLiveVoice = false;
        LiveVoiceMeter = "";
        string cost = session.CostUsd.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        if (reason is "not-opened" or "page-error")
        {
            // The status line already says why; a session that was created was billed (15 s at least).
            if (session.SessionId != null) StatusText += " " + LocF("chat.live.charged", cost);
            return;
        }
        if (session.State == "failed")
        {
            StatusText = LocF("chat.live.failed", session.LastError ?? reason);
            return;
        }
        string why = reason switch
        {
            "cap" or "expired" => Loc("chat.live.why.cap"),
            "page-closed" or "remote_hangup" or "connection_lost" => Loc("chat.live.why.closed"),
            "content" => Loc("chat.live.why.content"),
            _ => "",
        };
        StatusText = session.SessionId == null
            ? Loc("chat.live.notStarted")
            : LocF("chat.live.ended", Clock(session.Seconds), cost) + (why.Length > 0 ? " — " + why : "");
    }

    /// <summary>The live window reported a problem before or during the call (no microphone, the start failed).</summary>
    private void OnLivePageError(string detail)
    {
        if (_live is not { } live) return;
        if (detail.StartsWith("live", StringComparison.Ordinal))
        {
            // The window could not set the call up ("live" = the start failed, which the session reports itself;
            // "live-answer"/"live-setup" = after it was created). Nothing can talk to it any more: hang it up now.
            if (live.State != "failed") StatusText = LocF("chat.live.failed", detail);
            _ = live.PageGoneAsync("page-error");
            return;
        }
        StatusText = detail switch
        {
            "not-allowed" => Loc("chat.mic.denied"),
            "audio-capture" => Loc("chat.mic.noMic"),
            _ => LocF("chat.live.failed", detail),
        };
        // Nothing was created yet: there is no call to keep open.
        if (live.State == "new") live.Close("page-error");
    }

    /// <summary>
    /// A spoken request GPT-Live handed to CluadeX: sent into the chat on screen as a message of its own (the user's
    /// typed draft stays in the box), run by the agent as Send would run it, and its reply handed back to be spoken.
    /// A reply already running in this chat is waited for first.
    /// </summary>
    private async Task<LiveTaskResult> RunVoiceRequestAsync(string request, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return new LiveTaskResult("CluadeX is closing.", false);
        return await await dispatcher.InvokeAsync(async () =>
        {
            var until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (IsGenerating && DateTime.UtcNow < until && !ct.IsCancellationRequested)
                await Task.Delay(500);
            if (ct.IsCancellationRequested) return new LiveTaskResult("The voice session ended.", false);
            if (IsGenerating) return new LiveTaskResult("CluadeX is still busy with another reply in this chat.", false);
            if (!await EnsureProviderReadyAsync()) return new LiveTaskResult(StatusText, false);
            var sentInto = Messages;
            int rowsBefore = sentInto.Count;
            var pipe = new PipeTurn(ct, null);
            await SendMessageCore(request, pipe);
            var outcome = PipeTurnOutcome(pipe, request, sentInto, rowsBefore);
            return new LiveTaskResult(outcome.Text, !outcome.IsError);
        });
    }

    private VoiceSpeaker? _speaker;
    private ReplyVoiceQueue? _voiceQueue;
    private ChatVoiceOutput? _voiceOut;

    /// <summary>The replies read aloud, in order — what the voice player box under the chat shows and steps through.
    /// A new reply waits for the one being read (owner 2026-11-10: "ไม่ตัด ต่อให้มีของใหม่มาก็ให้รอคิว").</summary>
    public ReplyVoiceQueue VoiceQueue
    {
        get
        {
            if (_voiceQueue != null) return _voiceQueue;
            _voiceOut = new ChatVoiceOutput(this);
            _voiceQueue = new ReplyVoiceQueue(_voiceOut) { Hold = IsLiveVoice };
            _voiceQueue.Failed += (item, error) => { if (error != null) StatusText = error; };
            return _voiceQueue;
        }
    }

    /// <summary>Settings › Voice › Read replies aloud: the reply of the turn that just ended joins the voice queue (code
    /// and links left out), read by the girl who wrote it in her own voice and mood (VoicePersona). It waits for the
    /// one being read — a new reply never cuts the last. The browser voice speaks in the mic window; Edge, Windows,
    /// ElevenLabs and OpenAI make the audio here and play it in the mic window when it is open (it stops listening while
    /// she talks), else in the app. Nothing is read for a turn that was stopped or failed.
    /// <para>The free engines fall back to Edge's free voices when they cannot speak this reply: the browser voice with
    /// no mic window open, Windows with no voice for the reply's language (2026-11-09: most PCs have English voices
    /// only, and Zira reads a Thai reply as an empty WAV — the reply was silent without a word).</para></summary>
    private void SpeakReplyIfWanted(ChatMessage userMsg)
    {
        var s = _settingsService.Settings;
        if (!s.VoiceSpeakReplies) return;
        var rows = TurnMessages.ToList();
        int userAt = rows.IndexOf(userMsg);
        // Only the reply bubble that ends the turn (SpokenReply): not a card, a tool row or the "no summary" note.
        var reply = SpokenReply.Of(rows, userAt);
        if (reply == null)
        {
            _debugLog?.Info("Voice", userAt < 0 ? "nothing read: the turn's message is not in its chat"
                : "nothing read: the turn ended on " + SpokenReply.Describe(SpokenReply.LastRow(rows, userAt)));
            return;
        }
        var item = SpokenItemOf(reply);
        if (item == null) { _debugLog?.Info("Voice", "nothing read: the reply is only code or links"); return; }
        VoiceQueue.Enqueue(item);
        if (!ReferenceEquals(VoiceQueue.Current, item))
            _debugLog?.Info("Voice", $"reply queued behind the one being read ({VoiceQueue.Waiting} waiting)");
    }

    /// <summary>The words of a reply to read aloud (null = nothing to say: only code or links).</summary>
    private SpokenItem? SpokenItemOf(ChatMessage reply)
    {
        bool thai = reply.Content.Any(c => c >= '฀' && c <= '๿');
        int max = Math.Clamp(_settingsService.Settings.Voice?.MaxChars ?? 1200, 100, 10000);
        string text = SpeechText.ForSpeech(reply.Content, thai, max);
        if (text.Length == 0) return null;
        var who = ReplyCharacter(reply);
        return new SpokenItem(text, thai, who.Own, reply, character: who);
    }

    /// <summary>The girl a reply bubble is shown with — the face and the voice it is read in: the room member who
    /// spoke it, else the girl of the provider answering this chat (what the AI avatar beside the bubble shows). The
    /// reply's own Character is the default girl: it is only set on the boxes a run opens.</summary>
    private ChatCharacter ReplyCharacter(ChatMessage reply) =>
        reply.RoomSpeaker is { Length: > 0 } speaker ? ChatCharacter.ForRoomAgent(speaker) : ActiveCharacter;

    /// <summary>The ▶ on a reply's hover strip, or a click on the bubble: read this one now (it is the user's ask, so it
    /// may cut the one playing); whatever was waiting plays after it. On the one playing it pauses.</summary>
    public void PlayMessageVoice(ChatMessage message)
    {
        if (!message.CanReadAloud) return;
        var item = VoiceQueue.ItemOf(message) ?? SpokenItemOf(message);
        if (item == null) { StatusText = Loc("voice.player.nothing"); return; }
        VoiceQueue.Play(item);
    }

    /// <summary>
    /// The voice queue's sound: the mic window when it is open (it stops listening while she talks; the browser voice
    /// can only speak there), else the app's own player. Pausing holds the in-app sound where it is; the mic window
    /// cannot pause, so there pause stops and play starts the reply again.
    /// </summary>
    private sealed class ChatVoiceOutput(ChatViewModel vm) : IReplyVoiceOutput
    {
        private VoicePlayer? _player;
        private SpokenItem? _inApp, _inPage;
        private long _pageSayId = -1;
        private bool _stopping;

        public event Action<SpokenItem, string?>? Ended;
        public bool CanPause => _inApp != null;

        private bool MicOpen => vm.IsDictating && vm._dictation != null;

        public async Task PrepareAsync(SpokenItem item, CancellationToken ct)
        {
            if (item.Audio != null) return;
            var s = vm._settingsService.Settings;
            string engine = VoiceSpeaker.EngineOf(s);
            if (VoiceSpeaker.NeedsMicWindow(engine) && MicOpen) return;   // the page speaks the text itself
            // The browser voice with no mic window, or Windows with no voice for this language: Edge's free voices.
            bool edge = VoiceSpeaker.NeedsMicWindow(engine) || (engine == Models.VoiceSettings.Windows && !WindowsTts.Speaks(item.Lang));
            var speaker = vm._speaker ??= new VoiceSpeaker(() => vm._settingsService.Settings);
            var took = System.Diagnostics.Stopwatch.StartNew();
            string used = edge ? Models.VoiceSettings.Edge : engine;
            try
            {
                SpeechAudio audio;
                try
                {
                    audio = edge ? await speaker.SynthesizeEdgeAsync(item.Text, item.Thai, item.Girl, ct)
                                 : await speaker.SynthesizeAsync(item.Text, item.Thai, item.Girl, ct);
                }
                catch (WindowsVoiceMissingException)
                {
                    // The Windows voice made no sound after all (a voice that claims the language but reads none of it).
                    used = Models.VoiceSettings.Edge;
                    audio = await speaker.SynthesizeEdgeAsync(item.Text, item.Thai, item.Girl, ct);
                }
                ct.ThrowIfCancellationRequested();
                item.Audio = audio;
                vm._debugLog?.Info("Voice", $"reply made · {used}{(used == Models.VoiceSettings.Edge ? " via " + EdgeTts.LastHost : "")}"
                    + $" · {item.Text.Length} chars · {audio.Bytes.Length / 1024} KB in {took.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException) { throw; }
            catch (VoiceKeyMissingException ex)
            {
                vm._debugLog?.Warn("Voice", $"reply not read: no {ex.Engine} key");
                throw new InvalidOperationException(Loc(ex.Engine == Models.VoiceSettings.OpenAI ? "voice.openai.noKey" : "voice.eleven.noKey"));
            }
            catch (EdgeTtsException ex)
            {
                vm._debugLog?.Warn("Voice", $"reply not read: Edge failed after {took.ElapsedMilliseconds} ms ({item.Text.Length} chars)", ex);
                throw new InvalidOperationException(LocF(item.Thai && engine != Models.VoiceSettings.Edge ? "voice.chat.noThaiVoice" : "voice.chat.failed", ex.Message));
            }
            catch (Exception ex)
            {
                vm._debugLog?.Warn("Voice", $"reply not read: {used} failed", ex);
                throw new InvalidOperationException(LocF("voice.chat.failed", ex.Message));
            }
        }

        public bool Start(SpokenItem item)
        {
            _inApp = _inPage = null;
            if (MicOpen)
            {
                var dictation = vm._dictation!;
                if (item.Audio == null)
                {
                    var b = (vm._speaker ??= new VoiceSpeaker(() => vm._settingsService.Settings)).BrowserStyle(item.Girl);
                    dictation.Speak(item.Text, item.Lang, b.Voice, b.Rate, b.Pitch);
                    _inPage = item;
                }
                else if (dictation.SpeakAudio(item.Audio.Bytes, item.Audio.Mime, item.Text, item.Lang)) _inPage = item;
                if (_inPage != null) { _pageSayId = dictation.SayId; return true; }
            }
            if (item.Audio == null)
            {
                // Made for the mic window, which closed before it played: made again (Edge) on the next press of play.
                item.Error = Loc("voice.player.micClosed");
                return false;
            }
            if (_player == null)
            {
                _player = new VoicePlayer(vm._settingsService.DataRoot);
                _player.Finished += OnPlayerFinished;
            }
            _inApp = item;
            _player.Play(item.Audio);
            return true;
        }

        private void OnPlayerFinished(string? error)
        {
            if (_stopping || _inApp is not { } item) return;
            _inApp = null;
            if (error != null) vm._debugLog?.Warn("Voice", "the reply's audio did not play: " + error);
            Ended?.Invoke(item, error == null ? null : LocF("voice.chat.failed", error));
        }

        /// <summary>The mic window says a reply ended ("&lt;id&gt;", or "&lt;id&gt;:hush" when the user pressed its stop).</summary>
        public void OnPageSpoke(string detail)
        {
            if (_inPage is not { } item) return;
            bool hushed = detail.EndsWith(":hush", StringComparison.Ordinal);
            string id = hushed ? detail[..^5] : detail;
            if (!long.TryParse(id, out long said) || said != _pageSayId) return;   // a reply stopped earlier: not this one
            _inPage = null;
            if (hushed) vm.VoiceQueue.Stop();   // her stop button in the mic window: silence, like Stop in the box
            else Ended?.Invoke(item, null);
        }

        /// <summary>The mic window closed while it was speaking: that reply counts as read, the rest play in the app.</summary>
        public void OnPageGone()
        {
            if (_inPage is not { } item) return;
            _inPage = null;
            Ended?.Invoke(item, null);
        }

        public void Pause() => _player?.Pause();
        public void Resume() => _player?.Resume();

        /// <summary>Silence (never raises <see cref="Ended"/>).</summary>
        public void Stop()
        {
            _stopping = true;
            try
            {
                if (_inApp != null) _player?.Stop();
                if (_inPage != null) vm._dictation?.Hush();
            }
            finally { _stopping = false; _inApp = _inPage = null; }
        }
    }

    // ─── the speaker button by the girls' faces in the chat ───

    /// <summary>Read replies aloud — the same switch as Settings › Voice, by the girl's face on every reply.</summary>
    public bool SpeakReplies
    {
        get => _settingsService.Settings.VoiceSpeakReplies;
        set
        {
            if (value == _settingsService.Settings.VoiceSpeakReplies) return;
            _settingsService.UpdateSettings(st => st.VoiceSpeakReplies = value);
            if (!value) StopReplyVoice();
            OnPropertyChanged();
            StatusText = Loc(value ? "voice.chat.on" : "voice.chat.off");
        }
    }

    private ICommand? _toggleSpeakRepliesCommand;
    public ICommand ToggleSpeakRepliesCommand => _toggleSpeakRepliesCommand ??= new RelayCommand(() => SpeakReplies = !SpeakReplies);

    /// <summary>Reading aloud stops and what was waiting is dropped (the voice was turned off). Sending a new message
    /// does not stop it any more: the reply plays on and the next one waits its turn.</summary>
    private void StopReplyVoice() => _voiceQueue?.Stop();

    /// <summary>The mic window's "spoke" event (a reply it was reading ended or was hushed).</summary>
    private void OnPageSpoke(string detail) => _voiceOut?.OnPageSpoke(detail);

    /// <summary>The mic window closed: a reply it was reading counts as read, the rest play in the app.</summary>
    private void OnVoicePageGone() => _voiceOut?.OnPageGone();

    /// <summary>The mic window reported its browser's voices: remembered for the picker in Settings › Voice.</summary>
    private void RememberBrowserVoices(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        var v = _settingsService.Settings.Voice;
        if (v != null && v.KnownBrowserVoices.SequenceEqual(names)) return;
        _settingsService.UpdateSettings(st => (st.Voice ??= new Models.VoiceSettings()).KnownBrowserVoices = names.ToList());
    }
}
