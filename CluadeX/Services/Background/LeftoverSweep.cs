using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CluadeX.Services.Background;

/// <summary>
/// What the last CluadeX run left behind when it was closed or crashed mid-turn (owner 2026-11-11: "ถ้าเราออกจากโปรแกรม
/// ในขณะที่ แชทยังทำงาน …"), cleared once at start-up, off the UI thread:
/// <list type="bullet">
/// <item>Helpers it started (<see cref="AppChildren"/>) that are still running. They are normally in the kill-on-close
/// job and die with CluadeX; one the job could not take (<see cref="AppChildren.Adopt"/> is best effort) outlived it.
/// Each start is written to <c>app-children.json</c> beside codex.db with its start time, and only a process with that
/// pid AND that start time is ended — never anything picked by name (the owner's own terminal <c>claude</c> or
/// <c>codex</c> must not be touched), never a helper of a CluadeX that is still running.</item>
/// <item>Per-turn folders and files the CLI providers make and delete at the turn's end: Grok's and Gemini's turn
/// homes under %LOCALAPPDATA% (<see cref="Providers.GrokTurnHome"/>, <see cref="Providers.GeminiTurnHome"/>, which hold
/// a copy of the sign-in), Antigravity's turn plugins, Claude Dev's tool tickets and effort reports, Grok's prompt
/// files. They used to wait a day for the next turn of that provider to sweep them. Only while no other CluadeX runs
/// (one that does may be using them); then the old one-day rule stays.</item>
/// </list>
/// </summary>
public static class LeftoverSweep
{
    private static readonly object Gate = new();
    private static string? _ledger;
    private static readonly int Me = Environment.ProcessId;

    /// <summary>Only what is older than this run is the last run's: a turn this CluadeX starts while the sweep runs keeps
    /// its folder. (Tests set it to now.)</summary>
    public static DateTime Before { get; set; } = SelfStartUtc();

