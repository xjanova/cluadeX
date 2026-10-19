using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.ViewModels;

/// <summary>One editable CSS property in the inspector.</summary>
public sealed class StyleField : ViewModelBase
{
    private string _value = "";
    public string Prop { get; init; } = "";
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";
    public bool IsColor { get; init; }
    public string[]? Options { get; init; }
    public bool IsChoice => Options != null;
    public string Original { get; set; } = "";

    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value ?? "")) return;
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Swatch));
            Edited?.Invoke(this);
        }
    }

    public bool IsChanged => !string.Equals(Value.Trim(), Original.Trim(), StringComparison.Ordinal);

    public Brush? Swatch
    {
        get
        {
            if (!IsColor) return null;
            try { return (Brush?)new BrushConverter().ConvertFromString(CssColor.ToWpf(Value)); }
            catch { return null; }
        }
    }

    public event Action<StyleField>? Edited;

    /// <summary>Set without raising Edited (loading a new selection).</summary>
    public void Load(string value)
    {
        _value = value;
        Original = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(Swatch));
    }
}

/// <summary>A group of fields in the inspector ("Layout", "Spacing"…).</summary>
public sealed class StyleGroup
{
    public string Title { get; init; } = "";
    public ObservableCollection<StyleField> Fields { get; } = new();
}

/// <summary>An attribute that the element has (href, src, alt…).</summary>
public sealed class AttrField : ViewModelBase
{
    private string _value = "";
    public string Name { get; init; } = "";
    public string Original { get; set; } = "";
    public string Value { get => _value; set { if (SetProperty(ref _value, value ?? "")) { OnPropertyChanged(nameof(IsChanged)); Edited?.Invoke(this); } } }
    public bool IsChanged => Value != Original;
    public event Action<AttrField>? Edited;
}

/// <summary>A node in the Layers panel.</summary>
public sealed class LayerItem : ViewModelBase
{
    private bool _expanded, _selected;
    public DomNode Node { get; init; } = null!;
    public LayerItem? Parent { get; init; }
    public string Label => Node.Label;
    public string? Snippet => Node.Snippet;
    public bool HasSource => Node.Src != null;
    public ObservableCollection<LayerItem> Children { get; } = new();
    public bool IsExpanded { get => _expanded; set => SetProperty(ref _expanded, value); }
    public bool IsSelected { get => _selected; set { if (SetProperty(ref _selected, value) && value) Picked?.Invoke(this); } }
    public event Action<LayerItem>? Picked;
}

/// <summary>A starter template card in the gallery.</summary>
public sealed class TemplateCard
{
    public StudioTemplate Template { get; init; } = null!;
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Stack => Template.Stack;
    public string Icon => Template.Icon;
    public bool NeedsNode => Template.NeedsNode;
    public Brush Accent { get; init; } = Brushes.SlateBlue;
}

/// <summary>Tiny CSS colour helper: rgb()/rgba() → #AARRGGBB for WPF swatches.</summary>
internal static class CssColor
{
    public static string ToWpf(string css)
    {
        css = css.Trim();
        if (css.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = css.IndexOf('('), close = css.LastIndexOf(')');
            if (open > 0 && close > open)
            {
                var parts = css[(open + 1)..close].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                {
                    byte a = 255;
                    if (parts.Length >= 4 && double.TryParse(parts[3].TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var av))
                        a = (byte)Math.Clamp(parts[3].EndsWith('%') ? av * 2.55 : av * 255, 0, 255);
                    return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
                }
            }
        }
        return css;
    }
}

/// <summary>
/// The UI Studio page: a live view of the project's UI where any element can be picked, inspected and edited —
/// the edit lands in the exact source line that rendered it. Talks to the page through <see cref="StudioService"/>
/// (the view owns the WebView2 and forwards overlay messages here).
/// </summary>
public sealed partial class UiStudioViewModel : ViewModelBase
{
    private readonly StudioService _studio;
    private readonly LocalizationService _loc;
    private readonly FileSystemService _fs;
    private readonly ChatViewModel _chat;
    private readonly CodeEditorViewModel _editor;
    private readonly DebugLogService? _log;
    private readonly DispatcherTimer _previewTimer;
    private readonly Dictionary<string, string?> _pendingPreview = new();
    private CancellationTokenSource? _searchCts;

