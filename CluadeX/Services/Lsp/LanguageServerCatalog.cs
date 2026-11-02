using System.IO;

namespace CluadeX.Services.Lsp;

/// <summary>A server that can serve a language: the program's names to look for and how to start it.</summary>
public sealed record LanguageServerCandidate(string Name, string[] Executables, string Arguments);

/// <summary>One language the editor checks live: which files, which servers (best first), and how to get one.</summary>
public sealed record LanguageServerLanguage(
    string Key, string Name, string[] Extensions, LanguageServerCandidate[] Candidates,
    string? InstallCommand, string InstallUrl, string[] ExtraFolders)
{
    /// <summary>The LSP languageId of a file of this language (TypeScript's server serves four of them).</summary>
    public string LanguageIdFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tsx" => "typescriptreact",
        ".jsx" => "javascriptreact",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".ts" or ".mts" or ".cts" => "typescript",
        _ => Key,
    };
}

/// <summary>A server found on this machine: the full path that will run, and with what.</summary>
public sealed record ResolvedLanguageServer(string Name, string Executable, string Arguments);

/// <summary>
/// The languages the editor checks live (C#, TypeScript/JavaScript, Python, Dart) and where their servers live.
///
/// Nothing is bundled and nothing is installed for the user. <see cref="Resolve"/> looks on PATH and in the folders the
/// usual installers put the server in without touching PATH (dotnet tools, npm's global folder, pip's user Scripts,
/// the Flutter SDK) — the same rule as the debugger: a server we only think exists is a dead button, so the editor
/// shows the install command instead.
/// </summary>
public static class LanguageServerCatalog
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static readonly IReadOnlyList<LanguageServerLanguage> Languages = new[]
    {
        new LanguageServerLanguage("csharp", "C#", new[] { ".cs", ".csx" },
            new[]
            {
                new LanguageServerCandidate("csharp-ls", new[] { "csharp-ls" }, ""),
                new LanguageServerCandidate("OmniSharp", new[] { "OmniSharp" }, "-lsp"),
            },
            "dotnet tool install --global csharp-ls", "https://github.com/razzmatazz/csharp-language-server",
            new[] { Path.Combine(Home, ".dotnet", "tools") }),

        new LanguageServerLanguage("typescript", "TypeScript / JavaScript",
            new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs" },
            new[] { new LanguageServerCandidate("typescript-language-server", new[] { "typescript-language-server" }, "--stdio") },
            "npm install -g typescript-language-server typescript", "https://github.com/typescript-language-server/typescript-language-server",
            new[] { Path.Combine(AppData, "npm") }),

        new LanguageServerLanguage("python", "Python", new[] { ".py", ".pyi" },
            new[]
            {
                new LanguageServerCandidate("pyright", new[] { "pyright-langserver" }, "--stdio"),
                new LanguageServerCandidate("basedpyright", new[] { "basedpyright-langserver" }, "--stdio"),
                new LanguageServerCandidate("pylsp", new[] { "pylsp" }, ""),
            },
            "npm install -g pyright", "https://microsoft.github.io/pyright/#/installation",
            new[] { Path.Combine(AppData, "npm") }),

        new LanguageServerLanguage("dart", "Dart", new[] { ".dart" },
            new[] { new LanguageServerCandidate("dart language-server", new[] { "dart" }, "language-server --protocol=lsp") },
            // The Dart SDK comes with Flutter or on its own; neither has a one-line installer we can vouch for.
            null, "https://dart.dev/get-dart",
            Array.Empty<string>()),
    };

    public static LanguageServerLanguage? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0) return null;
        foreach (var lang in Languages)
            if (Array.IndexOf(lang.Extensions, ext) >= 0) return lang;
        return null;
    }

    public static LanguageServerLanguage? ForKey(string key)
        => Languages.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first installed server for <paramref name="lang"/>, or null. Only looks at files — never runs one.</summary>
    public static ResolvedLanguageServer? Resolve(LanguageServerLanguage lang)
    {
        var folders = SearchFolders(lang);
        foreach (var candidate in lang.Candidates)
            foreach (var exe in candidate.Executables)
                if (FindExecutable(exe, folders) is { } path)
                    return new ResolvedLanguageServer(candidate.Name, path, candidate.Arguments);
        return null;
    }

    private static List<string> SearchFolders(LanguageServerLanguage lang)
    {
        var path = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            path.Add(dir.Trim().Trim('"'));
        var folders = new List<string>();
        // Flutter's bin\dart.bat checks for SDK updates on every start; the dart.exe it wraps starts the server directly.
        if (lang.Key == "dart") folders.AddRange(DartFolders(path));
        folders.AddRange(path);
        folders.AddRange(lang.ExtraFolders);
        if (lang.Key == "python") folders.AddRange(PythonScriptFolders());
        return folders;
    }

    /// <summary>pip install --user puts scripts in %APPDATA%\Python\PythonXY\Scripts, which is not on PATH by default;
    /// the python.org installer's own Scripts folder often is not either.</summary>
    private static IEnumerable<string> PythonScriptFolders()
    {
        foreach (var root in new[] { Path.Combine(AppData, "Python"), Path.Combine(LocalAppData, "Programs", "Python") })
        {
            string[] versions;
            try { versions = Directory.Exists(root) ? Directory.GetDirectories(root, "Python*") : Array.Empty<string>(); }
            catch { continue; }
            foreach (var v in versions) yield return Path.Combine(v, "Scripts");
        }
    }

    /// <summary>The Dart SDK inside a Flutter on PATH, then the usual install folders of either SDK.</summary>
    private static IEnumerable<string> DartFolders(IEnumerable<string> path)
    {
        foreach (var dir in path)
        {
            bool flutterBin;
            try { flutterBin = File.Exists(Path.Combine(dir, "flutter.bat")); } catch { continue; }
            if (flutterBin) yield return Path.Combine(dir, "cache", "dart-sdk", "bin");
        }
        foreach (var sdk in new[] { @"C:\tools\dart-sdk", @"C:\src\flutter", Path.Combine(Home, "flutter"), Path.Combine(LocalAppData, "flutter") })
        {
            yield return Path.Combine(sdk, "bin", "cache", "dart-sdk", "bin");
            yield return Path.Combine(sdk, "bin");
        }
    }

    /// <summary>npm also writes an extension-less shell script next to each .cmd — that one is for Git Bash, not Windows.</summary>
    private static readonly string[] WindowsSuffixes = { ".exe", ".cmd", ".bat" };

    private static string? FindExecutable(string name, IEnumerable<string> folders)
    {
        foreach (var dir in folders)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var suffix in WindowsSuffixes)
            {
                try
                {
                    string candidate = Path.Combine(dir, name + suffix);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry must not abort the scan */ }
            }
        }
        return null;
    }
}
