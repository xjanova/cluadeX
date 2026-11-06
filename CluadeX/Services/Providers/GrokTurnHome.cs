using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CluadeX.Services.Safety;

namespace CluadeX.Services.Providers;

/// <summary>
/// One Grok Dev turn's own GROK_HOME: the user's ~/.grok as the CLI reads it, plus CluadeX's guard hook and chat-tools MCP
/// server, without touching the user's ~/.grok or the project — the same idea as <see cref="GeminiTurnHome"/>.
///
/// Why a home: Grok Build 1.0.70 has no --settings or --mcp-config. Its hooks come from files it discovers (&lt;home&gt;\hooks\*.json,
/// config.toml, ~/.claude/settings.json …) and its MCP servers from config.toml; the GROK_CONFIG overlay is an allowlist
/// that drops both, and an agent profile (--agent-profile / GROK_AGENT) replaces the session's whole definition, system
/// prompt included. So each turn gets a home of its own:
/// - hooks\cluadex-guard.json: PreToolUse on Grok's shell, its three file writers and its sub-agent spawn → guard.cmd
///   (CluadeX.exe --cx-guard, the same guard as Claude Dev and Gemini Dev); SessionStart → loaded.cmd, a plain batch file
///   that writes <see cref="Marker"/>. Grok runs the user's own hooks headless (`-p` counts the folder as trusted for them), and every
///   hook failure is fail-open, so the wrapper turns any failure of the exe into exit 2, and the provider stops a turn whose
///   first guarded tool call arrives with no marker.
/// - config.toml: the user's own, with CluadeX's chat-tools server appended as [mcp_servers.cluadex].
/// - agents\cluadex-explore.md: a read-only sub-agent type. Grok's model cannot pick its built-in explore / plan by name
///   (its spawn_subagent enum lists only plugin, project and user agents), so without this every spawn would be a
///   general-purpose writer, which CluadeX's sub-agent policy refuses. The guard also refuses this type's file writes.
/// - The rest of the user's home is carried: small files copied, folders linked (junctions — the bundled skills, plugins
///   and memory stay the user's, and are not downloaded again each turn), hooks copied (the turn's own file goes there).
///   The CLI's per-session state (sessions, logs, worktrees …) starts empty and goes with the turn.
/// - The sign-in is never copied: GROK_AUTH_PATH names the user's own auth.json (the CLI 1.0.70 logs that it reads that
///   path), so a token the CLI refreshes lands where `grok login` put it.
/// The folder is under LocalApplicationData, beside Gemini Dev's turn homes.
/// </summary>
public sealed class GrokTurnHome
{
    public const string HooksFileName = "cluadex-guard.json";
    private const string GuardFile = "guard.cmd";
    private const string LoadedFile = "loaded.cmd";
    private const long MaxCopiedFile = 4 * 1024 * 1024;

    /// <summary>Folders of the user's home the turn does not carry: the CLI's per-session state (made fresh, deleted with
    /// the turn), the installer's downloads, the binary and its extracted docs. hooks is copied, not linked (below).</summary>
    private static readonly HashSet<string> NotCarried = new(StringComparer.OrdinalIgnoreCase)
        { "hooks", "agents", "sessions", "logs", "crash", "trace-exports", "worktrees", "downloads", "bin", "docs", "completions" };

    /// <summary>Folders copied, not linked: the turn writes its own file into each.</summary>
    private static readonly string[] Copied = { "hooks", "agents" };

    /// <summary>The read-only sub-agent type the turn defines (Agents.CliAgentPolicy names it to the model).</summary>
    public const string ExploreAgent = "cluadex-explore";

    /// <summary>Files of the user's home not copied: the sign-in (see the class summary), live-session bookkeeping and
    /// config.toml, which <see cref="WriteConfig"/> writes.</summary>
    private static readonly HashSet<string> NotCopied = new(StringComparer.OrdinalIgnoreCase)
        { "auth.json", "active_sessions.json", "worktrees.db", "config.toml" };

    /// <summary>The tools the guard sees: Grok's shell (run_terminal_cmd, run_terminal_command on its other route), its
    /// file writers and its sub-agent spawn, plus the Claude names Grok maps onto them. A list of names, each compared
    /// exactly (Grok's matcher syntax).</summary>
    public const string GuardMatcher =
        "run_terminal_cmd|run_terminal_command|search_replace|hashline_edit|write|spawn_subagent|task|Bash|Shell|Edit|Write|Task";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cluadex-grok");

