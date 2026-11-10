using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// น้อง CluadeX's clips (owner 2026-11-08: "ใช้ digen สร้างวีดีโออารมณ์ … หลายๆ สถานการณ์ … 30+"): real motion for every
/// situation, made with Digen from her picture on flat green, cut to her 1:2 frame and keyed by
/// <see cref="Effects.ChromaKeyEffect"/>. Files: <c>Assets\Buddy\Clips\{name}.mp4</c> next to the app
/// (Assets/Buddy/Clips/README.md lists every clip and how it was made).
/// <para>Each mood is a <see cref="Scene"/>: an opening clip (a reaction, a yawn, a greeting), then clips that follow one
/// another — never the same one twice running when there is a choice — and now and then a small extra (she looks round,
/// fixes her beanie, winks …). Two players take turns: the next clip fades in on top, then the one under it closes. A clip
/// that is missing or fails, or Windows' "show animations" turned off, leaves her on the still pictures with the drawn
/// motion — the clips are never needed for anything to work.</para>
/// </summary>
public partial class CluadexBuddy
{
    /// <summary>A mood's clips. <paramref name="Intro"/>: one of these first (once); <paramref name="Loop"/>: then these, one
    /// after another; <paramref name="Extras"/>: one of these instead, when <paramref name="ExtraEvery"/> has passed;
    /// <paramref name="ThenScene"/>: after the intro, that scene's loop and extras instead of this one's;
    /// <paramref name="JitterSeconds"/>: up to this much more before each extra, so they do not come like clockwork.</summary>
    public sealed record Scene(string[] Intro, string[] Loop, string[] Extras, TimeSpan ExtraEvery, string? ThenScene = null,
                               int JitterSeconds = 15);

    /// <summary>Idle, an extra every <see cref="IdleExtraEvery"/> plus up to <see cref="IdleExtraJitter"/> s, i.e. after one to
    /// three of her 5 s breathing loops (owner 2026-11-10: "idle อวาต้า น้อยไป" — it was 28–43 s of the same loop).</summary>
    public static readonly TimeSpan IdleExtraEvery = TimeSpan.FromSeconds(9);
    public const int IdleExtraJitter = 8;

    /// <summary>Everything she does to pass the time while idle: small moves (glances, her hair, her beanie, a stretch),
    /// play (a wink, a heart, a peace sign, a giggle), and things she picked up (coffee, headphones, her phone, the iPad, a
    /// book). None of them is spoken and none is a sleepy one — yawning and dozing belong to the night extras and to sleep.</summary>
    public static readonly string[] IdleExtras =
    {
        "idle-look", "idle-lookaround", "idle-peek", "idle-hat", "idle-hair", "idle-ears", "idle-earring", "idle-nails",
        "idle-stretch", "stretch-big", "idle-hum", "idle-sway", "idle-wiggle", "idle-wink", "idle-heart", "idle-bored",
        "shy", "sneeze", "hiccup", "love", "blow-kiss", "laugh", "happy-giggle", "happy-peace", "nod-yes", "shrug", "meh",
        "thinking-tap", "salute",
        "coffee", "coffee-cozy", "coffee-cheers", "music-bob", "music-hum", "music-dance", "phone-check", "phone-buzz",
        "ipad-read", "ipad-giggle", "book-read", "book-flip",
        "bored-chin", "bored-slump", "yawn-small",
    };

    /// <summary>A scene that is not one of <see cref="BuddyMood"/>: the owner typing in the chat box while she is idle.</summary>
    public const string ListeningScene = "listening";

    /// <summary>Sad again soon after the last time (owner 2026-11-08: "ท่าโกรธ โมโห … หงุดหงิด"): the second failure in a row
    /// she is fed up, from the third on she is cross. The first is always plain <see cref="BuddyMood.Sad"/>.</summary>
    public const string AnnoyedScene = "annoyed", AngryScene = "angry";

    /// <summary>How close together two failures must be to count as "again".</summary>
    public static readonly TimeSpan SadStreakWindow = TimeSpan.FromMinutes(3);

