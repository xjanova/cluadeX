using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Attachments;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// The tools that make a file the user attached usable at once (owner 2026-10-19: วางภาพแล้วสั่งให้ใช้ภาพทำงาน,
/// วาง zip ต้องแตกได้, PDF ต้องอ่านได้):
/// <list type="bullet">
/// <item>read_document — the text of a PDF, Word, Excel, PowerPoint, text or source file, a slice at a time;</item>
/// <item>extract_archive — unpack a zip / tar / tar.gz / gz safely (ArchiveExtractor: zip-slip and zip-bomb guarded);</item>
/// <item>copy_file — copy a file or folder (an attachment, an unpacked archive) into the project, e.g. a picture into
///   the site's assets.</item>
/// </list>
/// Reading or unpacking inside the chat attachments folder or the project is free; anything that writes into the
/// project asks, as the file tools do, unless the user's rules allow it.
/// </summary>
public sealed class AttachmentToolModule : IAgentToolModule
{
    private readonly Func<string?> _attachmentsRoot;

    public AttachmentToolModule(SettingsService? settings)
        : this(() => settings == null ? null : Path.Combine(settings.DataRoot, "attachments")) { }

    public AttachmentToolModule(Func<string?> attachmentsRoot) => _attachmentsRoot = attachmentsRoot;