    /// <summary>The value for GROK_HOME.</summary>
    public string Home { get; }

    /// <summary>Exists once the CLI ran the SessionStart hook, so the hooks of this turn are live.</summary>
    public string Marker => Path.Combine(Home, "hooks.loaded");

    /// <summary>The user's auth.json (for GROK_AUTH_PATH), or null when there is none.</summary>
    public string? UserAuthFile { get; private set; }

    private GrokTurnHome(string home) => Home = home;

    public static GrokTurnHome Create()
    {
        SweepStale();
        var turn = new GrokTurnHome(Path.Combine(Root, Guid.NewGuid().ToString("N")[..12]));
        Directory.CreateDirectory(turn.Home);
        return turn;
    }

    /// <summary>A home in a folder the caller chose (the harness).</summary>
    public static GrokTurnHome At(string home)
    {
        Directory.CreateDirectory(home);
        return new GrokTurnHome(home);
    }

    /// <summary>Carry the user's home into this one (see the class summary). <paramref name="link"/>: how a folder is linked
    /// (target, link) → linked; null = an NTFS junction. A folder that cannot be linked is copied.</summary>
    public void CarryUserHome(string realHome, Func<string, string, bool>? link = null)
    {
        link ??= Junction;
        if (!Directory.Exists(realHome)) return;
        foreach (var file in Directory.EnumerateFiles(realHome))
        {
            string name = Path.GetFileName(file);
            if (NotCopied.Contains(name) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (new FileInfo(file).Length > MaxCopiedFile) continue; }
            catch (IOException) { continue; }
            TryCopy(file, Path.Combine(Home, name));
        }
        foreach (var dir in Directory.EnumerateDirectories(realHome))
        {
            string name = Path.GetFileName(dir);
            string to = Path.Combine(Home, name);
            if (Copied.Contains(name, StringComparer.OrdinalIgnoreCase)) CopyTree(dir, to);
            else if (!NotCarried.Contains(name) && !link(dir, to)) CopyTree(dir, to);
        }
        string auth = Path.Combine(realHome, "auth.json");
        if (File.Exists(auth)) UserAuthFile = auth;
    }

