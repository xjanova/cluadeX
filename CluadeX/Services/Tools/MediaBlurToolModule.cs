using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Media;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// media_blur — faces and licence plates covered in a video or a picture on this PC (owner 2026-11-14: "เมื่อสั่งทำคลิป …
/// เบลอคน เบลอทะเบียนรถ … ใส่ใน CluadeX"). The detectors run on this PC (ONNX Runtime, CPU): nothing of the clip leaves it.
/// The first call fetches the two small models (≈ 8 MB, pinned by SHA-256 — <see cref="PrivacyModels"/>). The original
/// is never changed: the result is a new file beside it ("name-blurred.mp4") or at the path given, never over an
/// existing one. Asks before writing, and names the download the first time.
/// </summary>
public sealed class MediaBlurToolModule : IAgentToolModule
{
    private readonly Func<string?> _dataRoot;

    public MediaBlurToolModule(SettingsService? settings) : this(() => settings?.DataRoot) { }

    public MediaBlurToolModule(Func<string?> dataRoot) => _dataRoot = dataRoot;

    public string Id => "media-blur";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.MediaBlur };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "media_blur" or "blur_faces" or "blur_plates" or "blur_license_plates" or "privacy_blur" or "blur_video"
            or "anonymize_video" or "anonymise_video" or "face_blur" => ToolType.MediaBlur,
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        var props = new Dictionary<string, object>
        {
            ["source"] = new Dictionary<string, string> { ["type"] = "string", ["description"] = "Path of the video or picture on this PC (mp4, mov, mkv, webm, avi… / jpg, png, webp)" },
            ["faces"] = new Dictionary<string, string> { ["type"] = "boolean", ["description"] = "Cover people's faces (default true)" },
            ["plates"] = new Dictionary<string, string> { ["type"] = "boolean", ["description"] = "Cover vehicle licence plates (default true)" },
            ["style"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "blur", "pixelate", "black" }, ["description"] = "How a covered part looks (default blur)" },
            ["output"] = new Dictionary<string, string> { ["type"] = "string", ["description"] = "Where to save the result (default: beside the source as <name>-blurred.<ext>). Never an existing file." },
        };
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props, ["required"] = new[] { "source" } };
        yield return new ToolSchema
        {
            Name = "media_blur",
            Description = "Cover people's faces and vehicle licence plates in a video or a picture on this PC, for privacy — "
                + "found automatically in every frame and followed as they move. Runs on this PC (nothing is uploaded); the "
                + "original is kept and the result is a new file. Takes about 1–3× the clip's length. Check the result before "
                + "publishing: a face turned away or a plate at a steep angle can be missed.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    public string? GetRefusal(ToolType type) => null;

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = Array.Empty<string>();

    public string? GetPromptSection(PromptSectionContext context) =>
        "To blur faces or licence plates in a video or picture (privacy before posting a clip), call media_blur — never "
        + "claim a clip is blurred without it.";

    // ─── What the call is about ───

    private static bool Flag(ToolCall call, string name, bool fallback)
    {
        string v = call.GetArg(name).Trim().ToLowerInvariant();
        return v switch { "true" or "1" or "yes" => true, "false" or "0" or "no" => false, _ => fallback };
    }

    public static PrivacyOptions Options(ToolCall call) => new(
        Flag(call, "faces", true), Flag(call, "plates", true),
        call.GetArg("style").Trim().ToLowerInvariant() switch
        {
            "pixelate" or "pixel" or "mosaic" => PrivacyStyle.Pixelate,
            "black" or "box" => PrivacyStyle.Black,
            _ => PrivacyStyle.Blur,
        });

    private static IEnumerable<PrivacyModel> ModelsFor(PrivacyOptions o)
    {
        if (o.Faces) yield return PrivacyModels.Face;
        if (o.Plates) yield return PrivacyModels.Plate;
    }

    /// <summary>The source as a full path, or why it cannot be blurred.</summary>
    public static string? SourcePath(ToolCall call, ToolModuleContext ctx, out string? error)
    {
        error = null;
        string raw = (call.GetArg("source") ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0) { error = L("media.blur.noSource"); return null; }
        string path;
        try
        {
            path = Path.IsPathFullyQualified(raw) ? Path.GetFullPath(raw)
                : ctx.WorkingDirectory is { Length: > 0 } wd ? Path.GetFullPath(Path.Combine(wd, raw)) : "";
        }
        catch { path = ""; }
        if (path.Length == 0 || !File.Exists(path)) { error = F("media.blur.notFound", raw); return null; }
        if (!PrivacyBlur.CanBlur(path)) { error = F("media.blur.notMedia", Path.GetFileName(path)); return null; }
        return path;
    }

    /// <summary>Where the result goes: the output asked for (made absolute; its extension becomes the source's
    /// output one when it has none), or a new name beside the source.</summary>
    public static string? OutputPath(ToolCall call, ToolModuleContext ctx, string source, out string? error)
    {
        error = null;
        string ext = PrivacyBlur.OutputExt(source);
        string raw = (call.GetArg("output") ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0) return MediaFiles.CopyPathFor(source, ext, "blurred");
        string path;
        try
        {
            path = Path.IsPathFullyQualified(raw) ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(ctx.WorkingDirectory is { Length: > 0 } wd ? wd : Path.GetDirectoryName(source)!, raw));
        }
        catch { error = F("media.blur.badOutput", raw); return null; }
        if (Path.GetExtension(path).Length == 0) path += "." + ext;
        if (File.Exists(path) || Directory.Exists(path)) { error = F("media.blur.outputExists", path); return null; }
        if (string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) { error = F("media.blur.outputExists", path); return null; }
        bool still = PrivacyBlur.StillExtensions.Contains(Path.GetExtension(source));
        if (still != PrivacyBlur.StillExtensions.Contains(Path.GetExtension(path)) || !PrivacyBlur.CanBlur(path))
        { error = F("media.blur.badOutput", raw); return null; }
        return path;
    }

    // ─── Permission ───

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        string? source = SourcePath(call, context, out _);
        if (source == null) return ToolPermission.Free("read", call.GetArg("source"));   // fails in Execute with the reason
        string? output = OutputPath(call, context, source, out _);
        if (output == null) return ToolPermission.Free("read", source);
        var o = Options(call);
        string what = o.Faces && o.Plates ? L("media.blur.whatBoth") : o.Faces ? L("media.blur.whatFaces") : L("media.blur.whatPlates");
        string detail = F("media.blur.permDetail", Path.GetFileName(source), what, output);
        long fetch = PrivacyModels.BytesToFetch(ModelsFor(o), _dataRoot());
        if (fetch > 0) detail += "\n" + F("media.blur.permDownload", MediaFiles.SizeText(fetch));
        return ToolPermission.Ask("write", output, detail);
    }

    // ─── Run ───

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        string? source = SourcePath(call, context, out var error);
        if (source == null) return Fail(call, error!);
        string? output = OutputPath(call, context, source, out error);
        if (output == null) return Fail(call, error!);
        var o = Options(call);
        if (!o.Faces && !o.Plates) return Fail(call, L("media.blur.nothingAsked"));
        string? ffmpeg = Ffmpeg.Locate();
        if (ffmpeg == null) return Fail(call, L("media.blur.noFfmpeg"));
        if (PrivacyBlur.LocateFfprobe(ffmpeg) == null) return Fail(call, L("media.blur.noFfmpeg"));

        var models = ModelsFor(o).ToList();
        string? root = _dataRoot();
        string? fetchError = await PrivacyModels.EnsureAsync(models, root, ct);
        if (fetchError == "cancelled") return Fail(call, L("media.tr.cancelled"));
        if (fetchError != null) return Fail(call, F("media.blur.modelFailed", fetchError));

        PrivacyDetector detector;
        try
        {
            detector = new PrivacyDetector(o.Faces ? PrivacyModels.PathOf(PrivacyModels.Face, root) : null,
                o.Plates ? PrivacyModels.PathOf(PrivacyModels.Plate, root) : null);
        }
        catch (Exception ex) { return Fail(call, F("media.blur.modelFailed", ex.GetType().Name + ": " + ex.Message)); }

        PrivacyResult r;
        using (detector)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(output)!); } catch { /* RunAsync says what failed */ }
            r = await Task.Run(() => PrivacyBlur.RunAsync(ffmpeg, source, output, o, detector.Detect, null, ct), ct);
        }
        return r.Outcome switch
        {
            FfmpegOutcome.Done => Done(call, source, output, o, r),
            FfmpegOutcome.Cancelled => Fail(call, L("media.tr.cancelled")),
            FfmpegOutcome.TimedOut => Fail(call, L("media.blur.timedOut")),
            _ => Fail(call, F("media.blur.failed", Short(r.Detail))),
        };
    }

    private static ToolResult Done(ToolCall call, string source, string output, PrivacyOptions o, PrivacyResult r)
    {
        bool still = r.Frames <= 1;
        string found = (o.Faces ? $"faces: up to {r.MostFaces} in one {(still ? "picture" : "frame")}" : "")
            + (o.Faces && o.Plates ? " · " : "")
            + (o.Plates ? $"licence plates: up to {r.MostPlates} in one {(still ? "picture" : "frame")}" : "");
        string text = $"Saved the covered copy as: {output}\nFrom: {source}\nFound — {found}\n"
            + (still ? "" : $"Frames: {r.Frames:N0}, {r.FramesCovered:N0} of them have something covered.\n")
            + (r.FramesCovered == 0
                ? "Nothing was found to cover — the copy shows the same picture. Tell the user, and check by eye if they expected faces or plates."
                : "Look through the result before it is posted: a face turned away, very small, or a plate at a steep angle can be missed.")
            + $"\nStyle: {o.Style.ToString().ToLowerInvariant()}. The original was not changed.";
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = text,
            Summary = $"{Path.GetFileName(output)} · {r.MostFaces} face(s), {r.MostPlates} plate(s) max",
        };
    }

    private static string Short(string s)
    {
        s = (s ?? "").Trim();
        var last = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? s;
        return last.Length > 300 ? last[..300] + "…" : last;
    }

    private static ToolResult Fail(ToolCall call, string msg) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

    private static string L(string key) => LocalizedResources.Instance[key];

    private static string F(string key, params object[] args)
    {
        try { return string.Format(L(key), args); }
        catch (FormatException) { return L(key); }
    }
}
