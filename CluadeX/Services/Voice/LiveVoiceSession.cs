using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Voice;

/// <summary>What one Live voice session is set up with.</summary>
public sealed class LiveVoiceOptions
{
    public string Model { get; init; } = LiveVoiceSession.Model;
    public string Voice { get; init; } = "marin";
    /// <summary>The session is closed when it has run this long (the owner's cost cap).</summary>
    public double MaxMinutes { get; init; } = 10;
    /// <summary>The user speaks Thai: the instructions ask for Thai and spoken notes are Thai.</summary>
    public bool Thai { get; init; }
}

/// <summary>A delegated request's outcome: what CluadeX's agent answered, and whether it finished.</summary>
public sealed record LiveTaskResult(string Text, bool Ok);

/// <summary>What the mic page gets back for its WebRTC offer: the answer SDP, or why there is none.</summary>
public sealed record LiveOfferResult(string? AnswerSdp, string? SessionId, string? Error);

/// <summary>
/// One GPT-Live voice session (owner 2026-10-19: study "GPT-Live and realtime audio"), driven from CluadeX.
/// GPT-Live (gpt-live-1, Live API) listens and speaks at once and hands the actual work to a backend; here the backend
/// is CluadeX itself — <c>delegation: {type: "client"}</c> — so a spoken request runs through the chat's own agent
/// loop on whatever provider is active, with its permission prompts in the CluadeX window, and only the outcome goes
/// back to be spoken.
///
/// The mic page holds the WebRTC peer connection (microphone, speaker, the "oai-events" data channel) but never the
/// API key: it hands its SDP offer to CluadeX, which makes the <c>POST /v1/live/sessions</c> call and passes back the
/// answer. Data-channel events reach this class through the page (<see cref="OnServerEvent"/>); what this class
/// sends goes out through <see cref="Send"/> to the page's data channel.
///
/// Wire format checked against developers.openai.com/api/docs/guides/live-delegation, live-conversations and
/// voice-webrtc?api=live (2026-10-19): <c>session.delegation.created</c> carries only <c>delegation.id</c> (no task
/// text — the request is rebuilt from <c>session.input_transcript.delta</c>), the result goes back as
/// <c>session.commentary.append {delegation_id, content}</c> of at most 500 tokens each, <c>session.usage.updated</c>
/// carries the cumulative <c>usage.seconds</c>, and a close is <c>session.close</c> → <c>session.closed</c>.
///
/// Money: $0.05 per minute of voice, billed per second, and a WebRTC session bills 15 s at creation (credited against
/// the duration later). The meter shows the larger of the API's own count and the clock since creation; the cap
/// closes the session, and when the page does not confirm the close (or dies) the session is hung up from here.
/// </summary>
public sealed class LiveVoiceSession : IDisposable
{
    public const string Model = "gpt-live-1";
    public const decimal UsdPerMinute = 0.05m;
    public const double InitBilledSeconds = 15;
    /// <summary>One context append is at most 500 tokens; Thai can be close to a token per character.</summary>
    public const int ChunkChars = 450;
    /// <summary>At most this much of a result is handed back to be spoken (the full answer is in the chat).</summary>
    public const int MaxResultChars = ChunkChars * 3;
    private const int MaxTranscriptChars = 4000;

    /// <summary>After <c>session.close</c>, how long to wait for <c>session.closed</c> before hanging up from here.</summary>
    public static TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>A delegation can arrive before the last words of the request are transcribed: wait until the
    /// transcript has been quiet this long (at most <see cref="TranscriptWaitMax"/>).</summary>
    public static TimeSpan TranscriptSettle { get; set; } = TimeSpan.FromMilliseconds(700);
    public static TimeSpan TranscriptWaitMax { get; set; } = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _apiKey;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _oneTask = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private readonly StringBuilder _heard = new();
    private Timer? _tick;
    private Timer? _graceTimer;
    private DateTime _lastHeardUtc = DateTime.MinValue;
    private double _apiSeconds;
    private bool _warned;
    private bool _ended;
    private int _eventSeq;
    private int _queued;
    private char _lastSpeaker;

    public LiveVoiceOptions Options { get; }
    /// <summary>new → connecting → live → closing → closed; or failed when the session could not be created.</summary>
    public string State { get; private set; } = "new";
    public string? SessionId { get; private set; }
    public string? CloseReason { get; private set; }
    public string? LastError { get; private set; }
    public bool HungUp { get; private set; }
    /// <summary>Requests handed to the agent so far (and their delegation ids, in order).</summary>
    public List<string> DelegationIds { get; } = new();
    /// <summary>What the user said last, and what GPT-Live said last (for the meter line).</summary>
    public string UserLine { get; private set; } = "";
    public string AssistantLine { get; private set; } = "";