    private static readonly string[] None = Array.Empty<string>();

    /// <summary>Every scene and its clips (the file names without ".mp4"). Some clips show her with something in her hands
    /// (an iPad, a coffee mug, a book, headphones …): each was made from its own start picture, so they mix in freely.</summary>
    public static readonly IReadOnlyDictionary<string, Scene> Scenes = new Dictionary<string, Scene>
    {
        [BuddyMood.Idle] = new(None, new[] { "idle" }, IdleExtras, IdleExtraEvery, JitterSeconds: IdleExtraJitter),
        [BuddyMood.Working] = new(None, new[] { "working", "working-type", "thinking", "working-ipad", "ipad-read", "book-read" },
            new[]
            {
                "thinking-hmm", "working-aha", "thinking-tap", "thinking-snap", "focus", "salute", "busy", "coffee", "coffee-blow",
                "coffee-hot", "ipad-surprise", "ipad-giggle", "ipad-frown", "ipad-type", "ipad-sigh", "book-flip", "book-aha",
                "search", "search-found",
            }, TimeSpan.FromSeconds(24)),
        [BuddyMood.Waiting] = new(None, new[] { "waiting" },
            new[] { "waiting-wave", "waiting-tap", "waiting-lean", "waiting-knock", "waiting-watch", "confused", "nervous", "shrug" },
            TimeSpan.FromSeconds(12)),
        [BuddyMood.Happy] = new(new[]
            {
                "happy", "happy-clap", "happy-peace", "happy-heart", "happy-giggle", "happy-jump", "happy-dance", "happy-proud",
                "happy-thumbs", "happy-cheer", "cheer-done", "relief", "surprised",
            }, None, None, TimeSpan.Zero, BuddyMood.Idle),
        [BuddyMood.Sad] = new(new[] { "sad", "sad-sorry", "sad-oops", "sad-sniff", "sad-droop", "oops", "facepalm", "cry", "cry-wipe" },
            None, None, TimeSpan.Zero, BuddyMood.Idle),
        [AnnoyedScene] = new(new[] { "annoyed", "annoyed-sigh", "annoyed-tap", "shake-no", "sulk", "sulk-peek" }, None, None, TimeSpan.Zero, BuddyMood.Idle),
        [AngryScene] = new(new[] { "angry", "angry-fume", "angry-cross" }, None, None, TimeSpan.Zero, BuddyMood.Idle),
        [BuddyMood.Sleep] = new(new[] { "yawn", "yawn-big", "yawn-stretch", "pillow-hug" }, new[] { "asleep", "asleep-nod", "pillow-doze" },
            new[] { "asleep-mumble", "asleep-snore" }, TimeSpan.FromSeconds(45)),
        [ListeningScene] = new(None, new[] { "listening", "listening-lean" }, new[] { "listening-hum", "nod-yes" }, TimeSpan.FromSeconds(10)),
    };

    /// <summary>The one-off clips that are not part of a scene: waking up, a suggestion.</summary>
    public static readonly string WakeClip = "wake";
    public static readonly string[] SuggestClips = { "suggest", "suggest-wink", "suggest-point", "suggest-whisper" };

    /// <summary>Clicked, now and then she jumps (owner 2026-11-08: "คลิ๊กที่เธอมีการตกใจได้ในบางครั้ง"); MainWindow rolls the dice.</summary>
    public static readonly string[] StartleClips = { "surprised" };

    // ---- Her spoken lines (owner 2026-11-08: "งานเสร็จแล้วก็เรียก เสร็จแล้วค่ะ ทำหลายๆ แบบ", "ต้องตัดสินใจ อะไรก็มี",
    // "เราไม่อยู่นานๆ ก็ เรียกได้ฮัลโหล มีใครอยู่มั้ย"). Short English lines, said in her own voice in the clip (Digen
    // Real Motion 3.5 with audio). She says one at most every SpeechGap, and never with sound off.

