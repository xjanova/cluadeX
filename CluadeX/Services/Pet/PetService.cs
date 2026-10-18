using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;

namespace CluadeX.Services.Pet;

/// <summary>
/// The desktop pet. Everyone starts with an egg of a random colour; it learns how its owner works and talks (real
/// minutes, days, messages, tone, tool use — pet.json, this PC only) and, after 24 hours of actual use, the AI the
/// owner uses most is asked what pet it would give them as a friend — and draws it, in the same pixel style. Nobody
/// knows beforehand what will hatch. Once out, it walks the whole screen (the taskbar, the tops of windows), can be
/// clicked, dragged and thrown, talks now and then, and can be put away and called back.
/// </summary>
public sealed class PetService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly ChatPersistenceService? _chats;
    private readonly DebugLogService? _log;
    private readonly AiProviderManager? _providers;
    private bool _turnRunning;
    private DateTime _lastTurnUtc = DateTime.MinValue;
    private readonly string _path;
    private readonly object _lock = new();
    private DispatcherTimer? _minuteTimer;
    private Views.Pet.PetWindow? _window;
    private DateTime _lastActivityUtc = DateTime.MinValue;
    private bool _dirty;

    public PetState State { get; private set; }

    /// <summary>Anything shown about the pet changed (hatched, progress, put away, name …). UI thread.</summary>
    public event Action? Changed;

    /// <summary>Something happened the pet may react to (raised on the UI thread).</summary>
    public event Action<PetMoment>? Moment;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public PetService(SettingsService settings, LocalizationService loc, ChatPersistenceService? chats = null,
        DebugLogService? log = null, AiProviderManager? providers = null)
    {
        _settings = settings;
        _loc = loc;
        _chats = chats;
        _log = log;
        _providers = providers;
        _path = Path.Combine(settings.DataRoot, "pet.json");
        State = Load(_path) ?? PetBrain.NewEgg(new Random());
        _dirty = !File.Exists(_path);
    }

    public bool Thai => _loc.CurrentLanguage == "th";
    /// <summary>It may follow the mouse around (Settings: the pet can tease).</summary>
    public bool Mischief => _settings.Settings.PetMischief;
    public bool Talks => _settings.Settings.PetTalks;
    public void SetTalks(bool on) => _settings.UpdateSettings(s => s.PetTalks = on);
    public void SetMischief(bool on) => _settings.UpdateSettings(s => s.PetMischief = on);
    public bool Enabled => _settings.Settings.Features.BuddyCompanion;
    public bool IsHatched => State.Hatched;
    public double HatchProgress => PetBrain.HatchProgress(State.Habits);

    /// <summary>The sidebar/title-bar line: the name, or "mystery egg".</summary>
    public string Title => State.Hatched ? State.Name : (Thai ? "ไข่ปริศนา" : "Mystery egg");

    /// <summary>What it is: the AI's name for it, or the built-in species.</summary>
    public string SpeciesText
    {
        get
        {
            var about = State.About;
            string? named = Thai ? about?.SpeciesTh : about?.Species;
            if (string.IsNullOrWhiteSpace(named)) named = Thai ? about?.Species : about?.SpeciesTh;
            return string.IsNullOrWhiteSpace(named) ? PetLines.SpeciesName(State.Species, Thai) : named!;
        }
    }

    /// <summary>The second line: what it is, or how far the egg has got.</summary>
    public string Subtitle => State.Hatched
        ? $"{SpeciesText} · {PetLines.PersonalityName(State.Personality, Thai)}{(State.Shiny ? " ✦" : "")}"
        : HatchPending
            ? (Thai ? "กำลังจะฟัก…" : "hatching…")
            : (Thai ? $"กำลังเรียนรู้นิสัย {HatchProgress:P0}" : $"learning you {HatchProgress:P0}");

    /// <summary>A tooltip with the details: what the egg still needs, or what the pet learned.</summary>
    public string Details
    {
        get
        {
            var h = State.Habits;
            if (!State.Hatched)
            {
                string hours = $"{h.ActiveMinutes / 60.0:F1}/{PetBrain.HatchMinutes / 60}";
                return Thai
                    ? $"ไข่ไม่รีบฟัก: รอจนรู้จักคุณจริง — ใช้งานจริง {hours} ชม. (นับเฉพาะเวลาที่ใช้งาน) · {h.ActiveDays.Count}/{PetBrain.HatchDays} วัน · ข้อความ {h.Messages}/{PetBrain.HatchMessages}\nตอนฟัก AI ที่คุณใช้ประจำจะเป็นคนเลือกว่าจะมอบเพื่อนแบบไหนให้คุณ จากสรุปนิสัยและตัวอย่างข้อความ (ตัดคีย์/ลิงก์/อีเมลออกแล้ว) — ข้อมูลอยู่บนเครื่องนี้ ส่งให้ AI ตัวนั้นครั้งเดียวตอนฟัก"
                    : $"No hurry: it waits until it really knows you — {hours} hours of real use (only time actually spent) · {h.ActiveDays.Count}/{PetBrain.HatchDays} days · {h.Messages}/{PetBrain.HatchMessages} messages\nWhen it hatches, the AI you use most decides what friend to give you, from a summary of your habits and a sample of your messages (keys, links and e-mails removed) — kept on this PC, sent once to that AI at hatch time";
            }
            var t = PetBrain.TraitsOf(h);
            string Pct(double v) => $"{v:P0}";
            var about = State.About;
            string why = about == null ? "" : "\n" + (about.Reason.Length > 0 ? about.Reason : about.PersonalityText)
                         + (about.DesignedBy.Length > 0 ? (Thai ? $"\n— ออกแบบโดย {about.DesignedBy}" : $"\n— designed by {about.DesignedBy}") : "");
            return Thai
                ? $"{State.Name} · {SpeciesText} นิสัย{PetLines.PersonalityName(State.Personality, true)} · ความผูกพัน {State.Affection}/100{why}\nเรียนรู้จาก: ใช้งาน {h.ActiveDays.Count} วัน {h.ActiveMinutes / 60.0:F0} ชม. · ทำงานดึก {Pct(t.Night)} · แก้ไฟล์ {h.Edits} · build {h.Builds} (พัง {h.BuildFailures}) · ข้อความ {h.Messages} (ไทย {Pct(t.Thai)})"
                : $"{State.Name} · {SpeciesText}, {PetLines.PersonalityName(State.Personality, false)} · affection {State.Affection}/100{why}\nLearned from: {h.ActiveDays.Count} days, {h.ActiveMinutes / 60.0:F0} h · late nights {Pct(t.Night)} · edits {h.Edits} · builds {h.Builds} ({h.BuildFailures} failed) · messages {h.Messages} ({Pct(t.Thai)} Thai)";
        }
    }

    /// <summary>A small picture for the title bar: the egg (with its cracks) or the pet's face.</summary>
    public BitmapSource Avatar => ToBitmap(State.Hatched
        ? PetArt.Render(State.Look, PetPose.Stand, PetFace.Normal)
        : PetArt.RenderEgg(State.EggHue, HatchProgress, State.EggSpots));

    public static BitmapSource ToBitmap(uint[] px)
    {
        var bmp = BitmapSource.Create(PetArt.Size, PetArt.Size, 96, 96, PixelFormats.Pbgra32, null, px, PetArt.Size * 4);
        bmp.Freeze();
        return bmp;
    }

    // ─── lifecycle ───────────────────────────────────────────────────────

    /// <summary>Start learning, and come out on the desktop if it is enabled and not put away. UI thread.</summary>
    public void Start()
    {
        if (_minuteTimer != null) return;
        _minuteTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _minuteTimer.Tick += (_, _) => OnMinute();
        _minuteTimer.Start();
        if (!State.Habits.Bootstrapped && _chats != null)
            _ = Task.Run(BootstrapFromHistory);
        // Not now: Start runs while the main window is still being built, and WPF makes the FIRST window it sees
        // the application's MainWindow — the pet would become the window every dialog and owner lookup uses.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyVisibility, DispatcherPriority.ApplicationIdle);
        Save();
    }

    /// <summary>Let a process that is not the CluadeX app put the pet on the desktop (pettest --live only).</summary>
    public static bool AllowDesktopOutsideApp { get; set; }

    /// <summary>The machine-wide token of "a pet is on the desktop": one pet, whichever CluadeX holds it.</summary>
    public const string DesktopMutexName = @"Local\CluadeX_DesktopPet";

    private System.Threading.Mutex? _desktopToken;

    /// <summary>
    /// May this process put a pet on the desktop at all? Only the CluadeX app itself. Owner 2026-10-18: "มีสัตว์เลี้ยงที่
    /// ฟักแล้ว และมีไข่ตกลงมาใหม่อีกได้อย่างไร" — pagestest / themetest build the real MainWindow, so each run started a
    /// pet from the harness's empty Data folder: a fresh egg fell onto the owner's desktop beside their hatched owl.
    /// </summary>
    public static bool MayShowOnDesktop(string? entryAssemblyName = null) =>
        AllowDesktopOutsideApp || Mcp.McpHostService.IsCluadeXApp(entryAssemblyName);

    /// <summary>Show or hide the desktop window to match the setting and the put-away state.</summary>
    public void ApplyVisibility()
    {
        bool show = Enabled && State.OnDesktop && MayShowOnDesktop();
        if (show && _window == null && System.Windows.Application.Current?.MainWindow == null) return;   // see Start
        if (show && _window == null)
        {
            // Another CluadeX (the installed app beside a portable / DEV copy, each with its own pet.json) already
            // has its pet out: never a second one.
            if (!TakeDesktopToken())
            {
                _log?.Info("Pet", "another CluadeX already has its pet on the desktop · staying in");
                Changed?.Invoke();
                return;
            }
            _window = new Views.Pet.PetWindow(this);
            _window.Closed += (_, _) => { _window = null; ReleaseDesktopToken(); };
            _window.Show();
        }
        else if (!show && _window != null)
        {
            _window.Close();
            _window = null;
            ReleaseDesktopToken();
        }
        Changed?.Invoke();
    }

    private bool TakeDesktopToken()
    {
        if (_desktopToken != null) return true;
        if (AllowDesktopOutsideApp) return true;   // pettest --live: shown on purpose, beside the app's own pet
        try
        {
            var m = new System.Threading.Mutex(false, DesktopMutexName, out bool createdNew);
            if (!createdNew) { m.Dispose(); return false; }
            _desktopToken = m;
            return true;
        }
        catch { return true; }   // no named objects here: one process, one window — as before
    }

    private void ReleaseDesktopToken()
    {
        try { _desktopToken?.Dispose(); } catch { }
        _desktopToken = null;
    }

    public bool IsOut => _window != null;

    public void PutAway()
    {
        Raise(PetMoment.PutAway);
        State.OnDesktop = false;
        _dirty = true;
        Save();
        // Let the goodbye show for a moment.
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        t.Tick += (_, _) => { t.Stop(); ApplyVisibility(); };
        t.Start();
    }

    public void CallBack()
    {
        State.OnDesktop = true;
        _dirty = true;
        Save();
        ApplyVisibility();
        Raise(PetMoment.Hello);
    }

    // ─── learning ────────────────────────────────────────────────────────

    public void NoteMessage(string text)
    {
        lock (_lock) PetBrain.RecordMessage(State.Habits, text);
        Touch();
    }

    public void NoteTool(string tool, bool success)
    {
        string family = PetBrain.ToolFamily(tool);
        lock (_lock) PetBrain.RecordTool(State.Habits, tool, success);
        Touch();
        if (family == "build") Raise(success ? PetMoment.BuildPassed : PetMoment.BuildFailed);
        else if (family == "test" && !success) Raise(PetMoment.BuildFailed);
    }

    public void NoteTurnStarted()
    {
        _turnRunning = true;
        Touch();
    }

    /// <summary>A reply finished. <paramref name="provider"/> is which AI answered ("Anthropic", "Local" …).</summary>
    public void NoteTurnDone(string provider, bool local, bool success)
    {
        _turnRunning = false;
        _lastTurnUtc = DateTime.UtcNow;
        lock (_lock)
        {
            if (local) State.Habits.LocalTurns++; else State.Habits.ApiTurns++;
            State.Habits.ProviderTurns[provider] = State.Habits.ProviderTurns.GetValueOrDefault(provider) + 1;
        }
        Touch();
        Raise(success ? PetMoment.TurnDone : PetMoment.Error);
    }

    /// <summary>The user is using CluadeX right now (counts toward this minute).</summary>
    public void Touch()
    {
        _lastActivityUtc = DateTime.UtcNow;
        _dirty = true;
    }

    /// <summary>A minute passed: count it if CluadeX was really in use, then see whether the egg is ready.</summary>
    private void OnMinute()
    {
        bool appInFront = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().Any(w => w.IsActive) == true;
        bool used = (DateTime.UtcNow - _lastActivityUtc).TotalSeconds < 70 || (appInFront && PetDesktop.IdleSeconds() < 60);
        if (used)
        {
            lock (_lock) PetBrain.RecordMinute(State.Habits, DateTime.Now);
            _dirty = true;
        }
        TryHatch();
        if (_dirty) Save();
        Changed?.Invoke();
    }

    /// <summary>The egg is ready and its owner's AI is being asked what it becomes.</summary>
    public bool HatchPending => _hatching;

    /// <summary>For the contract harness: stands in for the owner's AI (system, request) → (reply, who answered).</summary>
    public Func<string, string, CancellationToken, Task<(string reply, string by)>>? DesignerOverride { get; set; }

    /// <summary>Minutes between attempts when the AI is not available or could not answer usefully.</summary>
    public const int RetryMinutes = 60;
    /// <summary>After this many failed attempts the formula decides instead, so an egg is never stuck forever.</summary>
    public const int MaxAiAttempts = 5;

    /// <summary>Hatch when the egg is ready: ask the owner's AI, never in the middle of a reply it is writing.</summary>
    public void TryHatch()
    {
        if (State.Hatched || _hatching || !PetBrain.ReadyToHatch(State.Habits)) return;
        if (_turnRunning || (DateTime.UtcNow - _lastTurnUtc).TotalSeconds < 30) return;
        if (State.LastHatchAttemptUtc is { } last && (DateTime.UtcNow - last).TotalMinutes < RetryMinutes) return;
        _hatching = true;
        Changed?.Invoke();
        _ = HatchWithAiAsync();
    }

    private bool _hatching;

    /// <summary>The AI the owner uses most, if it can answer now; otherwise the one they have open, if that can.</summary>
    private Func<string, string, CancellationToken, Task<(string reply, string by)>>? Designer()
    {
        if (DesignerOverride != null) return DesignerOverride;
        if (_providers == null) return null;
        Providers.IAiProvider? pick = null;
        if (PetBrain.MostUsedProvider(State.Habits) is { } key && Enum.TryParse<AiProviderType>(key, out var type))
        {
            try { var p = _providers.GetProvider(type); if (p.IsReady) pick = p; } catch { }
        }
        if (pick == null && _providers.ActiveProvider is { IsReady: true } active) pick = active;
        if (pick == null) return null;
        string model = _settings.Settings.ProviderConfigs.TryGetValue(pick.ProviderId, out var cfg) && cfg != null ? cfg.EffectiveModelId : "";
        string by = string.IsNullOrWhiteSpace(model) ? pick.DisplayName : $"{pick.DisplayName} · {model}";
        return async (system, user, ct) => (await pick.GenerateAsync(new List<ChatMessage>(), user, system, ct), by);
    }

    private async Task HatchWithAiAsync()
    {
        Raise(PetMoment.EggAlmost);
        PetDesign? design = null, words = null;
        (PetSpecies sp, double hue, double accent)? template = null;
        string profile = PetDesigner.BuildRequest(State, new Random());
        var ask = Designer();
        if (ask != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                string request = profile;
                string by = "";
                for (int attempt = 1; attempt <= 2 && design == null; attempt++)
                {
                    var (reply, who) = await ask(PetDesigner.SystemPrompt, request, cts.Token);
                    by = who;
                    var (d, error) = PetDesigner.ParseDesign(reply);
                    if (d != null) { design = d; design.DesignedBy = by; break; }
                    _log?.Warn("Pet", $"design attempt {attempt} unusable: {error}");
                    request = profile + $"\n\nYour previous answer could not be used: {error}. Answer again with the corrected JSON object only.";
                }
                if (design == null)
                {
                    var (reply, who) = await ask(PetDesigner.SystemPrompt, PetDesigner.BuildTemplateRequest(profile), cts.Token);
                    if (PetDesigner.ParseTemplate(reply) is { } t)
                    {
                        template = (t.template, t.hue, t.accentHue);
                        words = t.words;
                        words.DesignedBy = who;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.Warn("Pet", "asking the AI to design the pet failed", ex);
            }
        }

        if (design == null && template == null)
        {
            // No AI ready right now: simply wait — the next minute tick asks again once one is (the owner is chatting
            // with it, so it will be). An AI that answered unusably counts as an attempt, retried an hour later; only
            // after several such attempts does the formula decide on its own, so an egg is never stuck forever.
            if (ask != null)
            {
                State.HatchAttempts++;
                State.LastHatchAttemptUtc = DateTime.UtcNow;
            }
            _dirty = true;
            if (ask == null || State.HatchAttempts < MaxAiAttempts)
            {
                _hatching = false;
                Save();
                Changed?.Invoke();
                return;
            }
        }

        void Complete()
        {
            lock (_lock)
            {
                if (design != null) ApplyDesign(State, design, DateTime.UtcNow);
                else if (template is { } t) ApplyTemplate(State, t.sp, t.hue, t.accent, words!, DateTime.UtcNow);
                else PetBrain.Hatch(State, Thai, DateTime.UtcNow);
            }
            _hatching = false;
            _log?.Info("Pet", $"hatched '{State.Name}': {SpeciesText} / {State.Personality}" + (State.About?.DesignedBy is { Length: > 0 } b ? $" (by {b})" : " (formula)"));
            _dirty = true;
            Save();
            Changed?.Invoke();
            Raise(PetMoment.Hatched);
        }
        if (_window != null) _window.PlayHatch(Complete); else Complete();
    }

    /// <summary>Turn the egg into the AI's own creature.</summary>
    public static void ApplyDesign(PetState s, PetDesign d, DateTime nowUtc)
    {
        s.Design = d;
        s.Words = null;
        s.Name = d.Name;
        s.Personality = Enum.TryParse<PetPersonality>(d.Personality, true, out var p) ? p : PetPersonality.Cheerful;
        s.Hatched = true;
        s.HatchedAtUtc = nowUtc;
    }

    /// <summary>Turn the egg into a built-in body, in the colours, name and voice the AI chose.</summary>
    public static void ApplyTemplate(PetState s, PetSpecies species, double hue, double accent, PetDesign words, DateTime nowUtc)
    {
        s.Design = null;
        s.Words = words;
        s.Species = species;
        s.Hue = hue;
        s.AccentHue = accent;
        s.Name = words.Name;
        s.Personality = Enum.TryParse<PetPersonality>(words.Personality, true, out var p) ? p : PetPersonality.Cheerful;
        s.Hatched = true;
        s.HatchedAtUtc = nowUtc;
    }

    /// <summary>The title-bar chip was clicked: pat the pet, or call it back when it was put away.</summary>
    public void Pat()
    {
        if (!Enabled) return;
        if (_window == null) CallBack();
        else _window.Pat();
    }


    private void BootstrapFromHistory()
    {
        try
        {
            var messages = new List<(DateTime, string)>();
            foreach (var meta in _chats!.LoadSessionList().Take(300))
            {
                var session = _chats.LoadSession(meta.Id);
                if (session == null) continue;
                foreach (var m in session.Messages)
                    if (m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content))
                        messages.Add((m.Timestamp, m.Content));
            }
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                lock (_lock) PetBrain.Bootstrap(State.Habits, messages);
                _log?.Info("Pet", $"learned from history: {messages.Count} messages over {State.Habits.ActiveDays.Count} days");
                _dirty = true;
                Save();
                Changed?.Invoke();
                TryHatch();
            });
        }
        catch (Exception ex)
        {
            _log?.Warn("Pet", "reading chat history for the egg failed", ex);
        }
    }

    // ─── affection / moments ─────────────────────────────────────────────

    public void AddAffection(int delta)
    {
        State.Affection = Math.Clamp(State.Affection + delta, 0, 100);
        _dirty = true;
    }

    public void CountPoke() { State.Pokes++; _dirty = true; }

    public void Raise(PetMoment m) => Moment?.Invoke(m);

    /// <summary>A line for this moment in the app's language, or "" when the pet is told to stay quiet.</summary>
    public string Line(PetMoment m, int pick)
    {
        if (!_settings.Settings.PetTalks) return "";
        // Its own voice: the catchphrases the AI wrote for it are half of its small talk.
        if (m == PetMoment.Idle && State.About is { } about && pick % 2 == 0)
        {
            var own = Thai ? about.CatchphrasesTh : about.CatchphrasesEn;
            if (own.Count == 0) own = Thai ? about.CatchphrasesEn : about.CatchphrasesTh;
            if (own.Count > 0) return own[(pick / 2) % own.Count];
        }
        return PetLines.Say(m, State.Hatched ? State.Personality : null, Thai, State.Name, pick);
    }

    // ─── persistence ─────────────────────────────────────────────────────

    private static PetState? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<PetState>(File.ReadAllText(path), JsonOptions);
            if (s != null && s.Habits.Hours.Length != 24) s.Habits.Hours = new int[24];
            return s;
        }
        catch
        {
            try { File.Copy(path, path + ".unreadable", overwrite: true); } catch { }
            return null;
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(State, JsonOptions);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
            _dirty = false;
        }
        catch (Exception ex) { _log?.Warn("Pet", "saving pet.json failed", ex); }
    }

    /// <summary>The app is closing: save and take the pet off the desktop (the setting is left as it is).</summary>
    public void Shutdown()
    {
        _minuteTimer?.Stop();
        Save();
        _window?.Close();
        _window = null;
        ReleaseDesktopToken();
    }

    public void Dispose()
    {
        _minuteTimer?.Stop();
        if (_dirty) Save();
        _window?.Close();
        ReleaseDesktopToken();
    }
}