    /// <summary>A JSON message for the page to send on the data channel.</summary>
    public Action<string>? Send { get; set; }
    /// <summary>Run one spoken request through CluadeX's agent and return its answer. Requests run one at a time.</summary>
    public Func<string, CancellationToken, Task<LiveTaskResult>>? RunTask { get; set; }
    /// <summary>The meter, the state or a transcript line changed (pool thread).</summary>
    public event Action? Changed;
    /// <summary>The session is over: (reason). Raised once.</summary>
    public event Action<string>? Ended;

    public LiveVoiceSession(HttpClient http, string apiBase, string apiKey, LiveVoiceOptions options)
    {
        _http = http;
        _apiBase = (string.IsNullOrWhiteSpace(apiBase) ? "https://api.openai.com/v1" : apiBase).TrimEnd('/');
        _apiKey = apiKey;
        Options = options;
    }

    /// <summary>Voice seconds billed so far: the API's own count, or the clock since creation when that is ahead
    /// (the API reports in steps); never under the 15 s a WebRTC session bills at creation.</summary>
    public double Seconds
    {
        get
        {
            lock (_gate) return Math.Max(InitBilledSeconds, Math.Max(_apiSeconds, _clock.Elapsed.TotalSeconds));
        }
    }

    public decimal CostUsd => State is "new" or "failed" ? 0m : Math.Round((decimal)Seconds / 60m * UsdPerMinute, 4);
    public double CapSeconds => Math.Max(0.05, Options.MaxMinutes) * 60;

    // ───────────────────────── creating the session ─────────────────────────

    /// <summary>The request body of <c>POST /v1/live/sessions</c> for a WebRTC offer.</summary>
    public string BuildCreateBody(string offerSdp) => JsonSerializer.Serialize(new
    {
        session = new
        {
            model = Options.Model,
            instructions = Instructions(Options.Thai),
            audio = new { output = new { voice = Options.Voice } },
            delegation = new { type = "client" },
        },
        transport = new { type = "webrtc", sdp = offerSdp },
    });