    /// <summary>A long turn finished (it ran at least <see cref="DoneAfter"/>): she says so instead of a plain happy face.</summary>
    public static readonly string[] DoneLines =
        { "done-alldone", "done-tada", "done-finished", "done-mission", "done-yay", "done-yours", "done-fun", "done-easy" };

    /// <summary>A turn waits for the owner (a question, a permission): she calls them.</summary>
    public static readonly string[] DecideLines =
        { "decide-call", "decide-goahead", "decide-which", "decide-check", "decide-ok", "decide-waiting" };

    /// <summary>A turn failed: what she says for plain sad, fed up and cross (the sad streak, <see cref="SadScene"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ErrorLines = new Dictionary<string, string[]>
    {
        [BuddyMood.Sad] = new[] { "err-oops", "err-retry" },
        [AnnoyedScene] = new[] { "err-again" },
        [AngryScene] = new[] { "err-seriously" },
    };

    /// <summary>The owner has been away from the computer a while: she calls out instead of only sleeping (CluadexBuddy.Away.cs).</summary>
    public static readonly string[] AwayLines =
        { "away-hello", "away-hellooo", "away-knock", "away-lonely", "away-still-here", "away-miss", "away-boo", "away-bored" };

    /// <summary>She called and nobody answered: when the owner comes back she sulks first (owner 2026-11-08: "งอน") — says so
    /// when she may talk, or only pouts.</summary>
    public static readonly string[] SulkLines = { "sulk-back" };
    public static readonly string[] SulkClips = { "sulk", "sulk-peek" };

    /// <summary>After she wakes up: "Welcome back!" said, or waved when she may not speak now.</summary>
    public static readonly string[] BackLines = { "hi-back" };
    public static readonly string[] BackClips = { "welcome-back" };

    /// <summary>Every clip in which she speaks words (the rest are gestures, some with small non-verbal sounds).</summary>
    public static readonly HashSet<string> Spoken = new(
        DoneLines.Concat(DecideLines).Concat(ErrorLines.Values.SelectMany(l => l)).Concat(AwayLines).Concat(SulkLines).Concat(BackLines)
            .Concat(new[] { "hi-there", "hi-morning", "hi-evening", "night-late" }));

    /// <summary>At least this long between two spoken lines; inside it she only gestures.</summary>
    public static readonly TimeSpan SpeechGap = TimeSpan.FromMinutes(2);

    /// <summary>A turn must run this long for a "done!" line (a quick answer gets a plain smile).</summary>
    public static readonly TimeSpan DoneAfter = TimeSpan.FromSeconds(15);

    /// <summary>Her greeting when CluadeX opens, by the time of day (local time): "Good morning!" in the morning,
    /// "Good evening!" in the evening, "It's getting late…" at night.</summary>
    public static string[] GreetingsAt(TimeSpan time) => time.Hours switch
    {
        >= 5 and < 11 => new[] { "hi-morning", "greet-morning" },
        >= 17 and < 22 => new[] { "hi-evening", "greet-evening" },
        >= 22 or < 5 => new[] { "night-late", "greet-evening" },
        _ => new[] { "greet", "greet-wai", "hi-there" },
    };

    /// <summary>The scene's extras at that time of day: idle at lunch she has a snack, late at night she gets sleepy.</summary>
    public static string[] ExtrasAt(string scene, TimeSpan time)
    {
        if (!Scenes.TryGetValue(scene, out var s)) return None;
        if (scene != BuddyMood.Idle) return s.Extras;
        string[] more = time >= new TimeSpan(11, 30, 0) && time < new TimeSpan(13, 30, 0) ? LunchExtras
                      : time.Hours >= 22 || time.Hours < 5 ? NightExtras
                      : None;
        return s.Extras.Concat(more).ToArray();
    }

