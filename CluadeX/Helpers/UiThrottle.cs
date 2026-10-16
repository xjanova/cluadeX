using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CluadeX.Helpers;

/// <summary>
/// Paces a stream of UI updates (reply tokens): at most one update queued at a time, no more often than every
/// <c>intervalMs</c>, and queued at Background priority — below keyboard input. Updates used to go every 3 tokens at
/// Normal priority, above Input, each one re-parsing the whole reply's markdown: once a reply grew long the UI could
/// not keep up, the queue piled up ahead of the keyboard, and the chat box stopped taking typing.
/// </summary>
public sealed class UiThrottle
{
    private readonly long _intervalTicks;
    private long _last;
    private int _queued;

    public UiThrottle(int intervalMs = 80) => _intervalTicks = Stopwatch.Frequency * intervalMs / 1000;

    /// <summary>True when an update may be posted now: none is waiting and the interval has passed. The caller then
    /// builds its snapshot and calls <see cref="Post"/>; otherwise it skips this token (a later one, or the end-of-reply
    /// finaliser, shows the text).</summary>
    public bool Due => Volatile.Read(ref _queued) == 0 && Stopwatch.GetTimestamp() - Volatile.Read(ref _last) >= _intervalTicks;

    /// <summary>Queue <paramref name="update"/> on the UI thread at Background priority.</summary>
    public void Post(Action update)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        Volatile.Write(ref _last, Stopwatch.GetTimestamp());
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Volatile.Write(ref _queued, 0);
            update();
        });
    }
}
