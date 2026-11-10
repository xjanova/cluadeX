using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CluadeX.Models;

namespace CluadeX.Services.Voice.Tts;

/// <summary>One reply to be read aloud: its speech text (code and links left out), who wrote it and the audio once made.</summary>
public sealed class SpokenItem : INotifyPropertyChanged
{
    private static int _ids;
    private bool _heard;
    private string? _error;

    public SpokenItem(string text, bool thai, string? girl, ChatMessage? message = null, string? preview = null)
    {
        Id = Interlocked.Increment(ref _ids);
        Text = text;
        Thai = thai;
        Girl = girl ?? "";
        Message = message;
        string p = (preview ?? text).Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (p.Contains("  ", StringComparison.Ordinal)) p = p.Replace("  ", " ", StringComparison.Ordinal);
        Preview = p.Length > 90 ? p[..90].TrimEnd() + "…" : p;
    }

    public int Id { get; }
    /// <summary>What is said (SpeechText.ForSpeech of the reply).</summary>
    public string Text { get; }
    public bool Thai { get; }
    public string Lang => Thai ? "th-TH" : "en-US";
    /// <summary>The girl who wrote it (ChatCharacter.Own) — her voice and mood.</summary>
    public string Girl { get; }
    /// <summary>The chat bubble it was read from (null for one made elsewhere). Its IsVoiceCurrent / IsVoicePlaying
    /// light the bubble while it plays.</summary>
    public ChatMessage? Message { get; }
    /// <summary>The first words, for the player box.</summary>
    public string Preview { get; }
    /// <summary>The face shown in the player box.</summary>
    public ChatCharacter? Character => Message?.Character;

    /// <summary>The synthesized audio, kept so going back to it plays at once (and a paid voice is not paid twice).
    /// Null for the browser voice, which the mic window speaks from the text.</summary>
    public SpeechAudio? Audio { get; set; }