    private static DateTime SelfStartUtc()
    {
        try { using var self = Process.GetCurrentProcess(); return self.StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>Where helper starts are written (set at start-up, beside codex.db). Null = not recorded (tests, tools).</summary>
    public static string? LedgerPath
    {
        get => _ledger;
        set { lock (Gate) _ledger = value; }
    }

    /// <summary>Test seam: is a CluadeX other than this process running? (It may be using the turn folders.)</summary>
    public static Func<bool> OtherCluadexRunning { get; set; } = DefaultOtherCluadex;

    private static bool DefaultOtherCluadex()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
                using (p)
                    if (p.Id != Me && p.ProcessName.StartsWith("CluadeX", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { return true; }   // cannot tell: act as if one runs (keep the folders)
        return false;
    }

    private static long StartTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    /// <summary>Is the process <paramref name="pid"/> the one that started at <paramref name="start"/> (a reused pid is
    /// another program)?</summary>
    private static Process? Same(int pid, long start)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (!p.HasExited && start != 0 && Math.Abs(StartTicks(p) - start) < TimeSpan.TicksPerSecond) return p;
            p.Dispose();
        }
        catch { }
        return null;
    }

    // { "<owner pid>": { "start": ticks, "children": [ { "pid": n, "start": ticks, "name": "x" } ] } }
    private static JsonObject ReadLedger(string path)
    {
        try { return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o ? o : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static void WriteLedger(string path, JsonObject root)
    {
        try
        {
            if (root.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch { /* only the next start's sweep loses it */ }
    }

    /// <summary>A helper CluadeX just started (<see cref="AppChildren.Adopt"/>). Children that ended are struck off on the
    /// way. Never throws.</summary>
    public static void Note(Process process)
    {
        string? path = _ledger;
        if (path == null) return;
        long start = StartTicks(process);
        if (start == 0) return;   // gone already
        string name;
        try { name = process.ProcessName; } catch { return; }
        lock (Gate)
        {
            try
            {
                var root = ReadLedger(path);
                var mine = root[Me.ToString()] as JsonObject;
                if (mine == null)
                {
                    using var self = Process.GetCurrentProcess();
                    mine = new JsonObject { ["start"] = StartTicks(self), ["children"] = new JsonArray() };
                    root[Me.ToString()] = mine;
                }
                var kids = mine["children"] as JsonArray ?? new JsonArray();
                var keep = new JsonArray();
                foreach (var k in kids.OfType<JsonObject>())
                {
                    int pid = (int?)k["pid"] ?? 0;
                    long st = (long?)k["start"] ?? 0;
                    using var alive = Same(pid, st);
                    if (alive != null && pid != process.Id) keep.Add(k.DeepClone());
                }
                keep.Add(new JsonObject { ["pid"] = process.Id, ["start"] = start, ["name"] = name });
                mine["children"] = keep;
                WriteLedger(path, root);
            }
            catch { }
        }
    }

    /// <summary>The result of one sweep, for the log and the harness.</summary>
    public sealed record Result(List<string> Killed, int FoldersRemoved, int FilesRemoved);

    /// <summary>Clear what the last run left (see the class). <paramref name="localAppData"/> and <paramref name="temp"/>
    /// are the roots of the turn folders (tests pass their own). Never throws.</summary>
    public static Result Run(string? localAppData = null, string? temp = null, Action<string>? log = null)
    {
        var killed = KillLeftoverChildren(log);
        int folders = 0, files = 0;
        if (!OtherCluadexRunning())
        {
            localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            temp ??= Path.GetTempPath();
            folders += ClearFolders(Path.Combine(localAppData, "cluadex-grok"), "*", log);
            folders += ClearFolders(Path.Combine(localAppData, "cluadex-gemini"), "*", log);
            folders += ClearFolders(Path.Combine(temp, "cluadex-guard"), "agy-*", log);
            files += ClearFiles(Path.Combine(temp, "cluadex-guard"), "claude-dev-tools-*.json", TimeSpan.Zero);
            files += ClearFiles(Path.Combine(temp, "cluadex-guard"), "effort-*.txt", TimeSpan.Zero);
            files += ClearFiles(Path.Combine(temp, "cluadex-grok"), "*.txt", TimeSpan.Zero);
            // One settings file per installed copy (a trial build, a portable copy): each is written again before every
            // turn, so a week-old one belongs to a copy that is gone.
            files += ClearFiles(Path.Combine(temp, "cluadex-guard"), "claude-dev-settings-*.json", TimeSpan.FromDays(7));
            files += ClearFiles(Path.Combine(temp, "cluadex-guard"), "claude-dev-report-*.json", TimeSpan.FromDays(7));
        }
        if (killed.Count > 0 || folders > 0 || files > 0)
            log?.Invoke($"left by the last run: ended {killed.Count} helper(s){(killed.Count > 0 ? " (" + string.Join(", ", killed) + ")" : "")}, " +
                        $"removed {folders} turn folder(s) and {files} file(s)");
        return new Result(killed, folders, files);
    }

    private static List<string> KillLeftoverChildren(Action<string>? log)
    {
        var killed = new List<string>();
        string? path = _ledger;
        if (path == null) return killed;
        lock (Gate)
        {
            try
            {
                var root = ReadLedger(path);
                foreach (var (owner, node) in root.ToList())
                {
                    if (node is not JsonObject o || !int.TryParse(owner, out int ownerPid)) { root.Remove(owner); continue; }
                    if (ownerPid == Me) continue;
                    // The CluadeX that started them still runs (same pid, same start): they are its, not leftovers.
                    using (var ownerAlive = Same(ownerPid, (long?)o["start"] ?? 0))
                        if (ownerAlive != null && ownerAlive.ProcessName.StartsWith("CluadeX", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var k in (o["children"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        int pid = (int?)k["pid"] ?? 0;
                        using var p = Same(pid, (long?)k["start"] ?? 0);
                        if (p == null) continue;
                        string name = (string?)k["name"] ?? "?";
                        string now;
                        try { now = p.ProcessName; } catch { continue; }
                        if (!string.Equals(now, name, StringComparison.OrdinalIgnoreCase)) continue;   // not the helper written down
                        try
                        {
                            p.Kill(entireProcessTree: true);
                            killed.Add($"{name} {pid}");
                        }
                        catch (Exception ex) { log?.Invoke($"leftover {name} {pid} not ended: {ex.Message}"); }
                    }
                    root.Remove(owner);
                }
                WriteLedger(path, root);
            }
            catch { }
        }
        return killed;
    }

    /// <summary>Delete the folders in <paramref name="root"/> matching <paramref name="pattern"/>. A junction inside one
    /// is unlinked, never entered (<see cref="Safety.SafeDelete"/>): a turn home links the user's own folders.</summary>
    private static int ClearFolders(string root, string pattern, Action<string>? log)
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return 0;
            foreach (var dir in Directory.EnumerateDirectories(root, pattern))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) >= Before) continue;   // this run's own turn
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(dir, false); n++; continue; }
                    Safety.SafeDelete.Directory(dir, root);
                    if (!Directory.Exists(dir)) n++;
                }
                catch (Exception ex) { log?.Invoke($"turn folder {dir} not removed: {ex.Message}"); }
            }
        }
        catch { }
        return n;
    }

    private static int ClearFiles(string dir, string pattern, TimeSpan olderThan)
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var cutoff = olderThan > TimeSpan.Zero ? DateTime.UtcNow - olderThan : Before;
            if (cutoff > Before) cutoff = Before;
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
                try
                {
                    if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;   // too new, or this run's own
                    File.Delete(f);
                    n++;
                }
                catch { /* in use */ }
        }
        catch { }
        return n;
    }
}
