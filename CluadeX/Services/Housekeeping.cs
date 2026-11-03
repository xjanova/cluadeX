using System.IO;

namespace CluadeX.Services;

/// <summary>
/// Disk cleanup once per start, off the UI thread and a while after the window is up (owner 2026-11-02: "มีการล้าง
/// เก็บกวาด ทั้งฮาร์ดดิส"). What grew without bound: crash logs (one file per crash, never removed), the chat snapshots
/// the pre-compaction hook saves, and the chat database's write-ahead log, which SQLite only shrinks when told to.
/// Everything else already prunes itself (the debug log 30 days, screenshots 7 days, restore points 5 GB).
/// </summary>
public static class Housekeeping
{
    /// <summary>Run every step; each is best effort and never throws.</summary>
    public static void Run(string dataRoot, string crashLogDir, DatabaseService? db, DebugLogService? log)
    {
        int removed = 0;
        removed += PruneFolder(crashLogDir, "*.log", maxAgeDays: 30, keepNewest: 50);
        removed += PruneFolder(Path.Combine(dataRoot, "logs", "compaction-snapshots"), "*", maxAgeDays: 14, keepNewest: 100);
        bool tidied = false;
        try
        {
            if (db?.IsHealthy == true) { db.Maintain(); tidied = true; }
        }
        catch (Exception ex) { log?.Warn("Housekeeping", "database tidy failed", ex); }
        log?.Info("Housekeeping", $"removed {removed} old file(s){(tidied ? ", database tidied" : "")}");
    }

    /// <summary>Delete files in <paramref name="dir"/> (not below it) older than <paramref name="maxAgeDays"/>, and
    /// any beyond the newest <paramref name="keepNewest"/>. A folder that is a link is left alone. Returns how many.</summary>
    public static int PruneFolder(string dir, string pattern, int maxAgeDays, int keepNewest)
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return 0;
            var cutoff = DateTime.Now.AddDays(-maxAgeDays);
            var files = new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            for (int i = 0; i < files.Count; i++)
            {
                if (i < keepNewest && files[i].LastWriteTime >= cutoff) continue;
                try { files[i].Delete(); n++; } catch { }
            }
        }
        catch { }
        return n;
    }
}
