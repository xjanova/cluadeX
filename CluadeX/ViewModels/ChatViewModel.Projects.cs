using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Naming chats and grouping them into projects (owner 2026-11-01: rename a chat; "เพิ่มระบบโปรเจค ย้ายแชทเข้าโปรเจคได้
/// หรือสร้างใหม่ เพื่อเริ่มแชทในโปรเจคเพื่อง่ายในการค้นหาเชื่อมโยง"). Both are metadata: a rename or a move writes one
/// column (ChatPersistenceService.RenameSession / SetSessionProject) and never the messages — a chat that is not open
/// holds no rows (LoadSessionAsync gives them back), so a full save of it would erase it. A move changes the chat's group
/// only, never its folder: rewind points and memory key off the folder.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The sidebar's projects, oldest first.</summary>
    public ObservableCollection<ChatProject> ChatProjects { get; } = new();

    /// <summary>A line of text from the owner (title, prompt, initial text) → the text, or null = cancelled. A seam like
    /// <see cref="Confirm"/>, so the harness answers without a modal dialog.</summary>
    public Func<string, string, string, string?> AskText { get; set; } = (title, prompt, initial) =>
        CluadeX.Services.Helpers.InputDialog.Show(title, prompt, initialText: initial,
            okLabel: Loc("sidebar.rename.ok"), cancelLabel: Loc("sidebar.rename.cancel"));

    /// <summary>A folder from the owner (initial folder) → the folder, or null = cancelled.</summary>
    public Func<string?, string?> PickFolder { get; set; } = initial =>
        CluadeX.Services.Helpers.FolderPicker.ShowDialog(Loc("sidebar.project.bindFolderTitle"), initial);

    // ─── the filter ───

    private string? _projectFilterId;
    /// <summary>The sidebar shows one project's chats: its id, "" = the chats in no project, null = no project filter.</summary>
    public string? ProjectFilterId
    {
        get => _projectFilterId;
        private set
        {
            if (_projectFilterId == value) return;
            _projectFilterId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProjectFilter));
            OnPropertyChanged(nameof(IsUngroupedSelected));
            OnPropertyChanged(nameof(ProjectFilterName));
            foreach (var p in ChatProjects) p.IsSelected = p.Id == value;
            RebuildVisibleSessions();
            // A search in progress follows the filter.
            if (!string.IsNullOrWhiteSpace(HistorySearchQuery)) PerformHistorySearch(HistorySearchQuery);
        }
    }

    public bool HasProjectFilter => _projectFilterId != null;
    public bool IsUngroupedSelected => _projectFilterId == "";

    /// <summary>The filter's name for the line above the list.</summary>
    public string ProjectFilterName => _projectFilterId == null ? ""
        : _projectFilterId.Length == 0 ? Loc("sidebar.project.none")
        : ChatProjects.FirstOrDefault(p => p.Id == _projectFilterId)?.Name ?? "";

    private int _ungroupedChatCount;
    /// <summary>Chats in no project (the "No project" row's number).</summary>
    public int UngroupedChatCount { get => _ungroupedChatCount; private set => SetProperty(ref _ungroupedChatCount, value); }

    public bool HasChatProjects => ChatProjects.Count > 0;

    /// <summary>The project list is open (persisted).</summary>
    public bool IsProjectsExpanded
    {
        get => _settingsService.Settings.SidebarProjectsExpanded;
        set
        {
            if (value == IsProjectsExpanded) return;
            _settingsService.UpdateSettings(s => s.SidebarProjectsExpanded = value);
            OnPropertyChanged();
            if (value) RefreshProjectFolders();
        }
    }

    /// <summary>Looks at every bound folder again, off the UI thread (a drive that is gone can take seconds to answer).</summary>
    private void RefreshProjectFolders()
    {
        var projects = ChatProjects.ToList();
        _ = Task.Run(() => { foreach (var p in projects) p.RefreshFolderState(); });
    }

    /// <summary>The chat's project, or "" — also for a project that is gone.</summary>
    private string ProjectOf(ChatSession s) =>
        s.ProjectId.Length > 0 && ChatProjects.Any(p => p.Id == s.ProjectId) ? s.ProjectId : "";

    /// <summary>A new chat's project: the one the sidebar shows, else the project bound to its folder, else none.</summary>
    private string DefaultProjectId(string? folder)
    {
        if (!string.IsNullOrEmpty(_projectFilterId) && ChatProjects.Any(p => p.Id == _projectFilterId)) return _projectFilterId;
        return FolderProjectId(folder);
    }

    /// <summary>The project bound to <paramref name="folder"/>, else none — for a chat whose folder is given (a pipe task),
    /// where the project the sidebar happens to show says nothing.</summary>
    private string FolderProjectId(string? folder)
    {
        string key = Models.ChatProject.FolderKey(folder);
        if (key.Length == 0) return "";
        return ChatProjects.FirstOrDefault(p => Models.ChatProject.FolderKey(p.Folder) == key)?.Id ?? "";
    }

    /// <summary>The numbers on the project rows (called by RebuildVisibleSessions).</summary>
    private void UpdateProjectCounts(Func<ChatSession, bool> hidden)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in Sessions)
        {
            if (hidden(s)) continue;
            string pid = ProjectOf(s);
            counts[pid] = counts.GetValueOrDefault(pid) + 1;
        }
        foreach (var p in ChatProjects) p.ChatCount = counts.GetValueOrDefault(p.Id);
        UngroupedChatCount = counts.GetValueOrDefault("");
        OnPropertyChanged(nameof(HasChatProjects));
    }

    /// <summary>Sidebar: show this project's chats ("" = the chats in no project); the same one again shows all.</summary>
    public ICommand SelectProjectFilterCommand => _selectProjectFilterCommand ??= new RelayCommand<string>(id =>
        ProjectFilterId = id == null || id == _projectFilterId ? null
            : id.Length == 0 || ChatProjects.Any(p => p.Id == id) ? id : null);
    private ICommand? _selectProjectFilterCommand;

    public ICommand ClearProjectFilterCommand => _clearProjectFilterCommand ??= new RelayCommand(() => ProjectFilterId = null);
    private ICommand? _clearProjectFilterCommand;

    public ICommand ToggleProjectsExpandedCommand => _toggleProjectsExpandedCommand ??= new RelayCommand(() => IsProjectsExpanded = !IsProjectsExpanded);
    private ICommand? _toggleProjectsExpandedCommand;

    // ─── rename ───

    /// <summary>Every object that stands for chat <paramref name="id"/>: the sidebar's entry, the chat on screen and a run's
    /// chat can be different copies (a chat loaded from the database replaces its entry).</summary>
    private List<ChatSession> CopiesOf(string id)
    {
        var copies = new List<ChatSession>();
        void Add(ChatSession? s) { if (s != null && s.Id == id && !copies.Any(c => ReferenceEquals(c, s))) copies.Add(s); }
        foreach (var s in Sessions) Add(s);
        Add(CurrentSession);
        foreach (var r in _runs) Add(r.Session);
        return copies;
    }

    /// <summary>The entry the sidebar had (renamed or moved while the chat loaded) → the copy just read from the database.</summary>
    private static void CarryMetadata(ChatSession from, ChatSession to)
    {
        if (from.TitleIsCustom) { to.Title = from.Title; to.TitleIsCustom = true; }
        to.ProjectId = from.ProjectId;
    }

    /// <summary>Writes a metadata change (<paramref name="write"/> = false: no stored row took it). A chat whose first save
    /// is still on its way is written first, then changed; a chat not stored at all takes the change with its first save
    /// (the snapshot carries it). False = the store refused it.</summary>
    private bool StoreMetadata(string id, Func<bool> write)
    {
        if (write()) return true;
        _persistenceService.Flush(TimeSpan.FromSeconds(5));
        if (write()) return true;
        return !_persistenceService.SessionHasMessages(id);
    }

    /// <summary>The longest chat name kept (the row trims it anyway; a pasted page is not a name).</summary>
    public const int MaxChatTitleLength = 120;

    /// <summary>A name as typed → one line, trimmed, at most <see cref="MaxChatTitleLength"/>; "" = no name.</summary>
    public static string CleanTitle(string? typed)
    {
        var line = string.Join(" ", (typed ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        while (line.Contains("  ")) line = line.Replace("  ", " ");
        return line.Length > MaxChatTitleLength ? line[..MaxChatTitleLength].TrimEnd() : line;
    }

    /// <summary>Names chat <paramref name="id"/>. Metadata only; an open, running or unopened chat alike. The name stays: no
    /// automatic title replaces it. Returns false when it was refused (empty name, unknown chat, the store failed).</summary>
    public bool RenameChat(string? id, string? typed)
    {
        if (string.IsNullOrEmpty(id) || Sessions.FirstOrDefault(s => s.Id == id) is not { } chat) return false;
        string name = CleanTitle(typed);
        if (name.Length == 0) { StatusText = Loc("sidebar.rename.empty"); return false; }
        if (chat.TitleIsCustom && chat.Title == name) return true;

        var copies = CopiesOf(id);
        var before = copies.Select(c => (c, c.Title, c.TitleIsCustom)).ToList();
        foreach (var c in copies) { c.Title = name; c.TitleIsCustom = true; }
        if (!StoreMetadata(id, () => _persistenceService.RenameSession(id, name)))
        {
            foreach (var (c, title, custom) in before) { c.Title = title; c.TitleIsCustom = custom; }
            StatusText = Loc("sidebar.rename.failed");
            return false;
        }
        StatusText = LocF("sidebar.renamed", name);
        return true;
    }

    private bool _asking;   // a dialog of this file is open: a second click does not open another

    /// <summary>Sidebar: rename a chat (asks for the name).</summary>
    public ICommand RenameChatCommand => _renameChatCommand ??= new RelayCommand<string>(id =>
    {
        if (_asking || string.IsNullOrEmpty(id) || Sessions.FirstOrDefault(s => s.Id == id) is not { } chat) return;
        _asking = true;
        try
        {
            var typed = AskText(Loc("sidebar.rename.title"), Loc("sidebar.rename.prompt"), chat.Title);
            if (typed != null) RenameChat(id, typed);
        }
        finally { _asking = false; }
    });
    private ICommand? _renameChatCommand;

    // ─── move ───

    /// <summary>A run goes on in this chat (or it waits for the owner): it is not moved.</summary>
    private bool IsChatWorking(ChatSession s) => RunOf(s) != null || s.RunStatus is "working" or "waiting";

    /// <summary>Moves chat <paramref name="chatId"/> into project <paramref name="projectId"/> ("" = out of any). Only the
    /// group changes, never the folder. Refused while the chat is working. Returns whether it is in that project now.</summary>
    public bool MoveChatToProject(string? chatId, string? projectId)
    {
        projectId ??= "";
        if (string.IsNullOrEmpty(chatId) || Sessions.FirstOrDefault(s => s.Id == chatId) is not { } chat) return false;
        if (projectId.Length > 0 && ChatProjects.FirstOrDefault(p => p.Id == projectId) is null) return false;
        if (ProjectOf(chat) == projectId) return true;
        if (IsChatWorking(chat))
        {
            StatusText = LocF("sidebar.project.moveRefusedWorking", chat.Title);
            return false;
        }

        var copies = CopiesOf(chatId);
        var before = copies.Select(c => (c, c.ProjectId)).ToList();
        foreach (var c in copies) c.ProjectId = projectId;
        if (!StoreMetadata(chatId, () => _persistenceService.SetSessionProject(chatId, projectId)))
        {
            foreach (var (c, pid) in before) c.ProjectId = pid;
            StatusText = Loc("sidebar.project.moveFailed");
            return false;
        }
        StatusText = projectId.Length == 0
            ? LocF("sidebar.project.movedOut", chat.Title)
            : LocF("sidebar.project.moved", chat.Title, ChatProjects.First(p => p.Id == projectId).Name);
        RebuildVisibleSessions();
        return true;
    }

    // ─── projects ───

    /// <summary>Creates a project named <paramref name="typed"/>, bound to <paramref name="folder"/> (optional). Null = refused.</summary>
    public ChatProject? CreateProject(string? typed, string? folder = null)
    {
        string name = CleanTitle(typed);
        if (name.Length == 0) { StatusText = Loc("sidebar.project.nameEmpty"); return null; }
        var project = new ChatProject { Name = name, Folder = (folder ?? "").Trim() };
        if (!_persistenceService.SaveProject(project)) { StatusText = Loc("sidebar.project.saveFailed"); return null; }
        ChatProjects.Add(project);
        StatusText = LocF("sidebar.project.created", name);
        RebuildVisibleSessions();
        return project;
    }

    /// <summary>Sidebar "+": a new project (asks for its name).</summary>
    public ICommand NewProjectCommand => _newProjectCommand ??= new RelayCommand(() => AskNewProject());
    private ICommand? _newProjectCommand;

    /// <summary>Asks for a name and creates the project; null = cancelled or refused.</summary>
    public ChatProject? AskNewProject()
    {
        if (_asking) return null;
        _asking = true;
        try
        {
            var typed = AskText(Loc("sidebar.project.newTitle"), Loc("sidebar.project.newPrompt"), "");
            return typed == null ? null : CreateProject(typed);
        }
        finally { _asking = false; }
    }

    /// <summary>Gives project <paramref name="projectId"/> a new name. False = refused.</summary>
    public bool RenameProject(string? projectId, string? typed)
    {
        if (ChatProjects.FirstOrDefault(p => p.Id == projectId) is not { } project) return false;
        string name = CleanTitle(typed);
        if (name.Length == 0) { StatusText = Loc("sidebar.project.nameEmpty"); return false; }
        string old = project.Name;
        project.Name = name;
        if (!_persistenceService.SaveProject(project)) { project.Name = old; StatusText = Loc("sidebar.project.saveFailed"); return false; }
        OnPropertyChanged(nameof(ProjectFilterName));
        RebuildVisibleSessions();
        return true;
    }

    public ICommand RenameProjectCommand => _renameProjectCommand ??= new RelayCommand<string>(id =>
    {
        if (_asking || ChatProjects.FirstOrDefault(p => p.Id == id) is not { } project) return;
        _asking = true;
        try
        {
            var typed = AskText(Loc("sidebar.project.renameTitle"), Loc("sidebar.project.newPrompt"), project.Name);
            if (typed != null) RenameProject(id, typed);
        }
        finally { _asking = false; }
    });
    private ICommand? _renameProjectCommand;

    /// <summary>Binds a folder to the project ("" = unbind). The chats in it keep their own folders.</summary>
    public bool SetProjectFolder(string? projectId, string? folder)
    {
        if (ChatProjects.FirstOrDefault(p => p.Id == projectId) is not { } project) return false;
        string old = project.Folder;
        project.Folder = (folder ?? "").Trim();
        if (!_persistenceService.SaveProject(project)) { project.Folder = old; StatusText = Loc("sidebar.project.saveFailed"); return false; }
        StatusText = project.HasFolder ? LocF("sidebar.project.folderBound", project.Name, project.Folder)
                                       : LocF("sidebar.project.folderUnbound", project.Name);
        return true;
    }

    public ICommand BindProjectFolderCommand => _bindProjectFolderCommand ??= new RelayCommand<string>(id =>
    {
        if (_asking || ChatProjects.FirstOrDefault(p => p.Id == id) is not { } project) return;
        _asking = true;
        try
        {
            var folder = PickFolder(project.HasFolder ? project.Folder : HasProject ? WorkingDirectory : null);
            if (!string.IsNullOrWhiteSpace(folder)) SetProjectFolder(id, folder);
        }
        finally { _asking = false; }
    });
    private ICommand? _bindProjectFolderCommand;

    public ICommand UnbindProjectFolderCommand => _unbindProjectFolderCommand ??= new RelayCommand<string>(id => SetProjectFolder(id, ""));
    private ICommand? _unbindProjectFolderCommand;

    /// <summary>Deletes a project after a confirmation. Its chats are kept, in no project. Refused while one of them works
    /// (it would move a working chat). Returns whether it is gone.</summary>
    public bool DeleteProject(string? projectId)
    {
        if (ChatProjects.FirstOrDefault(p => p.Id == projectId) is not { } project) return false;
        var members = Sessions.Where(s => s.ProjectId == project.Id).ToList();
        if (members.FirstOrDefault(IsChatWorking) is { } working)
        {
            StatusText = LocF("sidebar.project.deleteRefusedWorking", project.Name, working.Title);
            return false;
        }
        if (!Confirm(LocF("sidebar.project.deleteConfirm", project.Name, members.Count), Loc("sidebar.project.deleteTitle")))
            return false;
        if (_persistenceService.DeleteProject(project.Id) < 0) { StatusText = Loc("sidebar.project.saveFailed"); return false; }
        foreach (var s in members)
            foreach (var c in CopiesOf(s.Id)) c.ProjectId = "";
        ChatProjects.Remove(project);
        if (_projectFilterId == project.Id) ProjectFilterId = null;
        StatusText = LocF("sidebar.project.deleted", project.Name);
        RebuildVisibleSessions();
        return true;
    }

    public ICommand DeleteProjectCommand => _deleteProjectCommand ??= new RelayCommand<string>(id => DeleteProject(id));
    private ICommand? _deleteProjectCommand;

    /// <summary>A new chat in the project: in the group, and opened in the project's folder when it has one that exists
    /// (a folder that is gone is said in the status bar; the chat still starts, with no folder).</summary>
    public bool NewChatInProject(string? projectId)
    {
        if (ChatProjects.FirstOrDefault(p => p.Id == projectId) is not { } project) return false;
        project.RefreshFolderState();
        CancelPendingChatLoad();
        NewSession(keepProject: false);
        if (CurrentSession is not { } chat) return false;
        chat.ProjectId = project.Id;
        if (project.HasFolder && Directory.Exists(project.Folder))
        {
            ApplyWorkingDirectory(project.Folder, announce: false, showEditor: false);
            chat.ProjectPath = project.Folder;
            StatusText = LocF("sidebar.project.newChatIn", project.Name);
        }
        else
            StatusText = project.HasFolder ? LocF("sidebar.project.folderMissing", project.Name, project.Folder)
                                           : LocF("sidebar.project.newChatIn", project.Name);
        RebuildVisibleSessions();
        NavigateToChatRequested?.Invoke();
        return true;
    }

    public ICommand NewChatInProjectCommand => _newChatInProjectCommand ??= new RelayCommand<string>(id => NewChatInProject(id));
    private ICommand? _newChatInProjectCommand;

    // ─── search ───

    /// <summary>The search's results: a chat whose REFERENCE was typed or pasted ("CX-8cf58c2a", with or without "CX-")
    /// comes first, then chats whose NAME (or project's name) matches — a chat renamed to find it again is found by that
    /// name — then the message matches. With a project picked, only that project's chats (a reference hit is kept: the
    /// owner asked for that one chat).</summary>
    private List<SearchResult> WithTitleHits(string query, List<SearchResult> messageHits)
    {
        string q = query.Trim();
        var names = ChatProjects.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var list = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var refHits = new HashSet<string>(StringComparer.Ordinal);
        if (ChatRef.TryParse(q, out string wanted))
            foreach (var s in Sessions)
                if (ChatRef.Of(s.Id) == wanted && seen.Add(s.Id))
                {
                    refHits.Add(s.Id);
                    list.Add(new SearchResult
                    {
                        SessionId = s.Id, SessionTitle = s.Title, UpdatedAt = s.UpdatedAt, MatchCount = 1,
                        Snippet = LocF("sidebar.search.refHit", wanted),
                    });
                }
        foreach (var s in SidebarOrder(Sessions))
        {
            string project = names.GetValueOrDefault(ProjectOf(s)) ?? "";
            bool title = s.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!title && !(project.Length > 0 && project.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            if (!seen.Add(s.Id)) continue;
            list.Add(new SearchResult
            {
                SessionId = s.Id, SessionTitle = s.Title, UpdatedAt = s.UpdatedAt, MatchCount = 1,
                Snippet = project.Length > 0 ? LocF("sidebar.search.inProject", project) : Loc("sidebar.search.titleHit"),
            });
        }
        foreach (var r in messageHits)
            if (seen.Add(r.SessionId))
            {
                // The stored title can be older than the sidebar's (a rename while the search ran).
                if (Sessions.FirstOrDefault(s => s.Id == r.SessionId) is { } s) r.SessionTitle = s.Title;
                list.Add(r);
            }
        if (_projectFilterId != null)
            list = list.Where(r => refHits.Contains(r.SessionId)
                || Sessions.FirstOrDefault(s => s.Id == r.SessionId) is { } s && ProjectOf(s) == _projectFilterId).ToList();
        return list;
    }
}
