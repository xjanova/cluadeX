using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Installs skills from GitHub folders the owner trusts (Settings.TrustedSkillSources, default
/// https://github.com/anthropics/skills). The rules, in order:
///   - only a source on the list is ever contacted, and only through api.github.com / raw.githubusercontent.com
///     URLs this class builds itself from owner/repo/branch/path — never a URL taken from a response;
///   - only the SKILL.md (or name.md) text is downloaded, capped at 256 KB; a skill's scripts and other files are
///     never fetched and nothing is run — the preview says how many extra files were left behind;
///   - the owner sees the preview (source, license, allowed tools, warnings, the whole prompt) and presses Install;
///   - the file lands in ~/.cluadex/skills/&lt;name&gt;.md with a <c>source:</c> line, never over a built-in name,
///     never over an existing file unless asked.
/// </summary>
public class SkillInstaller
{
    public const int MaxSkillBytes = 256 * 1024;

    private readonly SettingsService _settings;
    private readonly SkillService _skills;
    private readonly HttpClient _http;

    public SkillInstaller(SettingsService settings, SkillService skills) : this(settings, skills, null) { }

    /// <summary>Test seam: a handler that answers instead of the network.</summary>
    public SkillInstaller(SettingsService settings, SkillService skills, HttpMessageHandler? handler)
    {
        _settings = settings;
        _skills = skills;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX-SkillInstaller");
    }

    /// <summary>A GitHub folder: owner/repo, branch and path inside the repo.</summary>
    public record SourceRef(string Owner, string Repo, string Branch, string Path)
    {
        public string HtmlUrl => $"https://github.com/{Owner}/{Repo}/tree/{Branch}/{Path}".TrimEnd('/');
        public string Key => $"{Owner}/{Repo}".ToLowerInvariant();
    }

    public record RemoteSkill(string Name, string HtmlUrl, SourceRef Source, bool IsFolder);

    public record Preview(RemoteSkill Remote, SkillDefinition Skill, string RawText, List<string> Problems,
                          List<string> Warnings, int ExtraFiles, bool AlreadyInstalled)
    {
        public bool CanInstall => Problems.Count == 0;
    }

    private static readonly Regex SegmentRegex = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);

    /// <summary>Parse https://github.com/owner/repo[/tree/branch[/path]]. Repo root defaults to branch main, path "skills".</summary>
    public static SourceRef? ParseSource(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var seg = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length < 2 || seg.Any(s => !SegmentRegex.IsMatch(s) || s is "." or "..")) return null;
        string owner = seg[0], repo = seg[1].EndsWith(".git") ? seg[1][..^4] : seg[1];
        if (seg.Length == 2) return new SourceRef(owner, repo, "main", "skills");
        if (seg.Length >= 4 && seg[2] == "tree") return new SourceRef(owner, repo, seg[3], string.Join('/', seg.Skip(4)));
        return null;
    }

    /// <summary>The trusted entry this ref falls under, or null. Same owner/repo, and the path at or below the entry's.</summary>
    public SourceRef? TrustedMatch(SourceRef target)
    {
        foreach (var entry in _settings.Settings.TrustedSkillSources)
        {
            var t = ParseSource(entry);
            if (t == null || t.Key != target.Key) continue;
            if (!t.Branch.Equals(target.Branch, StringComparison.Ordinal)) continue;
            if (t.Path.Length == 0 || target.Path == t.Path || target.Path.StartsWith(t.Path + "/", StringComparison.Ordinal))
                return t;
        }
        return null;
    }

    private SourceRef RequireTrusted(SourceRef s) =>
        TrustedMatch(s) ?? throw new InvalidOperationException($"{s.HtmlUrl} is not on the trusted skill sources list.");

    /// <summary>List the skills in a trusted folder (sub-folders with a SKILL.md, or .md files).</summary>
    public async Task<List<RemoteSkill>> ListAsync(string sourceUrl, CancellationToken ct = default)
    {
        var src = ParseSource(sourceUrl) ?? throw new ArgumentException("Not a GitHub folder URL: " + sourceUrl);
        RequireTrusted(src);
        var list = new List<RemoteSkill>();
        foreach (var (name, type) in await ListContentsAsync(src, ct))
        {
            if (type == "dir" && SegmentRegex.IsMatch(name))
                list.Add(new RemoteSkill(name, $"https://github.com/{src.Owner}/{src.Repo}/tree/{src.Branch}/{Join(src.Path, name)}", src, true));
            else if (type == "file" && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                     && !name.Equals("README.md", StringComparison.OrdinalIgnoreCase) && SegmentRegex.IsMatch(name))
                list.Add(new RemoteSkill(Path.GetFileNameWithoutExtension(name),
                    $"https://github.com/{src.Owner}/{src.Repo}/blob/{src.Branch}/{Join(src.Path, name)}", src, false));
        }
        return list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Download the skill text (nothing else), parse and check it. Nothing is written.</summary>
    public async Task<Preview> PreviewAsync(RemoteSkill remote, CancellationToken ct = default)
    {
        var src = remote.Source;
        RequireTrusted(src);
        if (!SegmentRegex.IsMatch(remote.Name)) throw new ArgumentException("Bad skill name: " + remote.Name);

        string filePath = remote.IsFolder ? Join(Join(src.Path, remote.Name), "SKILL.md") : Join(src.Path, remote.Name + ".md");
        string raw = await GetTextAsync(RawUrl(src, filePath), ct);

        int extra = 0;
        if (remote.IsFolder)
        {
            try
            {
                var inner = await ListContentsAsync(src with { Path = Join(src.Path, remote.Name) }, ct);
                extra = inner.Count(e => !e.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
            }
            catch (HttpRequestException) { extra = -1; }
        }
        return Check(remote, raw, extra);
    }

    /// <summary>The checks, separate from the network so a harness can feed text straight in.</summary>
    public Preview Check(RemoteSkill remote, string raw, int extraFiles)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        var skill = SkillService.ParseSkillContent(raw, remote.Name);
        skill.Source = SkillSource.User;
        skill.SourceUrl = remote.HtmlUrl;

        bool hasFrontmatter = raw.TrimStart('\uFEFF').StartsWith("---");
        if (!hasFrontmatter) problems.Add("no YAML frontmatter (--- name / description ---)");
        if (!SkillService.ValidNameRegex.IsMatch(skill.Name))
            problems.Add($"name '{skill.Name}' must be lower-case letters, digits and '-' (max 64)");
        if (string.IsNullOrWhiteSpace(skill.Description)) problems.Add("description is missing");
        if (string.IsNullOrWhiteSpace(skill.PromptContent)) problems.Add("the skill has no instructions below the frontmatter");
        if (_skills.GetAllSkills().Any(s => s.IsBuiltIn && s.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"'{skill.Name}' is a built-in skill name — it would replace the built-in one");

        if (skill.AllowedTools.Count == 0)
            warnings.Add("no allowed-tools: while it runs, the AI may use every tool (edit files, run commands)");
        if (skill.UnknownTools.Count > 0)
            warnings.Add("allowed-tools CluadeX does not have: " + string.Join(", ", skill.UnknownTools));
        if (extraFiles > 0)
            warnings.Add($"{extraFiles} other file(s) in the skill folder (scripts, references) are NOT installed and never run");
        else if (extraFiles < 0)
            warnings.Add("could not list the skill folder; only SKILL.md is installed");
        if (!skill.Name.Equals(remote.Name, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"folder is '{remote.Name}' but the skill calls itself '{skill.Name}'");

        bool exists = File.Exists(TargetPath(skill.Name));
        return new Preview(remote, skill, raw, problems, warnings, Math.Max(extraFiles, 0), exists);
    }

    public string TargetPath(string name) => Path.Combine(_skills.UserSkillsDirectory, name + ".md");

    /// <summary>Write the previewed skill to ~/.cluadex/skills/&lt;name&gt;.md. Returns the path, or throws with the reason.</summary>
    public string Install(Preview preview, bool replaceExisting)
    {
        if (!preview.CanInstall) throw new InvalidOperationException(string.Join("; ", preview.Problems));
        RequireTrusted(preview.Remote.Source);
        string target = TargetPath(preview.Skill.Name);
        if (File.Exists(target) && !replaceExisting)
            throw new InvalidOperationException($"{target} already exists");

        Directory.CreateDirectory(_skills.UserSkillsDirectory);
        string text = AddProvenance(preview.RawText, preview.Remote.HtmlUrl);
        string tmp = target + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, target, overwrite: true);
        _skills.ReloadSkills();
        return target;
    }

    /// <summary>Put source / installed lines first in the frontmatter (replacing any the file brought along).</summary>
    public static string AddProvenance(string raw, string sourceUrl)
    {
        string text = raw.Replace("\r\n", "\n").TrimStart('\uFEFF');
        var lines = text.Split('\n').ToList();
        int end = lines.Count > 0 && lines[0].TrimEnd() == "---" ? lines.FindIndex(1, l => l.TrimEnd() == "---") : -1;
        if (end < 0) return text;
        var front = lines.Skip(1).Take(end - 1)
            .Where(l => !l.StartsWith("source:") && !l.StartsWith("installed:"));
        var result = new List<string> { "---", $"source: {sourceUrl}", $"installed: {DateTime.Now:yyyy-MM-dd}" };
        result.AddRange(front);
        result.AddRange(lines.Skip(end));
        return string.Join("\n", result);
    }

    // ── network ─────────────────────────────────────────────────────────────

    private static string Join(string a, string b) => a.Length == 0 ? b : a.TrimEnd('/') + "/" + b;

    private static string RawUrl(SourceRef s, string path) =>
        $"https://raw.githubusercontent.com/{s.Owner}/{s.Repo}/{s.Branch}/{EscapePath(path)}";

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private async Task<List<(string Name, string Type)>> ListContentsAsync(SourceRef s, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{s.Owner}/{s.Repo}/contents/{EscapePath(s.Path)}?ref={Uri.EscapeDataString(s.Branch)}";
        string json = await GetTextAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
        var result = new List<(string, string)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string? name = e.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (name != null && type != null) result.Add((name, type));
        }
        return result;
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("api.github.com" or "raw.githubusercontent.com"))
            throw new InvalidOperationException("refused host " + uri.Host);
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} — {url}");
        if (resp.Content.Headers.ContentLength > MaxSkillBytes * 4)
            throw new InvalidOperationException("answer too large");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxSkillBytes * 4) throw new InvalidOperationException("answer too large");
        }
        if (url.Contains("raw.githubusercontent.com") && buffer.Length > MaxSkillBytes)
            throw new InvalidOperationException($"skill file is larger than {MaxSkillBytes / 1024} KB");
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
