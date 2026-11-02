using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Helpers;

namespace CluadeX.Services.AutoComment;

/// <summary>What the user picked: scope, comment language (th / en / match), model (null = the provider's own).
/// <see cref="Selection"/> is 1-based inclusive; <see cref="ChangedLines"/> are 1-based (null = untracked: every line).</summary>
public sealed record AutoCommentOptions(CommentScopeKind Scope, string Language, string? Model = null,
    (int First, int Last)? Selection = null, IReadOnlyCollection<int>? ChangedLines = null);

/// <summary>A model that answers one prompt. The app's is <see cref="ProviderCommentModel"/>; tests pass a canned one.</summary>
public interface IAutoCommentModel
{
    Task<string> AnswerAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}

/// <summary>A refusal the UI shows: <see cref="Key"/> is an <c>edt.acm.*</c> localization key.</summary>
public sealed class AutoCommentException(string key, params object[] args) : Exception(key)
{
    public string Key { get; } = key;
    public object[] Args { get; } = args;
}

/// <summary>Everything decided before the model is called — enough for the cost estimate.</summary>
public sealed class AutoCommentRequest
{
    public required string FullPath { get; init; }
    public required byte[] OriginalBytes { get; init; }
    public required string Text { get; init; }
    public required int CodePage { get; init; }
    public required bool Bom { get; init; }
    public required CommentLanguage Language { get; init; }
    public required LexResult Lex { get; init; }
    public required SortedSet<int> Allowed { get; init; }
    public required Dictionary<int, int> AnchorRemap { get; init; }
    public required int MaxComments { get; init; }
    public required int FunctionCount { get; init; }
    public required AutoCommentOptions Options { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
}

/// <summary>The model answered and the comments are placed — nothing is written yet. The user sees the diff first.</summary>
public sealed class AutoCommentPreview
{
    public required AutoCommentRequest Request { get; init; }
    public required CommentPlan Plan { get; init; }
    public required byte[] NewBytes { get; init; }
    public string FullPath => Request.FullPath;
}

/// <summary>A write that happened: enough to undo it (only while the file is still exactly what was written).</summary>
public sealed record AutoCommentApplied(string FullPath, byte[] OriginalBytes, byte[] WrittenBytes, int Comments, string? CheckpointProject);

/// <summary>
/// Phase 5 — analyse existing code and comment it (owner 2026-11-01: "ให้เอไอไล่ใส่คอมเม้นต์ว่าโค๊ดแต่ละบรรทัดทำงานยังไง มีไปทำไม").
/// The flow: <see cref="Prepare"/> (lex, scope, prompt, cost) → <see cref="RunAsync"/> (the model names lines and gives
/// text; <see cref="CommentPlanner"/> inserts and proves nothing else changed) → the user reviews the diff →
/// <see cref="Apply"/> (a Time Machine restore point, an atomic write, the byte-for-byte proof read back from disk,
/// restore on any doubt) → <see cref="Undo"/> any time while the file is unchanged since.
/// </summary>
public static class AutoCommentService
{
    /// <summary>Above this the whole file is not sent: only the lines in scope with some context around them.</summary>
    public const int WholeFilePromptChars = 160_000;
    public const int ContextLines = 12;
    public const long MaxFileBytes = 2 * 1024 * 1024;

    public const string ToolName = "submit_comments";

    public static readonly string ToolSchemaJson = """
        {"type":"object","properties":{"comments":{"type":"array","items":{"type":"object","properties":{
        "line":{"type":"integer","description":"The line number (as shown at the left) the comment goes above."},
        "text":{"type":"string","description":"The comment text: plain prose, no comment markers, no code."}},
        "required":["line","text"]}}},"required":["comments"]}
        """;

    /// <summary>Read and analyse the file, pick the lines in scope and write the prompt. Throws
    /// <see cref="AutoCommentException"/> for anything that makes the run pointless or unsafe.</summary>
    public static AutoCommentRequest Prepare(string fullPath, AutoCommentOptions options) =>
        Prepare(fullPath, File.ReadAllBytes(fullPath), options);

