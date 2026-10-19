using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services.Voice;

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
        private set { if (SetProperty(ref _isLiveVoice, value)) OnPropertyChanged(nameof(ShowLiveButton)); }
    }

    /// <summary>"0:42 · $0.035 · limit 10 min" while a call runs.</summary>
    public string LiveVoiceMeter { get => _liveVoiceMeter; private set => SetProperty(ref _liveVoiceMeter, value); }

    /// <summary>The Live button shows only when Settings › Voice › Live voice is on.</summary>
    public bool ShowLiveButton => _settingsService.Settings.VoiceLiveEnabled && !IsLiveVoice;

    private void RefreshVoiceButtons() => OnPropertyChanged(nameof(ShowLiveButton));

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

    /// <summary>Settings › Voice › Read replies aloud, with the mic window open: the reply of the turn that just ended
    /// is read out (code and links left out). Nothing is read for a turn that was stopped or failed.</summary>
    private void SpeakReplyIfWanted(ChatMessage userMsg)
    {
        if (!_settingsService.Settings.VoiceSpeakReplies || !IsDictating || _dictation == null) return;
        var rows = TurnMessages;
        int at = rows.IndexOf(userMsg);
        if (at < 0) return;
        var reply = rows.Skip(at + 1).LastOrDefault(m => m.Role == MessageRole.Assistant);
        if (reply == null || reply.HasError || reply.IsInterrupted || string.IsNullOrWhiteSpace(reply.Content)) return;
        bool thai = reply.Content.Any(c => c >= '฀' && c <= '๿');
        string text = SpeechText.ForSpeech(reply.Content, thai);
        if (text.Length > 0) _dictation.Speak(text, thai ? "th-TH" : "en-US");
    }
}
