namespace CluadeX.Models;

/// <summary>
/// A project in the sidebar: a named group of chats (owner 2026-11-01: "เพิ่มระบบโปรเจค ย้ายแชทเข้าโปรเจคได้หรือสร้างใหม่
/// เพื่อเริ่มแชทในโปรเจคเพื่อง่ายในการค้นหาเชื่อมโยง"). A folder can be bound to it — a chat started in the project then
/// opens there — but it does not have to be. Stored in the chat database (table <c>projects</c>); a chat names its project
/// in <see cref="ChatSession.ProjectId"/>.
/// </summary>
public sealed class ChatProject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value ?? ""; Raise(nameof(Name)); } }
    }

    private string _folder = "";
    /// <summary>The folder bound to it ("" = none).</summary>
    public string Folder
    {
        get => _folder;
        set { if (_folder != value) { _folder = value ?? ""; Raise(nameof(Folder)); Raise(nameof(HasFolder)); RefreshFolderState(); } }
    }

    public bool HasFolder => _folder.Length > 0;

    private bool _folderMissing;
    /// <summary>A folder is bound but was not there when last looked at (moved, deleted, a drive not plugged in). Kept, not
    /// asked of the disk on every redraw: a drive that is gone can take seconds to answer.</summary>
    public bool FolderMissing => _folderMissing;

    /// <summary>Looks at the disk again for <see cref="FolderMissing"/>.</summary>
    public void RefreshFolderState()
    {
        bool missing = _folder.Length > 0 && !System.IO.Directory.Exists(_folder);
        if (missing != _folderMissing) { _folderMissing = missing; Raise(nameof(FolderMissing)); }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    private int _chatCount;
    /// <summary>Chats in it (set by the sidebar).</summary>
    public int ChatCount
    {
        get => _chatCount;
        set { if (_chatCount != value) { _chatCount = value; Raise(nameof(ChatCount)); } }
    }

    private bool _isSelected;
    /// <summary>The sidebar shows only this project's chats.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    /// <summary>The key two spellings of one folder share (no trailing slash, any case).</summary>
    public static string FolderKey(string? path) => (path ?? "").Trim().TrimEnd('\\', '/').ToUpperInvariant();
}
