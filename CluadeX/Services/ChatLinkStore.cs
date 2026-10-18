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

    /// <summary>How many splits up <paramref name="chatId"/> is from a chat nobody split off (0 = a top chat).</summary>
    public int DepthOf(string? chatId)
    {
        int depth = 0;
        for (string? up = Get(chatId)?.ParentId; up != null && depth < 64; up = Get(up)?.ParentId) depth++;
        return depth;
    }

    /// <summary>Split chats opened at or after <paramref name="since"/> (local time, as <see cref="ChatLink.CreatedAt"/>).</summary>
    public int CreatedSince(DateTime since)
    {
        lock (_gate) return _links.Values.Count(l => l.CreatedAt >= since);
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

/// <summary>
/// How far an agent may split work into new chats (new_chat). Each split chat starts its own agent run — for Claude Dev
/// a CLI process and its tool servers — and may split again, so without a ceiling one request can breed chats without
/// end (owner 2026-10-18: "ระวัง … ผลิตออกมาไม่หยุด"). Only agent-opened chats count; the user opens as many as they like.
/// </summary>
public static class SplitLimits
{
    /// <summary>A split chat may split once more; a chat that deep may not.</summary>
    public const int MaxDepth = 2;
    /// <summary>Split chats of one chat still working at once.</summary>
    public const int MaxWorkingPerParent = 4;
    /// <summary>Split chats working at once in the whole app.</summary>
    public const int MaxWorkingTotal = 6;
    /// <summary>Split chats opened in <see cref="Window"/>, all chats together.</summary>
    public const int MaxPerWindow = 8;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Why a new split chat may not open now (what the agent is told), or null when it may.
    /// <paramref name="parentDepth"/>: how deep the asking chat already is (ChatLinkStore.DepthOf).</summary>
    public static string? Refusal(int parentDepth, int workingUnderParent, int workingTotal, int openedInWindow)
    {
        if (parentDepth + 1 > MaxDepth)
            return $"This chat is already a split of a split (depth {parentDepth}): it may not open more chats. "
                 + "Do the work here, or say in your reply what should be split and let the user decide.";
        if (workingUnderParent >= MaxWorkingPerParent)
            return $"{workingUnderParent} chats split off this one are still working (at most {MaxWorkingPerParent} at once). "
                 + "Wait for one to finish (close_chat lists them), or do this part here.";
        if (workingTotal >= MaxWorkingTotal)
            return $"{workingTotal} split chats are working in CluadeX right now (at most {MaxWorkingTotal} at once). "
                 + "Do this part here, or wait until some finish.";
        if (openedInWindow >= MaxPerWindow)
            return $"{openedInWindow} split chats were opened in the last {Window.TotalMinutes:0} minutes (at most {MaxPerWindow}). "
                 + "Do this part here; do not retry new_chat in a loop.";
        return null;
    }
}
