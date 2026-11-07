using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Data.Sqlite;

namespace ChatTest;

/// <summary>
/// PJ — naming a chat and grouping chats into projects (owner 2026-11-01). Both are metadata: a rename or a move writes one
/// column and never the messages; the chat database migrates to v3 (projects table, project_id, title_custom) safely
/// twice and imports the chats' folders as projects. Proven on the shipping DatabaseService (files of its own) and the
/// shipping ChatViewModel (this harness's DI graph and its chat database).
///
///   CHATTEST_ONLY=PROJ dotnet run      — just these checks
/// </summary>
internal static partial class Program
{
    static string PjRoot => Path.Combine(AppContext.BaseDirectory, "pj-data");

    static long ScalarOf(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string ProjectIdOf(string db, string sessionId)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", sessionId);
        return cmd.ExecuteScalar() as string ?? "(none)";
    }

    /// <summary>A chat database as an older build left it: v1 (no project_path) or v2 (with it).</summary>
    static void OldDatabase(string db, int version)
    {
        Exec(db, $@"
            CREATE TABLE sessions (id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT 'New Chat', created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL{(version >= 2 ? ", project_path TEXT NOT NULL DEFAULT ''" : "")});
            CREATE TABLE messages (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, role INTEGER NOT NULL, content TEXT NOT NULL DEFAULT '',
                timestamp TEXT NOT NULL, tool_name TEXT, tool_summary TEXT, tool_output TEXT, tool_success INTEGER NOT NULL DEFAULT 0,
                has_error INTEGER NOT NULL DEFAULT 0, sort_order INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            PRAGMA user_version = {version};");
    }

    static void OldChat(string db, string id, string title, string? folder, int messages)
    {
        string now = DateTime.Now.ToString("o");
        Exec(db, folder == null
            ? $"INSERT INTO sessions (id, title, created_at, updated_at) VALUES ('{id}', '{title}', '{now}', '{now}');"
            : $"INSERT INTO sessions (id, title, created_at, updated_at, project_path) VALUES ('{id}', '{title}', '{now}', '{now}', '{folder.Replace("'", "''")}');");
        for (int i = 0; i < messages; i++)
            Exec(db, $"INSERT INTO messages (id, session_id, role, content, timestamp, sort_order) VALUES ('{id}-m{i}', '{id}', {(i % 2 == 0 ? (int)MessageRole.User : (int)MessageRole.Assistant)}, 'message {i} of {title}', '{now}', {i});");
    }

    static async Task TestRenameAndProjects()
    {
        Log("\n## PJ — rename a chat, projects that group chats (metadata only; migration v3 twice; import; move; refusal)");
        TestProjectMigration();
        await TestRenameChat();
        await TestProjectsInSidebar();
        StaticSidebarXaml();
    }

    // ─── migration ───

    static void TestProjectMigration()
    {
        Directory.CreateDirectory(PjRoot);
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(PjRoot)) File.Delete(f);

        // PJ1 — a v2 file from the field: chats in two folders named "Alpha" (one spelt two ways), one with no folder, one
        // with an old pipe tag that is not a folder.
        string v2 = Path.Combine(PjRoot, "v2.db");
        OldDatabase(v2, 2);
        OldChat(v2, "a1", "alpha one", @"D:\Code\Alpha", 4);
        OldChat(v2, "a2", "alpha two", @"d:\code\alpha\", 2);
        OldChat(v2, "x1", "other alpha", @"E:\Work\Alpha", 2);
        OldChat(v2, "n1", "no folder", "", 2);
        OldChat(v2, "t1", "pipe tag", "obsidianx-task::abc", 2);
        long messagesBefore = ScalarOf(v2, "SELECT COUNT(*) FROM messages;");

        var db = new DatabaseService(v2);
        var projects = db.LoadProjects();
        var alpha = projects.FirstOrDefault(p => ChatProject.FolderKey(p.Folder) == ChatProject.FolderKey(@"D:\Code\Alpha"));
        var other = projects.FirstOrDefault(p => ChatProject.FolderKey(p.Folder) == ChatProject.FolderKey(@"E:\Work\Alpha"));
        Check("PJ1 a v2 database migrates to v3: user_version 3, the projects table and both new columns",
            ScalarOf(v2, "PRAGMA user_version;") == 3
            && ScalarOf(v2, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name IN ('project_id','title_custom');") == 2
            && ScalarOf(v2, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='projects';") == 1);
        Check("PJ1 the folders become projects: one per folder (two spellings of D:\\Code\\Alpha are one), named after it, a second 'Alpha' says where it is",
            projects.Count == 2 && alpha?.Name == "Alpha" && other?.Name == "Alpha (Work)",
            string.Join(" | ", projects.Select(p => $"{p.Name}={p.Folder}")));
        Check("PJ1 the chats go into their folder's project; no folder and a pipe tag stay in none",
            alpha != null && other != null && ProjectIdOf(v2, "a1") == alpha.Id && ProjectIdOf(v2, "a2") == alpha.Id
            && ProjectIdOf(v2, "x1") == other.Id && ProjectIdOf(v2, "n1") == "" && ProjectIdOf(v2, "t1") == "",
            $"a1={ProjectIdOf(v2, "a1")} a2={ProjectIdOf(v2, "a2")} x1={ProjectIdOf(v2, "x1")} n1={ProjectIdOf(v2, "n1")} t1={ProjectIdOf(v2, "t1")}");
        Check("PJ1 the migration touches no message and no folder",
            ScalarOf(v2, "SELECT COUNT(*) FROM messages;") == messagesBefore
            && ScalarOf(v2, "SELECT COUNT(*) FROM sessions WHERE id='a2' AND project_path='d:\\code\\alpha\\';") == 1);

        // PJ2 — the same migration again (user_version put back to 2, as a failed import leaves it): no error, nothing doubled,
        // a chat the owner moved stays where they put it, a name they gave stays.
        db.SetSessionProject("a1", other!.Id);
        db.RenameSession("a2", "my name");
        Exec(v2, "PRAGMA user_version = 2;");
        SqliteConnection.ClearAllPools();
        Exception? again = null;
        try { db = new DatabaseService(v2); } catch (Exception ex) { again = ex; }
        var projects2 = db.LoadProjects();
        Check("PJ2 the v3 migration runs twice safely: no error, still 2 projects, version 3",
            again == null && projects2.Count == 2 && ScalarOf(v2, "PRAGMA user_version;") == 3, again?.Message ?? string.Join(" | ", projects2.Select(p => p.Name)));
        Check("PJ2 …and the second run keeps a moved chat in its new project and a given name",
            ProjectIdOf(v2, "a1") == other.Id && db.LoadSessionList().First(s => s.Id == "a2") is { Title: "my name", TitleIsCustom: true });

        // PJ3 — a v1 file (before project_path) goes through v2 and v3; a fresh file is created at v3.
        string v1 = Path.Combine(PjRoot, "v1.db");
        OldDatabase(v1, 1);
        OldChat(v1, "o1", "very old", null, 2);
        new DatabaseService(v1);
        string fresh = Path.Combine(PjRoot, "fresh.db");
        var freshDb = new DatabaseService(fresh);
        Check("PJ3 a v1 file migrates through v2 to v3 (its chat kept, in no project); a fresh file starts at v3 with no projects",
            ScalarOf(v1, "PRAGMA user_version;") == 3 && ProjectIdOf(v1, "o1") == "" && ScalarOf(v1, "SELECT COUNT(*) FROM messages;") == 2
            && ScalarOf(fresh, "PRAGMA user_version;") == 3 && freshDb.LoadProjects().Count == 0
            && ScalarOf(fresh, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name IN ('project_id','title_custom');") == 2);

        // PJ4 — a save written after a rename or a move, from a snapshot taken before it, does not put the old values back.
        var chat = new ChatSession { Title = "auto title" };
        chat.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "hello" });
        freshDb.SaveSession(chat);
        var stale = DatabaseService.Snapshot(chat);
        var proj = new ChatProject { Name = "P" };
        freshDb.SaveProject(proj);
        freshDb.RenameSession(chat.Id, "owner's name");
        freshDb.SetSessionProject(chat.Id, proj.Id);
        freshDb.WriteSession(stale);
        var back = freshDb.LoadSession(chat.Id);
        Check("PJ4 an older snapshot written after a rename and a move keeps the owner's name and the new project (and the messages)",
            back is { Title: "owner's name", TitleIsCustom: true } && back.ProjectId == proj.Id && back.Messages.Count == 1, $"{back?.Title} / {back?.ProjectId}");
        Check("PJ4 the pipe-prefix cleanup at start never strips a name the owner gave",
            freshDb.RenameSession(chat.Id, "[ObsidianX] keep me") && new DatabaseService(fresh).LoadSession(chat.Id)?.Title == "[ObsidianX] keep me");
        Check("PJ4 deleting a project keeps its chats (they leave it)",
            freshDb.DeleteProject(proj.Id) == 1 && freshDb.LoadSession(chat.Id) is { ProjectId: "" } kept && kept.Messages.Count == 1 && freshDb.LoadProjects().Count == 0);
        SqliteConnection.ClearAllPools();
    }

    // ─── rename ───

    static async Task TestRenameChat()
    {
        var asks = new List<string>();
        string? answer = null;
        var oldAsk = Vm.AskText;
        Vm.AskText = (title, prompt, initial) => { asks.Add(initial); return answer; };
        try
        {
            // PJ5 — a chat that is not open (its rows released) is renamed: name stored, every message still there.
            var stored = ChatInDb("pj5 stored chat", "the question about the deploy", "the answer");
            await FreshChat();
            var entry = Vm.Sessions.First(s => s.Id == stored.Id);
            bool ok = Vm.RenameChat(stored.Id, "  Deploy\nnotes  ");
            var back = Persist.LoadSession(stored.Id);
            Check("PJ5 renaming a chat that isn't open stores the name (one line, trimmed) and the sidebar row shows it",
                ok && entry.Title == "Deploy notes" && entry.TitleIsCustom && back?.Title == "Deploy notes", $"{entry.Title} / {back?.Title}");
            Check("PJ5 …and every one of its messages is still in the database (no full save of an empty chat)",
                back != null && back.Messages.Count == 2 && back.Messages[0].Content == "the question about the deploy" && entry.Messages.Count == 0,
                $"{back?.Messages.Count} rows stored, {entry.Messages.Count} in memory");

            // PJ6 — it opens with that name, and a save after more work keeps it.
            await Open(stored.Id);
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "one more thing" });
            Vm.SaveNow();
            back = Persist.LoadSession(stored.Id);
            Check("PJ6 the renamed chat opens with its name; a save after more messages keeps it and all 3 messages",
                Vm.CurrentSession?.Title == "Deploy notes" && back?.Title == "Deploy notes" && back.Messages.Count == 3, $"{Vm.CurrentSession?.Title} / {back?.Title} / {back?.Messages.Count}");

            // PJ7 — a new chat named before its first message: the automatic title never replaces the name.
            await FreshChat();
            var fresh = Vm.CurrentSession!;
            Check("PJ7 a chat not saved yet can be named", Vm.RenameChat(fresh.Id, "Named first") && fresh.Title == "Named first");
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "this would have been the automatic title" });
            Vm.SaveNow();
            back = Persist.LoadSession(fresh.Id);
            Check("PJ7 …its first save keeps the name (no automatic title), stored as the owner's",
                fresh.Title == "Named first" && back is { Title: "Named first", TitleIsCustom: true }, $"{fresh.Title} / {back?.Title}");

            // PJ8 — the open chat renamed: the sidebar entry and the chat on screen are the same name.
            Check("PJ8 renaming the chat on screen renames its sidebar row too",
                Vm.RenameChat(fresh.Id, "Renamed while open") && Vm.Sessions.First(s => s.Id == fresh.Id).Title == "Renamed while open"
                && Vm.CurrentSession!.Title == "Renamed while open" && Persist.LoadSession(fresh.Id)?.Title == "Renamed while open");

            // PJ9 — an empty name is refused with the localized reason; the chat keeps its name.
            Check("PJ9 an empty or blank name is refused (localized status), the chat keeps its name",
                !Vm.RenameChat(fresh.Id, "   \n ") && fresh.Title == "Renamed while open" && Vm.StatusText == L("sidebar.rename.empty"), Vm.StatusText);

            // PJ10 — the command asks with the current name; cancel changes nothing; a second press while it asks opens no second dialog.
            answer = null;
            Vm.RenameChatCommand.Execute(fresh.Id);
            Check("PJ10 Rename asks with the current name; Cancel changes nothing",
                asks.LastOrDefault() == "Renamed while open" && fresh.Title == "Renamed while open");
            asks.Clear();
            Vm.AskText = (title, prompt, initial) => { asks.Add(initial); Vm.RenameChatCommand.Execute(fresh.Id); return "From the dialog"; };
            Vm.RenameChatCommand.Execute(fresh.Id);
            Check("PJ10 a double press opens ONE dialog, and its answer renames the chat",
                asks.Count == 1 && fresh.Title == "From the dialog" && Persist.LoadSession(fresh.Id)?.Title == "From the dialog", $"{asks.Count} dialogs, {fresh.Title}");

            // PJ11 — the right-click menu has Rename, and its click goes through the same command.
            Vm.AskText = (title, prompt, initial) => "From the menu";
            var menu = CluadeX.Views.SidebarMenus.ForChat(Vm, fresh);
            var renameItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string)i.Header == L("sidebar.rename"));
            renameItem?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, renameItem));
            Check("PJ11 the chat row's right-click menu holds Rename, and it renames", renameItem != null && fresh.Title == "From the menu",
                string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header)));
        }
        finally { Vm.AskText = oldAsk; }
    }

    // ─── projects ───

    static async Task TestProjectsInSidebar()
    {
        ConfirmAnswer = true;
        string folder = Path.Combine(PjRoot, "alpha-folder");
        Directory.CreateDirectory(folder);
        foreach (var p in Vm.ChatProjects.ToList()) Vm.DeleteProject(p.Id);
        await FreshChat();

        // PJ12 — creating: a name is needed; the project is stored.
        Check("PJ12 a project needs a name (refused, localized)", Vm.CreateProject("  ") == null && Vm.StatusText == L("sidebar.project.nameEmpty"));
        var alpha = Vm.CreateProject("Alpha", folder)!;
        var beta = Vm.CreateProject("Beta")!;
        Check("PJ12 created projects are in the list and in the database",
            Vm.ChatProjects.Contains(alpha) && Vm.ChatProjects.Contains(beta) && Db.LoadProjects().Count(p => p.Id == alpha.Id || p.Id == beta.Id) == 2);

        // PJ13 — move a chat that is not open: the group changes; folder, time and messages stay.
        var c1 = ChatInDb("pj13 chat in a folder", "q1", "a1");
        var entry = Vm.Sessions.First(s => s.Id == c1.Id);
        entry.ProjectPath = @"Z:\some\folder";
        Exec(Path.Combine(Settings.DataRoot, "codex.db"), $"UPDATE sessions SET project_path = 'Z:\\some\\folder' WHERE id = '{c1.Id}';");
        var updatedBefore = Db.LoadSessionList().First(s => s.Id == c1.Id).UpdatedAt;
        bool moved = Vm.MoveChatToProject(c1.Id, alpha.Id);
        var row = Db.LoadSessionList().First(s => s.Id == c1.Id);
        Check("PJ13 moving a chat that isn't open puts it in the project (memory and database)",
            moved && entry.ProjectId == alpha.Id && row.ProjectId == alpha.Id, $"{entry.ProjectId} / {row.ProjectId}");
        Check("PJ13 …its folder, its time and its messages stay as they were",
            row.ProjectPath == @"Z:\some\folder" && entry.ProjectPath == @"Z:\some\folder" && row.UpdatedAt == updatedBefore
            && Persist.LoadSession(c1.Id)?.Messages.Count == 2, $"{row.ProjectPath} {row.UpdatedAt:o} vs {updatedBefore:o}");
        Check("PJ13 the project row counts it", alpha.ChatCount >= 1, alpha.ChatCount.ToString());

        // PJ14 — the filter: only the project's chats (pinned ones stay on top); again = all; "No project".
        var outsider = ChatInDb("pj14 outsider", "q", "a");
        var pinned = ChatInDb("pj14 pinned outsider", "q", "a");
        Vm.TogglePinCommand.Execute(pinned.Id);
        Vm.SelectProjectFilterCommand.Execute(alpha.Id);
        await Render();
        Check("PJ14 picking a project shows its chats and not others; a pinned chat stays first",
            Vm.ProjectFilterId == alpha.Id && alpha.IsSelected && Vm.VisibleSessions.Any(s => s.Id == c1.Id)
            && !Vm.VisibleSessions.Any(s => s.Id == outsider.Id) && Vm.VisibleSessions.FirstOrDefault()?.Id == pinned.Id,
            string.Join(" | ", Vm.VisibleSessions.Select(s => s.Title)));
        Check("PJ14 the line above the list names the project", Vm.SidebarFilterText.Contains("Alpha"), Vm.SidebarFilterText);
        Vm.SelectProjectFilterCommand.Execute("");
        await Render();
        Check("PJ14 'No project' shows the chats in none (not the moved one)",
            Vm.IsUngroupedSelected && Vm.VisibleSessions.Any(s => s.Id == outsider.Id) && !Vm.VisibleSessions.Any(s => s.Id == c1.Id));
        Vm.SelectProjectFilterCommand.Execute("");
        Check("PJ14 picking it again shows every chat", Vm.ProjectFilterId == null && !alpha.IsSelected);
        Vm.TogglePinCommand.Execute(pinned.Id);

        // PJ15 — search: by the chat's name and by the project's name; a picked project limits it.
        Vm.RenameChat(c1.Id, "Zebra release plan");
        Vm.HistorySearchQuery = "Zebra";
        await WaitUntil(() => Vm.IsSearchingHistory && Vm.SearchResults.Any(r => r.SessionId == c1.Id), 5000);
        Check("PJ15 search finds a chat by the name it was given", Vm.SearchResults.Any(r => r.SessionId == c1.Id));
        Vm.HistorySearchQuery = "Alpha";
        await WaitUntil(() => Vm.SearchResults.Any(r => r.SessionId == c1.Id), 5000);
        Check("PJ15 …and by its project's name", Vm.SearchResults.Any(r => r.SessionId == c1.Id && r.Snippet.Contains("Alpha")));
        Vm.SelectProjectFilterCommand.Execute(beta.Id);
        Vm.HistorySearchQuery = "";
        Vm.HistorySearchQuery = "Zebra";
        await Task.Delay(700);
        await Render();
        Check("PJ15 with another project picked, the search leaves it out", !Vm.SearchResults.Any(r => r.SessionId == c1.Id),
            string.Join(" | ", Vm.SearchResults.Select(r => r.SessionTitle)));
        Vm.HistorySearchQuery = "";
        Vm.ClearProjectFilterCommand.Execute(null);

        // PJ16 — a snapshot of the open chat taken before a move, written after it, does not move it back.
        await Open(c1.Id);
        var before = DatabaseService.Snapshot(Vm.CurrentSession!);
        Check("PJ16 the open chat moves to another project", Vm.MoveChatToProject(c1.Id, beta.Id) && Vm.CurrentSession!.ProjectId == beta.Id);
        Db.WriteSession(before);
        Vm.SaveNow();
        Check("PJ16 …an older save landing after the move and the next save keep it there",
            Db.LoadSessionList().First(s => s.Id == c1.Id).ProjectId == beta.Id && Persist.LoadSession(c1.Id)?.Messages.Count == 2);

        // PJ17 — out of the project; the menu's Move items work.
        Check("PJ17 a chat moved out is in no project", Vm.MoveChatToProject(c1.Id, "") && Db.LoadSessionList().First(s => s.Id == c1.Id).ProjectId == "");
        var menu = CluadeX.Views.SidebarMenus.ForChat(Vm, Vm.Sessions.First(s => s.Id == c1.Id));
        var moveTo = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string)i.Header == L("sidebar.project.moveTo"));
        var toAlpha = moveTo?.Items.OfType<MenuItem>().FirstOrDefault(i => (string)i.Header == "Alpha");
        toAlpha?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, toAlpha));
        Check("PJ17 right-click ▸ Move to project ▸ Alpha moves it", toAlpha != null && Vm.Sessions.First(s => s.Id == c1.Id).ProjectId == alpha.Id);

        // PJ18 — a working chat is not moved (the run's own saves would carry on in the old group).
        await FreshChat();
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Hang());
        await Send("pj18 keep working");
        bool running = await WaitUntil(() => Vm.IsGenerating, 10000);
        var workingChat = Vm.CurrentSession!;
        string workingId = workingChat.Id;
        bool refused = !Vm.MoveChatToProject(workingId, alpha.Id);
        Check("PJ18 a chat that is working is not moved: refused, said in the status bar, still in no project",
            running && refused && workingChat.ProjectId == "" && Vm.StatusText == LocF("sidebar.project.moveRefusedWorking", workingChat.Title),
            $"running={running} refused={refused} status={Vm.StatusText}");
        workingChat.ProjectId = alpha.Id;   // as if it had been in the project when it started
        bool keptProject = !Vm.DeleteProject(alpha.Id) && Vm.ChatProjects.Contains(alpha)
                           && Vm.StatusText == LocF("sidebar.project.deleteRefusedWorking", "Alpha", workingChat.Title);
        workingChat.ProjectId = "";
        Check("PJ18 …and a project holding a working chat is not deleted (that would move it)", keptProject, Vm.StatusText);
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle();
        Check("PJ18 once it has stopped, it moves", Vm.MoveChatToProject(workingId, alpha.Id));

        // PJ19 — a new chat in a project: in the group, opened in the project's folder; a folder that is gone is said.
        Check("PJ19 'New chat in this project' starts a chat in it, in the project's folder",
            Vm.NewChatInProject(alpha.Id) && Vm.CurrentSession!.ProjectId == alpha.Id && SamePath(Vm.CurrentSession.ProjectPath, folder) && SamePath(Vm.WorkingDirectory, folder),
            $"{Vm.CurrentSession?.ProjectId} {Vm.CurrentSession?.ProjectPath} {Vm.WorkingDirectory}");
        var gone = Vm.CreateProject("Gone", Path.Combine(PjRoot, "no-such-folder"))!;
        Check("PJ19 a project whose folder is gone shows the warning, and its new chat starts with no folder (status says so)",
            gone.FolderMissing && Vm.NewChatInProject(gone.Id) && Vm.CurrentSession!.ProjectId == gone.Id && Vm.CurrentSession.ProjectPath == ""
            && Vm.StatusText == LocF("sidebar.project.folderMissing", "Gone", gone.Folder), Vm.StatusText);
        Vm.SelectProjectFilterCommand.Execute(beta.Id);
        await FreshChat();
        Check("PJ19 with a project picked, the + New chat joins it", Vm.CurrentSession!.ProjectId == beta.Id);
        Vm.ClearProjectFilterCommand.Execute(null);
        Vm.SetWorkingDirectory(folder);
        Vm.NewSession();
        Check("PJ19 a new chat in a folder bound to a project joins that project", Vm.CurrentSession!.ProjectId == alpha.Id);

        // PJ20 — delete: asks; No keeps it; Yes keeps the chats, ungrouped.
        ConfirmAnswer = false;
        Check("PJ20 deleting a project asks first; No keeps it", !Vm.DeleteProject(alpha.Id) && Vm.ChatProjects.Contains(alpha));
        ConfirmAnswer = true;
        var members = Vm.Sessions.Where(s => s.ProjectId == alpha.Id).Select(s => s.Id).ToList();
        Check("PJ20 Yes deletes it; its chats are kept, in no project (memory and database)",
            Vm.DeleteProject(alpha.Id) && !Vm.ChatProjects.Contains(alpha) && members.Count > 0
            && members.All(id => Vm.Sessions.First(s => s.Id == id).ProjectId == "")
            && Db.LoadSessionList().Where(s => members.Contains(s.Id)).All(s => s.ProjectId == "")
            && Persist.LoadSession(c1.Id)?.Messages.Count == 2, $"{members.Count} members");
        Check("PJ20 the project list empties back to its empty state",
            Vm.DeleteProject(beta.Id) && Vm.DeleteProject(gone.Id) && !Vm.HasChatProjects && Vm.ChatProjects.Count == 0);
        Vm.SetWorkingDirectory("");
    }

    // ─── static checks on the sidebar's XAML ───

    static void StaticSidebarXaml()
    {
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CluadeX", "MainWindow.xaml"))) { repo = d.FullName; break; }
        if (repo == null) { Check("PJ static: repository found next to the harness", false); return; }
        string xaml = File.ReadAllText(Path.Combine(repo, "CluadeX", "MainWindow.xaml"));
        string menus = File.ReadAllText(Path.Combine(repo, "CluadeX", "Views", "SidebarMenus.cs"))
                       + File.ReadAllText(Path.Combine(repo, "CluadeX", "ViewModels", "ChatViewModel.Projects.cs"));
        LocSvc.SetLanguage("th");
        try
        {
            var keys = Regex.Matches(xaml, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(menus, @"L(?:oc|ocF)?\(""(sidebar\.[\w.]+)""").Select(m => m.Groups[1].Value))
                .Distinct().ToList();
            // (a product name stays in English in Thai too: "DGX Spark")
            var missing = keys.Where(k => LocSvc.T(k) == k || !Regex.IsMatch(LocSvc.T(k), "[\u0E00-\u0E7F]") && k != "spark.chip").ToList();
            Check("PJ static: every {services:Loc} key in MainWindow.xaml and every sidebar key the menus use has a Thai value",
                missing.Count == 0 && keys.Count > 20, string.Join(", ", missing));
        }
        finally { LocSvc.SetLanguage("en"); }

        var missingRes = Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !xaml.Contains($"x:Key=\"{k}\"") && Application.Current.TryFindResource(k) == null).ToList();
        Check("PJ static: every {StaticResource} in MainWindow.xaml exists", missingRes.Count == 0, string.Join(", ", missingRes));

        // Paths through ChatVM resolve on ChatViewModel; the project rows' paths on ChatProject; every other simple path
        // on one of the types the window binds (the window's VM, a chat row, a search result, a group header).
        var vmProps = typeof(ChatViewModel).GetProperties().Select(p => p.Name).ToHashSet();
        var badVm = Regex.Matches(xaml, @"ChatVM\.(\w+)").Select(m => m.Groups[1].Value).Distinct().Where(p => !vmProps.Contains(p)).ToList();
        Check("PJ static: every ChatVM.* binding path is a ChatViewModel property", badVm.Count == 0, string.Join(", ", badVm));
        int start = xaml.IndexOf("ItemsSource=\"{Binding ChatVM.ChatProjects}\"", StringComparison.Ordinal);
        int end = start < 0 ? -1 : xaml.IndexOf("</ItemsControl>", start, StringComparison.Ordinal);
        var projProps = typeof(ChatProject).GetProperties().Select(p => p.Name).ToHashSet();
        var badProj = start < 0 || end < 0 ? new List<string> { "(project list not found)" }
            : Regex.Matches(xaml[start..end], @"\{Binding (\w+)[,}]").Select(m => m.Groups[1].Value).Distinct()
                .Where(p => !projProps.Contains(p) && p != "IsMouseOver" && p != "DataContext").ToList();
        Check("PJ static: every binding in the project rows is a ChatProject property", badProj.Count == 0, string.Join(", ", badProj));
        var known = new[] { typeof(MainViewModel), typeof(MainViewModel.ToolPage), typeof(ChatViewModel), typeof(ChatSession), typeof(ChatProject), typeof(SearchResult), typeof(PaletteItem),
                typeof(System.Windows.Data.CollectionViewGroup), typeof(FrameworkElement), typeof(Button) }
            .SelectMany(t => t.GetProperties().Select(p => p.Name)).ToHashSet();
        var badAny = Regex.Matches(xaml, @"\{Binding (?:Path=)?([\w]+)[,}.]").Select(m => m.Groups[1].Value).Distinct().Where(p => !known.Contains(p)).ToList();
        Check("PJ static: every other {Binding} path in MainWindow.xaml names a real property", badAny.Count == 0, string.Join(", ", badAny));
    }
}
