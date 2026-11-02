using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;

namespace CluadeX.Services.AutoComment;

/// <summary>One open Code Editor tab as the chat's tool sees it.</summary>
public sealed record EditorFileState(string FullPath, bool Dirty, bool Active);

/// <summary>
/// add_code_comments — Code Editor ▸ Auto-comment from the chat (and /comment). The chat model only picks the file,
/// scope, language and model: a separate call through <see cref="AutoCommentService"/> answers {line, text} pairs and
/// the app inserts them, so neither model ever writes code. Same proofs as the editor: lexer placement, the
/// byte-for-byte strip check before and after the write, a Time Machine restore point, and a refusal when the file
/// changed while the model was thinking. The permission prompt carries the cost estimate — nothing is spent before it.
/// </summary>
public sealed class AutoCommentToolModule : IAgentToolModule
{
    public const string ToolName = "add_code_comments";

    private readonly SettingsService? _settings;
    private readonly AiProviderManager? _providers;
    private readonly GitService? _git;
    private readonly Func<IReadOnlyList<EditorFileState>?> _editorFiles;

    /// <summary>Test seam (like <c>CodeEditorViewModel.ModelOverride</c>): when set, the tool asks this model instead
    /// of the active provider — harnesses drive the whole tool with a canned answer and no network.</summary>
    public static Func<IAutoCommentModel>? ModelOverride { get; set; }

    public AutoCommentToolModule(SettingsService? settings, AiProviderManager? providers, GitService? git,
        Func<IReadOnlyList<EditorFileState>?>? editorFiles = null)
    {
        _settings = settings;
        _providers = providers;
        _git = git;
        _editorFiles = editorFiles ?? (() => null);
    }

