using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>A translated text a widget shows: its key, the .arb file a change of it goes to, and what it says there.</summary>
public sealed class L10nText
{
    public string Key { get; init; } = "";
    /// <summary>The .arb file of the language the screen is drawn in (where a change is written).</summary>
    public string ArbPath { get; init; } = "";
    /// <summary>The ICU message as the file writes it.</summary>
    public string Message { get; init; } = "";
    /// <summary>What the screen shows: the message as plain text.</summary>
    public string Shown { get; init; } = "";
    /// <summary>That language has no translation of the key yet (the template's shows): a change adds it to <see cref="ArbPath"/>.</summary>
    public bool FromTemplate { get; init; }
    /// <summary>The template's keys in order (where an added key goes); null when the key is already in the file.</summary>
    public IReadOnlyList<string>? TemplateKeys { get; init; }
    public bool UseEscaping { get; init; }
}

/// <summary>The result of one change of an .arb file.</summary>
public sealed class ArbEdit
{
    public bool Ok { get; init; }
    /// <summary>Why nothing changed: a localization key.</summary>
    public string? Error { get; init; }
    /// <summary>The whole new file text.</summary>
    public string? Text { get; init; }

    internal static ArbEdit Fail(string key) => new() { Ok = false, Error = key };
}

/// <summary>
/// An app's translations as Flutter's gen-l10n reads them: l10n.yaml (arb-dir, template-arb-file, output-class,
/// output-dir, use-escaping), the .arb files, and where the generated Dart goes. The UI Studio's text tool changes a
/// translated text in the .arb file of the language the screen is drawn in; the renderer runs <c>flutter gen-l10n</c>
/// before a draw when an .arb changed (<c>flutter test --no-pub</c> never makes the translations itself).
/// </summary>
public sealed class FlutterL10n
{
    public string Root { get; private init; } = "";
    public string ArbDir { get; private init; } = "";
    /// <summary>The template .arb (every key; the language of a key no other file translates). Null: not there.</summary>
    public string? TemplatePath { get; private init; }
    public string OutputClass { get; private init; } = "AppLocalizations";
    public string OutputFile { get; private init; } = "app_localizations.dart";
    /// <summary>Where gen-l10n writes the Dart: output-dir, else next to the .arb files (an older Flutter's synthetic
    /// package when that is where the code already is).</summary>
    public string OutputDir { get; private init; } = "";
    /// <summary>The code is in an older Flutter's synthetic package (package:flutter_gen/gen_l10n).</summary>
    public bool Synthetic { get; private init; }
    /// <summary>use-escaping: a ' in a message starts quoted text ('' is one ').</summary>
    public bool UseEscaping { get; private init; }
    /// <summary>untranslated-messages-file (gen-l10n writes it too), null when there is none.</summary>
    public string? UntranslatedFile { get; private init; }
    public IReadOnlyList<string> ArbFiles { get; private init; } = Array.Empty<string>();

