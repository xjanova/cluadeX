using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Connections;

/// <summary>
/// The keys a connection needs (a Stripe key), kept on this PC only: DPAPI-encrypted for the current Windows user, in
/// the app's data folder — never in mcp_servers.json, never in BrainX's mcp-bridges.json (that file lives in the vault,
/// which syncs). Only the launcher reads them back, at the moment it starts the server, into that child's environment.
/// A copy of the file on another PC or account cannot be decrypted (the connection then just asks for the key again).
/// </summary>
public sealed class ConnectorSecrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CluadeX.Connections.v1");
    private readonly string _file;
    private readonly object _gate = new();

    public ConnectorSecrets(string dataRoot) => _file = Path.Combine(dataRoot, "connectors", "secrets.json");

    public string FilePath => _file;

    public bool Has(string id) => Get(id) != null;

    public string? Get(string id)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(id, out var blob)) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
            }
            catch { return null; }   // another PC / account, or damaged: as if never saved
        }
    }

    public void Set(string id, string secret)
    {
        lock (_gate)
        {
            var all = Load();
            all[id] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
            Save(all);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(id)) Save(all);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new();
        }
        catch { }
        return new();
    }

    private void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all));
        File.Move(tmp, _file, overwrite: true);
    }
}
