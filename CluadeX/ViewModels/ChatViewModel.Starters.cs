using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>A starter on the new chat page: "Build an app", "Fix a bug"… Its label, hint and prompt are read from
/// the language in use (Services/Localization/StarterKeys), and change with it.</summary>
public sealed class StarterTemplate : INotifyPropertyChanged
{
    /// <summary>The key under starter.* ("app", "web"…).</summary>
    public string Id { get; }
    /// <summary>Segoe MDL2 Assets glyph.</summary>
    public string Glyph { get; }
    /// <summary>Which theme colour the icon takes: 1-3 = Accent.1-3, 4 = Status.Success, 5 = Status.Warning,
    /// 6 = Status.Danger, 7 = Status.Info.</summary>
    public int Tone { get; }

    public string Label => LocalizedResources.Instance[$"starter.{Id}"];
    public string Hint => LocalizedResources.Instance[$"starter.{Id}.hint"];
    public string Prompt => LocalizedResources.Instance[$"starter.{Id}.prompt"];

    private StarterTemplate(string id, string glyph, int tone) { Id = id; Glyph = glyph; Tone = tone; }

    /// <summary>Every starter, in the order the page shows them.</summary>
    public static IReadOnlyList<StarterTemplate> All { get; } =
    [
        new("app", "\uECAA", 1), new("web", "\uE774", 7), new("program", "\uE943", 2), new("game", "\uE7FC", 3),
        new("design", "\uE790", 1), new("bug", "\uEBE8", 6), new("analyze", "\uE9D9", 7), new("comment", "\uE90A", 2),
        new("test", "\uE9D5", 4), new("research", "\uE721", 3), new("deploy", "\uE898", 5), new("explain", "\uE82D", 4),
    ];

