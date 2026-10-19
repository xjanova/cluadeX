using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Attachments;

namespace CluadeX.ViewModels;

/// <summary>
/// Files attached to the next message — pasted with Ctrl+V, dropped on the composer or picked from "+" (owner
/// 2026-10-11: "ควร ctrl+v แล้ววางรูปภาพลงแชทให้เอไอดูได้"; 2026-10-19: "วางภาพแล้วสั่งให้ใช้ภาพทำงาน, วาง zip ต้องแตกได้,
/// PDF ต้องอ่านได้"). Each is copied into its own folder under the data folder's attachments\ with its own name, so the
/// chat keeps it after the original moves and the AI can copy "hero.png" into a project by that path. Images go to a
/// vision model as pictures; every attachment's path, a document's text and an archive's unpacked files go with the
/// message as text (Services/Attachments/AttachmentDigest), shaped for what the provider can do.
/// </summary>
public partial class ChatViewModel
{
    public const int MaxPendingImages = 8;
    private const long MaxImageBytes = 20L * 1024 * 1024;
    public const int MaxPendingFiles = 10;
    private const long MaxFileBytes = 256L * 1024 * 1024;

    public ObservableCollection<string> PendingImages { get; } = new();
    public bool HasPendingImages => PendingImages.Count > 0;

    /// <summary>Attached files that are not pictures (PDF, zip, Office, text, code…) — copies, like the images.</summary>
    public ObservableCollection<string> PendingFiles { get; } = new();
    public bool HasPendingFiles => PendingFiles.Count > 0;

    private ICommand? _removePendingImageCommand;
    public ICommand RemovePendingImageCommand => _removePendingImageCommand ??= new RelayCommand<string>(p =>
    {
        if (p != null) PendingImages.Remove(p);
    });

    private ICommand? _removePendingFileCommand;
    public ICommand RemovePendingFileCommand => _removePendingFileCommand ??= new RelayCommand<string>(p =>
    {
        if (p != null) PendingFiles.Remove(p);
    });

    /// <summary>Files still being copied in (a big zip takes a moment): Send waits for them, so nothing dropped just
    /// before pressing Send is left out of the message.</summary>
    private Task _attachingFiles = Task.CompletedTask;
    private bool _sendWaitsForAttachments;

