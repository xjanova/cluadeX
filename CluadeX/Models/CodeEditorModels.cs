using System.Collections.ObjectModel;
using System.IO;
using CluadeX.Services;

namespace CluadeX.Models;

/// <summary>
/// A single open file in the code editor (one tab in the editor pane).
/// </summary>
public class OpenFileTab : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; set; } = "";

    /// <summary>The file had a UTF-8 BOM when opened — Save writes one back, and only then.</summary>
    public bool HasBom { get; set; }

    /// <summary>False when the bytes on disk were not UTF-8 (a legacy encoding). The tab then shows
    /// replacement characters, and saving it would write them over the real text — so Save refuses.</summary>
    public bool IsValidUtf8 { get; set; } = true;

    public string FileName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath).TrimStart('.').ToLowerInvariant();

    /// <summary>True when this tab is the one shown in the editor. The tab strip binds its
    /// highlight to this; it used to bind to a non-existent Border.IsSelected, so the active
    /// tab was never highlighted.</summary>
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Raise(nameof(IsActive)); } }
    }

    private string _content = "";
    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                Raise(nameof(Content));
                Raise(nameof(LineCount));
                Raise(nameof(LineNumbers));
                if (!_isDirty && _originalContent != value)
                {
                    IsDirty = true;
                }
            }
        }
    }

    /// <summary>Set by the editor while it may hold typing not yet copied into <see cref="Content"/> (large files
    /// sync after a pause). Anything about to read Content for keeps — Save — calls <see cref="FlushPendingEdits"/>.</summary>
    public Action? PendingEdits { get; set; }
    public void FlushPendingEdits() => PendingEdits?.Invoke();

    private string _originalContent = "";
    public void MarkOpened(string content)
    {
        _originalContent = content;
        _content = content;
        _isDirty = false;
        Raise(nameof(Content));
        Raise(nameof(IsDirty));
        Raise(nameof(TabTitle));
        Raise(nameof(LineCount));
        Raise(nameof(LineNumbers));
    }
    public void MarkSaved()
    {
        _originalContent = _content;
        IsDirty = false;
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set { if (_isDirty != value) { _isDirty = value; Raise(nameof(IsDirty)); Raise(nameof(TabTitle)); } }
    }

    public string TabTitle => _isDirty ? "● " + FileName : FileName;

    public int LineCount
    {
        get
        {
            if (string.IsNullOrEmpty(_content)) return 1;
            int n = 1;
            for (int i = 0; i < _content.Length; i++) if (_content[i] == '\n') n++;
            return n;
        }
    }

    /// <summary>Newline-joined "1\n2\n3..." for the gutter.</summary>
    public string LineNumbers
    {
        get
        {
            int n = LineCount;
            var sb = new System.Text.StringBuilder(n * 4);
            for (int i = 1; i <= n; i++)
            {
                sb.Append(i);
                if (i < n) sb.Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>Color hint for the extension monogram (cyan for .tsx, amber for .json etc).</summary>
    public string ExtensionColorHex
    {
        get
        {
            return Extension switch
            {
                "tsx" or "ts" or "js" or "jsx" => ThemeColors.Hex("Accent.1", "#4CDFFF"),
                "cs" => ThemeColors.Hex("Accent.2", "#A672FF"),
                "json" => ThemeColors.Hex("Status.Warning", "#FFD166"),
                "md" => ThemeColors.Hex("Status.Success", "#5CFFB0"),
                "css" or "scss" => ThemeColors.Hex("Accent.3", "#FF5EC4"),
                "html" or "xml" or "xaml" => ThemeColors.Hex("Accent.4", "#FF8AA6"),
                "py" => ThemeColors.Hex("Status.Info", "#5A8DFF"),
                "rs" => ThemeColors.Hex("Status.Danger", "#FF6E6E"),
                "go" => ThemeColors.Hex("Accent.1", "#4CDFFF"),
                "yaml" or "yml" => ThemeColors.Hex("Status.Warning", "#FFD166"),
                _ => ThemeColors.Hex("Text.Muted", "#8388BD"),
            };
        }
    }

    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}

/// <summary>
/// One node of the file-tree (folder or file). Folders can be expanded
/// to lazy-load their children — keeps the initial render cheap.
/// </summary>
public class FileTreeNode : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }

    /// <summary>"M" / "U" / "A" / "?" — set by external git scan.</summary>
    public string GitBadge { get; set; } = "";

    /// <summary>Colour of the badge — a theme token picked from <see cref="GitBadge"/> (modified = warning, added /
    /// untracked = success, deleted = danger, renamed = accent 2), read in the theme on screen.</summary>
    public string GitBadgeColorHex => GitBadge switch
    {
        "M" => ThemeColors.Hex("Status.Warning", "#FFD166"),
        "A" or "U" => ThemeColors.Hex("Status.Success", "#5CFFB0"),
        "D" => ThemeColors.Hex("Status.Danger", "#FF6E6E"),
        "R" => ThemeColors.Hex("Accent.2", "#A672FF"),
        _ => ThemeColors.Hex("Text.Muted", "#8388BD"),
    };

    public ObservableCollection<FileTreeNode> Children { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();

    public string ExtensionColorHex
    {
        get
        {
            return Extension switch
            {
                "tsx" or "ts" or "js" or "jsx" => ThemeColors.Hex("Accent.1", "#4CDFFF"),
                "cs" => ThemeColors.Hex("Accent.2", "#A672FF"),
                "json" => ThemeColors.Hex("Status.Warning", "#FFD166"),
                "md" => ThemeColors.Hex("Status.Success", "#5CFFB0"),
                "css" or "scss" => ThemeColors.Hex("Accent.3", "#FF5EC4"),
                "html" or "xml" or "xaml" => ThemeColors.Hex("Accent.4", "#FF8AA6"),
                "py" => ThemeColors.Hex("Status.Info", "#5A8DFF"),
                "rs" => ThemeColors.Hex("Status.Danger", "#FF6E6E"),
                "go" => ThemeColors.Hex("Accent.1", "#4CDFFF"),
                "yaml" or "yml" => ThemeColors.Hex("Status.Warning", "#FFD166"),
                _ => ThemeColors.Hex("Text.Muted", "#8388BD"),
            };
        }
    }

    /// <summary>"tsx" / "cs" / "json" — the monogram shown next to the file name.</summary>
    public string ExtensionLabel => string.IsNullOrEmpty(Extension) ? "?" : Extension;

    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}
