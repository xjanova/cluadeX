using System.Diagnostics;
using System.Windows.Threading;
using CluadeX.Services;

namespace CluadeX.Helpers;

/// <summary>
/// Measures how long the UI thread takes to get to a keyboard-priority item, from a background thread, and logs every
/// stall over 300 ms with what the app was doing (<see cref="Note"/>): "the app freezes after a while" becomes a line
/// in the debug log with a duration and a cause, instead of a guess.
/// </summary>
public static class UiStallWatchdog
{
    private static volatile string _doing = "";

    /// <summary>What the UI is busy with (switching chat, opening a page…), named in a stall line.</summary>
    public static void Note(string doing) => _doing = doing;

    public static void Start(Dispatcher dispatcher, DebugLogService? log)
    {
        if (log == null) return;
        var thread = new Thread(() =>
        {
            while (!dispatcher.HasShutdownStarted)
            {
                Thread.Sleep(250);
                var sw = Stopwatch.StartNew();
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
                    log.Warn("UI", $"UI stall {ms} ms" + (doing.Length > 0 ? $" — while {doing}" : ""));
            }
        })
        { IsBackground = true, Name = "UiStallWatchdog", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }
}