    /// <summary>An NTFS junction (no privilege needed, unlike a symbolic link). Deleting the home unlinks it
    /// (<see cref="SafeDelete"/> never enters a reparse point).</summary>
    public static bool Junction(string target, string link)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    /// <summary>The guard's hook file and its two wrappers, for a turn that may edit or run.</summary>
    public void WriteHooks(string exe, bool noEdit)
    {
        var ascii = new UTF8Encoding(false);
        string guard = Path.Combine(Home, GuardFile), loaded = Path.Combine(Home, LoadedFile);
        File.WriteAllText(guard, GeminiTurnHome.GuardCmd(exe, noEdit), ascii);
        File.WriteAllText(loaded, LoadedCmd, ascii);
        string hooks = Path.Combine(Home, "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, HooksFileName), HooksJson(guard, loaded), new UTF8Encoding(false));
        string agents = Path.Combine(Home, "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, ExploreAgent + ".md"), ExploreAgentMd, new UTF8Encoding(false));
    }

    /// <summary>The read-only sub-agent: Grok's three file writers and further spawns removed, CluadeX's chat tools not
    /// inherited (opening projects and chats is the main agent's call).</summary>
    public const string ExploreAgentMd =
        "---\n" +
        "name: " + ExploreAgent + "\n" +
        "description: Read-only investigation for this CluadeX chat. Searches, reads files and runs read-only commands; never edits files. Give it an independent research part of the task and use what it reports.\n" +
        "disallowedTools:\n  - search_replace\n  - hashline_edit\n  - write\n  - spawn_subagent\n" +
        "mcpInheritance:\n  except:\n    - " + Tools.ChatToolBridge.ServerName + "\n" +
        "---\n" +
        "You are a read-only investigator working for the main agent of a CluadeX chat. Find what you were asked to find: search, " +
        "read files, and run only commands that change nothing (listing, searching, git log/diff/status, reading build output). " +
        "Never create, edit or delete a file and never run a command that changes the project, the machine or a remote. " +
        "Report concisely: what you found, with file paths and line numbers, and what you could not confirm.\n";

    /// <summary>The SessionStart hook: cmd alone writes the marker beside it (no exe to start, so it is there long before
    /// the model's first tool call). ASCII only.</summary>
    public const string LoadedCmd = "@echo off\r\necho loaded> \"%~dp0hooks.loaded\"\r\nexit /b 0\r\n";

    /// <summary>The hook file. Each command is one program with no arguments (<c>args: []</c>: Grok starts it itself, no
    /// shell to re-read the path); the guard's timeout leaves the user time to answer CluadeX's red prompt. Pure.</summary>
    public static string HooksJson(string guardCmd, string loadedCmd)
    {
        static JsonObject Handler(string command, int timeout) =>
            new() { ["type"] = "command", ["command"] = command, ["args"] = new JsonArray(), ["timeout"] = timeout };
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["SessionStart"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(Handler(loadedCmd, 30)) }),
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = GuardMatcher,
                    ["hooks"] = new JsonArray(Handler(guardCmd, 300)),
                }),
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>config.toml: the user's own, plus CluadeX's chat-tools server. True = the chat tools are in.</summary>
    public bool WriteConfig(string realHome, string? chatToolsJson)
    {
        string user = "";
        try
        {
            string path = Path.Combine(realHome, "config.toml");
            if (File.Exists(path)) user = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string? mcp = McpToml(chatToolsJson, user);
        File.WriteAllText(Path.Combine(Home, "config.toml"), user + (mcp ?? ""), new UTF8Encoding(false));
        return mcp != null;
    }

    /// <summary>The ChatToolBridge's mcpServers as config.toml tables to append. Null = nothing to add: no config, or the
    /// user's file already has a table of that name (a second one would make the whole file invalid TOML). Pure.</summary>
    public static string? McpToml(string? chatToolsJson, string userToml)
    {
        if (string.IsNullOrWhiteSpace(chatToolsJson)) return null;
        JsonObject? servers;
        try { servers = JsonNode.Parse(chatToolsJson)?["mcpServers"] as JsonObject; }
        catch (JsonException) { return null; }
        if (servers == null) return null;
        var sb = new StringBuilder();
        foreach (var (name, node) in servers)
        {
            if (node is not JsonObject s || s["command"] is not JsonValue cv || !cv.TryGetValue(out string? command) || string.IsNullOrWhiteSpace(command)) continue;
            if (Regex.IsMatch(userToml, @"^\s*\[\s*mcp_servers\s*\.\s*[""']?" + Regex.Escape(name) + @"[""']?\s*[\].]", RegexOptions.Multiline)) continue;
            string key = "mcp_servers." + TomlKey(name);
            sb.Append("\n\n# CluadeX's chat tools for one Grok Dev turn (this home is deleted with the turn)\n");
            sb.Append('[').Append(key).Append("]\n");
            sb.Append("command = ").Append(TomlString(command)).Append('\n');
            var args = (s["args"] as JsonArray)?.Select(a => a is JsonValue av && av.TryGetValue(out string? x) ? x : null).Where(x => x != null).ToList()
                       ?? new List<string?>();
            sb.Append("args = [").Append(string.Join(", ", args.Select(a => TomlString(a!)))).Append("]\n");
            if (s["env"] is JsonObject env && env.Count > 0)
            {
                sb.Append("\n[").Append(key).Append(".env]\n");
                foreach (var (k, v) in env)
                    if (v is JsonValue ev && ev.TryGetValue(out string? value) && value != null)
                        sb.Append(TomlKey(k)).Append(" = ").Append(TomlString(value)).Append('\n');
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>A TOML basic string. Pure.</summary>
    public static string TomlString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            sb.Append(c switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ when c < 0x20 || c == 0x7f => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    private static string TomlKey(string k) => Regex.IsMatch(k, "^[A-Za-z0-9_-]+$") ? k : TomlString(k);

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) TryCopy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from))
            if (!new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static void TryCopy(string from, string to)
    {
        try { if (File.Exists(from)) File.Copy(from, to, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Delete the home. Its junctions are unlinked, never entered: the user's folders they point at stay.</summary>
    public void Delete() => DeleteHome(Home);

    private static void DeleteHome(string home)
    {
        try { SafeDelete.Directory(home, Path.GetDirectoryName(home)!); }
        catch (Exception ex) { SafeDelete.Log?.Invoke($"Grok Dev turn home not deleted ({ex.GetType().Name}): {ex.Message}"); }
    }

    /// <summary>Homes a crashed turn left behind (a day old): its sessions hold the conversation, so they are not kept.</summary>
    private static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root))
                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) DeleteHome(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