    public static AutoCommentRequest Prepare(string fullPath, byte[] bytes, AutoCommentOptions options)
    {
        var lang = CommentLanguage.For(fullPath) ?? throw new AutoCommentException("edt.acm.err.language", Path.GetExtension(fullPath));
        if (bytes.LongLength > MaxFileBytes) throw new AutoCommentException("edt.acm.err.tooLarge", bytes.LongLength / 1024);
        var decoded = TextFileCodec.DecodeForEditor(bytes);
        if (!decoded.Faithful) throw new AutoCommentException("edt.acm.err.encoding");
        string text = decoded.Text;
        if (text.Length > 0 && text[0] == CommentLexer.Bom) throw new AutoCommentException("edt.acm.err.encoding");

        var lex = CommentLexer.Lex(text, lang);
        if (lex.Count == 0) throw new AutoCommentException("edt.acm.err.empty");
        var allowed = CommentScopes.AllowedLines(lex, lang, options.Scope, options.Selection, options.ChangedLines);
        var functions = CommentScopes.FindFunctions(lex, lang);
        if (allowed.Count == 0)
            throw new AutoCommentException(options.Scope switch
            {
                CommentScopeKind.Functions => "edt.acm.err.noFunctions",
                CommentScopeKind.Changed => "edt.acm.err.noChanges",
                CommentScopeKind.Selection => "edt.acm.err.noSelection",
                _ => "edt.acm.err.nothing",
            });
        int max = CommentScopes.MaxComments(options.Scope, allowed.Count, lex.Count);
        string user = BuildUserPrompt(fullPath, lang, lex, allowed, options, max, functions.Count(f => !f.IsType));
        return new AutoCommentRequest
        {
            FullPath = fullPath, OriginalBytes = bytes, Text = text, CodePage = decoded.CodePage, Bom = decoded.HasBom,
            Language = lang, Lex = lex, Allowed = allowed, AnchorRemap = CommentScopes.AnchorRemap(lex, lang), MaxComments = max,
            FunctionCount = functions.Count, Options = options, SystemPrompt = SystemPrompt, UserPrompt = user,
        };
    }

    public const string SystemPrompt =
        "You add explanatory comments to existing source code. You never change, add, move or delete code: you only choose " +
        "where a comment goes and what it says. The application inserts each comment itself, as whole lines above the line " +
        "you name, in the file's own comment syntax and indentation.\n" +
        "Answer only by calling the submit_comments tool. If you cannot call tools, answer with this JSON and nothing else: " +
        "{\"comments\":[{\"line\":<line number>,\"text\":\"<comment>\"}]}\n" +
        "Rules:\n" +
        "- \"line\" is the number shown at the left of the code line the comment belongs above. Use only the allowed lines.\n" +
        "- \"text\" is plain prose without comment markers (//, #, /* */, <!-- -->) and without code. One to three short " +
        "sentences; use \\n only to break a longer comment into lines.\n" +
        "- Say what the code does and why it is there — the intent, the reason, the trap. Don't restate what a line plainly says.\n" +
        "- Never put a tool directive in a comment (eslint-disable, noqa, type: ignore, @ts-ignore, pragma, nolint, go:build…).\n" +
        "- Leave existing comments alone and don't repeat one that is already right above the line.";

