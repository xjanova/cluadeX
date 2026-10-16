using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Restore points for everything the agent changes (owner 2026-10-16: the Time Machine must see every project and
/// keep everything; a chat's plan must be able to go back to any step). Before an agent tool changes a file, the
/// file's previous content is kept — once per step — in ~/.cluadex/checkpoints:
///   blobs/ab/abcdef…           content-addressed copies (the same content is stored once)
///   &lt;project-slug&gt;/index.jsonl one line per file captured: which step, chat, plan item, and the blob (or "absent")
/// A restore point is one agent step (a chat turn's step N). Restoring to it puts every file touched by that step or
/// anything after it in the same project back to how it was just before the step — after first capturing the
/// current state as a restore point of its own, so a restore can be undone the same way. Commands run through the
/// shell are not seen (git history covers what was committed). Capped at <see cref="MaxBytes"/>: the oldest points go.
/// </summary>
public sealed class CheckpointService
{
    public sealed record Entry(string Id, DateTime TimeUtc, string Session, int Step, string? Plan, string Project,
        string File, bool Existed, string? Blob, long Size, string? Note);

    /// <summary>One restore point: every file a step changed.</summary>
    public sealed record Point(string Id, DateTime TimeUtc, string Session, int Step, string? Plan, string Project,
        IReadOnlyList<Entry> Files, string? Note)
    {
        public string FileList => string.Join(", ", Files.Select(f => Path.GetFileName(f.File)).Distinct().Take(6))
                                  + (Files.Count > 6 ? $" +{Files.Count - 6}" : "");
        public string When => TimeUtc.ToLocalTime().ToString("d MMM yyyy  HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        public string Title => !string.IsNullOrWhiteSpace(Plan) ? Plan! : !string.IsNullOrWhiteSpace(Note) ? Note! : $"step {Step}";
    }

    public const long MaxBytes = 5L * 1024 * 1024 * 1024;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private readonly string _root;
    private readonly DebugLogService? _log;
    private readonly object _lock = new();
    private readonly HashSet<(string Id, string File)> _captured = new();

    public static CheckpointService? Current { get; private set; }

    public CheckpointService(DebugLogService? log = null, string? root = null)
    {
        _log = log;
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "checkpoints");
        Current = this;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Slug(string project) => MemoryService.ProjectSlug(project);

    private string IndexPath(string project) => Path.Combine(_root, Slug(project), "index.jsonl");

    /// <summary>Keep <paramref name="fullPath"/>'s current content as part of the running step's restore point.
    /// Called before an agent tool changes it. Outside an agent run (no scope) nothing is captured.</summary>
    public void CaptureBeforeChange(string fullPath, string? note = null)
    {
        var scope = AgentRunScope.Current;
        string? project = scope?.WorkingDirectory;
        if (scope == null || string.IsNullOrWhiteSpace(project)) return;
        string id = $"{scope.TurnId}-{scope.Step:D3}";
        lock (_lock) { if (!_captured.Add((id, fullPath.ToLowerInvariant()))) return; }
        try
        {
            bool existed = File.Exists(fullPath);
            string? blob = null; long size = 0;
            if (existed)
            {
                var fi = new FileInfo(fullPath);
                if (fi.Length > MaxFileBytes) { _log?.Info("Checkpoint", $"skipped {fullPath}: {fi.Length / 1048576} MB is over the cap"); return; }
                byte[] bytes = File.ReadAllBytes(fullPath);
                size = bytes.Length;
                blob = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                string bp = BlobPath(blob);
                if (!File.Exists(bp)) { Directory.CreateDirectory(Path.GetDirectoryName(bp)!); File.WriteAllBytes(bp, bytes); }
            }
            string plan = scope.Todos.FirstOrDefault(t => t.Status == "in_progress")?.Content ?? "";
            var e = new Entry(id, DateTime.UtcNow, scope.SessionKey, scope.Step, plan.Length > 0 ? plan : null, project!,
                fullPath, existed, blob, size, note);
            Append(project!, e);
        }
        catch (Exception ex) { _log?.Warn("Checkpoint", $"could not keep {fullPath}", ex); }
    }

    private string BlobPath(string sha) => Path.Combine(_root, "blobs", sha[..2], sha);

    private void Append(string project, Entry e)
    {
        string p = IndexPath(project);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.AppendAllText(p, JsonSerializer.Serialize(e, Json) + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>Every project that has restore points: (project path, last change).</summary>
    public List<(string Project, DateTime LastUtc, int Points)> Projects()
    {
        var list = new List<(string, DateTime, int)>();
        if (!Directory.Exists(_root)) return list;
        foreach (var dir in Directory.GetDirectories(_root))
        {
            if (Path.GetFileName(dir) == "blobs") continue;
            var entries = ReadIndex(Path.Combine(dir, "index.jsonl"));
            if (entries.Count == 0) continue;
            list.Add((entries[^1].Project, entries.Max(x => x.TimeUtc), entries.Select(x => x.Id).Distinct().Count()));
        }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>The restore points of <paramref name="project"/>, newest first; optionally only those on one local day.</summary>
    public List<Point> Points(string project, DateTime? localDay = null)
    {
        var entries = ReadIndex(IndexPath(project));
        return entries.GroupBy(e => e.Id)
            .Select(g => new Point(g.Key, g.Min(x => x.TimeUtc), g.First().Session, g.First().Step, g.First().Plan, project,
                g.GroupBy(x => x.File.ToLowerInvariant()).Select(x => x.First()).ToList(), g.First().Note))
            .Where(p => localDay == null || p.TimeUtc.ToLocalTime().Date == localDay.Value.Date)
            .OrderByDescending(p => p.TimeUtc).ToList();
    }

    /// <summary>The restore points one chat made, oldest first (the plan window lists them by step).</summary>
    public List<Point> PointsForSession(string project, string session) =>
        Points(project).Where(p => p.Session == session).OrderBy(p => p.TimeUtc).ToList();

    /// <summary>
    /// Put the project back to how it was just before <paramref name="pointId"/>: each file that point or any later
    /// one touched gets its earliest captured content from then on (or is removed if it did not exist yet). The current
    /// state is captured first as a "before restore" point. Returns the files restored, or an error.
    /// </summary>
    public (bool Ok, int Files, string Message) RestoreTo(string project, string pointId)
    {
        try
        {
            var entries = ReadIndex(IndexPath(project));
            var target = entries.FirstOrDefault(e => e.Id == pointId);
            if (target == null) return (false, 0, "restore point not found");
            var after = entries.Where(e => e.TimeUtc >= target.TimeUtc).OrderBy(e => e.TimeUtc).ToList();
            var earliestPerFile = after.GroupBy(e => e.File.ToLowerInvariant()).Select(g => g.First()).ToList();

            // Undo point: the files as they are now.
            string undoId = "restore-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            foreach (var e in earliestPerFile)
            {
                string? blob = null; long size = 0; bool existed = File.Exists(e.File);
                if (existed)
                {
                    byte[] now = File.ReadAllBytes(e.File);
                    size = now.Length;
                    blob = Convert.ToHexString(SHA256.HashData(now)).ToLowerInvariant();
                    string bp = BlobPath(blob);
                    if (!File.Exists(bp)) { Directory.CreateDirectory(Path.GetDirectoryName(bp)!); File.WriteAllBytes(bp, now); }
                }
                Append(project, new Entry(undoId, DateTime.UtcNow, "", 0, null, project, e.File, existed, blob, size, $"before restoring to {pointId}"));
            }

            int n = 0;
            foreach (var e in earliestPerFile)
            {
                if (e.Existed && e.Blob != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(e.File)!);
                    File.Copy(BlobPath(e.Blob), e.File, overwrite: true);
                }
                else if (File.Exists(e.File)) File.Delete(e.File);
                n++;
            }
            _log?.Info("Checkpoint", $"restored {n} file(s) of {project} to {pointId} (undo: {undoId})");
            return (true, n, undoId);
        }
        catch (Exception ex)
        {
            _log?.Warn("Checkpoint", $"restore to {pointId} failed", ex);
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Branching: write the files as they were just before <paramref name="pointId"/> into another copy of the project
    /// (<paramref name="targetRoot"/>, e.g. a git worktree). Paths are mapped from the project to the copy; files outside
    /// the project are left alone. The project itself is not touched.
    /// </summary>
    public (bool Ok, int Files, string Message) RestoreInto(string project, string pointId, string targetRoot)
    {
        try
        {
            var entries = ReadIndex(IndexPath(project));
            var target = entries.FirstOrDefault(e => e.Id == pointId);
            if (target == null) return (false, 0, "restore point not found");
            string root = Path.GetFullPath(project).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            int n = 0;
            foreach (var e in entries.Where(e => e.TimeUtc >= target.TimeUtc).OrderBy(e => e.TimeUtc)
                         .GroupBy(e => e.File.ToLowerInvariant()).Select(g => g.First()))
            {
                string full = Path.GetFullPath(e.File);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(targetRoot, full[root.Length..]);
                if (e.Existed && e.Blob != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(BlobPath(e.Blob), dest, overwrite: true);
                }
                else if (File.Exists(dest)) File.Delete(dest);
                n++;
            }
            return (true, n, targetRoot);
        }
        catch (Exception ex)
        {
            _log?.Warn("Checkpoint", $"branch copy of {pointId} failed", ex);
            return (false, 0, ex.Message);
        }
    }

    /// <summary>Total size of the store; over <see cref="MaxBytes"/> the oldest restore points (and blobs nothing else
    /// uses) are removed. Run off the UI thread, e.g. at startup.</summary>
    public void Prune()
    {
        try
        {
            string blobs = Path.Combine(_root, "blobs");
            if (!Directory.Exists(blobs)) return;
            long total = new DirectoryInfo(blobs).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            if (total <= MaxBytes) return;
            var all = Directory.GetDirectories(_root).Where(d => Path.GetFileName(d) != "blobs")
                .SelectMany(d => ReadIndex(Path.Combine(d, "index.jsonl")).Select(e => (Dir: d, E: e))).OrderBy(x => x.E.TimeUtc).ToList();
            var drop = new HashSet<string>();
            long freed = 0;
            foreach (var g in all.GroupBy(x => x.E.Id))
            {
                if (total - freed <= MaxBytes * 0.9) break;
                drop.Add(g.Key);
                freed += g.Sum(x => x.E.Size);
            }
            foreach (var d in all.Select(x => x.Dir).Distinct())
            {
                var keep = ReadIndex(Path.Combine(d, "index.jsonl")).Where(e => !drop.Contains(e.Id)).ToList();
                File.WriteAllLines(Path.Combine(d, "index.jsonl"), keep.Select(e => JsonSerializer.Serialize(e, Json)), new UTF8Encoding(false));
            }
            var used = Directory.GetDirectories(_root).Where(d => Path.GetFileName(d) != "blobs")
                .SelectMany(d => ReadIndex(Path.Combine(d, "index.jsonl"))).Select(e => e.Blob).Where(b => b != null).ToHashSet();
            foreach (var f in new DirectoryInfo(blobs).EnumerateFiles("*", SearchOption.AllDirectories))
                if (!used.Contains(f.Name)) f.Delete();
            _log?.Info("Checkpoint", $"pruned {drop.Count} old restore point(s)");
        }
        catch (Exception ex) { _log?.Warn("Checkpoint", "prune failed", ex); }
    }

    /// <summary>Parsed indexes, kept until the file changes: the plan window re-reads every 1.5 s from the UI thread.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Len, DateTime Write, List<Entry> Entries)> IndexCache = new();

    private static List<Entry> ReadIndex(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return new List<Entry>();
        if (IndexCache.TryGetValue(path, out var c) && c.Len == fi.Length && c.Write == fi.LastWriteTimeUtc)
            return new List<Entry>(c.Entries);
        var list = new List<Entry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<Entry>(line, Json) is { } e) list.Add(e); } catch { }
        }
        IndexCache[path] = (fi.Length, fi.LastWriteTimeUtc, list);
        return new List<Entry>(list);
    }
}
