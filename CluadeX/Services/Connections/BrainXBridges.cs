using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CluadeX.Services.Connections;

/// <summary>
/// BrainX's outbound MCP bridges (<c>&lt;vault&gt;\.obsidianx\mcp-bridges.json</c>): servers the brain connects OUT to,
/// whose tools every agent wired to BrainX gets as <c>&lt;id&gt;__&lt;tool&gt;</c>. A connection placed here is shared by
/// CluadeX, Claude, Gemini and Grok alike — so it is the first choice whenever BrainX is installed.
///
/// The file is the owner's: it holds the Unity / Unreal / Roblox bridges and a "_readme". Edits change only the one
/// entry CluadeX owns (args "--connector &lt;id&gt;"); every other field and entry is kept as it was. A file that does not
/// parse is never written (it would lose the owner's bridges); the first write of a session keeps a .bak.
/// </summary>
public static class BrainXBridges
{
    public static readonly JsonSerializerOptions Write = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions Read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The brain's vault: where BrainX itself points (%LOCALAPPDATA%\BrainX\vault-path.txt), else BRAINX_VAULT
    /// (from the brain's MCP entry, then this process). Null when none of them is an existing folder.</summary>
    public static string? ResolveVault(string? vaultFromBrainEntry = null, string? localAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string?>();
        try
        {
            string pointer = Path.Combine(localAppData, "BrainX", "vault-path.txt");
            if (File.Exists(pointer)) candidates.Add(File.ReadAllText(pointer).Trim().Trim('"'));
        }
        catch { }
        candidates.Add(vaultFromBrainEntry);
        candidates.Add(Environment.GetEnvironmentVariable("BRAINX_VAULT"));
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    public static string FileFor(string vault) => Path.Combine(vault, ".obsidianx", "mcp-bridges.json");

    /// <summary>The file's tree, a new one when it doesn't exist, or null when it exists and doesn't parse.</summary>
    public static JsonObject? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return new JsonObject { ["version"] = 1, ["bridges"] = new JsonArray() };
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: Read) as JsonObject;
        }
        catch { return null; }
    }

    private static JsonArray Bridges(JsonObject root)
    {
        if (root["bridges"] is JsonArray a) return a;
        var created = new JsonArray();
        root["bridges"] = created;
        return created;
    }

    private static List<string> Args(JsonObject e) =>
        e["args"] is JsonArray a ? a.Select(x => x?.GetValueKind() == JsonValueKind.String ? x.GetValue<string>() : "").ToList() : new List<string>();

    private static string? Str(JsonObject e, string key) =>
        e[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Is this entry one CluadeX wrote (its launcher, for <paramref name="id"/>)?</summary>
    public static bool IsOurs(JsonObject e, string id)
    {
        var a = Args(e);
        int at = a.IndexOf(ConnectorLauncher.Flag);
        return at >= 0 && at + 1 < a.Count && string.Equals(a[at + 1], id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The entry that serves this service already — ours, or one the owner set up by hand (enabled only).</summary>
    public static (JsonObject? Entry, bool Ours) Find(JsonObject root, ConnectorDef c)
    {
        foreach (var node in Bridges(root))
        {
            if (node is not JsonObject e) continue;
            if (IsOurs(e, c.Id)) return (e, true);
            bool enabled = e["enabled"] is JsonValue en && en.GetValueKind() is JsonValueKind.True;
            if (enabled && ConnectorCatalog.Recognises(c, Str(e, "command"), Args(e), Str(e, "url"))) return (e, false);
        }
        return (null, false);
    }

    /// <summary>Add or refresh CluadeX's entry for <paramref name="c"/>. A bridge the owner made with the same id but for
    /// something else is left alone: false (the caller then keeps the connection in CluadeX). Pure.</summary>
    public static bool Upsert(JsonObject root, ConnectorDef c, string launcherExe)
    {
        var bridges = Bridges(root);
        JsonObject? mine = null;
        foreach (var node in bridges)
        {
            if (node is not JsonObject e || !string.Equals(Str(e, "id"), c.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsOurs(e, c.Id)) return false;   // the owner's own "github" bridge: never overwritten
            mine = e;
        }
        if (mine == null) { mine = new JsonObject(); bridges.Add(mine); }
        mine["id"] = c.Id;
        mine["enabled"] = true;
        mine["command"] = launcherExe;
        mine["args"] = new JsonArray(ConnectorLauncher.Flag, c.Id);
        mine["env"] = new JsonObject();   // the key is read by the launcher at start — never stored here (the vault syncs)
        mine["timeoutSeconds"] = c.StartupTimeoutSeconds;
        mine["docs"] = c.Docs;
        mine["setup"] = $"Managed by CluadeX (Setup ▸ Connections ▸ {c.Name}). The key stays on this PC, encrypted; this entry holds none.";
        return true;
    }

    /// <summary>Remove CluadeX's entry for <paramref name="c"/> (never one the owner made). True when something was removed. Pure.</summary>
    public static bool Remove(JsonObject root, ConnectorDef c)
    {
        var bridges = Bridges(root);
        bool removed = false;
        for (int i = bridges.Count - 1; i >= 0; i--)
            if (bridges[i] is JsonObject e && IsOurs(e, c.Id)) { bridges.RemoveAt(i); removed = true; }
        return removed;
    }

    private static readonly HashSet<string> BackedUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Write the tree back: a .bak of the original first (once per session), then tmp + move (no half-written file).</summary>
    public static void Save(string file, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lock (BackedUp)
        {
            if (File.Exists(file) && BackedUp.Add(file))
                File.Copy(file, file + ".bak", overwrite: true);
        }
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Write));
        File.Move(tmp, file, overwrite: true);
    }
}
