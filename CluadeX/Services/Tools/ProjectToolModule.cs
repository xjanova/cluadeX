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
    public static Func<string?, string?, string?, Task<string>>? NewChatHandler;

    public string Id => "project";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.OpenProject, ToolType.NewChat };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "open_project" or "open_folder" or "set_project" or "open_workspace" or "set_working_directory" => ToolType.OpenProject,
        "new_chat" or "open_new_chat" or "start_chat" or "new_session" or "create_chat" => ToolType.NewChat,
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
                    ["description"] = "Absolute path of the project folder, e.g. D:\\Code\\Atmos",
                },
                ["add"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "\"true\" = open it BESIDE the current project (a related repo: a shared library, the API a "
                        + "client calls) instead of switching to it. Reach its files by absolute path.",
                },
            },
            ["required"] = new[] { "path" },
        };
        yield return new ToolSchema
        {
            Name = ToolName,
            Description = "Open a project folder so the file, search, shell and build tools work in it (from your next step on). "
                + "Use it when the user names a project and no folder — or the wrong one — is open: find the path (brain_search "
                + "notes record where each project lives), then call this. Do not ask the user to open it and do not hand the task off. "
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
            },
        };
        yield return new ToolSchema
        {
            Name = NewChatToolName,
            Description = "Open a NEW chat in CluadeX — in this project or another folder — and start a task there; it runs "
                + "beside this chat and the user can switch to it in the sidebar. Use it when the user asks for a new chat / "
                + "new session / to work on another project in its own chat, or to hand a separate piece of work to its own "
                + "chat. Never answer that you cannot open a chat.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(chatSchema)).RootElement.Clone(),
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
        string? full = FullPathOrNull(path);
        if (full == null || string.IsNullOrWhiteSpace(context.WorkingDirectory)) return ToolPermission.Free("read", path);
        var s = _settings?.Settings;
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
        try
        {
            string said = await NewChatHandler(full, message, title);
            if (full != null) _registry?.Record(full);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true, Output = said,
                Summary = "💬+ " + (title ?? full ?? "new chat"), FilePath = full,
            };
        }
        catch (Exception ex) { return Fail("The new chat could not be opened: " + ex.Message); }
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
        ToolResult Fail(string msg) => new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

        string raw = call.GetArg("path").Trim().Trim('"', '\'');
        if (raw.Length == 0) return Task.FromResult(Fail("open_project needs a path, e.g. D:\\Code\\Atmos."));
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw.StartsWith("~")) raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw.TrimStart('~', '/', '\\'));
        if (!Path.IsPathRooted(raw))
            return Task.FromResult(Fail($"'{raw}' is not an absolute path. Give the full folder path (e.g. D:\\Code\\Atmos) — brain_search usually has it."));

        string full;
        try { full = Path.GetFullPath(raw).TrimEnd('\\', '/'); }
        catch (Exception ex) { return Task.FromResult(Fail($"'{raw}' is not a valid path: {ex.Message}")); }
        if (full.EndsWith(':')) full += "\\";

        if (File.Exists(full))
            return Task.FromResult(Fail($"{full} is a file, not a folder. Open the folder that contains it: {Path.GetDirectoryName(full)}"));
        if (!Directory.Exists(full))
            return Task.FromResult(Fail($"No folder at {full}. The note may be out of date — search for the project (brain_search, or ask the user where it is now)."));
        // A drive root, the user profile, Windows, Program Files, AppData, CluadeX's own data: the agent never makes
        // one of these its project on its own — the user can, with the Open Folder button.
        if (PermissionService.IsUnsafeAcceptEditsRoot(full, _settings?.DataRoot))
            return Task.FromResult(Fail($"{full} is a drive root or a system / profile folder, which the agent does not open by itself. "
                + "Ask the user to open it with the Open Folder button if they really mean it."));

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
            string addText = (added ? $"Opened {full} beside the project." : $"{full} was already open.")
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
        string text = (same ? $"{full} was already the open project." : $"Opened project {full}.")
            + " The file, search, shell and build tools now work in it (paths relative to this folder). Top level:\n"
            + Listing(full);
        return Task.FromResult(new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = text,
            Summary = $"📁 {full}", FilePath = full,
        });
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
