using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CluadeX.Services.Providers;

/// <summary>
/// The environment CluadeX gives the commands inside Codex's Windows sandbox, so that the everyday tools work there
/// instead of failing as the sandbox account. Each value was checked with `codex sandbox` (Codex 0.167, 2026-11-11):
/// - git: schannel HTTPS fails (SEC_E_NO_CREDENTIALS) and a repository owned by the user can trip "dubious ownership";
///   <c>http.sslBackend=openssl</c> and <c>safe.directory=*</c> through GIT_CONFIG_COUNT fix both (ls-remote https worked).
/// - dotnet: NuGet reads %APPDATA%\NuGet\NuGet.Config, which the sandbox may not read, and cannot write
///   ~/.nuget/packages. APPDATA pointed at a sandbox home with a copy of the config (credentials removed), NUGET_PACKAGES
///   in that home and the user's own package folder as a read-only fallback: restore, build and run worked from the
///   packages the user already has. A package not downloaded yet still fails (.NET's HTTPS is schannel too).
/// - MSBuild nodes are not reused: a node left running as the sandbox account would keep another command's environment.
/// - Gradle (~/.gradle lock denied) and pip get their homes there too; Python prints UTF-8 (this machine's console is cp874).
/// Only the sandboxed shell gets these (<c>shell_environment_policy.set</c>), never codex.exe or its MCP servers.
/// </summary>
public static class CodexSandboxEnv
{
    /// <summary>The sandbox's own home: %TEMP%\cluadex-codex-home (%TEMP% is one of the sandbox's writable roots).</summary>
    public static string HomeFor(string? temp = null) =>
        Path.Combine(string.IsNullOrEmpty(temp) ? Path.GetTempPath() : temp, "cluadex-codex-home");

    public const string DefaultNuGetConfig =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n"
        + "    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" />\n"
        + "  </packageSources>\n</configuration>\n";

    /// <summary>
    /// The user's NuGet.Config without what the sandbox must not see: packageSourceCredentials, apikeys, and any
    /// config/add whose key names a password or token. Unreadable XML → the plain nuget.org config. Pure.
    /// </summary>
    public static string SanitizeNuGetConfig(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return DefaultNuGetConfig;
        try
        {
            var doc = XDocument.Parse(xml);
            if (doc.Root is not { } root || root.Name.LocalName != "configuration") return DefaultNuGetConfig;
            foreach (var el in root.Elements().Where(e => e.Name.LocalName is "packageSourceCredentials" or "apikeys").ToList())
                el.Remove();
            foreach (var add in root.Descendants().Where(e => e.Name.LocalName == "add"
                         && Regex.IsMatch((string?)e.Attribute("key") ?? "", "password|token|secret|apikey", RegexOptions.IgnoreCase)).ToList())
                add.Remove();
            return (doc.Declaration?.ToString() ?? "<?xml version=\"1.0\" encoding=\"utf-8\"?>") + "\n" + root + "\n";
        }
        catch (System.Xml.XmlException) { return DefaultNuGetConfig; }
    }

    /// <summary>
    /// Makes the home and its NuGet.Config (rewritten only when it changed). Returns false when the home cannot be made;
    /// the turn then runs without the folder-based values. Never throws.
    /// </summary>
    public static bool Prepare(string home, string? userAppData = null)
    {
        try
        {
            string nugetDir = Path.Combine(home, "Roaming", "NuGet");
            Directory.CreateDirectory(nugetDir);
            userAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string? userConfig = null;
            try
            {
                string src = Path.Combine(userAppData, "NuGet", "NuGet.Config");
                if (File.Exists(src)) userConfig = File.ReadAllText(src);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            string config = SanitizeNuGetConfig(userConfig);
            string dest = Path.Combine(nugetDir, "NuGet.Config");
            string? old = null;
            try { if (File.Exists(dest)) old = File.ReadAllText(dest); } catch (IOException) { }
            if (old != config)
            {
                // Two turns starting at once write the same text: the one that loses the race still has the file.
                try { File.WriteAllText(dest, config); }
                catch (IOException) when (File.Exists(dest)) { }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The variables, in order. <paramref name="home"/>: the prepared home, or null to leave out the folder-based ones.
    /// <paramref name="env"/>: this process's environment (null = the real one): a GIT_CONFIG_COUNT the user set is kept,
    /// and so is a NUGET_PACKAGES of theirs (then used as the fallback). Pure apart from the default environment.
    /// </summary>
    public static List<KeyValuePair<string, string>> Vars(string? home, string userProfile,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        string? Get(string k) => env != null ? (env.TryGetValue(k, out var v) ? v : null) : Environment.GetEnvironmentVariable(k);
        var vars = new List<KeyValuePair<string, string>>();
        void Add(string k, string v) => vars.Add(new(k, v));
        if (string.IsNullOrEmpty(Get("GIT_CONFIG_COUNT")))
        {
            Add("GIT_CONFIG_COUNT", "2");
            Add("GIT_CONFIG_KEY_0", "http.sslBackend");
            Add("GIT_CONFIG_VALUE_0", "openssl");
            Add("GIT_CONFIG_KEY_1", "safe.directory");
            Add("GIT_CONFIG_VALUE_1", "*");
        }
        Add("MSBUILDDISABLENODEREUSE", "1");
        Add("DOTNET_NOLOGO", "1");
        Add("PYTHONUTF8", "1");
        Add("PYTHONIOENCODING", "utf-8");
        if (!string.IsNullOrEmpty(home))
        {
            Add("APPDATA", Path.Combine(home, "Roaming"));
            string userPackages = Get("NUGET_PACKAGES") is { Length: > 0 } own ? own
                : string.IsNullOrEmpty(userProfile) ? "" : Path.Combine(userProfile, ".nuget", "packages");
            Add("NUGET_PACKAGES", Path.Combine(home, "nuget", "packages"));
            if (userPackages.Length > 0) Add("RestoreFallbackFolders", userPackages);
            Add("GRADLE_USER_HOME", Path.Combine(home, "gradle"));
            Add("PIP_CACHE_DIR", Path.Combine(home, "pip"));
        }
        return vars;
    }

    private static readonly Regex EnvName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// The variables as `codex exec -c` overrides: shell_environment_policy.set.NAME='value' (a TOML literal string). A
    /// value a literal string cannot hold (a quote or a line break) is left out rather than mangled. Pure.
    /// </summary>
    public static List<string> ConfigOverrides(IEnumerable<KeyValuePair<string, string>> vars)
    {
        var list = new List<string>();
        foreach (var (k, v) in vars)
        {
            if (!EnvName.IsMatch(k) || v.IndexOfAny(new[] { '\'', '\r', '\n' }) >= 0) continue;
            list.Add($"shell_environment_policy.set.{k}='{v}'");
        }
        return list;
    }

    /// <summary>The overrides for a real turn: the home prepared, then <see cref="Vars"/>. Never throws.</summary>
    public static List<string> ForTurn()
    {
        try
        {
            string home = HomeFor();
            bool ok = Prepare(home);
            return ConfigOverrides(Vars(ok ? home : null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new();
        }
    }
}
