namespace CluadeX.Services;

/// <summary>
/// One honest progress figure for an update download, shared by the splash and the main window's update bar.
///
/// Owner 2026-11-03: "ระบบโหลดต้องแม่นยำเปอเซ็น … อย่าค้าง". Before this the bar was Velopack's own int while the text
/// was the downloader's bytes — two sources that disagreed — and the bar went backwards (a file restarted at 0 after the
/// server ignored a Range, parallel deltas whose sizes arrived late, a failed delta falling back to the full package) or
/// sat at Velopack's 70 % split for the whole patch step. Here:
/// <list type="bullet">
/// <item>the percent is bytes over a FIXED denominator known before the first byte: the full package's size, or the sum
/// of the delta sizes when Velopack will take the delta path (<see cref="Predict"/> mirrors its rule);</item>
/// <item>each file counts its high-water mark, capped at its size, so the figure never goes backwards in a stage — a file
/// that restarts from 0 holds the bar and says so (<see cref="Snapshot.Redownloading"/>) instead of dropping it;</item>
/// <item>a switch of route (the delta failed → full package) resets the denominator explicitly and is reported
/// (<see cref="Snapshot.FellBackToFull"/>), never a silent jump back;</item>
/// <item>once every byte is in, the stage is <see cref="Stage.Preparing"/> (Velopack verifies, Update.exe patches) —
/// a separate step, not a bar stuck at a split value;</item>
/// <item>no byte for <see cref="StallAfter"/> is reported with the real seconds (<see cref="Snapshot.StalledSeconds"/>).</item>
/// </list>
/// Pure: no WPF, no clock of its own (every call passes <c>now</c>), thread-safe. Proven by tests/apitest
/// UpdateProgressWire.
/// </summary>
public sealed class UpdateProgressComposer
{
    /// <summary>No byte for this long while downloading = "connection stalled — retrying…" on the update row.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(12);
    /// <summary>Velopack 1.1.1 maps the delta downloads to 0..70 of its percent, then Update.exe patches up to 100.</summary>
    public const int VelopackDeltaDownloadShare = 70;
    /// <summary>Velopack's default for <c>UpdateManager.MaximumDeltasBeforeFallback</c>.</summary>
    public const int DefaultMaximumDeltas = 10;

    public enum Route { Full, Delta }
    public enum Stage { Downloading, Preparing }

    /// <summary>A package Velopack will download: its file name and the size the release feed states (0 = not stated).</summary>
    public readonly record struct Asset(string FileName, long Size);

    public readonly record struct Snapshot(
        Stage Stage, Route Route, double Percent, long Bytes, long Total, double BytesPerSecond,
        bool Resuming, string? Error, double? StalledSeconds, double StageSeconds,
        bool FellBackToFull, bool Redownloading, int DeltaCount, bool AnyBytes);

    /// <summary>The route Velopack 1.1.1 takes in <c>DownloadUpdatesAsync</c>: deltas only when a base package is on disk
    /// and there are 1..<paramref name="maxDeltas"/> of them, together no bigger than the full package.</summary>
    public static Route Predict(Asset full, IReadOnlyList<Asset> deltas, bool hasBase, int maxDeltas)
    {
        if (!hasBase || deltas.Count == 0 || deltas.Count > maxDeltas) return Route.Full;
        long sum = 0;
        foreach (var d in deltas) sum += Math.Max(0, d.Size);
        return sum > full.Size ? Route.Full : Route.Delta;
    }

    private readonly object _lock = new();
    private Route _route;
    private Asset _full;
    private Asset[] _deltas = [];
    private readonly Dictionary<string, long> _high = new(StringComparer.OrdinalIgnoreCase);    // per file: most bytes ever seen this stage
    private readonly Dictionary<string, long> _raw = new(StringComparer.OrdinalIgnoreCase);     // per file: bytes on disk at the last report
    private readonly Dictionary<string, long> _reported = new(StringComparer.OrdinalIgnoreCase); // per file: total the server stated
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    private Stage _stage;
    private double _shown;
    private bool _fellBack;
    private string? _error;
    private int _vpkPercent;
    private DateTime _lastByteAt, _stageAt;
    private double _speed, _speedPending;
    private DateTime _speedAt;
    private bool _anyBytes;

    /// <summary>A new download of <paramref name="full"/> (or its <paramref name="deltas"/>) begins.</summary>
    public void Begin(Asset full, IReadOnlyList<Asset> deltas, bool hasBase, int maxDeltas, DateTime now)
    {
        lock (_lock)
        {
            _full = full;
            _deltas = deltas.ToArray();
            _fellBack = false;
            _error = null;
            ResetStage(Predict(full, deltas, hasBase, maxDeltas), now);
        }
    }

    private void ResetStage(Route route, DateTime now)
    {
        _route = route;
        _high.Clear(); _raw.Clear(); _reported.Clear(); _failing.Clear();
        _stage = Stage.Downloading;
        _shown = 0;
        _vpkPercent = 0;
        _lastByteAt = now; _stageAt = now;
        _speed = 0; _speedPending = 0; _speedAt = now;
        _anyBytes = false;
    }