    public static readonly string[] LunchExtras = { "snack", "snack-offer", "snack-look" };
    /// <summary>Late at night, idle, she gets sleepy now and then — but stays awake: no pillow here (hugging it read as
    /// going to sleep, owner 2026-11-10: "เธอหลับบ่อยเกินไป"); the pillow belongs to the sleep scene.</summary>
    public static readonly string[] NightExtras = { "yawn-big", "yawn-stretch", "sleepy-blink" };

    /// <summary>Every clip name, for the check that the files are there.</summary>
    public static IEnumerable<string> AllClipNames =>
        Scenes.Values.SelectMany(s => s.Intro.Concat(s.Loop).Concat(s.Extras))
            .Concat(new[] { TimeSpan.FromHours(8), TimeSpan.FromHours(14), TimeSpan.FromHours(19), TimeSpan.FromHours(23) }.SelectMany(GreetingsAt))
            .Append(WakeClip).Concat(SuggestClips).Concat(StartleClips).Concat(Spoken).Concat(BackClips).Concat(SulkClips).Concat(LunchExtras).Concat(NightExtras)
            .Distinct();

    /// <summary>Her sound on or off (AppSettings.BuddySounds, Settings › General › Appearance; MainWindow keeps it in step).
    /// Off: every clip is muted and she does not pick the spoken ones.</summary>
    public static bool SoundsOn
    {
        get => _soundsOn;
        set
        {
            if (_soundsOn == value) return;
            _soundsOn = value;
            SoundsChanged?.Invoke();
        }
    }
    private static bool _soundsOn = true;
    private static event Action? SoundsChanged;

    /// <summary>Off in tests that need the same picture every time (themetest's renders).</summary>
    public static bool ClipsAllowed { get; set; } = true;

    /// <summary>Where the clips are: next to the app.</summary>
    public static string ClipDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Buddy", "Clips");

    private static readonly Dictionary<string, bool> Usable = new();
    private static readonly Random Dice = new();

    /// <summary>Picks her extras with variety (<see cref="VariedPicker"/>); one per buddy, remembered across scenes.</summary>
    private readonly VariedPicker _extraPicker = new(Dice);

    /// <summary>The extras she did lately, oldest first (tests).</summary>
    public IReadOnlyList<string> RecentExtras => _extraPicker.Recent;

    /// <summary>The clip is on disk and has not failed to play.</summary>
    public static bool HasClip(string name)
    {
        lock (Usable)
        {
            if (!Usable.TryGetValue(name, out bool ok))
                Usable[name] = ok = File.Exists(Path.Combine(ClipDir, name + ".mp4"));
            return ok;
        }
    }

    private static bool ClipsOn => ClipsAllowed && Display == DisplayClips && SystemParameters.ClientAreaAnimation;

    private MediaElement? _front, _pending;
    private bool _clipLoop, _greeted, _clipsHooked, _listening;
    private Action? _clipThen;
    private Scene? _scene;
    private DateTime _nextExtraAt;
    private DateTime _lastSadAt = DateTime.MinValue;
    private int _sadStreak;
    private DateTime _workingSince = DateTime.MaxValue;
    private DateTime _lastSpokeAt = DateTime.MinValue;

    /// <summary>She may say a line now: sound on and the last line at least <see cref="SpeechGap"/> ago.</summary>
    private bool CanTalk => SoundsOn && DateTime.UtcNow - _lastSpokeAt >= SpeechGap;

    /// <summary>One of <paramref name="names"/> she may play now — spoken ones only when she may talk (and then that counts
    /// as talking).</summary>
    private string? PickLine(string[] names, string? not = null)
    {
        bool talk = CanTalk;
        string? line = Pick(talk ? names : names.Where(n => !Spoken.Contains(n)).ToArray(), not);
        if (line != null && Spoken.Contains(line)) _lastSpokeAt = DateTime.UtcNow;
        return line;
    }

    /// <summary>The clip she last said or did from a line list (so the next one is a different one).</summary>
    private string? _lastLine;

