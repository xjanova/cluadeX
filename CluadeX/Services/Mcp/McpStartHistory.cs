using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// Which MCP server launches have answered initialize at least once on this machine — kept in
/// &lt;data root&gt;\mcp-started.json, so it survives a restart.
///
/// A package runner (npx, uvx …) may download its package on the very first start, which can take far longer than the
/// usual 15 s initialize timeout; that first start gets 120 s (<see cref="McpServerManager.StartupTimeoutFor"/>). Kept
/// only in memory, "first" meant "first since the app started": one silent npx server then held every app start for
/// two minutes. A launch is identified by server name + command + arguments — a server switched to another package
/// downloads again and gets the long timeout again. Thread-safe; file errors are never fatal (worst case: 120 s again).
/// </summary>
public sealed class McpStartHistory
{
    private readonly string? _path;
    private readonly object _gate = new();
    /// <summary>One writer at a time for every history object in the process.</summary>
    private static readonly object FileGate = new();
    private HashSet<string>? _ready;
    /// <summary>Most launches kept (a tampered or runaway file is read only this far).</summary>
    private const int MaxEntries = 4096;

    /// <param name="dataRoot">The app's data root; null/empty = memory only.</param>
    public McpStartHistory(string? dataRoot)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "mcp-started.json");
    }

    /// <summary>Has this exact launch (name + command + arguments) answered initialize before?</summary>
    public bool HasBeenReady(string name, McpServerConfig config)
    {
        string sig = Signature(name, config);
        lock (_gate) return Load().Contains(sig);
    }

    /// <summary>Remember that this launch answered initialize (written to disk the first time).</summary>
    public void Record(string name, McpServerConfig config)
    {
        string sig = Signature(name, config);
        lock (_gate)
        {
            if (!Load().Add(sig)) return;
        }
        if (_path == null) return;
        // Servers start four at a time: writes are serialized, and each one writes the set as it is NOW (not a snapshot
        // taken before waiting) — two writers sharing one .tmp name collided, or an older snapshot landed last, and a
        // launch was forgotten (= 120 s again at the next start). The .tmp name is unique too (another CluadeX).
        lock (FileGate)
        {
            // What another writer (another history object, another CluadeX) saved meanwhile is kept, not overwritten.
            var onDisk = ReadFile();
            string[] latest;
            lock (_gate)
            {
                var set = Load();
                foreach (var i in onDisk) { if (set.Count >= MaxEntries) break; set.Add(i); }
                latest = set.ToArray();
            }
            string tmp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(latest));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[mcp] could not save {Path.GetFileName(_path)}: {ex.Message}");
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>name + command + arguments → a short hash (the file never holds paths or arguments in clear).</summary>
    public static string Signature(string name, McpServerConfig config)
    {
        var sb = new StringBuilder();
        sb.Append(name.Trim().ToLowerInvariant()).Append('\u0001').Append((config.Command ?? "").Trim().ToLowerInvariant());
        foreach (var a in config.Args ?? new List<string>()) sb.Append('\u0001').Append(a ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    private HashSet<string> Load()
    {
        if (_ready != null) return _ready;
        _ready = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in ReadFile()) _ready.Add(i);
        return _ready;
    }

    /// <summary>The signatures on disk (at most <see cref="MaxEntries"/>); none when missing or unreadable.</summary>
    private List<string> ReadFile()
    {
        var list = new List<string>();
        if (_path == null) return list;
        try
        {
            if (File.Exists(_path))
            {
                var items = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path));
                if (items != null)
                    foreach (var i in items)
                    {
                        if (list.Count >= MaxEntries) break;
                        if (!string.IsNullOrWhiteSpace(i) && i.Length <= 64) list.Add(i);
                    }
            }
        }
        catch { /* unreadable: start from nothing (the long timeout once more) */ }
        return list;
    }
}
