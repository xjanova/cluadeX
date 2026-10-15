using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Background;

/// <summary>One named start configuration from a project's launch.json.</summary>
public sealed record LaunchEntry(
    string Name,
    string RuntimeExecutable,
    IReadOnlyList<string> RuntimeArgs,
    int? Port,
    string? Url,
    string? Cwd,
    IReadOnlyDictionary<string, string> Env,
    string SourceFile);

/// <summary>
/// bg_start config=&lt;name&gt;: the project's own start configurations, read from <c>.cluadex/launch.json</c> first
/// and then <c>.claude/launch.json</c> — Claude Code's preview schema
/// (<c>{"version":"0.0.1","configurations":[{"name","runtimeExecutable","runtimeArgs","port","url"}]}</c>), so a
/// project already set up for Claude Code's preview works unchanged. Optional "cwd" (project-relative) and "env"
/// are honoured too. The file is project content — possibly written by the agent itself — so the command it
/// builds is shown in the permission prompt and judged exactly like a typed bg_start command.
/// </summary>
public static class LaunchConfig
{
    /// <summary>Where configurations are looked for, in order; the first file that names a configuration wins.</summary>
    public static readonly string[] RelativeLocations = { Path.Combine(".cluadex", "launch.json"), Path.Combine(".claude", "launch.json") };

    private const long MaxFileBytes = 256 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Every configuration the project defines (the first definition of a name wins), plus a description of each
    /// file that could not be read — never throws.
    /// </summary>
    public static (IReadOnlyList<LaunchEntry> Entries, IReadOnlyList<string> Problems) ReadAll(string? projectRoot)
    {
        var entries = new List<LaunchEntry>();
        var problems = new List<string>();
        if (string.IsNullOrEmpty(projectRoot)) return (entries, problems);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in RelativeLocations)
        {
            string file = Path.Combine(projectRoot, rel);
            try
            {
                if (!File.Exists(file)) continue;
                if (new FileInfo(file).Length > MaxFileBytes) { problems.Add($"{rel}: larger than 256 KB"); continue; }
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("configurations", out var configs)
                    || configs.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{rel}: no \"configurations\" array");
                    continue;
                }
                foreach (var c in configs.EnumerateArray())
                {
                    var entry = Parse(c, file, out var problem);
                    if (entry == null)
                    {
                        if (problem != null) problems.Add($"{rel}: {problem}");
                        continue;
                    }
                    if (seen.Add(entry.Name)) entries.Add(entry);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{rel}: {ex.Message}");
            }
        }
        return (entries, problems);
    }

    /// <summary>The configuration called <paramref name="name"/> (case-insensitive), or null.</summary>
    public static LaunchEntry? Find(string? projectRoot, string name) =>
        ReadAll(projectRoot).Entries.FirstOrDefault(e => e.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static LaunchEntry? Parse(JsonElement c, string file, out string? problem)
    {
        problem = null;
        if (c.ValueKind != JsonValueKind.Object) { problem = "a configuration is not an object"; return null; }
        string name = Str(c, "name")?.Trim() ?? "";
        if (name.Length == 0) { problem = "a configuration has no \"name\""; return null; }
        string exe = Str(c, "runtimeExecutable")?.Trim() ?? "";
        if (exe.Length == 0) { problem = $"\"{name}\" has no \"runtimeExecutable\""; return null; }

        var args = new List<string>();
        if (c.TryGetProperty("runtimeArgs", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var item in a.EnumerateArray())
                args.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText());

        int? port = null;
        if (c.TryGetProperty("port", out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n is > 0 and < 65536) port = n;
            else if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s) && s is > 0 and < 65536) port = s;
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (c.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var prop in e.EnumerateObject())
                env[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();

        return new LaunchEntry(name, exe, args, port, Str(c, "url"), Str(c, "cwd"), env, file);
    }

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The command line for <paramref name="entry"/> in <paramref name="shell"/>'s syntax. Every word is quoted
    /// where needed, so an argument can never become a second command.
    /// </summary>
    public static string BuildCommand(LaunchEntry entry, ShellRunner.Shell shell)
    {
        var words = new List<string> { entry.RuntimeExecutable };
        words.AddRange(entry.RuntimeArgs);
        return shell switch
        {
            ShellRunner.Shell.Bash => string.Join(' ', words.Select(BashWord)),
            ShellRunner.Shell.PowerShell => "& " + string.Join(' ', words.Select(PowerShellWord)),
            _ => string.Join(' ', words.Select(CmdWord)),
        };
    }

    private static bool IsPlain(string w) =>
        w.Length > 0 && w.All(ch => char.IsLetterOrDigit(ch) || "-_./=:,@+".IndexOf(ch) >= 0);

    internal static string BashWord(string w) =>
        IsPlain(w) && !w.Contains('\\') ? w : "'" + w.Replace("'", "'\\''") + "'";

    private static string PowerShellWord(string w) => "'" + w.Replace("'", "''") + "'";

    private static string CmdWord(string w)
    {
        if (IsPlain(w)) return w;
        // cmd has no escape inside quotes; strip the characters that would end the quoting or expand.
        string clean = new(w.Where(ch => ch is not ('"' or '%' or '\r' or '\n')).ToArray());
        return "\"" + clean + "\"";
    }
}
