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

    public const string ToolName = "open_project";

    public string Id => "project";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.OpenProject };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "open_project" or "open_folder" or "set_project" or "open_workspace" or "set_working_directory" => ToolType.OpenProject,
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
            },
            ["required"] = new[] { "path" },
        };
        yield return new ToolSchema
        {
            Name = ToolName,
            Description = "Open a project folder so the file, search, shell and build tools work in it (from your next step on). "
                + "Use it when the user names a project and no folder — or the wrong one — is open: find the path (brain_search "
                + "notes record where each project lives), then call this. Do not ask the user to open it and do not hand the task off.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    public string? GetRefusal(ToolType type) => null;

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context) =>
        // Opening a folder reads nothing and changes nothing; every write after it still goes through its own gate.
        ToolPermission.Free("read", call.GetArg("path"));

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
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

    /// <summary>The projects opened before, so a named one opens without a search (offered only with no folder open).</summary>
    public string? GetPromptSection(PromptSectionContext context) =>
        AgentRunScope.Current?.WorkingDirectory == null ? (_registry ?? ProjectRegistryService.Current)?.PromptList() : null;
}
