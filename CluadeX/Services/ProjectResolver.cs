using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Which folder a project name (or a path that no longer exists) means, and where a new project should go (owner
/// 2026-10-20: "การเปิดโปรเจคเอไอต้องฉลาดในการเปิดโฟลเด้อที่ทำงานที่เกี่ยวข้อง หรือหาไม่เจอต้องฉลาดในการสร้างมาใหม่").
/// open_project took only an absolute path that exists, so a model that knew the name ("netwix") or guessed a drive
/// wrong failed, and one that needed a new project ran `mkdir` wherever its shell happened to be.
///
/// Matching, best first: the same name ignoring case and separators ("cluadex academic" = CluadeX-Academic), then one
/// name containing the other, then a typo (edit distance ≤ 2 for names of 5+ letters). Candidates are the projects
/// opened before and the folders one level under the project roots (the folders that hold several known projects, and
/// the user's own default root). Ties go to the project opened more often. Pure apart from the two folder functions.
/// </summary>
public static class ProjectResolver
{
    public sealed record Candidate(string Path, string Name, int Opens, DateTime LastOpened);

    /// <summary>Found = one clear match; Matches = every match of the best tier (more than one: say which);
    /// NewPath = where a new project of that name would go.</summary>
    public sealed record Result(string? Found, IReadOnlyList<string> Matches, string NewPath);

    public static Result Resolve(string name, IReadOnlyList<Candidate> known, IEnumerable<string> roots,
        Func<string, IEnumerable<string>> listDirs, string defaultRoot)
    {
        string want = Norm(name);
        var pool = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in known) pool.TryAdd(Trim(k.Path), k);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var dir in SafeList(listDirs, root))
            {
                string leaf = System.IO.Path.GetFileName(Trim(dir));
                if (leaf.Length == 0 || leaf.StartsWith('.')) continue;
                pool.TryAdd(Trim(dir), new Candidate(Trim(dir), leaf, 0, DateTime.MinValue));
            }

        string newPath = System.IO.Path.Combine(defaultRoot, FolderName(name));
        if (want.Length == 0) return new Result(null, Array.Empty<string>(), newPath);

        foreach (var tier in new Func<string, bool>[]
                 {
                     n => n == want,
                     n => want.Length >= 4 && n.Length >= 4 && (n.Contains(want) || want.Contains(n)),
                     n => want.Length >= 5 && n.Length >= 5 && Distance(n, want) <= 2,
                 })
        {
            var hits = pool.Values.Where(c => tier(Norm(c.Name)))
                .OrderByDescending(c => c.Opens).ThenByDescending(c => c.LastOpened).ThenBy(c => c.Path.Length).ToList();
            if (hits.Count == 0) continue;
            // One clear winner: the only match, or the one opened clearly more often than the next.
            bool clear = hits.Count == 1 || hits[0].Opens >= 3 && hits[0].Opens >= 2 * Math.Max(1, hits[1].Opens);
            return new Result(clear ? hits[0].Path : null, hits.Select(h => h.Path).ToList(), newPath);
        }
        return new Result(null, Array.Empty<string>(), newPath);
    }

    /// <summary>
    /// Where new projects go: the setting when it is set, else the folder that already holds the most known projects
    /// (two at least — D:\Code here), else Documents\CluadeX Projects. Pure.
    /// </summary>
    public static string DefaultRoot(string? setting, IEnumerable<string> knownProjects, string documents)
    {
        if (!string.IsNullOrWhiteSpace(setting)) return Trim(setting!);
        var best = knownProjects.Select(p => System.IO.Path.GetDirectoryName(Trim(p)))
            .Where(p => !string.IsNullOrEmpty(p) && !p!.EndsWith(':') && !p.EndsWith(":\\"))
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        return best is { } g && g.Count() >= 2 ? g.Key : System.IO.Path.Combine(documents, "CluadeX Projects");
    }

    /// <summary>The folders that hold two or more known projects — where a project named but never opened most likely is.</summary>
    public static List<string> Roots(IEnumerable<string> knownProjects, string? extra = null)
    {
        var roots = knownProjects.Select(p => System.IO.Path.GetDirectoryName(Trim(p)))
            .Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2).Select(g => g.Key).ToList();
        if (!string.IsNullOrWhiteSpace(extra) && !roots.Contains(Trim(extra!), StringComparer.OrdinalIgnoreCase)) roots.Add(Trim(extra!));
        return roots;
    }

    /// <summary>A folder name from what the user called the project: the words kept (case too), anything Windows does not
    /// allow dropped, spaces as "-". "เว็บ ร้านกาแฟ" stays Thai. Empty → "NewProject".</summary>
    public static string FolderName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); }
            else if (!bad.Contains(c) && c != '.' || c == '.' && sb.Length > 0) sb.Append(c);
        }
        string s = sb.ToString().Trim('-', '.', ' ');
        if (s.Length > 60) s = s[..60].TrimEnd('-', '.');
        return s.Length == 0 ? "NewProject" : s;
    }

    /// <summary>Lower case, letters and digits only (any script): "CluadeX-Academic" → "cluadexacademic".</summary>
    public static string Norm(string s) => new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Levenshtein distance (small names only — the caller limits it).</summary>
    public static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string Trim(string p) => p.TrimEnd('\\', '/');

    private static IEnumerable<string> SafeList(Func<string, IEnumerable<string>> listDirs, string root)
    {
        try { return listDirs(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
