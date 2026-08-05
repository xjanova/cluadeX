using System.Collections.ObjectModel;
using System.IO;

namespace CluadeX.ViewModels;

/// <summary>One row in the command palette.</summary>
public class PaletteItem
{
    public string Icon { get; set; } = "";      // Segoe MDL2 glyph
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Short badge: Page / Skill / File.</summary>
    public string Kind { get; set; } = "";
    public string KindColor { get; set; } = "#8388BD";
    /// <summary>What running this row does.</summary>
    public Action Run { get; set; } = () => { };
    /// <summary>Lowercased haystack used for matching (title + subtitle).</summary>
    public string Haystack { get; set; } = "";
}

/// <summary>
/// The Ctrl+K command palette. The title-bar pill used to be cosmetic — it said
/// "Search files, symbols &amp; commands…" and simply opened Settings. This is the
/// real thing: pages, skills (slash commands) and project files in one list,
/// subsequence-matched, ranked, Enter to run.
/// </summary>
public class CommandPaletteViewModel : ViewModelBase
{
    private readonly Func<IEnumerable<PaletteItem>> _sourceProvider;
    private List<PaletteItem> _all = new();

    public ObservableCollection<PaletteItem> Results { get; } = new();

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set { if (SetProperty(ref _isOpen, value)) OnPropertyChanged(nameof(HasResults)); }
    }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value)) Refilter(); }
    }

    private int _selectedIndex;
    public int SelectedIndex { get => _selectedIndex; set => SetProperty(ref _selectedIndex, value); }

    public bool HasResults => Results.Count > 0;

    public string ResultSummary => Results.Count == 0
        ? "No matches"
        : $"{Results.Count} result{(Results.Count == 1 ? "" : "s")}";

    public CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> sourceProvider)
    {
        _sourceProvider = sourceProvider;
    }

    public void Open()
    {
        // Rebuild every time: the project (and therefore the file list) can change.
        _all = _sourceProvider().ToList();
        Query = "";
        Refilter();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Query = "";
    }

    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        int i = SelectedIndex + delta;
        if (i < 0) i = Results.Count - 1;
        if (i >= Results.Count) i = 0;
        SelectedIndex = i;
    }

    /// <summary>Run the highlighted row and close.</summary>
    public void RunSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var item = Results[SelectedIndex];
        Close();
        try { item.Run(); }
        catch (Exception ex) { CommandErrorSink.Report("CommandPalette", ex); }
    }

    private void Refilter()
    {
        Results.Clear();
        IEnumerable<PaletteItem> matched;

        if (string.IsNullOrWhiteSpace(_query))
        {
            matched = _all.Take(40);
        }
        else
        {
            string q = _query.Trim().ToLowerInvariant();
            matched = _all
                .Select(it => (item: it, score: Score(it.Haystack, q)))
                .Where(t => t.score > 0)
                .OrderByDescending(t => t.score)
                .Take(40)
                .Select(t => t.item);
        }

        foreach (var m in matched) Results.Add(m);
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultSummary));
    }

    /// <summary>
    /// Subsequence match with a bonus for contiguity and for matching at a word start,
    /// so "cev" finds "Code Editor View" and "sett" outranks a file that merely contains it.
    /// 0 = no match.
    /// </summary>
    internal static int Score(string haystack, string needle)
    {
        if (needle.Length == 0) return 1;
        if (haystack.Length == 0) return 0;

        int exact = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (exact >= 0)
            return 1000 - Math.Min(exact, 200) + (exact == 0 ? 200 : 0);

        int hi = 0, score = 0, streak = 0;
        foreach (char c in needle)
        {
            int found = -1;
            for (int i = hi; i < haystack.Length; i++)
            {
                if (haystack[i] == c) { found = i; break; }
            }
            if (found < 0) return 0;              // a needle char is missing → not a match
            bool wordStart = found == 0 || haystack[found - 1] is ' ' or '/' or '\\' or '.' or '_' or '-';
            streak = (found == hi) ? streak + 1 : 0;
            score += 10 + streak * 5 + (wordStart ? 15 : 0);
            hi = found + 1;
        }
        return score;
    }

    // ── Source builders ──────────────────────────────────────────────────────

    /// <summary>Enumerate project files for the palette, skipping noise folders and capping cost.</summary>
    public static IEnumerable<PaletteItem> BuildFileItems(string workingDir, Action<string> openFile, int max = 600)
    {
        if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir)) yield break;

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "dist", "build", "out", ".vs", ".idea",
            "__pycache__", "target", "venv", ".venv", "packages", ".nuget", ".cluadex-backups",
        };

        int count = 0;
        var stack = new Stack<string>();
        stack.Push(workingDir);
        while (stack.Count > 0 && count < max)
        {
            string dir = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }

            foreach (var e in entries)
            {
                if (count >= max) yield break;
                string name = Path.GetFileName(e);
                if (Directory.Exists(e))
                {
                    if (!skip.Contains(name) && !name.StartsWith('.')) stack.Push(e);
                    continue;
                }
                string rel = Path.GetRelativePath(workingDir, e);
                count++;
                string captured = e;
                yield return new PaletteItem
                {
                    Icon = "",
                    Title = name,
                    Subtitle = rel,
                    Kind = "File",
                    KindColor = "#5CFFB0",
                    Haystack = (name + " " + rel).ToLowerInvariant(),
                    Run = () => openFile(captured),
                };
            }
        }
    }
}