    /// <summary>Which scene a failure gets: plain sad, fed up (the second close together) or cross (the third on).</summary>
    private string SadScene()
    {
        var now = DateTime.UtcNow;
        _sadStreak = now - _lastSadAt <= SadStreakWindow ? _sadStreak + 1 : 1;
        _lastSadAt = now;
        return _sadStreak >= 3 ? AngryScene : _sadStreak == 2 ? AnnoyedScene : BuddyMood.Sad;
    }

    /// <summary>The clip playing or about to show ("" = the still pictures), for tests.</summary>
    public string ClipShown { get; private set; } = "";

    /// <summary>The scene playing ("" = none), for tests.</summary>
    public string SceneShown { get; private set; } = "";

    private void HookClips()
    {
        if (_clipsHooked) return;
        _clipsHooked = true;
        foreach (var me in new[] { ClipA, ClipB })
        {
            me.MediaOpened += OnClipOpened;
            me.MediaEnded += OnClipEnded;
            me.MediaFailed += OnClipFailed;
        }
    }

    /// <summary>The mood's scene; false when it has no clip to play (she stays on the stills and the drawn motion).</summary>
    private bool StartMoodClip(string mood, string previous)
    {
        _forcePlay = false;   // a mood change ends an away call (CluadexBuddy.Away.cs)
        if (!ClipsOn) { HideClips(); return false; }
        HookClips();
        var now = DateTime.UtcNow;
        bool longTurn = previous == BuddyMood.Working && now - _workingSince >= DoneAfter;
        if (mood == BuddyMood.Working && previous != BuddyMood.Working) _workingSince = now;
        string scene = mood == BuddyMood.Idle && _listening ? ListeningScene
                     : mood == BuddyMood.Sad ? SadScene()
                     : mood;
        // the first thing she does: a greeting when CluadeX opens, a stretch when she wakes up
        var chain = new List<string>();
        bool woke = mood != BuddyMood.Sleep && previous == BuddyMood.Sleep && HasClip(WakeClip);
        if (woke) chain.Add(WakeClip);
        else if (!_greeted && mood == BuddyMood.Idle && PickLine(GreetingsAt(DateTime.Now.TimeOfDay)) is { } hello) chain.Add(hello);
        _greeted = true;
        // then what she has to say about it: "done!", "your call!", "oops…", "welcome back!"
        string[]? lines = woke ? (CanTalk ? BackLines : BackClips)
                        : mood == BuddyMood.Happy && longTurn ? DoneLines
                        : mood == BuddyMood.Waiting && previous != BuddyMood.Waiting ? DecideLines
                        : ErrorLines.TryGetValue(scene, out var err) ? err
                        : null;
        // a reaction line takes the place of the scene's own opening gesture; one that she may not say now leaves it
        string? line = lines == null ? null : woke ? PickLine(lines) : CanTalk ? PickLine(lines, _lastLine) : null;
        if (line != null && !woke) _lastLine = line;
        if (line != null) chain.Add(line);
        bool skipIntro = line != null && !woke;
        SceneShown = scene;   // what she is in, from the first clip (a greeting or a line plays before the scene's own)
        bool ok = PlayChain(chain, 0, () => StartScene(scene, skipIntro));
        if (!ok) HideClips();
        return ok;
    }

    /// <summary>The clips one after another, then <paramref name="then"/>. A clip that is missing is skipped.</summary>
    private bool PlayChain(IReadOnlyList<string> clips, int from, Func<bool> then)
    {
        for (int i = from; i < clips.Count; i++)
        {
            int next = i + 1;
            if (PlayClip(clips[i], loop: false, () => { if (!PlayChain(clips, next, then)) HideClips(); })) return true;
        }
        return then();
    }

    /// <summary>A scene from the start: its intro (unless <paramref name="skipIntro"/>), then its loop.</summary>
    private bool StartScene(string name, bool skipIntro = false)
    {
        if (!Scenes.TryGetValue(name, out var scene)) scene = Scenes[BuddyMood.Idle];
        _scene = scene;
        SceneShown = name;
        _nextExtraAt = DateTime.UtcNow + scene.ExtraEvery;
        string? intro = skipIntro ? null : Pick(scene.Intro, null);
        if (intro != null && PlayClip(intro, loop: false, () => { if (!ContinueScene()) HideClips(); })) return true;
        return ContinueScene();
    }

