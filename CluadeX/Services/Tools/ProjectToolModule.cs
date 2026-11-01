using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// open_project: the agent opens a project folder itself. A chat that starts with no folder open has no file tools,
/// so "open the atmos project" used to end in "I have no tool to read or write files" — the model found the path in
/// the brain and then had nothing to open it with (2026-10-14, it handed the whole task to another agent instead).
/// Offered with or without a project; the run's tools switch to the folder from the very next step, the chat on
/// screen shows it, and the chat remembers it as its project.
/// </summary>
public sealed class ProjectToolModule : IAgentToolModule
{
    private readonly SettingsService? _settings;
    private readonly ProjectRegistryService? _registry;

    public ProjectToolModule(SettingsService? settings = null, ProjectRegistryService? registry = null)
    {
        _settings = settings;
        _registry = registry;
    }

    /// <summary>Raised after a run opened a folder (the run's scope, or null outside a run; the full path). The chat
    /// page subscribes: it opens the folder in the app when that chat is on screen and stores it on the chat.</summary>
    public static event Action<AgentRunScope?, string>? ProjectOpened;

    /// <summary>Raised after a run opened a related folder beside its project (add=true).</summary>
    public static event Action<AgentRunScope?, string>? RelatedFolderAdded;

    public const string ToolName = "open_project";
    public const string NewChatToolName = "new_chat";

    /// <summary>
    /// Opens a new chat for new_chat (UI thread work, set by the chat page): the project folder (null = the one open),
    /// the first message to start there (null = an empty chat), a title (null = from the message). Returns what the
    /// tool reports, or throws to fail it. Null in harnesses with no chat page.
    /// </summary>
    public static Func<string?, string?, string?, bool, Task<string>>? NewChatHandler;

    public const string CloseChatToolName = "close_chat";

    /// <summary>
    /// close_chat (UI thread work, set by the chat page): the asking chat's split chats — list them (target "" / "list"),
    /// or put away the finished ones ("done") or one by id / title. Returns what the tool reports. Null in harnesses.
    /// </summary>
    public static Func<string, Task<string>>? CloseChatHandler;