    private bool _pendingImagesHooked;
    private void HookPendingImages()
    {
        if (_pendingImagesHooked) return;
        _pendingImagesHooked = true;
        PendingImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingImages));
        PendingFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingFiles));
    }

    private string AttachmentsRoot => Path.Combine(_settingsService.DataRoot, "attachments");

    /// <summary>A new, empty folder for one attachment: attachments\yyyy-MM\yyyyMMdd-HHmmss-xxxxxx\.</summary>
    private string NewAttachmentFolder()
    {
        var now = DateTime.Now;
        string dir = Path.Combine(AttachmentsRoot, now.ToString("yyyy-MM"), $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A file name that is safe on disk, from what the user's file was called ("" → <paramref name="fallback"/>).</summary>
    public static string SafeFileName(string? name, string fallback)
    {
        name = Path.GetFileName(name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.TrimEnd('.', ' ');
        if (name.Length > 120) name = name[..80] + "~" + name[^30..];
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>The kinds of image file that can be attached (a BMP or TIFF is converted to PNG).</summary>
    public static bool LooksLikeImageFile(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff";

    /// <summary>Image bytes (a pasted screenshot): saved as a file and attached. False — and why in the status line —
    /// when they are not an image, too big, or the limit is reached. <paramref name="name"/>: what the picture was called
    /// (a dropped file's name without extension), kept so the AI can use it by a recognisable name.</summary>
    public bool AttachImageBytes(byte[] bytes, string? name = null)
    {
        HookPendingImages();
        if (PendingImages.Count >= MaxPendingImages) { StatusText = LocF("chat.attach.tooMany", MaxPendingImages); return false; }
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) { StatusText = Loc("chat.attach.tooBig"); return false; }
        string? mime = Services.Vision.ImageCodec.SniffMediaType(bytes);
        string? ext = mime switch
        {
            "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp",
            "image/bmp" or "image/tiff" => ".png",
            _ => null,
        };
        if (ext == null) { StatusText = Loc("chat.attach.notImage"); return false; }
        try
        {
            if (mime is "image/bmp" or "image/tiff") bytes = ToPng(bytes);
            string stem = SafeFileName(name, $"image-{DateTime.Now:HHmmss}");
            string path = Path.Combine(NewAttachmentFolder(), stem + ext);
            File.WriteAllBytes(path, bytes);
            PendingImages.Add(path);
            StatusText = LocF("chat.attach.added", PendingImages.Count);
            return true;
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "attaching an image failed", ex);
            StatusText = Loc("chat.attach.failed");
            return false;
        }
    }

    /// <summary>Image files (dropped, picked, or copied in Explorer): each read and attached like pasted bytes.</summary>
    public int AttachImageFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths.Where(LooksLikeImageFile))
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) continue;
                if (fi.Length > MaxImageBytes) { StatusText = Loc("chat.attach.tooBig"); continue; }
                if (AttachImageBytes(File.ReadAllBytes(p), Path.GetFileNameWithoutExtension(p))) added++;
                else if (PendingImages.Count >= MaxPendingImages) break;
            }
            catch (Exception ex) { _debugLog?.Warn("Chat", $"attaching {p} failed", ex); }
        }
        return added;
    }

    /// <summary>
    /// Any files (dropped, picked, or copied in Explorer): pictures are attached as images, everything else is copied in
    /// as a file the AI can read, unpack or copy (PDF, zip, Word, Excel, CSV, code…). A folder is refused with a hint to
    /// zip it. Returns how many were attached; the status line says what happened. The copies run off the UI thread and
    /// Send waits for them.
    /// </summary>
    public Task<int> AttachFilesAsync(IEnumerable<string> paths)
    {
        HookPendingImages();
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int images = AttachImageFiles(list.Where(LooksLikeImageFile));
        var others = list.Where(p => !LooksLikeImageFile(p)).ToList();
        if (others.Count == 0) return Task.FromResult(images);
        var task = CopyFilesInAsync(others, images);
        _attachingFiles = Task.WhenAll(_attachingFiles, task);
        return task;
    }

    private async Task<int> CopyFilesInAsync(List<string> paths, int imagesAdded)
    {
        int added = 0;
        string? problem = null;
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { problem = Loc("chat.attach.folder"); continue; }
            if (PendingFiles.Count >= MaxPendingFiles) { problem = LocF("chat.attach.tooManyFiles", MaxPendingFiles); break; }
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) continue;
                if (fi.Length > MaxFileBytes) { problem = LocF("chat.attach.fileTooBig", fi.Name, MaxFileBytes >> 20); continue; }
                string target = await Task.Run(() =>
                {
                    string t = Path.Combine(NewAttachmentFolder(), SafeFileName(fi.Name, "file"));
                    File.Copy(fi.FullName, t);
                    return t;
                });
                PendingFiles.Add(target);
                added++;
            }
            catch (Exception ex)
            {
                _debugLog?.Warn("Chat", $"attaching {p} failed", ex);
                problem = LocF("chat.attach.fileFailed", Path.GetFileName(p));
            }
        }
        if (imagesAdded + added > 0)
            StatusText = LocF("chat.attach.addedFiles", PendingImages.Count + PendingFiles.Count) + (problem != null ? " · " + problem : "");
        else if (problem != null)
            StatusText = problem;
        return added;
    }

    /// <summary>Waits for files still being copied in, so the message about to go out carries them.</summary>
    private async Task WaitForAttachingAsync()
    {
        try { await _attachingFiles; } catch { /* each copy reported its own failure */ }
    }

    private static byte[] ToPng(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(frame);
        using var output = new MemoryStream();
        enc.Save(output);
        return output.ToArray();
    }

    /// <summary>The attached files decoded for a model (resized and re-encoded as the vision pipeline does for tool
    /// images). A file that is gone or unreadable is left out.</summary>
    private async Task<List<ToolImage>> LoadAttachedImagesAsync(IEnumerable<string> paths)
    {
        var list = new List<ToolImage>();
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var decoded = await Services.Vision.ImageCodec.ProcessAsync(await File.ReadAllBytesAsync(p));
                if (decoded.Image != null) list.Add(decoded.Image with { FilePath = p });
                else _debugLog?.Warn("Chat", $"attached image {p} could not be used: {decoded.Error}");
            }
            catch (Exception ex) { _debugLog?.Warn("Chat", $"attached image {p} could not be read", ex); }
        }
        return list;
    }

    /// <summary>
    /// What the model gets with the attachments, shaped for the active provider: a CLI agent (Claude Dev, Gemini Dev)
    /// opens the files itself, CluadeX's own loop has read_document / copy_file / extract_archive, a plain chat gets the
    /// content only. Pictures a vision model is sent are listed too — by path, so it can copy them into the project.
    /// Off the UI thread: unpacking a zip or reading a long PDF takes a moment.
    /// </summary>
    private async Task<AttachmentDigestResult> BuildAttachmentDigestAsync(ChatMessage userMsg, bool nativeProvider, CancellationToken ct)
    {
        var provider = _providerManager.ActiveProvider;
        bool cli = provider is Services.Providers.IDelegatedCliProvider;
        bool local = _providerManager.ActiveProviderType is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
        var audience = cli ? AttachmentAudience.CliAgent
            : nativeProvider ? (_agentService.SeesImages(provider) ? AttachmentAudience.LoopSeesImages : AttachmentAudience.LoopBlind)
            : AgenticMode && HasProject ? AttachmentAudience.LoopBlind
            : AttachmentAudience.NoTools;
        var budget = cli ? DigestBudget.Cli : local ? DigestBudget.Local : DigestBudget.Api;
        // A blind model is told about its pictures by AttachedImagesNote (here or in the agent loop) — not twice.
        var images = audience is AttachmentAudience.LoopSeesImages or AttachmentAudience.CliAgent
            ? userMsg.AttachedImages.ToList() : new List<string>();
        var files = userMsg.AttachedFiles.ToList();
        if (images.Count == 0 && files.Count == 0) return new AttachmentDigestResult();
        if (files.Count > 0) StatusText = Loc("chat.attach.reading");
        try
        {
            // WaitAsync: Stop returns at once even if a pathological PDF keeps the parser busy (it finishes in the background).
            var digest = await Task.Run(() => AttachmentDigest.Build(images, files, audience, budget, null, ct), ct).WaitAsync(ct);
            _debugLog?.Info("Chat", $"attachments · {audience} · {images.Count} image(s), {files.Count} file(s) · {digest.Text.Length} chars"
                + $" · {digest.ExtraImages.Count} scan page(s) · {digest.Folders.Count} folder(s)");
            return digest;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "reading the attachments failed", ex);
            // Still usable: the model gets the paths.
            return new AttachmentDigestResult
            {
                Text = "\n\n[Attached files (they could not be read here — use them by path): " + string.Join(", ", images.Concat(files)) + "]",
            };
        }
    }

    /// <summary>How a later turn mentions an earlier message's file (only the path: the content is not resent).</summary>
    public static string EarlierAttachmentLine(string path)
    {
        if (ArchiveExtractor.IsSupported(path))
        {
            string unpacked = Path.Combine(Path.GetDirectoryName(path) ?? "", ArchiveExtractor.StemOf(path));
            if (Directory.Exists(unpacked)) return $"[the user attached a file: {path} (unpacked to {unpacked})]";
        }
        return $"[the user attached a file: {path}]";
    }
}
