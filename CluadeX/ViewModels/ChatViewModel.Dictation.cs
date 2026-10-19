using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services.Voice;

namespace CluadeX.ViewModels;

/// <summary>
/// Speak instead of typing (owner 2026-10-11: "ระบบรับคำสั่ง หรือพร้อมด้วยไมค์ แม่นๆ" — "ใช้ของ chrome google … ของฟรี").
/// The mic button opens a small Chrome window running Chrome's free speech-to-text (<see cref="ChromeDictationService"/>);
/// what it hears streams into the input box — the unfinished guess at the end, replaced as it firms up. Nothing is
/// ever sent by voice alone: the user reads the box and presses Send (here, or in the mic window).
/// </summary>
public partial class ChatViewModel
{
    private ChromeDictationService? _dictation;
    private bool _isDictating;
    /// <summary>The unfinished words currently shown at the end of the box (replaced by the next update).</summary>
    private string _dictInterim = "";

    public bool IsDictating { get => _isDictating; private set => SetProperty(ref _isDictating, value); }

    /// <summary>Heard words were put in the box: the view moves the caret to the end.</summary>
    public event Action? DictationApplied;

    /// <summary>Opens the mic page at the given URL; false when it could not. Null = the real browser. A test
    /// replaces it, so no test ever opens a browser or a microphone.</summary>
    public Func<string, bool>? DictationLauncher { get; set; }

    private ICommand? _toggleDictationCommand;
    public ICommand ToggleDictationCommand => _toggleDictationCommand ??= new RelayCommand(ToggleDictation);

    public void ToggleDictation()
    {
        if (IsDictating) StopDictation(Loc("chat.mic.stopped"));
        else StartDictation();
    }

    private void StartDictation()
    {
        ChromeDictationService.Browser? browser = null;
        if (DictationLauncher == null)
        {
            browser = ChromeDictationService.FindBrowser();
            if (browser == null) { StatusText = Loc("chat.mic.noBrowser"); return; }
        }
        if (!EnsureMicBridge()) return;
        if (IsLiveVoice) StopLiveVoice();   // one mic window at a time

        string ui = _settingsService.Settings.Language == "th" ? "th" : "en";
        string url = _dictation!.BeginSession(ui == "th" ? "th-TH" : "en-US", ui, browser?.Engine ?? "google",
            speak: _settingsService.Settings.VoiceSpeakReplies);
        _dictInterim = "";
        IsDictating = true;
        bool opened;
        string? launchError = null;
        if (DictationLauncher != null) opened = DictationLauncher(url);
        else
        {
            var (x, y) = MicWindowSpot();
            opened = ChromeDictationService.Launch(browser!, url, Path.Combine(_settingsService.DataRoot, "voice", browser!.Engine), x, y, out launchError);
        }
        if (!opened) { StopDictation(LocF("chat.mic.failed", launchError ?? "?")); return; }
        StatusText = LocF("chat.mic.opening", browser?.Name ?? "Google Chrome");
    }

    /// <summary>The loopback bridge to the mic window, started once (dictation, reading aloud and Live voice share it).</summary>
    private bool EnsureMicBridge()
    {
        if (_dictation != null) return true;
        _dictation = ChromeDictationService.TryStart(out var error);
        if (_dictation == null) { StatusText = LocF("chat.mic.failed", error ?? "?"); return false; }
        _dictation.TextReceived += (s, fin, interim) => OnUi(() => ApplyDictation(s, fin, interim));
        _dictation.PageEvent += (s, type, detail) => OnUi(() => OnDictationEvent(s, type, detail));
        _dictation.SendRequested += s => OnUi(() =>
        {
            if (IsDictating && s == _dictation?.Session && SendMessageCommand.CanExecute(null)) SendMessageCommand.Execute(null);
        });
        _dictation.Closed += (s, neverOpened) =>
        {
            // A live call whose window is gone is hung up from here at once — on this thread, so it does not wait
            // for the UI — then the chat hears about it through the session's Ended.
            if (_live is { } live && s == _liveBridgeSession) _ = live.PageGoneAsync();
            OnUi(() =>
            {
                if (IsDictating && s == _dictation?.Session) StopDictation(Loc(neverOpened ? "chat.mic.noConnect" : "chat.mic.closed"));
            });
        };
        _dictation.LiveEvent += (s, ev) =>
        {
            if (_live is { } live && s == _liveBridgeSession) live.OnServerEvent(ev);
        };
        try { Application.Current.Exit += (_, _) => { _live?.PageGoneAsync().Wait(3000); _dictation?.Dispose(); }; } catch { }
        return true;
    }

    private void StopDictation(string? status)
    {
        _dictation?.EndSession();
        _dictInterim = "";   // an unfinished guess still in the box stays there as typed text
        IsDictating = false;
        if (status != null) StatusText = status;
    }

    /// <summary>
    /// Put heard words in the box: the previous guess comes off the end, newly final words are added, and the new
    /// guess goes on the end. When the user has edited the end of the box meanwhile, the old guess is left alone.
    /// </summary>
    private void ApplyDictation(int session, string final, string interim)
    {
        if (!IsDictating || _dictation == null || session != _dictation.Session) return;
        string current = UserInput ?? "";
        if (_dictInterim.Length > 0 && current.EndsWith(_dictInterim, StringComparison.Ordinal))
            current = current[..^_dictInterim.Length];
        if (final.Length > 0) current = JoinSpoken(current, final);
        string shown = interim.Length > 0 ? JoinSpoken(current, interim) : current;
        _dictInterim = shown[current.Length..];
        UserInput = shown;
        DictationApplied?.Invoke();
    }

    private static string JoinSpoken(string text, string words) =>
        text.Length == 0 || char.IsWhiteSpace(text[^1]) ? text + words : text + " " + words;

    /// <summary>The box was emptied (sent, or cleared by hand): the mic window clears the words it shows.</summary>
    private void OnInputCleared()
    {
        if (!IsDictating) return;
        _dictInterim = "";
        _dictation?.NotifySent();
    }

    private void OnDictationEvent(int session, string type, string detail)
    {
        if (IsLiveVoice && session == _liveBridgeSession && type == "error")
        {
            OnLivePageError(detail);
            return;
        }
        if (!IsDictating || _dictation == null || session != _dictation.Session) return;
        StatusText = type switch
        {
            "listening" => LocF("chat.mic.listening", detail.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? "ไทย" : "English"),
            "paused" => Loc("chat.mic.paused"),
            "speaking" => Loc("chat.mic.speaking"),
            "error" => detail switch
            {
                "not-allowed" or "service-not-allowed" => Loc("chat.mic.denied"),
                "audio-capture" => Loc("chat.mic.noMic"),
                "network" => Loc("chat.mic.network"),
                "unsupported" => Loc("chat.mic.unsupported"),
                "no-tts" => Loc("chat.mic.noTts"),
                _ => LocF("chat.mic.error", detail),
            },
            _ => StatusText,
        };
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) action();
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>Where the mic window opens: above the composer, at the right of the chat window (DIPs).</summary>
    private static (int? X, int? Y) MicWindowSpot()
    {
        try
        {
            var w = Application.Current?.MainWindow;
            if (w == null || !w.IsVisible) return (null, null);
            var dpi = VisualTreeHelper.GetDpi(w);
            var corner = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight));
            int x = (int)(corner.X / dpi.DpiScaleX) - 480, y = (int)(corner.Y / dpi.DpiScaleY) - 580;
            return (Math.Max(0, x), Math.Max(0, y));
        }
        catch { return (null, null); }
    }
}
