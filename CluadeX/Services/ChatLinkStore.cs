using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>One chat that another chat split off (new_chat): who opened it, for what, and whether it was put away.</summary>
public sealed record ChatLink(string ParentId, string Task, DateTime CreatedAt, DateTime? ArchivedAt);

/// <summary>
/// Which chat split off which (the agent's new_chat), and which finished side chats were put away — so the sidebar can
/// draw the split as a tree and a long list of side chats can be cleaned up (owner 2026-10-18: "ปิดแชทเก็บกวาดงานแยก
/// ที่เสร็จแล้ว … ทำผังว่าแยกไปที่ไหนทำอะไร").
///
/// Kept in its own small file (<c>chat-links.json</c> beside codex.db), NOT in the chat database: codex.db takes no
/// schema changes (it has been damaged before), and an older CluadeX reading the same database must not trip over new
/// columns. Archiving hides a chat from the list; nothing is deleted, and it can be brought back.
/// </summary>
public sealed class ChatLinkStore
{
    /// <summary>The app's store (null in harnesses that never made one).</summary>
    public static ChatLinkStore? Current { get; set; }

    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, ChatLink> _links = new(StringComparer.Ordinal);

    /// <summary>Raised (any thread) with the chat id whose link or archive state changed.</summary>
    public event Action<string>? Changed;

    /// <param name="dataRoot">Folder of codex.db; null = memory only (tests).</param>
    public ChatLinkStore(string? dataRoot)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "chat-links.json");
        Load();
    }

    private sealed class FileShape
    {
        public Dictionary<string, ChatLink> Links { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json);
            if (shape?.Links != null)
                lock (_gate) _links = new Dictionary<string, ChatLink>(shape.Links, StringComparer.Ordinal);
        }
        catch { /* unreadable: start empty — the chats themselves are untouched */ }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(new FileShape { Links = new(_links) }, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch { /* a failed save loses only the tree drawing, never a chat */ }
    }

    public ChatLink? Get(string? chatId)
    {
        if (string.IsNullOrEmpty(chatId)) return null;
        lock (_gate) return _links.TryGetValue(chatId, out var l) ? l : null;
    }

    /// <summary>Record that <paramref name="childId"/> was split off <paramref name="parentId"/> to do <paramref name="task"/>.</summary>
    public void Link(string childId, string parentId, string task)
    {
        if (string.IsNullOrEmpty(childId) || string.IsNullOrEmpty(parentId) || childId == parentId) return;
        // A chat never becomes its own ancestor (a loop would hang the tree).
        for (string? up = parentId; up != null; up = Get(up)?.ParentId)
            if (up == childId) return;
        lock (_gate) _links[childId] = new ChatLink(parentId, (task ?? "").Trim(), DateTime.Now, null);
        Save();
        Changed?.Invoke(childId);
    }

    /// <summary>Put a split chat away (true) or bring it back (false). Only split chats have a record; others: false.</summary>
    public bool SetArchived(string chatId, bool archived)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(chatId, out var l)) return false;
            if (archived == (l.ArchivedAt != null)) return true;
            _links[chatId] = l with { ArchivedAt = archived ? DateTime.Now : null };
        }
        Save();
        Changed?.Invoke(chatId);
        return true;
    }

    /// <summary>The chats split off <paramref name="parentId"/>, oldest first.</summary>
    public List<(string Id, ChatLink Link)> ChildrenOf(string parentId)
    {
        lock (_gate)
            return _links.Where(kv => kv.Value.ParentId == parentId).OrderBy(kv => kv.Value.CreatedAt)
                         .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Forget links of chats that no longer exist (deleted, or dropped as empty at start-up).</summary>
    public void Prune(IEnumerable<string> existingIds)
    {
        var keep = new HashSet<string>(existingIds, StringComparer.Ordinal);
        bool changed;
        lock (_gate)
        {
            var gone = _links.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var k in gone) _links.Remove(k);
            changed = gone.Count > 0;
        }
        if (changed) Save();
    }
}
