using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using CluadeX.Services;

namespace CluadeX.Helpers;

/// <summary>
/// Measures how long the UI thread takes to get to a keyboard-priority item, from a background thread, and logs every
/// stall over 300 ms with what the app was doing (<see cref="Note"/>), the slowest dispatcher operation behind it and
/// the time the GC held every thread: "the app freezes after a while" becomes a line in the debug log with a duration
/// and a cause, instead of a guess.
///
/// Failure audit 2026-11-05: 5,916 of 6,979 stall lines named no cause, and every timer showed up as the same
/// "DispatcherTimer+&lt;&gt;c.&lt;Restart&gt;b__21_0" (up to 23.7 s). A stall line now also says how long the UI thread
/// really ran operations inside the stall and how many — "busy 40 ms in 3 ops" over a 900 ms stall is a starved
/// process (CPU taken by a build or a local model), not UI code — and a timer is named by its Tick handler.
/// </summary>
public static class UiStallWatchdog
{
    private static volatile string _doing = "";

    /// <summary>What the UI is busy with (switching chat, opening a page…), named in a stall line.</summary>
    public static void Note(string doing) => _doing = doing;

    /// <summary>The longest operation the dispatcher has run since the last stall line, and how long it took.</summary>
    private sealed record SlowOp(string Name, long Ms);
    private static volatile SlowOp? _slowest;
    private static long _opStarted;
    private const long SlowOpMs = 100;

    // Every operation's run time and count, summed on the UI thread, read by the watchdog thread.
    private static long _busyTicks;
    private static long _opCount;

    // DispatcherOperation.Name ("Class.Method" of the delegate) is internal, and read only for an operation worth naming.
    private static readonly PropertyInfo? OpName =
        typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);
    // A DispatcherTimer queues a static lambda with the timer as its argument: the argument names the real handler.
    private static readonly FieldInfo? OpArgs =
        typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TimerTick =
        typeof(DispatcherTimer).GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void WatchOperations(Dispatcher dispatcher)
    {
        // UI thread only, around every operation: a timestamp and two adds, nothing more, unless it was slow.
        dispatcher.Hooks.OperationStarted += (_, _) => _opStarted = Stopwatch.GetTimestamp();
        dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            long elapsed = Stopwatch.GetTimestamp() - _opStarted;
            Interlocked.Add(ref _busyTicks, elapsed);
            Interlocked.Increment(ref _opCount);
            long ms = elapsed * 1000 / Stopwatch.Frequency;
            if (ms < SlowOpMs || ms <= (_slowest?.Ms ?? 0)) return;
            _slowest = new SlowOp($"{NameOf(e.Operation)} @{e.Operation.Priority}", ms);
        };
    }

    /// <summary>The operation's delegate, and for a timer the method(s) its Tick runs. Never throws.</summary>
    public static string NameOf(DispatcherOperation op)
    {
        string name;
        try { name = OpName?.GetValue(op) as string ?? "?"; }
        catch { name = "?"; }
        if (!name.Contains("DispatcherTimer", StringComparison.Ordinal)) return name;
        try
        {
            if (OpArgs?.GetValue(op) is DispatcherTimer timer && TimerTick?.GetValue(timer) is Delegate tick)
            {
                var handlers = tick.GetInvocationList()
                    .Select(d => $"{d.Method.DeclaringType?.Name ?? "?"}.{d.Method.Name}")
                    .Distinct().Take(3);
                return $"timer {string.Join(", ", handlers)} (every {timer.Interval.TotalMilliseconds:0} ms)";
            }
        }
        catch { }
        return name;
    }

    /// <summary>A stall line's cause: the slowest operation, how busy the UI really was, and any GC pause.</summary>
    public static string Describe(TimeSpan gcPause, long busyMs, long ops, long stallMs)
    {
        var op = _slowest;
        _slowest = null;
        string text = op != null ? $" — slowest {op.Name} {op.Ms} ms" : "";
        text += $" — UI busy {busyMs} ms in {ops} ops";
        // Most of the stall ran no dispatcher work: the CPU was taken (a build, a local model on every core), or the
        // thread sat in Win32 message handling / a modal loop (window drag, a native dialog) — not in CluadeX's code.
        if (stallMs >= 500 && busyMs * 4 < stallMs) text += " (outside dispatcher work: CPU starved or a Win32 loop)";
        return gcPause.TotalMilliseconds >= 50 ? text + $" — GC {gcPause.TotalMilliseconds:N0} ms" : text;
    }

    public static void Start(Dispatcher dispatcher, DebugLogService? log)
    {
        if (log == null) return;
        WatchOperations(dispatcher);
        var thread = new Thread(() =>
        {
            while (!dispatcher.HasShutdownStarted)
            {
                Thread.Sleep(250);
                var sw = Stopwatch.StartNew();
                var gcBefore = GC.GetTotalPauseDuration();
                long busyBefore = Interlocked.Read(ref _busyTicks);
                long opsBefore = Interlocked.Read(ref _opCount);
                string doing = _doing;
                try
                {
                    // Input priority: the same queue position a key press gets.
                    var op = dispatcher.BeginInvoke(DispatcherPriority.Input, () => { });
                    while (op.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
                    {
                        if (dispatcher.HasShutdownStarted) return;
                        Thread.Sleep(20);
                    }
                }
                catch { return; }
                long ms = sw.ElapsedMilliseconds;
                if (ms >= 300)
                {
                    long busyMs = (Interlocked.Read(ref _busyTicks) - busyBefore) * 1000 / Stopwatch.Frequency;
                    long ops = Interlocked.Read(ref _opCount) - opsBefore;
                    log.Warn("UI", $"UI stall {ms} ms" + (doing.Length > 0 ? $" — while {doing}" : "")
                        + Describe(GC.GetTotalPauseDuration() - gcBefore, busyMs, ops, ms));
                }
            }
        })
        { IsBackground = true, Name = "UiStallWatchdog", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }
}