    public UiStudioViewModel(StudioService studio, LocalizationService loc, FileSystemService fs, ChatViewModel chat,
        CodeEditorViewModel editor, DebugLogService? log = null)
    {
        _studio = studio;
        _loc = loc;
        _fs = fs;
        _chat = chat;
        _editor = editor;
        _log = log;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _previewTimer.Tick += (_, _) => FlushPreview();

        BuildStyleGroups();
        LoadTemplates();
        Candidates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCandidates));
        NewProjectParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CluadeX Projects");

        studio.ProjectChanged += OnProjectChanged;
        studio.SelectionChanged += OnSelectionChanged;
        studio.TreeChanged += RebuildLayers;
        studio.ServingChanged += () => { OnPropertyChanged(nameof(IsServing)); OnPropertyChanged(nameof(IsStarting)); OnPropertyChanged(nameof(ShowStartButton)); OnServingForPages(); };
        studio.ServerOutput += line =>
        {
            ServerLog.Add(line);
            while (ServerLog.Count > 400) ServerLog.RemoveAt(0);
            LastLogLine = line;
        };
        studio.SourceWritten += _ => OnPropertyChanged(nameof(CanUndo));
        loc.LanguageChanged += () => { LoadTemplates(); RefreshTexts(); };

        OpenFolderCommand = new RelayCommand(OpenFolder);
        UseCurrentProjectCommand = new RelayCommand(() => { if (_fs.AppWorkingDirectory is { Length: > 0 } p) OpenProject(p, autoStart: true); },
            () => !string.IsNullOrEmpty(_fs.AppWorkingDirectory));
        ShowGalleryCommand = new RelayCommand(() => ShowGallery = true);
        CloseGalleryCommand = new RelayCommand(() => ShowGallery = false);
        PickTemplateCommand = new RelayCommand<TemplateCard>(c => { if (c != null) SelectedTemplate = c; });
        BrowseParentCommand = new RelayCommand(() =>
        {
            var p = Services.Helpers.FolderPicker.ShowDialog(T("studio.tpl.pickParent"), Directory.Exists(NewProjectParent) ? NewProjectParent : null);
            if (!string.IsNullOrEmpty(p)) NewProjectParent = p;
        });
        CreateFromTemplateCommand = new RelayCommand(CreateFromTemplate, () => SelectedTemplate != null && !string.IsNullOrWhiteSpace(NewProjectName));
        StartCommand = new AsyncRelayCommand(StartAsync, () => _studio.HasProject && !_studio.IsStarting);
        StopCommand = new RelayCommand(() => { _studio.StopServing(); Status(T("studio.stopped")); });
        ReloadCommand = new RelayCommand(() => _studio.RunScript("location.reload()"));
        NavigateCommand = new RelayCommand(() =>
        {
            string u = Url.Trim();
            if (u.Length == 0) return;
            if (!u.Contains("://", StringComparison.Ordinal)) u = (u.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || u.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + u;
            NoteTypedUrl(u);
            NavigateTo?.Invoke(u);
        });
        SetDeviceCommand = new RelayCommand<string>(d => Device = d ?? "desktop");
        ToggleModeCommand = new RelayCommand(() => IsSelectMode = !IsSelectMode);
        ToggleLogCommand = new RelayCommand(() => ShowLog = !ShowLog);

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsDirty && CanEditSource);
        RevertCommand = new RelayCommand(Revert, () => IsDirty);
        SelectParentCommand = new RelayCommand(() => _studio.RunScript("__cx.selectParent()"), () => HasSelection);
        MoveUpCommand = new RelayCommand(() => Structure("moveUp"), () => CanEditSource);
        MoveDownCommand = new RelayCommand(() => Structure("moveDown"), () => CanEditSource);
        DuplicateCommand = new RelayCommand(() => Structure("duplicate"), () => CanEditSource);
        DeleteCommand = new RelayCommand(() => Structure("delete"), () => CanEditSource);
        UndoCommand = new RelayCommand(Undo, () => _studio.UndoCount > 0);
        OpenSourceCommand = new RelayCommand(OpenSource, () => SourceRef != null);
        OpenCandidateCommand = new RelayCommand<SourceCandidate>(c => { if (c != null) { _ = _editor.OpenAtLineAsync(c.FullPath, c.Line); OpenPageRequested?.Invoke("Code"); } });
        AskAiCommand = new RelayCommand(AskAi, () => HasSelection && !string.IsNullOrWhiteSpace(AiRequest));
        OpenChatCommand = new RelayCommand(() => OpenPageRequested?.Invoke("Chat"));
        CopySelectorCommand = new RelayCommand(() => { if (Selection != null) TryCopy(Selection.Selector); });
        ExportCssCommand = new AsyncRelayCommand(ExportCssAsync);
        InstallLocatorCommand = new RelayCommand(InstallLocator);
        OpenGraphCommand = new RelayCommand(() => OpenPageRequested?.Invoke("NodeGraph"));
        HookAppProject();
    }

    private string T(string key) => _loc.T(key);
    private string T(string key, params object[] args) => _loc.T(key, args);
    internal string Tr(string key, params object[] args) => args.Length == 0 ? _loc.T(key) : _loc.T(key, args);

    private static Brush TryBrush(string hex)
    {
        try { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        catch { return Brushes.SlateBlue; }
    }

    // ───────────────────────────── navigation hooks (MainViewModel / the view) ─────────────────────────────

    /// <summary>Ask the main window to show another page ("Chat", "Code", "NodeGraph").</summary>
    public event Action<string>? OpenPageRequested;
    /// <summary>The view navigates its browser (address bar).</summary>
    public event Action<string>? NavigateTo;

    // ───────────────────────────── project ─────────────────────────────

    public bool HasProject => _studio.HasProject;
    public bool NoProject => !_studio.HasProject;
    public string ProjectName => HasProject ? Path.GetFileName(_studio.Root.TrimEnd(Path.DirectorySeparatorChar)) : "";
    public string ProjectPath => _studio.Root;
    public string ProjectKindText => _studio.Project.Kind switch
    {
        StudioProjectKind.Static => T("studio.kind.static"),
        StudioProjectKind.Vite => T("studio.kind.vite", _studio.Project.Framework),
        StudioProjectKind.NodeOther => T("studio.kind.node", _studio.Project.Framework),
        StudioProjectKind.Unsupported => T("studio.kind.unsupported"),
        StudioProjectKind.Server => T("studio.kind.server", _studio.Project.Framework),
        StudioProjectKind.Simulated => T("studio.kind.simulated", _studio.Project.Framework),
        _ => "",
    };
    public bool IsUnsupported => _studio.Project.Kind == StudioProjectKind.Unsupported;
    public string UnsupportedText => _studio.Project.UnsupportedReason is { } k ? T(k) : "";
    public bool ShowLocatorBanner => _studio.Project.Kind == StudioProjectKind.Vite && !_studio.Project.LocatorInstalled;
    public bool ShowApproxBanner => _studio.Project.Kind == StudioProjectKind.NodeOther;
    public string CurrentAppProject => _fs.AppWorkingDirectory is { Length: > 0 } p ? Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)) : "";
    public bool HasAppProject => !string.IsNullOrEmpty(_fs.AppWorkingDirectory);

    public bool IsServing => _studio.IsServing;
    public bool IsStarting => _studio.IsStarting;
    public bool ShowStartButton => HasProject && !IsUnsupported && !IsSimulated && !IsServing && !IsStarting;

    /// <summary>Called the first time the page is shown (and on later visits): follow the app's open project when
    /// the Studio has none yet. A static site starts at once; a Node project waits for Start (npm install may be big).</summary>
    public void PageShown()
    {
        OnPropertyChanged(nameof(CurrentAppProject));
        OnPropertyChanged(nameof(HasAppProject));
        if (!_studio.HasProject && _fs.AppWorkingDirectory is { Length: > 0 } p && Directory.Exists(p))
            OpenProject(p, autoStart: StudioProject.Detect(p).Kind == StudioProjectKind.Static);
        CatchUpOnShow();   // what following the chat's project left for the first look (a dev server, the screens)
    }

    private void OpenFolder()
    {
        var p = Services.Helpers.FolderPicker.ShowDialog(T("studio.pickProject"), HasProject ? _studio.Root : _fs.AppWorkingDirectory);
        if (!string.IsNullOrEmpty(p)) OpenProject(p, autoStart: true);
    }

    public void OpenProject(string path, bool autoStart)
    {
        if (!Directory.Exists(path)) { Status(T("studio.err.noFolder"), error: true); return; }
        _studio.OpenProject(path);
        // The Studio's project is the app's project: the chat, the AI and the code editor work on the same files.
        try { _chat.OpenProjectQuietly(path); }
        catch (Exception ex) { _log?.Warn("UiStudio", "could not switch the app project", ex); }
        ShowGallery = false;
        if (_studio.Project.Kind == StudioProjectKind.Simulated) { ShowPages = true; return; }   // a native app: its screens are the view
        if (autoStart && !IsUnsupported) _ = StartAsync();
        else if (IsUnsupported) Status(UnsupportedText, error: true);
    }

    private async Task StartAsync()
    {
        if (_studio.Project.Kind is StudioProjectKind.Vite or StudioProjectKind.NodeOther)
        {
            ShowLog = true;
            Status(Directory.Exists(Path.Combine(_studio.Root, "node_modules")) ? T("studio.startingDev") : T("studio.installing"));
        }
        string? err = await _studio.StartAsync();
        if (err != null) Status(T(err), error: true);
        else if (_studio.Project.Kind == StudioProjectKind.Static) Status(T("studio.started"));
        if (err == null && _studio.IsServing && _studio.Project.Kind is StudioProjectKind.Vite or StudioProjectKind.NodeOther && _studio.HasProject)
        {
            // remembered: next time the chat opens this project its preview is started in the background, ready
            _store ??= new PageShotStore(_studio.DataRoot, _studio.Root);
            if (!_store.State.DevServerApproved) { _store.State.DevServerApproved = true; _store.SaveState(); }
        }
    }

    private void OnProjectChanged()
    {
        ResetPages();
        ServerLog.Clear();
        LastLogLine = "";
        foreach (var name in new[] { nameof(HasProject), nameof(NoProject), nameof(ProjectName), nameof(ProjectPath), nameof(ProjectKindText),
                     nameof(IsUnsupported), nameof(UnsupportedText), nameof(ShowLocatorBanner), nameof(ShowApproxBanner), nameof(ShowStartButton), nameof(CanUndo),
                     nameof(IsSimulated), nameof(IsDesktopApp), nameof(IsServerSite), nameof(IsLiveView) })
            OnPropertyChanged(name);
    }

    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(ProjectKindText));
        OnPropertyChanged(nameof(UnsupportedText));
        OnPropertyChanged(nameof(PrecisionText));
    }

    // ───────────────────────────── browser chrome ─────────────────────────────

    private string _url = "";
    public string Url { get => _url; set => SetProperty(ref _url, value ?? ""); }

    private bool _isSelectMode = true;
    public bool IsSelectMode
    {
        get => _isSelectMode;
        set
        {
            if (!SetProperty(ref _isSelectMode, value)) return;
            OnPropertyChanged(nameof(IsInteractMode));
            _studio.RunScript($"window.__cx && __cx.setMode('{(value ? "select" : "interact")}')");
        }
    }

    public bool IsInteractMode { get => !_isSelectMode; set => IsSelectMode = !value; }

    private string _device = "desktop";
    public string Device
    {
        get => _device;
        set
        {
            if (!SetProperty(ref _device, value)) return;
            OnPropertyChanged(nameof(DeviceWidth));
            OnDeviceChangedForPages();
        }
    }
    /// <summary>The page frame's width: NaN fills the page area.</summary>
    public double DeviceWidth => Device switch { "laptop" => 1280, "tablet" => 768, "mobile" => 390, _ => double.NaN };

    public ObservableCollection<string> ServerLog { get; } = new();
    private string _lastLogLine = "";
    public string LastLogLine { get => _lastLogLine; set => SetProperty(ref _lastLogLine, value); }
    private bool _showLog;
    public bool ShowLog { get => _showLog; set => SetProperty(ref _showLog, value); }

    /// <summary>The view reports the page's address.</summary>
    public void NotePage(string? url)
    {
        _studio.NotePage(url);
        MarkCurrentPage(url);
        Url = url ?? "";
        OnPropertyChanged(nameof(IsRemotePage));
        OnPropertyChanged(nameof(CanEditSource));
    }
    public bool IsRemotePage => !string.IsNullOrEmpty(_studio.PageUrl) && !_studio.PageIsLocal;

    // ───────────────────────────── status line ─────────────────────────────

    private string _status = "";
    private bool _statusIsError;
    private DispatcherTimer? _statusTimer;
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

    public void Status(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 9 : 5) };
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(error ? 9 : 5);
        _statusTimer.Tick -= ClearStatus;
        _statusTimer.Tick += ClearStatus;
        _statusTimer.Start();
    }
    private void ClearStatus(object? s, EventArgs e) { _statusTimer?.Stop(); StatusText = ""; StatusIsError = false; }

    // ───────────────────────────── templates ─────────────────────────────

    public ObservableCollection<TemplateCard> Templates { get; } = new();
    private TemplateCard? _selectedTemplate;
    public TemplateCard? SelectedTemplate { get => _selectedTemplate; set => SetProperty(ref _selectedTemplate, value); }
    private bool _showGallery;
    public bool ShowGallery { get => _showGallery; set => SetProperty(ref _showGallery, value); }
    private string _newProjectName = "my-app";
    public string NewProjectName { get => _newProjectName; set { if (SetProperty(ref _newProjectName, value)) OnPropertyChanged(nameof(NewProjectFolder)); } }
    private string _newProjectParent = "";
    public string NewProjectParent { get => _newProjectParent; set { if (SetProperty(ref _newProjectParent, value)) OnPropertyChanged(nameof(NewProjectFolder)); } }
    public string NewProjectFolder => string.IsNullOrWhiteSpace(NewProjectName) ? NewProjectParent : Path.Combine(NewProjectParent, StudioTemplates.Slug(NewProjectName));

    private void LoadTemplates()
    {
        string? selectedId = SelectedTemplate?.Template.Id;
        Templates.Clear();
        foreach (var t in StudioTemplates.All())
            Templates.Add(new TemplateCard
            {
                Template = t, Name = t.DisplayName(_loc.CurrentLanguage), Description = t.DisplayDescription(_loc.CurrentLanguage),
                Accent = TryBrush(t.Accent),
            });
        SelectedTemplate = Templates.FirstOrDefault(c => c.Template.Id == selectedId) ?? Templates.FirstOrDefault();
    }

    private void CreateFromTemplate()
    {
        var card = SelectedTemplate;
        if (card == null || string.IsNullOrWhiteSpace(NewProjectName)) return;
        string folder = NewProjectFolder;
        string? err = StudioTemplates.CreateProject(card.Template.Id, folder, NewProjectName.Trim());
        if (err != null) { Status(T(err, folder), error: true); return; }
        _log?.Info("UiStudio", $"created {card.Template.Id} at {folder}");
        Status(T("studio.tpl.created", card.Name, folder));
        OpenProject(folder, autoStart: true);
    }

    // ───────────────────────────── layers ─────────────────────────────

    public ObservableCollection<LayerItem> Layers { get; } = new();
    private readonly Dictionary<int, LayerItem> _layerById = new();
    private bool _syncingLayer;
    public bool TreeTruncated => _studio.TreeTruncated;

    private void RebuildLayers()
    {
        Layers.Clear();
        _layerById.Clear();
        if (_studio.Tree is { } root)
        {
            var item = MakeLayer(root, null, 0);
            Layers.Add(item);
        }
        OnPropertyChanged(nameof(TreeTruncated));
        if (Selection != null) SyncLayerSelection(Selection.NodeId);
    }

    private LayerItem MakeLayer(DomNode node, LayerItem? parent, int depth)
    {
        var item = new LayerItem { Node = node, Parent = parent, IsExpanded = depth < 2 };
        item.Picked += OnLayerPicked;
        _layerById[node.NodeId] = item;
        foreach (var k in node.Kids) item.Children.Add(MakeLayer(k, item, depth + 1));
        return item;
    }

    private void OnLayerPicked(LayerItem item)
    {
        if (_syncingLayer) return;
        _studio.SelectNode(item.Node.NodeId);
    }

    public void HoverLayer(LayerItem? item) => _studio.RunScript(item == null ? "window.__cx && __cx.hoverById(-1)" : $"window.__cx && __cx.hoverById({item.Node.NodeId})");

    private void SyncLayerSelection(int nodeId)
    {
        if (!_layerById.TryGetValue(nodeId, out var item)) return;
        _syncingLayer = true;
        try
        {
            for (var p = item.Parent; p != null; p = p.Parent) p.IsExpanded = true;
            item.IsSelected = true;
        }
        finally { _syncingLayer = false; }
    }

    // ───────────────────────────── selection & inspector ─────────────────────────────

    public StudioElement? Selection => _studio.Selection;
    public bool HasSelection => Selection != null;
    public bool NoSelection => Selection == null;
    internal StudioService Studio => _studio;
    public string SelectionLabel => Selection?.Label ?? "";
    public string SelectionSize => Selection == null ? "" : $"{Math.Round(Selection.Width)} × {Math.Round(Selection.Height)}";
    public SourceRef? SourceRef => Selection == null ? null : _studio.ResolveSource(Selection.Src ?? Selection.AncestorSrc);
    public string SourceText => SourceRef?.ToString() ?? "";
    public bool CanEditSource => Selection is { HasExactSource: true } && _studio.PageIsLocal && _studio.ResolveSource(Selection.Src) != null;

    /// <summary>exact / ancestor / none — drives the precision chip.</summary>
    public string Precision => Selection == null ? "" : CanEditSource ? "exact" : Selection.AncestorSrc != null ? "ancestor" : "none";
    public string PrecisionText => Precision switch
    {
        "exact" => T("studio.precision.exact"),
        "ancestor" => T("studio.precision.ancestor"),
        "none" => IsRemotePage ? T("studio.precision.remote") : T("studio.precision.none"),
        _ => "",
    };

    private string _textValue = "", _textOriginal = "";
    public string TextValue
    {
        get => _textValue;
        set
        {
            if (!SetProperty(ref _textValue, value ?? "")) return;
            OnPropertyChanged(nameof(IsDirty));
            _studio.RunScript($"window.__cx && __cx.previewText({JsonSerializer.Serialize(_textValue)})");
        }
    }
    public bool CanEditText => Selection is { OnlyText: true };

    private string _classValue = "", _classOriginal = "";
    public string ClassValue
    {
        get => _classValue;
        set
        {
            if (!SetProperty(ref _classValue, value ?? "")) return;
            OnPropertyChanged(nameof(IsDirty));
            _studio.RunScript($"window.__cx && __cx.previewClass({JsonSerializer.Serialize(_classValue)})");
        }
    }

    public ObservableCollection<StyleGroup> StyleGroups { get; } = new();
    public ObservableCollection<AttrField> AttrFields { get; } = new();
    public ObservableCollection<SourceCandidate> Candidates { get; } = new();
    public bool HasCandidates => Candidates.Count > 0;
    private bool _searching;
    public bool IsSearchingCandidates { get => _searching; private set => SetProperty(ref _searching, value); }

    public bool IsDirty =>
        _textValue != _textOriginal || _classValue != _classOriginal
        || StyleGroups.SelectMany(g => g.Fields).Any(f => f.IsChanged) || AttrFields.Any(a => a.IsChanged);

    private static readonly string[] Displays = { "block", "flex", "grid", "inline", "inline-block", "inline-flex", "none" };
    private static readonly string[] FlexDirs = { "row", "column", "row-reverse", "column-reverse" };
    private static readonly string[] Justify = { "normal", "flex-start", "center", "flex-end", "space-between", "space-around", "space-evenly" };
    private static readonly string[] Align = { "normal", "stretch", "flex-start", "center", "flex-end", "baseline" };
    private static readonly string[] Weights = { "300", "400", "500", "600", "700", "800", "900" };
    private static readonly string[] TextAligns = { "start", "left", "center", "right", "justify" };

    private void BuildStyleGroups()
    {
        StyleGroup G(string titleKey, params StyleField[] fields)
        {
            var g = new StyleGroup { Title = T(titleKey) };
            foreach (var f in fields) { f.Edited += OnStyleEdited; g.Fields.Add(f); }
            StyleGroups.Add(g);
            return g;
        }
        StyleField F(string prop, string label, bool color = false, string[]? options = null) => new() { Prop = prop, Label = label, IsColor = color, Options = options };

        G("studio.group.layout",
            F("display", "display", options: Displays), F("width", "width"), F("height", "height"),
            F("flex-direction", "direction", options: FlexDirs), F("justify-content", "justify", options: Justify),
            F("align-items", "align", options: Align), F("gap", "gap"));
        G("studio.group.spacing",
            F("padding-top", "pad ↑"), F("padding-right", "pad →"), F("padding-bottom", "pad ↓"), F("padding-left", "pad ←"),
            F("margin-top", "mar ↑"), F("margin-right", "mar →"), F("margin-bottom", "mar ↓"), F("margin-left", "mar ←"));
        G("studio.group.type",
            F("color", "color", color: true), F("font-size", "size"), F("font-weight", "weight", options: Weights),
            F("line-height", "line"), F("letter-spacing", "tracking"), F("text-align", "align", options: TextAligns));
        G("studio.group.look",
            F("background-color", "background", color: true), F("border-radius", "radius"), F("opacity", "opacity"),
            F("box-shadow", "shadow"));
    }

    private void OnStyleEdited(StyleField f)
    {
        _pendingPreview[f.Prop] = f.Value;
        _previewTimer.Stop();
        _previewTimer.Start();
        OnPropertyChanged(nameof(IsDirty));
    }

    private void FlushPreview()
    {
        _previewTimer.Stop();
        if (_pendingPreview.Count == 0) return;
        string json = JsonSerializer.Serialize(_pendingPreview);
        _pendingPreview.Clear();
        _studio.RunScript($"window.__cx && __cx.previewStyle({json})");
    }

    private int _shownNodeId = -1;

    private void OnSelectionChanged()
    {
        var el = Selection;
        bool wasDirty = IsDirty;
        bool sameElement = el != null && el.NodeId == _shownNodeId;
        _shownNodeId = el?.NodeId ?? -1;

        if (wasDirty && sameElement)
        {
            // The page re-described the element being edited (a stylesheet reload, a refresh): keep the user's
            // unsaved values — reloading them from the page would make the preview look like the saved state.
            foreach (var n in new[] { nameof(SelectionSize), nameof(SourceRef), nameof(SourceText), nameof(CanEditSource), nameof(Precision), nameof(PrecisionText) })
                OnPropertyChanged(n);
            return;
        }
        if (wasDirty)
        {
            // Another element was picked with edits not saved: put the page back so it never shows a change
            // that is not in the source.
            _studio.RunScript("window.__cx && __cx.revertPreview()");
            Status(T("studio.discarded"));
        }

        _previewTimer.Stop();
        _pendingPreview.Clear();
        _textOriginal = _textValue = el?.OnlyText == true ? el.Text : "";
        _classOriginal = _classValue = el?.Classes ?? "";
        foreach (var f in StyleGroups.SelectMany(g => g.Fields))
            f.Load(el != null && el.Styles.TryGetValue(f.Prop, out var v) ? v : "");
        AttrFields.Clear();
        if (el != null)
            foreach (var (name, value) in el.Attrs)
            {
                var a = new AttrField { Name = name, Original = value };
                a.Value = value;
                a.Edited += x =>
                {
                    OnPropertyChanged(nameof(IsDirty));
                    _studio.RunScript($"window.__cx && __cx.previewAttr({JsonSerializer.Serialize(x.Name)}, {JsonSerializer.Serialize(x.Value)})");
                };
                AttrFields.Add(a);
            }
        foreach (var name in new[] { nameof(HasSelection), nameof(NoSelection), nameof(SelectionLabel), nameof(SelectionSize), nameof(SourceRef), nameof(SourceText),
                     nameof(CanEditSource), nameof(Precision), nameof(PrecisionText), nameof(TextValue), nameof(CanEditText), nameof(ClassValue), nameof(IsDirty) })
            OnPropertyChanged(name);
        if (el != null) SyncLayerSelection(el.NodeId);
        FindCandidates(el);
    }

    private void FindCandidates(StudioElement? el)
    {
        _searchCts?.Cancel();
        Candidates.Clear();
        if (el == null || CanEditSource || !_studio.HasProject || IsRemotePage) { IsSearchingCandidates = false; return; }
        var cts = _searchCts = new CancellationTokenSource();
        string root = _studio.Root;
        IsSearchingCandidates = true;
        Task.Run(() => SourceSearch.Find(root, el, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested) return;
            IsSearchingCandidates = false;
            if (t.Status == TaskStatus.RanToCompletion) foreach (var c in t.Result) Candidates.Add(c);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ───────────────────────────── writing to source ─────────────────────────────

    private async Task SaveAsync()
    {
        var el = Selection;
        if (el == null || !CanEditSource) return;
        FlushPreview();
        var styleChanges = StyleGroups.SelectMany(g => g.Fields).Where(f => f.IsChanged)
            .ToDictionary(f => f.Prop, f => string.IsNullOrWhiteSpace(f.Value) ? null : f.Value.Trim());
        if (styleChanges.Count > 0 && _studio.Evaluate != null)
        {
            // Ask the browser which values it accepts — a typo is refused here instead of being written to the source.
            string? json = await _studio.Evaluate($"window.__cx ? __cx.invalidCss({JsonSerializer.Serialize(styleChanges)}) : []");
            string[] bad = Array.Empty<string>();
            try { bad = json == null ? bad : JsonSerializer.Deserialize<string[]>(json) ?? bad; } catch { }
            if (bad.Length > 0)
            {
                Status(T("studio.badCss", string.Join(", ", bad.Select(b => $"{b}: {styleChanges[b]}"))), error: true);
                return;
            }
            if (!ReferenceEquals(el, Selection)) return;   // the user picked something else while the page answered
        }
        bool textChanged = _textValue != _textOriginal && CanEditText;
        bool classChanged = _classValue != _classOriginal;
        var attrChanges = AttrFields.Where(a => a.IsChanged).ToList();

        var ops = new List<Func<string, SourceTag, SourceDialect, SourceEdit>>();
        if (styleChanges.Count > 0) ops.Add((t, tag, d) => SourceTagEditor.SetStyle(t, tag, d, styleChanges!));
        if (classChanged) ops.Add((t, tag, d) => SourceTagEditor.SetAttribute(t, tag, d, "class", _classValue.Trim().Length == 0 ? null : _classValue.Trim()));
        foreach (var a in attrChanges) { var n = a.Name; var v = a.Value; ops.Add((t, tag, d) => SourceTagEditor.SetAttribute(t, tag, d, n, v)); }
        if (textChanged) ops.Add((t, tag, d) => SourceTagEditor.SetText(t, tag, d, _textValue));
        if (ops.Count == 0) return;

        string? err = _studio.EditSource(el.Src, el.Tag, "inspector", (text, tag, dialect) =>
        {
            string current = text;
            var currentTag = tag;
            foreach (var op in ops)
            {
                var r = op(current, currentTag, dialect);
                if (!r.Ok) return r;
                current = r.Text!;
                currentTag = SourceTagEditor.ParseOpenTag(current, tag.Start, dialect)!;
                if (currentTag == null) return SourceEdit.Fail("stale");
            }
            return SourceEdit.Done(current);
        });
        AfterWrite(err, T("studio.saved", SourceText));
    }

    private void AfterWrite(string? err, string okMessage)
    {
        if (err != null)
        {
            Status(T("studio.saveFailed", T(err)), error: true);
            return;
        }
        Status(okMessage);
        _studio.RunScript("window.__cx && __cx.commitPreview()");
        _textOriginal = _textValue;
        _classOriginal = _classValue;
        foreach (var f in StyleGroups.SelectMany(g => g.Fields)) f.Original = f.Value;
        foreach (var a in AttrFields) a.Original = a.Value;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanUndo));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Revert()
    {
        _studio.RunScript("window.__cx && __cx.revertPreview()");   // the page answers with a fresh "select"
    }

    /// <summary>Structural edits from the toolbar or the page's keys (Delete, Ctrl+D, Alt+↑/↓, Ctrl+Z).</summary>
    public void Structure(string cmd)
    {
        var el = Selection;
        if (cmd == "undo") { Undo(); return; }
        if (el == null) return;
        if (!CanEditSource) { Status(T(IsRemotePage ? "studio.err.remotePage" : "studio.err.noSource"), error: true); return; }
        string? err;
        switch (cmd)
        {
            case "delete":
                int choice = Views.Editor.ChoiceDialog.Show(T("studio.deleteTitle"), T("studio.deleteBody", el.Label, SourceText),
                    new[] { T("studio.deleteYes"), T("studio.cancel") }, 1);
                if (choice != 0) return;
                err = _studio.EditSource(el.Src, el.Tag, "delete", SourceTagEditor.Delete);
                AfterWrite(err, T("studio.deleted", el.Label));
                if (err == null) _studio.RunScript("window.__cx && __cx.deselect()");
                break;
            case "duplicate":
                err = _studio.EditSource(el.Src, el.Tag, "duplicate", SourceTagEditor.Duplicate);
                AfterWrite(err, T("studio.duplicated", el.Label));
                break;
            case "moveUp":
                if (el.PrevSrc == null) { Status(T("studio.edit.noSibling"), error: true); return; }
                err = _studio.SwapSiblings(el.PrevSrc, el.Src, "move up");
                AfterWrite(err, T("studio.moved"));
                break;
            case "moveDown":
                if (el.NextSrc == null) { Status(T("studio.edit.noSibling"), error: true); return; }
                err = _studio.SwapSiblings(el.Src, el.NextSrc, "move down");
                AfterWrite(err, T("studio.moved"));
                break;
        }
    }

    /// <summary>Overlay: the user rewrote a text in place (double-click).</summary>
    public void InlineText(StudioElement el, string value)
    {
        if (!(el.HasExactSource && _studio.PageIsLocal))
        {
            Status(T("studio.inlineNotSaved"), error: true);
            return;
        }
        string? err = _studio.EditSource(el.Src, el.Tag, "text", (t, tag, d) => SourceTagEditor.SetText(t, tag, d, value));
        if (err == null) { _textOriginal = _textValue = value; OnPropertyChanged(nameof(TextValue)); }
        AfterWrite(err, T("studio.textSaved"));
    }

    /// <summary>Overlay: the user dragged a resize handle.</summary>
    public void Resized(StudioElement el, string? width, string? height)
    {
        var changes = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(width)) changes["width"] = width;
        if (!string.IsNullOrEmpty(height)) changes["height"] = height;
        if (changes.Count == 0) return;
        if (!(el.HasExactSource && _studio.PageIsLocal)) { Status(T("studio.inlineNotSaved"), error: true); return; }
        string? err = _studio.EditSource(el.Src, el.Tag, "resize", (t, tag, d) => SourceTagEditor.SetStyle(t, tag, d, changes));
        AfterWrite(err, T("studio.resized", width ?? "auto", height ?? "auto"));
    }

    public bool CanUndo => _studio.UndoCount > 0;

    private void Undo()
    {
        string? err = _studio.Undo(out var label);
        if (err != null) Status(T(err), error: true);
        else Status(T("studio.undone", label ?? ""));
        OnPropertyChanged(nameof(CanUndo));
    }

    private void OpenSource()
    {
        if (SourceRef is not { } s) return;
        _ = _editor.OpenAtLineAsync(s.FullPath, s.Line);
        OpenPageRequested?.Invoke("Code");
    }

    // ───────────────────────────── AI ─────────────────────────────

    private string _aiRequest = "";
    public string AiRequest { get => _aiRequest; set => SetProperty(ref _aiRequest, value ?? ""); }

    private void AskAi()
    {
        var el = Selection;
        if (el == null || string.IsNullOrWhiteSpace(AiRequest)) return;
        string prompt = BuildAiPrompt(el, AiRequest.Trim());
        if (!_chat.SendFromStudio(prompt))
        {
            Status(T("studio.ai.busy"), error: true);
            return;
        }
        AiRequest = "";
        Status(T("studio.ai.sent"));
    }

    /// <summary>The element, its exact place in the source and what it looks like now — so the AI edits that
    /// element and nothing else, without hunting for it.</summary>
    internal string BuildAiPrompt(StudioElement el, string request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[UI Studio] Change one element of the UI.");
        sb.AppendLine();
        sb.AppendLine("Request: " + request);
        sb.AppendLine();
        sb.AppendLine($"Element: <{el.Tag}> {el.Label}");
        sb.AppendLine($"CSS selector on the page: {el.Selector}");
        var exact = el.HasExactSource ? _studio.ResolveSource(el.Src) : null;
        var near = exact == null ? _studio.ResolveSource(el.AncestorSrc) : null;
        if (exact != null)
            sb.AppendLine($"Source (exact): {exact.RelativePath} line {exact.Line}, column {exact.Column} — the opening tag of this element.");
        else if (near != null)
            sb.AppendLine($"Source: not located exactly. Its nearest located parent is {near.RelativePath} line {near.Line}; the element is rendered inside it (a loop, a component, or a library).");
        else if (Candidates.Count > 0)
            sb.AppendLine("Source: not located. Likely places: " + string.Join(", ", Candidates.Take(4).Select(c => c.Display)));
        else
            sb.AppendLine("Source: not located — search the project for it.");
        if (!string.IsNullOrEmpty(_studio.PageUrl)) sb.AppendLine("Page: " + _studio.PageUrl);
        sb.AppendLine();
        sb.AppendLine("Current HTML (as rendered):");
        sb.AppendLine("```html");
        sb.AppendLine(el.Html);
        sb.AppendLine("```");
        var styles = new[] { "display", "width", "height", "color", "background-color", "font-size", "font-weight", "padding-top", "padding-left", "margin-top", "border-radius", "gap" }
            .Where(k => el.Styles.ContainsKey(k)).Select(k => $"{k}: {el.Styles[k]}");
        sb.AppendLine("Computed style: " + string.Join("; ", styles));
        sb.AppendLine();
        sb.AppendLine("Edit only what the request needs, in the file that renders this element. Follow the project's existing styling approach (its CSS files, Tailwind classes or component styles). The UI Studio preview reloads by itself when the file changes.");
        return sb.ToString();
    }

    // ───────────────────────────── misc ─────────────────────────────

    private async Task ExportCssAsync()
    {
        if (_studio.Evaluate == null) return;
        string? json = await _studio.Evaluate("window.__cx ? __cx.exportCss() : ''");
        string css = "";
        try { css = json == null ? "" : JsonSerializer.Deserialize<string>(json) ?? ""; } catch { }
        if (css.Length == 0) { Status(T("studio.css.none")); return; }
        if (TryCopy(css)) Status(T("studio.css.copied"));
    }

    private bool TryCopy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch { Status(T("studio.copyFailed"), error: true); return false; }
    }

    private void InstallLocator()
    {
        var p = _studio.Project;
        if (p.Kind != StudioProjectKind.Vite || p.ViteConfigPath == null) return;
        string configName = Path.GetFileName(p.ViteConfigPath);
        if (configName.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            Views.Editor.ChoiceDialog.Show(T("studio.locator.title"), T("studio.locator.manual", configName), new[] { T("studio.ok") });
            return;
        }
        string config;
        try { config = File.ReadAllText(p.ViteConfigPath); }
        catch { Status(T("studio.err.read"), error: true); return; }
        var (patched, error) = StudioProject.PlanLocatorInstall(config);
        if (patched == null)
        {
            Views.Editor.ChoiceDialog.Show(T("studio.locator.title"), T(error ?? "studio.locator.manual", configName), new[] { T("studio.ok") });
            return;
        }
        int choice = Views.Editor.ChoiceDialog.Show(T("studio.locator.title"), T("studio.locator.confirm", configName, StudioProject.LocatorFileName),
            new[] { T("studio.locator.install"), T("studio.cancel") });
        if (choice != 0) return;
        try
        {
            File.WriteAllText(Path.Combine(p.Root, StudioProject.LocatorFileName), StudioTemplates.ReadAsset("cluadex-locator.mjs"), new UTF8Encoding(false));
            if (patched != config)
                CluadeX.Helpers.TextFileCodec.Write(p.ViteConfigPath, patched, CluadeX.Helpers.TextFileCodec.HasBom(p.ViteConfigPath));
        }
        catch (Exception ex)
        {
            _log?.Warn("UiStudio", "locator install failed", ex);
            Status(T("studio.err.write"), error: true);
            return;
        }
        _studio.OpenProject(p.Root);   // re-detect: the banner goes away
        Status(T("studio.locator.done"));
        if (_studio.IsServing) { _studio.StopServing(); _ = StartAsync(); }   // vite.config changes need a restart
    }

    // ───────────────────────────── commands ─────────────────────────────

    public ICommand OpenFolderCommand { get; }
    public ICommand UseCurrentProjectCommand { get; }
    public ICommand ShowGalleryCommand { get; }
    public ICommand CloseGalleryCommand { get; }
    public ICommand PickTemplateCommand { get; }
    public ICommand BrowseParentCommand { get; }
    public ICommand CreateFromTemplateCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand SetDeviceCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand SelectParentCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand OpenSourceCommand { get; }
    public ICommand OpenCandidateCommand { get; }
    public ICommand AskAiCommand { get; }
    public ICommand OpenChatCommand { get; }
    public ICommand CopySelectorCommand { get; }
    public ICommand ExportCssCommand { get; }
    public ICommand InstallLocatorCommand { get; }
    public ICommand OpenGraphCommand { get; }
}
