using System.IO;
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
        // Agent edits: a throwaway project, a 600-line file written and re-written as the agent would.
        string proj = Path.Combine(Path.GetTempPath(), "cx-perf-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(proj, "src"));
        ApplyWorkingDirectory(proj, announce: false, showEditor: false);
        await Task.Delay(1500);
        for (int i = 0; i < 4; i++)
        {
            string rel = Path.Combine("src", $"File{i % 2}.cs");
            var lines = Enumerable.Range(0, 600).Select(n => $"    public int Value{n}_{i} => {n} * {i}; // line {n}");
            File.WriteAllText(Path.Combine(proj, rel), "namespace Probe;\npublic class C\n{\n" + string.Join("\n", lines) + "\n}\n");
            FileMutatedByAgent?.Invoke(rel, 1, null);
            await Task.Delay(2500);
            navigate("Chat");
            await Task.Delay(800);
        }
        _debugLog?.Info("Perf", "probe done");
    }
#endif
}
