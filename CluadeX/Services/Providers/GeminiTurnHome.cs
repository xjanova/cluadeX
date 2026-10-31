using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Services.Safety;

namespace CluadeX.Services.Providers;

/// <summary>
/// One Gemini Dev turn's own GEMINI_CLI_HOME: a throwaway copy of the user's ~/.gemini with the turn's settings in it, so
/// the guard hook and CluadeX's MCP servers reach the CLI without touching the user's ~/.gemini or the project.
///
/// Why not GEMINI_CLI_SYSTEM_SETTINGS_PATH: CLI 0.64 skips a system settings file unless its folder is owned by an
/// administrator (seen 2026-10-31: "Security Warning: Skipping system settings file … not owned by a trusted
/// administrator"), and an unelevated app cannot make one. User-scope settings lose to the project's
/// .gemini/settings.json. CLI 0.64 kept the hooks on a --skip-trust turn when a project said hooksConfig.enabled:false
/// (tests/safetytest), but that is not a promise: if hooks ever don't load, the load marker (SessionStart, fires before
/// the stream's init event) never appears and the provider kills the turn. The hook names carry a per-turn id so a
/// project cannot disable the guard by name while the marker survives.
///
/// The hooks are .cmd wrappers because the CLI runs a hook through PowerShell (-Command): `"exe" --flag` is a parse error
/// there, and `&amp; 'exe'` returns at once with no exit code since CluadeX.exe is a WinExe. Either way the hook fails, and
/// a failed hook lets the tool run (proven with the real CLI). A batch file waits for a WinExe and passes its exit code on.
///
/// The folder is under LocalApplicationData, not %TEMP%: it holds a copy of the user's Google sign-in, and %TEMP% may be
/// writable by others.
/// </summary>
public sealed class GeminiTurnHome
{
    private const string GuardFile = "guard.cmd";
    private const string LoadedFile = "loaded.cmd";

    /// <summary>The folders of ~/.gemini the CLI reads as the user's own set-up. Its history and tmp stay behind.</summary>
    private static readonly string[] CarriedFolders = { "commands", "skills", "agents", "policies" };

    /// <summary>The CLI's marker for an extension that lives elsewhere (`gemini extensions link`).</summary>
    private const string ExtensionInstallFile = ".gemini-extension-install.json";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cluadex-gemini");

    /// <summary>The value for GEMINI_CLI_HOME (the CLI reads &lt;Home&gt;\.gemini).</summary>
    public string Home { get; }

    /// <summary>Exists once the CLI ran the SessionStart hook, so the hooks of this turn are live.</summary>
    public string Marker => Path.Combine(Home, "hooks.loaded");

    private string GeminiDir => Path.Combine(Home, ".gemini");

    private GeminiTurnHome(string home) => Home = home;

    public static GeminiTurnHome Create()
    {
        SweepStale();
        var turn = new GeminiTurnHome(Path.Combine(Root, Guid.NewGuid().ToString("N")[..12]));
        Directory.CreateDirectory(turn.GeminiDir);
        return turn;
    }

    /// <summary>The wrapper's text: only ASCII (cmd reads a .cmd in the OEM code page), the exe by its short path. Any
    /// failure of the exe itself becomes a block: only exit 2 stops a tool, every other exit code lets it run.</summary>
    public static string GuardCmd(string exe, bool noEdit) =>
        "@echo off\r\n"
        + $"set \"{GuardHook.TurnDirEnv}=%~dp0\"\r\n"
        + (noEdit ? $"set \"{GuardHook.NoEditEnv}=1\"\r\n" : "")
        + $"\"{exe}\" {GuardHook.Flag}\r\n"
        + "if errorlevel 1 exit /b 2\r\n"
        + "exit /b 0\r\n";

    /// <summary>A hook command for the CLI's PowerShell.</summary>
    public static string PowerShellCall(string cmdPath) => "& '" + cmdPath.Replace("'", "''") + "'";

    /// <summary>Write the hook wrappers for a turn that may edit or run; the commands go into the turn's settings.</summary>
    public GeminiDevProvider.TurnHooks WriteGuardHooks(string exe, bool noEdit)
    {
        var ascii = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(Home, GuardFile), GuardCmd(exe, noEdit), ascii);
        File.WriteAllText(Path.Combine(Home, LoadedFile), AntigravityGuard.LoadedCmd(exe), ascii);
        return new GeminiDevProvider.TurnHooks(Path.GetFileName(Home),
            PowerShellCall(Path.Combine(Home, GuardFile)), PowerShellCall(Path.Combine(Home, LoadedFile)));
    }

    public Task WriteSettingsAsync(JsonObject settings, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(GeminiDir, "settings.json"),
            settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);

    /// <summary>What the CLI reads from its home: the sign-in and other single files of ~/.gemini (an API key chosen with
    /// /auth stays in the OS keychain, which no home changes), GEMINI.md, the user's commands, skills, agents and policies,
    /// the extensions (linked, not copied: they can be large) and the home folder's .env.</summary>
    public void CarryUserHome(string realHome, string realGeminiDir)
    {
        if (Directory.Exists(realGeminiDir))
        {
            foreach (var file in Directory.EnumerateFiles(realGeminiDir))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                TryCopy(file, Path.Combine(GeminiDir, name));
            }
            foreach (var folder in CarriedFolders) CopyTree(Path.Combine(realGeminiDir, folder), Path.Combine(GeminiDir, folder));
            LinkExtensions(Path.Combine(realGeminiDir, "extensions"), Path.Combine(GeminiDir, "extensions"));
        }
        TryCopy(Path.Combine(realHome, ".env"), Path.Combine(Home, ".env"));
        CopyTree(Path.Combine(realHome, ".agents", "skills"), Path.Combine(Home, ".agents", "skills"));
    }

    private static void LinkExtensions(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        TryCopy(Path.Combine(from, "extension-enablement.json"), Path.Combine(to, "extension-enablement.json"));
        foreach (var extension in Directory.EnumerateDirectories(from))
        {
            string source = extension;
            try
            {
                string installed = Path.Combine(extension, ExtensionInstallFile);
                if (File.Exists(installed) && JsonNode.Parse(File.ReadAllText(installed)) is JsonObject meta
                    && meta["type"]?.GetValue<string>() == "link" && meta["source"]?.GetValue<string>() is { Length: > 0 } linked) source = linked;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            string dir = Path.Combine(to, Path.GetFileName(extension));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ExtensionInstallFile),
                new JsonObject { ["source"] = source, ["type"] = "link" }.ToJsonString(), new UTF8Encoding(false));
            TryCopy(Path.Combine(extension, ".env"), Path.Combine(dir, ".env"));
        }
    }

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

    public void Delete()
    {
        try { if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Folders a crashed turn left behind: they hold a copy of the sign-in, so they are not kept.</summary>
    private static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root))
                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