    private static string BuildUserPrompt(string path, CommentLanguage lang, LexResult lex, SortedSet<int> allowed,
        AutoCommentOptions o, int max, int functions)
    {
        var sb = new StringBuilder();
        sb.Append("File: ").Append(Path.GetFileName(path)).Append(" (").Append(lang.DisplayName).Append(")\n");
        sb.Append("Task: ").Append(o.Scope switch
        {
            CommentScopeKind.Functions =>
                $"Comment every function, method and class that does real work ({functions} found): one comment above each " +
                "declaration saying what it does and why, and short comments above the steps inside its body that are not obvious.",
            CommentScopeKind.Changed => "Comment only the lines that changed (the allowed lines): what each change does and why.",
            CommentScopeKind.KeyPoints =>
                $"Comment only the key points: non-obvious decisions, tricky steps, workarounds and traps. At most {max} comments.",
            CommentScopeKind.Selection => "Comment the selected lines (the allowed lines): what each part does and why it is there.",
            _ => "Comment the whole file, line by line where it helps a reader: what each part does and why it is there.",
        }).Append('\n');
        sb.Append("Comment language: ").Append(CodeCommentPolicy.NormalizeLanguage(o.Language) switch
        {
            CodeCommentPolicy.Thai => "Thai (code identifiers and technical terms stay as they are)",
            CodeCommentPolicy.English => "English",
            _ => "the language the file's existing comments use (English when it has none)",
        }).Append('\n');
        sb.Append("Allowed lines: ").Append(CommentScopes.Ranges(allowed)).Append('\n');
        if (o.Scope != CommentScopeKind.KeyPoints) sb.Append("At most ").Append(max).Append(" comments.\n");
        sb.Append("\nCode:\n");

        int width = lex.Count.ToString().Length;
        void Line(int k) => sb.Append((k + 1).ToString().PadLeft(width)).Append("| ").Append(lex.Lines[k]).Append('\n');
        long whole = lex.Lines.Sum(l => (long)l.Length + width + 3);
        if (whole <= WholeFilePromptChars)
        {
            for (int k = 0; k < lex.Count; k++) Line(k);
            return sb.ToString();
        }
        // A big file: the lines in scope with context; what is left out is said so the numbers still make sense.
        var show = new SortedSet<int>();
        foreach (int a in allowed)
            for (int k = Math.Max(0, a - ContextLines); k <= Math.Min(lex.Count - 1, a + ContextLines); k++) show.Add(k);
        long chars = show.Sum(k => (long)lex.Lines[k].Length + width + 3);
        if (chars > WholeFilePromptChars) throw new AutoCommentException("edt.acm.err.promptTooLarge");
        int prev = -1;
        foreach (int k in show)
        {
            if (k != prev + 1) sb.Append($"… (lines {prev + 2}-{k} not shown)\n");
            Line(k);
            prev = k;
        }
        if (prev < lex.Count - 1) sb.Append($"… (lines {prev + 2}-{lex.Count} not shown)\n");
        return sb.ToString();
    }

    /// <summary>Rough cost before the run: the prompt in, the expected comments out (Thai runs ~2 chars a token).</summary>
    public static (int InputTokens, int OutputTokens, decimal Usd) Estimate(AutoCommentRequest r, string? model, bool local)
    {
        int input = CostEstimate.TokensOf(r.SystemPrompt.Length + r.UserPrompt.Length + ToolSchemaJson.Length);
        int expected = r.Options.Scope switch
        {
            CommentScopeKind.KeyPoints => r.MaxComments,
            CommentScopeKind.Functions => Math.Max(3, r.FunctionCount * 3),
            CommentScopeKind.File => Math.Max(5, r.Allowed.Count / 3),
            _ => Math.Max(3, r.Allowed.Count / 2),
        };
        expected = Math.Min(expected, r.MaxComments);
        int perComment = CodeCommentPolicy.NormalizeLanguage(r.Options.Language) == CodeCommentPolicy.Thai ? 70 : 40;
        int output = 60 + expected * perComment;
        if (local || string.IsNullOrWhiteSpace(model)) return (input, output, 0m);
        var p = CostTrackingService.PricingOf(model, input);
        return (input, output, (input * p.InputPerMToken + output * p.OutputPerMToken) / 1_000_000m);
    }