    /// <summary>The next clip of the scene's loop (or an extra when one is due). A scene with one loop clip and no extras
    /// loops it seamlessly.</summary>
    private bool ContinueScene()
    {
        var scene = _scene;
        if (scene == null) return false;
        if (scene.ThenScene != null && Scenes.TryGetValue(scene.ThenScene, out var then))
        {
            _scene = scene = then;
            SceneShown = scene == Scenes[BuddyMood.Idle] ? BuddyMood.Idle : SceneShown;
            _nextExtraAt = DateTime.UtcNow + scene.ExtraEvery;
        }
        // idle at lunch or late at night: a few more (a snack, a sleepy yawn)
        var extras = scene == Scenes[BuddyMood.Idle] ? ExtrasAt(BuddyMood.Idle, DateTime.Now.TimeOfDay) : scene.Extras;
        if (extras.Length > 0 && DateTime.UtcNow >= _nextExtraAt)
        {
            _nextExtraAt = DateTime.UtcNow + scene.ExtraEvery + TimeSpan.FromSeconds(Dice.Next(0, scene.JitterSeconds + 1));
            // a different one each time, and none of the last dozen again (it only avoided the clip on screen — the loop —
            // so the same extra could come back twice running)
            string? extra = _extraPicker.Next(extras, HasClip, ClipShown);
            if (extra != null && PlayClip(extra, loop: false, () => { if (!ContinueScene()) HideClips(); })) return true;
        }
        var usable = scene.Loop.Where(HasClip).ToArray();
        if (usable.Length == 0) return false;
        bool single = usable.Length == 1 && extras.All(e => !HasClip(e));
        string next = Pick(usable, ClipShown)!;
        return PlayClip(next, loop: single, single ? null : () => { if (!ContinueScene()) HideClips(); });
    }

    /// <summary>One of <paramref name="names"/> that is on disk, not <paramref name="not"/> when there is another.</summary>
    private static string? Pick(string[] names, string? not)
    {
        var usable = names.Where(HasClip).ToArray();
        if (usable.Length == 0) return null;
        var fresh = usable.Where(n => n != not).ToArray();
        var from = fresh.Length > 0 ? fresh : usable;
        return from[Dice.Next(from.Length)];
    }

    /// <summary>The owner is typing in the chat box (true) or stopped a while ago (false): while she is idle she listens.</summary>
    public void SetListening(bool on)
    {
        if (_listening == on) return;
        _listening = on;
        if (!IsLoaded || !ClipsOn || Shown != BuddyMood.Idle || ClipShown is "" ) return;
        if (on ? SceneShown == BuddyMood.Idle : SceneShown == ListeningScene)
        {
            _forcePlay = false;   // the owner is here: an away call ends
            StartScene(on ? ListeningScene : BuddyMood.Idle);
        }
    }

    /// <summary>A suggestion gesture while she has nothing else to show; back to her scene after. False when it cannot
    /// play (busy, asleep, no clip).</summary>
    public bool Suggest()
    {
        if (!IsLoaded || !ClipsOn || _forcePlay || Shown != BuddyMood.Idle || SuggestClips.Contains(ClipShown)) return false;
        string? clip = Pick(SuggestClips, null);
        return clip != null && PlayClip(clip, loop: false, () => { if (!ContinueScene()) HideClips(); });
    }