    private readonly Dictionary<string, string?> _locales = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's gen-l10n setup (null: no l10n.yaml — then nothing is generated from .arb files).</summary>
    public static FlutterL10n? Load(string root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        string yamlPath = Path.Combine(root, "l10n.yaml");
        if (!File.Exists(yamlPath)) return null;
        string yaml = WebPages.SafeRead(yamlPath, 50_000);
        string Opt(string name) => Regex.Match(yaml, @"(?m)^[ \t]*" + Regex.Escape(name) + @"[ \t]*:[ \t]*(?<v>[^\r\n#]*)").Groups["v"].Value.Trim().Trim('"', '\'').Trim();
        string arbDir = Full(root, Opt("arb-dir") is { Length: > 0 } ad ? ad : "lib/l10n");
        string template = Opt("template-arb-file") is { Length: > 0 } tf ? tf : "app_en.arb";
        string file = Opt("output-localization-file") is { Length: > 0 } of ? of : "app_localizations.dart";
        string outClass = Opt("output-class") is { Length: > 0 } oc ? oc : "AppLocalizations";
        string outDir;
        bool synthetic = false;
        if (Opt("output-dir") is { Length: > 0 } od) outDir = Full(root, od);
        else if (Opt("synthetic-package").Equals("false", StringComparison.OrdinalIgnoreCase)) outDir = arbDir;
        else
        {
            // Flutter 3.32+ writes next to the .arb files (the synthetic package is gone); an older one into .dart_tool
            string synth = Path.Combine(root, ".dart_tool", "flutter_gen", "gen_l10n");
            synthetic = File.Exists(Path.Combine(synth, file)) && !File.Exists(Path.Combine(arbDir, file));
            outDir = synthetic ? synth : arbDir;
        }
        var arbs = new List<string>();
        try
        {
            if (Directory.Exists(arbDir))
                arbs.AddRange(Directory.EnumerateFiles(arbDir, "*.arb", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".arb", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
        string templatePath = Path.GetFullPath(Path.Combine(arbDir, template));
        return new FlutterL10n
        {
            Root = root, ArbDir = arbDir, OutputClass = outClass, OutputFile = file,
            // only an .arb file is ever read or written as one (a template-arb-file that names something else is ignored)
            TemplatePath = templatePath.EndsWith(".arb", StringComparison.OrdinalIgnoreCase) && File.Exists(templatePath) ? templatePath : null,
            OutputDir = outDir, Synthetic = synthetic, UseEscaping = Opt("use-escaping").Equals("true", StringComparison.OrdinalIgnoreCase),
            UntranslatedFile = Opt("untranslated-messages-file") is { Length: > 0 } uf ? Full(root, uf) : null, ArbFiles = arbs,
        };
    }

    private static string Full(string root, string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The import of the generated class for a test of package <paramref name="package"/> (null: it is outside lib/).</summary>
    public string? ImportUri(string package)
    {
        if (Synthetic) return "package:flutter_gen/gen_l10n/" + OutputFile;
        string lib = Path.GetFullPath(Path.Combine(Root, "lib")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string dir = OutputDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (package.Length == 0 || !dir.StartsWith(lib, StringComparison.OrdinalIgnoreCase)) return null;
        string rel = dir[lib.Length..].Replace('\\', '/');
        return $"package:{package}/{rel}{OutputFile}";
    }

    /// <summary>A file gen-l10n writes (the generated Dart, the untranslated-messages report) — never a source to follow.</summary>
    public bool IsGenerated(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (UntranslatedFile != null && string.Equals(full, UntranslatedFile, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar), OutputDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(full), stem = Path.GetFileNameWithoutExtension(OutputFile);
            return name.EndsWith(".dart", StringComparison.OrdinalIgnoreCase)
                   && (name.Equals(OutputFile, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>An .arb file of this app.</summary>
    public bool IsArb(string path)
    {
        try
        {
            return path.EndsWith(".arb", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd(Path.DirectorySeparatorChar), ArbDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ───────────────────────────── languages ─────────────────────────────

    /// <summary>An .arb file's locale ("th", "en_US"): its @@locale, else the end of its name (app_th.arb).</summary>
    public string? LocaleOf(string arbPath)
    {
        if (_locales.TryGetValue(arbPath, out var known)) return known;
        string? locale = null;
        try { locale = ArbFile.Message(ReadText(arbPath) ?? "", "@@locale"); } catch { }
        if (string.IsNullOrWhiteSpace(locale))
        {
            // as gen-l10n reads a name: the whole name, else what follows each underscore
            string name = Path.GetFileNameWithoutExtension(arbPath);
            locale = null;
            for (int i = -1; i < name.Length; i++)
            {
                if (i >= 0 && name[i] != '_') continue;
                string rest = name[(i + 1)..];
                if (Regex.IsMatch(rest, @"^[a-zA-Z]{2}(?:[_-](?:[A-Z][a-z]{3}|[A-Za-z]{2}|\d{3}))*$")) { locale = rest; break; }
            }
        }
        locale = locale?.Trim().Replace('-', '_');
        _locales[arbPath] = locale;
        return locale;
    }

    /// <summary>The .arb file of the language a screen is drawn in: that language's, else Thai, else the template, else the first.</summary>
    public string? ArbFor(string language)
    {
        if (ArbFiles.Count == 0) return null;
        string lang = (language ?? "").Trim().Replace('-', '_');
        var withLocale = ArbFiles.Select(f => (File: f, Locale: LocaleOf(f) ?? "")).ToList();
        return withLocale.FirstOrDefault(x => x.Locale.Equals(lang, StringComparison.OrdinalIgnoreCase)).File
               ?? withLocale.FirstOrDefault(x => lang.Length > 0 && x.Locale.StartsWith(lang + "_", StringComparison.OrdinalIgnoreCase)).File
               ?? withLocale.FirstOrDefault(x => x.Locale.Equals("th", StringComparison.OrdinalIgnoreCase)).File
               ?? TemplatePath
               ?? ArbFiles[0];
    }

    /// <summary>What a key says in the language a screen is drawn in, ready to be typed over — or null with the reason
    /// (a localization key): not in any .arb, or a message with a placeholder / plural / select.</summary>
    public L10nText? Resolve(string key, string language, out string? why)
    {
        why = null;
        string? arb = ArbFor(language);
        string? text = arb != null ? ReadText(arb) : null;
        if (arb == null || text == null) { why = "studio.fedit.text.l10nMissing"; return null; }
        if (ArbFile.Members(text) == null) { why = "studio.fedit.arb.bad"; return null; }
        string? message = ArbFile.Message(text, key);
        IReadOnlyList<string>? order = null;
        if (message == null && TemplatePath != null && !string.Equals(TemplatePath, arb, StringComparison.OrdinalIgnoreCase)
            && ReadText(TemplatePath) is { } tmpl)
        {
            // not translated into this language: the app shows the template's message, and a change adds the key here
            message = ArbFile.Message(tmpl, key);
            order = message != null ? ArbFile.Keys(tmpl) : null;
        }
        if (message == null) { why = "studio.fedit.text.l10nMissing"; return null; }
        string? shown = ArbFile.PlainText(message, UseEscaping);
        if (shown == null) { why = "studio.fedit.text.l10nPlaceholder"; return null; }
        return new L10nText { Key = key, ArbPath = arb, Message = message, Shown = shown, FromTemplate = order != null, TemplateKeys = order, UseEscaping = UseEscaping };
    }

    private static string? ReadText(string path)
    {
        try
        {
            var f = CluadeX.Helpers.TextFileCodec.Read(path);
            return f.IsValidUtf8 ? f.Text : null;
        }
        catch { return null; }
    }

    // ───────────────────────────── generating ─────────────────────────────

    /// <summary>A fingerprint of everything gen-l10n reads (l10n.yaml and every .arb): when it changes, the generated code is old.</summary>
    public string Fingerprint()
    {
        using var sha = SHA256.Create();
        void Add(string name, byte[] bytes)
        {
            byte[] n = Encoding.UTF8.GetBytes(name.ToLowerInvariant() + "\0" + bytes.Length + "\0");
            sha.TransformBlock(n, 0, n.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        foreach (var f in new[] { Path.Combine(Root, "l10n.yaml") }.Concat(ArbFiles))
        {
            try { Add(Path.GetFileName(f), File.ReadAllBytes(f)); }
            catch { Add(Path.GetFileName(f), Array.Empty<byte>()); }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>The generated code exists and is not older than l10n.yaml or any .arb (a checkout writes them all at once).</summary>
    public bool GeneratedUpToDate()
    {
        try
        {
            string main = Path.Combine(OutputDir, OutputFile);
            if (!File.Exists(main)) return false;
            DateTime made = File.GetLastWriteTimeUtc(main);
            DateTime newest = ArbFiles.Append(Path.Combine(Root, "l10n.yaml")).Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            return newest <= made.AddSeconds(2);
        }
        catch { return false; }
    }
}

/// <summary>
/// One .arb file (JSON) changed the way a person would: only the characters of one message are replaced (or one
/// member is added for a key a language does not translate yet, next to its neighbour in the template), so the file's
/// formatting, its key order and every other key stay exactly as they were. A result is parsed again before it is
/// handed back — it must be JSON that gen-l10n reads, with nothing but that message changed.
/// </summary>
public static class ArbFile
{
    /// <summary>A top-level member: name, the span of its name's string and of its value, the kind of value
    /// ('"' a string, '{' an object, '[' an array, 'v' a number / true / false / null).</summary>
    internal sealed record Member(string Name, int NameStart, int NameEnd, int ValueStart, int ValueEnd, char Kind);

    // ───────────────────────────── reading ─────────────────────────────

    /// <summary>The message of a key (null: not there, or not a string). A key written twice reads its last value, as Dart's json does.</summary>
    public static string? Message(string text, string key)
    {
        var members = Members(text);
        var m = members?.LastOrDefault(x => x.Name == key);
        return m is { Kind: '"' } ? Decode(text, m.ValueStart, m.ValueEnd) : null;
    }

    /// <summary>The message keys in the file's order (no "@" metadata).</summary>
    public static IReadOnlyList<string> Keys(string text) =>
        Members(text)?.Where(m => !m.Name.StartsWith('@')).Select(m => m.Name).Distinct().ToList() ?? new List<string>();

    /// <summary>The top-level members of the file's object, in order (null: not a JSON object this can read).</summary>
    internal static List<Member>? Members(string text)
    {
        int i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;
        SkipSpace(text, ref i);
        if (i >= text.Length || text[i] != '{') return null;
        i++;
        var list = new List<Member>();
        SkipSpace(text, ref i);
        if (i < text.Length && text[i] == '}') return OnlySpaceFrom(text, i + 1) ? list : null;
        while (true)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length || text[i] != '"') return null;
            int nameStart = i, nameEnd = StringEnd(text, i);
            if (nameEnd < 0) return null;
            string? name = Decode(text, nameStart, nameEnd);
            if (name == null) return null;
            i = nameEnd;
            SkipSpace(text, ref i);
            if (i >= text.Length || text[i] != ':') return null;
            i++;
            SkipSpace(text, ref i);
            int vs = i, ve = ValueEnd(text, i);
            if (ve < 0) return null;
            list.Add(new Member(name, nameStart, nameEnd, vs, ve, text[vs] switch { '"' => '"', '{' => '{', '[' => '[', _ => 'v' }));
            i = ve;
            SkipSpace(text, ref i);
            if (i >= text.Length) return null;
            if (text[i] == ',') { i++; continue; }
            if (text[i] == '}') return OnlySpaceFrom(text, i + 1) ? list : null;
            return null;
        }
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n') i++;
    }

    private static bool OnlySpaceFrom(string s, int i)
    {
        SkipSpace(s, ref i);
        return i >= s.Length;
    }

    /// <summary>Past the closing quote of the string starting at <paramref name="i"/> (-1: unterminated or a raw control character).</summary>
    private static int StringEnd(string s, int i)
    {
        for (int k = i + 1; k < s.Length; k++)
        {
            char c = s[k];
            if (c == '\\') { k++; continue; }
            if (c == '"') return k + 1;
            if (c < ' ') return -1;
        }
        return -1;
    }

    /// <summary>Past the value starting at <paramref name="i"/> (-1: none there). Objects and arrays are skipped whole
    /// (their inside is checked when the whole text is parsed).</summary>
    private static int ValueEnd(string s, int i)
    {
        if (i >= s.Length) return -1;
        char c = s[i];
        if (c == '"') return StringEnd(s, i);
        if (c is '{' or '[')
        {
            int depth = 0;
            for (int k = i; k < s.Length; k++)
            {
                char ch = s[k];
                if (ch == '"') { k = StringEnd(s, k); if (k < 0) return -1; k--; continue; }
                if (ch is '{' or '[') depth++;
                else if (ch is '}' or ']' && --depth == 0) return k + 1;
            }
            return -1;
        }
        int e = i;
        while (e < s.Length && s[e] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n')) e++;
        return e > i ? e : -1;
    }

    /// <summary>The value of the JSON string s[start..end) (quotes included); null when it is not a valid one.</summary>
    private static string? Decode(string s, int start, int end)
    {
        if (end - start < 2 || s[start] != '"' || s[end - 1] != '"') return null;
        var sb = new StringBuilder(end - start);
        for (int k = start + 1; k < end - 1; k++)
        {
            char c = s[k];
            if (c != '\\') { sb.Append(c); continue; }
            if (++k >= end - 1) return null;
            switch (s[k])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (k + 4 >= end - 1 || !int.TryParse(s.AsSpan(k + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code)) return null;
                    sb.Append((char)code);
                    k += 4;
                    break;
                default: return null;
            }
        }
        return sb.ToString();
    }

    /// <summary>Strict JSON (no comments, no trailing comma): what Dart's json.decode — and so gen-l10n — reads.</summary>
    internal static bool IsJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text.Length > 0 && text[0] == '﻿' ? text[1..] : text,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    // ───────────────────────────── ICU messages ─────────────────────────────

    /// <summary>The message as the screen shows it, when it is plain text — null for a placeholder ({name}), a
    /// plural / select ({count, plural, …}) or anything else ICU would read as syntax.</summary>
    public static string? PlainText(string message, bool useEscaping)
    {
        if (!useEscaping) return message.IndexOfAny(new[] { '{', '}' }) >= 0 ? null : message;
        // use-escaping: '…' is literal text and '' one quote — read as gen-l10n's lexer does
        var sb = new StringBuilder();
        int i = 0;
        while (i < message.Length)
        {
            char c = message[i];
            if (c == '\'')
            {
                int close = message.IndexOf('\'', i + 1);
                if (close < 0) return null;   // an unmatched quote: gen-l10n refuses the message
                string quoted = message[i..(close + 1)];
                if (quoted == "''") sb.Append('\'');
                else if (i > 1 && message[i - 1] == '\'') sb.Append(quoted[..^1]);
                else sb.Append(quoted[1..^1]);
                i = close + 1;
                continue;
            }
            if (c is '{' or '}') return null;
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Plain text as an ICU message that shows exactly it (null: it can't be one — a { or } would be read as a
    /// placeholder). With use-escaping a ' is written ''.</summary>
    public static string? Encode(string text, bool useEscaping)
    {
        text = text.Replace("\r", "");
        if (text.IndexOfAny(new[] { '{', '}' }) >= 0) return null;
        string message = useEscaping ? text.Replace("'", "''") : text;
        return PlainText(message, useEscaping) == text ? message : null;
    }

    // ───────────────────────────── writing ─────────────────────────────

    /// <summary>A JSON string literal: ", \ and control characters escaped; every other character (Thai too) as it is.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    bool lone = char.IsHighSurrogate(c) ? !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1]));
                    if (c < ' ' || lone) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Set the message of <paramref name="key"/>: its string's characters are replaced, nothing else. A key
    /// the file doesn't have is added after its neighbour in <paramref name="templateKeys"/> (the template's order) in
    /// the file's own layout.</summary>
    public static ArbEdit SetMessage(string text, string key, string message, IReadOnlyList<string>? templateKeys = null)
    {
        var members = Members(text);
        if (members == null || !IsJson(text)) return ArbEdit.Fail("studio.fedit.arb.bad");
        string value = Quote(message);
        int at = members.FindLastIndex(m => m.Name == key);
        string result;
        if (at >= 0)
        {
            var m = members[at];
            if (m.Kind != '"') return ArbEdit.Fail("studio.fedit.arb.bad");
            result = text[..m.ValueStart] + value + text[m.ValueEnd..];
        }
        else result = Add(text, members, key, value, templateKeys);
        if (!Unchanged(text, result, key, message)) return ArbEdit.Fail("studio.fedit.arb.unsafe");
        return new ArbEdit { Ok = true, Text = result };
    }

    private static string Add(string text, List<Member> members, string key, string value, IReadOnlyList<string>? order)
    {
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (members.Count == 0)
        {
            int open = text.IndexOf('{'), close = text.LastIndexOf('}');
            return text[..(open + 1)] + nl + "  " + Quote(key) + ": " + value + nl + text[close..];
        }
        // the name/value separator the file uses (": " in most)
        var sample = members.FirstOrDefault(m => !m.Name.StartsWith('@')) ?? members[0];
        string pair = Quote(key) + text[sample.NameEnd..sample.ValueStart] + value;
        // after the key that comes before it in the template (and that key's @metadata), else after the leading @@locale
        int anchor = -1;
        int idx = order?.ToList().IndexOf(key) ?? -1;
        for (int k = idx - 1; k >= 0 && anchor < 0; k--)
            anchor = members.FindLastIndex(m => m.Name == order![k]);
        if (anchor >= 0)
        {
            // its "@key" metadata stays with it
            if (anchor + 1 < members.Count && members[anchor + 1].Name == "@" + members[anchor].Name) anchor++;
        }
        else
        {
            while (anchor + 1 < members.Count && members[anchor + 1].Name.StartsWith("@@", StringComparison.Ordinal)) anchor++;
        }
        if (anchor >= 0)
        {
            var a = members[anchor];
            bool own = OnOwnLine(text, a.NameStart);
            return text[..a.ValueEnd] + (own ? "," + nl + IndentOf(text, a.NameStart) : ", ") + pair + text[a.ValueEnd..];
        }
        var f = members[0];
        bool ownFirst = OnOwnLine(text, f.NameStart);
        return text[..f.NameStart] + pair + (ownFirst ? "," + nl + IndentOf(text, f.NameStart) : ", ") + text[f.NameStart..];
    }

    private static bool OnOwnLine(string s, int pos)
    {
        for (int i = pos - 1; i >= 0 && s[i] != '\n'; i--)
            if (s[i] is not (' ' or '\t')) return false;
        return true;
    }

    private static string IndentOf(string s, int pos)
    {
        int start = pos;
        while (start > 0 && s[start - 1] is ' ' or '\t') start--;
        return s[start..pos];
    }

    /// <summary>The new text is JSON gen-l10n reads, every other member is exactly as it was (same order, same
    /// characters), and the key now reads <paramref name="message"/>.</summary>
    private static bool Unchanged(string before, string after, string key, string message)
    {
        if (!IsJson(after)) return false;
        var a = Members(before);
        var b = Members(after);
        if (a == null || b == null) return false;
        var restA = a.Where(m => m.Name != key).Select(m => (m.Name, before[m.ValueStart..m.ValueEnd])).ToList();
        var restB = b.Where(m => m.Name != key).Select(m => (m.Name, after[m.ValueStart..m.ValueEnd])).ToList();
        if (!restA.SequenceEqual(restB)) return false;
        return Message(after, key) == message;
    }
}