    public string Id => "auto-comment";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.AddCodeComments };

    public bool WorksWithoutProject => false;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        ToolName or "auto_comment" or "comment_code" or "add_comments" => ToolType.AddCodeComments,
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        var props = new Dictionary<string, object>
        {
            ["path"] = Prop("string", "The source file (relative to the project). Leave out to use the file open in the Code Editor."),
            ["scope"] = new Dictionary<string, object>
            {
                ["type"] = "string", ["enum"] = Scopes,
                ["description"] = "functions = every function that does work; changed = only lines changed since git HEAD; " +
                                  "key = only the key points; file = the whole file; selection = only start_line..end_line.",
            },
            ["start_line"] = Prop("integer", "First line (1-based) for scope=selection."),
            ["end_line"] = Prop("integer", "Last line (1-based, inclusive) for scope=selection."),
            ["language"] = new Dictionary<string, object>
            {
                ["type"] = "string", ["enum"] = new[] { "th", "en", "match" },
                ["description"] = "Comment language: th = Thai, en = English, match = the language of the file's existing comments.",
            },
            ["model"] = Prop("string", "Model id for the analysis (API providers only). Leave out for the one picked in the Code Editor, else the provider's own."),
        };
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props };
        yield return new ToolSchema
        {
            Name = ToolName,
            Description = "Explain existing code with comments: what each part does and why. Another model call names lines and " +
                          "writes the text; the app inserts the comments in the file's own syntax and proves the code unchanged " +
                          "byte for byte, with a Time Machine restore point. Use this to comment code — never edit_file for that. " +
                          "The user is asked first and sees the estimated cost.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    private static readonly string[] Scopes = { "functions", "changed", "key", "file", "selection" };

    private static Dictionary<string, object> Prop(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

    public string? GetRefusal(ToolType type) => null;

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = Array.Empty<string>();

    public string? GetPromptSection(PromptSectionContext context) =>
        "To add explanatory comments to existing code, call add_code_comments (path, scope functions|changed|key|file|selection, " +
        "language th|en|match, optional model) — never write comments with edit_file / write_file: the tool's own model only names " +
        "lines and the app inserts them, so the code is proven unchanged.";

    // ─── Arguments ───

    private sealed record Target(string FullPath, string Display, CommentScopeKind Scope, string ScopeId, string Language,
        (int First, int Last)? Selection);

    private Target? Parse(ToolCall call, ToolModuleContext ctx, out string error)
    {
        error = "";
        string? project = ctx.WorkingDirectory;
        string raw = (call.GetArg("path") is { Length: > 0 } p ? p : call.GetArg("file_path", call.GetArg("file"))).Trim().Trim('"', '\'');
        string full;
        if (raw.Length == 0)
        {
            var open = SafeEditorFiles()?.FirstOrDefault(f => f.Active);
            if (open == null) { error = L("edt.acm.tool.noFile"); return null; }
            full = open.FullPath;
        }
        else
        {
            try { full = Path.GetFullPath(Path.IsPathFullyQualified(raw) || string.IsNullOrEmpty(project) ? raw : Path.Combine(project, raw)); }
            catch (Exception ex) { error = $"'{raw}' is not a valid path: {ex.Message}"; return null; }
        }
        if (string.IsNullOrEmpty(project) || !Inside(project, full)) { error = F("edt.acm.tool.outside", full); return null; }
        if (!File.Exists(full)) { error = F("edt.acm.tool.noSuchFile", full); return null; }

        string scopeArg = call.GetArg("scope").Trim().ToLowerInvariant();
        bool hasLines = call.GetArg("start_line").Length > 0 || call.GetArg("end_line").Length > 0 || call.GetArg("lines").Length > 0;
        if (scopeArg.Length == 0) scopeArg = hasLines ? "selection" : _settings?.Settings.AutoCommentScope ?? "functions";
        string? scopeId = scopeArg switch
        {
            "functions" or "function" or "every_function" or "methods" => "functions",
            "changed" or "diff" or "changes" => "changed",
            "key" or "key_points" or "keypoints" or "important" => "key",
            "file" or "all" or "whole_file" or "everything" => "file",
            "selection" or "lines" or "range" => "selection",
            _ => null,
        };
        if (scopeId == null) { error = F("edt.acm.tool.badScope", scopeArg, string.Join(", ", Scopes)); return null; }
        var kind = scopeId switch
        {
            "changed" => CommentScopeKind.Changed,
            "key" => CommentScopeKind.KeyPoints,
            "file" => CommentScopeKind.File,
            "selection" => CommentScopeKind.Selection,
            _ => CommentScopeKind.Functions,
        };

        (int, int)? selection = null;
        if (kind == CommentScopeKind.Selection)
        {
            int a = 0, b = 0;
            var lines = call.GetArg("lines").Split('-', ':', ',');
            bool ok = call.GetArg("start_line").Length > 0
                ? int.TryParse(call.GetArg("start_line"), out a) & int.TryParse(call.GetArg("end_line", call.GetArg("start_line")), out b)
                : lines.Length == 2 && int.TryParse(lines[0], out a) && int.TryParse(lines[1], out b);
            if (!ok || a < 1 || b < a) { error = L("edt.acm.tool.badLines"); return null; }
            selection = (a, b);
        }

        string langArg = call.GetArg("language", call.GetArg("lang")).Trim().ToLowerInvariant();
        string lang = langArg switch
        {
            "" => CodeCommentPolicy.NormalizeLanguage(_settings?.Settings.AutoCommentLanguage),
            "th" or "thai" or "ไทย" or "ภาษาไทย" => CodeCommentPolicy.Thai,
            "en" or "english" or "อังกฤษ" => CodeCommentPolicy.English,
            _ => CodeCommentPolicy.MatchFile,
        };
        string display = Path.GetRelativePath(project, full);
        return new Target(full, display, kind, scopeId, lang, selection);
    }

    private static bool Inside(string root, string path)
    {
        string r = Path.GetFullPath(root).TrimEnd('\\', '/');
        string p = Path.GetFullPath(path);
        return p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<EditorFileState>? SafeEditorFiles()
    {
        try { return _editorFiles(); }
        catch { return null; }   // the editor not up (or busy): nothing is known to be open
    }

    private bool DirtyInEditor(string full) =>
        SafeEditorFiles()?.Any(f => f.Dirty && string.Equals(f.FullPath, full, StringComparison.OrdinalIgnoreCase)) == true;

    // ─── Model ───

    private sealed record ModelPick(IAiProvider? Provider, string Name, string Effective, string? Override, bool Local, bool Api, string? Ignored);

    private ModelPick PickModel(string requested)
    {
        var type = _providers?.ActiveProviderType ?? _settings?.Settings.ActiveProvider ?? AiProviderType.Local;
        IAiProvider? provider = null;
        try { provider = _providers?.ActiveProvider; } catch { }
        bool local = type is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
        bool api = type is AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini;
        string configured = "";
        if (provider != null && _settings != null && _settings.Settings.ProviderConfigs.TryGetValue(provider.ProviderId, out var cfg)
            && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId))
            configured = cfg.EffectiveModelId!.Trim();
        else
            configured = type switch
            {
                AiProviderType.Anthropic => AnthropicProvider.DefaultModel,
                AiProviderType.OpenAI => OpenAiProvider.DefaultModel,
                AiProviderType.Gemini => GeminiProvider.DefaultModel,
                _ => "",
            };
        string? over = null, ignored = null;
        requested = (requested ?? "").Trim();
        if (requested.Length > 0)
        {
            if (api) over = requested; else ignored = requested;
        }
        else if (api && _settings?.Settings.AutoCommentModel is { Length: > 0 } saved)
        {
            // The model the owner picked in Code Editor ▸ Auto-comment, while this provider still offers it.
            var known = type switch
            {
                AiProviderType.Anthropic => AnthropicProvider.KnownModels,
                AiProviderType.OpenAI => OpenAiProvider.KnownModels,
                _ => GeminiProvider.KnownModels,
            };
            if (known.Contains(saved, StringComparer.OrdinalIgnoreCase)) over = saved;
        }
        string name = provider?.DisplayName ?? type.ToString();
        return new ModelPick(provider, name, over ?? configured, over, local, api, ignored);
    }

    private static string CostText(AutoCommentRequest req, ModelPick m)
    {
        var (input, output, usd) = AutoCommentService.Estimate(req, m.Effective, m.Local);
        string price = m.Local ? L("edt.acm.free") : !m.Api ? L("edt.acm.unpriced") : "$" + usd.ToString("0.0000");
        return F("edt.acm.estimate", m.Effective.Length > 0 ? m.Effective : m.Name, input, output, price);
    }

    // ─── Permission ───

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        var t = Parse(call, context, out _);
        // A call that cannot run asks nothing: Execute fails with the reason before any model is called.
        if (t == null) return ToolPermission.Free("write", call.GetArg("path"));
        var m = PickModel(call.GetArg("model"));
        AutoCommentRequest req;
        try
        {
            // The changed-lines scope is estimated on the whole file here (no git call in the gate) — an upper bound.
            req = AutoCommentService.Prepare(t.FullPath, new AutoCommentOptions(
                t.Scope == CommentScopeKind.Changed ? CommentScopeKind.File : t.Scope, t.Language, m.Override, t.Selection));
        }
        catch (Exception) { return ToolPermission.Free("write", t.FullPath); }
        var (_, _, usd) = AutoCommentService.Estimate(req, m.Effective, m.Local);
        string detail = F("edt.acm.tool.permDetail", t.Display, L("edt.acm.scope." + t.ScopeId), L("edt.acm.lang." + t.Language),
            req.Allowed.Count, CostText(req, m));
        return new ToolPermission
        {
            Scope = "write", Resource = t.FullPath, Detail = detail,
            // The editor never spends money without showing the cost first: a paid run is asked about even with the
            // permission system switched off (a user rule that allows writing here still decides).
            AskEvenWhenPermissionsOff = m.Api && usd > 0,
        };
    }

    // ─── Execute ───

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var t = Parse(call, context, out var error);
        if (t == null) return Fail(call, error);
        if (DirtyInEditor(t.FullPath)) return Fail(call, F("edt.acm.tool.unsaved", t.Display));
        var m = PickModel(call.GetArg("model"));
        var model = ModelOverride;
        if (model == null && (m.Provider == null || !m.Provider.IsReady)) return Fail(call, F("edt.acm.notReady", m.Name));

        AutoCommentPreview preview;
        AutoCommentApplied applied;
        try
        {
            IReadOnlyCollection<int>? changed = null;
            if (t.Scope == CommentScopeKind.Changed)
            {
                if (_git == null) return Fail(call, L("edt.acm.noGit"));
                changed = await AutoCommentService.ChangedLinesAsync(_git, t.FullPath).ConfigureAwait(false);
            }
            var options = new AutoCommentOptions(t.Scope, t.Language, m.Override, t.Selection, changed);
            var req = await Task.Run(() => AutoCommentService.Prepare(t.FullPath, options), ct).ConfigureAwait(false);
            IAutoCommentModel ai = model?.Invoke() ?? new ProviderCommentModel(m.Provider!, m.Override);
            preview = await AutoCommentService.RunAsync(req, ai, ct).ConfigureAwait(false);
            // Typing may have started in the editor while the model was thinking: an unsaved tab is never written over.
            if (DirtyInEditor(t.FullPath)) return Fail(call, F("edt.acm.tool.unsaved", t.Display));
            // Apply refuses (and writes nothing) when the file on disk is no longer what the model read.
            applied = AutoCommentService.Apply(preview, CheckpointService.Current, context.WorkingDirectory);
        }
        catch (AutoCommentException ex) { return Fail(call, F("edt.acm.failed", F(ex.Key, ex.Args))); }

        var plan = preview.Plan;
        var lines = plan.Result.Split('\n');
        var sb = new StringBuilder();
        sb.Append($"Added {plan.Count} comment(s) to {t.Display} (scope {t.ScopeId}, language {t.Language}, model {(m.Effective.Length > 0 ? m.Effective : m.Name)}). ");
        sb.Append("Only comment lines were inserted: removing them gives the original file byte for byte (checked before and after the write). ");
        sb.Append(applied.CheckpointProject != null ? "A Time Machine restore point holds the file as it was.\n" : "\n");
        if (m.Ignored != null) sb.Append($"(model '{m.Ignored}' ignored: {m.Name} answers with its own model)\n");
        if (plan.Skipped.Count > 0)
            sb.Append($"Skipped {plan.Skipped.Count}: ").Append(string.Join("; ",
                plan.Skipped.GroupBy(s => s.ReasonKey).Select(g => $"{g.Count()} × {L(g.Key)}"))).Append('\n');
        sb.Append("Inserted lines (new numbering):\n");
        foreach (int k in plan.Inserted.Take(80)) sb.Append($"  {k + 1}: {lines[k].Trim().TrimEnd('\r')}\n");
        if (plan.Inserted.Count > 80) sb.Append($"  … and {plan.Inserted.Count - 80} more\n");

        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = sb.ToString(),
            Summary = F("edt.acm.tool.summary", plan.Count, Path.GetFileName(t.FullPath)),
            Diff = CluadeX.Helpers.DiffUtil.Compute(preview.Request.Text, plan.Result),
            FilePath = t.Display,
        };
    }

    private static ToolResult Fail(ToolCall call, string msg) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

    private static string L(string key) => LocalizedResources.Instance[key];

    private static string F(string key, params object[] args)
    {
        string f = L(key);
        try { return args.Length == 0 ? f : string.Format(f, args); }
        catch (FormatException) { return f; }
    }
}