    /// <summary>The owner clicked her and she jumps: a startle clip, then back to what she was doing (asleep she dozes off
    /// again). It cuts into an away call or a sulk: the owner who clicked is here. With no clip (her stills or the
    /// slideshow) she hops instead. False when she cannot now: already startled, not on screen.</summary>
    public bool Startle()
    {
        if (!IsLoaded) return false;
        if (!ClipsOn || ClipShown == "") return Hop();
        if (StartleClips.Contains(ClipShown)) return false;
        string? clip = Pick(StartleClips, null);
        if (clip == null) return false;
        if (_forcePlay) { _forcePlay = false; SyncPause(); }
        // a click during a greeting or a line: the scene that chain was leading to (it set SceneShown) follows instead
        string back = SceneShown == "" ? BuddyMood.Idle : SceneShown;
        bool inScene = _scene != null;
        return PlayClip(clip, loop: false, () =>
        {
            if (!(inScene && ContinueScene()) && !StartScene(back, skipIntro: true)) HideClips();
        });
    }

    private DateTime _hopUntil = DateTime.MinValue;

    /// <summary>A startled hop with a little shake (no clip): false while one is still going, or with Windows' animations off.</summary>
    private bool Hop()
    {
        if (!SystemParameters.ClientAreaAnimation || DateTime.UtcNow < _hopUntil) return false;
        _hopUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
        var up = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560) };
        foreach (var (t, y) in new[] { (0.0, 0.0), (0.09, -16.0), (0.24, 0.0), (0.36, -6.0), (0.5, 0.0) })
            up.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), new QuadraticEase()));
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560) };
        foreach (var (t, x) in new[] { (0.0, 0.0), (0.06, -3.0), (0.12, 3.0), (0.18, -2.0), (0.24, 0.0) })
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        Jump.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, up);
        Jump.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shake);
        return true;
    }

    private bool PlayClip(string name, bool loop, Action? then = null)
    {
        if (!HasClip(name)) return false;
        if (name == ClipShown && _front != null && _pending == null)
        {
            // the same clip again: start it over on the same player, no fade
            _clipLoop = loop;
            _clipThen = then;
            _front.Position = TimeSpan.Zero;
            SetMuted(_front, name);
            if (!_paused) _front.Play();
            return true;
        }
        var next = _front == ClipA ? ClipB : ClipA;
        if (_pending != null && _pending != next) Close(_pending);
        _pending = next;
        ClipShown = name;
        _clipLoop = loop;
        _clipThen = then;
        next.BeginAnimation(OpacityProperty, null);
        next.Opacity = 0;
        Panel.SetZIndex(next, 2);
        if (_front != null) Panel.SetZIndex(_front, 1);
        Silence(next);   // not on screen yet: silent until it opens and fades in (OnClipOpened)
        next.Source = new Uri(Path.Combine(ClipDir, name + ".mp4"), UriKind.Absolute);
        next.Play();
        if (_paused) next.Pause();
        return true;
    }

    /// <summary>
    /// Her sound for <paramref name="name"/> on <paramref name="me"/>, decided NOW: on when CluadeX is in front, or for
    /// what is meant to reach the owner wherever they are (a spoken line, an away call); muted otherwise, so a sip of
    /// coffee never sounds from a window in the background. Asked again whenever that can change — each loop, the window
    /// losing or getting the focus, the sound setting (owner 2026-11-08: "ปิดเสียงอวาต้าแล้วทำไมยังมีเสียงหลุดมาอีก …
    /// โดยเฉพาะเวลาไม่ได้โฟกัสโปรแกรม"): it used to be decided once when a clip started, so a working loop begun while the
    /// owner was here went on talking for as long as the run lasted after they left.
    /// </summary>
    /// <para>Only the clip ON SCREEN (<see cref="_front"/>) may ever be heard: the other player, opening or fading out
    /// underneath, is always silent — the owner heard clapping while she was seen playing on her iPad (a clip with no
    /// sound track at all), i.e. the player hidden under her.</para>
    private void SetMuted(MediaElement me, string name)
    {
        bool muted = me != _front || !(SoundsOn && (Spoken.Contains(name) || _forcePlay || _window is not { IsActive: false }));
        me.IsMuted = muted;
        me.Volume = muted || !AudioAllowed ? 0 : 0.5;   // a second guard: silent even if the player missed the mute
    }

    /// <summary>
    /// Can she be heard from this process at all: only from CluadeX itself. A test harness (themetest, animtest, chattest …)
    /// builds her too and never reads settings.json, so <see cref="SoundsOn"/> starts on there — owner 2026-11-09: "ยังได้ยิน
    /// เสียงของน้องอวาต้า ทั้งๆที่ปิดเสียงแล้ว", and the speaker heard was a themetest run (peak 0.88; CluadeX itself 0.00).
    /// The mute logic still runs and is still checked there (<see cref="MediaElement.IsMuted"/>); only the volume stays 0.
    /// </summary>
    public static bool AudioAllowed { get; set; } =
        string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "CluadeX", StringComparison.OrdinalIgnoreCase);

    private static void Silence(MediaElement me) { me.IsMuted = true; me.Volume = 0; }

    /// <summary>The clip a player holds ("" when none).</summary>
    private static string ClipName(MediaElement? me) => Path.GetFileNameWithoutExtension(me?.Source?.LocalPath ?? "");

    /// <summary>Both players' sound asked again (the focus or the sound setting changed).</summary>
    private void ResyncSound()
    {
        if (!_clipsHooked) return;
        foreach (var me in new[] { ClipA, ClipB })
        {
            // a player that is neither on screen nor opening is left over: it must not go on playing under her
            if (me.Source != null && me != _front && me != _pending) Close(me);
            if (me.Source != null) SetMuted(me, ClipName(me));
            else Silence(me);
        }
    }

    /// <summary>The window lost or got the focus: the clips playing now follow at once, not only the next one.</summary>
    public void OnFocusChanged() => ResyncSound();

    private void OnClipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement me || me != _pending) return;
        _pending = null;
        var old = _front;
        _front = me;
        if (old != null && old != me) Silence(old);   // fading out: seen a moment longer, never heard
        SetMuted(me, ClipName(me));
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { FillBehavior = FillBehavior.HoldEnd };
        fadeIn.Completed += (_, _) =>
        {
            if (_front != me) return;
            if (old != null && old != me) Close(old);
            Stills.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        };
        me.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void OnClipEnded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement me || me != _front || _pending != null) return;
        if (_clipLoop)
        {
            me.Position = TimeSpan.Zero;
            SetMuted(me, ClipName(me));
            me.Play();
            return;
        }
        var then = _clipThen;
        _clipThen = null;
        then?.Invoke();   // nothing next: she holds the last frame until the mood changes
    }

    private void OnClipFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement me) return;
        string name = Path.GetFileNameWithoutExtension(me.Source?.LocalPath ?? "");
        lock (Usable) Usable[name] = false;
        App.WriteCrashLog("buddy-clip", e.ErrorException);
        bool wasFront = me == _front;
        if (me == _pending) _pending = null;
        Close(me);
        if (wasFront) _front = null;
        if (_front == null && _pending == null && !ContinueScene()) HideClips();
    }

    /// <summary>Back to the still pictures: both players closed.</summary>
    private void HideClips()
    {
        _pending = null;
        _clipThen = null;
        _scene = null;
        ClipShown = SceneShown = "";
        Stills.BeginAnimation(OpacityProperty, null);
        Stills.Opacity = 1;
        if (!_clipsHooked) return;
        Close(ClipA);
        Close(ClipB);
        _front = null;
    }

    private static void Close(MediaElement me)
    {
        me.BeginAnimation(OpacityProperty, null);
        me.Opacity = 0;
        try { me.Close(); } catch (InvalidOperationException) { }
        me.Source = null;
        Silence(me);
    }

    /// <summary>The clip pauses and resumes with her storyboard (hidden, minimised, or calm while CluadeX is in the background).</summary>
    private void SyncClipPause(bool pause)
    {
        if (!_clipsHooked) return;
        foreach (var me in new[] { _front, _pending })
        {
            if (me?.Source == null) continue;
            try { if (pause) me.Pause(); else me.Play(); } catch (InvalidOperationException) { }
        }
    }
}