    /// <summary>Played to the end, skipped, or stopped: the queue does not come back to it by itself.</summary>
    public bool Heard { get => _heard; internal set { if (_heard != value) { _heard = value; Raise(); } } }
    /// <summary>Why it could not be played (null when it played).</summary>
    public string? Error { get => _error; internal set { if (_error != value) { _error = value; Raise(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Where the queue's sound comes out (the app's own player or the mic window). Called on the UI thread.</summary>
public interface IReplyVoiceOutput
{
    /// <summary>Make whatever <see cref="Start"/> needs (synthesize into <see cref="SpokenItem.Audio"/>, unless it is
    /// there already or the mic window will speak the text itself). Throws with a message the user can act on.</summary>
    Task PrepareAsync(SpokenItem item, CancellationToken ct);
    /// <summary>Start playing <paramref name="item"/>. False = it cannot be played now (the reason is in the item's Error).</summary>
    bool Start(SpokenItem item);
    /// <summary>The sound can be paused and resumed where it was (the in-app player); else Pause stops and Play starts again.</summary>
    bool CanPause { get; }
    void Pause();
    void Resume();
    void Stop();
    /// <summary>The item that was started ended by itself (error = null) or failed to play.</summary>
    event Action<SpokenItem, string?>? Ended;
}

/// <summary>
/// The replies read aloud, one after another (owner 2026-11-10: "เสียงของเอไอ ควรเล่นไปเรื่อยๆ ไม่ตัด ต่อให้มีของใหม่มาก็ให้รอคิว
/// ทำเป็นกล่อง เพลย์เสียงไว้ ว่าจะเดินหน้าถอยหลัง ฟังเสียงได้"). A new reply never cuts the one being read: it waits its turn.
/// The list is also the history the player box steps through (back / forward / play / pause / stop); a reply clicked in the
/// chat is played at once (the user asked for it) and the queue carries on after it.
/// <para>Rules. A finished item is followed by the first not-yet-heard one after it; none = idle. Paused stays paused
/// when a new reply arrives. Stop silences and drops what was waiting (those count as heard). <see cref="Hold"/> (a
/// GPT-Live call is talking) keeps new replies waiting without playing them. Everything runs on the UI thread.</para>
/// </summary>
public sealed class ReplyVoiceQueue : INotifyPropertyChanged
{
    public enum PlayState { Idle, Preparing, Playing, Paused }

    /// <summary>Most replies kept for going back to; the oldest heard ones leave first.</summary>
    public const int MaxItems = 30;
    /// <summary>Most items that keep their audio (an mp3 of a long reply is 1–2 MB); farther ones are made again if played.</summary>
    public const int MaxCachedAudio = 8;

    private IReplyVoiceOutput _output;
    private CancellationTokenSource? _prepCts, _prefetchCts;
    private SpokenItem? _current;
    private PlayState _state;
    private bool _hold;
    private int _playToken;
    /// <summary>The user stepped back or forward in the box: it carries on down the list from there, like a playlist,
    /// heard or not. Off again after a click on a bubble (just that one, then whatever waits), Stop, or the end.</summary>
    private bool _browse;

    public ReplyVoiceQueue(IReplyVoiceOutput output)
    {
        _output = output;
        _output.Ended += OnEnded;
        Items.CollectionChanged += (_, _) => RaiseAll();
    }

    /// <summary>A test (or a harness) swaps in a silent output.</summary>
    public IReplyVoiceOutput Output
    {
        get => _output;
        set
        {
            if (ReferenceEquals(value, _output)) return;
            Stop();
            _output.Ended -= OnEnded;
            _output = value;
            _output.Ended += OnEnded;
        }
    }

    public ObservableCollection<SpokenItem> Items { get; } = new();
    public SpokenItem? Current => _current;
    public int CurrentIndex => _current == null ? -1 : Items.IndexOf(_current);
    public PlayState State => _state;
    public bool IsPlaying => _state == PlayState.Playing;
    public bool IsPreparing => _state == PlayState.Preparing;
    public bool IsPaused => _state == PlayState.Paused;
    /// <summary>Sound is on its way or playing (the play / pause button shows pause).</summary>
    public bool IsActive => _state is PlayState.Playing or PlayState.Preparing;
    public bool HasItems => Items.Count > 0;
    public bool CanPrevious => CurrentIndex > 0;
    public bool CanNext => CurrentIndex >= 0 && CurrentIndex < Items.Count - 1;
    /// <summary>Replies after the current one that have not been read yet.</summary>
    public int Waiting => Items.Skip(CurrentIndex + 1).Count(i => !i.Heard);
    /// <summary>"2 / 5".</summary>
    public string Position => Items.Count == 0 ? "" : $"{Math.Max(CurrentIndex, 0) + 1} / {Items.Count}";

    /// <summary>A GPT-Live call is talking: new replies wait without playing until it is released.</summary>
    public bool Hold
    {
        get => _hold;
        set
        {
            if (_hold == value) return;
            _hold = value;
            Raise();
            if (!value && _state == PlayState.Idle) PlayNextWaiting();
        }
    }

    /// <summary>The item was started, could not be made, or could not play (error text or null).</summary>
    public event Action<SpokenItem, string?>? Failed;

    // ─── commands for the player box ───
    private ICommand? _playPause, _prev, _next, _stop, _close, _playItem;
    public ICommand PlayPauseCommand => _playPause ??= new RelayCmd(_ => TogglePause());
    public ICommand PreviousCommand => _prev ??= new RelayCmd(_ => Previous());
    public ICommand NextCommand => _next ??= new RelayCmd(_ => Next());
    public ICommand StopCommand => _stop ??= new RelayCmd(_ => Stop());
    public ICommand CloseCommand => _close ??= new RelayCmd(_ => Clear());
    public ICommand PlayItemCommand => _playItem ??= new RelayCmd(o => { if (o is SpokenItem i) Play(i); });

    /// <summary>A new reply to read: it waits for the one being read, then plays. Plays at once when nothing is.</summary>
    public void Enqueue(SpokenItem item)
    {
        Items.Add(item);
        Trim();
        if (_state == PlayState.Idle && !_hold) PlayAt(Items.IndexOf(item));
        else { RaiseAll(); if (_state == PlayState.Playing) Prefetch(); }
    }

    /// <summary>The user asked for this one (a click on its bubble, or on it in the box): it plays now. The one it
    /// interrupts counts as heard, and what was waiting plays after it. A click on the one playing pauses it.</summary>
    public void Play(SpokenItem item)
    {
        int at = Items.IndexOf(item);
        if (at >= 0 && ReferenceEquals(item, _current) && _state != PlayState.Idle) { TogglePause(); return; }
        if (at < 0)
        {
            // Not in the list yet (an older reply): right after the current one, so the waiting ones still follow it.
            at = _current == null ? Items.Count : CurrentIndex + 1;
            Items.Insert(at, item);
            Trim();
            at = Items.IndexOf(item);
        }
        if (_current != null && _state != PlayState.Idle && !ReferenceEquals(_current, item)) _current.Heard = true;
        item.Heard = false;
        _browse = false;
        PlayAt(at);
    }

    /// <summary>The reply a chat bubble belongs to, if it is in the list.</summary>
    public SpokenItem? ItemOf(ChatMessage message) => Items.LastOrDefault(i => ReferenceEquals(i.Message, message));

    public void TogglePause()
    {
        switch (_state)
        {
            case PlayState.Playing:
                if (_output.CanPause) _output.Pause();
                else { _playToken++; _output.Stop(); }
                SetState(PlayState.Paused);
                break;
            case PlayState.Preparing:
                CancelPrep();
                SetState(PlayState.Paused);
                break;
            case PlayState.Paused:
                if (_current == null) { SetState(PlayState.Idle); PlayNextWaiting(); }
                else if (_output.CanPause && _resumable) { _output.Resume(); SetState(PlayState.Playing); }
                else PlayAt(CurrentIndex);
                break;
            default:
                if (_current != null && !_current.Heard) PlayAt(CurrentIndex);
                else if (FirstWaitingAfter(CurrentIndex) is int w) PlayAt(w);
                else if (_current != null) { _current.Heard = false; PlayAt(CurrentIndex); }
                else if (Items.Count > 0) PlayAt(Items.Count - 1);
                break;
        }
    }

    /// <summary>Skip forward: the next one in the list (heard or not); at the end, silence.</summary>
    public void Next()
    {
        if (_current == null) { if (Items.Count > 0) PlayAt(0); return; }
        _current.Heard = true;
        _browse = true;
        int at = CurrentIndex;
        if (at < Items.Count - 1) { Items[at + 1].Heard = false; PlayAt(at + 1); }
        else Silence(PlayState.Idle);
    }

    /// <summary>Back one (the first one plays again from its start).</summary>
    public void Previous()
    {
        if (_current == null) { if (Items.Count > 0) PlayAt(Items.Count - 1); return; }
        int at = Math.Max(CurrentIndex - 1, 0);
        if (_state != PlayState.Idle && at != CurrentIndex) _current.Heard = true;
        _browse = true;
        Items[at].Heard = false;
        PlayAt(at);
    }

    /// <summary>Silence now; what was waiting is dropped (counted as heard), the list stays for going back.</summary>
    public void Stop()
    {
        foreach (var i in Items.Skip(Math.Max(CurrentIndex, 0))) i.Heard = true;
        Silence(PlayState.Idle);
    }

    /// <summary>Silence and empty the box (the user closed it, or turned reading aloud off).</summary>
    public void Clear()
    {
        Silence(PlayState.Idle);
        SetCurrent(null);
        foreach (var i in Items) i.Audio = null;
        Items.Clear();
        RaiseAll();
    }

    private bool _resumable;

    private void Silence(PlayState to)
    {
        _playToken++;
        _browse = false;
        CancelPrep();
        try { _prefetchCts?.Cancel(); } catch { }   // nothing waits any more: a paid voice is not made for nothing
        _prefetchCts = null;
        _output.Stop();
        _resumable = false;
        SetState(to);
    }

    private void CancelPrep()
    {
        try { _prepCts?.Cancel(); } catch { }
        _prepCts = null;
    }

    private async void PlayAt(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        var item = Items[index];
        int token = ++_playToken;
        CancelPrep();
        _output.Stop();
        _resumable = false;
        SetCurrent(item);
        item.Error = null;
        SetState(PlayState.Preparing);
        var cts = _prepCts = new CancellationTokenSource();
        try
        {
            await PrepareOnceAsync(item, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (token != _playToken) return;
            Fail(item, ex.Message);
            return;
        }
        if (token != _playToken || cts.IsCancellationRequested) return;
        if (!_output.Start(item))
        {
            Fail(item, item.Error ?? "could not play");
            return;
        }
        _resumable = true;
        SetState(PlayState.Playing);
        KeepAudioNear(index);
        Prefetch();
    }

    private void Fail(SpokenItem item, string error)
    {
        item.Error = error;
        item.Heard = true;
        Failed?.Invoke(item, error);
        SetState(PlayState.Idle);
        // One that cannot be made (no key, no internet) should not hold up the rest — but every waiting one would most
        // likely fail the same way, so the queue stops here; the user can press play again.
    }

    private void OnEnded(SpokenItem item, string? error)
    {
        if (!ReferenceEquals(item, _current) || _state is not (PlayState.Playing or PlayState.Paused)) return;
        _resumable = false;
        item.Heard = true;
        if (error != null)
        {
            item.Error = error;
            Failed?.Invoke(item, error);
        }
        SetState(PlayState.Idle);
        if (_hold) return;
        int here = CurrentIndex;
        if (_browse && here >= 0 && here < Items.Count - 1) { Items[here + 1].Heard = false; PlayAt(here + 1); return; }
        _browse = false;
        PlayNextWaiting();
    }

    private void PlayNextWaiting()
    {
        if (FirstWaitingAfter(CurrentIndex) is int at) PlayAt(at);
    }

    private int? FirstWaitingAfter(int index)
    {
        for (int i = Math.Max(index + 1, 0); i < Items.Count; i++)
            if (!Items[i].Heard) return i;
        return null;
    }

    /// <summary>Make the next waiting reply while this one plays, so it follows without a gap.</summary>
    private async void Prefetch()
    {
        if (FirstWaitingAfter(CurrentIndex) is not int at) return;
        var next = Items[at];
        if (next.Audio != null) return;
        if (_making.ContainsKey(next)) return;
        try { _prefetchCts?.Cancel(); } catch { }
        var cts = _prefetchCts = new CancellationTokenSource();
        try { await PrepareOnceAsync(next, cts.Token); }
        catch { /* PlayAt makes it again and reports the error then */ }
    }

    /// <summary>The audio being made per item: the one made ahead (Prefetch) is waited for when it comes up to play,
    /// not made a second time (ElevenLabs and OpenAI bill every one).</summary>
    private readonly Dictionary<SpokenItem, Task> _making = new();

    private async Task PrepareOnceAsync(SpokenItem item, CancellationToken ct)
    {
        if (_making.TryGetValue(item, out var running))
        {
            try { await running.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* the one ahead failed (or was cancelled): made again below, and its error is reported then */ }
            if (item.Audio != null) return;
        }
        var task = _output.PrepareAsync(item, ct);
        _making[item] = task;
        try { await task; }
        finally { if (_making.TryGetValue(item, out var t) && ReferenceEquals(t, task)) _making.Remove(item); }
    }

    private void KeepAudioNear(int index)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Math.Abs(i - index) > MaxCachedAudio / 2 && !ReferenceEquals(Items[i], _current)) Items[i].Audio = null;
    }

    private void Trim()
    {
        while (Items.Count > MaxItems)
        {
            var old = Items.FirstOrDefault(i => i.Heard && !ReferenceEquals(i, _current))
                      ?? Items.FirstOrDefault(i => !ReferenceEquals(i, _current));
            if (old == null) break;
            old.Audio = null;
            Items.Remove(old);
        }
    }

    private void SetCurrent(SpokenItem? item)
    {
        if (ReferenceEquals(item, _current)) return;
        if (_current?.Message is { } was) { was.IsVoiceCurrent = false; was.IsVoicePlaying = false; }
        _current = item;
        if (item?.Message is { } now) now.IsVoiceCurrent = true;
        RaiseAll();
    }

    private void SetState(PlayState s)
    {
        _state = s;
        if (_current?.Message is { } m) m.IsVoicePlaying = s is PlayState.Playing or PlayState.Preparing;
        RaiseAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private void RaiseAll()
    {
        foreach (var n in new[] { nameof(Current), nameof(CurrentIndex), nameof(State), nameof(IsPlaying), nameof(IsPreparing), nameof(IsPaused),
                     nameof(IsActive), nameof(HasItems), nameof(CanPrevious), nameof(CanNext), nameof(Waiting), nameof(Position) })
            Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    private sealed class RelayCmd(Action<object?> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run(parameter);
    }
}