    /// <summary>Create the session with the page's SDP offer (the one paid call). Never throws; the error text
    /// never carries the key.</summary>
    public async Task<LiveOfferResult> CreateAsync(string offerSdp, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (State != "new") return new LiveOfferResult(null, null, "this voice session was already started");
            State = "connecting";
        }
        if (string.IsNullOrWhiteSpace(offerSdp) || !offerSdp.StartsWith("v=0", StringComparison.Ordinal))
            return Fail("the page sent no WebRTC offer");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _apiBase + "/live/sessions")
            {
                Content = new StringContent(BuildCreateBody(offerSdp), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Fail(ApiProviderBase.DescribeHttpError("OpenAI", (int)resp.StatusCode, body, _apiKey, Options.Model,
                    "platform.openai.com/api-keys", "platform.openai.com/settings/organization/billing"));
            string? id = null, sdp = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("id", out var i))
                    id = i.GetString();
                if (root.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("sdp", out var a))
                    sdp = a.GetString();
            }
            catch (JsonException) { }
            if (string.IsNullOrEmpty(sdp)) return Fail("the Live API answered without an SDP answer");
            lock (_gate)
            {
                SessionId = id;
                _clock.Restart();   // billing starts at creation
                // Closed while the call was in flight: the close timer hangs it up now that it has an id.
                if (State == "connecting") State = "live";
            }
            _tick = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            RaiseChanged();
            return new LiveOfferResult(sdp, id, null);
        }
        catch (Exception ex)
        {
            return Fail(Redact(ex.Message));
        }
    }

    private LiveOfferResult Fail(string why)
    {
        lock (_gate) { State = "failed"; LastError = why; }
        Finish("failed");
        return new LiveOfferResult(null, null, why);
    }

    private string Redact(string s) => string.IsNullOrEmpty(_apiKey) ? s : s.Replace(_apiKey, "[REDACTED]");

    /// <summary>What GPT-Live is told: talk, delegate the work to CluadeX, speak outcomes short.</summary>
    public static string Instructions(bool thai) =>
        "You are the voice of CluadeX, a desktop AI coding assistant. You talk with the user; CluadeX's own agent does the work. " +
        "Delegate every request that needs the user's code, files, project, tools, the internet, or facts you are not sure of. " +
        "Do not answer those yourself and never invent results. Small talk and short clarifying questions you may handle directly. " +
        "While a delegated task runs, say briefly that you are working on it. When its result arrives, tell the user the outcome " +
        "in one to three short sentences. Do not read code, file paths, URLs or long lists aloud: say they are in the CluadeX window. " +
        "If a result starts with Failed, say plainly that it did not work and why. Files are changed only after the user approves " +
        "in the CluadeX window; if a task is waiting for approval, ask the user to look there. " +
        (thai ? "The user speaks Thai: answer in Thai, polite and natural, unless they switch language."
              : "Answer in the language the user speaks.");

    // ───────────────────────── events from the data channel ─────────────────────────

    /// <summary>A server event the page read off the data channel (one at a time, in order).</summary>
    public void OnServerEvent(JsonElement ev)
    {
        if (ev.ValueKind != JsonValueKind.Object) return;
        string type = Str(ev, "type");
        switch (type)
        {
            case "session.started":
                lock (_gate) { if (State == "connecting") State = "live"; }
                RaiseChanged();
                break;
            case "session.input_transcript.delta":
            {
                string d = Str(ev, "delta");
                lock (_gate)
                {
                    if (_heard.Length + d.Length <= MaxTranscriptChars) _heard.Append(d);
                    _lastHeardUtc = DateTime.UtcNow;
                    // A new turn of speech starts a new line; deltas of the same turn are appended exactly as received.
                    UserLine = Tail(_lastSpeaker == 'u' ? UserLine + d : d);
                    _lastSpeaker = 'u';
                }
                RaiseChanged();
                break;
            }
            case "session.output_transcript.delta":
            {
                string d = Str(ev, "delta");
                lock (_gate)
                {
                    AssistantLine = Tail(_lastSpeaker == 'a' ? AssistantLine + d : d);
                    _lastSpeaker = 'a';
                }
                RaiseChanged();
                break;
            }
            case "session.delegation.created":
                if (ev.TryGetProperty("delegation", out var del) && del.ValueKind == JsonValueKind.Object)
                {
                    string id = Str(del, "id");
                    string target = Str(del, "target");
                    if (id.Length > 0 && (target.Length == 0 || target == "client")) _ = HandleDelegationAsync(id);
                }
                break;
            case "session.usage.updated":
                ReadUsage(ev);
                RaiseChanged();
                break;
            case "session.closed":
                ReadUsage(ev);
                Finish(Str(ev, "reason") is { Length: > 0 } r ? r : "closed");
                break;
            case "error":
                if (ev.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    string msg = Str(err, "message");
                    lock (_gate) LastError = Redact(msg.Length > 300 ? msg[..300] : msg);
                    RaiseChanged();
                }
                break;
        }
    }

    private void ReadUsage(JsonElement ev)
    {
        if (ev.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("seconds", out var s) && s.TryGetDouble(out double sec) && sec >= 0)
            lock (_gate) _apiSeconds = Math.Max(_apiSeconds, sec);
    }

    private static string Tail(string s) => s.Length > 400 ? s[^400..] : s;

    /// <summary>
    /// GPT-Live decided the user's request needs CluadeX: rebuild the request from what was heard since the last
    /// one, run it through the agent (one at a time), and hand the outcome back as commentary for that delegation.
    /// </summary>
    private async Task HandleDelegationAsync(string delegationId)
    {
        lock (_gate) DelegationIds.Add(delegationId);
        // The last words may still be on their way: wait for the transcript to go quiet.
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TranscriptWaitMax)
        {
            DateTime last;
            lock (_gate) last = _lastHeardUtc;
            if (DateTime.UtcNow - last >= TranscriptSettle) break;
            try { await Task.Delay(100, _cts.Token).ConfigureAwait(false); } catch { return; }
        }
        string request;
        lock (_gate)
        {
            request = ChromeDictationService.Clean(_heard.ToString());
            _heard.Clear();
        }
        if (request.Length == 0)
        {
            Append("session.commentary.append", delegationId, Options.Thai
                ? "Failed: ไม่ได้ยินคำขอชัดเจน ขอให้ผู้ใช้พูดอีกครั้ง"
                : "Failed: the request was not heard clearly. Ask the user to say it again.");
            return;
        }

        bool waiting = Interlocked.Increment(ref _queued) > 1;
        if (waiting)
            Append("session.thinking.append", delegationId, "Queued: CluadeX finishes the earlier request first.");
        try
        {
            await _oneTask.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch { Interlocked.Decrement(ref _queued); return; }
        LiveTaskResult result;
        try
        {
            if (IsOver) return;
            Append("session.thinking.append", delegationId, "Started: CluadeX is working on it; this can take a while.");
            result = RunTask == null
                ? new LiveTaskResult("CluadeX is not connected.", false)
                : await RunTask(request, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { result = new LiveTaskResult(Redact(ex.Message), false); }
        finally
        {
            Interlocked.Decrement(ref _queued);
            _oneTask.Release();
        }
        foreach (var part in ResultChunks(result, Options.Thai))
            Append("session.commentary.append", delegationId, part);
    }

    /// <summary>A result as the pieces handed back: "Completed:" / "Failed:" first, code and links left out, cut to
    /// <see cref="MaxResultChars"/>, split into appends of at most <see cref="ChunkChars"/>.</summary>
    public static List<string> ResultChunks(LiveTaskResult result, bool thai)
    {
        string spoken = SpeechText.ForSpeech(result.Text, thai, MaxResultChars);
        if (spoken.Length == 0) spoken = result.Ok ? "(done, no text reply)" : "(no reply)";
        return SpeechText.Chunks((result.Ok ? "Completed: " : "Failed: ") + spoken, ChunkChars);
    }

    private bool IsOver { get { lock (_gate) return _ended || State is "closing" or "closed" or "failed"; } }

    /// <summary>A context append on the data channel. <paramref name="delegationId"/> null = about the session.</summary>
    private void Append(string type, string? delegationId, string content)
    {
        if (IsOver && type != "session.instructions.append") return;
        SendJson(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["event_id"] = NextEventId(),
            ["delegation_id"] = delegationId,
            ["content"] = content,
        });
    }

    private string NextEventId() => "cx_" + Interlocked.Increment(ref _eventSeq);

    private void SendJson(object message)
    {
        try { Send?.Invoke(JsonSerializer.Serialize(message)); } catch { }
    }

    // ───────────────────────── the meter, the cap, closing ─────────────────────────

    private void Tick()
    {
        double sec = Seconds, cap = CapSeconds;
        bool warn = false, stop = false;
        lock (_gate)
        {
            if (State != "live") return;
            if (!_warned && cap >= 90 && sec >= cap - 30) { _warned = true; warn = true; }
            if (sec >= cap) stop = true;
        }
        if (warn)
            Append("session.instructions.append", null, "The voice session ends in 30 seconds (the user's time limit). Tell the user briefly.");
        if (stop) Close("cap");
        else RaiseChanged();
    }

    /// <summary>Close gracefully: <c>session.close</c> on the data channel, then wait for <c>session.closed</c>;
    /// without it in <see cref="CloseGrace"/>, the session is hung up from here so billing stops anyway.</summary>
    public void Close(string reason)
    {
        bool neverStarted;
        lock (_gate)
        {
            if (_ended || State is "closing" or "closed" or "failed") return;
            neverStarted = State == "new";
            State = neverStarted ? "closed" : "closing";
            CloseReason = reason;
        }
        if (neverStarted) { Finish(reason); return; }
        SendJson(new { type = "session.close", event_id = NextEventId() });
        _graceTimer = new Timer(_ => { _ = HangupAsync().ContinueWith(_ => Finish(reason)); }, null, CloseGrace, Timeout.InfiniteTimeSpan);
        RaiseChanged();
    }

    /// <summary>The page is gone (window closed, browser crashed): nobody can send <c>session.close</c> any more,
    /// so the session is hung up from here.</summary>
    public async Task PageGoneAsync(string reason = "page-closed")
    {
        lock (_gate)
        {
            if (_ended) return;
            CloseReason ??= reason;
        }
        if (SessionId != null) await HangupAsync().ConfigureAwait(false);
        Finish(CloseReason ?? reason);
    }

    /// <summary><c>POST /v1/live/sessions/{id}/hangup</c> — ends the session on OpenAI's side. Best effort.</summary>
    public async Task HangupAsync()
    {
        string? id;
        lock (_gate)
        {
            id = SessionId;
            if (id == null || _ended || HungUp) return;
            HungUp = true;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/live/sessions/{Uri.EscapeDataString(id)}/hangup")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch { /* the session also ends by itself when the peer connection drops */ }
    }

    private void Finish(string reason)
    {
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            if (State != "failed") State = "closed";
            CloseReason ??= reason;
            _clock.Stop();
        }
        try { _tick?.Dispose(); } catch { }
        try { _graceTimer?.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        RaiseChanged();
        try { Ended?.Invoke(CloseReason ?? reason); } catch { }
    }

    public bool IsEnded { get { lock (_gate) return _ended; } }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose()
    {
        Finish(CloseReason ?? "disposed");
        try { _cts.Dispose(); } catch { }
    }
}
