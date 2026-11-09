using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.BugReports;

/// <summary>
/// Everything a problem report carries goes through here before it leaves the PC (owner 2026-11-09: a report must never
/// hold an API key, a token, a license key, a password, the Windows user name or the chat, unless the user ticks the
/// chat). The server runs its own copy of these rules (xmanstudio SecretScrubber) on what arrives, so a report that
/// slips past one still loses its secrets at the other.
///
/// What it masks: provider keys (sk-…, AIza…, gh*_…, xai-…, gsk_…, hf_…, xox…, AKIA…), JWTs, Bearer/Basic headers,
/// "secret-named" fields (api_key=…, "password": "…"), secrets in query strings, license keys (FREE-/TRIAL-/DEMO- and
/// 4–5-character groups), private-key blocks, e-mail addresses, the user's profile folder and name, the PC's name.
/// What it keeps: version numbers, GUIDs, ticket numbers, hex hashes shorter than a key — what a developer needs.
/// </summary>
public static class ReportScrubber
{
    public const string Mask = "[REDACTED]";

    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        // -----BEGIN … PRIVATE KEY----- … -----END … PRIVATE KEY-----
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", Opt), Mask),
        // Authorization: Bearer xxx / Basic xxx
        (new Regex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", Opt), "$1 " + Mask),
        // JWT
        (new Regex(@"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\b", Opt), Mask),
        // provider keys
        (new Regex(@"\bsk-(?:ant-|proj-|or-)?[A-Za-z0-9_-]{12,}", Opt), Mask),
        (new Regex(@"\bAIza[0-9A-Za-z_-]{20,}", Opt), Mask),
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", Opt), Mask),
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", Opt), Mask),
        (new Regex(@"\b(?:xai|gsk|hf|pplx|fw|nvapi|csk)[-_][A-Za-z0-9_-]{16,}", Opt), Mask),
        (new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", Opt), Mask),
        (new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", Opt), Mask),
        // "api_key": "…", password=…, X-Api-Key: … — the name stays, the value goes
        (new Regex(@"(?i)(\b[\w-]*(?:api[_-]?key|apikey|secret|token|password|passwd|pwd|authorization|auth|cookie|session[_-]?id|license[_-]?key|activation[_-]?key|device[_-]?code|private[_-]?key|access[_-]?key)[\w-]*\b[""']?\s*[:=]\s*[""']?)([^\s""',;&}\]]{3,})", Opt), "$1" + Mask),
        // ?key=…&token=…
        (new Regex(@"(?i)([?&](?:key|api_key|apikey|token|access_token|sig|signature|secret|password|code)=)[^&\s""']+", Opt), "$1" + Mask),
        // license keys: FREE-/TRIAL-/DEMO-…, and ABCD-EFGH-IJKL(-MNOP…) / ABCDE-…
        // (whole dash-groups only: an upper-case GUID's middle groups are not a key)
        (new Regex(@"(?<![A-Za-z0-9-])(?:FREE|TRIAL|DEMO)-[A-Z0-9]{4,}(?:-[A-Z0-9]{2,})*(?![A-Za-z0-9-])", Opt), Mask),
        (new Regex(@"(?<![A-Za-z0-9-])[A-Z0-9]{4,5}(?:-[A-Z0-9]{4,5}){2,7}(?![A-Za-z0-9-])", Opt), Mask),
        // e-mail
        (new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", Opt), "[EMAIL]"),
    };

    /// <summary>Hex runs this long are keys or secrets, not hashes a developer reads (a SHA-256 is 64; still masked —
    /// the machine fingerprint is one).</summary>
    private static readonly Regex LongHex = new(@"\b[0-9a-fA-F]{40,}\b", Opt);

    /// <summary>Mask every secret in <paramref name="text"/> and replace the user's own folders and names.</summary>
    public static string Scrub(string? text, ScrubContext? context = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var ctx = context ?? ScrubContext.Current;
        string s = text;

        // the user's folders first, before a rule can eat half a path: C:\Users\xman\… → %USERPROFILE%\…
        foreach (var (path, token) in ctx.Paths)
        {
            s = ReplaceIgnoreCase(s, path, token);
            s = ReplaceIgnoreCase(s, path.Replace('\\', '/'), token);
            s = ReplaceIgnoreCase(s, path.Replace(@"\", @"\\"), token);   // a JSON-escaped copy of the path
        }

        foreach (var (pattern, replacement) in Rules)
            s = pattern.Replace(s, replacement);
        s = LongHex.Replace(s, Mask);

        foreach (var (word, token) in ctx.Words)
            s = Regex.Replace(s, @"(?<![A-Za-z0-9])" + Regex.Escape(word) + @"(?![A-Za-z0-9])", token, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return s;
    }

    private static string ReplaceIgnoreCase(string s, string find, string with)
        => find.Length < 3 ? s : s.Replace(find, with, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The names on this PC a report must not carry: the profile folder (and the data folder, which can live
/// elsewhere), the Windows user name, the PC's name, and any extra words (the account's display name).</summary>
public sealed class ScrubContext
{
    public IReadOnlyList<(string Path, string Token)> Paths { get; }
    public IReadOnlyList<(string Word, string Token)> Words { get; }

    public ScrubContext(IEnumerable<(string Path, string Token)> paths, IEnumerable<(string Word, string Token)> words)
    {
        // the longest first, so C:\Users\xman\.cluadex goes before C:\Users\xman
        Paths = paths.Where(p => !string.IsNullOrWhiteSpace(p.Path) && p.Path.Trim().Length >= 3)
            .Select(p => (p.Path.Trim().TrimEnd('\\', '/'), p.Token))
            .DistinctBy(p => p.Item1.ToLowerInvariant())
            .OrderByDescending(p => p.Item1.Length).ToList();
        // a name of one or two letters would mask half the report ("a", "pc"): only names a person could recognise
        Words = words.Where(w => !string.IsNullOrWhiteSpace(w.Word) && w.Word.Trim().Length >= 3)
            .Select(w => (w.Word.Trim(), w.Token))
            .DistinctBy(w => w.Item1.ToLowerInvariant())
            .OrderByDescending(w => w.Item1.Length).ToList();
    }

    private static ScrubContext? _current;

    /// <summary>This PC's: %USERPROFILE%, %LOCALAPPDATA%, %APPDATA%, %TEMP%, the user name, the machine name.</summary>
    public static ScrubContext Current => _current ??= ForThisPc(null);

    public static ScrubContext ForThisPc(string? dataRoot, params string?[] extraWords)
    {
        var paths = new List<(string, string)>();
        void AddPath(string? p, string token) { if (!string.IsNullOrWhiteSpace(p)) paths.Add((p, token)); }
        AddPath(dataRoot, "%DATA%");
        AddPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        AddPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        try { AddPath(Path.GetTempPath(), "%TEMP%"); } catch { }
        AddPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");

        var words = new List<(string, string)>
        {
            (Environment.UserName, "[USER]"),
            (Environment.MachineName, "[PC]"),
        };
        foreach (var w in extraWords)
            if (!string.IsNullOrWhiteSpace(w)) words.Add((w!, "[NAME]"));
        return new ScrubContext(paths, words);
    }
}
