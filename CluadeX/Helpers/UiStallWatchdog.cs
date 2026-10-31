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

    // DispatcherOperation.Name ("Class.Method" of the delegate) is internal, and read only for an operation worth naming.
    private static readonly PropertyInfo? OpName =
        typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void WatchOperations(Dispatcher dispatcher)
    {
        // UI thread only, around every operation: a timestamp, nothing more, unless it was slow.
        dispatcher.Hooks.OperationStarted += (_, _) => _opStarted = Stopwatch.GetTimestamp();
        dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            long ms = (long)Stopwatch.GetElapsedTime(_opStarted).TotalMilliseconds;
            if (ms < SlowOpMs || ms <= (_slowest?.Ms ?? 0)) return;
            string name;
            try { name = OpName?.GetValue(e.Operation) as string ?? "?"; }
            catch { name = "?"; }
            _slowest = new SlowOp($"{name} @{e.Operation.Priority}", ms);
        };
    }

    /// <summary>A stall line's cause: the slowest operation, and any GC pause inside the stall.</summary>
    private static string Describe(TimeSpan gcPause)
    {
        var op = _slowest;
        _slowest = null;
        string text = op != null ? $" — slowest {op.Name} {op.Ms} ms" : "";
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
                    log.Warn("UI", $"UI stall {ms} ms" + (doing.Length > 0 ? $" — while {doing}" : "")
                        + Describe(GC.GetTotalPauseDuration() - gcBefore));
            }
        })
        { IsBackground = true, Name = "UiStallWatchdog", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }
}
