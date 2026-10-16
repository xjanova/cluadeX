namespace CluadeX.ViewModels;

public partial class ChatViewModel
{
#if DEBUG
    /// <summary>
    /// Debug builds with CLUADEX_PERF_PROBE=1: after startup, switch between the most recent chats and between the
    /// Chat and Code pages, the way the owner does, and log each switch's time ("Perf" lines) plus any UI stall the
    /// watchdog sees. The app hides its automation tree, so this is how a switch gets measured instead of guessed.
    /// </summary>
    public async Task RunPerfProbeAsync(Action<string> navigate)
    {
        _debugLog?.Info("Perf", "probe start");
        var ids = Sessions.OrderByDescending(s => s.UpdatedAt).Take(6).Select(s => s.Id).ToList();
        for (int round = 0; round < 2; round++)
        {
            foreach (var id in ids)
            {
                await LoadSessionAsync(id);
                await Task.Delay(700);
            }
            foreach (var page in new[] { "Code", "Chat", "Code", "Chat" })
            {
                navigate(page);
                await Task.Delay(700);
            }
        }
        _debugLog?.Info("Perf", "probe done");
    }
#endif
}