    public string Id => "project";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.OpenProject, ToolType.NewChat, ToolType.CloseChat };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "open_project" or "open_folder" or "set_project" or "open_workspace" or "set_working_directory" => ToolType.OpenProject,
        "new_chat" or "open_new_chat" or "start_chat" or "new_session" or "create_chat" => ToolType.NewChat,
        "close_chat" or "archive_chat" or "finish_chat" or "close_chats" or "cleanup_chats" or "list_split_chats" => ToolType.CloseChat,
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "The project folder: its absolute path (e.g. D:\\Code\\Atmos) or just the project's name "
                        + "(e.g. \"atmos\") — CluadeX finds the folder among the projects on this PC (case, spaces and dashes "
                        + "do not matter; small typos are forgiven). Product names from README headings and .csproj metadata work too.",
                },
                ["add"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "\"true\" = open it BESIDE the current project (a related repo: a shared library, the API a "
                        + "client calls) instead of switching to it. Reach its files by absolute path.",
                },
                ["create"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "\"true\" = the user wants a NEW project and no folder of that name exists: create it and "
                        + "open it. With a name, CluadeX puts it beside the user's other projects; with a full path, there. "
                        + "If a project of that name already exists, that one opens instead (no duplicate).",
                },
            },
            ["required"] = new[] { "path" },
        };
        yield return new ToolSchema
        {
            Name = ToolName,
            Description = "Open a project folder so the file, search, shell and build tools work in it (from your next step on). "
                + "Use it when the user names a project and no folder — or the wrong one — is open: pass its name or path "
                + "(the list of projects opened before, or brain_search notes, say where each lives), then call this. Do not ask "
                + "the user to open it and do not hand the task off. For a NEW project (nothing of that name exists) call it "
                + "with create=\"true\" — never make a project folder yourself with mkdir. "
                + "When the task also spans a related project, open that one with add=\"true\" so both stay open.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };

        // Owner 2026-10-18: "cluadex ต้องเปิดแชทใหม่ โปรเจคใหม่ได้ตามคำสั่ง" — the agent answered that it had no way to
        // open a session. It has now: a new chat beside this one (both run), in this project or another.
        var chatSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "Absolute path of the project folder the new chat works in. Omit for the project open now.",
                },
                ["message"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "The task to start in the new chat, written as the user's first message (complete: "
                        + "what to do, where, what done looks like — that chat does not see this one). Omit for an empty chat.",
                },
                ["title"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "Optional short title for the chat in the sidebar.",
                },
                ["fork"] = new Dictionary<string, string>
                {
                    ["type"] = "boolean",
                    ["description"] = "true: the new chat starts with this chat's history (a fork), so the message may rely on "
                        + "it, and every turn there carries that history's cost. Default: it starts clean. Same project only.",
                },
            },
        };
        yield return new ToolSchema
        {
            Name = NewChatToolName,
            Description = "Open a NEW chat in CluadeX — in this project or another folder — and start a task there; it runs "
                + "beside this chat (in parallel) and the sidebar shows it under this chat as a split. Use it when the user "
                + "asks for a new chat / new session / to work on another project in its own chat, to hand a separate piece of "
                + "work to its own chat, or when the user adds a NEW, independent request while you are still working on "
                + "something else (a note that arrives mid-task): start that request in its own chat so both run at once, and "
                + "keep corrections or follow-ups of the current task here. The message must be complete — that chat does not "
                + "see this one, unless you pass fork. Never answer that you cannot open a chat.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(chatSchema)).RootElement.Clone(),
        };

        // Owner 2026-10-18: "ปิดแชทเก็บกวาดงานแยกที่เสร็จแล้วได้ … เพื่อทำให้แชทฮิสทอรี่สะอาด".
        var closeSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["chat"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "Which split chat to put away: its id or title, or \"done\" for every finished one. "
                        + "Omit (or \"list\") to only list this chat's split chats and how each stands.",
                },
            },
        };
        yield return new ToolSchema
        {
            Name = CloseChatToolName,
            Description = "The chats THIS chat split off with new_chat: list them with how each stands (working / finished / put "
                + "away), or put finished ones away so the chat list stays tidy. Put away = hidden from the list, kept, the user "
                + "can bring it back; a chat still working is never closed. Use it once a split chat's result is in.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(closeSchema)).RootElement.Clone(),
        };
    }

    public string? GetRefusal(ToolType type) => null;

    private static string L(string key) => LocalizedResources.Instance[key];

    /// <summary>
    /// Opening a folder reads nothing and changes nothing by itself — but reaching into ANOTHER project is the user's
    /// call (owner 2026-10-18: "ให้ cluadex ฉลาดมากในการเปิดโฟลเดอร์แก้งานที่เกี่ยวข้องได้เลย โดยเราไม่ต้องระบุ แต่ต้อง
    /// ขออนุญาตในการเปิดโฟลเดอร์อื่น จะให้ถามเสมอ หรือไม่ต้องถามอีกก็ได้"). Free: the first project of a chat with none
    /// open, a folder already open or inside one, a folder the user answered "Always allow", or every folder when the
    /// setting says "allow". Anything else asks once per folder per chat, with "Always allow this folder" on the card.
    /// </summary>
    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        string path = call.GetArg("path");
        var target = Target(call);
        string? full = target.Path;
        var s = _settings?.Settings;
        // A new project folder beside the user's other projects (or in a folder they allowed) is the ordinary case and
        // free; anywhere else the user says where new folders may appear.
        if (target.Creating && full != null)
        {
            string? parent = Path.GetDirectoryName(full);
            bool known = parent != null && (KnownRoots().Any(r => string.Equals(r.TrimEnd('\\', '/'), parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                || (s?.AllowedProjectFolders ?? new()).Any(a => IsSameOrInside(a, full)));
            if (known) return ToolPermission.Free("write", full);
            return new ToolPermission
            {
                Scope = "write",
                Resource = full,
                Detail = string.Format(L("chat.perm.createProject"), full),
                OnceKey = "create-folder|" + full.ToLowerInvariant(),
            };
        }
        if (full == null || string.IsNullOrWhiteSpace(context.WorkingDirectory)) return ToolPermission.Free("read", path);
        if (string.Equals(s?.RelatedFolderAccess?.Trim(), "allow", StringComparison.OrdinalIgnoreCase))
            return ToolPermission.Free("read", full);

        var open = new List<string> { context.WorkingDirectory! };
        if (AgentRunScope.Current is { } scope) open.AddRange(scope.ExtraFolders);
        if (open.Any(o => IsSameOrInside(o, full)) || (s?.AllowedProjectFolders ?? new()).Any(a => IsSameOrInside(a, full)))
            return ToolPermission.Free("read", full);

        return new ToolPermission
        {
            Scope = "read",
            Resource = full,
            Detail = string.Format(L("chat.perm.openFolder"), full),
            OnceKey = "open-folder|" + full.ToLowerInvariant(),
            OnAllowedAlways = s == null ? null : () =>
            {
                if (s.AllowedProjectFolders.Any(a => IsSameOrInside(a, full))) return;
                s.AllowedProjectFolders.Add(full);
                _settings!.Save();
            },
        };
    }

    /// <summary>new_chat: a new chat in the sidebar, in this project or <c>path</c>, started with <c>message</c>.</summary>
    private async Task<ToolResult> ExecuteNewChatAsync(ToolCall call)
    {
        ToolResult Fail(string msg) => new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };
        if (NewChatHandler == null) return Fail("No chat page is running here, so a new chat cannot be opened.");

        string? full = null;
        string raw = call.GetArg("path").Trim().Trim('"', '\'');
        if (raw.Length > 0)
        {
            full = FullPathOrNull(raw);
            if (full == null)
                return Fail($"'{raw}' is not an absolute folder path. Give the full path (e.g. D:\\Code\\Atmos) — brain_search usually has it.");
            if (!Directory.Exists(full))
                return Fail($"No folder at {full}. Search for the project (brain_search, or ask the user where it is now).");
            if (PermissionService.IsUnsafeAcceptEditsRoot(full, _settings?.DataRoot))
                return Fail($"{full} is a drive root or a system / profile folder, which the agent does not open by itself.");
        }
        string? message = call.GetArg("message").Trim() is { Length: > 0 } m ? m : null;
        string? title = call.GetArg("title").Trim() is { Length: > 0 } t ? t : null;
        bool fork = bool.TryParse(call.GetArg("fork"), out bool f) && f;
        if (fork && full != null) return Fail("fork stays in this chat's project: leave path out, or start clean with a complete message.");
        try
        {
            string said = await NewChatHandler(full, message, title, fork);
            if (full != null) _registry?.Record(full);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true, Output = said,
                Summary = "💬+ " + (title ?? full ?? "new chat"), FilePath = full,
            };
        }
        catch (Exception ex) { return Fail("The new chat could not be opened: " + ex.Message); }
    }

    /// <summary>close_chat: list this chat's split chats, or put finished ones away.</summary>
    private static async Task<ToolResult> ExecuteCloseChatAsync(ToolCall call)
    {
        if (CloseChatHandler == null)
        {
            const string none = "No chat page is running here, so there are no split chats to close.";
            return new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = none, Error = none };
        }
        string target = call.GetArg("chat").Trim().Trim('"', '\'');
        string said = await CloseChatHandler(target);
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = said,
            Summary = target.Length == 0 || target.Equals("list", StringComparison.OrdinalIgnoreCase) ? "💬 split chats" : "🗃 " + target,
        };
    }

    private static string? FullPathOrNull(string raw)
    {
        raw = (raw ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0) return null;
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw.StartsWith("~")) raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw.TrimStart('~', '/', '\\'));
        if (!Path.IsPathRooted(raw)) return null;
        try
        {
            string full = Path.GetFullPath(raw).TrimEnd('\\', '/');
            return full.EndsWith(':') ? full + "\\" : full;
        }
        catch { return null; }
    }

    /// <summary><paramref name="path"/> is <paramref name="root"/> or inside it (case-insensitive, whole segments).</summary>
    public static bool IsSameOrInside(string root, string path)
    {
        try
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            string p = Path.GetFullPath(path).TrimEnd('\\', '/');
            return p.Equals(r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        if (call.Type == ToolType.NewChat) return ExecuteNewChatAsync(call);
        if (call.Type == ToolType.CloseChat) return ExecuteCloseChatAsync(call);
        ToolResult Fail(string msg) => new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

        string raw = call.GetArg("path").Trim().Trim('"', '\'');
        if (raw.Length == 0) return Task.FromResult(Fail("open_project needs a path or a project name, e.g. D:\\Code\\Atmos or atmos."));
        var target = Target(call);
        if (target.Error != null) return Task.FromResult(Fail(target.Error));
        string full = target.Path!;

        if (File.Exists(full))
            return Task.FromResult(Fail($"{full} is a file, not a folder. Open the folder that contains it: {Path.GetDirectoryName(full)}"));
        // A drive root, the user profile, Windows, Program Files, AppData, CluadeX's own data: the agent never makes
        // one of these its project on its own — the user can, with the Open Folder button.
        if (PermissionService.IsUnsafeAcceptEditsRoot(full, _settings?.DataRoot))
            return Task.FromResult(Fail($"{full} is a drive root or a system / profile folder, which the agent does not open by itself. "
                + "Ask the user to open it with the Open Folder button if they really mean it."));
        if (target.Creating)
        {
            try { Directory.CreateDirectory(full); }
            catch (Exception ex) { return Task.FromResult(Fail($"Could not create {full}: {ex.Message}")); }
        }
        else if (!Directory.Exists(full))
            return Task.FromResult(Fail($"No folder at {full}."));
        string note = target.Note is { Length: > 0 } n ? n + " " : "";

        string? before = context.WorkingDirectory;
        bool add = call.GetArg("add").Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(before);
        if (add)
        {
            bool added = AgentRunScope.Current?.AddExtraFolder(full) ?? false;
            _registry?.Record(full);
            try { RelatedFolderAdded?.Invoke(AgentRunScope.Current, full); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[open_project] RelatedFolderAdded handler failed: {ex.Message}"); }
            var all = new List<string> { before! };
            if (AgentRunScope.Current is { } sc) all.AddRange(sc.ExtraFolders);
            string addText = note + (added ? $"Opened {full} beside the project." : $"{full} was already open.")
                + $" {all.Count} folders are open: " + string.Join(", ", all)
                + $". Relative paths stay in {before}; reach {full} by absolute path. Top level:\n" + Listing(full);
            return Task.FromResult(new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true, Output = addText,
                Summary = $"📁+ {full}", FilePath = full,
            });
        }
        if (AgentRunScope.Current is { } scope)
        {
            scope.WorkingDirectory = full;
            scope.ShellCwd = null;
        }
        _registry?.Record(full);
        try { ProjectOpened?.Invoke(AgentRunScope.Current, full); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[open_project] ProjectOpened handler failed: {ex.Message}"); }

        bool same = before != null && string.Equals(Path.GetFullPath(before).TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase);
        string text = note + (same ? $"{full} was already the open project." : $"Opened project {full}.")
            + " The file, search, shell and build tools now work in it (paths relative to this folder). Top level:\n"
            + Listing(full);
        return Task.FromResult(new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = text,
            Summary = $"📁 {full}", FilePath = full,
        });
    }

    /// <summary>The folder a call means: Path (null with Error), Creating = a new project folder is to be made, Note =
    /// what the result says about how it was found ("no folder at X — opened Y, the project of that name").</summary>
    private sealed record Resolved(string? Path, bool Creating, string? Note, string? Error);

    /// <summary>
    /// An existing absolute path is itself. A name — or an absolute path that is not there (a model guessing the drive,
    /// a note out of date) — is looked up by its last segment (<see cref="ProjectResolver"/>): one clear match opens;
    /// several are listed for the model to choose; none fails with where a new one would go. create="true" makes the
    /// folder (at the full path given, else beside the user's other projects) — unless a project of that name exists,
    /// which then opens instead.
    /// </summary>
    private Resolved Target(ToolCall call)
    {
        string raw = call.GetArg("path").Trim().Trim('"', '\'');
        bool create = call.GetArg("create").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        if (raw.Length == 0) return new Resolved(null, false, null, "open_project needs a path or a project name.");
        string? full = FullPathOrNull(raw);
        if (full != null && (Directory.Exists(full) || File.Exists(full))) return new Resolved(full, false, null, null);
        if (full != null && full.EndsWith(":\\")) return new Resolved(full, false, null, null);   // a drive root: refused later

        string name = full != null ? Path.GetFileName(full) : raw;
        var (known, roots, defaultRoot) = Knowledge();
        var found = ProjectResolver.Resolve(name, known, roots, SafeDirs, defaultRoot, ProjectResolver.ReadAliases);
        if (found.Found != null)
            return new Resolved(found.Found, false, full != null
                ? $"(There is no folder at {full}; the project \"{name}\" is at {found.Found} — opened that.)"
                : $"(Project \"{name}\" is at {found.Found}.)", null);
        if (found.Matches.Count > 1 && !(create && full != null))
            return new Resolved(null, false, null, $"Several projects match \"{name}\": {string.Join(", ", found.Matches.Take(6))}. "
                + "Call open_project again with the full path of the one the user means (ask the user if it is not clear).");
        if (create)
        {
            string target = full ?? found.NewPath;
            return new Resolved(target, true, $"(Created the new project folder {target}.)", null);
        }
        return new Resolved(null, false, null, (full != null ? $"No folder at {full}, and no project named \"{name}\" in the known project folders. " : $"No project named \"{name}\" in the known project folders. ")
            + $"If the user wants a NEW project, call open_project again with create=\"true\" (it will be made at {found.NewPath}, "
            + "beside the user's other projects — or pass a full path to put it elsewhere). If it should exist, search for it (brain_search) or ask the user where it is.");
    }

    /// <summary>The projects opened before, the folders that hold them, and where a new project goes.</summary>
    private (List<ProjectResolver.Candidate> Known, List<string> Roots, string DefaultRoot) Knowledge()
    {
        var registry = _registry ?? ProjectRegistryService.Current;
        var recent = registry?.Recent(50) ?? Array.Empty<ProjectRegistryService.Entry>();
        var known = recent.Select(e => new ProjectResolver.Candidate(e.Path, e.Name, e.Opens, e.LastOpened)).ToList();
        string? setting = _settings?.Settings.DefaultProjectRoot;
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var paths = known.Select(k => k.Path).ToList();
        return (known, ProjectResolver.Roots(paths, setting), ProjectResolver.DefaultRoot(setting, paths, documents));
    }

    private List<string> KnownRoots()
    {
        var (_, roots, defaultRoot) = Knowledge();
        if (!roots.Contains(defaultRoot, StringComparer.OrdinalIgnoreCase)) roots.Add(defaultRoot);
        return roots;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Take(500).ToList() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string Listing(string dir)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(dir).Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith('.') && n is not ("node_modules" or "bin" or "obj"))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(40).Select(n => n + "/");
            var files = Directory.EnumerateFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(40);
            var all = dirs.Concat(files!).ToList();
            return all.Count == 0 ? "(empty folder)" : string.Join("\n", all);
        }
        catch (Exception ex) { return $"(could not list: {ex.Message})"; }
    }

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = new[] { ToolName };

    /// <summary>The projects opened before, so a named one opens without a search. With a project open, the same list
    /// tells the agent which RELATED projects exist — it opens one beside this one by itself when the work crosses
    /// into it (the user is asked first, unless they said to always allow it).</summary>
    public string? GetPromptSection(PromptSectionContext context)
    {
        // A run's scope says where its tools work; with no scope (a prompt built outside a run) the UI's folder does.
        bool hasProject = AgentRunScope.Current is { } scope ? scope.WorkingDirectory != null : context.HasProject;
        var registry = _registry ?? ProjectRegistryService.Current;
        if (!hasProject) return registry?.PromptList();
        var recent = registry?.Recent(10) ?? Array.Empty<ProjectRegistryService.Entry>();
        string? current = AgentRunScope.Current?.WorkingDirectory;
        var others = recent.Where(e => current == null || !IsSameOrInside(current, e.Path)).ToList();
        string list = others.Count == 0 ? "" : "\nOther projects on this machine:\n" + string.Join("\n", others.Select(e => $"- {e.Name} — {e.Path}"));
        return "# Related projects\n"
            + "When the task needs code that lives in ANOTHER project (a shared library, the server an app calls, the tool "
            + "whose behaviour you are fixing), do not stop and ask the user to open it: find its path (the list below, or "
            + "brain_search) and call open_project with add=\"true\", then work in it by absolute path. The app asks the user "
            + "for permission itself — do not ask in chat first. If they deny, carry on with what you can and say what is left."
            + list;
    }
}