    public string Id => "attachments";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.ReadDocument, ToolType.ExtractArchive, ToolType.CopyFile };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "read_document" or "read_pdf" or "read_docx" or "read_xlsx" or "read_attachment" => ToolType.ReadDocument,
        "extract_archive" or "unzip" or "extract_zip" or "unpack_archive" => ToolType.ExtractArchive,
        "copy_file" or "copy_files" or "copy_path" or "import_file" => ToolType.CopyFile,
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        yield return Schema("read_document",
            "Read the text of a document: PDF, Word (.docx), Excel (.xlsx, as tab-separated rows per sheet), PowerPoint (.pptx), "
            + "or any text / code / CSV / JSON file; a zip/tar lists its entries. Large documents come back a slice at a time — "
            + "pass offset (from the footer) to continue. A scanned PDF is reported as such. Use it for files the user attached.",
            ("path", "Absolute path, or relative to the project", true),
            ("offset", "Character offset to start from (default 0)", false),
            ("max_chars", "Most characters to return (default 30000)", false));
        yield return Schema("extract_archive",
            "Unpack a .zip, .tar, .tar.gz/.tgz or .gz safely (entries that would escape the folder, links and zip bombs are refused). "
            + "Default destination: a folder next to the archive (for an attachment) or under the attachments folder. "
            + "Pass destination (relative = inside the project) to unpack into the project. Existing files are kept unless overwrite=true.",
            ("path", "The archive (absolute, or relative to the project)", true),
            ("destination", "Folder to unpack into (created if missing)", false),
            ("overwrite", "true to replace files that already exist", false));
        yield return Schema("copy_file",
            "Copy a file or a whole folder to a new place — typically an attached file (its absolute path) into the project, e.g. a "
            + "picture into the site's assets folder. destination relative = inside the project; a destination ending in / or an "
            + "existing folder receives the item under its own name. Refuses to replace an existing file unless overwrite=true.",
            ("source", "The file or folder to copy (absolute path)", true),
            ("destination", "Target path (relative = inside the project)", true),
            ("overwrite", "true to replace an existing file", false));
    }

    private static ToolSchema Schema(string name, string description, params (string Name, string Desc, bool Required)[] props)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props.ToDictionary(p => p.Name, p => (object)new Dictionary<string, string> { ["type"] = "string", ["description"] = p.Desc }),
            ["required"] = props.Where(p => p.Required).Select(p => p.Name).ToArray(),
        };
        return new ToolSchema { Name = name, Description = description, InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone() };
    }

    public string? GetRefusal(ToolType type) => null;

    // ─── Paths ───

    private static bool Inside(string? root, string path)
    {
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            string p = Path.GetFullPath(path).TrimEnd('\\', '/');
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>An absolute path for what the model wrote: absolute as is, relative under the project. Null = unusable.</summary>
    private static string? Resolve(string raw, string? project, out string? error)
    {
        error = null;
        raw = (raw ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0) { error = "No path given."; return null; }
        try
        {
            if (Path.IsPathFullyQualified(raw)) return Path.GetFullPath(raw);
            if (string.IsNullOrEmpty(project)) { error = $"'{raw}' is relative but no project is open — pass an absolute path."; return null; }
            return Path.GetFullPath(Path.Combine(project, raw));
        }
        catch (Exception ex) { error = $"'{raw}' is not a valid path: {ex.Message}"; return null; }
    }

    private bool FreeToRead(string path, ToolModuleContext ctx) => Inside(_attachmentsRoot(), path) || Inside(ctx.WorkingDirectory, path);

    /// <summary>Where extract_archive unpacks when no destination is given.</summary>
    private string DefaultDestination(string archive)
    {
        string stem = ArchiveExtractor.StemOf(archive);
        string? root = _attachmentsRoot();
        if (Inside(root, archive)) return Path.Combine(Path.GetDirectoryName(archive)!, stem);
        string baseDir = root ?? Path.Combine(Path.GetTempPath(), "CluadeX-attachments");
        return Path.Combine(baseDir, "extracted", $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        switch (call.Type)
        {
            case ToolType.ReadDocument:
            {
                var path = Resolve(call.GetArg("path"), context.WorkingDirectory, out _);
                if (path == null) return ToolPermission.Free("read", call.GetArg("path"));   // fails in Execute with the reason
                return FreeToRead(path, context) ? ToolPermission.Free("read", path) : ToolPermission.Ask("read", path);
            }
            case ToolType.ExtractArchive:
            {
                var src = Resolve(call.GetArg("path"), context.WorkingDirectory, out _);
                if (src == null) return ToolPermission.Free("read", call.GetArg("path"));
                string destArg = call.GetArg("destination");
                var dest = string.IsNullOrWhiteSpace(destArg) ? DefaultDestination(src) : Resolve(destArg, context.WorkingDirectory, out _);
                if (dest == null) return ToolPermission.Free("read", src);
                bool free = FreeToRead(src, context) && Inside(_attachmentsRoot(), dest);
                return free ? ToolPermission.Free("write", dest)
                    : ToolPermission.Ask("write", dest, $"Unpack {Path.GetFileName(src)} into {dest}" + (IsTrue(call.GetArg("overwrite")) ? " (replacing files that exist)" : ""));
            }
            case ToolType.CopyFile:
            {
                var src = Resolve(call.GetArg("source"), context.WorkingDirectory, out _);
                var dest = src == null ? null : CopyTarget(src, call.GetArg("destination"), context.WorkingDirectory, out _);
                if (src == null || dest == null) return ToolPermission.Free("write", call.GetArg("destination"));
                bool free = Inside(_attachmentsRoot(), dest) && FreeToRead(src, context);
                return free ? ToolPermission.Free("write", dest)
                    : ToolPermission.Ask("write", dest, $"Copy {src} → {dest}" + (IsTrue(call.GetArg("overwrite")) ? " (replacing what is there)" : ""));
            }
        }
        return ToolPermission.Ask("execute", call.ToolName);
    }

    private static bool IsTrue(string? v) => v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    // ─── Execute ───

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct) =>
        Task.Run(() => call.Type switch
        {
            ToolType.ReadDocument => ReadDocument(call, context),
            ToolType.ExtractArchive => Extract(call, context, ct),
            ToolType.CopyFile => Copy(call, context, ct),
            _ => Fail(call, $"{call.ToolName} is not an attachment tool."),
        }, ct);

    private static ToolResult Fail(ToolCall call, string msg) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

    private static ToolResult Ok(ToolCall call, string output, string summary) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = true, Output = output, Summary = summary };

    private ToolResult ReadDocument(ToolCall call, ToolModuleContext ctx)
    {
        var path = Resolve(call.GetArg("path"), ctx.WorkingDirectory, out var err);
        if (path == null) return Fail(call, err!);
        if (Directory.Exists(path)) return Fail(call, $"{path} is a folder — list it with list_files, or read one file in it.");
        if (!File.Exists(path)) return Fail(call, $"No file at {path}.");
        int.TryParse(call.GetArg("offset"), out int offset);
        int max = int.TryParse(call.GetArg("max_chars"), out var m) && m > 0 ? m : 30_000;
        max = Math.Min(max, Math.Max(2_000, ctx.OutputBudgetChars - 400));
        var doc = DocumentReader.Read(path, offset, max);
        if (!doc.Success && string.IsNullOrEmpty(doc.Text)) return Fail(call, doc.Note ?? $"Nothing could be read from {Path.GetFileName(path)}.");
        var sb = new StringBuilder();
        sb.Append($"{Path.GetFileName(path)} — {doc.Kind}{(doc.Parts is int p ? $", {p} part(s)" : "")}, {doc.TotalChars:N0}{(doc.Truncated ? "+" : "")} chars");
        if (offset > 0) sb.Append($", from offset {offset:N0}");
        sb.Append('\n');
        if (doc.Note != null) sb.Append(doc.Note).Append('\n');
        if (doc.LooksScanned) sb.Append("Its pages are pictures: a model that sees images can look at them after copying a page image out, otherwise OCR is needed.\n");
        sb.Append(doc.Text);
        if (doc.Truncated) sb.Append($"\n[… more — call read_document again with offset={doc.NextOffset}]");
        return Ok(call, sb.ToString(), $"📄 {Path.GetFileName(path)} · {doc.Text.Length:N0} chars");
    }

    private ToolResult Extract(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        var src = Resolve(call.GetArg("path"), ctx.WorkingDirectory, out var err);
        if (src == null) return Fail(call, err!);
        if (!File.Exists(src)) return Fail(call, $"No file at {src}.");
        string destArg = call.GetArg("destination");
        var dest = string.IsNullOrWhiteSpace(destArg) ? DefaultDestination(src) : Resolve(destArg, ctx.WorkingDirectory, out err);
        if (dest == null) return Fail(call, err!);
        if (!Inside(_attachmentsRoot(), dest) && PermissionService.IsUnsafeAcceptEditsRoot(dest)) return Fail(call, $"Refusing to unpack straight into {dest} (a drive root or system/profile folder) — pick a subfolder.");
        var r = ArchiveExtractor.Extract(src, dest, IsTrue(call.GetArg("overwrite")), ArchiveLimits.Default, ct);
        if (!r.Success) return Fail(call, r.Error ?? "The archive could not be unpacked.");
        var sb = new StringBuilder();
        sb.Append($"Unpacked {Path.GetFileName(src)} into {r.Destination}\\ — {r.Files.Count} file(s), {r.TotalBytes / 1024:N0} KB.\n");
        foreach (var f in r.Files.Take(200)) sb.Append("  ").Append(f).Append('\n');
        if (r.Files.Count > 200) sb.Append($"  … and {r.Files.Count - 200} more\n");
        if (r.Skipped.Count > 0)
        {
            sb.Append($"Skipped {r.Skipped.Count}:\n");
            foreach (var s in r.Skipped.Take(30)) sb.Append("  ").Append(s).Append('\n');
        }
        return Ok(call, sb.ToString(), $"📦 {Path.GetFileName(src)} → {r.Files.Count} file(s)");
    }

    /// <summary>Where copy_file writes: "assets/" or an existing folder receives the item under its own name.</summary>
    private static string? CopyTarget(string src, string destRaw, string? project, out string? error)
    {
        var dest = Resolve(destRaw, project, out error);
        if (dest == null) return null;
        destRaw = (destRaw ?? "").Trim().Trim('"', '\'');
        if (destRaw.EndsWith('/') || destRaw.EndsWith('\\') || Directory.Exists(dest))
            dest = Path.Combine(dest.TrimEnd('\\', '/'), Path.GetFileName(src.TrimEnd('\\', '/')));
        return dest;
    }

    private const int MaxCopyFiles = 5000;
    private const long MaxCopyBytes = 2L << 30;

    private ToolResult Copy(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        var src = Resolve(call.GetArg("source"), ctx.WorkingDirectory, out var err);
        if (src == null) return Fail(call, err!);
        var dest = CopyTarget(src, call.GetArg("destination"), ctx.WorkingDirectory, out err);
        if (dest == null) return Fail(call, err!);
        bool overwrite = IsTrue(call.GetArg("overwrite"));
        bool srcIsDir = Directory.Exists(src);
        if (!srcIsDir && !File.Exists(src)) return Fail(call, $"No file or folder at {src}.");
        if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return Fail(call, "Source and destination are the same.");
        if (srcIsDir && Inside(src, dest)) return Fail(call, "Cannot copy a folder into itself.");
        if (!Inside(_attachmentsRoot(), dest) && PermissionService.IsUnsafeAcceptEditsRoot(dest)) return Fail(call, $"Refusing to write straight into {dest} (a drive root or system/profile folder).");

        if (!srcIsDir)
        {
            if (File.Exists(dest) && !overwrite) return Fail(call, $"{dest} already exists — pass overwrite=true to replace it, or pick another name.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite);
            return Ok(call, $"Copied {src} → {dest} ({AttachmentDigest.SizeText(dest)}).", $"📋 {Path.GetFileName(dest)}");
        }

        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Take(MaxCopyFiles + 1).ToList();
        if (files.Count > MaxCopyFiles) return Fail(call, $"{src} holds more than {MaxCopyFiles} files — copy a smaller folder.");
        long bytes = files.Sum(f => new FileInfo(f).Length);
        if (bytes > MaxCopyBytes) return Fail(call, $"{src} is {bytes >> 20} MB — more than {MaxCopyBytes >> 20} MB is not copied in one go.");
        int copied = 0;
        var kept = new List<string>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(f);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { kept.Add(Path.GetRelativePath(src, f) + " (link, skipped)"); continue; }
            string target = Path.Combine(dest, Path.GetRelativePath(src, f));
            if (File.Exists(target) && !overwrite) { kept.Add(Path.GetRelativePath(src, f) + " (exists)"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite);
            copied++;
        }
        var sb = new StringBuilder($"Copied {copied} file(s) from {src} into {dest}\\.");
        if (kept.Count > 0) sb.Append($" Not copied ({kept.Count}): ").Append(string.Join(", ", kept.Take(20))).Append(kept.Count > 20 ? ", …" : "").Append(" — pass overwrite=true to replace existing files.");
        return Ok(call, sb.ToString(), $"📋 {copied} file(s) → {Path.GetFileName(dest)}");
    }

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = new[] { "read_document", "copy_file", "extract_archive" };

    public string? GetPromptSection(PromptSectionContext context) =>
        "Files the user attaches arrive with their absolute paths (and the text of documents). copy_file puts one into the project " +
        "(e.g. a picture into the site's assets), read_document reads PDF/Word/Excel/PowerPoint/text a slice at a time, and " +
        "extract_archive unpacks zip/tar safely. Use the files directly — never ask the user to upload them again.";
}
