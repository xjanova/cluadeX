using System.IO;

namespace CluadeX.Services.Setup;

/// <summary>
/// Which girl stands on each page of one setup-wizard run (installer contract 2.6): the eight names are shuffled once per
/// run, page i shows name[i % 8] in a random pose, so nobody repeats inside a run. The run's list is remembered in
/// <c>&lt;data&gt;\setup-girls.txt</c>; when the new shuffle would open on the same girl as last time it is shuffled again.
/// </summary>
public static class SetupGirls
{
    public const string FileName = "setup-girls.txt";

    /// <summary>A shuffled plan (name, pose 1|2) whose first girl differs from <paramref name="lastFirst"/> when that is possible.</summary>
    public static IReadOnlyList<(string Name, int Pose)> Plan(IReadOnlyList<string> names, string? lastFirst, Random rng)
    {
        if (names.Count == 0) return Array.Empty<(string, int)>();
        var list = names.ToList();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
            if (list.Count < 2 || !string.Equals(list[0], lastFirst, StringComparison.OrdinalIgnoreCase)) break;
        }
        // Still the same first girl after eight shuffles (a tiny chance): swap her with the second.
        if (list.Count > 1 && string.Equals(list[0], lastFirst, StringComparison.OrdinalIgnoreCase)) (list[0], list[1]) = (list[1], list[0]);
        return list.Select(n => (n, rng.Next(1, 3))).ToList();
    }

    /// <summary>The first girl of the run saved in <paramref name="file"/>, or null.</summary>
    public static string? ReadFirst(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            string line = File.ReadLines(file).FirstOrDefault() ?? "";
            string first = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            int colon = first.IndexOf(':');
            return colon > 0 ? first[..colon] : first.Length > 0 ? first : null;
        }
        catch { return null; }
    }

    /// <summary>"cluadex:2,claude:1,…" — what <see cref="ReadFirst"/> reads back.</summary>
    public static string Format(IReadOnlyList<(string Name, int Pose)> plan) => string.Join(",", plan.Select(p => $"{p.Name}:{p.Pose}"));

    /// <summary>This run's plan for the girls the build has, remembered in <paramref name="dataRoot"/> (best effort).</summary>
    public static IReadOnlyList<(string Name, int Pose)> ForThisRun(string dataRoot)
    {
        string file = Path.Combine(dataRoot, FileName);
        var plan = Plan(GirlArt.Names, ReadFirst(file), new Random());
        try { Directory.CreateDirectory(dataRoot); File.WriteAllText(file, Format(plan)); } catch { /* remembering is a nicety */ }
        return plan;
    }
}