    static StarterTemplate()
    {
        // A language switch re-reads every label (LocalizedResources raises Item[] for its own bindings).
        LocalizedResources.Instance.PropertyChanged += (_, _) =>
        {
            foreach (var t in All) t.PropertyChanged?.Invoke(t, new PropertyChangedEventArgs(string.Empty));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A folder offered in the starter's project question.</summary>
public sealed record StarterProject(string Name, string Path, bool IsCurrent);

public partial class ChatViewModel
{
    public IReadOnlyList<StarterTemplate> StarterTemplates => StarterTemplate.All;

    private StarterTemplate? _pendingStarter;
    /// <summary>The starter whose project question is on screen (null = the starters are shown).</summary>
    public StarterTemplate? PendingStarter
    {
        get => _pendingStarter;
        private set
        {
            if (!SetProperty(ref _pendingStarter, value)) return;
            OnPropertyChanged(nameof(IsChoosingStarterProject));
            OnPropertyChanged(nameof(StarterProjectTitle));
            OnPropertyChanged(nameof(StarterWillReplaceInput));
        }
    }
    public bool IsChoosingStarterProject => _pendingStarter != null;
    public string StarterProjectTitle => _pendingStarter == null ? "" : LocF("starter.project.title", _pendingStarter.Label);

    /// <summary>The box holds something the user typed (not the last starter's text): choosing replaces it, and the
    /// question says so before they choose.</summary>
    public bool StarterWillReplaceInput => _pendingStarter != null && !string.IsNullOrWhiteSpace(UserInput)
                                           && !string.Equals(UserInput, _lastStarterFill, StringComparison.Ordinal);
    private string? _lastStarterFill;

    /// <summary>The open project (if any) first, then the recent ones that still exist.</summary>
    public ObservableCollection<StarterProject> StarterProjects { get; } = new();
    public bool HasStarterProjects => StarterProjects.Count > 0;

    /// <summary>Asks for a folder (title, start folder) → the folder, or null when cancelled. The shipping one is the
    /// owner-aware picker; a harness puts its own here.</summary>
    public Func<string, string?, string?> StarterFolderPicker { get; set; } =
        (title, start) => CluadeX.Services.Helpers.FolderPicker.ShowDialog(title, start);

    /// <summary>A starter was filled into the box: the view focuses it and selects the [bracketed] part.</summary>
    public event Action? StarterApplied;

    private ICommand? _pickStarterCommand, _useStarterProjectCommand, _browseStarterProjectCommand,
                      _starterWithoutProjectCommand, _cancelStarterCommand;
    public ICommand PickStarterCommand => _pickStarterCommand ??= new RelayCommand<StarterTemplate>(PickStarter);
    public ICommand UseStarterProjectCommand => _useStarterProjectCommand ??= new RelayCommand<StarterProject>(p => { if (p != null) ApplyStarter(p.Path); });
    public ICommand BrowseStarterProjectCommand => _browseStarterProjectCommand ??= new RelayCommand(BrowseStarterProject);
    public ICommand StarterWithoutProjectCommand => _starterWithoutProjectCommand ??= new RelayCommand(() => ApplyStarter(null));
    public ICommand CancelStarterCommand => _cancelStarterCommand ??= new RelayCommand(() => PendingStarter = null);

    private void InitStarters()
    {
        PropertyChanged += (_, e) =>
        {
            // Another chat opened (or a new one) while the question was up: it belonged to the chat that was left.
            if (e.PropertyName == nameof(CurrentSession)) PendingStarter = null;
            else if (e.PropertyName == nameof(UserInput)) OnPropertyChanged(nameof(StarterWillReplaceInput));
        };
        StarterProjects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStarterProjects));
    }

    /// <summary>A starter was clicked: a new chat has no folder yet, so ask which project first. Nothing is sent.</summary>
    private void PickStarter(StarterTemplate? t)
    {
        if (t == null) return;
        RefreshStarterProjects();
        PendingStarter = t;
    }

    private void RefreshStarterProjects()
    {
        StarterProjects.Clear();
        string current = HasProject && Directory.Exists(WorkingDirectory) ? WorkingDirectory.TrimEnd('\\', '/') : "";
        if (current.Length > 0) StarterProjects.Add(new StarterProject(Path.GetFileName(current), current, true));
        foreach (var e in ProjectRegistryService.Current?.Recent(6) ?? [])
        {
            if (StarterProjects.Count >= 6) break;
            if (PathEquals(e.Path, current)) continue;
            StarterProjects.Add(new StarterProject(e.Name, e.Path, false));
        }
    }

    private void BrowseStarterProject()
    {
        if (_pendingStarter == null) return;
        string? folder = StarterFolderPicker(Loc("chat.folder.pickTitle"), HasProject ? WorkingDirectory : null);
        if (!string.IsNullOrEmpty(folder)) ApplyStarter(folder);
    }

    /// <summary>Open <paramref name="project"/> (null = none) for this chat and put the starter's prompt in the box —
    /// never sends it. A folder deleted since it was listed is refused and the list refreshed.</summary>
    private void ApplyStarter(string? project)
    {
        var t = _pendingStarter;
        if (t == null) return;   // a second click on the same question: already done
        if (project != null)
        {
            if (!Directory.Exists(project))
            {
                StatusText = LocF("starter.project.gone", project);
                RefreshStarterProjects();
                return;
            }
            // Quietly: an "Opened project" line would be the chat's first message and take the page away, and the
            // workbench coming forward would hide the prompt the user is about to edit.
            if (!(HasProject && PathEquals(WorkingDirectory, project)))
                ApplyWorkingDirectory(project, announce: false, showEditor: false);
            if (CurrentSession is { } chat && !PathEquals(chat.ProjectPath, project))
            {
                chat.ProjectPath = project;
                chat.ExtraFolders.Clear();
                RebuildVisibleSessions();
            }
        }
        else if (HasProject)
        {
            ApplyWorkingDirectory("", announce: false, showEditor: false);
            if (CurrentSession is { } chat) { chat.ProjectPath = ""; RebuildVisibleSessions(); }
        }

        string text = t.Prompt;
        _lastStarterFill = text;
        UserInput = text;
        PendingStarter = null;
        StatusText = Loc("starter.filled");
        StarterApplied?.Invoke();
    }
}