    /// <summary>Ask the model, place its comments and prove the code unchanged. Writes nothing: a cancel at any point
    /// leaves the file as it was.</summary>
    public static async Task<AutoCommentPreview> RunAsync(AutoCommentRequest r, IAutoCommentModel model, CancellationToken ct)
    {
        string answer = await model.AnswerAsync(r.SystemPrompt, r.UserPrompt, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var parsed = CommentPlanner.ParseAnswer(answer, r.Language);
        if (parsed.Refused) throw new AutoCommentException(parsed.RefuseKey!, parsed.RefuseDetail ?? "");
        var plan = CommentPlanner.Plan(r.Text, r.Language, r.Lex, parsed.Comments, r.Allowed, r.AnchorRemap, r.MaxComments);
        if (plan.Count == 0) throw new AutoCommentException("edt.acm.err.noneUsable", plan.Skipped.Count);
        string? why = CommentPlanner.Verify(plan, r.Language);
        if (why != null) throw new AutoCommentException("edt.acm.err.verify", why);

        byte[] bytes;
        try { bytes = TextFileCodec.EncodeForEditor(plan.Result, r.CodePage, r.Bom); }
        catch (EncoderFallbackException) { throw new AutoCommentException("edt.acm.err.cannotEncode", TextFileCodec.EncodingLabel(r.CodePage, r.Bom)); }
        if (!RoundTrips(bytes, plan.Inserted, r)) throw new AutoCommentException("edt.acm.err.verify", "byte-roundtrip");
        ct.ThrowIfCancellationRequested();
        return new AutoCommentPreview { Request = r, Plan = plan, NewBytes = bytes };
    }

    /// <summary>The byte-level proof: decode what would be written, drop the inserted lines, encode again — it must be
    /// the original file byte for byte (BOM, encoding and line endings included).</summary>
    public static bool RoundTrips(byte[] newBytes, IReadOnlyCollection<int> inserted, AutoCommentRequest r)
    {
        try
        {
            var d = TextFileCodec.DecodeForEditor(newBytes);
            if (!d.Faithful || d.CodePage != r.CodePage || d.HasBom != r.Bom) return false;
            byte[] back = TextFileCodec.EncodeForEditor(CommentPlanner.Strip(d.Text, inserted), r.CodePage, r.Bom);
            return back.AsSpan().SequenceEqual(r.OriginalBytes);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Write the previewed comments: refuse when the file changed since the preview, keep a Time Machine restore point,
    /// write atomically, read back and prove the bytes; any doubt puts the original back and throws.
    /// </summary>
    public static AutoCommentApplied Apply(AutoCommentPreview p, CheckpointService? checkpoints, string? projectDir)
    {
        string path = p.FullPath;
        byte[] now = File.ReadAllBytes(path);
        if (!now.AsSpan().SequenceEqual(p.Request.OriginalBytes)) throw new AutoCommentException("edt.acm.err.changedOnDisk");

        string project = !string.IsNullOrWhiteSpace(projectDir) ? projectDir! : Path.GetDirectoryName(path)!;
        string? pointProject = null;
        if (checkpoints != null)
        {
            // CheckpointService keeps files only inside a run scope: make a one-step one for this write.
            var prev = AgentRunScope.Current;
            var scope = new AgentRunScope("auto-comment", project) { Step = 1 };
            scope.Todos.Add(new Models.TodoItem { Content = $"Auto-comment {Path.GetFileName(path)} ({p.Plan.Count})", Status = "in_progress" });
            AgentRunScope.Enter(scope);
            try { checkpoints.CaptureBeforeChange(path, "before auto-comment"); pointProject = project; }
            finally { AgentRunScope.Enter(prev); }
        }

        string tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".cluadex-comment.tmp");
        try
        {
            File.WriteAllBytes(tmp, p.NewBytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw new AutoCommentException("edt.acm.err.write", ex.Message);
        }

        byte[] written = File.ReadAllBytes(path);
        if (!written.AsSpan().SequenceEqual(p.NewBytes) || !RoundTrips(written, p.Plan.Inserted, p.Request))
        {
            File.WriteAllBytes(path, p.Request.OriginalBytes);
            throw new AutoCommentException("edt.acm.err.verify", "disk-readback");
        }
        return new AutoCommentApplied(path, p.Request.OriginalBytes, p.NewBytes, p.Plan.Count, pointProject);
    }

    /// <summary>Put the file back as it was before <see cref="Apply"/> — only while it is still exactly what was written
    /// (otherwise the user's later edits would be lost; the Time Machine point is still there for that case).</summary>
    public static void Undo(AutoCommentApplied a)
    {
        byte[] now = File.ReadAllBytes(a.FullPath);
        if (!now.AsSpan().SequenceEqual(a.WrittenBytes)) throw new AutoCommentException("edt.acm.err.changedSinceApply");
        File.WriteAllBytes(a.FullPath, a.OriginalBytes);
    }

    /// <summary>The tool definition handed to providers with native tool use (structured output).</summary>
    public static Providers.ToolSchema ToolSchema()
    {
        using var doc = JsonDocument.Parse(ToolSchemaJson);
        return new Providers.ToolSchema
        {
            Name = ToolName,
            Description = "Submit the comments to insert: each names the line it goes above and the comment text.",
            InputSchema = doc.RootElement.Clone(),
        };
    }
}