    private bool IsFullFile(string name) =>
        (_full.FileName.Length > 0 && name.Equals(_full.FileName, StringComparison.OrdinalIgnoreCase))
        || name.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase);

    private bool IsDeltaFile(string name) =>
        _deltas.Any(d => d.FileName.Length > 0 && name.Equals(d.FileName, StringComparison.OrdinalIgnoreCase))
        || name.EndsWith("-delta.nupkg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The size the feed states for this file, else the size the server reported, else 0.</summary>
    private long SizeOf(string key)
    {
        if (_route == Route.Full) return _full.Size > 0 ? _full.Size : _reported.GetValueOrDefault(key);
        foreach (var d in _deltas)
            if (d.Size > 0 && key.Equals(d.FileName, StringComparison.OrdinalIgnoreCase)) return d.Size;
        return _reported.GetValueOrDefault(key);
    }

    /// <summary>True when every package of the route has a size from the feed — the denominator is fixed up front.</summary>
    private bool Fixed => _route == Route.Full ? _full.Size > 0 : _deltas.Length > 0 && _deltas.All(d => d.Size > 0);

    private long Denominator()
    {
        if (_route == Route.Full) return _full.Size > 0 ? _full.Size : _reported.Values.Sum();
        if (Fixed) return _deltas.Sum(d => d.Size);
        return _reported.Values.Sum();
    }

    private long Counted()
    {
        long sum = 0;
        foreach (var (key, high) in _high)
        {
            long size = SizeOf(key);
            sum += size > 0 ? Math.Min(high, size) : high;
        }
        long den = Denominator();
        return den > 0 ? Math.Min(sum, den) : sum;
    }

    /// <summary>A report from the downloader (<see cref="ResumableDownloader.Progress"/>).</summary>
    public void OnFile(string fileName, long bytes, long total, int failuresInARow, string? error, DateTime now)
    {
        lock (_lock)
        {
            bool full = IsFullFile(fileName), delta = IsDeltaFile(fileName);
            if (_route == Route.Delta && full && !delta) { _fellBack = true; ResetStage(Route.Full, now); }      // the delta path failed
            else if (_route == Route.Full && delta && !full) ResetStage(Route.Delta, now);                       // the prediction was wrong

            // Full route: one file, whatever the URL called it. Delta route: one entry per delta.
            string key = _route == Route.Full ? "\0full" : fileName;
            if (total > 0) _reported[key] = total;
            long size = SizeOf(key);
            long b = Math.Max(0, size > 0 ? Math.Min(bytes, size) : bytes);

            if (_raw.TryGetValue(key, out long prev))
            {
                if (b > prev) { _lastByteAt = now; _speedPending += b - prev; _anyBytes = true; }
            }
            else if (b > 0) { _lastByteAt = now; _anyBytes = true; }   // the first report: a baseline (a resumed file starts far from 0), not speed
            _raw[key] = b;
            _high[key] = Math.Max(_high.GetValueOrDefault(key), b);
            if (failuresInARow > 0) _failing.Add(key); else _failing.Remove(key);
            _error = error;

            double secs = (now - _speedAt).TotalSeconds;
            if (secs >= 1)
            {
                double inst = _speedPending / secs;
                _speed = _speed <= 0 ? inst : _speed * 0.6 + inst * 0.4;
                _speedPending = 0; _speedAt = now;
            }

            if (_stage == Stage.Downloading) Advance(now);
        }
    }

    /// <summary>Velopack's own percent: only a hint of the stage (the bytes are the figure), and the figure itself only
    /// when the feed stated no sizes.</summary>
    public void OnVelopackPercent(int percent, DateTime now)
    {
        lock (_lock)
        {
            percent = Math.Clamp(percent, 0, 100);
            // Patching (≥ 70 on the delta route) and then a low figure again: Velopack fell back to the full package.
            if (_route == Route.Delta && _stage == Stage.Preparing && percent < VelopackDeltaDownloadShare && percent < _vpkPercent)
            {
                _fellBack = true;
                ResetStage(Route.Full, now);
            }
            _vpkPercent = percent;
            if (_stage == Stage.Downloading) Advance(now);
        }
    }

    private void Advance(DateTime now)
    {
        bool done;
        if (_route == Route.Full)
            done = _vpkPercent >= 100 || (_full.Size > 0 && _high.GetValueOrDefault("\0full") >= _full.Size);
        else
            done = _vpkPercent >= VelopackDeltaDownloadShare
                   || (Fixed && _deltas.All(d => _high.GetValueOrDefault(d.FileName) >= d.Size));
        if (done)
        {
            _stage = Stage.Preparing;
            _stageAt = now;
            _shown = 100;
            _failing.Clear();
            return;
        }
        double pct;
        long den = Denominator();
        if (Fixed && den > 0) pct = 100.0 * Counted() / den;
        else pct = _route == Route.Full ? _vpkPercent : 100.0 * _vpkPercent / VelopackDeltaDownloadShare;   // no sizes in the feed
        _shown = Math.Max(_shown, Math.Clamp(pct, 0, 100));
    }

    /// <summary>Where the download stands at <paramref name="now"/>.</summary>
    public Snapshot Read(DateTime now)
    {
        lock (_lock)
        {
            bool downloading = _stage == Stage.Downloading;
            double idle = (now - _lastByteAt).TotalSeconds;
            double? stalled = downloading && idle >= StallAfter.TotalSeconds ? Math.Floor(idle) : null;
            long total = Denominator();
            long bytes = _stage == Stage.Preparing && total > 0 ? total : Counted();
            bool redownloading = downloading && _high.Any(kv => _raw.GetValueOrDefault(kv.Key) < kv.Value);
            return new Snapshot(_stage, _route, _shown, bytes, total,
                stalled != null || !downloading ? 0 : _speed,
                downloading && _failing.Count > 0, downloading ? _error : null, stalled,
                Math.Max(0, (now - _stageAt).TotalSeconds), _fellBack, redownloading,
                _route == Route.Delta ? _deltas.Length : 0, _anyBytes);
        }
    }
}
